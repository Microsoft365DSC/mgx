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
    public void Get_MgxOption_reports_the_options_the_session_is_running_with()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, """{"id":"1"}""");
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps =>
        {
            ps.AddCommand("Get-MgxOption");
        });

        Assert.Single(result.Output);
        var output = result.Output[0].BaseObject;
        object? Value(string name) => output.GetType().GetProperty(name)!.GetValue(output);

        // MgxTestHost applies FastOptions, so these are the values the cmdlet must report back
        Assert.Equal(true, Value("NoRateLimit"));
        Assert.Equal(2, Value("MaxRetryAttempts"));
        Assert.Equal(10, Value("AttemptTimeoutSeconds"));
        Assert.Equal(30, Value("TotalTimeoutSeconds"));
        Assert.Equal(1000, Value("CircuitBreakerMinThroughput"));
        Assert.Equal(1, Value("MaxRetryAfterSeconds"));
        Assert.Equal(0, Value("BatchItemsPerSecond"));
    }
}
