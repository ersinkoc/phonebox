namespace PhoneBox.Agent;

/// <summary>Writes logs to %LOCALAPPDATA%\PhoneBox\agent.log (the agent has no console window).</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public static readonly string LogPath = AppPaths.LogPath;

    private readonly StreamWriter _writer;
    private readonly Lock _lock = new();
    private bool _disposed;

    public FileLoggerProvider() : this(LogPath) { }

    internal FileLoggerProvider(string logPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var fi = new FileInfo(logPath);
        var append = !(fi.Exists && fi.Length > 2 * 1024 * 1024); // start over once it exceeds 2 MB
        _writer = new StreamWriter(new FileStream(logPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        { AutoFlush = true };
    }

    public ILogger CreateLogger(string category) => new FileLogger(this, category);

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _writer.Dispose();
        }
    }

    private void Write(string line)
    {
        lock (_lock)
            if (!_disposed) _writer.WriteLine(line);
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) =>
            level >= (category.StartsWith("Microsoft", StringComparison.Ordinal) ? LogLevel.Warning : LogLevel.Information);

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
        {
            if (!IsEnabled(level)) return;
            var cat = category[(category.LastIndexOf('.') + 1)..];
            owner.Write($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level.ToString()[..4].ToLowerInvariant()} {cat}: {fmt(state, ex)}{(ex is null ? "" : " | " + ex.Message)}");
        }
    }
}
