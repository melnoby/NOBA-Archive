using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NOBA.Archive.Cli.Output;
using NOBA.Archive.Application.Handlers;
using NOBA.Archive.Application.Operations;
using NOBA.Archive.Application.Policies;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Actions;
using NOBA.Archive.Core.Errors;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Operations;
using NOBA.Archive.Core.Primitives;
using NOBA.Archive.Engine;

namespace NOBA.Archive.Cli.Commands;

/// <summary>
/// The CLI, minus the process entry point, so it can be driven directly by tests with captured
/// writers — which is what makes the dual-surface parity suite possible.
/// </summary>
/// <remarks>
/// This class parses arguments, shapes output, and maps errors to exit codes. It contains no
/// compression, extraction, path validation, conflict resolution, or bomb-guard logic: all of that is
/// resolved from the same engine the desktop application uses (ADR-0009), and an architecture test
/// fails the build if a type here ever implements an engine interface.
/// </remarks>
public sealed class CliApplication
{
    public const string ProductName = "NOBA Archive";

    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private readonly TextReader? _stdin;

    public CliApplication(TextWriter stdout, TextWriter stderr, TextReader? stdin = null)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        _stdout = stdout;
        _stderr = stderr;
        _stdin = stdin;
    }

    public static string Version =>
        typeof(CliApplication).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public async Task<ExitCode> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (!CommandLineParser.TryParse(args, out var parsed, out var parseError))
        {
            var output = new CliOutput(_stdout, _stderr, json: false);
            output.WriteError(parseError);
            output.WriteError("Run 'noba-ar help' for usage.");
            return ExitCode.UsageError;
        }

        var io = new CliOutput(_stdout, _stderr, parsed.Json);

        if (parsed.Help && !string.Equals(parsed.Verb, "help", StringComparison.Ordinal))
        {
            WriteHelp(io);
            return ExitCode.Success;
        }

        try
        {
            return parsed.Verb switch
            {
                "help" => Help(io),
                "version" => Version_(io),
                "formats" => await FormatsAsync(io, cancellationToken).ConfigureAwait(false),
                "shell-manifest" => await ShellManifestAsync(io, cancellationToken).ConfigureAwait(false),
                "appx-manifest" => AppxManifest(io, parsed),
                ShellRegistration.ResolverVerb =>
                    await ShellResolveAsync(io, parsed, cancellationToken).ConfigureAwait(false),
                "list" => await ListAsync(io, parsed, cancellationToken).ConfigureAwait(false),
                "info" => await InfoAsync(io, parsed, cancellationToken).ConfigureAwait(false),
                "test" => await TestAsync(io, parsed, cancellationToken).ConfigureAwait(false),
                "extract" => await ExtractAsync(io, parsed, cancellationToken).ConfigureAwait(false),
                "create" => await CreateAsync(io, parsed, cancellationToken).ConfigureAwait(false),
                _ => UnknownVerb(io, parsed.Verb),
            };
        }
        catch (ArchiveOperationException ex)
        {
            return Fail(io, ex.Error);
        }
        catch (OperationCanceledException)
        {
            return Fail(io, ArchiveError.Create(ArchiveErrorCode.Cancelled, "The operation was cancelled."));
        }
    }

    /// <summary>
    /// Builds the CLI's service container. Public so the parity suite can assert that the CLI and the
    /// desktop app resolve the same engine implementations rather than two lookalikes (ADR-0009).
    /// </summary>
    public static ServiceProvider CreateHostServices(
        ConflictPolicy conflictPolicy = ConflictPolicy.Fail,
        Password? password = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(static b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddNobaArchiveEngine(options =>
        {
            // The CLI is headless by definition: it must never block waiting for a human.
            options.Headless = true;
            options.DefaultConflictPolicy = conflictPolicy;
        });

        if (password is not null)
        {
            services.AddSingleton<IPasswordProvider>(new StaticPasswordProvider(password));
        }

        return services.BuildServiceProvider();
    }

    private static ExitCode Help(CliOutput io)
    {
        WriteHelp(io);
        return ExitCode.Success;
    }

    private static ExitCode Version_(CliOutput io)
    {
        io.WriteResult(new CliVersionPayload(
            ProductName,
            Version,
            Environment.Version.ToString(),
            "Phase 3 - ZIP, 7Z, RAR",
            BuildStamp.Value));

        // The build stamp is on the banner rather than buried in --json because the thing it guards
        // against is a human reading a transcript and believing it describes the source they just sent.
        io.WriteLine($"{ProductName} {Version} (.NET {Environment.Version}) - Phase 3: ZIP, 7Z, RAR");
        io.WriteLine($"Source drop: {BuildStamp.Value}");
        return ExitCode.Success;
    }

    /// <summary>
    /// Writes the sparse package manifest that registers the modern context-menu handler.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>SHELL-065, same reason as <c>shell-manifest</c>:</b> the package name, the CLSID and the
    /// verb id live in <c>ShellRegistration</c>, and a manifest typed alongside them is a second copy
    /// of the product's identity. A CLSID that disagrees registers cleanly and never activates.
    /// </para>
    /// <para>
    /// <b>The publisher is required and is not defaulted.</b> It must equal the signing certificate's
    /// subject character for character, and <c>Add-AppxPackage</c> fails with a signature error that
    /// names neither when it does not. A default here would be a value that is right on one machine.
    /// </para>
    /// <para>
    /// This needs no engine and no provider registry: the modern menu declares ONE verb and decides
    /// its contents at draw time, so nothing about formats reaches the manifest.
    /// </para>
    /// </remarks>
    private static ExitCode AppxManifest(CliOutput io, ParsedCommandLine parsed)
    {
        if (!parsed.Options.TryGetValue("publisher", out var publisher) || string.IsNullOrWhiteSpace(publisher))
        {
            io.WriteError(
                "--publisher is required, and must be the signing certificate's subject exactly " +
                "(development signs as CN=NOBA-DEV-ONLY).");
            return ExitCode.UsageError;
        }

        var identity = new ShellPackageIdentity(
            publisher,
            parsed.Options.TryGetValue("package-version", out var version) && !string.IsNullOrWhiteSpace(version)
                ? version
                : "0.5.0.0",
            "NOBA Archive",
            "NOBA");

        io.WriteLine(ShellPackageManifest.Generate(identity));
        return ExitCode.Success;
    }

    /// <summary>
    /// Emits the shell registration manifest as JSON.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The installer needs this description and must not hold a second copy of it. So the one
    /// authority — <see cref="ShellRegistration"/> — is printed here and the registration script reads
    /// what it prints. A verb added in code appears in the installer on the next generation, and an
    /// uninstall list cannot drift from the install list because neither is written by hand.
    /// </para>
    /// <para>
    /// Labels come out in English. That is the interim classic handler's real limitation, recorded as
    /// SHELL-L2: a registry <c>MUIVerb</c> holds one string, and the per-language menu ADR-0006's
    /// <c>IExplorerCommand</c> handler would give is the thing this stands in for.
    /// </para>
    /// </remarks>
    private static async Task<ExitCode> ShellManifestAsync(CliOutput io, CancellationToken cancellationToken)
    {
        await using var provider = CreateHostServices();
        var registry = provider.GetRequiredService<IArchiveProviderRegistry>();

        FormatCapabilityReport ReportFor(ArchiveFormat format) =>
            new(format, registry.CapabilitiesFor(format));

        var creatable = CreatableFormats.From(registry.SupportedFormats, ReportFor);

        var entries = ShellRegistration.Entries(
            ReportFor,
            creatable,
            ShellRegistration.InterimTextFor);

        // Translation only. ShellScope is the decision - which entry may appear on what - and it was
        // made against the capability model in Core. This turns those three words into the classic
        // registry's spelling of them, and decides nothing (D5). SystemFileAssociations is the
        // per-extension shell scope that does not require owning the ProgID.
        var archiveKeys = registry.SupportedFormats
            .Where(f => ReportFor(f).CanOpen)
            .SelectMany(static f => f.Extensions)
            .Where(static e => e.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(static e => $@"SystemFileAssociations\{e}")
            .ToList();

        List<string> KeysFor(ShellScope scope)
        {
            var keys = new List<string>();
            if (scope.HasFlag(ShellScope.Archives))
            {
                keys.AddRange(archiveKeys);
            }

            if (scope.HasFlag(ShellScope.Files))
            {
                keys.Add("*");
            }

            if (scope.HasFlag(ShellScope.Folders))
            {
                keys.Add("Directory");
            }

            return keys;
        }

        var manifest = new
        {
            menuId = ShellRegistration.MenuId,
            menuTitle = ShellRegistration.InterimMenuTitle,
            rootKey = ShellRegistration.RootKey,
            installPathValue = ShellRegistration.InstallPathValue,
            applicationExecutable = ShellRegistration.ApplicationExecutable,
            commandLineExecutable = ShellRegistration.CommandLineExecutable,

            // The verb that resolves an entry against a selection. The classic registry cannot
            // compute a containing folder or a suggested archive name, so it invokes this instead of
            // spelling a command it would get wrong.
            resolverVerb = ShellRegistration.ResolverVerb,

            keysCreatedOnInstall = ShellRegistration.KeysForEntries(entries),
            keysRemovedOnUninstall = ShellRegistration.KeysForEntries(entries).Reverse(),

            entries = entries.Select(e => new
            {
                id = e.Id,
                action = e.Action.ToString(),
                format = e.Format?.Id,
                host = e.Host.ToString(),
                label = e.Label,
                archiveOnly = e.ArchiveOnly,
                scope = e.Scope.ToString(),
                scopes = KeysFor(e.Scope),
                executable = e.Template.Executable,

                // The action contract, as tokens. SHELL-054 compares this to ShellInvocations.
                arguments = e.Template.Tokens,

                // Whether the registry can write this command itself, or must call the resolver.
                directlyRegistrable = ShellInvocations.IsDirectlyRegistrable(e.Template),

                // D4: one invocation for the whole selection, or an entry that only appears for one.
                multiSelect = ShellInvocations.MultiSelectFor(e.Template).ToString(),
            }),
        };

        cancellationToken.ThrowIfCancellationRequested();
        io.WriteLine(System.Text.Json.JsonSerializer.Serialize(manifest, ManifestJson));

        return ExitCode.Success;
    }

    /// <summary>Indented, and shared: the analyzer is right that one per call is waste.</summary>
    private static readonly System.Text.Json.JsonSerializerOptions ManifestJson = new() { WriteIndented = true };

    /// <summary>
    /// Resolves a registered shell entry against a real selection and carries it out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this verb exists.</b> A classic registry command can substitute the selection and nothing
    /// else. "Extract here" needs the folder the archive is in, and "Compress to 7Z" needs an archive
    /// name derived from what was selected — neither is a substitution, both are computations. The
    /// previous registration approximated them and got both wrong (D2, D3). This does the computation
    /// with the same <see cref="ShellMenuBuilder"/> and <see cref="ShellLauncher"/> the tests exercise,
    /// so the shell and the product cannot mean different things.
    /// </para>
    /// <para>
    /// <b>It builds no command line.</b> <see cref="ShellLauncher.PlanFor"/> returns an argument list,
    /// and that list is handed straight back into <see cref="RunAsync"/> in this same process. Nothing
    /// is quoted, nothing is concatenated, no shell is invoked — so a file named
    /// <c>holiday" &amp; del /q *.*.zip</c> is one argument containing those characters.
    /// </para>
    /// <para>
    /// <b>It re-asks the menu.</b> The registry offers an entry on a whole scope — every file, every
    /// folder — and cannot narrow it per selection. So the capability question is asked again here,
    /// against the real selection, and an entry the menu would not have enabled is refused with the
    /// reason rather than attempted. That is how "Compress to GZIP" on three files ends: a refusal
    /// naming the single-member rule, not a broken archive.
    /// </para>
    /// </remarks>
    private async Task<ExitCode> ShellResolveAsync(
        CliOutput io,
        ParsedCommandLine parsed,
        CancellationToken cancellationToken)
    {
        if (parsed.Positional.Count < 2)
        {
            io.WriteError(
                $"{ShellRegistration.ResolverVerb} requires an entry id and at least one selected path: "
                + $"noba-ar {ShellRegistration.ResolverVerb} <entry-id> <paths...>");
            return ExitCode.UsageError;
        }

        var entryId = parsed.Positional[0];
        var selectedPaths = parsed.Positional.Skip(1).ToList();

        await using var provider = CreateHostServices();
        var registry = provider.GetRequiredService<IArchiveProviderRegistry>();

        FormatCapabilityReport ReportFor(ArchiveFormat format) =>
            new(format, registry.CapabilitiesFor(format));

        var creatable = CreatableFormats.From(registry.SupportedFormats, ReportFor);

        var registered = ShellRegistration
            .Entries(ReportFor, creatable, ShellRegistration.InterimTextFor)
            .FirstOrDefault(e => string.Equals(e.Id, entryId, StringComparison.Ordinal));

        if (registered is null)
        {
            io.WriteError($"'{entryId}' is not a registered shell entry for this build.");
            return ExitCode.UsageError;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Directory.Exists is legitimate here and forbidden in menu construction (ARC-061): the person
        // has already chosen the line, so this is no longer inside a held right-click.
        var selection = ShellSelection.From(selectedPaths, Directory.Exists);
        if (selection.IsEmpty)
        {
            io.WriteError("The selection was empty.");
            return ExitCode.UsageError;
        }

        var line = ShellMenuBuilder
            .Build(selection, ReportFor, creatable)
            .FirstOrDefault(m => m.Action == registered.Action
                && (registered.Format is null || m.Format == registered.Format));

        if (line is null)
        {
            io.WriteError($"'{registered.Label}' is not available for this selection.");
            return ExitCode.UnsupportedOperation;
        }

        if (!line.IsEnabled)
        {
            io.WriteError(ExplainBlocker(line, registered));
            return ExitCode.UnsupportedOperation;
        }

        if (ShellMenuBuilder.RequestFor(line, selection) is not { } request
            || ShellLauncher.PlanFor(request) is not { } plan)
        {
            io.WriteError($"'{registered.Label}' cannot be carried out for this selection.");
            return ExitCode.UnsupportedOperation;
        }

        if (!string.Equals(plan.Executable, ShellRegistration.CommandLineExecutable, StringComparison.Ordinal))
        {
            // Only the two shapes the registry cannot spell reach this verb, and both are CLI-hosted.
            // An application-hosted plan arriving here means the registration invoked the wrong thing;
            // saying so beats starting a process this layer has no business starting.
            io.WriteError(
                $"'{registered.Label}' runs in {plan.Executable}, which the shell must invoke directly.");
            return ExitCode.UsageError;
        }

        return await RunAsync(plan.Arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The blocker in words, so a refusal names its rule rather than just declining.</summary>
    private static string ExplainBlocker(ShellMenuEntry line, ShellRegistration.ShellRegisteredAction registered) =>
        line.Blocker switch
        {
            ShellBlocker.SingleMemberFormat =>
                $"{registered.Format?.DisplayName ?? "This format"} holds a single file, and this "
                + "selection is more than one. Use 'Create archive…' to choose a format that can hold "
                + "all of it.",
            _ => $"'{registered.Label}' is not available for this selection.",
        };

    private static async Task<ExitCode> FormatsAsync(CliOutput io, CancellationToken cancellationToken)
    {
        await using var provider = CreateHostServices();
        var registry = provider.GetRequiredService<IArchiveProviderRegistry>();

        var formats = new List<CliFormatPayload>();
        foreach (var format in registry.SupportedFormats.OrderBy(static f => f.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            formats.Add(new CliFormatPayload(
                format.Id,
                format.DisplayName,
                format.PrimaryExtension,
                format.Extensions,
                DescribeCapabilities(registry.CapabilitiesFor(format))));
        }

        io.WriteResult(new CliFormatsPayload(ProductName, Version, formats));

        if (formats.Count == 0)
        {
            io.WriteLine("No archive formats are available in this build.");
        }
        else
        {
            io.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{formats.Count} format(s) available:"));
            foreach (var format in formats)
            {
                io.WriteLine($"  {format.Id,-8} {format.DisplayName,-8} {string.Join(", ", format.Capabilities)}");
            }
        }

        return ExitCode.Success;
    }

    private async Task<ExitCode> ListAsync(CliOutput io, ParsedCommandLine parsed, CancellationToken cancellationToken)
    {
        if (!TryGetArchive(io, parsed, out var archive))
        {
            return ExitCode.UsageError;
        }

        using var password = ReadPassword(parsed);
        await using var services = CreateHostServices(password: password);

        var outcome = await services.GetRequiredService<ListArchiveHandler>()
            .HandleAsync(
                new ListRequest { ArchivePath = archive, LegacyNameCodePage = NameCodePage(parsed) },
                cancellationToken)
            .ConfigureAwait(false);

        var entries = outcome.Entries.Select(static e => new CliEntryPayload(
            e.DisplayPath,
            e.IsDirectory,
            e.UncompressedSize.Bytes,
            e.CompressedSize.Bytes,
            e.Modified,
            e.IsEncrypted,
            e.Rejection?.Reason.ToString())).ToList();

        io.WriteResult(new CliListPayload(
            archive,
            outcome.Format.Id,
            entries.Count,
            entries,
            outcome.NameEncoding?.DisplayName,
            outcome.NameEncoding?.IsGuess ?? false));

        // Said out loud, because the alternative is a user seeing mojibake and concluding the archive
        // is broken. It is not: we guessed, and they can tell us otherwise.
        if (outcome.NameEncoding is { IsGuess: true } naming)
        {
            io.WriteLine(
                $"Entry names were read as {naming.DisplayName}. If they look wrong, re-run with " +
                "--name-encoding <codepage> (1256 Arabic, 1252 Western, 437 DOS).");
        }

        foreach (var entry in entries)
        {
            var marker = entry.BlockedReason is not null ? "!" : entry.IsDirectory ? "d" : " ";
            io.WriteLine($"{marker} {entry.UncompressedSize,12:N0}  {entry.Path}");
        }

        var blocked = entries.Count(static e => e.BlockedReason is not null);
        if (blocked > 0)
        {
            io.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{blocked} entr(ies) marked '!' would be refused on extraction by the security policy."));
        }

        return ExitCode.Success;
    }

    private async Task<ExitCode> InfoAsync(CliOutput io, ParsedCommandLine parsed, CancellationToken cancellationToken)
    {
        if (!TryGetArchive(io, parsed, out var archive))
        {
            return ExitCode.UsageError;
        }

        using var password = ReadPassword(parsed);
        await using var services = CreateHostServices(password: password);

        var outcome = await services.GetRequiredService<ListArchiveHandler>()
            .HandleAsync(
                new ListRequest { ArchivePath = archive, LegacyNameCodePage = NameCodePage(parsed) },
                cancellationToken)
            .ConfigureAwait(false);

        var stats = outcome.Statistics;
        io.WriteResult(new CliInfoPayload(
            archive,
            outcome.Format.Id,
            stats.EntryCount,
            stats.FileCount,
            stats.DirectoryCount,
            stats.EncryptedEntryCount,
            stats.TotalUncompressedSize.Bytes,
            stats.TotalCompressedSize.Bytes,
            stats.CompressionRatio));

        io.WriteLine($"Archive : {archive}");
        io.WriteLine($"Format  : {outcome.Format.DisplayName}");
        io.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Entries : {stats.EntryCount:N0} ({stats.FileCount:N0} files, {stats.DirectoryCount:N0} folders)"));
        io.WriteLine($"Size    : {stats.TotalUncompressedSize} uncompressed, {stats.TotalCompressedSize} stored");
        return ExitCode.Success;
    }

    private async Task<ExitCode> TestAsync(CliOutput io, ParsedCommandLine parsed, CancellationToken cancellationToken)
    {
        if (!TryGetArchive(io, parsed, out var archive))
        {
            return ExitCode.UsageError;
        }

        using var password = ReadPassword(parsed);
        await using var services = CreateHostServices(password: password);
        var ids = services.GetRequiredService<OperationIdFactory>();

        var result = await services.GetRequiredService<TestArchiveHandler>()
            .HandleAsync(new TestRequest { ArchivePath = archive }, ids.Next(OperationType.Test), null, cancellationToken)
            .ConfigureAwait(false);

        io.WriteResult(new CliTestPayload(
            archive,
            result.Status.ToString(),
            result.EntriesTested,
            [.. result.Findings.Select(static f => new CliFindingPayload(f.EntryPath, f.Message, f.IsError))]));

        io.WriteLine($"{result.Status}: {result.EntriesTested:N0} entr(ies) tested.");
        foreach (var finding in result.Findings)
        {
            io.WriteLine($"  {(finding.IsError ? "error" : "warn ")} {finding.EntryPath}: {finding.Message}");
        }

        return result.IsUsable ? ExitCode.Success : ExitCode.CorruptArchive;
    }

    private async Task<ExitCode> ExtractAsync(CliOutput io, ParsedCommandLine parsed, CancellationToken cancellationToken)
    {
        if (!TryGetArchive(io, parsed, out var archive))
        {
            return ExitCode.UsageError;
        }

        var destination = parsed.Option("o") ?? parsed.Option("output");
        if (string.IsNullOrWhiteSpace(destination))
        {
            io.WriteError("extract requires a destination: -o <folder>.");
            return ExitCode.UsageError;
        }

        if (!TryParseConflictPolicy(parsed.Option("conflict"), out var conflictPolicy))
        {
            io.WriteError($"'{parsed.Option("conflict")}' is not a conflict policy. Use overwrite, skip, rename or fail.");
            return ExitCode.UsageError;
        }

        using var password = ReadPassword(parsed);
        await using var services = CreateHostServices(conflictPolicy, password);
        var ids = services.GetRequiredService<OperationIdFactory>();

        var selected = parsed.Positional
            .Skip(1)
            .Select(static p => ArchivePath.TryCreate(p, out var path, out _) ? path : (ArchivePath?)null)
            .Where(static p => p is not null)
            .Select(static p => p!.Value)
            .ToList();

        var options = new ExtractionOptions
        {
            DestinationRoot = Path.GetFullPath(destination),
            ConflictPolicy = conflictPolicy,
            LinkPolicy = parsed.HasFlag("no-links") ? LinkPolicy.Never : LinkPolicy.Never,
            SelectedEntries = selected.Count > 0 ? selected : null,
        };

        var operationId = ids.Next(OperationType.Extract);
        var outcome = await services.GetRequiredService<ExtractArchiveHandler>()
            .HandleAsync(
                new ExtractRequest
                {
                    ArchivePath = archive,
                    Options = options,
                    LegacyNameCodePage = NameCodePage(parsed),
                },
                operationId,
                null,
                null,
                cancellationToken)
            .ConfigureAwait(false);

        io.WriteResult(new CliExtractPayload(
            operationId.Value,
            archive,
            options.DestinationRoot,
            outcome.EntriesWritten,
            outcome.EntriesSkipped,
            outcome.DirectoriesCreated,
            outcome.BytesWritten.Bytes,
            [.. outcome.Blocked.Select(static b => new CliBlockedPayload(b.RawPath, b.Reason.ToString(), b.Detail))]));

        io.WriteLine($"Operation {operationId.Value}");
        io.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Extracted {outcome.EntriesWritten:N0} file(s), {outcome.DirectoriesCreated:N0} folder(s), {outcome.BytesWritten} to {options.DestinationRoot}"));

        if (outcome.HadBlockedEntries)
        {
            io.WriteLine($"{outcome.Blocked.Count} entr(ies) were refused by the security policy:");
            foreach (var blocked in outcome.Blocked)
            {
                io.WriteLine($"  {blocked.Reason}: {blocked.RawPath}");
            }
        }

        // Blocked entries are reported, not fatal: the safe entries were extracted, and hiding the
        // refusal behind a success code would defeat the point of reporting it.
        return ExitCode.Success;
    }

    private async Task<ExitCode> CreateAsync(CliOutput io, ParsedCommandLine parsed, CancellationToken cancellationToken)
    {
        if (parsed.Positional.Count < 2)
        {
            io.WriteError("create requires an archive path and at least one input: noba-ar create <archive> <inputs...>");
            return ExitCode.UsageError;
        }

        var target = parsed.Positional[0];
        var sources = parsed.Positional.Skip(1).ToList();

        var formatId = parsed.Option("format") ?? InferFormatId(target);
        var format = ArchiveFormat.FromId(formatId);
        if (format is null)
        {
            io.WriteError($"'{formatId}' is not a known archive format. Run 'noba-ar formats'.");
            return ExitCode.UsageError;
        }

        if (!TryParseProfile(parsed.Option("profile"), out var profile))
        {
            io.WriteError($"'{parsed.Option("profile")}' is not a profile. Use fast, balanced, max or smallest.");
            return ExitCode.UsageError;
        }

        var options = CompressionOptions.ForProfile(format, profile);
        if (parsed.Option("level") is { } levelText)
        {
            if (!int.TryParse(levelText, CultureInfo.InvariantCulture, out var level))
            {
                io.WriteError($"'{levelText}' is not a compression level (0-9).");
                return ExitCode.UsageError;
            }

            options = options with { Level = new Core.Primitives.CompressionLevel(level), Profile = CompressionProfile.Custom };
        }

        if (parsed.HasFlag("solid"))
        {
            options = options with { Solid = true };
        }

        if (parsed.HasFlag("encrypt") || parsed.HasFlag("encrypt-names"))
        {
            options = options with
            {
                Encryption = EncryptionAlgorithm.Aes256,
                EncryptFileNames = parsed.HasFlag("encrypt-names"),
            };
        }

        // The gate is checked here as well as inside the handler so that the answer is a usage error
        // naming the option, rather than a capability failure two layers down. A build whose native
        // library is missing can create ZIP and nothing else, and the user should be told which of
        // their switches is the problem.
        using var probe = CreateHostServices();
        var available = probe.GetRequiredService<IArchiveProviderRegistry>().CapabilitiesFor(format);
        var wanted = options.RequiredCapabilities();
        if (CapabilityGate.Check(available, wanted, format) is { } refusal)
        {
            io.WriteError(refusal.TechnicalDetail);
            return ExitCode.UnsupportedOperation;
        }

        using var password = ReadPassword(parsed);
        await using var services = CreateHostServices(password: password);
        var ids = services.GetRequiredService<OperationIdFactory>();
        var operationId = ids.Next(OperationType.Create);

        var outcome = await services.GetRequiredService<CreateArchiveHandler>()
            .HandleAsync(
                new CreateRequest
                {
                    TargetPath = Path.GetFullPath(target),
                    Sources = sources,
                    Options = options,
                    Overwrite = parsed.HasFlag("overwrite"),
                },
                operationId,
                null,
                null,
                cancellationToken)
            .ConfigureAwait(false);

        io.WriteResult(new CliCreatePayload(
            operationId.Value,
            outcome.ArchivePath,
            format.Id,
            outcome.EntriesAdded,
            outcome.BytesRead.Bytes,
            outcome.ArchiveSize.Bytes));

        io.WriteLine($"Operation {operationId.Value}");
        io.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Created {outcome.ArchivePath}: {outcome.EntriesAdded:N0} entr(ies), {outcome.ArchiveSize} from {outcome.BytesRead}"));
        return ExitCode.Success;
    }

    /// <summary>
    /// The "Open as…" escape hatch for legacy archives whose names carry no encoding (INT-020).
    /// </summary>
    private static int? NameCodePage(ParsedCommandLine parsed) =>
        parsed.Option("name-encoding") is { } text &&
        int.TryParse(text, CultureInfo.InvariantCulture, out var codePage)
            ? codePage
            : null;

    private static bool TryGetArchive(CliOutput io, ParsedCommandLine parsed, out string archive)
    {
        archive = string.Empty;
        if (parsed.Positional.Count == 0)
        {
            io.WriteError($"{parsed.Verb} requires an archive path.");
            return false;
        }

        archive = Path.GetFullPath(parsed.Positional[0]);
        return true;
    }

    /// <summary>
    /// Reads a password from stdin or an ACL-checked file. Never from an argument or an environment
    /// variable: on Windows a same-user process can read both a command line and an environment block.
    /// </summary>
    private Password? ReadPassword(ParsedCommandLine parsed)
    {
        if (parsed.HasFlag("password-stdin"))
        {
            var line = _stdin?.ReadToEnd()?.TrimEnd('\r', '\n');
            return string.IsNullOrEmpty(line) ? null : Password.FromChars(line);
        }

        if (parsed.Option("password-file") is { } file)
        {
            var text = File.ReadAllText(file).TrimEnd('\r', '\n');
            return string.IsNullOrEmpty(text) ? null : Password.FromChars(text);
        }

        return null;
    }

    private static bool TryParseConflictPolicy(string? value, out ConflictPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            policy = ConflictPolicy.Fail;
            return true;
        }

        switch (value.ToLowerInvariant())
        {
            case "overwrite": policy = ConflictPolicy.Overwrite; return true;
            case "skip": policy = ConflictPolicy.Skip; return true;
            case "rename": policy = ConflictPolicy.Rename; return true;
            case "fail": policy = ConflictPolicy.Fail; return true;
            default: policy = ConflictPolicy.Fail; return false;
        }
    }

    private static bool TryParseProfile(string? value, out CompressionProfile profile)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            profile = CompressionProfile.Balanced;
            return true;
        }

        switch (value.ToLowerInvariant())
        {
            case "fast": profile = CompressionProfile.Fast; return true;
            case "balanced": profile = CompressionProfile.Balanced; return true;
            case "max": profile = CompressionProfile.MaximumCompression; return true;
            case "smallest": profile = CompressionProfile.SmallestSize; return true;
            default: profile = CompressionProfile.Balanced; return false;
        }
    }

    private static string InferFormatId(string target) =>
        ArchiveFormat.GuessFromExtension(target)?.Id ?? ArchiveFormat.Zip.Id;

    private static List<string> DescribeCapabilities(ArchiveCapabilities capabilities) =>
        [.. Enum.GetValues<ArchiveCapabilities>()
            .Where(static c => c != ArchiveCapabilities.None && c != ArchiveCapabilities.ReadOnlySet)
            .Where(c => capabilities.Has(c))
            .Select(static c => c.ToString())];

    private static ExitCode UnknownVerb(CliOutput io, string verb)
    {
        io.WriteResult(new CliErrorPayload("UnknownCommand", $"Unknown command '{verb}'.", ExitCode.UsageError));
        io.WriteError($"Unknown command '{verb}'. Run 'noba-ar help' for usage.");
        return ExitCode.UsageError;
    }

    private static ExitCode Fail(CliOutput io, ArchiveError error)
    {
        var code = ExitCodeMapper.FromError(error.Code);
        io.WriteResult(new CliErrorPayload(error.Code.ToString(), error.TechnicalDetail, code));
        io.WriteError($"{error.Code}: {error.TechnicalDetail}");
        return code;
    }

    private static void WriteHelp(CliOutput io)
    {
        io.WriteLine($"{ProductName} command-line interface (noba-ar {Version})");
        io.WriteLine(string.Empty);
        io.WriteLine("Usage:");
        io.WriteLine("  noba-ar <command> [options]");
        io.WriteLine(string.Empty);
        io.WriteLine("Commands:");
        io.WriteLine("  list <archive>                     List entries without extracting anything");
        io.WriteLine("  info <archive>                     Summarise format, entry counts and sizes");
        io.WriteLine("  test <archive>                     Verify integrity");
        io.WriteLine("  extract <archive> [entries...]     Extract to -o <folder>");
        io.WriteLine("  create <archive> <inputs...>       Create an archive");
        io.WriteLine("  formats                            Formats this build supports, with capabilities");
        io.WriteLine("  version                            Print product and runtime version");
        io.WriteLine("  help                               Show this text");
        io.WriteLine(string.Empty);
        io.WriteLine("Options:");
        io.WriteLine("  -o, --output <folder>              Extraction destination");
        io.WriteLine("  --conflict overwrite|skip|rename|fail   What to do when a file already exists");
        io.WriteLine("  --format 7z|zip                    Archive format when creating");
        io.WriteLine("  --profile fast|balanced|max|smallest    Compression preset");
        io.WriteLine("  --level 0-9                        Explicit compression level");
        io.WriteLine("  --overwrite                        Replace an existing archive when creating");
        io.WriteLine("  --solid                            Solid compression (7Z)");
        io.WriteLine("  --encrypt                          Encrypt contents (needs a password)");
        io.WriteLine("  --encrypt-names                    Encrypt the file list as well as the contents");
        io.WriteLine(string.Empty);
        io.WriteLine("  'noba-ar formats' lists what this build can actually create: the switches above");
        io.WriteLine("  are refused rather than ignored when the backend behind a format cannot honour");
        io.WriteLine("  them.");
        io.WriteLine("  --json                             Emit one JSON document on stdout; text to stderr");
        io.WriteLine("  --help                             Show this text");
        io.WriteLine(string.Empty);
        io.WriteLine("  --name-encoding <cp>  read legacy entry names as this code page (1256, 1252, 437...)");
        io.WriteLine("Passwords are read from stdin (--password-stdin) or an ACL-checked file");
        io.WriteLine("(--password-file). They are never accepted as a command-line argument or an");
        io.WriteLine("environment variable: on Windows another process running as the same user can");
        io.WriteLine("read both a process's command line and its environment block.");
        io.WriteLine(string.Empty);
        io.WriteLine("Exit codes: 0 success, 1 usage, 2 archive not found, 3 unsupported operation,");
        io.WriteLine("4 password required, 5 corrupt archive, 6 blocked by security policy,");
        io.WriteLine("7 insufficient disk space, 8 cancelled, 9 I/O failure, 10 backend failure.");
    }
}
