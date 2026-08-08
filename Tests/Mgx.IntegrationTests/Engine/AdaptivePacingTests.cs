using System.Diagnostics;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests.Engine;

/// <summary>
/// Batch write pacing is halved whenever Graph returns 429, and the reduced rate is persisted
/// across Invoke-MgxBatchRequest calls. Without a way back up, one throttling episode slowed
/// every later batch for the lifetime of the process, so recovery is the point of these tests.
/// </summary>
public class AdaptivePacingTests
{
    [Theory]
    [InlineData(20, 10)]
    [InlineData(10, 5)]
    [InlineData(5, 2)]
    public void ReduceRate_halves_on_throttling(int rate, int expected)
    {
        Assert.Equal(expected, GraphBatchClient.ReduceRate(rate));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(2)]
    [InlineData(1)]
    public void ReduceRate_never_falls_below_the_floor(int rate)
    {
        // Halving without a floor would eventually reach zero, which disables pacing
        // entirely - the opposite of what a throttled tenant needs.
        Assert.True(GraphBatchClient.ReduceRate(rate) >= 2);
    }

    [Fact]
    public void RecoverRate_climbs_additively_not_by_doubling()
    {
        // Additive increase against multiplicative decrease: the rate approaches the
        // throttling threshold instead of leaping back onto it.
        Assert.Equal(12, GraphBatchClient.RecoverRate(10, 20));
    }

    [Fact]
    public void RecoverRate_always_makes_progress_at_small_configured_rates()
    {
        // configuredRate / 10 rounds to zero below 10 items/sec, which would stall recovery.
        Assert.Equal(3, GraphBatchClient.RecoverRate(2, 5));
    }

    [Fact]
    public void RecoverRate_stops_at_the_configured_rate()
    {
        Assert.Equal(20, GraphBatchClient.RecoverRate(19, 20));
        Assert.Equal(20, GraphBatchClient.RecoverRate(20, 20));
    }

    [Fact]
    public void Reduced_rate_climbs_all_the_way_back_to_the_configured_rate()
    {
        // The regression this whole change is about: from halved, repeated clean chunks
        // must terminate at the configured rate rather than converging below it.
        const int configured = 20;
        var rate = GraphBatchClient.ReduceRate(configured);
        Assert.True(rate < configured);

        for (var i = 0; i < 100 && rate < configured; i++)
            rate = GraphBatchClient.RecoverRate(rate, configured);

        Assert.Equal(configured, rate);
    }

    [Fact]
    public void Adapted_rate_expires_after_a_long_quiet_period()
    {
        var now = Stopwatch.GetTimestamp();
        var throttledLongAgo = now
            - (long)((GraphBatchClient.AdaptiveRecoveryWindow.TotalSeconds + 60) * Stopwatch.Frequency);

        Assert.True(GraphBatchClient.AdaptedRateHasExpired(throttledLongAgo, now));
    }

    [Fact]
    public void Adapted_rate_survives_a_recent_throttle()
    {
        var now = Stopwatch.GetTimestamp();
        var throttledJustNow = now - (long)(5 * Stopwatch.Frequency);

        Assert.False(GraphBatchClient.AdaptedRateHasExpired(throttledJustNow, now));
    }

    [Fact]
    public void Never_expires_when_no_throttle_was_ever_recorded()
    {
        Assert.False(GraphBatchClient.AdaptedRateHasExpired(0, Stopwatch.GetTimestamp()));
    }
}
