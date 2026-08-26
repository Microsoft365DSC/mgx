using System.Management.Automation;
using System.Net;
using Mgx.Cmdlets.Cmdlets.Configuration;
using Mgx.IntegrationTests.Fakes;
using Mgx.IntegrationTests.Infrastructure;
using Mgx.IntegrationTests.Engine;

namespace Mgx.IntegrationTests.Cmdlets;

/// <summary>
/// Tests for Disable-MgxResilience cmdlet and its internal state management.
/// </summary>
[Collection(ResilienceCollection.Name)]
public class DisableMgxResilienceTests
{
    [Fact]
    public void RefreshInjectedClient_NotEnabled_DoesNothing()
    {
        EnableMgxResilience.IsEnabled = false;
        var warnings = new List<string>();
        var verboses = new List<string>();

        EnableMgxResilience.RefreshInjectedClient(warnings.Add, verboses.Add);

        Assert.False(EnableMgxResilience.IsEnabled);
        Assert.Empty(warnings);
        Assert.Empty(verboses);
    }

    [Fact]
    public void RefreshInjectedClient_EnabledButNoGraphSession_DisablesAndWarns()
    {
        try
        {
            EnableMgxResilience.IsEnabled = true;
            EnableMgxResilience.ResilientSdkClient = new HttpClient();
            var warnings = new List<string>();
            var verboses = new List<string>();

            EnableMgxResilience.RefreshInjectedClient(warnings.Add, verboses.Add);

            Assert.False(EnableMgxResilience.IsEnabled);
            // Warning message when GraphSession.Instance is null
            Assert.Contains(warnings, w => w.Contains("Graph identity changed", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            EnableMgxResilience.IsEnabled = false;
            EnableMgxResilience.ResilientSdkClient = null;
            EnableMgxResilience.OriginalSdkClient = null;
            EnableMgxResilience.ActiveHandler = null;
        }
    }

    [Fact]
    public void StateLock_IsSharedBetweenEnableAndDisable()
    {
        var lockObj = EnableMgxResilience.StateLock;
        Assert.NotNull(lockObj);

        lock (lockObj)
        {
            EnableMgxResilience.IsEnabled = true;
            Assert.True(EnableMgxResilience.IsEnabled);
        }
    }
}