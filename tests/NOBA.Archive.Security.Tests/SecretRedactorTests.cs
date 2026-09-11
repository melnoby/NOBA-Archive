using NOBA.Archive.Security;
using Xunit;

namespace NOBA.Archive.Security.Tests;

/// <summary>SEC-014 at the unit level: the redactor itself must not let a secret through.</summary>
public sealed class SecretRedactorTests
{
    private readonly SecretRedactor _redactor = new();

    [Theory]
    [InlineData("password=hunter2")]
    [InlineData("Password: hunter2")]
    [InlineData("archivePassword = \"hunter2\"")]
    [InlineData("api_key=hunter2")]
    [InlineData("passphrase: hunter2")]
    public void Removes_secret_values(string message)
    {
        var redacted = _redactor.Redact(message);

        Assert.DoesNotContain("hunter2", redacted);
        Assert.Contains(SecretRedactor.Mask, redacted);
    }

    [Fact]
    public void Leaves_ordinary_text_alone()
    {
        const string message = "Extracted 42 entries from backup.7z to D:\\restore";

        Assert.Equal(message, _redactor.Redact(message));
    }

    [Theory]
    [InlineData("password", true)]
    [InlineData("ArchivePassword", true)]
    [InlineData("pwd", true)]
    [InlineData("secretKey", true)]
    [InlineData("entryCount", false)]
    [InlineData("path", false)]
    public void Recognises_sensitive_property_names(string name, bool expected)
    {
        Assert.Equal(expected, _redactor.IsSensitivePropertyName(name));
    }

    [Fact]
    public void Handles_empty_input()
    {
        Assert.Equal(string.Empty, _redactor.Redact(string.Empty));
        Assert.False(_redactor.IsSensitivePropertyName(string.Empty));
    }
}
