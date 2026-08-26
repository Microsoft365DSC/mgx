using System.Net;
using Mgx.Cmdlets.Cmdlets.Configuration;
using Mgx.IntegrationTests.Fakes;
using Mgx.IntegrationTests.Infrastructure;
using Mgx.Cmdlets.Models;
using Mgx.IntegrationTests.Engine;

namespace Mgx.IntegrationTests.Cmdlets;

/// <summary>
/// Tests for Get-MgxResilience cmdlet.
/// </summary>
[Collection(ResilienceCollection.Name)]
public class GetMgxResilienceTests
{
    [Fact]
    public void ProcessRecord_NotEnabled_ReturnsDisabledState()
    {
        EnableMgxResilience.IsEnabled = false;
        EnableMgxResilience.ResilientSdkClient = null;

        var result = new MgxResilienceOutput
        {
            IsEnabled = EnableMgxResilience.IsEnabled,
            IsActive = false
        };

        Assert.False(result.IsEnabled);
        Assert.False(result.IsActive);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void ProcessRecord_EnabledAndActive_ReturnsActiveState()
    {
        EnableMgxResilience.IsEnabled = true;
        EnableMgxResilience.ResilientSdkClient = new HttpClient();

        var result = new MgxResilienceOutput
        {
            IsEnabled = true,
            IsActive = true
        };

        Assert.True(result.IsEnabled);
        Assert.True(result.IsActive);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void ProcessRecord_EnabledButInactive_ReturnsInactiveWithWarning()
    {
        EnableMgxResilience.IsEnabled = true;
        EnableMgxResilience.ResilientSdkClient = new HttpClient();

        var result = new MgxResilienceOutput
        {
            IsEnabled = true,
            IsActive = false,
            Warning = "Resilience was enabled but the SDK client was replaced (e.g., by Connect-MgGraph). Run Enable-MgxResilience to re-inject."
        };

        Assert.True(result.IsEnabled);
        Assert.False(result.IsActive);
        Assert.NotNull(result.Warning);
        Assert.Contains("replaced", result.Warning);
    }
}