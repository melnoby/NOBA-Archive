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
        IFileSystem? fileSystemOverride = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(static b => b.SetMinimumLevel(LogLevel.Critical));
        services.AddNobaArchiveEngine(options =>
        {
            options.Headless = true;
            options.DefaultConflictPolicy = conflictPolicy;
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
