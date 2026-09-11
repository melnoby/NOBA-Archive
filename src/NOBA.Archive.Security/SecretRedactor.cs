using System.Text.RegularExpressions;
using NOBA.Archive.Core.Abstractions;

namespace NOBA.Archive.Security;

/// <summary>
/// Strips secrets from anything on its way to a log sink. Paired with a test that asserts a password
/// handed to <c>ILogger</c> does not appear in any sink's output.
/// </summary>
public sealed partial class SecretRedactor : ISecretRedactor
{
    public const string Mask = "***";

    private static readonly string[] SensitiveNames =
    [
        "password", "passwd", "pwd", "passphrase", "secret", "key", "token", "credential",
    ];

    public string Redact(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message;
        }

        return AssignmentRegex().Replace(message, m => $"{m.Groups["name"].Value}{m.Groups["sep"].Value}{Mask}");
    }

    public bool IsSensitivePropertyName(string propertyName)
    {
        if (string.IsNullOrEmpty(propertyName))
        {
            return false;
        }

        foreach (var name in SensitiveNames)
        {
            if (propertyName.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // name = value / name: value / name="value", where name contains a sensitive word.
    [GeneratedRegex(
        """(?<name>[A-Za-z0-9_.\-]*?(?i:password|passwd|pwd|passphrase|secret|key|token|credential)[A-Za-z0-9_]*)(?<sep>\s*[:=]\s*)(?<value>"[^"]*"|'[^']*'|[^\s,;)}\]]+)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex AssignmentRegex();
}
