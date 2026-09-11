using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Primitives;
using NOBA.Archive.Presentation.Bidi;
using NOBA.Archive.Presentation.Interaction;
using NOBA.Archive.Presentation.ViewModels;
using Xunit;

namespace NOBA.Archive.Presentation.Tests;

/// <summary>
/// DROP 06 — what a drop does to the create screen's inputs.
/// </summary>
/// <remarks>
/// <para>
/// These are the contract, not the gesture. Whether Windows raises <c>DragOver</c> before <c>Drop</c>,
/// and whether a <c>StorageFolder</c> resolves to a path with a trailing separator, is Windows'
/// business and is checked by hand on Windows. What is checked here is everything that happens after
/// the platform has handed over a list of paths — which is where every rule in the feature lives.
/// </para>
/// <para>
/// The filesystem is a parameter, not an ambient fact. A test that had to create real files to prove
/// a deleted file is refused would be creating and deleting files to simulate a race it cannot
/// actually lose, and would be slower and less certain for it.
/// </para>
/// </remarks>
public sealed class DragDropTests
{
    /// <summary>Everything exists, which is the ordinary case.</summary>
    private static CreateViewModel Screen(FakeWorkspace? workspace = null, Func<string, bool>? exists = null) =>
        new(workspace ?? new FakeWorkspace(), operations: null, exists: exists ?? (static _ => true))
        {
            Options = { Format = ArchiveFormat.SevenZip },
        };

    /// <summary>A build where GZIP is creatable but single-member, exactly as DROP 05 reports it.</summary>
    private static FakeWorkspace WithGZip() => new(new Dictionary<ArchiveFormat, ArchiveCapabilities>
    {
        [ArchiveFormat.SevenZip] = ArchiveCapabilities.ReadOnlySet
            | ArchiveCapabilities.Create
            | ArchiveCapabilities.AddEntries,

        // No AddEntries. This is not a stand-in for GZIP; it is what the real provider advertises.
        [ArchiveFormat.GZip] = ArchiveCapabilities.ReadOnlySet | ArchiveCapabilities.Create,
    });

    /// <summary>DD-001: one file becomes one input.</summary>
    [Fact]
    public void A_single_file_drop_adds_one_input()
    {
        var model = Screen();

        var outcome = model.AcceptDrop([@"C:\Docs\report.pdf"]);

        Assert.Equal(1, outcome.Added);
        Assert.False(outcome.HasRejections);
        Assert.Equal(@"C:\Docs\report.pdf", string.Join(" | ", model.Inputs));
    }

    /// <summary>DD-002: every file in a multi-file drop is added, in the order it arrived.</summary>
    [Fact]
    public void A_multiple_file_drop_adds_all_of_them()
    {
        var model = Screen();

        var outcome = model.AcceptDrop([@"C:\Docs\report.pdf", @"C:\Docs\photo.jpg", @"C:\Docs\data.xlsx"]);

        Assert.Equal(3, outcome.Added);
        Assert.Equal(
            @"C:\Docs\report.pdf | C:\Docs\photo.jpg | C:\Docs\data.xlsx",
            string.Join(" | ", model.Inputs));
    }

    /// <summary>
    /// DD-003: a folder is one input.
    /// </summary>
    /// <remarks>
    /// The point of the assertion is the count. A folder that arrived as its own thousands of entries
    /// would also "work", and would have read the whole tree on the UI thread to do it.
    /// </remarks>
    [Fact]
    public void A_folder_drop_adds_one_input_and_does_not_expand_it()
    {
        var model = Screen();

        var outcome = model.AcceptDrop([@"C:\Projects\ProjectA"]);

        Assert.Equal(1, outcome.Added);
        Assert.Single(model.Inputs);
        Assert.Equal(@"C:\Projects\ProjectA", model.Inputs[0]);
    }

    /// <summary>DD-004: several folders are several inputs.</summary>
    [Fact]
    public void Multiple_folders_all_arrive()
    {
        var model = Screen();

        model.AcceptDrop([@"C:\Projects\ProjectA", @"C:\Projects\ProjectB"]);

        Assert.Equal(@"C:\Projects\ProjectA | C:\Projects\ProjectB", string.Join(" | ", model.Inputs));
    }

    /// <summary>DD-005: a drop may mix the two, and nothing reorders it.</summary>
    [Fact]
    public void Files_and_folders_can_arrive_together()
    {
        var model = Screen();

        var outcome = model.AcceptDrop([@"C:\Docs\report.pdf", @"C:\Projects\ProjectA", @"C:\Images\logo.png"]);

        Assert.Equal(3, outcome.Added);
        Assert.Equal(
            @"C:\Docs\report.pdf | C:\Projects\ProjectA | C:\Images\logo.png",
            string.Join(" | ", model.Inputs));
    }

