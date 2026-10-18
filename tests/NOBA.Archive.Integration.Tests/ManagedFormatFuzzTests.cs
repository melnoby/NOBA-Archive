using NOBA.Archive.Core.Errors;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.TestKit.Fixtures;
using Xunit;

namespace NOBA.Archive.Integration.Tests;

/// <summary>
/// FUZ-001/002/003: THREAT_MODEL.md T5 has claimed "every parse path has a fuzz-tested entry point"
/// since the document was written, and the acceptance gate in §6 names it explicitly - but until this
/// drop nothing in the repository actually generated a malformed byte stream and fed it to a parser.
/// <c>HostileArchiveTests</c> covers two specific, hand-picked corruptions (SEC-017's single flipped
/// payload byte, SEC-018's truncation); this instead explores the space around them.
/// </summary>
/// <remarks>
/// <para>
/// Scope is ZIP, TAR, and GZIP: the three managed formats that build and run without a native
/// dependency, so the harness is exercised on every platform this suite runs on, Linux included. 7Z
/// and RAR parse through the native backend (<c>7z.dll</c>), which is not present in this environment
/// at all - that half of the gate's "ZIP/7Z/TAR/RAR" wording stays open and is recorded as a limitation
/// rather than quietly declared done by a test that only ever ran the managed half.
/// </para>
/// <para>
/// The pass criterion is exactly the gate's own wording: no crash, no unhandled exception. A mutated
/// byte stream is allowed to be rejected — <see cref="ArchiveOperationException"/> and
/// <see cref="OperationCanceledException"/> are both a parser correctly recognizing garbage — but
/// nothing else may escape <c>TestAsync</c> or <c>ExtractAsync</c>. The seed is fixed rather than
/// derived from the clock: a fuzz failure is only useful if the next run reproduces the exact same
/// mutation, and a suite that occasionally fails on an unlucky seed would train everyone to re-run it
/// and ignore the red.
/// </para>
/// </remarks>
public sealed class ManagedFormatFuzzTests
{
    private const int IterationsPerFormat = 300;
    private const int Seed = 20261018;

    [Fact]
    public Task Zip_parsing_survives_a_mutated_corpus_without_crashing() =>
        RunFuzzAsync("FUZ-001", ArchiveFormat.Zip);

    [Fact]
    public Task Tar_parsing_survives_a_mutated_corpus_without_crashing() =>
        RunFuzzAsync("FUZ-002", ArchiveFormat.Tar);

    [Fact]
    public Task Gzip_parsing_survives_a_mutated_corpus_without_crashing() =>
        RunFuzzAsync("FUZ-003", ArchiveFormat.GZip);

    private static async Task RunFuzzAsync(string id, ArchiveFormat format)
    {
        using var sandbox = new TempDirectory(id);
        var baseline = await BuildBaselineAsync(sandbox, format);
        var original = await File.ReadAllBytesAsync(baseline);

        var random = new Random(Seed);
        var failures = new List<string>();

        for (var iteration = 0; iteration < IterationsPerFormat; iteration++)
        {
            var mutated = Mutate(original, random, iteration);
            var candidate = sandbox.Combine($"mutant-{iteration}{format.PrimaryExtension}");
            await File.WriteAllBytesAsync(candidate, mutated);

            await using var engine = new EngineFixture();

            // TestAsync: the read-only integrity pass - every byte of every entry's declared content
            // is walked, which is where a parser trusting a declared size or offset would overrun.
            await TryRunAsync(failures, id, iteration, "TestAsync", () => engine.TestAsync(candidate).AsTask());

            // ExtractAsync: the write path, reached through the same parse but a different handler -
            // SEC-017/018 both go through this one, and a bug could easily live only on this side.
            var destination = sandbox.Combine($"out-{iteration}");
            await TryRunAsync(failures, id, iteration, "ExtractAsync", () => engine.ExtractAsync(candidate, destination).AsTask());
        }

        Assert.True(
            failures.Count == 0,
            $"{id}: {failures.Count}/{IterationsPerFormat} mutations crashed instead of failing cleanly " +
            $"(seed {Seed}):\n{string.Join('\n', failures.Take(5))}");
    }

    private static async Task TryRunAsync(
        List<string> failures, string id, int iteration, string entryPoint, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ArchiveOperationException)
        {
            // A parser correctly recognizing garbage. This is the expected outcome for most mutations.
        }
        catch (OperationCanceledException)
        {
            // Treated the same as a refusal - a guard that aborts rather than keeps parsing.
        }
        catch (Exception ex)
        {
            failures.Add($"{id} iteration {iteration} via {entryPoint}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// One deterministic mutation per iteration, varied by shape so the corpus is not just "flip one
    /// byte" 300 times: a handful of scattered bit flips (the shape bit rot or a truncated transfer
    /// takes), a block of random bytes dropped in (the shape a hostile rewrite takes), or truncation at
    /// a random point (the shape SEC-018 already covers one instance of, explored more broadly here).
    /// </summary>
    private static byte[] Mutate(byte[] original, Random random, int iteration)
    {
        if (original.Length == 0)
        {
            return original;
        }

        var bytes = (byte[])original.Clone();
        switch (iteration % 3)
        {
            case 0:
                var flips = random.Next(1, 9);
                for (var i = 0; i < flips; i++)
                {
                    bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
                }

                break;

            case 1:
                var blockStart = random.Next(bytes.Length);
                var blockLength = Math.Min(random.Next(1, 64), bytes.Length - blockStart);
                random.NextBytes(bytes.AsSpan(blockStart, blockLength));
                break;

            default:
                var keep = random.Next(bytes.Length + 1);
                bytes = bytes[..keep];
                break;
        }

        return bytes;
    }

    private static async Task<string> BuildBaselineAsync(TempDirectory sandbox, ArchiveFormat format)
    {
        var sourceDir = sandbox.Combine("src");
        await WriteFile(Path.Combine(sourceDir, "a.txt"), "the quick brown fox jumps over the lazy dog");
        await WriteFile(Path.Combine(sourceDir, "nested", "b.txt"), new string('x', 2048));

        await using var engine = new EngineFixture();
        var target = sandbox.Combine($"baseline{format.PrimaryExtension}");

        // GZIP holds exactly one member, so it gets one source file rather than the directory the
        // other two formats are happy to recurse into.
        IReadOnlyList<string> sources = format == ArchiveFormat.GZip
            ? [Path.Combine(sourceDir, "a.txt")]
            : [sourceDir];

        await engine.CreateAsync(
            target,
            sources,
            CompressionOptions.ForProfile(format, CompressionProfile.Balanced));

        return target;
    }

    private static async Task WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }
}
