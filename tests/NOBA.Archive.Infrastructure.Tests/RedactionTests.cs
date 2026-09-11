using Microsoft.Extensions.Logging;
using NOBA.Archive.Core.Primitives;
using NOBA.Archive.Infrastructure.Logging;
using NOBA.Archive.Security;
using NOBA.Archive.TestKit.Fakes;
using Xunit;

namespace NOBA.Archive.Infrastructure.Tests;

/// <summary>
/// SEC-014. The rule "never log a password" is worth exactly as much as the test that proves it, so
/// this drives a real logger through the real provider and inspects what the sink received.
/// </summary>
public sealed class RedactionTests
{
    private const string Secret = "correct-horse-battery-staple";

    [Fact]
    public void A_password_written_through_the_logger_never_reaches_the_sink()
    {
        var sink = new RecordingLoggerProvider();
        using var provider = new RedactingLoggerProvider(sink, new SecretRedactor());
        var logger = provider.CreateLogger("test");

        logger.LogInformation("Opening archive with password={Password}", Secret);

        Assert.NotEmpty(sink.Lines);
        Assert.DoesNotContain(Secret, sink.AllText);
        Assert.Contains(SecretRedactor.Mask, sink.AllText);
    }

    [Fact]
    public void Ordinary_diagnostics_survive_redaction_intact()
    {
        var sink = new RecordingLoggerProvider();
        using var provider = new RedactingLoggerProvider(sink, new SecretRedactor());
        var logger = provider.CreateLogger("test");

        logger.LogWarning("Operation {OperationId} blocked {Count} entries", "EX-20260819-000142", 3);

        Assert.Contains("EX-20260819-000142", sink.AllText);
        Assert.Contains("3", sink.AllText);
    }

    /// <summary>Even interpolating the Password type itself must produce nothing useful.</summary>
    [Fact]
    public void The_password_type_cannot_leak_through_interpolation()
    {
        using var password = Password.FromChars(Secret);
        var sink = new RecordingLoggerProvider();
        using var provider = new RedactingLoggerProvider(sink, new SecretRedactor());
        var logger = provider.CreateLogger("test");

        logger.LogInformation("Using {Password}", password);

        Assert.DoesNotContain(Secret, sink.AllText);
        Assert.Contains("***", sink.AllText);
    }

    [Fact]
    public void Exception_messages_are_redacted_too()
    {
        var sink = new RecordingLoggerProvider();
        using var provider = new RedactingLoggerProvider(sink, new SecretRedactor());
        var logger = provider.CreateLogger("test");

        logger.LogError(new InvalidOperationException("boom"), "Failed with password={Password}", Secret);

        Assert.DoesNotContain(Secret, sink.AllText);
    }
}
