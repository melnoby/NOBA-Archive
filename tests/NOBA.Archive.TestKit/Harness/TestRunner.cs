#if NOBA_OFFLINE_TESTS
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Xunit;

namespace NOBA.Archive.TestKit.Harness;

public sealed record TestOutcome(string Suite, string Name, bool Passed, string? Failure, TimeSpan Duration)
{
    /// <summary>A skipped test is neither a pass nor a failure, and is counted as neither.</summary>
    public string? SkipReason { get; init; }

    public bool Skipped => SkipReason is not null;
}

/// <summary>
/// Discovers and runs <c>[Fact]</c> and <c>[Theory]</c> methods by reflection. Used only when the
/// solution is built with <c>NobaOfflineBuild=true</c>; xUnit v3 is the runner everywhere else.
/// </summary>
public static class TestRunner
{
    public static int Run(Assembly assembly, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(output);

        var suite = assembly.GetName().Name ?? "tests";
        var outcomes = new List<TestOutcome>();
        var stopwatch = Stopwatch.StartNew();

        foreach (var type in assembly.GetTypes().Where(static t => t.IsClass && !t.IsAbstract).OrderBy(static t => t.FullName, StringComparer.Ordinal))
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance).OrderBy(static m => m.Name, StringComparer.Ordinal))
            {
                var fact = method.GetCustomAttribute<FactAttribute>();
                var theory = method.GetCustomAttribute<TheoryAttribute>();
                if (fact is null && theory is null)
                {
                    continue;
                }

                if (theory is not null)
                {
                    var cases = method.GetCustomAttributes<InlineDataAttribute>().ToList();
                    if (cases.Count == 0)
                    {
                        outcomes.Add(new TestOutcome(
                            suite,
                            $"{type.Name}.{method.Name}",
                            false,
                            "A [Theory] with no [InlineData] can never run.",
                            TimeSpan.Zero));
                        continue;
                    }

                    foreach (var testCase in cases)
                    {
                        outcomes.Add(Execute(suite, type, method, testCase.Data));
                    }

                    continue;
                }

                outcomes.Add(Execute(suite, type, method, []));
            }
        }

        stopwatch.Stop();

        var skipped = outcomes.Where(static o => o.Skipped).ToList();
        var failed = outcomes.Where(static o => !o.Passed && !o.Skipped).ToList();
        foreach (var skip in skipped)
        {
            output.WriteLine($"  SKIP  {skip.Name}: {skip.SkipReason}");
        }

        foreach (var failure in failed)
        {
            output.WriteLine($"  FAIL  {failure.Name}");
            foreach (var line in (failure.Failure ?? string.Empty).Split('\n'))
            {
                output.WriteLine($"        {line.TrimEnd('\r')}");
            }
        }

        var ran = outcomes.Count - skipped.Count;
        var skippedNote = skipped.Count > 0
            ? string.Create(CultureInfo.InvariantCulture, $", {skipped.Count} skipped")
            : string.Empty;
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{suite}: {ran - failed.Count}/{ran} passed{skippedNote} in {stopwatch.Elapsed.TotalSeconds:F2}s"));

        return failed.Count;
    }

    private static TestOutcome Execute(string suite, Type type, MethodInfo method, object?[] arguments)
    {
        var name = arguments.Length == 0
            ? $"{type.Name}.{method.Name}"
            : $"{type.Name}.{method.Name}({string.Join(", ", arguments.Select(static a => a?.ToString() ?? "null"))})";

        var stopwatch = Stopwatch.StartNew();
        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(type);
            var result = method.Invoke(instance, arguments);
            switch (result)
            {
                case Task task:
                    task.GetAwaiter().GetResult();
                    break;
                case ValueTask valueTask:
                    valueTask.AsTask().GetAwaiter().GetResult();
                    break;
                default:
                    break;
            }

            stopwatch.Stop();
            return new TestOutcome(suite, name, true, null, stopwatch.Elapsed);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            stopwatch.Stop();
            return Failure(suite, name, ex.InnerException, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return Failure(suite, name, ex, stopwatch.Elapsed);
        }
        finally
        {
            (instance as IDisposable)?.Dispose();
        }
    }

    private static TestOutcome Failure(string suite, string name, Exception ex, TimeSpan elapsed) =>
        ex is TestSkippedException skip
            ? new TestOutcome(suite, name, false, null, elapsed) { SkipReason = skip.Message }
            : new TestOutcome(suite, name, false, Describe(ex), elapsed);

    private static string Describe(Exception ex) =>
        ex is AssertionFailedException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
}
#endif
