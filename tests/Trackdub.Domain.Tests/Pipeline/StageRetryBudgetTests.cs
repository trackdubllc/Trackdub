using Trackdub.Domain.Pipeline;
using Xunit;

namespace Trackdub.Domain.Tests.Pipeline;

public sealed class StageRetryBudgetTests
{
    [Fact]
    public void Default_constants_match_legacy_hardcoded_values()
    {
        StageRetryBudget budget = StageRetryBudget.Default;
        Assert.Equal(3, budget.MaxAttempts);
        Assert.Equal(50, budget.BaseBackoffMs);
        Assert.Equal(StageRetryBudget.MaxBackoffMsDefault, budget.MaxBackoffMs);
    }

    [Theory]
    [InlineData(1, 50)]
    [InlineData(2, 100)]
    [InlineData(3, 200)]
    [InlineData(4, 400)]
    public void BackoffFor_doubles_each_attempt_until_attempt_cap(int attempt, int expectedMs)
    {
        StageRetryBudget budget = StageRetryBudget.Default;
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), budget.BackoffFor(attempt));
    }

    [Fact]
    public void BackoffFor_caps_at_MaxBackoffMs_for_very_high_attempts()
    {
        // Cap reached when attempt - 1 == 10 -> 50ms * 2^10 = 51200ms. Further doublings clamp.
        Assert.Equal(TimeSpan.FromMilliseconds(51200), StageRetryBudget.Default.BackoffFor(11));
        Assert.Equal(TimeSpan.FromMilliseconds(51200), StageRetryBudget.Default.BackoffFor(20));
    }

    [Fact]
    public void BackoffFor_clamps_calculated_value_when_MaxBackoffMs_set_below_default()
    {
        // MaxBackoffMs is overridable via `with` so the per-stage override seam stays reachable
        // without forking the constructor. With MaxBackoffMs=150 + BaseBackoffMs=50, attempt 3
        // (200ms) clamps to 150ms while attempt 1 (50ms) and attempt 2 (100ms) fit.
        StageRetryBudget budget = StageRetryBudget.Default with { MaxBackoffMs = 150 };
        Assert.Equal(TimeSpan.FromMilliseconds(50), budget.BackoffFor(1));
        Assert.Equal(TimeSpan.FromMilliseconds(100), budget.BackoffFor(2));
        Assert.Equal(TimeSpan.FromMilliseconds(150), budget.BackoffFor(3));
    }

    [Theory]
    [InlineData(0, 50)]   // MaxAttempts < 1
    [InlineData(11, 50)]  // MaxAttempts > MaxAttemptsHardCap (10)
    [InlineData(3, -1)]   // BaseBackoffMs < 0
    public void Constructor_throws_for_invalid_inputs(int maxAttempts, int baseBackoffMs)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StageRetryBudget(maxAttempts, baseBackoffMs));
    }

    [Fact]
    public void BackoffFor_throws_when_attempt_less_than_one()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StageRetryBudget.Default.BackoffFor(0));
    }
}
