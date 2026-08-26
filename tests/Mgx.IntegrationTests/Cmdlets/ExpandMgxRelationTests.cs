using System.Collections;
using System.Management.Automation;
using System.Net;
using System.Text.Json;
using Mgx.Cmdlets.Cmdlets.Expand;
using Mgx.IntegrationTests.Fakes;
using Mgx.IntegrationTests.Infrastructure;

namespace Mgx.IntegrationTests.Cmdlets;

/// <summary>
/// Tests for Expand-MgxRelation cmdlet.
/// </summary>
// Hosts cmdlets through MgxTestHost, which sets the shared transport and client options
[Collection("Pipeline")]
public class ExpandMgxRelationTests
{
    // Use reflection to test private methods - cast to non-nullable since we control the test setup
    private static T InvokeMethod<T>(object target, string methodName, params object?[] parameters)
    {
        var method = target.GetType().GetMethod(methodName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);
        var result = method!.Invoke(target, parameters);
        return (T)result!;
    }

    private static T InvokeMethod<T>(Type type, string methodName, params object?[] parameters)
    {
        var method = type.GetMethod(methodName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var result = method!.Invoke(null, parameters);
        return (T)result!;
    }

    [Fact]
    public void ProcessRecord_SearchRequiresConsistencyLevel_ThrowsError()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, """{"value":[]}""");
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps =>
        {
            ps.AddCommand("Expand-MgxRelation")
                .AddParameter("Uri", "/users/{id}/messages?$search=test")
                .AddParameter("As", "messages")
                .AddParameter("InputObject", new Hashtable { ["id"] = "123" });
        });

        Assert.NotNull(result.Terminating);
        Assert.Contains("ConsistencyLevelRequired", result.Terminating.FullyQualifiedErrorId);
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
    public void GetStatusCodeFromException_ReturnsGraphServiceExceptionStatus()
    {
        var cmdlet = new ExpandMgxRelation();
        var ex = new Mgx.Engine.Models.GraphServiceException(HttpStatusCode.NotFound, "Not found");

        var status = InvokeMethod<HttpStatusCode?>(cmdlet, "GetStatusCodeFromException", ex);

        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Fact]
    public void GetStatusCodeFromException_ReturnsHttpRequestExceptionStatus()
    {
        var cmdlet = new ExpandMgxRelation();
        var ex = new HttpRequestException("Error", null, HttpStatusCode.ServiceUnavailable);

        var status = InvokeMethod<HttpStatusCode?>(cmdlet, "GetStatusCodeFromException", ex);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
    }

    [Fact]
    public void MapStatusToCategory_MapsCommonCodes()
    {
        var category = InvokeMethod<ErrorCategory>(typeof(Mgx.Cmdlets.Base.MgxCmdletBase), "MapStatusToCategory", HttpStatusCode.NotFound);
        Assert.Equal(ErrorCategory.ObjectNotFound, category);

        category = InvokeMethod<ErrorCategory>(typeof(Mgx.Cmdlets.Base.MgxCmdletBase), "MapStatusToCategory", HttpStatusCode.Forbidden);
        Assert.Equal(ErrorCategory.PermissionDenied, category);

        category = InvokeMethod<ErrorCategory>(typeof(Mgx.Cmdlets.Base.MgxCmdletBase), "MapStatusToCategory", HttpStatusCode.BadRequest);
        Assert.Equal(ErrorCategory.InvalidArgument, category);

        category = InvokeMethod<ErrorCategory>(typeof(Mgx.Cmdlets.Base.MgxCmdletBase), "MapStatusToCategory", HttpStatusCode.ServiceUnavailable);
        Assert.Equal(ErrorCategory.NotSpecified, category);
    }
}