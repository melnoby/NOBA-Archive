using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NOBA.Archive.Application.Handlers;
using NOBA.Archive.Application.Operations;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Primitives;
using NOBA.Archive.Core.Operations;
using NOBA.Archive.Engine;

namespace NOBA.Archive.Integration.Tests;

/// <summary>
/// One engine, composed exactly the way an embedding host composes it.
/// </summary>
/// <remarks>
/// These tests go through <see cref="ServiceCollectionExtensions.AddNobaArchiveEngine"/> rather than
/// newing up handlers, because half of what SEC-### proves is that the guards are <em>wired in</em>.
/// A test that constructed <c>ExtractArchiveHandler</c> by hand could pass while the shipping
/// registration silently omitted the path policy.
/// </remarks>
internal sealed class EngineFixture : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    public EngineFixture(ConflictPolicy conflictPolicy = ConflictPolicy.Fail)
        : this(null, conflictPolicy)
    {
    }

    /// <summary>A fixture that supplies one password, for the encrypted-archive tests.</summary>
    public EngineFixture(
        string? password,
        ConflictPolicy conflictPolicy = ConflictPolicy.Fail,
        IFileSystem? fileSystemOverride = null,
        int maxConcurrentOperations = 1)
    {
        var services = new ServiceCollection();
        services.AddLogging(static b => b.SetMinimumLevel(LogLevel.Critical));
        services.AddNobaArchiveEngine(options =>
        {
            options.Headless = true;
            options.DefaultConflictPolicy = conflictPolicy;

            // DROP 09: only the concurrency tests pass more than one — every other fixture keeps the
            // shipped default (ARCHIVE_PLAN.md's "compression already saturates every core"), so this
            // parameter cannot change what every other integration test is actually proving.
            options.MaxConcurrentOperations = maxConcurrentOperations;
        });

        if (password is not null)
        {
            services.AddSingleton<IPasswordProvider>(new FixedPasswordProvider(password));
        }

        if (fileSystemOverride is not null)
        {
            // AddNobaArchiveEngine registers the real filesystem with TryAddSingleton, so this
            // later registration is what the container actually resolves — a host-style decoration,
            // not a bypass of the production wiring. Used to inject disk-full / permission-denied
            // faults at the one seam (IFileSystem) real I/O failures would actually arrive through.
            services.AddSingleton(fileSystemOverride);
        }

        _services = services.BuildServiceProvider();
    }

    /// <summary>
    /// Hands out the same password every time it is asked.
    /// </summary>
    /// <remarks>
    /// A fresh <see cref="Password"/> per call on purpose: the contract says the caller owns and
    /// disposes what it receives, and handing out one shared instance would have the first disposal
    /// wipe the buffer the next caller is about to read.
    /// </remarks>
    private sealed class FixedPasswordProvider : IPasswordProvider
    {
        private readonly string _password;

        public FixedPasswordProvider(string password) => _password = password;

        public ValueTask<Password?> GetPasswordAsync(PasswordRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Password?>(Password.FromChars(_password));
    }

    public T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    public ValueTask<ExtractOutcome> ExtractAsync(
        string archivePath,
        string destination,
        Action<ExtractionOptionsBuilder>? configure = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var builder = new ExtractionOptionsBuilder(destination);
        configure?.Invoke(builder);

        var id = Get<OperationIdFactory>().Next(OperationType.Extract);
        return new ValueTask<ExtractOutcome>(Get<ExtractArchiveHandler>().HandleAsync(
            new ExtractRequest { ArchivePath = archivePath, Options = builder.Build() },
            id,
            progress,
            null,
            cancellationToken));
    }

    public ValueTask<ListOutcome> ListAsync(string archivePath, CancellationToken cancellationToken = default) =>
        new(Get<ListArchiveHandler>().HandleAsync(new ListRequest { ArchivePath = archivePath }, cancellationToken));

    public ValueTask<SearchOutcome> SearchAsync(
        string archivePath,
        Action<SearchRequestBuilder>? configure = null,
        CancellationToken cancellationToken = default)
    {
        var builder = new SearchRequestBuilder(archivePath);
        configure?.Invoke(builder);
        return new(Get<SearchArchiveHandler>().HandleAsync(builder.Build(), cancellationToken));
    }

    public IAsyncEnumerable<ArchiveEntry> StreamSearchAsync(
        string archivePath,
        Action<SearchRequestBuilder>? configure = null,
        CancellationToken cancellationToken = default)
    {
        var builder = new SearchRequestBuilder(archivePath);
        configure?.Invoke(builder);
        return Get<SearchArchiveHandler>().StreamAsync(builder.Build(), cancellationToken);
    }

    public ValueTask<ArchiveTestResult> TestAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        var id = Get<OperationIdFactory>().Next(OperationType.Test);
        return new ValueTask<ArchiveTestResult>(Get<TestArchiveHandler>().HandleAsync(
            new TestRequest { ArchivePath = archivePath },
            id,
            null,
            cancellationToken));
    }

    public ValueTask<CreateOutcome> CreateAsync(
        string targetPath,
        IReadOnlyList<string> sources,
        CompressionOptions? options = null,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var id = Get<OperationIdFactory>().Next(OperationType.Create);
        return new ValueTask<CreateOutcome>(Get<CreateArchiveHandler>().HandleAsync(
            new CreateRequest
            {
                TargetPath = targetPath,
                Sources = sources,
                Options = options ?? CompressionOptions.ForProfile(ArchiveFormat.Zip, CompressionProfile.Balanced),
                Overwrite = overwrite,
            },
            id,
            null,
            null,
            cancellationToken));
    }

    public ValueTask<ManageOutcome> ManageAsync(
        string archivePath,
        CompressionOptions? options = null,
        IReadOnlyList<ArchivePath>? remove = null,
        IReadOnlyList<EntryRename>? rename = null,
        IReadOnlyList<string>? add = null,
        ArchiveFormat? forceFormat = null,
        CancellationToken cancellationToken = default)
    {
        var id = Get<OperationIdFactory>().Next(OperationType.Add);
        return new ValueTask<ManageOutcome>(Get<ManageArchiveHandler>().HandleAsync(
            new ManageRequest
            {
                ArchivePath = archivePath,
                ForceFormat = forceFormat,
                Options = options ?? CompressionOptions.ForProfile(ArchiveFormat.Zip, CompressionProfile.Balanced),
                EntriesToRemove = remove ?? [],
                EntriesToRename = rename ?? [],
                SourcesToAdd = add ?? [],
            },
            id,
            null,
            null,
            cancellationToken));
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();
}

