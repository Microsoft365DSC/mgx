using System.Management.Automation;
using System.Net;
using System.Threading.RateLimiting;
using Mgx.Cmdlets.Cmdlets.Configuration;
using Mgx.IntegrationTests.Fakes;
using Mgx.IntegrationTests.Infrastructure;
using Mgx.Engine.Http;
using Polly;
using Mgx.IntegrationTests.Engine;

namespace Mgx.IntegrationTests.Cmdlets;

/// <summary>
/// Tests for Enable-MgxResilience cmdlet and its internal state management.
/// </summary>
[Collection(ResilienceCollection.Name)]
public class EnableMgxResilienceTests
{
    private static void ResetState()
    {
        EnableMgxResilience.IsEnabled = false;
        EnableMgxResilience.ResilientSdkClient = null;
        EnableMgxResilience.OriginalSdkClient = null;
        EnableMgxResilience.ActiveHandler = null;
    }

    [Fact]
    public void RefreshInjectedClient_NotEnabled_DoesNothing()
    {
        // Ensure clean state - reset all static state before test
        try
        {
            ResetState();
            var warnings = new List<string>();
            var verboses = new List<string>();

            EnableMgxResilience.RefreshInjectedClient(warnings.Add, verboses.Add);

            Assert.False(EnableMgxResilience.IsEnabled);
            Assert.Empty(warnings);
            Assert.Empty(verboses);
        }
        finally
        {
            ResetState();
        }
    }

}
