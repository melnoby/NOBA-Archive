# Drag & Drop — product contract

DROP 06. What dropping something on NOBA Archive does, and what it will never do.

## Scope

Drag and drop is an **input** feature. It adds paths to the Create screen's list. It starts nothing,
copies nothing, and changes no setting.

The Create screen is the only drop target. The Browse and Operations screens do not accept drops.

## The path a drop takes

```
Windows Explorer
  → CreatePage           DataPackageView → StorageItems → IStorageItem.Path
  → CreateViewModel      AcceptDrop(paths) — the whole policy lives here
  → CreateOptionsViewModel   format and capability, unchanged by the drop
  → IArchiveWorkspace    StartCreate, only when Create archive is pressed
  → OperationQueue
  → CreateArchiveHandler
  → provider
```

`CreatePage` resolves platform items to strings and hands them over. Every decision — duplicate,
unreachable, self-referential, not-a-file — is made in `CreateViewModel.AcceptDrop`, which has no
dependency on WinUI and is tested where WinUI cannot be built.

There is no second creation pipeline. A dropped file and a picked file are indistinguishable by the
time the engine sees them.

## Accepted input

| Dropped | Result |
|---|---|
| one file | one input |
| several files | one input each, in the order they arrived |
| one folder | **one** input — the folder itself |
| several folders | one input each |
| files and folders together | every valid item, order preserved |

A folder is never expanded at drop time. `ARC-058` fails the build if any WinUI source enumerates a
directory. Recursion is the engine's job, on its own thread, under ResourceGuard — so dropping a
folder of thirty thousand files costs the same as dropping a folder of three.

Nothing is copied or read. The drop transfers path references only; the files stay where they are.

## Duplicates

The same path is never listed twice, whether it arrived twice in one drop or is already in the list.

Comparison folds case and separators and drops one trailing separator — `PathKey.For`. So these are
one path:

```
C:\Docs\File.txt        c:\docs\file.txt
C:\Projects\A           C:\Projects\A\          C:/Projects/A
```

It deliberately does **not** resolve `..`, junctions, `subst` drives or UNC aliases. Those need the
disk, and this comparison must give the same answer on the machine that runs the tests as on the
machine that runs the product. The engine canonicalises for real, later, where the disk is.

A duplicate is silent when the drop added something else — dragging a folder twice is how people
check the first drag worked. It is reported only when the drop achieved nothing at all.

## Invalid items

One bad item never costs the user the good ones. Each is judged alone.

| Case | Reason reported |
|---|---|
| no filesystem path (virtual file, dragged text) | `DropRejection.NotAFilesystemItem` |
| path gone between drag and drop | `DropRejection.Unreachable` |
| already listed | `DropRejection.AlreadyListed` |
| the archive being created | `DropRejection.SelfTarget` |

One sentence reaches the screen, not four: the most serious cause wins, because that is the one the
user can act on. Windows failing to produce the items at all — a shell extension that threw, a share
that vanished — is caught and reported the same way. No HRESULT reaches the UI.

## The target archive is not its own input

Dropping the archive currently being written is refused with a localized explanation. The rule is
exactly one path — `TargetPath`. A different file in the same folder, or one with a similar name, is
an ordinary input.

## Single-member formats

GZIP holds one file. With more than one input selected and GZIP chosen, Create is blocked with
`CreateBlocker.TooManyInputsForFormat` and the screen says which of the two to change.

This is **not** a GZIP rule and not a drag-and-drop rule. The condition is the absence of
`ArchiveCapabilities.AddEntries`, which is how a single-member container describes itself. The same
line blocks a picker-added second file, unblocks the moment a multi-member format is chosen, and
would cover a future format of the same shape without an edit.

## What a drop never changes

Destination folder, archive name, format, compression profile, solid, encryption, overwrite consent.
Only the input collection moves. `DD-012` and `DD-013` hold this.

Creation happens when **Create archive** is pressed. Never on drop.

## The drop zone

The input list *is* the drop target. There is no separate dashed rectangle — that would be a second
place to put files and would push the list it feeds off the screen.

| State | Border | Glyph | Text |
|---|---|---|---|
| idle | subtle, 1px | folder | Drag files or folders here / اسحب الملفات أو المجلدات هنا |
| valid drag over | focus, 2px | tray | Release to add / أفلت لإضافة العناصر |
| invalid drag over | strong, 2px | refusal | This content cannot be added / لا يمكن إضافة هذا المحتوى |

Colour never carries the meaning alone: the glyph and the sentence change with the border. The hint
is visible whenever the list is empty, and again over a full list while a drag is in progress.

Whether the payload is acceptable is decided synchronously during `DragOver` — the only honest
question answerable without touching the disk is whether it carries filesystem items at all.
Everything else is decided on drop.

## RTL

The zone mirrors with the shell. The layout uses logical alignment only; no `Left` or `Right`
appears in the Create markup. Paths stay left-to-right and are isolated exactly as elsewhere —
`ListView` and the destination and target lines are pinned `LeftToRight`, and technical values go
through `BidiText`. A dropped path is stored byte for byte: not reversed, not reshaped, not
normalized, or the file will not be found.

## Accessibility

The drop zone is an addition, never a replacement. **Add files** and **Add folder** remain, and the
whole Create workflow is reachable from the keyboard. Selecting a list row keeps **Remove** enabled,
and the selection survives the list being rebuilt when an input is added.

## Threading

`DragOver` is synchronous and cheap. `Drop` takes a deferral before its first `await` — `DataView`
is torn down when the handler returns, which is at the first `await`, so without the deferral the
collection is read after Windows has reclaimed it. Items are materialized into a list before being
handed to the view model. Nothing blocks the dispatcher; there is no sync-over-async.

## Test coverage

`tests/NOBA.Archive.Presentation.Tests/DragDropTests.cs` — DD-001…DD-023. No timing-based tests.
The filesystem is a constructor parameter, so "the user deleted it mid-drag" is reachable at all.

`ARC-058` / `ARC-059` — no WinUI source walks or copies the filesystem, and the rule is proven to
fire.

Windows drag-and-drop itself is platform behaviour and is verified by hand:
`scripts/dragdrop-gate.ps1`.

## Known limitations

- **L24** — `PathKey` compares text, not identity. Two paths reaching the same file through a
  junction, a `subst` drive or a UNC alias are listed twice, and the archive will contain the
  contents twice. Resolving that needs the disk during the drop, which the drop budget does not
  allow.
- **L25** — Drop is Create-only. Dropping an archive onto Browse to open it is not implemented.
- **L26** — There is no drag *out* of NOBA Archive; entries cannot be dragged to Explorer.
- **L27** — Whether a dropped item is acceptable is not known until the drop completes, so the
  cursor shows "copy" over items that will then be refused by name. Deciding earlier would mean
  touching the disk on every pointer move.
