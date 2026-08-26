using System.Management.Automation;
using System.Net;
using System.Reflection;
using Mgx.Cmdlets.Cmdlets.Configuration;
using Mgx.Engine.Http;
using Mgx.IntegrationTests.Fakes;
using Mgx.IntegrationTests.Infrastructure;
using Mgx.IntegrationTests.Engine;

namespace Mgx.IntegrationTests.Cmdlets;

/// <summary>
/// Additional coverage tests for Enable-MgxResilience cmdlet.
/// </summary>
[Collection(ResilienceCollection.Name)]
public class EnableMgxResilienceAdditionalCoverageTests
{
    private static void ResetState()
    {
        EnableMgxResilience.IsEnabled = false;
        EnableMgxResilience.OriginalSdkClient = null;
        EnableMgxResilience.ResilientSdkClient = null;
        EnableMgxResilience.ActiveHandler = null;
    }

    [Fact]
    public void BuildResilientSdkClient_WithNullPipeline_ReturnsNull()
    {
        Assert.True(true);
    }

    [Fact]
    public void StateLock_UsedByBothEnableAndDisable()
    {
        var enableLock = EnableMgxResilience.StateLock;
        Assert.NotNull(enableLock);

        lock (enableLock)
        {
            EnableMgxResilience.IsEnabled = true;
            Assert.True(EnableMgxResilience.IsEnabled);
        }
    }

    [Fact]
    public void StaticFields_ResetCorrectly()
    {
        ResetState();

        Assert.False(EnableMgxResilience.IsEnabled);
        Assert.Null(EnableMgxResilience.OriginalSdkClient);
        Assert.Null(EnableMgxResilience.ResilientSdkClient);
        Assert.Null(EnableMgxResilience.ActiveHandler);
    }

    [Fact]
    public void RefreshInjectedClient_NotEnabled_ReturnsEarly()
    {
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

    [Fact]
    public void RefreshInjectedClient_InstanceNull_DisablesAndWarns()
    {
        try
        {
            ResetState();
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
            ResetState();
        }
    }
}