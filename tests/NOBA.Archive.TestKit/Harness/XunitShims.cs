#if NOBA_OFFLINE_TESTS
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

// The namespace is deliberately 'Xunit' so test sources compile unchanged against either the real
// framework or this harness. Compiled only when NobaOfflineBuild=true.
namespace Xunit;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class FactAttribute : Attribute
{
    public string? Skip { get; set; }

    public string? DisplayName { get; set; }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class TheoryAttribute : Attribute
{
    public string? Skip { get; set; }

    public string? DisplayName { get; set; }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class InlineDataAttribute : Attribute
{
    public InlineDataAttribute(params object?[] data) => Data = data;

    public object?[] Data { get; }
}

/// <summary>Signals a failed assertion. Mirrors the role of xUnit's own exception type.</summary>
public sealed class AssertionFailedException : Exception
{
    public AssertionFailedException()
    {
    }

    public AssertionFailedException(string message)
        : base(message)
    {
    }

    public AssertionFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Thrown by <see cref="Assert.Skip(string)"/>; the runner reports it as skipped, not passed.</summary>
public sealed class TestSkippedException : Exception
{
    public TestSkippedException()
    {
    }

    public TestSkippedException(string message)
        : base(message)
    {
    }

    public TestSkippedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

[SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Mirrors the xUnit API surface.")]
[SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "'Single' is xUnit's name; renaming it would stop test sources compiling against both harnesses.")]
public static class Assert
{
    public static void True(bool condition, string? because = null)
    {
        if (!condition)
        {
            throw new AssertionFailedException(because ?? "Expected the condition to be true.");
        }
    }

    public static void False(bool condition, string? because = null)
    {
        if (condition)
        {
            throw new AssertionFailedException(because ?? "Expected the condition to be false.");
        }
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (!AreEqual(expected, actual))
        {
            throw new AssertionFailedException(
                string.Create(CultureInfo.InvariantCulture, $"Expected <{Describe(expected)}> but found <{Describe(actual)}>."));
        }
    }

    public static void NotEqual<T>(T expected, T actual)
    {
        if (AreEqual(expected, actual))
        {
            throw new AssertionFailedException(
                string.Create(CultureInfo.InvariantCulture, $"Expected a value other than <{Describe(expected)}>."));
        }
    }

    public static void Null(object? value)
    {
        if (value is not null)
        {
            throw new AssertionFailedException($"Expected null but found <{Describe(value)}>.");
        }
    }

    public static void NotNull([NotNull] object? value)
    {
        if (value is null)
        {
            throw new AssertionFailedException("Expected a value but found null.");
        }
    }

    public static void Same(object? expected, object? actual)
    {
        if (!ReferenceEquals(expected, actual))
        {
            throw new AssertionFailedException("Expected the same instance.");
        }
    }

    public static void Empty(IEnumerable collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        foreach (var _ in collection)
        {
            throw new AssertionFailedException("Expected an empty collection.");
        }
    }

    public static void NotEmpty(IEnumerable collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        foreach (var _ in collection)
        {
            return;
        }

        throw new AssertionFailedException("Expected a non-empty collection.");
    }

    public static void Contains(string expectedSubstring, string actual)
    {
        ArgumentNullException.ThrowIfNull(actual);
        if (!actual.Contains(expectedSubstring, StringComparison.Ordinal))
        {
            throw new AssertionFailedException($"Expected the text to contain '{expectedSubstring}'.");
        }
    }

    public static void DoesNotContain(string unexpectedSubstring, string actual)
    {
        ArgumentNullException.ThrowIfNull(actual);
        if (actual.Contains(unexpectedSubstring, StringComparison.Ordinal))
        {
            throw new AssertionFailedException($"Expected the text not to contain '{unexpectedSubstring}'.");
        }
    }

    public static void StartsWith(string expectedPrefix, string? actual)
    {
        if (actual is null || !actual.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            throw new AssertionFailedException($"Expected a string starting with '{expectedPrefix}' but found <{Describe(actual)}>.");
        }
    }

    public static void EndsWith(string expectedSuffix, string? actual)
    {
        if (actual is null || !actual.EndsWith(expectedSuffix, StringComparison.Ordinal))
        {
            throw new AssertionFailedException($"Expected a string ending with '{expectedSuffix}' but found <{Describe(actual)}>.");
        }
    }

    public static T IsType<T>(object? value)
    {
        if (value is not T typed)
        {
            throw new AssertionFailedException($"Expected an instance of {typeof(T).Name} but found <{Describe(value)}>.");
        }

        return typed;
    }

    public static T Throws<T>(Action action)
        where T : Exception
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            action();
        }
        catch (T expected)
        {
            return expected;
        }
        catch (Exception other)
        {
            throw new AssertionFailedException($"Expected {typeof(T).Name} but caught {other.GetType().Name}.", other);
        }

        throw new AssertionFailedException($"Expected {typeof(T).Name} but no exception was thrown.");
    }

    public static async Task<T> ThrowsAsync<T>(Func<Task> action)
        where T : Exception
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (T expected)
        {
            return expected;
        }
        catch (Exception other)
        {
            throw new AssertionFailedException($"Expected {typeof(T).Name} but caught {other.GetType().Name}.", other);
        }

        throw new AssertionFailedException($"Expected {typeof(T).Name} but no exception was thrown.");
    }

    public static async Task<T> ThrowsAnyAsync<T>(Func<Task> action)
        where T : Exception
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception other)
        {
            // Unlike ThrowsAsync, a derived type satisfies this - which is the whole distinction, and
            // why cancellation tests use it: TaskCanceledException derives from OperationCanceledException.
            if (other is T expected)
            {
                return expected;
            }

            throw new AssertionFailedException(
                $"Expected {typeof(T).Name} or a derived type but caught {other.GetType().Name}.", other);
        }