internal sealed class ExtractionOptionsBuilder
{
    private ExtractionOptions _options;

    public ExtractionOptionsBuilder(string destination) =>
        _options = new ExtractionOptions
        {
            DestinationRoot = destination,
            ConflictPolicy = ConflictPolicy.Fail,
            // Off by default in tests: propagation reads an alternate data stream from the source
            // archive, which is a Windows-only concern with its own dedicated coverage.
            PropagateMarkOfTheWeb = false,
        };

    public ExtractionOptionsBuilder With(Func<ExtractionOptions, ExtractionOptions> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        _options = mutate(_options);
        return this;
    }

    public ExtractionOptions Build() => _options;
}

/// <summary>A small fluent builder so a search test reads as "what it's asking for", not record syntax.</summary>
internal sealed class SearchRequestBuilder
{
    private SearchRequest _request;

    public SearchRequestBuilder(string archivePath) => _request = new SearchRequest { ArchivePath = archivePath };

    public SearchRequestBuilder NameContains(string text)
    {
        _request = _request with { NameContains = text };
        return this;
    }

    public SearchRequestBuilder Extensions(params string[] extensions)
    {
        _request = _request with { Extensions = extensions };
        return this;
    }

    public SearchRequestBuilder Kind(EntryKind kind)
    {
        _request = _request with { Kind = kind };
        return this;
    }

    public SearchRequestBuilder SizeBetween(long? minBytes, long? maxBytes)
    {
        _request = _request with
        {
            MinSize = minBytes is { } min ? ByteSize.FromBytes(min) : null,
            MaxSize = maxBytes is { } max ? ByteSize.FromBytes(max) : null,
        };
        return this;
    }

    public SearchRequestBuilder ModifiedBetween(DateTimeOffset? after, DateTimeOffset? before)
    {
        _request = _request with { ModifiedAfter = after, ModifiedBefore = before };
        return this;
    }

    public SearchRequestBuilder SortedBy(SearchSortKey key, bool descending = false)
    {
        _request = _request with { SortBy = key, Descending = descending };
        return this;
    }

    public SearchRequest Build() => _request;
}
