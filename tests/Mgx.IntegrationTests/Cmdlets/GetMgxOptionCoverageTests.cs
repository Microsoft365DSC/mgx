using System;
using System.Management.Automation;
using System.Net;
using Mgx.Cmdlets.Cmdlets.Configuration;
using Mgx.Cmdlets.Models;
using Mgx.IntegrationTests.Fakes;
using Mgx.IntegrationTests.Infrastructure;

namespace Mgx.IntegrationTests.Cmdlets;

/// <summary>
/// Tests for Get-MgxOption cmdlet.
/// </summary>
// Hosts cmdlets through MgxTestHost, which sets the shared transport and client options
[Collection("Pipeline")]
public class GetMgxOptionCoverageTests
{
    [Fact]
    public void ProcessRecord_ReturnsAllOptions()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, """{"id":"1"}""");
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps =>
        {
            ps.AddCommand("Get-MgxOption");
        });

        Assert.NotNull(result.Output);
        Assert.Single(result.Output);

        var output = result.Output[0].BaseObject;
        Assert.NotNull(output);

        var props = output.GetType().GetProperties();
        Assert.Contains(props, p => p.Name == "RateLimitBurst");
        Assert.Contains(props, p => p.Name == "RateLimitPerSecond");
        Assert.Contains(props, p => p.Name == "NoRateLimit");
        Assert.Contains(props, p => p.Name == "RateLimitQueueLimit");
        Assert.Contains(props, p => p.Name == "MaxRetryAttempts");
        Assert.Contains(props, p => p.Name == "MaxRetryAfterSeconds");
        Assert.Contains(props, p => p.Name == "TotalTimeoutSeconds");
        Assert.Contains(props, p => p.Name == "AttemptTimeoutSeconds");
        Assert.Contains(props, p => p.Name == "CircuitBreakerDurationSeconds");
        Assert.Contains(props, p => p.Name == "CircuitBreakerFailureRatio");
        Assert.Contains(props, p => p.Name == "CircuitBreakerMinThroughput");
        Assert.Contains(props, p => p.Name == "CircuitBreakerSamplingDurationSeconds");
        Assert.Contains(props, p => p.Name == "BatchChunkConcurrency");
        Assert.Contains(props, p => p.Name == "BatchItemsPerSecond");
    }
}