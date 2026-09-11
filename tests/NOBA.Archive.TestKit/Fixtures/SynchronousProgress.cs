namespace NOBA.Archive.TestKit.Fixtures;

/// <summary>
/// An <see cref="IProgress{T}"/> that invokes its callback inline, on the reporting thread.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Progress{T}"/> is the wrong tool for a test that asserts on a <em>sequence</em>. With no
/// synchronization context it posts each report to the thread pool, so callbacks can run out of order
/// and concurrently: a test that appends to a <c>List&lt;T&gt;</c> from that callback has both a data
/// race and an ordering assumption that the type does not provide. The symptom is a suite that passes
/// twelve times and fails the thirteenth, with the final snapshot appearing to be <c>Running</c>
/// because a report queued earlier happened to execute later.
/// </para>
/// <para>
/// Reporting inline gives the test the order the production code actually produced, which is the thing
/// under test. How a UI framework chooses to marshal those reports is that framework's concern.
/// </para>
/// </remarks>
public sealed class SynchronousProgress<T> : IProgress<T>
{
    private readonly Action<T> _onReport;

    public SynchronousProgress(Action<T> onReport)
    {
        ArgumentNullException.ThrowIfNull(onReport);
        _onReport = onReport;
    }

    public void Report(T value) => _onReport(value);
}
