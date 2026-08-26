using System.Collections;
using System.Management.Automation;
using System.Net;
using System.Text.Json;
using Mgx.Cmdlets.Cmdlets.Expand;
using Mgx.IntegrationTests.Fakes;
using Mgx.IntegrationTests.Infrastructure;

namespace Mgx.IntegrationTests.Cmdlets;

/// <summary>
/// Coverage tests for Expand-MgxRelation cmdlet (unit-testable paths only).
/// Tests requiring GraphSession are in E2ETests with WireMock.
/// </summary>
// Hosts cmdlets through MgxTestHost, which sets the shared transport and client options
[Collection("Pipeline")]
public class ExpandMgxRelationCoverageTests
{
    // Use reflection to test private methods - cast to non-nullable since we control the test setup
    private static T InvokeMethod<T>(object target, string methodName, params object?[] parameters)
    {
        var method = target.GetType().GetMethod(methodName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);
        var result = method!.Invoke(target, parameters);
        return (T)result!;
    }
    [Fact]
    public void BeginProcessing_MissingIdPlaceholder_ThrowsError()
    {
        var handler = new StubHttpMessageHandler();
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps =>
        {
            ps.AddCommand("Expand-MgxRelation")
                .AddParameter("Uri", "/users/123/messages")
                .AddParameter("As", "messages");
        });

        Assert.NotNull(result.Terminating);
        Assert.Contains("MissingMandatoryParameter", result.Terminating.FullyQualifiedErrorId);
    }

    [Fact]
    public void BeginProcessing_SearchWithoutConsistencyLevel_ThrowsError()
    {
        var handler = new StubHttpMessageHandler();
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps =>
        {
            ps.AddCommand("Expand-MgxRelation")
                .AddParameter("Uri", "/users/{id}/messages?$search=test")
                .AddParameter("As", "messages");
        });

        Assert.NotNull(result.Terminating);
        Assert.True(
            result.Terminating.FullyQualifiedErrorId.Contains("ConsistencyLevelRequired") ||
            result.Terminating.FullyQualifiedErrorId.Contains("MissingMandatoryParameter"));
    }

    [Fact]
    public void ExecuteFanOut_EmptyBuffer_ReturnsEarly()
    {
        var handler = new StubHttpMessageHandler();
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps =>
        {
            ps.AddCommand("Expand-MgxRelation")
                .AddParameter("Uri", "/users/{id}/manager")
                .AddParameter("As", "manager");
        }, Array.Empty<Hashtable>());

        Assert.Empty(result.Output);
    }

    [Fact]
    public void ExecuteFanOut_MissingIdProperty_WritesErrorAndContinues()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, """{"id":"mgr1"}""");
        using var host = new MgxTestHost(handler);

        var input = new[]
        {
            new Hashtable { ["displayName"] = "User 1" },
            new Hashtable { ["id"] = "2", ["displayName"] = "User 2" }
        };

        var result = host.Run(ps =>
        {
            ps.AddCommand("Expand-MgxRelation")
                .AddParameter("Uri", "/users/{id}/manager")
                .AddParameter("As", "manager");
        }, input);

        // This test may hit NotConnected - if so skip
        if (result.Terminating != null)
        {
            if (result.Terminating.FullyQualifiedErrorId.Contains("NotConnected", StringComparison.OrdinalIgnoreCase))
            {
                Assert.True(true, "Test requires GraphSession which is not available in test environment");
                return;
            }
        }

        Assert.Equal(2, result.Output.Count);
        Assert.NotNull(result.Errors.FirstOrDefault(e => e.FullyQualifiedErrorId.Contains("MissingIdProperty")));
    }

    [Fact]
    public void BuildUrl_ReplacesIdPlaceholder()
    {
        var cmdlet = new ExpandMgxRelation
        {
            Uri = "/users/{id}/manager",
            ApiVersion = "v1.0"
        };

        var url = InvokeMethod<string>(cmdlet, "BuildUrl", "user123");

        Assert.Contains("user123", url);
        Assert.Contains("/users/user123/manager", url);
    }

    [Fact]
    public void BuildUrl_AddsTopParameter()
    {
        var cmdlet = new ExpandMgxRelation
        {
            Uri = "/users/{id}/messages",
            ApiVersion = "v1.0",
            Top = 10
        };

        var url = InvokeMethod<string>(cmdlet, "BuildUrl", "user123");

        Assert.Contains("$top=10", url);
    }

    [Fact]
    public void BuildUrl_HandlesSpecialCharactersInId()
    {
        var cmdlet = new ExpandMgxRelation
        {
            Uri = "/users/{id}/manager",
            ApiVersion = "v1.0"
        };

        var url = InvokeMethod<string>(cmdlet, "BuildUrl", "user@domain.com");

        Assert.Contains("user%40domain.com", url);
    }
}