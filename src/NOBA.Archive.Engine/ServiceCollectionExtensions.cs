using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NOBA.Archive.Application.Handlers;
using NOBA.Archive.Application.Operations;
using NOBA.Archive.Application.Policies;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Backends.SevenZip;
using NOBA.Archive.Formats;
using NOBA.Archive.Formats.Managed;
using NOBA.Archive.Infrastructure.FileSystem;
using NOBA.Archive.Infrastructure.Runtime;
using NOBA.Archive.Infrastructure.Settings;
using NOBA.Archive.Security;
using NOBA.Archive.Windows;

namespace NOBA.Archive.Engine;

/// <summary>
/// The single registration call. The desktop app, <c>noba-ar.exe</c>, and NobaOS all go through this,
/// which is what makes "exactly one archive engine" true rather than aspirational (ADR-0009).
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNobaArchiveEngine(
        this IServiceCollection services,
        Action<NobaArchiveEngineOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new NobaArchiveEngineOptions();
        if (configure is not null)
        {
            var builder = new NobaArchiveEngineOptions();
            configure(builder);
            options = builder;
        }

        options.Validate();
        services.TryAddSingleton(options);

        // Runtime
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IAppDataPathProvider>(_ => new AppDataPathProvider(options.AppDataRoot));
        services.TryAddSingleton<IUpdateService, DisabledUpdateService>();

        // Security
        services.TryAddSingleton<IPathPolicy, PathPolicy>();
        services.TryAddSingleton<IResourceGuard, ResourceGuard>();
        services.TryAddSingleton<ISecretRedactor, SecretRedactor>();

        // Infrastructure
        services.TryAddSingleton<IFileSystem, PhysicalFileSystem>();

        // The platform half of the extraction writer. Chosen here rather than left to the host: a host
        // that forgot to register it would silently lose post-open handle verification and Mark of the
        // Web, which is a downgrade no caller should be able to make by omission.
        services.TryAddSingleton<IExtractionPlatform>(static _ => OperatingSystem.IsWindows()
            ? new WindowsExtractionPlatform()
            : new PortableExtractionPlatform());
        services.TryAddSingleton<IExtractionWriter, ExtractionWriter>();
        services.TryAddSingleton<IArchivePublisher, ArchivePublisher>();
        services.TryAddSingleton<ITempWorkspaceFactory, TempWorkspaceFactory>();
        services.TryAddSingleton<SettingsStore>();

        // Formats. Providers are contributed by the format projects from Phase 2 onward; the registry
        // works with none registered, which is what lets Phase 1 validate the contract on its own.
        services.TryAddSingleton<IFormatDetector, MagicByteFormatDetector>();
        services.AddOptions<SevenZipBackendOptions>().Configure<IOptions<NobaArchiveEngineOptions>>(
            static (backend, engine) => backend.LibraryPath = engine.Value.NativeLibraryPath);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IArchiveProvider, ZipArchiveProvider>());

        // TAR and GZIP, both managed and both on the BCL. Registered beside ZIP rather than behind a
        // switch: they need no native library, so unlike 7Z and RAR they are available on every machine
        // this engine runs on, and a capability that is always there should not be conditional.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IArchiveProvider, TarArchiveProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IArchiveProvider, GZipArchiveProvider>());

        // The native backend is optional by construction. 7z.dll may simply not be on the machine -
        // a development box, a trimmed NOBA OS image - and the product must still open ZIPs rather
        // than fail to start. Absence costs 7Z and RAR support and nothing else, and it reports the
        // paths it searched instead of leaving a mystery.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IArchiveProvider, SevenZipBackend>());

        services.TryAddSingleton<IArchiveProviderRegistry>(sp =>
            new ArchiveProviderRegistry(sp.GetServices<IArchiveProvider>()));

        // Application
        services.TryAddSingleton<OperationIdFactory>();
        services.TryAddSingleton<ListArchiveHandler>();
        services.TryAddSingleton<SearchArchiveHandler>();
        services.TryAddSingleton<TestArchiveHandler>();
        services.TryAddSingleton<ExtractArchiveHandler>();
        services.TryAddSingleton<CreateArchiveHandler>();
        services.TryAddSingleton<ManageArchiveHandler>();
        services.TryAddSingleton<DiskSpaceEstimator>();
        services.TryAddSingleton<IConflictResolutionService, ConflictResolutionService>();
        services.TryAddSingleton<IOperationQueue>(sp => new OperationQueue(
            sp.GetRequiredService<OperationIdFactory>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<OperationQueue>>(),
            new OperationQueueOptions { MaxConcurrentOperations = options.MaxConcurrentOperations }));

        if (options.Headless)
        {
            services.TryAddSingleton<IPasswordProvider, FailFastPasswordProvider>();
            services.TryAddSingleton<IConflictPrompt>(_ => new FixedConflictPrompt(options.DefaultConflictPolicy));
        }

        return services;
    }
}
