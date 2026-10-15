using System.Globalization;
using System.Text;

namespace Shorekeeper.Engine.Api;

/// <param name="Path">Request target without the query, e.g. <c>/api/v1/hello</c>.</param>
internal sealed record HttpRequestHead(
    string Method, string Path, IReadOnlyDictionary<string, string> Query, IReadOnlyDictionary<string, string> Headers, bool IsHttp10)
{
    public bool CloseRequested =>
        Headers.TryGetValue("Connection", out string? connection)
            ? connection.Contains("close", StringComparison.OrdinalIgnoreCase)
              || (IsHttp10 && !connection.Contains("keep-alive", StringComparison.OrdinalIgnoreCase))
            : IsHttp10;
}

/// <summary>Reads HTTP/1.1 requests from a connection, one after the other (<see cref="MiniHttpServer"/>).</summary>
internal sealed class HttpRequestReader(Stream stream)
{
    private const int MaxLineBytes = 8 * 1024;
    private const int MaxHeadBytes = 32 * 1024;

    private readonly byte[] buffer = new byte[MaxLineBytes];
    private int start;
    private int end;

    /// <returns>Null when the caller closed the connection before sending anything.</returns>
    /// <exception cref="InvalidDataException">Not an HTTP/1.x request, or headers too large.</exception>
    public async Task<HttpRequestHead?> ReadHeadAsync(CancellationToken cancellationToken)
    {
        if (await ReadLineAsync(cancellationToken) is not { } requestLine)
        {
            return null;
        }

        string[] parts = requestLine.Split(' ');
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Not an HTTP/1.x request line.");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int headBytes = requestLine.Length;
        while (true)
        {
            string line = await ReadLineAsync(cancellationToken) ?? throw new InvalidDataException("Connection closed inside the headers.");
            if (line.Length == 0)
            {
                break;
            }

            headBytes += line.Length + 2;
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || headBytes > MaxHeadBytes)
            {
                throw new InvalidDataException("Malformed or oversized headers.");
            }

            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            headers[name] = headers.TryGetValue(name, out string? earlier) ? $"{earlier}, {value}" : value;
        }

        string target = parts[1];
        int question = target.IndexOf('?', StringComparison.Ordinal);
        return new HttpRequestHead(
            parts[0],
            question < 0 ? target : target[..question],
            question < 0 ? new Dictionary<string, string>() : ParseQuery(target[(question + 1)..]),
            headers,
            parts[2] == "HTTP/1.0");
    }

    /// <returns>The body, empty when there is none; null when it is larger than <paramref name="maxBytes"/>.</returns>
    public async Task<byte[]?> ReadBodyAsync(HttpRequestHead head, int maxBytes, CancellationToken cancellationToken)
    {
        if (head.Headers.TryGetValue("Transfer-Encoding", out string? encoding) && encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            return await ReadChunkedAsync(maxBytes, cancellationToken);
        }

        if (!head.Headers.TryGetValue("Content-Length", out string? lengthText))
        {
            return [];
        }

        if (!long.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out long length))
        {
            throw new InvalidDataException("Invalid Content-Length.");
        }

        if (length > maxBytes)
        {
            return null;
        }

        byte[] body = new byte[length];
        await ReadExactlyAsync(body, cancellationToken);
        return body;
    }

    private async Task<byte[]?> ReadChunkedAsync(int maxBytes, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        while (true)
        {
            string sizeLine = await ReadLineAsync(cancellationToken) ?? throw new InvalidDataException("Connection closed inside a chunked body.");
            int extension = sizeLine.IndexOf(';', StringComparison.Ordinal);
            if (!int.TryParse(extension < 0 ? sizeLine : sizeLine[..extension], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int size)
                || size < 0)
            {
                throw new InvalidDataException("Invalid chunk size.");
            }

            if (size == 0)
            {
                // Trailers, if any, end with an empty line.
                while (!string.IsNullOrEmpty(await ReadLineAsync(cancellationToken)))
                {
                }

                return body.ToArray();
            }

            if (body.Length + size > maxBytes)
            {
                return null;
            }

            byte[] chunk = new byte[size];
            await ReadExactlyAsync(chunk, cancellationToken);
            body.Write(chunk);
            if (await ReadLineAsync(cancellationToken) is not "")
            {
                throw new InvalidDataException("Chunk not followed by CRLF.");
            }
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            string name = Uri.UnescapeDataString((equals < 0 ? pair : pair[..equals]).Replace('+', ' '));
            values[name] = equals < 0 ? "" : Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
        }

        return values;
    }

    /// <returns>The line without CRLF; null at the end of the stream before any byte of it.</returns>
    private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            int newline = Array.IndexOf(buffer, (byte)'\n', start, end - start);
            if (newline >= 0)
            {
                int lineEnd = newline > start && buffer[newline - 1] == '\r' ? newline - 1 : newline;
                string line = Encoding.Latin1.GetString(buffer, start, lineEnd - start);
                start = newline + 1;
                return line;
            }

            if (start == 0 && end == buffer.Length)
            {
                throw new InvalidDataException("Line too long.");
            }

            if (!await FillAsync(cancellationToken))
            {
                return start == end ? null : throw new InvalidDataException("Connection closed inside a line.");
            }
        }
    }

    private async Task ReadExactlyAsync(byte[] destination, CancellationToken cancellationToken)
    {
        int copied = Math.Min(end - start, destination.Length);
        Buffer.BlockCopy(buffer, start, destination, 0, copied);
        start += copied;
        if (copied < destination.Length)
        {
            await stream.ReadExactlyAsync(destination.AsMemory(copied), cancellationToken);
        }
    }

    /// <returns>False at the end of the stream.</returns>
    private async Task<bool> FillAsync(CancellationToken cancellationToken)
    {
        if (start > 0)
        {
            Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
            end -= start;
            start = 0;
        }

        int read = await stream.ReadAsync(buffer.AsMemory(end), cancellationToken);
        end += read;
        return read > 0;
    }
}
