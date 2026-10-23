using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Shorekeeper.Core.Platform;
using Shorekeeper.Core.Settings;
using Shorekeeper.Engine.Groups;
using Shorekeeper.Engine.Monitoring;
using Shorekeeper.Engine.Transfers;

namespace Shorekeeper.Engine.Tests;

/// <summary>The audit log IT turns on with the policy AuditLog (docs/11-windows-deployment.md §9).</summary>
public sealed class AuditLogTests : IAsyncLifetime
{
    private readonly RecordingSink aLog = new();
    private readonly RecordingSink bLog = new();
    private TestNode a = null!;
    private TestNode b = null!;

    public async ValueTask InitializeAsync()
    {
        a = await TestNode.StartAsync("A", services: s => Audited(s, aLog));
        b = await TestNode.StartAsync("B", services: s => Audited(s, bLog));
        await a.ConnectAsync(b);
    }

    public async ValueTask DisposeAsync()
    {
        await a.DisposeAsync();
        await b.DisposeAsync();
    }

    [Fact]
    public async Task A_file_sent_and_received_is_logged_on_both_sides_with_its_hash_but_not_the_text()
    {
        byte[] content = RandomNumberGenerator.GetBytes(300 * 1024);
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        string source = Path.Combine(a.WorkDirectory, "báo cáo.xlsx");
        await File.WriteAllBytesAsync(source, content, Ct);

        string offerId = await a.Offers.CreateAsync(OfferService.Prepare([source]), [b.Id], "mật khẩu là 1234", snapshotLocked: false, groupId: null, Ct);
        var key = new OfferKey(a.Id, offerId);
        await WaitUntil(() => b.Inbox.Get(key) is not null);
        b.Inbox.Download(key);
        await WaitUntil(() => b.Inbox.Get(key) is { State: InboxState.Completed });

        JsonObject sent = aLog.Single(AuditEvent.OfferSent);
        Assert.Equal(offerId, (string?)sent["offer"]);
        Assert.Equal(b.Id.Value, (string?)sent["to"]![0]!["id"]);
        Assert.Equal(1, (int)sent["files"]!);
        Assert.True((bool)sent["text"]!);
        Assert.Equal("báo cáo.xlsx", (string?)sent["list"]![0]!["path"]);
        Assert.Equal(a.Id.Value, (string?)sent["device"]);

        JsonObject received = bLog.Single(AuditEvent.OfferReceived);
        Assert.Equal(a.Id.Value, (string?)received["from"]!["id"]);
        Assert.Equal(content.Length, (long)received["bytes"]!);

        JsonObject saved = bLog.Single(AuditEvent.FileReceived);
        Assert.Equal("báo cáo.xlsx", (string?)saved["file"]);
        Assert.Equal(sha256, (string?)saved["sha256"]);
        Assert.Equal(b.Inbox.Get(key)!.Files.Single().FinalPath, (string?)saved["savedTo"]);

        await WaitUntil(() => aLog.Has(AuditEvent.FileSent));
        JsonObject served = aLog.Single(AuditEvent.FileSent);
        Assert.Equal(b.Id.Value, (string?)served["to"]!["id"]);
        Assert.Equal(sha256, (string?)served["sha256"]);
        Assert.Equal(content.Length, (long)served["size"]!);

        Assert.DoesNotContain(aLog.Lines.Concat(bLog.Lines), line => line.Text.Contains("1234", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_declined_offer_is_logged()
    {
        string source = Path.Combine(a.WorkDirectory, "x.bin");
        await File.WriteAllBytesAsync(source, [1, 2, 3], Ct);
        string offerId = await a.Offers.CreateAsync(OfferService.Prepare([source]), [b.Id], null, snapshotLocked: false, groupId: null, Ct);
        var key = new OfferKey(a.Id, offerId);
        await WaitUntil(() => b.Inbox.Get(key) is not null);

        await b.Inbox.DeclineAsync(key);

        Assert.Equal(offerId, (string?)bLog.Single(AuditEvent.OfferDeclined)["offer"]);
        Assert.False(bLog.Has(AuditEvent.FileReceived));
    }

    [Fact]
    public async Task Contacts_and_group_members_are_logged()
    {
        Assert.Equal(b.Id.Value, (string?)aLog.Single(AuditEvent.ContactAdded)["peer"]!["id"]);

        Group group = await a.Groups.CreateAsync("Build QA", Ct);
        Assert.Equal("host", (string?)aLog.Single(AuditEvent.GroupJoined)["role"]);
        await a.Groups.InviteAsync(group.Id, b.Id, "B", "HOST", Ct);
        await b.Groups.AcceptInvitationAsync(b.Groups.GetInvitations().Single(), Ct);
        await WaitUntil(() => bLog.Has(AuditEvent.GroupJoined));
        Assert.Equal("member", (string?)bLog.Single(AuditEvent.GroupJoined)["role"]);
        Assert.Equal(b.Id.Value, (string?)aLog.Single(AuditEvent.GroupMemberAdded)["member"]!["id"]);

        await a.Groups.RemoveMemberAsync(group.Id, b.Id);
        await WaitUntil(() => bLog.Has(AuditEvent.GroupLeft));
        Assert.Equal(b.Id.Value, (string?)aLog.Single(AuditEvent.GroupMemberRemoved)["member"]!["id"]);
        Assert.Equal("Build QA", (string?)bLog.Single(AuditEvent.GroupLeft)["group"]!["name"]);

        await a.Trust.ForgetAsync(b.Id, Ct);
        Assert.Equal("contact", (string?)aLog.Single(AuditEvent.ContactRemoved)["was"]);
    }

    [Fact]
    public async Task Nothing_is_logged_unless_IT_turns_it_on()
    {
        var log = new RecordingSink();
        await using TestNode c = await TestNode.StartAsync("C", services: s => s.AddSingleton<IAuditSink>(log));
        await a.ConnectAsync(c);
        string source = Path.Combine(c.WorkDirectory, "x.bin");
        await File.WriteAllBytesAsync(source, [1, 2, 3], Ct);

        string offerId = await c.Offers.CreateAsync(OfferService.Prepare([source]), [a.Id], null, snapshotLocked: false, groupId: null, Ct);
        await WaitUntil(() => aLog.Has(AuditEvent.OfferReceived));

        Assert.Empty(log.Lines);
        Assert.Equal(offerId, (string?)aLog.Single(AuditEvent.OfferReceived)["offer"]);
    }

    private static void Audited(IServiceCollection services, RecordingSink sink)
    {
        services.AddSingleton<IAuditSink>(sink);
        services.AddSingleton<IPolicyProvider>(new AuditOn());
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(20, Ct);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class RecordingSink : IAuditSink
    {
        public ConcurrentQueue<(int Id, string Text)> Lines { get; } = new();

        public void Write(int eventId, string message) => Lines.Enqueue((eventId, message));

        public bool Has(AuditEvent what) => Lines.Any(l => l.Id == (int)what);

        /// <summary>The one event of that kind, which names itself as such.</summary>
        public JsonObject Single(AuditEvent what)
        {
            JsonObject entry = JsonNode.Parse(Assert.Single(Lines, l => l.Id == (int)what).Text)!.AsObject();
            Assert.Equal(what.ToString(), (string?)entry["event"]);
            return entry;
        }
    }

    private sealed class AuditOn : IPolicyProvider
    {
        public string? GetString(string name) => null;

        public int? GetInt32(string name) => null;

        public bool? GetBoolean(string name) => name == PolicyNames.AuditLog ? true : null;

        public IReadOnlyList<string>? GetStringList(string name) => null;
    }
}
