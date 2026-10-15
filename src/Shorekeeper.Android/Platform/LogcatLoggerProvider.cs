using Microsoft.Extensions.Logging;
using AndroidLog = Android.Util.Log;

namespace Shorekeeper.Android.Platform;

/// <summary>Engine logs go to logcat with tag "Shorekeeper" (adb logcat -s Shorekeeper).</summary>
public sealed class LogcatLoggerProvider : ILoggerProvider
{
    private const string Tag = "Shorekeeper";

    public ILogger CreateLogger(string categoryName) => new LogcatLogger(categoryName[(categoryName.LastIndexOf('.') + 1)..]);

    public void Dispose()
    {
    }

    private sealed class LogcatLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            string message = $"{category}: {formatter(state, exception)}{(exception is null ? "" : "\n" + exception)}";
            switch (logLevel)
            {
                case LogLevel.Warning:
                    AndroidLog.Warn(Tag, message);
                    break;
                case LogLevel.Error or LogLevel.Critical:
                    AndroidLog.Error(Tag, message);
                    break;
                default:
                    AndroidLog.Info(Tag, message);
                    break;
            }
        }
    }
}
