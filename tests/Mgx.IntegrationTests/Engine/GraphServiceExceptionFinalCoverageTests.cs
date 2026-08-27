using System.Net;
using Mgx.Engine.Models;

namespace Mgx.IntegrationTests.Engine;

/// <summary>
/// Tests for GraphServiceException.
/// </summary>
public class GraphServiceExceptionFinalCoverageTests
{
    [Fact]
    public void Constructor_WithMinimalBody_ParsesCorrectly()
    {
        var ex = new GraphServiceException(HttpStatusCode.BadRequest, "{}");

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Contains("BadRequest", ex.Message);
    }

    [Fact]
    public void Constructor_WithNewlinesInBody_HandlesCorrectly()
    {
        var body = "{\n  \"error\": {\n    \"code\": \"Test\"\n  }\n}";
        var ex = new GraphServiceException(HttpStatusCode.NotFound, body);

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Contains("Test", ex.Message);
    }

    [Fact]
    public void Constructor_WithComplexGraphError_FormatsMessage()
    {
        var body = """{"error":{"code":"Request_BadRequest","message":"Invalid request","innerError":{"date":"2024-01-01T00:00:00","request-id":"12345","client-request-id":"67890"}}}""";
        var ex = new GraphServiceException(HttpStatusCode.BadRequest, body);

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Contains("Request_BadRequest", ex.Message);
        Assert.Contains("Invalid request", ex.Message);
    }

    [Fact]
    public void Equals_Null_ReturnsFalse()
    {
        var ex = new GraphServiceException(HttpStatusCode.NotFound, """{"error":{"code":"NotFound"}}""");

        Assert.False(ex.Equals(null));
    }

    [Fact]
    public void Equals_DifferentType_ReturnsFalse()
    {
        var ex = new GraphServiceException(HttpStatusCode.NotFound, """{"error":{"code":"NotFound"}}""");

        Assert.False(ex.Equals("not an exception"));
    }

    [Fact]
    public void GetHashCode_DifferentObjects_DifferentHashes()
    {
        var ex1 = new GraphServiceException(HttpStatusCode.NotFound, """{"error":{"code":"A"}}""");
        var ex2 = new GraphServiceException(HttpStatusCode.NotFound, """{"error":{"code":"B"}}""");

        Assert.NotEqual(ex1.GetHashCode(), ex2.GetHashCode());
    }

    [Fact]
    public void ToString_ContainsStatusCode()
    {
        var ex = new GraphServiceException(HttpStatusCode.NotFound, """{"error":{"code":"NotFound"}}""");

        var str = ex.ToString();
        Assert.Contains("NotFound", str);
    }

    [Fact]
    public void Constructor_WithEmptyBody_ReturnsHttpStatusMessage()
    {
        var ex = new GraphServiceException(HttpStatusCode.NotFound, "");

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Contains("404", ex.Message);
        Assert.Contains("NotFound", ex.Message);
        Assert.Null(ex.ErrorCode);
    }

    [Fact]
    public void Constructor_WithNullBody_ReturnsHttpStatusMessage()
    {
        var ex = new GraphServiceException(HttpStatusCode.InternalServerError, null!);

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Contains("500", ex.Message);
        Assert.Contains("InternalServerError", ex.Message);
        Assert.Null(ex.ErrorCode);
    }

    [Fact]
    public void Constructor_WithInvalidJson_ReturnsHttpStatusMessage()
    {
        var ex = new GraphServiceException(HttpStatusCode.BadRequest, "{ not valid json }");

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Contains("400", ex.Message);
        Assert.Contains("BadRequest", ex.Message);
        Assert.Null(ex.ErrorCode);
    }

    [Fact]
    public void Constructor_WithErrorCodeOnly_ReturnsCodeWithEmptyMessage()
    {
        var body = """{"error":{"code":"TestCode"}}""";
        var ex = new GraphServiceException(HttpStatusCode.BadRequest, body);

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Equal("TestCode", ex.ErrorCode);
        Assert.Contains("TestCode:", ex.Message);
    }

    [Fact]
    public void Constructor_WithMessageOnly_ReturnsMessageWithoutCodePrefix()
    {
        var body = """{"error":{"message":"Just a message"}}""";
        var ex = new GraphServiceException(HttpStatusCode.NotFound, body);

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Null(ex.ErrorCode);
        Assert.Contains("Just a message", ex.Message);
    }

