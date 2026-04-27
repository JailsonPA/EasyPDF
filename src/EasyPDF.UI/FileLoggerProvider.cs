using Microsoft.Extensions.Logging;
using System.IO;
using System.Text;

namespace EasyPDF.UI;

/// <summary>
/// Writes log entries to a daily rolling file under AppData\EasyPDF\logs\.
/// Filtering (Warning+) is applied by the logging infrastructure via AddFilter,
/// so IsEnabled always returns true here and lets the framework decide.
/// Thread-safety is achieved with a shared file lock across all category loggers.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logDirectory;
    private readonly object _fileLock = new();

    public FileLoggerProvider(string logDirectory)
    {
        _logDirectory = logDirectory;
        Directory.CreateDirectory(logDirectory);
    }

    public ILogger CreateLogger(string categoryName) =>
        new FileLogger(categoryName, _logDirectory, _fileLock);

    public void Dispose() { }

    private sealed class FileLogger(string category, string logDir, object fileLock) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // Level filtering is done by the logging infrastructure (AddFilter); always true here.
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (string.IsNullOrEmpty(message) && exception is null) return;

            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            sb.Append(' ');
            sb.Append($"[{logLevel,-11}]");
            sb.Append(' ');
            sb.Append(category);
            sb.Append(": ");
            sb.Append(message);
            if (exception is not null)
            {
                sb.AppendLine();
                sb.Append(exception.ToString());
            }

            var path = Path.Combine(logDir, $"easypdf-{DateTime.Now:yyyy-MM-dd}.log");
            try
            {
                lock (fileLock)
                    File.AppendAllText(path, sb.ToString() + Environment.NewLine);
            }
            catch { /* logging must never propagate exceptions */ }
        }
    }
}
