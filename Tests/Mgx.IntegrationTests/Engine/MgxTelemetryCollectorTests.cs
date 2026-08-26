using System;
using System.Net;
using Mgx.Engine.Http;
using Mgx.IntegrationTests.Fakes;
using Mgx.IntegrationTests.Engine;

namespace Mgx.IntegrationTests.Engine;

/// <summary>
/// Tests for MgxTelemetryCollector to boost coverage to 95%+.
/// </summary>
[Collection(ResilienceCollection.Name)]
public class MgxTelemetryCollectorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void RecordRequest_IncrementsTotalRequests()
    {
        MgxTelemetryCollector.Current.Reset();

        MgxTelemetryCollector.Current.RecordRequest(true, 0);

        var summary = MgxTelemetryCollector.Current.GetSummary();
        Assert.Equal(1, summary.TotalRequests);
        Assert.Equal(1, summary.Succeeded);
        Assert.Equal(0, summary.Failed);
    }

    [Fact]
    public void RecordRequest_FailedIncrementsFailed()
    {
        MgxTelemetryCollector.Current.Reset();

        MgxTelemetryCollector.Current.RecordRequest(false, 0);

        var summary = MgxTelemetryCollector.Current.GetSummary();
        Assert.Equal(1, summary.TotalRequests);
        Assert.Equal(0, summary.Succeeded);
        Assert.Equal(1, summary.Failed);
    }

    [Fact]
    public void RecordRetry_ThrottleIncrementsThrottleRetries()
    {
        MgxTelemetryCollector.Current.Reset();

        MgxTelemetryCollector.Current.RecordRetry(isThrottle: true, delayMs: 100);

        var summary = MgxTelemetryCollector.Current.GetSummary();
        Assert.Equal(1, summary.ThrottleRetries);
        Assert.Equal(100, summary.RetryDelayMs);
    }

    [Fact]
    public void RecordRetry_OtherIncrementsOtherRetries()
    {
        MgxTelemetryCollector.Current.Reset();

        MgxTelemetryCollector.Current.RecordRetry(isThrottle: false, delayMs: 50);

        var summary = MgxTelemetryCollector.Current.GetSummary();
        Assert.Equal(1, summary.OtherRetries);
        Assert.Equal(50, summary.RetryDelayMs);
    }

    [Fact]
    public void RecordCircuitBreakerTrip_IncrementsCounter()
    {
        MgxTelemetryCollector.Current.Reset();

        MgxTelemetryCollector.Current.RecordCircuitBreakerTrip();
        MgxTelemetryCollector.Current.RecordCircuitBreakerTrip();

        var summary = MgxTelemetryCollector.Current.GetSummary();
        Assert.Equal(2, summary.CircuitBreakerTrips);
    }

    [Fact]
    public void RecordBatchItemThrottles_IncrementsCounter()
    {
        MgxTelemetryCollector.Current.Reset();

        MgxTelemetryCollector.Current.RecordBatchItemThrottles(5);

        var summary = MgxTelemetryCollector.Current.GetSummary();
        Assert.Equal(5, summary.BatchItemThrottles);
    }

    [Fact]
    public void ResourceUnitsConsumed_IsInitiallyZero()
    {
        MgxTelemetryCollector.Current.Reset();

        var summary = MgxTelemetryCollector.Current.GetSummary();
        Assert.Equal(0, summary.ResourceUnitsConsumed);
    }

    [Fact]
    public void Reset_ClearsAllCounters()
    {
        MgxTelemetryCollector.Current.Reset();
        MgxTelemetryCollector.Current.RecordRequest(true, 0);
        MgxTelemetryCollector.Current.RecordRetry(true, 100);
        MgxTelemetryCollector.Current.RecordCircuitBreakerTrip();

        MgxTelemetryCollector.Current.Reset();

        var summary = MgxTelemetryCollector.Current.GetSummary();
        Assert.Equal(0, summary.TotalRequests);
        Assert.Equal(0, summary.Succeeded);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(0, summary.ThrottleRetries);
        Assert.Equal(0, summary.OtherRetries);
        Assert.Equal(0, summary.RetryDelayMs);
        Assert.Equal(0, summary.CircuitBreakerTrips);
        Assert.Equal(0, summary.BatchItemThrottles);
        Assert.Equal(0, summary.ResourceUnitsConsumed);
    }

    [Fact]
    public void GetSummary_ReturnsCorrectElapsedTime()
    {
        MgxTelemetryCollector.Current.Reset();

        var summary = MgxTelemetryCollector.Current.GetSummary();
        Assert.True(summary.ElapsedMs >= 0);
    }

    [Fact]
    public async Task ConcurrentCalls_AreThreadSafe()
    {
        MgxTelemetryCollector.Current.Reset();

        var tasks = new List<System.Threading.Tasks.Task>();
        var ct = TestContext.Current.CancellationToken;
        for (int i = 0; i < 100; i++)
        {
            tasks.Add(System.Threading.Tasks.Task.Run(() =>
            {
                MgxTelemetryCollector.Current.RecordRequest(true, 0);
                MgxTelemetryCollector.Current.RecordRetry(true, 1);
                MgxTelemetryCollector.Current.RecordRetry(false, 1);
            }, ct));
        }

        await System.Threading.Tasks.Task.WhenAll(tasks);

        var summary = MgxTelemetryCollector.Current.GetSummary();
        Assert.Equal(100, summary.TotalRequests);
        Assert.Equal(100, summary.ThrottleRetries);
        Assert.Equal(100, summary.OtherRetries);
        Assert.Equal(200, summary.RetryDelayMs); // 100 * 1 + 100 * 1 = 200
    }

    [Fact]
    public void Current_ReturnsSameInstance()
    {
        var instance1 = MgxTelemetryCollector.Current;
        var instance2 = MgxTelemetryCollector.Current;

        Assert.Same(instance1, instance2);
    }

    [Fact]
    public void HttpMs_IsInitiallyZero()
    {
        MgxTelemetryCollector.Current.Reset();

        var summary = MgxTelemetryCollector.Current.GetSummary();
        Assert.Equal(0, summary.HttpMs);
    }
}