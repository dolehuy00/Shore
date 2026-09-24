using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace Shorekeeper.Desktop.Hosting;

/// <summary>
/// One Shorekeeper process per Windows user session (docs/02-architecture.md §5).
/// A second launch forwards its command line to the running instance over a named pipe and exits.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly string pipeName;
    private readonly CancellationTokenSource cancellation = new();
    private bool ownsMutex;

    private SingleInstance(string name)
    {
        mutex = new Mutex(initiallyOwned: true, $@"Local\{name}", out bool createdNew);
        ownsMutex = createdNew;
        pipeName = name;
    }

    public bool IsPrimary => ownsMutex;

    public static SingleInstance Acquire()
    {
        // Local\ mutexes are per session; the pipe name also needs the session and user to stay unique machine-wide.
        string user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        int session = Process.GetCurrentProcess().SessionId;
        return new SingleInstance($"Shorekeeper-{user}-{session}");
    }

    /// <summary>Sends <paramref name="args"/> to the primary instance. Returns false if it could not be reached.</summary>
    public bool TrySignalPrimary(IReadOnlyList<string> args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(TimeSpan.FromSeconds(3));
            using var writer = new StreamWriter(client);
            writer.Write(string.Join('\n', args));
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Listens for launches of second instances; <paramref name="onActivated"/> receives their arguments.</summary>
    public void StartListening(Action<string[]> onActivated, ILogger logger)
    {
        if (!IsPrimary)
        {
            throw new InvalidOperationException("Only the primary instance can listen.");
        }

        _ = Task.Run(async () =>
        {
            CancellationToken token = cancellation.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        pipeName,
                        PipeDirection.In,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token);
                    using var reader = new StreamReader(server);
                    string message = await reader.ReadToEndAsync(token);
                    string[] args = message.Length == 0 ? [] : message.Split('\n');
                    onActivated(args);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Single-instance pipe listener failed; retrying");
                    await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
                }
            }
        });
    }

    public void Dispose()
    {
        cancellation.Cancel();
        if (ownsMutex)
        {
            mutex.ReleaseMutex();
            ownsMutex = false;
        }

        mutex.Dispose();
        cancellation.Dispose();
    }
}
