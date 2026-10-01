using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Trust;
using Shorekeeper.Engine.Api;

namespace Shorekeeper.Engine.Trust;

/// <summary>A peer asks to connect with us; the UI shows it with Accept / Decline / Block.</summary>
public sealed record IncomingPairing(string Id, DeviceId PeerId, string Name, string Host, string Code, string? Note);

/// <summary>We asked a peer to connect and are waiting for its answer.</summary>
public sealed record OutgoingPairing(string Id, DeviceId PeerId, string Name, string Code);

public enum PairingOutcome
{
    Accepted,
    Declined,
    Expired,
    Cancelled,
}

public enum IncomingRequestResult
{
    Accepted,
    Invalid,
    RateLimited,
}

/// <summary>
/// "Kết nối": one side asks, the other accepts (docs/04-identity-security.md §4).
/// The requester long-polls for the decision, so only requester → responder connectivity is needed,
/// and the responder records trust only once the requester has received the answer.
/// </summary>
public sealed class PairingService(
    LocalDevice device,
    ApiServer api,
    PeerClient client,
    TrustStore trust,
    TimeProvider timeProvider,
    ILogger<PairingService> logger)
{
    public static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DecisionWait = TimeSpan.FromSeconds(25);

    private const int MaxRequestsPerWindow = 3;
    private const int MaxPending = 20;
    private const int DeclinesBeforeMute = 3;
    private static readonly TimeSpan RequestWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MuteDuration = TimeSpan.FromHours(24);

    private readonly Lock gate = new();
    private readonly Dictionary<string, PendingRequest> incoming = [];
    private readonly Dictionary<DeviceId, List<DateTimeOffset>> recentRequests = [];
    private readonly Dictionary<DeviceId, (int Declines, DateTimeOffset MutedUntil)> declines = [];

    public event EventHandler<IncomingPairing>? IncomingRequested;

    /// <summary>The incoming request with this id is over (cancelled by the requester or expired).</summary>
    public event EventHandler<string>? IncomingClosed;

    // ───────────── Requester side ─────────────

    /// <summary>Asks <paramref name="target"/> to connect. <paramref name="onCode"/> is called once the code is known.</summary>
    /// <exception cref="PeerUnreachableException">The peer could not be reached.</exception>
    /// <exception cref="PeerApiException">The peer refused the request (e.g. rate limited).</exception>
    public async Task<PairingOutcome> RequestAsync(DeviceId target, string? note, Action<OutgoingPairing> onCode, CancellationToken cancellationToken)
    {
        string id = Guid.NewGuid().ToString("N");
        byte[] nonce = RandomNumberGenerator.GetBytes(PairingCode.NonceSize);
        var body = new PairingRequestBody(id, Convert.ToBase64String(nonce), device.DisplayName, LocalDevice.HostName, api.Port, note);

        using HttpResponseMessage response = await client.SendAsync(
            target,
            baseUri => new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "pairing/request")) { Content = JsonContent.Create(body) },
            TimeSpan.FromSeconds(10),
            cancellationToken);
        PairingRequestAccepted accepted = await PeerClient.ReadAsync<PairingRequestAccepted>(response, cancellationToken);

        byte[] theirNonce = Convert.FromBase64String(accepted.Nonce);
        onCode(new OutgoingPairing(id, target, accepted.Name, PairingCode.Compute(device.DeviceId, target, nonce, theirNonce)));

        try
        {
            while (true)
            {
                using HttpResponseMessage poll = await client.SendAsync(
                    target,
                    baseUri => new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, $"pairing/{id}/decision")),
                    DecisionWait + TimeSpan.FromSeconds(10),
                    cancellationToken);
                string status = (await PeerClient.ReadAsync<PairingDecisionResponse>(poll, cancellationToken)).Status;

                switch (status)
                {
                    case PairingStatus.Pending:
                        continue;
                    case PairingStatus.Accepted:
                        await trust.SetTrustedAsync(target, accepted.Name, accepted.Host, CancellationToken.None);
                        logger.LogInformation("Connected with {Peer} ({Name})", target, accepted.Name);
                        return PairingOutcome.Accepted;
                    case PairingStatus.Declined:
                        return PairingOutcome.Declined;
                    case PairingStatus.Cancelled:
                        return PairingOutcome.Cancelled;
                    default:
                        return PairingOutcome.Expired;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryCancelRemoteAsync(target, id);
            return PairingOutcome.Cancelled;
        }
        catch (PeerApiException ex) when (ex.Code == ApiErrorCodes.NotTrusted)
        {
            // The peer blocked us while deciding; to us it looks like a refusal.
            return PairingOutcome.Declined;
        }
    }

    /// <summary>"Ngắt kết nối": forget the peer here and tell it, if reachable, to forget us too.</summary>
    public async Task DisconnectAsync(DeviceId peer, CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpResponseMessage response = await client.SendAsync(
                peer,
                baseUri => new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "pairing/revoke")),
                TimeSpan.FromSeconds(5),
                cancellationToken);
        }
        catch (PeerUnreachableException ex)
        {
            // It will find out on its next request (403).
            logger.LogInformation(ex, "Could not tell {Peer} about the disconnect", peer);
        }

        await trust.ForgetAsync(peer, cancellationToken);
    }

    // ───────────── Responder side (called by the API and the UI) ─────────────

    internal (IncomingRequestResult Result, PairingRequestAccepted? Response) HandleRequest(DeviceId caller, IPAddress remote, PairingRequestBody body)
    {
        if (!IsValid(body, out byte[] requesterNonce))
        {
            return (IncomingRequestResult.Invalid, null);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        PendingRequest pending;
        lock (gate)
        {
            // Retried request (e.g. over another address): answer the same way.
            if (incoming.TryGetValue(body.PairingId, out PendingRequest? existing) && existing.Info.PeerId == caller)
            {
                return (IncomingRequestResult.Accepted, existing.Response);
            }

            List<DateTimeOffset> recent = recentRequests.TryGetValue(caller, out var list) ? list : recentRequests[caller] = [];
            recent.RemoveAll(t => now - t > RequestWindow);
            bool muted = declines.TryGetValue(caller, out var record) && record.MutedUntil > now;
            if (muted || recent.Count >= MaxRequestsPerWindow || incoming.Count >= MaxPending)
            {
                return (IncomingRequestResult.RateLimited, null);
            }

            recent.Add(now);
            byte[] nonce = RandomNumberGenerator.GetBytes(PairingCode.NonceSize);
            string code = PairingCode.Compute(device.DeviceId, caller, requesterNonce, nonce);
            pending = new PendingRequest(
                new IncomingPairing(body.PairingId, caller, body.Name.Trim(), body.Host.Trim(), code, string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim()),
                new PairingRequestAccepted(Convert.ToBase64String(nonce), device.DisplayName, LocalDevice.HostName),
                new IPEndPoint(remote, body.ApiPort));
            incoming[body.PairingId] = pending;
        }

        _ = ExpireLaterAsync(pending);
        IncomingRequested?.Invoke(this, pending.Info);
        return (IncomingRequestResult.Accepted, pending.Response);
    }

    /// <summary>Long-poll from the requester. Trust is recorded here, when the requester actually learns the answer.</summary>
    internal async Task<string> WaitDecisionAsync(DeviceId caller, string id, TimeSpan wait, CancellationToken cancellationToken)
    {
        PendingRequest? pending;
        lock (gate)
        {
            incoming.TryGetValue(id, out pending);
        }

        if (pending is null || pending.Info.PeerId != caller)
        {
            return PairingStatus.Expired;
        }

        Task<string> decision = pending.Decision.Task;
        if (await Task.WhenAny(decision, Task.Delay(wait, timeProvider, cancellationToken)) != decision)
        {
            return PairingStatus.Pending;
        }

        string status = await decision;
        Remove(id);
        if (status == PairingStatus.Accepted)
        {
            await trust.SetTrustedAsync(caller, pending.Info.Name, pending.Info.Host, CancellationToken.None);
            await trust.RecordEndpointAsync(caller, pending.Endpoint, "pairing", CancellationToken.None);
            logger.LogInformation("Connected with {Peer} ({Name})", caller, pending.Info.Name);
        }

        return status;
    }

    internal void CancelByRequester(DeviceId caller, string id)
    {
        PendingRequest? pending;
        lock (gate)
        {
            incoming.TryGetValue(id, out pending);
        }

        if (pending is not null && pending.Info.PeerId == caller && pending.Decision.TrySetResult(PairingStatus.Cancelled))
        {
            Remove(id);
            IncomingClosed?.Invoke(this, id);
        }
    }

    public void Accept(string id) => Decide(id, PairingStatus.Accepted);

    public void Decline(string id)
    {
        PendingRequest? pending = Decide(id, PairingStatus.Declined);
        if (pending is null)
        {
            return;
        }

        lock (gate)
        {
            DeviceId peer = pending.Info.PeerId;
            int count = (declines.TryGetValue(peer, out var record) ? record.Declines : 0) + 1;
            declines[peer] = (count, count >= DeclinesBeforeMute ? timeProvider.GetUtcNow() + MuteDuration : DateTimeOffset.MinValue);
        }
    }

    public async Task BlockAsync(string id)
    {
        PendingRequest? pending = Decide(id, PairingStatus.Declined);
        if (pending is not null)
        {
            await trust.BlockAsync(pending.Info.PeerId, pending.Info.Name, pending.Info.Host);
        }
    }

    private PendingRequest? Decide(string id, string status)
    {
        PendingRequest? pending;
        lock (gate)
        {
            incoming.TryGetValue(id, out pending);
        }

        return pending is not null && pending.Decision.TrySetResult(status) ? pending : null;
    }

    private async Task ExpireLaterAsync(PendingRequest pending)
    {
        await Task.Delay(RequestLifetime, timeProvider);
        if (pending.Decision.TrySetResult(PairingStatus.Expired))
        {
            IncomingClosed?.Invoke(this, pending.Info.Id);
        }

        Remove(pending.Info.Id);
    }

    private void Remove(string id)
    {
        lock (gate)
        {
            incoming.Remove(id);
        }
    }

    private async Task TryCancelRemoteAsync(DeviceId target, string id)
    {
        try
        {
            using HttpResponseMessage response = await client.SendAsync(
                target,
                baseUri => new HttpRequestMessage(HttpMethod.Delete, new Uri(baseUri, $"pairing/{id}")),
                TimeSpan.FromSeconds(5),
                CancellationToken.None);
        }
        catch (PeerUnreachableException)
        {
            // The request expires on its own.
        }
    }

    private static bool IsValid(PairingRequestBody body, out byte[] nonce)
    {
        nonce = [];
        if (body.PairingId is not { Length: > 0 and <= 64 }
            || body.Name is not { Length: > 0 and <= 64 }
            || body.Host is not { Length: <= 64 }
            || body.Note is { Length: > 200 }
            || body.ApiPort is <= 0 or > 65535)
        {
            return false;
        }

        try
        {
            nonce = Convert.FromBase64String(body.Nonce ?? "");
        }
        catch (FormatException)
        {
            return false;
        }

        return nonce.Length == PairingCode.NonceSize;
    }

    private sealed class PendingRequest(IncomingPairing info, PairingRequestAccepted response, IPEndPoint endpoint)
    {
        public IncomingPairing Info { get; } = info;

        public PairingRequestAccepted Response { get; } = response;

        /// <summary>Requester's API endpoint, remembered once connected.</summary>
        public IPEndPoint Endpoint { get; } = endpoint;

        public TaskCompletionSource<string> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
