using System.Net;
using Mgx.Engine.Models;

namespace Mgx.IntegrationTests.Engine;

/// <summary>
/// Error-body parsing must never throw. The constructor runs while a caller is already building
/// an exception, and 2.1's content path follows a redirect to SharePoint, OneDrive and CDN hosts
/// that are not Graph and do not emit the OData error envelope.
/// </summary>
public class ErrorBodyShapeTests
{
    [Theory]
    [InlineData("""{"error":{"code":"itemNotFound","message":"Item not found"}}""")]   // the normal shape
    [InlineData("""{"error":"just a string"}""")]                                      // error is not an object
    [InlineData("""{"error":{"code":404,"message":"numeric code"}}""")]                // code is not a string
    [InlineData("""{"error":{"message":{"value":"nested"}}}""")]                       // message is not a string
    [InlineData("""["an","array","root"]""")]                                          // root is an array
    [InlineData("\"a bare json string\"")]                                             // root is a string
    [InlineData("12345")]                                                              // root is a number
    [InlineData("null")]                                                               // root is null
    [InlineData("<html><body>503 from a CDN</body></html>")]                           // not JSON at all
    [InlineData("")]                                                                   // empty
    public void Never_throws_whatever_the_body_shape(string body)
    {
        var ex = new GraphServiceException(HttpStatusCode.ServiceUnavailable, body);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void Still_extracts_a_graph_shaped_error()
    {
        var ex = new GraphServiceException(
            HttpStatusCode.NotFound,
            """{"error":{"code":"itemNotFound","message":"Item not found"}}""");
        Assert.Contains("itemNotFound", ex.Message);
        Assert.Contains("Item not found", ex.Message);
    }

    [Fact]
    public void Extracts_a_message_nested_under_value()
    {
        var ex = new GraphServiceException(
            HttpStatusCode.BadRequest,
            """{"error":{"code":"badRequest","message":{"value":"the real text"}}}""");
        Assert.Contains("the real text", ex.Message);
    }

    /// <summary>
    /// The theory above only asserts the message is non-empty, and the backstop catch at the
    /// bottom of FormatAndExtract returns the status line for anything that throws - so all ten
    /// cases stay green with every ValueKind guard deleted. The guards are two independently
    /// sufficient controls with the catch, and the theory pins only their disjunction.
    /// Pin the guards themselves: a body odd in ONE place must still yield the parts that are
    /// well formed, instead of falling back wholesale.
    /// </summary>
    [Fact]
    public void A_body_odd_in_one_place_still_yields_the_parts_that_are_well_formed()
    {
        var ex = new GraphServiceException(
            HttpStatusCode.ServiceUnavailable,
            """{"error":{"code":404,"message":"numeric code"}}""");
        Assert.Contains("numeric code", ex.Message);
        Assert.Null(ex.ErrorCode);
    }

    [Fact]
    public void Falls_back_to_the_status_line_when_the_shape_is_wrong()
    {
        var ex = new GraphServiceException(HttpStatusCode.ServiceUnavailable, """{"error":404}""");
        Assert.Contains("503", ex.Message);
    }

    [Fact]
    public void Carries_the_inner_error_message()
    {
        var ex = new GraphServiceException(
            HttpStatusCode.BadRequest,
            """{"error":{"code":"","message":"The request is invalid.","innerError":{"message":"configuration.DisplayName : The DisplayName field is required.","date":"2026-09-21T19:08:59"}}}""");

        Assert.Equal("configuration.DisplayName : The DisplayName field is required.", ex.InnerErrorMessage);
        Assert.Contains("The request is invalid.", ex.Message);
        Assert.Contains("configuration.DisplayName", ex.Message);
    }

    [Fact]
    public void Takes_the_deepest_message_of_a_nested_inner_error()
    {
        var ex = new GraphServiceException(
            HttpStatusCode.BadRequest,
            """{"error":{"code":"BadRequest","message":"top","innerError":{"message":"middle","innerError":{"message":"deepest"}}}}""");

        Assert.Equal("deepest", ex.InnerErrorMessage);
        Assert.Contains("deepest", ex.Message);
    }

    [Fact]
    public void Leaves_the_message_alone_when_the_inner_error_carries_no_message()
    {
        var ex = new GraphServiceException(
            HttpStatusCode.BadRequest,
            """{"error":{"code":"Request_BadRequest","message":"Invalid request","innerError":{"date":"2024-01-01T00:00:00","request-id":"12345"}}}""");

        Assert.Null(ex.InnerErrorMessage);
        Assert.StartsWith("Request_BadRequest: Invalid request", ex.Message);
    }

    [Fact]
    public void Does_not_repeat_an_inner_error_that_matches_the_message()
    {
        var ex = new GraphServiceException(
            HttpStatusCode.BadRequest,
            """{"error":{"code":"BadRequest","message":"same text","innerError":{"message":"same text"}}}""");

        Assert.Equal("same text", ex.InnerErrorMessage);
        Assert.Equal(1, ex.Message.Split("same text").Length - 1);
    }

    [Fact]
    public void Stops_walking_a_chain_nested_past_any_real_depth()
    {
        var ex = new GraphServiceException(
            HttpStatusCode.BadRequest,
            """{"error":{"code":"BadRequest","message":"top","innerError":{"message":"a","innerError":{"message":"b","innerError":{"message":"c","innerError":{"message":"d","innerError":{"message":"e","innerError":{"message":"f","innerError":{"message":"g","innerError":{"message":"h","innerError":{"message":"i"}}}}}}}}}}}""");

        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }
}
