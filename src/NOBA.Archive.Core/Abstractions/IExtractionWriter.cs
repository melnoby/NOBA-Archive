using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Primitives;

namespace NOBA.Archive.Core.Abstractions;

public enum EntryWriteOutcome
{
    Written = 0,
    Skipped,
    Blocked,
    Renamed,
    Overwritten,
}

public sealed record EntryWriteResult(
    EntryWriteOutcome Outcome,
    string? DestinationPath,
    PathRejection? Rejection,
    ByteSize BytesWritten)
{
    public static EntryWriteResult Blocked(PathRejection rejection) =>
        new(EntryWriteOutcome.Blocked, null, rejection, ByteSize.Zero);

    public static EntryWriteResult Skipped(string destinationPath) =>
        new(EntryWriteOutcome.Skipped, destinationPath, null, ByteSize.Zero);
}

/// <summary>
/// The only component in NOBA Archive that writes to an extraction destination (ADR-0005).
/// </summary>
/// <remarks>
/// Implemented in NOBA.Archive.Infrastructure. It validates the path, creates directories one
/// component at a time, opens the handle, re-verifies the opened handle still resolves inside the
/// destination, streams the bytes a provider decodes, and publishes atomically. Concentrating this in
/// one place is what removes the traversal/symlink CVE class even when a backend has the bug.
/// </remarks>
/// <summary>
/// One entry mid-write: the destination is chosen, the temporary file is open, and the guarded stream
/// is waiting for bytes.
/// </summary>
/// <remarks>
/// <para>
/// This exists because control is inverted for a native backend. 7-Zip drives its own extraction loop
/// and calls <c>GetStream</c> to ask where entry N should go, so there is no point at which we can
/// hand it a callback that fills a stream we opened — it is the other way round. Splitting the writer
/// into begin and complete lets that loop drive it without the writer surrendering an inch: the
/// destination is still resolved here, the handle is still re-verified here, and publication is still
/// the writer's decision alone (ADR-0005).
/// </para>
/// <para>
/// <see cref="IExtractionWriter.WriteEntryAsync"/> is now a thin wrapper over this pair, so the
/// per-entry and batch paths cannot drift apart — there is one implementation of the rules, used two
/// ways.
/// </para>
/// </remarks>
public interface IEntryWriteSession : IAsyncDisposable
{
    /// <summary>The stream to fill. Null when <see cref="Refusal"/> is set.</summary>
    Stream? Content { get; }

    /// <summary>
    /// Set when the entry never reached the point of being written — refused by policy, or skipped by
    /// conflict resolution. <see cref="Content"/> is null and <see cref="CompleteAsync"/> must not be
    /// called.
    /// </summary>
    EntryWriteResult? Refusal { get; }

    /// <summary>
    /// Verifies the final size, then publishes the temporary file atomically and applies metadata.
    /// </summary>
    ValueTask<EntryWriteResult> CompleteAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Discards the entry. The temporary file is removed and the destination name is never created —
    /// which is the whole reason a failed extraction cannot leave a half-written file behind.
    /// </summary>
    ValueTask AbandonAsync();
}

public interface IExtractionWriter
{
    /// <summary>
    /// Opens one entry for writing. The caller must call <see cref="IEntryWriteSession.CompleteAsync"/>
    /// on success or <see cref="IEntryWriteSession.AbandonAsync"/> otherwise; disposing without either
    /// abandons.
    /// </summary>
    ValueTask<IEntryWriteSession> BeginEntryAsync(
        ArchiveEntry entry,
        ExtractionOptions options,
        CancellationToken cancellationToken);

    ValueTask<EntryWriteResult> WriteEntryAsync(
        ArchiveEntry entry,
        ExtractionOptions options,
        Func<Stream, CancellationToken, ValueTask> decodeInto,
        CancellationToken cancellationToken);

    ValueTask<EntryWriteResult> CreateDirectoryAsync(
        ArchiveEntry entry,
        ExtractionOptions options,
        CancellationToken cancellationToken);
}
