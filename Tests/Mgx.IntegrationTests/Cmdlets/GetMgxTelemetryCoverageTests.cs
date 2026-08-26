using System;
using System.Management.Automation;
using System.Net;
using Mgx.Cmdlets.Cmdlets.Configuration;
using Mgx.Cmdlets.Models;
using Mgx.Engine.Http;
using Mgx.IntegrationTests.Fakes;
using Mgx.IntegrationTests.Infrastructure;

namespace Mgx.IntegrationTests.Cmdlets;

/// <summary>
/// Tests for Get-MgxTelemetry cmdlet.
/// </summary>
public class GetMgxTelemetryCoverageTests
{
    [Fact]
    public void ProcessRecord_ReturnsTelemetryOutput()
    {
        MgxTelemetryCollector.Current.Reset();

        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, """{"id":"1"}""");
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps =>
        {
            ps.AddCommand("Get-MgxTelemetry");
        });

        Assert.NotNull(result.Output);
        Assert.Single(result.Output);

        var output = result.Output[0].BaseObject;
        Assert.NotNull(output);

        var props = output.GetType().GetProperties();
        Assert.Contains(props, p => p.Name == "Requests");
        Assert.Contains(props, p => p.Name == "Succeeded");
        Assert.Contains(props, p => p.Name == "Failed");
        Assert.Contains(props, p => p.Name == "ThrottleRetries");
        Assert.Contains(props, p => p.Name == "OtherRetries");
        Assert.Contains(props, p => p.Name == "CircuitBreakerTrips");
        Assert.Contains(props, p => p.Name == "RateLimiterWaitMs");
        Assert.Contains(props, p => p.Name == "RetryDelayMs");
        Assert.Contains(props, p => p.Name == "HttpMs");
        Assert.Contains(props, p => p.Name == "TotalElapsedMs");
        Assert.Contains(props, p => p.Name == "ResourceUnitsConsumed");
        Assert.Contains(props, p => p.Name == "BatchItemThrottles");
    }
}