#if NOBA_OFFLINE_TESTS
using System.Reflection;
using NOBA.Archive.TestKit.Harness;

namespace NOBA.Archive.Tests;

/// <summary>
/// Entry point used only when the solution is built with <c>NobaOfflineBuild=true</c>. On the Windows
/// build machine xUnit v3 supplies its own entry point and this file is not compiled.
/// </summary>
internal static class OfflineTestMain
{
    private static int Main() => TestRunner.Run(Assembly.GetExecutingAssembly(), Console.Out);
}
#endif
