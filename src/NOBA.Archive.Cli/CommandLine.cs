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
        // manage (DROP 03 CLI surface).
        "remove", "rename",
    };

    /// <summary>
    /// Boolean options recognised with no following value. DROP 13: this registry did not exist -
    /// any "-"-leading token that was not a value-option fell through to <c>options[name] = null</c>
    /// unconditionally, so a shell-selected file literally named e.g. "-backup.zip" was silently
    /// parsed as an unknown bare flag named "backup.zip" rather than a path: <c>Positional</c> stayed
    /// empty (a legitimate archive could never be named), and worse, for a verb expecting several
    /// paths, the *next* real positional argument would be swallowed as that unknown flag's would-be
    /// value in some call shapes - a selected file silently dropped from the operation with no error
    /// at all. Keeping an explicit allow-list means an unrecognised "-"-leading token now falls
    /// through to <see cref="TryParse"/>'s positional branch instead, the same way any other filename
    /// does.
    /// </summary>
    private static readonly HashSet<string> KnownFlags = new(StringComparer.Ordinal)
    {
        "json", "help", "h", "?", "version",
        "desc", "no-links", "solid", "encrypt", "encrypt-names", "overwrite", "password-stdin",
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
        var endOfOptions = false;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Length == 0)
            {
                continue;
            }

            // The conventional escape hatch: everything after a bare "--" is positional, dash-leading
            // or not. A caller that builds an argv from untrusted paths (the shell resolver included -
            // CliApplication.RunAsync parses the raw argv once, before any verb-specific dispatch, so
            // this covers every verb) can use it to guarantee a path is never misread as an option,
            // without this parser having to guess from shape alone.
            if (!endOfOptions && arg == "--")
            {
                endOfOptions = true;
                continue;
            }

            if (!endOfOptions && arg[0] == '-')
            {
                var name = arg.TrimStart('-');
                if (name.Length == 0)
                {
                    error = $"'{arg}' is not a valid option.";
                    return false;
                }

                var eq = name.IndexOf('=', StringComparison.Ordinal);
                var optionName = eq > 0 ? name[..eq] : name;
                var isKnown = ValueOptions.Contains(optionName) || KnownFlags.Contains(optionName);

                if (eq > 0 && isKnown)
                {
                    options[optionName] = name[(eq + 1)..];
                    continue;
                }

                if (eq < 0 && ValueOptions.Contains(name))
                {
                    if (i + 1 >= args.Count)
                    {
                        error = $"Option '--{name}' requires a value.";
                        return false;
                    }

                    options[name] = args[++i];
                    continue;
                }

                if (eq < 0 && KnownFlags.Contains(name))
                {
                    options[name] = null;
                    continue;
                }

                // Not a recognised option at all - fall through and treat the literal token, dash
                // included, as positional. See the DROP 13 remark on KnownFlags above.
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
