using System.Security.Cryptography;
using System.Text;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Errors;
using NOBA.Archive.Infrastructure.FileSystem;
using NOBA.Archive.TestKit.Fixtures;
using Xunit;

namespace NOBA.Archive.Integration.Tests;

/// <summary>
/// CRT-014/015/016: the three real-world write failures <c>docs/PRODUCT_PLAN.md</c>'s L13 names as in
/// scope for Create — disk-full, permission-denied, and a locked source — were previously unexercised
/// anywhere in the suite.
/// </summary>
/// <remarks>
/// <para>
/// None of the three can be provoked deterministically on a real filesystem in CI (filling a disk,
/// revoking an ACL, or holding a file open with no shared read access mid-test is not something a
/// shared test machine can do safely), so all three are injected at <see cref="IFileSystem"/> — the one
/// seam <see cref="PhysicalFileSystem"/> itself translates real OS errors at (see its <c>Translate</c>
/// method). <see cref="FaultInjectingFileSystem"/> wraps the real filesystem and throws the same
/// <see cref="ArchiveOperationException"/> a genuine disk-full, access-denied, or sharing-violation
/// error would produce, at the same call sites production code already goes through
/// <see cref="EngineFixture"/> to reach.
/// </para>
/// <para>
/// Same two-part assertion as the rest of <see cref="CreationMatrixTests"/>: the operation is reported
/// as failed, <em>and</em> nothing is left on disk — no half-written archive, no orphaned staging file.
/// </para>
/// </remarks>
public sealed class CreationFailureInjectionTests
{
    /// <summary>Nothing published, and no staging file masquerading as content.</summary>
    private static void AssertNothingLandedAt(string archive, TempDirectory sandbox)
    {
        Assert.False(File.Exists(archive), $"a refused creation published '{archive}'");

        foreach (var leftover in Directory.EnumerateFiles(sandbox.Path, ".noba-*.tmp", SearchOption.AllDirectories))
        {
            Assert.Fail($"a staging file survived the failure: {leftover}");
        }
    }

    /// <summary>
    /// CRT-014: the volume fills up partway through writing the compressed data, not at the moment of
    /// opening the staging file — the shape a real disk-full failure actually takes.
    /// </summary>
    [Fact]
    public async Task A_volume_that_fills_up_mid_write_fails_cleanly_and_leaves_nothing()
    {
        using var sandbox = new TempDirectory("CRT-014");
        var source = sandbox.Combine("large.bin");

        // Incompressible and comfortably larger than the write budget below, so the fault fires
        // during AddAsync rather than only at the end — proving this is a mid-write failure, not a
        // same-as-CreateNew failure in disguise.
        await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(8 * 1024 * 1024));
        var archive = sandbox.Combine("out.zip");

        var diskFull = new ArchiveOperationException(ArchiveError.Create(
            ArchiveErrorCode.DiskFull,
            "There is not enough space on the destination volume.",
            archive));

        var fileSystem = new FaultInjectingFileSystem(
            new PhysicalFileSystem(),
            writeFaultAfterBytes: 32 * 1024,
            writeFaultError: diskFull);

        await using var engine = new EngineFixture(password: null, fileSystemOverride: fileSystem);
        var failure = await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await engine.CreateAsync(archive, [source]));

        Assert.Equal(ArchiveErrorCode.DiskFull, failure.Error.Code);
        AssertNothingLandedAt(archive, sandbox);
    }

    /// <summary>
    /// CRT-015: the destination cannot be opened for writing at all — an ACL or a read-only location,
    /// discovered before a single byte of the archive is produced.
    /// </summary>
    [Fact]
    public async Task A_destination_that_cannot_be_opened_fails_cleanly_and_leaves_nothing()
    {
        using var sandbox = new TempDirectory("CRT-015");
        var source = await WriteFile(sandbox.Combine("notes.txt"), "content");
        var archive = sandbox.Combine("out.zip");

        var accessDenied = new ArchiveOperationException(ArchiveError.Create(
            ArchiveErrorCode.AccessDenied,
            "Access to the destination is denied.",
            archive));

        var fileSystem = new FaultInjectingFileSystem(new PhysicalFileSystem(), createNewError: accessDenied);

        await using var engine = new EngineFixture(password: null, fileSystemOverride: fileSystem);
        var failure = await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await engine.CreateAsync(archive, [source]));

        Assert.Equal(ArchiveErrorCode.AccessDenied, failure.Error.Code);
        AssertNothingLandedAt(archive, sandbox);
    }

    /// <summary>
    /// CRT-016: a source file is locked by another process — already open with no shared read access —
    /// discovered while NOBA Archive reads it to compress it, not while opening the destination.
    /// </summary>
    [Fact]
    public async Task A_source_locked_by_another_process_fails_cleanly_and_leaves_nothing()
    {
        using var sandbox = new TempDirectory("CRT-016");
        var locked = await WriteFile(sandbox.Combine("locked.txt"), "content");
        var archive = sandbox.Combine("out.zip");

        var sharingViolation = new ArchiveOperationException(ArchiveError.Create(
            ArchiveErrorCode.AccessDenied,
            "The process cannot access the file because it is being used by another process.",
            locked));

        var fileSystem = new FaultInjectingFileSystem(new PhysicalFileSystem(), openReadError: sharingViolation);

        await using var engine = new EngineFixture(password: null, fileSystemOverride: fileSystem);
        var failure = await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await engine.CreateAsync(archive, [locked]));

        Assert.Equal(ArchiveErrorCode.AccessDenied, failure.Error.Code);
        AssertNothingLandedAt(archive, sandbox);
    }

    private static async Task<string> WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, Encoding.UTF8);
        return path;
    }
}