    /// <summary>DD-006: the same path twice in one drop is one entry.</summary>
    [Fact]
    public void A_path_dropped_twice_in_one_gesture_is_added_once()
    {
        var model = Screen();

        var outcome = model.AcceptDrop([@"C:\Docs\report.pdf", @"C:\Docs\report.pdf"]);

        Assert.Equal(1, outcome.Added);
        Assert.Single(model.Inputs);
        Assert.Contains(outcome.Rejected, r => r.Reason == DropRejection.AlreadyListed);
    }

    /// <summary>
    /// DD-007: dropping something already listed changes nothing, however it is spelled.
    /// </summary>
    /// <remarks>
    /// Windows paths are case-insensitive and a folder may or may not arrive with its trailing
    /// separator. Treating those as different files is how a folder ends up in an archive twice.
    /// </remarks>
    [Fact]
    public void A_path_already_in_the_list_is_not_added_again()
    {
        var model = Screen();
        model.AddInputs([@"C:\Docs\File.txt", @"C:\Projects\ProjectA"]);

        var outcome = model.AcceptDrop([@"c:\docs\file.txt", @"C:\Projects\ProjectA\"]);

        Assert.Equal(0, outcome.Added);
        Assert.Equal(2, model.Inputs.Count);
        Assert.Equal(@"C:\Docs\File.txt | C:\Projects\ProjectA", string.Join(" | ", model.Inputs));
    }

    /// <summary>DD-008: an item with no filesystem path is refused, and nothing throws.</summary>
    [Fact]
    public void An_item_that_is_not_a_file_is_refused_without_throwing()
    {
        var model = Screen();

        var outcome = model.AcceptDrop([null, string.Empty, "   "]);

        Assert.Equal(0, outcome.Added);
        Assert.Empty(model.Inputs);
        Assert.Equal(3, outcome.Rejected.Count);
        Assert.Contains(outcome.Rejected, r => r.Reason == DropRejection.NotAFilesystemItem);
    }

    /// <summary>DD-009: an empty payload does nothing at all.</summary>
    [Fact]
    public void An_empty_drop_changes_nothing()
    {
        var model = Screen();
        model.AddInputs([@"C:\Docs\report.pdf"]);

        var outcome = model.AcceptDrop([]);

        Assert.Equal(0, outcome.Added);
        Assert.False(outcome.HasRejections);
        Assert.Single(model.Inputs);
    }

    /// <summary>
    /// DD-010: an Arabic path is stored byte for byte.
    /// </summary>
    /// <remarks>
    /// Not reversed, not reshaped, not normalised. The screen isolates it for display; the value the
    /// engine receives is the value Windows gave, or the file will not be found.
    /// </remarks>
    [Fact]
    public void An_Arabic_path_survives_unchanged()
    {
        const string arabic = @"C:\Users\م\مستندات\تقرير سنوي.txt";
        var model = Screen();

        model.AcceptDrop([arabic]);

        Assert.Equal(arabic, model.Inputs[0]);
        Assert.Equal(arabic.Length, model.Inputs[0].Length);
    }

    /// <summary>DD-011: so does every other script.</summary>
    [Fact]
    public void A_Unicode_path_survives_unchanged()
    {
        const string japanese = @"C:\データ\日本語のファイル.txt";
        const string greek = @"C:\δεδομένα\Ελληνικά.txt";
        var model = Screen();

        model.AcceptDrop([japanese, greek]);

        Assert.Equal(japanese + " | " + greek, string.Join(" | ", model.Inputs));
    }

    /// <summary>DD-012: a drop adds inputs and touches nothing about the destination.</summary>
    [Fact]
    public void Dropping_does_not_change_the_destination()
    {
        var model = Screen();
        model.DestinationFolder = @"D:\Archives";
        model.ArchiveName = "backup";

        model.AcceptDrop([@"C:\Docs\report.pdf", @"C:\Projects\ProjectA"]);

        Assert.Equal(@"D:\Archives", model.DestinationFolder);
        Assert.Equal("backup", model.ArchiveName);
        Assert.Equal(Path.Combine(@"D:\Archives", "backup.7z"), model.TargetPath);
    }

    /// <summary>DD-013: nor the format, the profile, or the overwrite consent.</summary>
    [Fact]
    public void Dropping_does_not_change_the_selected_format_or_options()
    {
        var model = Screen();
        model.Options.Format = ArchiveFormat.Zip;
        model.Options.Profile = CompressionProfile.MaximumCompression;

        model.AcceptDrop([@"C:\Docs\report.pdf"]);

        Assert.Equal(ArchiveFormat.Zip, model.Options.Format);
        Assert.Equal(CompressionProfile.MaximumCompression, model.Options.Profile);
        Assert.False(model.Overwrite);
    }

    /// <summary>
    /// DD-014: two files and a single-member format is a refusal, and it says which.
    /// </summary>
    /// <remarks>
    /// The blocker is the capability, not the format's name — so this also proves the same two files
    /// are fine the moment a multi-member format is chosen, without a second rule being consulted.
    /// </remarks>
    [Fact]
    public void A_single_member_format_refuses_a_second_input()
    {
        var model = new CreateViewModel(WithGZip(), operations: null, exists: static _ => true);
        model.Options.Format = ArchiveFormat.GZip;
        model.DestinationFolder = @"D:\Archives";
        model.ArchiveName = "backup";

        model.AcceptDrop([@"C:\Docs\one.txt"]);
        Assert.True(model.CanCreate);

        model.AcceptDrop([@"C:\Docs\two.txt"]);

        Assert.False(model.CanCreate);
        Assert.Equal(CreateBlocker.TooManyInputsForFormat, model.Blocker);

        model.Options.Format = ArchiveFormat.SevenZip;
        Assert.True(model.CanCreate);
        Assert.Equal(CreateBlocker.None, model.Blocker);
    }

    /// <summary>DD-015: a drop never starts an operation. Only the button does.</summary>
    [Fact]
    public void Dropping_does_not_start_a_creation()
    {
        var workspace = new FakeWorkspace();
        var model = new CreateViewModel(workspace, operations: null, exists: static _ => true);
        model.Options.Format = ArchiveFormat.SevenZip;
        model.DestinationFolder = @"D:\Archives";
        model.ArchiveName = "backup";

        model.AcceptDrop([@"C:\Docs\report.pdf", @"C:\Projects\ProjectA"]);

        Assert.Empty(workspace.Started);
        Assert.Equal(CreateStatus.Idle, model.Status);
        Assert.False(model.IsBusy);
        Assert.True(model.CanCreate);
    }

    /// <summary>DD-016: Remove still removes a dropped item.</summary>
    [Fact]
    public void Remove_works_on_a_dropped_input()
    {
        var model = Screen();
        model.AcceptDrop([@"C:\Docs\report.pdf", @"C:\Docs\photo.jpg"]);

        Assert.True(model.RemoveInput(@"C:\Docs\report.pdf"));

        Assert.Single(model.Inputs);
        Assert.Equal(@"C:\Docs\photo.jpg", model.Inputs[0]);
    }

    /// <summary>DD-017: Clear empties a list that a drop filled.</summary>
    [Fact]
    public void Clear_empties_a_dropped_list()
    {
        var model = Screen();
        model.AcceptDrop([@"C:\Docs\report.pdf", @"C:\Projects\ProjectA"]);

        model.ClearInputs();

        Assert.Empty(model.Inputs);
        Assert.False(model.HasInputs);
    }

    /// <summary>
    /// DD-018: the archive being written cannot be one of its own inputs.
    /// </summary>
    /// <remarks>
    /// And the rule stays where it belongs: a different file in the same folder, and a file whose name
    /// merely resembles the target, are ordinary inputs.
    /// </remarks>
    [Fact]
    public void The_target_archive_is_refused_as_its_own_input()
    {
        var model = Screen();
        model.DestinationFolder = @"D:\Archives";
        model.ArchiveName = "backup";

        var outcome = model.AcceptDrop([Path.Combine(@"D:\Archives", "backup.7z")]);

        Assert.Equal(0, outcome.Added);
        Assert.Empty(model.Inputs);
        Assert.Contains(outcome.Rejected, r => r.Reason == DropRejection.SelfTarget);
        Assert.Equal(
            LocalizedStrings.For(UiLanguage.English).DropSelfTarget,
            outcome.ReasonFor(LocalizedStrings.For(UiLanguage.English)));

        var neighbours = model.AcceptDrop([
            Path.Combine(@"D:\Archives", "backup2.7z"),
            Path.Combine(@"D:\Archives", "notes.txt")]);

        Assert.Equal(2, neighbours.Added);
    }

    /// <summary>DD-019: a path that vanished between the drag and the drop is refused, not crashed on.</summary>
    [Fact]
    public void A_path_that_no_longer_exists_is_refused_safely()
    {
        var model = Screen(exists: static path => !path.Contains("deleted", StringComparison.Ordinal));

        var outcome = model.AcceptDrop([@"C:\Docs\deleted.txt"]);

        Assert.Equal(0, outcome.Added);
        Assert.Empty(model.Inputs);
        Assert.Contains(outcome.Rejected, r => r.Reason == DropRejection.Unreachable);
        Assert.Equal(
            LocalizedStrings.For(UiLanguage.Arabic).DropUnreachable,
            outcome.ReasonFor(LocalizedStrings.For(UiLanguage.Arabic)));
    }

    /// <summary>
    /// DD-020: one bad item does not cost the user the good ones.
    /// </summary>
    /// <remarks>
    /// This is the whole reason each item is judged alone. Explorer selections routinely contain a
    /// shortcut to a disconnected drive; rejecting the drop wholesale would make the feature unusable
    /// for exactly the people with the most files.
    /// </remarks>
    [Fact]
    public void A_mixed_drop_keeps_every_valid_item()
    {
        var model = Screen(exists: static path => !path.Contains("gone", StringComparison.Ordinal));
        model.DestinationFolder = @"D:\Archives";
        model.ArchiveName = "backup";

        var outcome = model.AcceptDrop([
            @"C:\Docs\valid.pdf",
            null,
            @"C:\Docs\gone.txt",
            @"C:\Projects\valid-folder",
            Path.Combine(@"D:\Archives", "backup.7z"),
            @"C:\Docs\valid.pdf",
        ]);

        Assert.Equal(2, outcome.Added);
        Assert.Equal(@"C:\Docs\valid.pdf | C:\Projects\valid-folder", string.Join(" | ", model.Inputs));

        Assert.Equal(4, outcome.Rejected.Count);
        Assert.Contains(outcome.Rejected, r => r.Reason == DropRejection.NotAFilesystemItem);
        Assert.Contains(outcome.Rejected, r => r.Reason == DropRejection.Unreachable);
        Assert.Contains(outcome.Rejected, r => r.Reason == DropRejection.SelfTarget);
        Assert.Contains(outcome.Rejected, r => r.Reason == DropRejection.AlreadyListed);

        // The most serious cause is what the user is told, not four sentences.
        Assert.Equal(
            LocalizedStrings.For(UiLanguage.English).DropSelfTarget,
            outcome.ReasonFor(LocalizedStrings.For(UiLanguage.English)));
    }

    /// <summary>DD-021: a plain duplicate is silent when the drop still achieved something.</summary>
    [Fact]
    public void A_duplicate_alongside_a_new_item_is_not_worth_a_message()
    {
        var model = Screen();
        model.AddInputs([@"C:\Docs\report.pdf"]);

        var outcome = model.AcceptDrop([@"C:\Docs\report.pdf", @"C:\Docs\photo.jpg"]);

        Assert.Equal(1, outcome.Added);
        Assert.Null(outcome.ReasonFor(LocalizedStrings.For(UiLanguage.English)));
    }

    /// <summary>DD-022: both languages define every drop string.</summary>
    [Fact]
    public void Both_languages_have_the_drop_text()
    {
        var english = LocalizedStrings.For(UiLanguage.English);
        var arabic = LocalizedStrings.For(UiLanguage.Arabic);

        foreach (var pair in new[]
        {
            (english.DropIdle, arabic.DropIdle),
            (english.DropActive, arabic.DropActive),
            (english.DropInvalid, arabic.DropInvalid),
            (english.DropSelfTarget, arabic.DropSelfTarget),
            (english.DropUnreachable, arabic.DropUnreachable),
            (english.DropNotAFile, arabic.DropNotAFile),
            (english.DropAlreadyListed, arabic.DropAlreadyListed),
            (english.CreateSingleMemberFormat, arabic.CreateSingleMemberFormat),
        })
        {
            Assert.False(string.IsNullOrWhiteSpace(pair.Item1));
            Assert.False(string.IsNullOrWhiteSpace(pair.Item2));

            // An unresolved key returns the key itself — which is ASCII, so it cannot pass this.
            Assert.True(BidiText.ContainsRightToLeft(pair.Item2), $"'{pair.Item2}' is not Arabic");
            Assert.NotEqual(pair.Item1, pair.Item2);
        }

        Assert.True(BidiText.ContainsRightToLeft(arabic.DropIdle));
    }

    /// <summary>DD-023: the comparison key folds what Windows folds, and nothing more.</summary>
    [Fact]
    public void The_path_key_folds_case_and_separators_only()
    {
        Assert.True(PathKey.Same(@"C:\Docs\File.txt", @"c:\docs\file.txt"));
        Assert.True(PathKey.Same(@"C:\Projects\A", @"C:\Projects\A\"));
        Assert.True(PathKey.Same(@"C:\Projects\A", @"C:/Projects/A"));

        Assert.False(PathKey.Same(@"C:\Docs\a.txt", @"C:\Docs\b.txt"));
        Assert.False(PathKey.Same(@"C:\Docs", @"D:\Docs"));
        Assert.False(PathKey.Same(null, null));
        Assert.False(PathKey.Same(@"C:\Docs", null));

        // A drive root keeps its separator rather than collapsing to "C:".
        Assert.Equal(@"C:\", PathKey.For(@"C:\"));
    }
}