    [Fact]
    public void Constructor_WithKnownErrorCode_IncludesGuidance()
    {
        var body = """{"error":{"code":"Authorization_RequestDenied"}}""";
        var ex = new GraphServiceException(HttpStatusCode.Forbidden, body);

        Assert.Contains("Hint:", ex.Message);
        Assert.Contains("Get-MgContext", ex.Message);
    }

    [Fact]
    public void Constructor_WithRequest_ResourceNotFound_IncludesGuidance()
    {
        var body = """{"error":{"code":"Request_ResourceNotFound"}}""";
        var ex = new GraphServiceException(HttpStatusCode.NotFound, body);

        Assert.Contains("Hint:", ex.Message);
        Assert.Contains("SkipNotFound", ex.Message);
    }

    [Fact]
    public void Constructor_WithRequest_BadRequest_IncludesGuidance()
    {
        var body = """{"error":{"code":"Request_BadRequest"}}""";
        var ex = new GraphServiceException(HttpStatusCode.BadRequest, body);

        Assert.Contains("Hint:", ex.Message);
        Assert.Contains("ConsistencyLevel", ex.Message);
    }

    [Fact]
    public void Constructor_WithInvalidAuthenticationToken_IncludesGuidance()
    {
        var body = """{"error":{"code":"InvalidAuthenticationToken"}}""";
        var ex = new GraphServiceException(HttpStatusCode.Unauthorized, body);

        Assert.Contains("Hint:", ex.Message);
        Assert.Contains("Connect-MgGraph", ex.Message);
    }

    [Fact]
    public void Constructor_WithAuthentication_ExpiredToken_IncludesGuidance()
    {
        var body = """{"error":{"code":"Authentication_ExpiredToken"}}""";
        var ex = new GraphServiceException(HttpStatusCode.Unauthorized, body);

        Assert.Contains("Hint:", ex.Message);
        Assert.Contains("Connect-MgGraph", ex.Message);
    }

    [Fact]
    public void Constructor_WithErrorAccessDenied_IncludesGuidance()
    {
        var body = """{"error":{"code":"ErrorAccessDenied"}}""";
        var ex = new GraphServiceException(HttpStatusCode.Forbidden, body);

        Assert.Contains("Hint:", ex.Message);
        Assert.Contains("permissions-reference", ex.Message);
    }

    [Fact]
    public void Constructor_WithForbidden_IncludesGuidance()
    {
        var body = """{"error":{"code":"Forbidden"}}""";
        var ex = new GraphServiceException(HttpStatusCode.Forbidden, body);

        Assert.Contains("Hint:", ex.Message);
        Assert.Contains("admin consent", ex.Message);
    }

    [Fact]
    public void Constructor_WithTooManyRequests_IncludesGuidance()
    {
        var body = """{"error":{"code":"TooManyRequests"}}""";
        var ex = new GraphServiceException((HttpStatusCode)429, body);

        Assert.Contains("Hint:", ex.Message);
        Assert.Contains("Throttled", ex.Message);
    }

    [Fact]
    public void Constructor_WithActivityLimitReached_IncludesGuidance()
    {
        var body = """{"error":{"code":"activityLimitReached"}}""";
        var ex = new GraphServiceException((HttpStatusCode)429, body);

        Assert.Contains("Hint:", ex.Message);
        Assert.Contains("Throttled", ex.Message);
    }

    [Fact]
    public void Constructor_WithServiceNotAvailable_IncludesGuidance()
    {
        var body = """{"error":{"code":"ServiceNotAvailable"}}""";
        var ex = new GraphServiceException(HttpStatusCode.ServiceUnavailable, body);

        Assert.Contains("Hint:", ex.Message);
        Assert.Contains("status.cloud.microsoft.com", ex.Message);
    }

    [Fact]
    public void Constructor_WithBadRequest_IncludesGuidance()
    {
        var body = """{"error":{"code":"BadRequest"}}""";
        var ex = new GraphServiceException(HttpStatusCode.BadRequest, body);

        Assert.Contains("Hint:", ex.Message);
        Assert.Contains("syntax", ex.Message);
    }

    [Fact]
    public void Constructor_WithUnknownErrorCode_NoGuidance()
    {
        var body = """{"error":{"code":"Unknown_Code_123"}}""";
        var ex = new GraphServiceException(HttpStatusCode.BadRequest, body);

        Assert.DoesNotContain("Hint:", ex.Message);
    }

