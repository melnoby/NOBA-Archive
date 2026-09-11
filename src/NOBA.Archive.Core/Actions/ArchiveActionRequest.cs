using NOBA.Archive.Core.Formats;

namespace NOBA.Archive.Core.Actions;

/// <summary>Something the product can be asked to do with a set of files.</summary>
public enum ArchiveAction
{
    /// <summary>Show the archive in the application.</summary>
    OpenInApp = 0,

    /// <summary>Verify the archive's integrity.</summary>
    TestArchive,

    /// <summary>Extract into the folder the archive is already in.</summary>
    ExtractHere,

    /// <summary>Extract somewhere the person chooses.</summary>
    ExtractTo,

    /// <summary>Compress the selection straight to a named format, with defaults.</summary>
    CompressTo,

    /// <summary>Open the Create screen with the selection already loaded.</summary>
    CreateArchive,
}

/// <summary>
/// One thing to do, and what to do it to.
/// </summary>
/// <remarks>
/// <para>
/// The boundary between "something asked for an action" and "the engine performs it". Explorer's
/// context menu produces these today. It is deliberately free of any notion of <em>who</em> asked:
/// nothing here names a shell, a window, a command line or a caller, so the same request can be built
/// by a menu, by <c>noba-ar</c>, by NobaOS (ADR-0008), or by a future assistant turning "compress this
/// folder" into an <see cref="ArchiveAction"/> — without any of them gaining a second engine.
/// </para>
/// <para>
/// It carries intent, not permission. Every request still passes the capability gate, the path policy
/// and ResourceGuard exactly as a button press does. Being able to name an action has never been the
/// same as being allowed to perform it, and this type must never become the place that decides.
/// </para>
/// </remarks>
public sealed record ArchiveActionRequest
{
    public required ArchiveAction Action { get; init; }

    /// <summary>
    /// What the action applies to, in the order the caller listed them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Items, not paths, and that is SHELL-075.</b> These were a list of strings, which meant the
    /// one fact Explorer had already supplied — whether a selected thing is a folder — was thrown away
    /// at this boundary. <see cref="ShellLauncher.SuggestedArchivePath"/> then had to guess it back
    /// from the count, "one input, therefore a file", and a folder named <c>Project.Alpha</c> proposed
    /// <c>Project.zip</c>: the extension-stripping rule applied to something that has no extension. Two
    /// folders differing only after a dot collided on one archive name.
    /// </para>
    /// <para>
    /// This is the same shape as D7, which guessed member counts from click counts. Both are a caller
    /// re-deriving something it was already told. The correction is the same in both cases — carry the
    /// fact — and it costs nothing, because <see cref="ShellSelection"/> holds these items already and
    /// simply hands them over.
    /// </para>
    /// </remarks>
    public required IReadOnlyList<ShellItem> Inputs { get; init; }

    /// <summary>Where output goes, when the action fixes it. Null means "the person will choose".</summary>
    public string? Destination { get; init; }

    /// <summary>The format to write. Null for actions that do not create anything.</summary>
    public ArchiveFormat? Format { get; init; }

    /// <summary>The first input's path, which is the archive for every single-archive action.</summary>
    public string? PrimaryInput => Inputs.Count > 0 ? Inputs[0].Path : null;

    /// <summary>Just the paths, for the callers that only ever wanted those.</summary>
    public IReadOnlyList<string> InputPaths => [.. Inputs.Select(static i => i.Path)];
}
