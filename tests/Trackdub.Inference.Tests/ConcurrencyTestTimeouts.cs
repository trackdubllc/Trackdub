using System.Globalization;

namespace Trackdub.Inference.Tests;

/// <summary>
/// Wall-clock limits for concurrency tests. These are hang guards, not performance budgets: a
/// deadlocked handoff must fail the test, but a correct handoff that a loaded CI runner schedules
/// late must not.
/// </summary>
/// <remarks>
/// Await a signal produced by the code under test rather than polling a counter for a fixed
/// duration. <c>SpinWait.SpinUntil</c> keeps its thread pool thread for the whole budget, and that
/// thread is often one the handoff being waited on needs in order to make progress.
/// </remarks>
internal static class ConcurrencyTestTimeouts
{
    /// <summary>Upper bound for a single handoff; reaching it means the handoff is broken, not slow.</summary>
    /// <remarks>
    /// Set <c>TRACKDUB_TEST_HANG_GUARD_SECONDS</c> to trip a genuine hang faster while triaging
    /// locally. The default is sized for a loaded CI runner, where a correct handoff can be
    /// scheduled many seconds late.
    /// </remarks>
    internal static readonly TimeSpan HangGuard = ReadHangGuard();

    private static TimeSpan ReadHangGuard()
    {
        string? configured = Environment.GetEnvironmentVariable("TRACKDUB_TEST_HANG_GUARD_SECONDS");
        return int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) &&
            seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(60);
    }

    /// <summary>
    /// Awaits <paramref name="task"/>, rethrowing as a <see cref="TimeoutException"/> that names
    /// <paramref name="expectation"/> once <see cref="HangGuard"/> elapses.
    /// </summary>
    internal static async Task<T> AwaitWithHangGuard<T>(this Task<T> task, string expectation)
    {
        try
        {
            return await task.WaitAsync(HangGuard);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"{expectation} within {HangGuard.TotalSeconds:0} s.", ex);
        }
    }

    /// <inheritdoc cref="AwaitWithHangGuard{T}(Task{T}, string)" />
    internal static async Task AwaitWithHangGuard(this Task task, string expectation)
    {
        try
        {
            await task.WaitAsync(HangGuard);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"{expectation} within {HangGuard.TotalSeconds:0} s.", ex);
        }
    }
}
