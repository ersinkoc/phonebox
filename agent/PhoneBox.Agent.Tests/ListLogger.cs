using Microsoft.Extensions.Logging;

/// <summary>A logger that keeps the formatted lines, so a test can see what would reach the agent's log.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    private readonly List<string> _lines = [];
    public IReadOnlyList<string> Lines { get { lock (_lines) return _lines.ToList(); } }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_lines) _lines.Add(formatter(state, exception));
    }
}
