using System.Globalization;
using System.Text.RegularExpressions;
using NOBA.Archive.Core.Operations;

namespace NOBA.Archive.Core.Primitives;

/// <summary>
/// Correlation identifier for a single archive operation, e.g. <c>EX-20260819-000142</c>.
/// Quoted by users in error reports and attached to every log scope.
/// </summary>
public readonly partial record struct OperationId
{
    public const string Pattern = @"^(EX|CR|AD|RM|TS|LS|SR)-\d{8}-\d{6}$";

    public string Value { get; }

    private OperationId(string value) => Value = value;

    public static OperationId Create(OperationType type, DateTimeOffset stamp, int sequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sequence, 999_999);
        var value = string.Create(
            CultureInfo.InvariantCulture,
            $"{Prefix(type)}-{stamp.UtcDateTime:yyyyMMdd}-{sequence:D6}");
        return new OperationId(value);
    }

    public static bool TryParse(string? value, out OperationId id)
    {
        if (!string.IsNullOrEmpty(value) && ShapeRegex().IsMatch(value))
        {
            id = new OperationId(value);
            return true;
        }

        id = default;
        return false;
    }

    public static string Prefix(OperationType type) => type switch
    {
        OperationType.Extract => "EX",
        OperationType.Create => "CR",
        OperationType.Add => "AD",
        OperationType.Remove => "RM",
        OperationType.Test => "TS",
        OperationType.List => "LS",
        OperationType.Search => "SR",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown operation type."),
    };

    public override string ToString() => Value ?? string.Empty;

    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant)]
    private static partial Regex ShapeRegex();
}
