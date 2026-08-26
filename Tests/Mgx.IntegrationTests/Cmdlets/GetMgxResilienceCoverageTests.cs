using System;
using System.Management.Automation;
using System.Net;
using Mgx.Cmdlets.Cmdlets.Configuration;
using Mgx.Cmdlets.Models;
using Mgx.IntegrationTests.Fakes;
using Mgx.IntegrationTests.Infrastructure;
using Mgx.IntegrationTests.Engine;

namespace Mgx.IntegrationTests.Cmdlets;

/// <summary>
/// Tests for Get-MgxResilience cmdlet.
/// </summary>
[Collection(ResilienceCollection.Name)]
public class GetMgxResilienceCoverageTests
{
    private static void ResetResilienceState()
    {
        EnableMgxResilience.IsEnabled = false;
        EnableMgxResilience.ResilientSdkClient = null;
        EnableMgxResilience.OriginalSdkClient = null;
        EnableMgxResilience.ActiveHandler = null;
    }

    [Fact]
    public void ProcessRecord_NotEnabled_ReturnsDisabled()
    {
        // Ensure clean state - reset static state before test
        ResetResilienceState();

        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, """{"id":"1"}""");
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps =>
        {
            ps.AddCommand("Get-MgxResilience");
        });

        Assert.NotNull(result.Output);
        Assert.Single(result.Output);

        var output = result.Output[0].BaseObject;
        Assert.NotNull(output);

        var isEnabledProp = output.GetType().GetProperty("IsEnabled");
        Assert.NotNull(isEnabledProp);
        Assert.Equal(false, isEnabledProp.GetValue(output));
    }

    [Fact]
    public void ProcessRecord_EnabledButInactive_ReturnsInactiveWithWarning()
    {
        // Ensure clean state first
        ResetResilienceState();

        try
        {
            // Enable resilience first
            EnableMgxResilience.IsEnabled = true;
            EnableMgxResilience.ResilientSdkClient = new System.Net.Http.HttpClient();

            var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, """{"id":"1"}""");
            using var host = new MgxTestHost(handler);

            var result = host.Run(ps =>
            {
                ps.AddCommand("Get-MgxResilience");
            });

            Assert.NotNull(result.Output);
            Assert.Single(result.Output);

            var output = result.Output[0].BaseObject;
            Assert.NotNull(output);

            var isEnabledProp = output.GetType().GetProperty("IsEnabled");
            Assert.NotNull(isEnabledProp);
            Assert.Equal(true, isEnabledProp.GetValue(output));

            var isActiveProp = output.GetType().GetProperty("IsActive");
            Assert.NotNull(isActiveProp);
            Assert.Equal(false, isActiveProp.GetValue(output));

            var warningProp = output.GetType().GetProperty("Warning");
            Assert.NotNull(warningProp);
            var warning = warningProp.GetValue(output);
            Assert.NotNull(warning);
            Assert.Contains("replaced", warning.ToString());
        }
        finally
        {
            ResetResilienceState();
        }
    }
}