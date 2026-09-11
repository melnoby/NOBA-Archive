using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Operations;
using NOBA.Archive.Core.Primitives;

namespace NOBA.Archive.TestKit.Fakes;

/// <summary>
/// A provider that decodes from an in-memory table. It exists so the provider and registry contracts
/// can be exercised before any real backend lands - and, importantly, so the "providers never touch
/// the filesystem" rule is testable: this type has no filesystem access at all.
/// </summary>
public sealed class FakeArchiveProvider : IArchiveProvider
{
    private readonly Dictionary<string, byte[]> _content;
    private readonly ArchiveCapabilities _capabilities;

    public FakeArchiveProvider(
        ArchiveFormat format,
        ArchiveCapabilities capabilities,
        IReadOnlyDictionary<string, byte[]>? content = null,
        int priority = 0,
        string providerId = "fake")
    {
        ArgumentNullException.ThrowIfNull(format);
        Format = format;
        _capabilities = capabilities;
        _content = content is null
            ? []
            : new Dictionary<string, byte[]>(content, StringComparer.Ordinal);
        Priority = priority;
        ProviderId = providerId;
    }

    public ArchiveFormat Format { get; }

    public int Priority { get; }

    public string ProviderId { get; }

    public IReadOnlyList<ArchiveFormat> SupportedFormats => [Format];

    public ArchiveCapabilities GetCapabilities(ArchiveFormat format) =>
        format == Format ? _capabilities : ArchiveCapabilities.None;

    public int PriorityFor(ArchiveFormat format) => format == Format ? Priority : int.MinValue;

    public ValueTask<FormatDetection> DetectAsync(Stream probe, string? fileName, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new FormatDetection(Format, DetectionSource.Content, 100));

    public ValueTask<IArchiveSession> OpenAsync(
        ArchiveSource source,
        OpenOptions options,
        IPasswordProvider passwords,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<IArchiveSession>(new FakeSession(Format, _capabilities, _content));

    public ValueTask<IArchiveBuilder> CreateAsync(
        Stream output,
        CompressionOptions options,
        IPasswordProvider passwords,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<IArchiveBuilder>(new FakeBuilder(Format, _capabilities));

    private sealed class FakeSession : IArchiveSession
    {
        private readonly Dictionary<string, byte[]> _content;

        public FakeSession(ArchiveFormat format, ArchiveCapabilities capabilities, Dictionary<string, byte[]> content)
        {
            Format = format;
            Capabilities = capabilities;
            _content = content;
            Statistics = new ArchiveStatistics
            {
                EntryCount = content.Count,
                FileCount = content.Count,
                TotalUncompressedSize = ByteSize.FromBytes(content.Values.Sum(static v => (long)v.Length)),
            };
        }

        public ArchiveFormat Format { get; }

        public ArchiveCapabilities Capabilities { get; }

        public ArchiveStatistics Statistics { get; }

        public bool SupportsPause => true;

        public async IAsyncEnumerable<ArchiveEntry> ListEntriesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var (raw, bytes) in _content)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();

                // Hostile paths stay listable: they are surfaced with a rejection instead of throwing.
                if (ArchivePath.TryCreate(raw, out var path, out var rejection))
                {
                    yield return ArchiveEntry.ForAcceptedPath(raw, path, EntryKind.File) with
                    {
                        UncompressedSize = ByteSize.FromBytes(bytes.Length),
                        CompressedSize = ByteSize.FromBytes(bytes.Length),
                    };
                }
                else
                {
                    yield return ArchiveEntry.ForRejectedPath(raw, rejection!, EntryKind.File);
                }
            }
        }

        public async ValueTask ExtractToAsync(
            ArchiveEntry entry,
            Stream destination,
            IProgress<long>? bytesProgress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentNullException.ThrowIfNull(destination);

            if (!_content.TryGetValue(entry.RawPath, out var bytes))
            {
                return;
            }

            await destination.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            bytesProgress?.Report(bytes.Length);
        }

        public ValueTask<ArchiveTestResult> TestAsync(
            IProgress<OperationProgress>? progress,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ArchiveTestResult.Valid(_content.Count));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeBuilder : IArchiveBuilder
    {
        public FakeBuilder(ArchiveFormat format, ArchiveCapabilities capabilities)
        {
            Format = format;
            Capabilities = capabilities;
        }

        public ArchiveFormat Format { get; }

        public ArchiveCapabilities Capabilities { get; }

        public Dictionary<string, byte[]> Written { get; } = new(StringComparer.Ordinal);

        public bool Completed { get; private set; }

        public async ValueTask AddAsync(
            ArchivePath entryPath,
            EntryContentSource content,
            DateTimeOffset? modified,
            IProgress<long>? bytesProgress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(content);
            using var buffer = new MemoryStream();
            await using (var source = await content.OpenAsync(cancellationToken).ConfigureAwait(false))
            {
                await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            }

            Written[entryPath.Value] = buffer.ToArray();
            bytesProgress?.Report(buffer.Length);
        }

        public ValueTask AddDirectoryAsync(ArchivePath entryPath, DateTimeOffset? modified, CancellationToken cancellationToken)
        {
            Written[entryPath.Value + "/"] = [];
            return ValueTask.CompletedTask;
        }

        public ValueTask CompleteAsync(CancellationToken cancellationToken)
        {
            Completed = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
