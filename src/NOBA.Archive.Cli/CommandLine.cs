using System.Diagnostics.CodeAnalysis;

namespace NOBA.Archive.Cli;

/// <summary>A parsed command line: verb, positional arguments, and flags.</summary>
public sealed record ParsedCommandLine
{
    public required string Verb { get; init; }

    public IReadOnlyList<string> Positional { get; init; } = [];

    public IReadOnlyDictionary<string, string?> Options { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);

    public bool Json => HasFlag("json");

    public bool Help => HasFlag("help") || HasFlag("h") || HasFlag("?");

    public bool HasFlag(string name) => Options.ContainsKey(name);

    public string? Option(string name) => Options.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// A deliberately small parser. Keeping it hand-written avoids taking a dependency for the sake of
/// three verbs, and keeps the CLI what it is supposed to be: parsing, output, exit codes.
/// </summary>
public static class CommandLineParser
{
    /// <summary>Options that take a following value rather than acting as a flag.</summary>
    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "o", "output", "format", "profile", "level", "conflict", "password-file", "split", "max-ratio", "max-entries",
        "name-encoding",
        // appx-manifest. An option missing from this set is parsed as a FLAG, so its value becomes a
        // positional argument and the option reads as absent - which is how `--publisher CN=...`
        // silently became "publisher is required".
        "publisher", "package-version",
        // search (DROP 07).
        "name", "ext", "kind", "min-size", "max-size", "modified-after", "modified-before", "sort",
    };

    public static bool TryParse(
        IReadOnlyList<string> args,
        [NotNullWhen(true)] out ParsedCommandLine? parsed,
        [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        parsed = null;
        error = null;

        var positional = new List<string>();
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        string? verb = null;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Length == 0)
            {
                continue;
            }

            if (arg[0] == '-')
            {
                var name = arg.TrimStart('-');
                if (name.Length == 0)
                {
                    error = $"'{arg}' is not a valid option.";
                    return false;
                }

                var eq = name.IndexOf('=', StringComparison.Ordinal);
                if (eq > 0)
                {
                    options[name[..eq]] = name[(eq + 1)..];
                    continue;
                }

                if (ValueOptions.Contains(name))
                {
                    if (i + 1 >= args.Count)
                    {
                        error = $"Option '--{name}' requires a value.";
                        return false;
                    }

                    options[name] = args[++i];
                    continue;
                }

                options[name] = null;
                continue;
            }

            if (verb is null)
            {
                verb = arg;
            }
            else
            {
                positional.Add(arg);
            }
        }

        // "noba-ar --help" with no verb is a help request, not a usage error.
        if (verb is null)
        {
            verb = options.ContainsKey("version") ? "version" : "help";
        }

        parsed = new ParsedCommandLine
        {
            Verb = verb,
            Positional = positional,
            Options = options,
        };
        return true;
    }
}
