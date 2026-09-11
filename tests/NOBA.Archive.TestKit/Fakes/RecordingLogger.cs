using System.Globalization;
using Microsoft.Extensions.Logging;

namespace NOBA.Archive.TestKit.Fakes;

/// <summary>
/// Captures everything written to it, which is how the redaction guarantee is asserted rather than
/// assumed: a test logs a password and then checks it is nowhere in this sink.
/// </summary>
public sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _lines = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_gate)
            {
                return [.. _lines];
            }
        }
    }

    public string AllText => string.Join('\n', Lines);

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(this, categoryName);

    internal void Append(string line)
    {
        lock (_gate)
        {
            _lines.Add(line);
        }
    }

    public void Dispose()
    {
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly RecordingLoggerProvider _owner;
        private readonly string _category;

        public RecordingLogger(RecordingLoggerProvider owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            _owner.Append(string.Create(
                CultureInfo.InvariantCulture,
                $"[{logLevel}] {_category}: {formatter(state, exception)}"));
        }
    }
}

/// <summary>A logger that does nothing, for components under test that merely require one.</summary>
public sealed class NullLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => false;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
    }
}
