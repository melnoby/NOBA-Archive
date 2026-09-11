namespace NOBA.Archive.Presentation.Tests;

/// <summary>
/// A bounded token for every awaited test operation, so a regression shows up as a failing test rather
/// than a hung suite.
/// </summary>
/// <remarks>
/// The same helper exists in the Application suite. It is duplicated rather than shared because the
/// name has to sit in each suite's own namespace to take precedence over xUnit's <c>TestContext</c>
/// under the shipping configuration, where the real package is present.
/// </remarks>
internal static class TestContext
{
    public static CancellationToken Token => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;
}
