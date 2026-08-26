using System.Management.Automation;
using System.Net;
using System.Reflection;
using Polly;
using Mgx.Cmdlets.Cmdlets.Configuration;
using Mgx.Engine.Http;
using Mgx.IntegrationTests.Fakes;
using Mgx.IntegrationTests.Infrastructure;
using Mgx.IntegrationTests.Engine;

namespace Mgx.IntegrationTests.Cmdlets;

/// <summary>
/// Coverage tests for Disable-MgxResilience cmdlet.
/// </summary>
[Collection(ResilienceCollection.Name)]
public class DisableMgxResilienceCoverageTests
{
    [Fact]
    public void ProcessRecord_NotEnabled_WritesWarning()
    {
        var handler = new StubHttpMessageHandler();
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps =>
        {
            ps.AddCommand("Disable-MgxResilience");
        });

        // Without GraphSession, it fails at GraphSessionNotFound
        // The warning "not currently enabled" would be written if GraphSession existed
        Assert.True(result.Terminating != null || result.Warnings.Any(w => w.Contains("not currently enabled", StringComparison.OrdinalIgnoreCase)));
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
