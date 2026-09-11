using Microsoft.Extensions.Logging;
using NOBA.Archive.Core.Abstractions;

namespace NOBA.Archive.Infrastructure.Logging;

/// <summary>
/// Wraps another provider so every message passes through <see cref="ISecretRedactor"/> before it
/// reaches a sink. A password in a log is a security defect, so this sits in front of all of them.
/// </summary>
public sealed class RedactingLoggerProvider : ILoggerProvider
{
    private readonly ILoggerProvider _inner;
    private readonly ISecretRedactor _redactor;

    public RedactingLoggerProvider(ILoggerProvider inner, ISecretRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(redactor);
        _inner = inner;
        _redactor = redactor;
    }

    public ILogger CreateLogger(string categoryName) => new RedactingLogger(_inner.CreateLogger(categoryName), _redactor);

    public void Dispose() => _inner.Dispose();

    private sealed class RedactingLogger : ILogger
    {
        private readonly ILogger _inner;
        private readonly ISecretRedactor _redactor;

        public RedactingLogger(ILogger inner, ISecretRedactor redactor)
        {
            _inner = inner;
            _redactor = redactor;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => _inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            _inner.Log(
                logLevel,
                eventId,
                state,
                exception,
                (s, e) => _redactor.Redact(formatter(s, e)));
        }
    }
}

/// <summary>Convenience for wiring the redactor around every provider already registered.</summary>
public static class RedactingLoggerExtensions
{
    public static ILoggingBuilder AddRedaction(this ILoggingBuilder builder, ISecretRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(redactor);

        var existing = builder.Services
            .Where(static d => d.ServiceType == typeof(ILoggerProvider))
            .ToList();

        foreach (var descriptor in existing)
        {
            builder.Services.Remove(descriptor);
            builder.Services.Add(Microsoft.Extensions.DependencyInjection.ServiceDescriptor.Describe(
                typeof(ILoggerProvider),
                sp => new RedactingLoggerProvider((ILoggerProvider)Instantiate(sp, descriptor), redactor),
                descriptor.Lifetime));
        }

        return builder;
    }

    private static object Instantiate(IServiceProvider provider, Microsoft.Extensions.DependencyInjection.ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is { } instance)
        {
            return instance;
        }

        if (descriptor.ImplementationFactory is { } factory)
        {
            return factory(provider);
        }

        if (descriptor.ImplementationType is { } type)
        {
            return Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance(provider, type);
        }

        throw new InvalidOperationException("The logger provider descriptor has no implementation.");
    }
}
