using Microsoft.Extensions.Logging;

namespace SentenceStudio.Services;

/// <summary>
/// Simple file logger provider for troubleshooting.
/// Writes all log messages to a file that can be read from disk.
/// </summary>
public class FileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;
    private readonly object _lock = new();

    public FileLoggerProvider(string filePath)
    {
        _filePath = filePath;
        
        // Clear the log file on startup
        try
        {
            File.WriteAllText(_filePath, $"=== Log started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
        }
        catch { }
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new FileLogger(_filePath, categoryName, _lock);
    }

    public void Dispose() { }
}

/// <summary>
/// Simple file logger that appends log messages to a file.
/// </summary>
public class FileLogger : ILogger
{
    private readonly string _filePath;
    private readonly string _categoryName;
    private readonly object _lock;

    public FileLogger(string filePath, string categoryName, object lockObj)
    {
        _filePath = filePath;
        _categoryName = categoryName;
        _lock = lockObj;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var message = formatter(state, exception);
        var logLine = $"{DateTime.Now:HH:mm:ss.fff} [{logLevel}] {_categoryName}: {message}";
        
        if (exception != null)
        {
            logLine += $"{Environment.NewLine}  Exception: {exception.GetType().Name}: {exception.Message}";
            logLine += $"{Environment.NewLine}  StackTrace: {exception.StackTrace}";
        }

        try
        {
            lock (_lock)
            {
                File.AppendAllText(_filePath, logLine + Environment.NewLine);
            }
        }
        catch { }
    }
}