    [Fact]
    public void GetGuidanceForCode_Authorization_RequestDenied_ReturnsGuidance()
    {
        var guidance = GraphServiceException.GetGuidanceForCode("Authorization_RequestDenied");
        Assert.NotNull(guidance);
        Assert.Contains("Get-MgContext", guidance);
    }

    [Fact]
    public void GetGuidanceForCode_Request_ResourceNotFound_ReturnsGuidance()
    {
        var guidance = GraphServiceException.GetGuidanceForCode("Request_ResourceNotFound");
        Assert.NotNull(guidance);
        Assert.Contains("SkipNotFound", guidance);
    }

    [Fact]
    public void GetGuidanceForCode_Request_BadRequest_ReturnsGuidance()
    {
        var guidance = GraphServiceException.GetGuidanceForCode("Request_BadRequest");
        Assert.NotNull(guidance);
        Assert.Contains("ConsistencyLevel", guidance);
    }

    [Fact]
    public void GetGuidanceForCode_InvalidAuthenticationToken_ReturnsGuidance()
    {
        var guidance = GraphServiceException.GetGuidanceForCode("InvalidAuthenticationToken");
        Assert.NotNull(guidance);
        Assert.Contains("Connect-MgGraph", guidance);
    }

    [Fact]
    public void GetGuidanceForCode_Authentication_ExpiredToken_ReturnsGuidance()
    {
        var guidance = GraphServiceException.GetGuidanceForCode("Authentication_ExpiredToken");
        Assert.NotNull(guidance);
        Assert.Contains("Connect-MgGraph", guidance);
    }

    [Fact]
    public void GetGuidanceForCode_ErrorAccessDenied_ReturnsGuidance()
    {
        var guidance = GraphServiceException.GetGuidanceForCode("ErrorAccessDenied");
        Assert.NotNull(guidance);
        Assert.Contains("permissions-reference", guidance);
    }

    [Fact]
    public void GetGuidanceForCode_Forbidden_ReturnsGuidance()
    {
        var guidance = GraphServiceException.GetGuidanceForCode("Forbidden");
        Assert.NotNull(guidance);
        Assert.Contains("admin consent", guidance);
    }

    [Fact]
    public void GetGuidanceForCode_TooManyRequests_ReturnsGuidance()
    {
        var guidance = GraphServiceException.GetGuidanceForCode("TooManyRequests");
        Assert.NotNull(guidance);
        Assert.Contains("Throttled", guidance);
    }

    [Fact]
    public void GetGuidanceForCode_ActivityLimitReached_ReturnsGuidance()
    {
        var guidance = GraphServiceException.GetGuidanceForCode("activityLimitReached");
        Assert.NotNull(guidance);
        Assert.Contains("Throttled", guidance);
    }

    [Fact]
    public void GetGuidanceForCode_ServiceNotAvailable_ReturnsGuidance()
    {
        var guidance = GraphServiceException.GetGuidanceForCode("ServiceNotAvailable");
        Assert.NotNull(guidance);
        Assert.Contains("status.cloud.microsoft.com", guidance);
    }

    [Fact]
    public void GetGuidanceForCode_BadRequest_ReturnsGuidance()
    {
        var guidance = GraphServiceException.GetGuidanceForCode("BadRequest");
        Assert.NotNull(guidance);
        Assert.Contains("syntax", guidance);
    }

    [Fact]
    public void GetGuidanceForCode_UnknownCode_ReturnsNull()
    {
        var guidance = GraphServiceException.GetGuidanceForCode("Unknown_Code");
        Assert.Null(guidance);
    }

    [Fact]
    public void GetGuidanceForCode_NullCode_ReturnsNull()
    {
        var guidance = GraphServiceException.GetGuidanceForCode(null);
        Assert.Null(guidance);
    }

    [Fact]
    public void Constructor_WithErrorObjectButNoCodeOrMessage_FallsBackToHttpStatus()
    {
        var body = """{"error":{}}""";
        var ex = new GraphServiceException(HttpStatusCode.NotFound, body);

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Contains("404", ex.Message);
        Assert.Contains("NotFound", ex.Message);
        Assert.Null(ex.ErrorCode);
    }

    [Fact]
    public void Constructor_WithErrorCodeAndEmptyMessage_FallsBackToCode()
    {
        var body = """{"error":{"code":"TestCode","message":""}}""";
        var ex = new GraphServiceException(HttpStatusCode.NotFound, body);

        Assert.Equal("TestCode", ex.ErrorCode);
        Assert.Contains("TestCode:", ex.Message);
    }
}