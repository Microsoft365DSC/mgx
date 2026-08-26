using System.Net;
using Mgx.Cmdlets.Cmdlets.Configuration;
using Mgx.IntegrationTests.Fakes;
using Mgx.IntegrationTests.Infrastructure;
using Mgx.Cmdlets.Models;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests.Cmdlets;

/// <summary>
/// Tests for Get-MgxTelemetry cmdlet.
/// </summary>
public class GetMgxTelemetryTests
{
    [Fact]
    public void ProcessRecord_ReturnsTelemetryOutputWithAllProperties()
    {
        MgxTelemetryCollector.Current.Reset();
        MgxTelemetryCollector.Current.RecordRequest(true, 0);

        var summary = MgxTelemetryCollector.Current.GetSummary();

        var result = new MgxTelemetryOutput
        {
            Requests = summary.TotalRequests,
            Succeeded = summary.Succeeded,
            Failed = summary.Failed,
            ThrottleRetries = summary.ThrottleRetries,
            OtherRetries = summary.OtherRetries,
            CircuitBreakerTrips = summary.CircuitBreakerTrips,
            RateLimiterWaitMs = summary.RateLimiterWaitMs,
            RetryDelayMs = summary.RetryDelayMs,
            HttpMs = summary.HttpMs,
            TotalElapsedMs = summary.ElapsedMs,
            ResourceUnitsConsumed = summary.ResourceUnitsConsumed,
            BatchItemThrottles = summary.BatchItemThrottles,
        };

        Assert.Equal(1, result.Requests);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.ThrottleRetries);
        Assert.Equal(0, result.OtherRetries);
        Assert.Equal(0, result.CircuitBreakerTrips);
        Assert.Equal(0, result.RateLimiterWaitMs);
        Assert.Equal(0, result.RetryDelayMs);
        Assert.Equal(0, result.HttpMs);
        Assert.True(result.TotalElapsedMs >= 0);
        Assert.Equal(0, result.ResourceUnitsConsumed);
        Assert.Equal(0, result.BatchItemThrottles);
    }

    [Fact]
    public void ProcessRecord_WithReset_ClearsTelemetry()
    {
        MgxTelemetryCollector.Current.Reset();
        MgxTelemetryCollector.Current.RecordRequest(true, 0);

        MgxTelemetryCollector.Current.Reset();

        var summary = MgxTelemetryCollector.Current.GetSummary();

        Assert.Equal(0, summary.TotalRequests);
        Assert.Equal(0, summary.Succeeded);
        Assert.Equal(0, summary.Failed);
    }

    [Fact]
    public void ProcessRecord_RecordsThrottleRetries()
    {
        MgxTelemetryCollector.Current.Reset();
        MgxTelemetryCollector.Current.RecordRetry(isThrottle: true, delayMs: 100);
        MgxTelemetryCollector.Current.RecordRetry(isThrottle: true, delayMs: 200);

        var summary = MgxTelemetryCollector.Current.GetSummary();

        Assert.Equal(2, summary.ThrottleRetries);
        Assert.Equal(300, summary.RetryDelayMs);
    }

    [Fact]
    public void ProcessRecord_RecordsOtherRetries()
    {
        MgxTelemetryCollector.Current.Reset();
        MgxTelemetryCollector.Current.RecordRetry(isThrottle: false, delayMs: 50);

        var summary = MgxTelemetryCollector.Current.GetSummary();

        Assert.Equal(1, summary.OtherRetries);
        Assert.Equal(50, summary.RetryDelayMs);
    }

    [Fact]
    public void ProcessRecord_RecordsCircuitBreakerTrips()
    {
        MgxTelemetryCollector.Current.Reset();
        MgxTelemetryCollector.Current.RecordCircuitBreakerTrip();
        MgxTelemetryCollector.Current.RecordCircuitBreakerTrip();

        var summary = MgxTelemetryCollector.Current.GetSummary();

        Assert.Equal(2, summary.CircuitBreakerTrips);
    }

    [Fact]
    public void ProcessRecord_RecordsBatchItemThrottles()
    {
        MgxTelemetryCollector.Current.Reset();
        MgxTelemetryCollector.Current.RecordBatchItemThrottles(5);

        var summary = MgxTelemetryCollector.Current.GetSummary();

        Assert.Equal(5, summary.BatchItemThrottles);
    }
}