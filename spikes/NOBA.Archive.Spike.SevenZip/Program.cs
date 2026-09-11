using System.Globalization;
using System.Security.Cryptography;

namespace NOBA.Archive.Spike.SevenZip;

/// <summary>
/// ADR-0003 / risk R1: can <c>7z.dll</c> be driven so that <b>we</b> own every destination stream?
/// If yes, ADR-0005 is implementable and option B (our own interop layer) is viable. If no, the
/// architecture's central security property needs rethinking - which is why this runs now rather than
/// in Phase 3.
/// </summary>
public static class Program
{
    /// <summary>
    /// Bumped whenever the spike changes.
    /// </summary>
    /// <remarks>
    /// Printed in the header so a run can be identified. This exists because a delivered source drop can
    /// carry timestamps <em>older</em> than build output already on the target machine — MSBuild then
    /// considers everything up to date and silently runs the previous binary. Without a visible marker
    /// that looks exactly like "the fix did not work".
    /// </remarks>
    public const string Revision = "r3 — PROPVARIANT=24, OLE BSTR on Windows, platform library discovery";

    private static int _failures;
    private static readonly List<string> Findings = [];

    public static int Main(string[] args)
    {
        var libraryPath = args.Length > 0 ? args[0] : LocateSevenZipLibrary();
        var workRoot = Path.Combine(Path.GetTempPath(), "noba-spike-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workRoot);

        Console.WriteLine("NOBA Archive - 7z interop spike (ADR-0003 / risk R1)");
        Console.WriteLine($"spike build  : {Revision}");
        Console.WriteLine(new string('=', 72));

        try
        {
            using var library = new SevenZipLibrary(libraryPath);
            Console.WriteLine($"library      : {library.LibraryPath}");
            Console.WriteLine($"OLECHAR size : {BStr.CharSize} bytes");
            Console.WriteLine($"PROPVARIANT  : {System.Runtime.InteropServices.Marshal.SizeOf<PropVariant>()} bytes");
            Console.WriteLine($"handlers     : {library.FormatCount}");
            Console.WriteLine();

            var handlers = library.EnumerateHandlers();
            ReportHandlers(handlers);

            var fixtures = TestFixtures.Create(workRoot);

            RunScenario("1. Plain ZIP  - per-entry managed streams", () => ExtractAllToMemory(library, handlers, fixtures.Zip, "zip", null));
            RunScenario("2. Plain 7Z   - per-entry managed streams", () => ExtractAllToMemory(library, handlers, fixtures.SevenZip, "7z", null));
            RunScenario("3. SOLID 7Z   - per-entry managed streams", () => ExtractAllToMemory(library, handlers, fixtures.Solid, "7z", null));
            RunScenario("4. Encrypted 7Z (incl. encrypted names)  ", () => ExtractAllToMemory(library, handlers, fixtures.Encrypted, "7z", "spike-password"));
            RunScenario("5. Selective extraction of one entry     ", () => ExtractSingleEntry(library, handlers, fixtures.Solid));
            RunScenario("6. Refusing an entry (null out-stream)   ", () => RefuseOneEntry(library, handlers, fixtures.SevenZip));
            RunScenario("7. Backend writes nothing to disk        ", () => AssertNoDiskWrites(library, handlers, fixtures.Solid, workRoot));
            RunScenario("8. Archive read through a managed stream ", () => AssertInputIsOurStream(library, handlers, fixtures.SevenZip));
            RunScenario("9. Entry-name fidelity (informational)   ", () => ReportNameFidelity(library, handlers, fixtures));

            Console.WriteLine();
            Console.WriteLine(new string('=', 72));
            Console.WriteLine(_failures == 0 ? "SPIKE RESULT: all scenarios passed." : $"SPIKE RESULT: {_failures} scenario(s) FAILED.");
            Console.WriteLine();
            Console.WriteLine("Findings:");
            foreach (var finding in Findings)
            {
                Console.WriteLine($"  - {finding}");
            }

            return _failures == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SPIKE ABORTED: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 2;
        }
        finally
        {
            try
            {
                Directory.Delete(workRoot, recursive: true);
            }
            catch (IOException)
            {
                // Nothing the spike should fail over.
            }
        }
    }

    /// <summary>
    /// Finds the native library without ever consulting <c>PATH</c> - the same discipline ADR-0009
    /// imposes on the product itself.
    /// </summary>
    private static string LocateSevenZipLibrary()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ?
            [
                @"C:\Program Files\7-Zip\7z.dll",
                @"C:\Program Files (x86)\7-Zip\7z.dll",
            ]
            :
            [
                "/usr/lib/7zip/7z.so",
                "/usr/lib/p7zip/7z.so",
                "/usr/local/lib/7zip/7z.so",
            ];

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            "Could not find the 7-Zip native library. Install 7-Zip, or pass the path as the first " +
            $"argument. Looked in: {string.Join("; ", candidates)}");
    }

    private static void ReportHandlers(IReadOnlyList<HandlerInfo> handlers)
    {
        Console.WriteLine("Handlers present in this build (FORMAT_SUPPORT.md wants this from the binary, not memory):");
        foreach (var handler in handlers.OrderBy(static h => h.Name, StringComparer.OrdinalIgnoreCase))
        {
            var update = handler.CanUpdate ? "read+write" : "read-only ";
            Console.WriteLine($"  {handler.Name,-14} {update}  {handler.Extensions}");
        }

        Console.WriteLine();

        var names = handlers.Select(static h => h.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var claimed in new[] { "zstd", "lz4", "ACE", "lzip" })
        {
            if (names.Contains(claimed))
            {
                Findings.Add($"'{claimed}' IS present as a handler in this build - FORMAT_SUPPORT.md should say so.");
            }
        }

        var rar = handlers.FirstOrDefault(static h => h.Name.Equals("Rar5", StringComparison.OrdinalIgnoreCase));
        if (rar is not null)
        {
            Findings.Add(rar.CanUpdate
                ? "Rar5 reports update support, which contradicts ADR-0004 - investigate before Phase 4."
                : "Rar5 handler reports read-only, exactly as ADR-0004 assumes.");
        }
    }

    private static void RunScenario(string name, Func<string> scenario)
    {
        Console.Write($"{name} ... ");
        try
        {
            var detail = scenario();
            Console.WriteLine($"PASS  {detail}");
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine($"FAIL  {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static OpenedArchive OpenArchive(
        SevenZipLibrary library,
        IReadOnlyList<HandlerInfo> handlers,
        string archivePath,
        string handlerName,
        string? password = null)
    {
        var handler = handlers.FirstOrDefault(h => h.Name.Equals(handlerName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No '{handlerName}' handler in this build.");

        var archive = new InArchive(library.CreateArchiveHandler(handler.ClassId));
        var input = new InStreamWrapper(File.OpenRead(archivePath), ownsStream: true);
        var openCallback = new OpenCallback(password);

        var hr = archive.Open(input, openCallback);
        if (hr != Hresult.S_OK)
        {
            archive.Dispose();
            input.Dispose();
            openCallback.Dispose();
            throw new InvalidOperationException(
                $"Open failed (hr=0x{hr:X8}){(hr == Hresult.S_FALSE && password is null ? " - the archive may have encrypted headers" : string.Empty)}.");
        }

        return new OpenedArchive(archive, input, openCallback);
    }

    private static string ExtractAllToMemory(
        SevenZipLibrary library,
        IReadOnlyList<HandlerInfo> handlers,
        FixtureArchive fixture,
        string handlerName,
        string? password)
    {
        using var opened = OpenArchive(library, handlers, fixture.ArchivePath, handlerName, password);
        var archive = opened.Archive;

        var items = archive.ListItems();
        var buffers = new Dictionary<uint, MemoryStream>();

        using var callback = new ExtractCallback(
            index =>
            {
                var item = items.First(i => i.Index == index);
                if (item.IsDirectory)
                {
                    return null;
                }

                var buffer = new MemoryStream();
                buffers[index] = buffer;
                return buffer;
            },
            password);

        var hr = archive.Extract(callback);
        if (hr != Hresult.S_OK)
        {
            throw new InvalidOperationException($"Extract returned 0x{hr:X8}.");
        }

        // Match decoded bytes against the expected set by content, not by name. Entry-name decoding is
        // a separate concern (scenario 9) and must not be able to disguise itself as a decode failure.
        var outstanding = fixture.Expected.ToDictionary(static kv => kv.Key, static kv => kv.Value, StringComparer.Ordinal);
        var verified = 0;

        foreach (var item in items.Where(static i => !i.IsDirectory))
        {
            if (!buffers.TryGetValue(item.Index, out var buffer))
            {
                throw new InvalidOperationException($"No stream was requested for entry {item.Index}.");
            }

            var actual = buffer.ToArray();
            var match = outstanding.FirstOrDefault(kv => kv.Value.AsSpan().SequenceEqual(actual));
            if (match.Key is null)
            {
                throw new InvalidOperationException(
                    $"Entry {item.Index} ({actual.Length} bytes) does not match any expected content.");
            }

            outstanding.Remove(match.Key);
            verified++;
        }

        if (outstanding.Count != 0)
        {
            throw new InvalidOperationException(
                $"{outstanding.Count} expected entr(ies) were never produced: {string.Join(", ", outstanding.Keys)}.");
        }

        return $"{verified} entries, {callback.GetStreamCalls} GetStream calls" +
               (password is not null
                   ? $", password requested {opened.PasswordRequestsDuringOpen}x on open / {callback.PasswordRequests}x on extract"
                   : string.Empty);
    }

    /// <summary>A solid archive is the hard case: entries share one compressed stream.</summary>
    private static string ExtractSingleEntry(SevenZipLibrary library, IReadOnlyList<HandlerInfo> handlers, FixtureArchive fixture)
    {
        using var opened = OpenArchive(library, handlers, fixture.ArchivePath, "7z");
        var archive = opened.Archive;

        var items = archive.ListItems().Where(static i => !i.IsDirectory).ToList();
        var target = items[^1];

        using var buffer = new MemoryStream();
        using var callback = new ExtractCallback(index => index == target.Index ? buffer : null);

        var hr = archive.Extract(callback, [target.Index]);
        if (hr != Hresult.S_OK)
        {
            throw new InvalidOperationException($"Extract returned 0x{hr:X8}.");
        }

        var actual = buffer.ToArray();
        if (!fixture.Expected.Values.Any(v => v.AsSpan().SequenceEqual(actual)))
        {
            throw new InvalidOperationException($"Selective extraction of entry {target.Index} produced unexpected bytes.");
        }

        return $"entry {target.Index} of {items.Count} solid entries, {buffer.Length} bytes decoded correctly";
    }

    /// <summary>
    /// The refusal path: an entry the path policy rejects gets no stream, and the rest still extract.
    /// This is how a blocked entry will be handled without the backend ever learning a destination.
    /// </summary>
    private static string RefuseOneEntry(SevenZipLibrary library, IReadOnlyList<HandlerInfo> handlers, FixtureArchive fixture)
    {
        using var opened = OpenArchive(library, handlers, fixture.ArchivePath, "7z");
        var archive = opened.Archive;

        var items = archive.ListItems().Where(static i => !i.IsDirectory).ToList();
        var refused = items[0];
        var delivered = new Dictionary<uint, MemoryStream>();

        using var callback = new ExtractCallback(index =>
        {
            if (index == refused.Index)
            {
                return null;
            }

            var buffer = new MemoryStream();
            delivered[index] = buffer;
            return buffer;
        });

        var hr = archive.Extract(callback);
        if (hr != Hresult.S_OK)
        {
            throw new InvalidOperationException($"Extract returned 0x{hr:X8} after a refused entry.");
        }

        if (delivered.ContainsKey(refused.Index))
        {
            throw new InvalidOperationException("The refused entry was written anyway.");
        }

        if (delivered.Count != items.Count - 1)
        {
            throw new InvalidOperationException($"Expected {items.Count - 1} delivered entries, got {delivered.Count}.");
        }

        return $"1 refused, {delivered.Count} extracted, extraction continued";
    }

    /// <summary>
    /// The claim under test, stated as an observable fact: during a full extraction the backend creates
    /// no file anywhere the caller did not create it.
    /// </summary>
    private static string AssertNoDiskWrites(
        SevenZipLibrary library,
        IReadOnlyList<HandlerInfo> handlers,
        FixtureArchive fixture,
        string workRoot)
    {
        var watched = Path.Combine(workRoot, "watched");
        Directory.CreateDirectory(watched);
        var previous = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(watched);

        try
        {
            var before = Directory.GetFileSystemEntries(watched, "*", SearchOption.AllDirectories);
            var tempBefore = Directory.GetFileSystemEntries(Path.GetTempPath(), "7z*", SearchOption.TopDirectoryOnly);

            using var opened = OpenArchive(library, handlers, fixture.ArchivePath, "7z");
            using var callback = new ExtractCallback(_ => new MemoryStream());
            var hr = opened.Archive.Extract(callback);
            if (hr != Hresult.S_OK)
            {
                throw new InvalidOperationException($"Extract returned 0x{hr:X8}.");
            }

            var after = Directory.GetFileSystemEntries(watched, "*", SearchOption.AllDirectories);
            var tempAfter = Directory.GetFileSystemEntries(Path.GetTempPath(), "7z*", SearchOption.TopDirectoryOnly);

            if (after.Length != before.Length)
            {
                throw new InvalidOperationException(
                    $"The backend created {after.Length - before.Length} file(s) in the working directory.");
            }

            if (tempAfter.Length != tempBefore.Length)
            {
                throw new InvalidOperationException(
                    $"The backend created {tempAfter.Length - tempBefore.Length} temp file(s).");
            }

            return "0 files created by the backend, in CWD or temp";
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    /// <summary>
    /// Informational: do decoded entry names survive a round-trip? This never fails the spike, because
    /// it answers a different question from the one the spike exists for - but the answer matters for
    /// Phase 2 (INT-020), so it is measured rather than assumed.
    /// </summary>
    private static string ReportNameFidelity(SevenZipLibrary library, IReadOnlyList<HandlerInfo> handlers, FixtureSet fixtures)
    {
        var verdicts = new List<string>();

        foreach (var (label, fixture, handlerName, password) in new (string, FixtureArchive, string, string?)[]
                 {
                     ("zip", fixtures.Zip, "zip", null),
                     ("7z", fixtures.SevenZip, "7z", null),
                     ("7z+enc", fixtures.Encrypted, "7z", "spike-password"),
                 })
        {
            using var opened = OpenArchive(library, handlers, fixture.ArchivePath, handlerName, password);
            var names = opened.Archive.ListItems()
                .Where(static i => !i.IsDirectory)
                .Select(static i => Path.GetFileName(i.Path.Replace('\\', '/')))
                .ToHashSet(StringComparer.Ordinal);

            var missing = fixture.Expected.Keys.Where(k => !names.Contains(k)).ToList();
            if (missing.Count == 0)
            {
                verdicts.Add($"{label}: exact");
            }
            else
            {
                verdicts.Add($"{label}: {missing.Count} mangled");
                Findings.Add(
                    $"Entry-name decoding is LOSSY for '{label}': {string.Join(", ", missing.Select(static m => $"'{m}'"))} " +
                    "came back mangled. Phase 2 must set the handler's code page explicitly rather than " +
                    "trusting the default (INT-020).");
            }
        }

        return string.Join(", ", verdicts);
    }

    /// <summary>The archive itself is read through our stream, so even input never goes through a path.</summary>
    private static string AssertInputIsOurStream(SevenZipLibrary library, IReadOnlyList<HandlerInfo> handlers, FixtureArchive fixture)
    {
        using var opened = OpenArchive(library, handlers, fixture.ArchivePath, "7z");

        using var callback = new ExtractCallback(_ => new MemoryStream());
        var hr = opened.Archive.Extract(callback);
        if (hr != Hresult.S_OK)
        {
            throw new InvalidOperationException($"Extract returned 0x{hr:X8}.");
        }

        if (opened.BytesReadFromInput <= 0)
        {
            throw new InvalidOperationException("The backend did not read through the managed IInStream.");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{opened.BytesReadFromInput:N0} bytes read via managed IInStream");
    }
}
