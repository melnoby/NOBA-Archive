namespace NOBA.Archive.TestKit.Fixtures;

/// <summary>
/// Real 7-Zip archives, checked in as base64.
/// </summary>
/// <remarks>
/// <para>
/// Embedded rather than generated at test time, for two reasons. The suite must not depend on a 7-Zip
/// binary being installed on whatever machine runs it — a skipped security test is an untested
/// security control. And a fixture produced by the same tool family that the code was written against
/// tests agreement rather than correctness; a fixed archive tests the reader against bytes that do not
/// move when a tool is upgraded.
/// </para>
/// <para>
/// The two hostile ones cannot be produced by NOBA Archive at all — it refuses to write such names —
/// so they were built with <c>7z a -spf</c>, which retains the paths verbatim. That is the point: the
/// code under test must never be able to generate its own attack fixture.
/// </para>
/// </remarks>
public static class SevenZipFixtures
{
    /// <summary>The password for <see cref="WriteEncryptedHeadersSevenZip"/>.</summary>
    public const string EncryptedPassword = "hunter2";

    /// <summary>Entry names in the well-formed fixtures, in archive order.</summary>
    public static IReadOnlyList<string> PlainEntryNames { get; } =
        ["a.txt", "docs/b.txt", "report \U0001F389.txt", "\u062A\u0642\u0631\u064A\u0631.txt"];

    private const string PlainSevenZipBase64 =
        "N3q8ryccAARSrOCU1AAAAAAAAAAiAAAAAAAAABZgrOABADxoZWxsbyBmcm9tIDd6bmVzdGVkIGNvbnRlbnRlbW9qaSDwn46J" +
        "IG5hbWXZhdit2KrZiNmJINi52LHYqNmKAAAAgTMHrg/R/+M9QMCQ0v99aU2C5Zs+Kky6hhdtS2ZofCUpJUr6sa578WmWXSYw" +
        "90F1eF1Sk/TzLr4wb7QySgT6LgGCnLwW+08tRzamnVvkgMLfuZ/l5QR4edfB90+uFGLLH5Vg6FiZkKtwdr/8rONTWnCXvFZs" +
        "e1cKuo94jNKqapmIATFhm3/53LT6ff0LqQAAABcGQQEJgJMABwsBAAEjAwEBBV0AEAAADIDiCgF976bhAAA=";

    /// <summary>PlainSevenZip, written out to <paramref name="path"/>.</summary>
    public static string WritePlainSevenZip(string path) => WriteFixture(path, PlainSevenZipBase64);

    private const string SolidSevenZipBase64 =
        "N3q8ryccAARSrOCU1AAAAAAAAAAiAAAAAAAAABZgrOABADxoZWxsbyBmcm9tIDd6bmVzdGVkIGNvbnRlbnRlbW9qaSDwn46J" +
        "IG5hbWXZhdit2KrZiNmJINi52LHYqNmKAAAAgTMHrg/R/+M9QMCQ0v99aU2C5Zs+Kky6hhdtS2ZofCUpJUr6sa578WmWXSYw" +
        "90F1eF1Sk/TzLr4wb7QySgT6LgGCnLwW+08tRzamnVvkgMLfuZ/l5QR4edfB90+uFGLLH5Vg6FiZkKtwdr/8rONTWnCXvFZs" +
        "e1cKuo94jNKqapmIATFhm3/53LT6ff0LqQAAABcGQQEJgJMABwsBAAEjAwEBBV0AEAAADIDiCgF976bhAAA=";

    /// <summary>SolidSevenZip, written out to <paramref name="path"/>.</summary>
    public static string WriteSolidSevenZip(string path) => WriteFixture(path, SolidSevenZipBase64);

    private const string EncryptedHeadersSevenZipBase64 =
        "N3q8ryccAAQSFejeAAEAAAAAAAA+AAAAAAAAAKvQ7r74ymKuJnhGeewzUQiitQBTqiSXbBtu1Khb/6EcqbbOquEdnBrtcyg6" +
        "oGE3cMUXUNU6v40qpNCFsm9x4LLu8IoG9wJLJryvc1e4sWYQWO5I7uWLvVCiPdE0/CCMmL/kRaDSWUuNGC9xjRWTuH5zQzqJ" +
        "RSGyoWfv0LBuKNoYl8dOo9U5w9UlIpTJ/GuEgmoBhM8HSiKy3co8XvWxvfcVqeH9Jysa1atphXjHm8LU29C1wR/os438PP4X" +
        "zmVK3ssRAJAUKqbITc53yt7dwchOAsG4K647HgZ7YKDiZA1z4MtwDeSfP5MsK1LvW8+zC1Q6iCr3BhukTlZNnF+CjNXrbMAG" +
        "FwZQAQmAsAAHCwEAAiQG8QcBElMPxU2e5hN7XFcRNK/0DW3kgiMDAQEFXQAQAAABAAyAr4ECCgE7wNUCAAA=";

    /// <summary>EncryptedHeadersSevenZip, written out to <paramref name="path"/>.</summary>
    public static string WriteEncryptedHeadersSevenZip(string path) => WriteFixture(path, EncryptedHeadersSevenZipBase64);

    private const string TraversalSevenZipBase64 =
        "N3q8ryccAATkX1u1CQAAAAAAAACKAAAAAAAAAO13YxIBAARQV05FRAABBAYAAQkJAAcLAQABISEBAAwFAAgKAc6RUC4AAAUB" +
        "GQwAAAAAAAAAAAAAAAARPwAuAC4ALwAuAC4ALwAuAC4ALwB0AHoAMgAvAGQAZQBlAHAALwBzAHUAYgAvAGUAdgBpAGwALgB0" +
        "AHgAdAAAABkEAAAAABQKAQANaX6rIDHdARUGAQAggKSBAAA=";

    /// <summary>TraversalSevenZip, written out to <paramref name="path"/>.</summary>
    public static string WriteTraversalSevenZip(string path) => WriteFixture(path, TraversalSevenZipBase64);

    private const string AbsolutePathSevenZipBase64 =
        "N3q8ryccAASG43iVCQAAAAAAAACCAAAAAAAAAKGk0foBAARQV05FRAABBAYAAQkJAAcLAQABISEBAAwFAAgKAc6RUC4AAAUB" +
        "GQwAAAAAAAAAAAAAAAARNwAvAHQAbQBwAC8AdAB6ADIALwBkAGUAZQBwAC8AcwB1AGIALwBlAHYAaQBsAC4AdAB4AHQAAAAZ" +
        "BAAAAAAUCgEADWl+qyAx3QEVBgEAIICkgQAA";

    /// <summary>AbsolutePathSevenZip, written out to <paramref name="path"/>.</summary>
    public static string WriteAbsolutePathSevenZip(string path) => WriteFixture(path, AbsolutePathSevenZipBase64);

    private static string WriteFixture(string path, string base64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Convert.FromBase64String(base64));
        return path;
    }
}
