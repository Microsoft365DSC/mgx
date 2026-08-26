using System.Collections;
using System.Management.Automation;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mgx.Cmdlets.Cmdlets.Batch;
using Mgx.IntegrationTests.Fakes;
using Mgx.IntegrationTests.Infrastructure;

namespace Mgx.IntegrationTests.Cmdlets;

/// <summary>
/// Tests for Invoke-MgxBatchRequest cmdlet.
/// </summary>
// Hosts cmdlets through MgxTestHost, which sets the shared transport and client options
[Collection("Pipeline")]
public class InvokeMgxBatchRequestTests
{
    // Use reflection to test private methods - cast to non-nullable since we control the test setup
    private static T InvokeMethod<T>(object target, string methodName, params object?[] parameters)
    {
        var method = target.GetType().GetMethod(methodName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);
        var result = method!.Invoke(target, parameters);
        return (T)result!;
    }
    [Fact]
    public void ProcessRecord_SearchRequiresConsistencyLevel_ThrowsError()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, """{"responses":[]}""");
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps =>
        {
            ps.AddCommand("Invoke-MgxBatchRequest")
                .AddParameter("Uri", "/users/{id}/messages?$search=test")
                .AddParameter("Method", "GET");
        });

        Assert.NotNull(result.Terminating);
        Assert.Contains("ConsistencyLevelRequired", result.Terminating.FullyQualifiedErrorId);
    }

    [Fact]
    public void NormalizeToRelativeUrl_HandlesAbsoluteUrl()
    {
        var cmdlet = new InvokeMgxBatchRequest { ApiVersion = "v1.0" };
        var url = InvokeMethod<string>(cmdlet, "NormalizeToRelativeUrl", "https://graph.microsoft.com/v1.0/users/123");
        Assert.Equal("/users/123", url);
    }

    [Fact]
    public void NormalizeToRelativeUrl_HandlesRelativeUrl()
    {
        var cmdlet = new InvokeMgxBatchRequest { ApiVersion = "v1.0" };
        var url = InvokeMethod<string>(cmdlet, "NormalizeToRelativeUrl", "/users/123");
        Assert.Equal("/users/123", url);
    }

    [Fact]
    public void NormalizeToRelativeUrl_HandlesBetaUrl()
    {
        var cmdlet = new InvokeMgxBatchRequest { ApiVersion = "beta" };
        var url = InvokeMethod<string>(cmdlet, "NormalizeToRelativeUrl", "https://graph.microsoft.com/beta/groups/123");
        Assert.Equal("/groups/123", url);
    }


    [Fact]
    public void A_failed_batch_item_is_reported_as_an_error_without_a_dead_letter_path()
    {
        // The per-item errors are what makes -ErrorAction Stop trip and $Error fill in
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, """
            {"responses":[
              {"id":"1","status":200,"body":{"id":"u0"}},
              {"id":"2","status":403,"body":{"error":{"code":"Authorization_RequestDenied","message":"denied"}}}
            ]}
            """);
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps =>
        {
            ps.AddCommand("Invoke-MgxBatchRequest")
                .AddParameter("Uri", new[] { "/users/u0", "/users/u1" })
                .AddParameter("Method", "GET");
        });

        Assert.Null(result.Terminating);
        Assert.Contains(result.Errors, e => e.FullyQualifiedErrorId.Contains("BatchItemError"));
        Assert.Contains(result.Errors, e => e.Exception.Message.Contains("Authorization_RequestDenied"));
    }
}