/// <summary>
/// Wraps a real <see cref="IFileSystem"/> and injects exactly one failure, at one of three call sites:
/// opening the staging file (<see cref="CreateNew"/>, for "destination cannot be opened"), a write to
/// the stream it returns after a fixed byte budget (for "volume fills up mid-write"), or opening a
/// source file for reading (<see cref="OpenRead"/>, for "source locked by another process"). Everything
/// else is forwarded untouched — this is a scalpel, not a fake filesystem.
/// </summary>
internal sealed class FaultInjectingFileSystem : IFileSystem
{
    private readonly IFileSystem _inner;
    private readonly ArchiveOperationException? _createNewError;
    private readonly long _writeFaultAfterBytes;
    private readonly ArchiveOperationException? _writeFaultError;
    private readonly ArchiveOperationException? _openReadError;

    public FaultInjectingFileSystem(
        IFileSystem inner,
        ArchiveOperationException? createNewError = null,
        long writeFaultAfterBytes = long.MaxValue,
        ArchiveOperationException? writeFaultError = null,
        ArchiveOperationException? openReadError = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _createNewError = createNewError;
        _writeFaultAfterBytes = writeFaultAfterBytes;
        _writeFaultError = writeFaultError;
        _openReadError = openReadError;
    }

    public Stream CreateNew(string path, long? preallocateBytes = null)
    {
        if (_createNewError is { } error)
        {
            throw error;
        }

        var stream = _inner.CreateNew(path, preallocateBytes);
        return _writeFaultError is { } fault
            ? new FaultingStream(stream, _writeFaultAfterBytes, fault)
            : stream;
    }

    public bool FileExists(string path) => _inner.FileExists(path);

    public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

    public bool IsReparsePoint(string path) => _inner.IsReparsePoint(path);

    public FileSystemEntryInfo? GetInfo(string path) => _inner.GetInfo(path);

    public IEnumerable<FileSystemEntryInfo> EnumerateEntries(string directory, bool recursive, CancellationToken cancellationToken) =>
        _inner.EnumerateEntries(directory, recursive, cancellationToken);

    public Stream OpenRead(string path) => _openReadError is { } error ? throw error : _inner.OpenRead(path);

    public void CreateDirectory(string path) => _inner.CreateDirectory(path);

    public void MoveFile(string sourcePath, string destinationPath, bool overwrite) =>
        _inner.MoveFile(sourcePath, destinationPath, overwrite);

    public void ReplaceFile(string sourcePath, string destinationPath, string? backupPath) =>
        _inner.ReplaceFile(sourcePath, destinationPath, backupPath);

    public void DeleteFile(string path) => _inner.DeleteFile(path);

    public void DeleteDirectory(string path, bool recursive) => _inner.DeleteDirectory(path, recursive);

    public void SetTimestamps(string path, DateTimeOffset? created, DateTimeOffset? modified, DateTimeOffset? accessed) =>
        _inner.SetTimestamps(path, created, modified, accessed);

    public void SetAttributes(string path, FileAttributes attributes) => _inner.SetAttributes(path, attributes);

    public VolumeSpace GetVolumeSpace(string path) => _inner.GetVolumeSpace(path);

    /// <summary>
    /// Lets a fixed number of bytes through, then fails every write after that — the shape of a volume
    /// that runs out of room partway through, not at the moment the (empty) staging file was opened.
    /// </summary>
    private sealed class FaultingStream : Stream
    {
        private readonly Stream _inner;
        private readonly ArchiveOperationException _error;
        private long _remainingBudget;
        private bool _disposed;

        public FaultingStream(Stream inner, long budget, ArchiveOperationException error)
        {
            _inner = inner;
            _remainingBudget = budget;
            _error = error;
        }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => _inner.CanSeek;

        public override bool CanWrite => _inner.CanWrite;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            Consume(count);
            _inner.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Consume(buffer.Length);
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        private void Consume(int count)
        {
            if (_remainingBudget <= 0)
            {
                throw _error;
            }

            _remainingBudget -= count;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                await _inner.DisposeAsync().ConfigureAwait(false);
            }

            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
