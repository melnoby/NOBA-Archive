namespace NOBA.Archive.Application.Tests;

/// <summary>
/// A bounded token for every awaited test operation, so a regression shows up as a failing test rather
/// than a hung suite.
/// </summary>
internal static class TestContext
{
    public static CancellationToken Token => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;
}
