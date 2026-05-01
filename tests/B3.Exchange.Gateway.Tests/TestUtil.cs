namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// Shared helpers for Gateway test polling. Centralizes the "spin until
/// predicate or timeout" pattern so individual tests don't reinvent it
/// (and don't subtly diverge on poll intervals or fail-mode behavior).
/// </summary>
internal static class TestUtil
{
    /// <summary>
    /// Polls <paramref name="predicate"/> until it returns true or
    /// <paramref name="timeout"/> elapses. Returns true if the predicate
    /// became true within the window; false if the timeout fired first.
    /// Callers typically follow the call with an Assert on the returned
    /// boolean (or on the same condition the predicate observed) so the
    /// failure message includes the actual offending state.
    /// </summary>
    /// <param name="poll">Optional poll interval. Defaults to 20 ms which
    /// is short enough for sub-second timeouts typical in Gateway tests
    /// without busy-spinning.</param>
    public static async Task<bool> WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan timeout,
        TimeSpan? poll = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var pollInterval = poll ?? TimeSpan.FromMilliseconds(20);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(pollInterval).ConfigureAwait(false);
        }
        return predicate();
    }

    /// <summary>
    /// Async-predicate variant. Useful when the wait condition itself
    /// requires awaiting (e.g., pulling a counter from a shared sink).
    /// </summary>
    public static async Task<bool> WaitUntilAsync(
        Func<Task<bool>> predicate,
        TimeSpan timeout,
        TimeSpan? poll = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var pollInterval = poll ?? TimeSpan.FromMilliseconds(20);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate().ConfigureAwait(false)) return true;
            await Task.Delay(pollInterval).ConfigureAwait(false);
        }
        return await predicate().ConfigureAwait(false);
    }
}
