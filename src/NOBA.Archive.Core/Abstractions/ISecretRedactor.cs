namespace NOBA.Archive.Core.Abstractions;

/// <summary>
/// Sits in front of every logging sink. A password reaching a log is a security defect, so the
/// guarantee is a test, not a convention (THREAT_MODEL.md T4).
/// </summary>
public interface ISecretRedactor
{
    string Redact(string message);

    bool IsSensitivePropertyName(string propertyName);
}
