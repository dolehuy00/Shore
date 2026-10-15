using System.Globalization;
using System.Net;
using System.Text.Json;
using Shorekeeper.Core.Identity;

namespace Shorekeeper.Engine.Api;

/// <summary>
/// A call to the peer API as the handlers see it, whichever HTTP server received it (Kestrel on the desktop,
/// <see cref="MiniHttpServer"/> where ASP.NET Core does not run, docs/14-android.md §3.1).
/// </summary>
public sealed class PeerRequest
{
    public required string Method { get; init; }

    /// <summary>Path below <c>/api/v1</c>, e.g. <c>/offers/abc/files/f0</c>.</summary>
    public required string Path { get; init; }

    public required IReadOnlyDictionary<string, string> Query { get; init; }

    /// <summary>DeviceId of the caller, from its TLS client certificate.</summary>
    public required DeviceId Caller { get; init; }

    public required IPAddress RemoteAddress { get; init; }

    public required Stream Body { get; init; }

    /// <summary>Cancelled when the server stops (and, on Kestrel, when the caller hangs up).</summary>
    public CancellationToken Aborted { get; init; }

    /// <summary>Values of the <c>{name}</c> segments of the matched route.</summary>
    public IReadOnlyDictionary<string, string> RouteValues { get; internal set; } = new Dictionary<string, string>();

    public string Route(string name) => RouteValues[name];

    public long? QueryInt64(string name) =>
        Query.TryGetValue(name, out string? text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : null;

    /// <returns>Null when the body is missing or is not valid JSON for <typeparamref name="T"/>.</returns>
    public async Task<T?> ReadJsonAsync<T>()
        where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(Body, PeerJson.Options, Aborted);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>What a handler answers; each HTTP server writes it in its own way.</summary>
public abstract record PeerResponse(int Status)
{
    public static PeerResponse Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) => new JsonPeerResponse((int)status, value, typeof(T));

    /// <summary><c>application/problem+json</c> with a stable <c>code</c> (docs/05-protocol.md §3).</summary>
    public static PeerResponse Problem(HttpStatusCode status, string code) => new ProblemPeerResponse((int)status, code);

    public static PeerResponse Empty(HttpStatusCode status) => new EmptyPeerResponse((int)status);

    /// <summary>A file that callers may fetch in parts (<c>Range</c>, guarded by <c>If-Range</c> with <paramref name="ETag"/>).</summary>
    public static PeerResponse File(Stream content, string contentType, DateTimeOffset? lastModified = null, string? eTag = null) =>
        new FilePeerResponse(content, contentType, lastModified, eTag);
}

public sealed record JsonPeerResponse(int Status, object? Value, Type Type) : PeerResponse(Status);

public sealed record ProblemPeerResponse(int Status, string Code) : PeerResponse(Status);

public sealed record EmptyPeerResponse(int Status) : PeerResponse(Status);

/// <param name="ETag">Quoted entity tag, e.g. <c>"1f-18a"</c>.</param>
public sealed record FilePeerResponse(Stream Content, string ContentType, DateTimeOffset? LastModified, string? ETag) : PeerResponse(200);

/// <summary>JSON as ASP.NET Core writes it (camelCase, case-insensitive reads), so both servers look the same to callers.</summary>
public static class PeerJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);

    public static byte[] Problem(int status, string code) =>
        JsonSerializer.SerializeToUtf8Bytes(new ProblemBody(code, status, code), Options);

    private sealed record ProblemBody(string Title, int Status, string Code);
}