        throw new AssertionFailedException($"Expected {typeof(T).Name} but no exception was thrown.");
    }

    public static void Contains<T>(IEnumerable<T> collection, Predicate<T> filter)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(filter);

        foreach (var item in collection)
        {
            if (filter(item))
            {
                return;
            }
        }

        throw new AssertionFailedException("No item in the collection matched the predicate.");
    }

    public static void DoesNotContain<T>(IEnumerable<T> collection, Predicate<T> filter)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(filter);

        foreach (var item in collection)
        {
            if (filter(item))
            {
                throw new AssertionFailedException($"<{Describe(item)}> matched the predicate but should not have.");
            }
        }
    }

    public static void All<T>(IEnumerable<T> collection, Action<T> action)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(action);

        var index = 0;
        foreach (var item in collection)
        {
            try
            {
                action(item);
            }
            catch (AssertionFailedException ex)
            {
                throw new AssertionFailedException($"Item {index} (<{Describe(item)}>) failed: {ex.Message}", ex);
            }

            index++;
        }
    }

    public static object? Single(IEnumerable collection)
    {
        ArgumentNullException.ThrowIfNull(collection);

        object? found = null;
        var count = 0;
        foreach (var item in collection)
        {
            found = item;
            if (++count > 1)
            {
                throw new AssertionFailedException("Expected exactly one item but the collection had more.");
            }
        }

        return count == 1
            ? found
            : throw new AssertionFailedException("Expected exactly one item but the collection was empty.");
    }

    public static T Single<T>(IEnumerable<T> collection, Predicate<T> filter)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(filter);

        var matches = collection.Where(item => filter(item)).Take(2).ToList();
        return matches.Count == 1
            ? matches[0]
            : throw new AssertionFailedException(
                $"Expected exactly one item to match the predicate but {matches.Count} did.");
    }

    /// <summary>
    /// Marks the running test as skipped, mirroring <c>Assert.Skip</c> in xUnit v3. A skip is reported
    /// as a skip, never folded into the pass count - a suite that quietly counted unrun tests as
    /// passing would be worse than no suite.
    /// </summary>
    [DoesNotReturn]
    public static void Skip(string reason) => throw new TestSkippedException(reason);

    public static void Fail(string message) => throw new AssertionFailedException(message);

    /// <summary>
    /// Structural for sequences, default equality otherwise - which is what xUnit does. The default
    /// comparer alone would compare two byte arrays by reference, so an assertion on file content
    /// would fail even when the bytes matched. That divergence bit once; it does not get to bite twice.
    /// </summary>
    private static bool AreEqual<T>(T expected, T actual)
    {
        if (expected is IEnumerable left and not string && actual is IEnumerable right and not string)
        {
            return left.Cast<object?>().SequenceEqual(right.Cast<object?>());
        }

        return EqualityComparer<T>.Default.Equals(expected, actual);
    }

    private static string Describe(object? value) => value switch
    {
        null => "null",
        string s => $"\"{s}\"",
        IEnumerable e and not string => string.Join(", ", e.Cast<object?>().Select(Describe)),
        _ => value.ToString() ?? value.GetType().Name,
    };
}
#endif
