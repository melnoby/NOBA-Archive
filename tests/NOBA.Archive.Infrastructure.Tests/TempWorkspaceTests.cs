using NOBA.Archive.Core.Operations;
using NOBA.Archive.Core.Primitives;
using NOBA.Archive.Infrastructure.FileSystem;
using NOBA.Archive.Infrastructure.Runtime;
using NOBA.Archive.TestKit.Fakes;
using Xunit;

namespace NOBA.Archive.Infrastructure.Tests;

/// <summary>SEC-020 and SEC-021: scratch space is per-operation, cleans up, and orphans get swept.</summary>
public sealed class TempWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noba-temp-" + Guid.NewGuid().ToString("N"));

    private TempWorkspaceFactory CreateFactory()
    {
        var paths = new AppDataPathProvider(_root);
        Directory.CreateDirectory(paths.TempRoot);
        return new TempWorkspaceFactory(paths, new PhysicalFileSystem(), new NullLogger<TempWorkspaceFactory>());
    }

    private static OperationId NewId(int sequence = 1) =>
        OperationId.Create(OperationType.Extract, DateTimeOffset.UnixEpoch, sequence);

    [Fact]
    public async Task Creates_and_removes_its_own_directory()
    {
        var factory = CreateFactory();
        string path;

        await using (var workspace = factory.Create(NewId()))
        {
            path = workspace.RootPath;
            Assert.True(Directory.Exists(path));
        }

        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task Gives_each_operation_its_own_space()
    {
        var factory = CreateFactory();

        await using var first = factory.Create(NewId(1));
        await using var second = factory.Create(NewId(2));

        Assert.NotEqual(first.RootPath, second.RootPath);
    }

    [Fact]
    public async Task Produces_unique_file_paths_inside_the_workspace()
    {
        var factory = CreateFactory();
        await using var workspace = factory.Create(NewId());

        var a = workspace.CreateFilePath("entry.bin");
        var b = workspace.CreateFilePath("entry.bin");

        Assert.NotEqual(a, b);
        Assert.Equal(workspace.RootPath, Path.GetDirectoryName(a));
    }

    [Fact]
    public async Task Sanitises_a_suggested_name_that_tries_to_escape()
    {
        var factory = CreateFactory();
        await using var workspace = factory.Create(NewId());

        var path = workspace.CreateFilePath(Path.Combine("..", "..", "escape.bin"));

        Assert.Equal(workspace.RootPath, Path.GetDirectoryName(path));
    }

    /// <summary>A crash leaves a workspace behind; the next start reclaims it.</summary>
    [Fact]
    public void Sweeps_a_workspace_owned_by_a_process_that_no_longer_exists()
    {
        var factory = CreateFactory();
        var paths = new AppDataPathProvider(_root);
        var orphan = Path.Combine(paths.TempRoot, "EX-19700101-000099");
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, ".noba-owner"), "2147483645");

        var swept = factory.SweepOrphans();

        Assert.Equal(1, swept);
        Assert.False(Directory.Exists(orphan));
    }

    /// <summary>A live process's workspace must survive the sweep, or concurrent runs eat each other.</summary>
    [Fact]
    public async Task Leaves_a_live_workspace_alone()
    {
        var factory = CreateFactory();
        await using var workspace = factory.Create(NewId());

        var swept = factory.SweepOrphans();

        Assert.Equal(0, swept);
        Assert.True(Directory.Exists(workspace.RootPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
