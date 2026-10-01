using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Shorekeeper.Engine.Api;

namespace Shorekeeper.Engine.Transfers;

/// <summary>Asks the sender for the file from <paramref name="offset"/>; <paramref name="ifRange"/> is the ETag of the first response.</summary>
public delegate Task<HttpResponseMessage> RangeRequester(long offset, EntityTagHeaderValue? ifRange, CancellationToken cancellationToken);

/// <param name="ReconnectingUntil">Set while the connection is lost and we keep retrying.</param>
public sealed record DownloadProgress(long BytesReceived, DateTimeOffset? ReconnectingUntil);

/// <summary>The download cannot finish; <see cref="Exception.Message"/> is shown to the user.</summary>
public sealed class DownloadFailedException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// Streams one file to disk while hashing it (docs/06-file-transfer.md §5, §7).
/// A dropped connection is resumed with a Range request from the byte already received; the ETag
/// (If-Range) guarantees the rest comes from the same file version. Giving up after the reconnect window.
/// </summary>
public sealed class FileDownloader(TimeProvider timeProvider)
{
    public static readonly TimeSpan NoDataTimeout = TimeSpan.FromSeconds(15);
    private const int BufferSize = 1024 * 1024;
    private static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15)];

    /// <returns>Lower-case hex SHA-256 of what was written.</returns>
    public async Task<string> DownloadAsync(
        RangeRequester request,
        string tempPath,
        long size,
        TimeSpan reconnectWindow,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        WriteOrFail(() => file.SetLength(size));

        long received = 0;
        EntityTagHeaderValue? etag = null;
        DateTimeOffset? failingSince = null;
        int attempt = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (received < size)
            {
                try
                {
                    using HttpResponseMessage response = await request(received, etag, cancellationToken);
                    await EnsureUsableAsync(response, resuming: received > 0, cancellationToken);
                    etag ??= response.Headers.ETag;

                    await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
                    while (true)
                    {
                        int read = await ReadWithTimeoutAsync(body, buffer, cancellationToken);
                        if (read == 0)
                        {
                            break;
                        }

                        if (received + read > size)
                        {
                            throw new DownloadFailedException("Máy gửi trả về nhiều dữ liệu hơn kích thước file.");
                        }

                        await WriteOrFailAsync(() => file.WriteAsync(buffer.AsMemory(0, read), cancellationToken));
                        hash.AppendData(buffer, 0, read);
                        received += read;
                        failingSince = null;
                        attempt = 0;
                        progress?.Report(new DownloadProgress(received, null));
                    }

                    if (received < size)
                    {
                        throw new IOException("The connection closed before the whole file arrived.");
                    }
                }
                catch (Exception ex) when (IsTransient(ex, cancellationToken))
                {
                    DateTimeOffset now = timeProvider.GetUtcNow();
                    failingSince ??= now;
                    DateTimeOffset giveUpAt = failingSince.Value + reconnectWindow;
                    if (now >= giveUpAt)
                    {
                        throw new DownloadFailedException("Mất kết nối với máy gửi quá lâu.", ex);
                    }

                    progress?.Report(new DownloadProgress(received, giveUpAt));
                    TimeSpan delay = Backoff[Math.Min(attempt++, Backoff.Length - 1)];
                    await Task.Delay(delay < giveUpAt - now ? delay : giveUpAt - now, timeProvider, cancellationToken);
                }
            }

            await WriteOrFailAsync(() => file.FlushAsync(cancellationToken));
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task EnsureUsableAsync(HttpResponseMessage response, bool resuming, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            // If-Range did not match: the sender's file changed, so the server sent everything again.
            if (resuming && response.StatusCode != HttpStatusCode.PartialContent)
            {
                throw new DownloadFailedException("File gốc đã bị thay đổi trong lúc tải.");
            }

            return;
        }

        try
        {
            await PeerClient.EnsureSuccessAsync(response, cancellationToken);
        }
        catch (PeerApiException ex) when (ex.Code == ApiErrorCodes.Busy)
        {
            throw new IOException("Sender is busy.", ex);
        }
        catch (PeerApiException ex)
        {
            throw new DownloadFailedException(ex.Code switch
            {
                ApiErrorCodes.SourceChanged => "File gốc đã bị thay đổi.",
                ApiErrorCodes.OfferClosed => "Người gửi đã thu hồi hoặc lượt gửi đã hết hạn.",
                ApiErrorCodes.NotFound => "Người gửi không còn file này.",
                _ => "Người gửi từ chối yêu cầu tải.",
            }, ex);
        }
    }

    private static async Task<int> ReadWithTimeoutAsync(Stream body, byte[] buffer, CancellationToken cancellationToken)
    {
        using var noData = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        noData.CancelAfter(NoDataTimeout);
        return await body.ReadAsync(buffer, noData.Token);
    }

    /// <summary>Network problems are worth retrying; disk problems and the user cancelling are not.</summary>
    private static bool IsTransient(Exception ex, CancellationToken cancellationToken) => ex switch
    {
        DownloadFailedException => false,
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        HttpRequestException or IOException or PeerUnreachableException => true,
        _ => false,
    };

    private static void WriteOrFail(Action write)
    {
        try
        {
            write();
        }
        catch (IOException ex)
        {
            throw new DownloadFailedException($"Không ghi được file: {ex.Message}", ex);
        }
    }

    private static async Task WriteOrFailAsync(Func<ValueTask> write)
    {
        try
        {
            await write();
        }
        catch (IOException ex)
        {
            throw new DownloadFailedException($"Không ghi được file: {ex.Message}", ex);
        }
    }

    private static async Task WriteOrFailAsync(Func<Task> write)
    {
        try
        {
            await write();
        }
        catch (IOException ex)
        {
            throw new DownloadFailedException($"Không ghi được file: {ex.Message}", ex);
        }
    }
}
