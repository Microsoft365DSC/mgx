using System.Net;
using System.Text.Json;
using Mgx.Engine.Http;
using Mgx.IntegrationTests.Fakes;

namespace Mgx.IntegrationTests.Engine;

/// <summary>
/// Additional coverage tests for ResilientGraphClient to push toward 95%.
/// </summary>
public class ResilientGraphClientAdditionalCoverageTests
{
    [Fact]
    public async Task SendAsync_WithPutMethod_Works()
    {
        var handler = new StubHttpMessageHandler().Enqueue(request =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"updated"}""", System.Text.Encoding.UTF8, "application/json")
            };
        });

        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };
        var client = new ResilientGraphClient(http, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAttempts = 1,
            CircuitBreakerMinThroughput = 1000,
            AttemptTimeoutSeconds = 10,
            TotalTimeoutSeconds = 60
        });

        var content = new StringContent("""{"displayName":"Updated"}""", System.Text.Encoding.UTF8, "application/json");
        var response = await client.SendAsync(HttpMethod.Put, "https://graph.microsoft.com/v1.0/users/1", content, null, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SendAsync_WithDeleteMethod_Works()
    {
        var handler = new StubHttpMessageHandler().Enqueue(request =>
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };
        var client = new ResilientGraphClient(http, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAttempts = 1,
            CircuitBreakerMinThroughput = 1000,
            AttemptTimeoutSeconds = 10,
            TotalTimeoutSeconds = 60
        });

        var response = await client.SendAsync(HttpMethod.Delete, "https://graph.microsoft.com/v1.0/users/1", null, null, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task SendAsync_WithNullBody_Works()
    {
        var handler = new StubHttpMessageHandler().Enqueue(request =>
        {
            Assert.Null(request.Content);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"test"}""", System.Text.Encoding.UTF8, "application/json")
            };
        });

        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };
        var client = new ResilientGraphClient(http, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAttempts = 1,
            CircuitBreakerMinThroughput = 1000,
            AttemptTimeoutSeconds = 10,
            TotalTimeoutSeconds = 60
        });

        var response = await client.SendAsync(HttpMethod.Get, "https://graph.microsoft.com/v1.0/users/1", null, null, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SendAsync_WithCustomHeaders_IncludesHeaders()
    {
        var capturedHeaders = new Dictionary<string, string>();
        var handler = new StubHttpMessageHandler().Enqueue(request =>
        {
            foreach (var h in request.Headers)
                capturedHeaders[h.Key] = string.Join(",", h.Value);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"test"}""", System.Text.Encoding.UTF8, "application/json")
            };
        });

        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };
        var client = new ResilientGraphClient(http, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAttempts = 1,
            CircuitBreakerMinThroughput = 1000,
            AttemptTimeoutSeconds = 10,
            TotalTimeoutSeconds = 60
        });

        var headers = new Dictionary<string, string> { ["X-Custom"] = "test-value" };
        var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        var response = await client.SendAsync(HttpMethod.Get, "https://graph.microsoft.com/v1.0/users/1", content, headers, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(capturedHeaders.ContainsKey("X-Custom"));
    }

    [Fact]
    public async Task SendAsync_WithCancellation_Cancels()
    {
        var handler = new StubHttpMessageHandler().Enqueue(request =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"test"}""", System.Text.Encoding.UTF8, "application/json")
            };
        });

        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };
        var client = new ResilientGraphClient(http, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAttempts = 1,
            CircuitBreakerMinThroughput = 1000,
            AttemptTimeoutSeconds = 10,
            TotalTimeoutSeconds = 60
        });

        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await client.SendAsync(HttpMethod.Get, "https://graph.microsoft.com/v1.0/users/1", null, null, cts.Token));
    }

    [Fact]
    public async Task GetCollectionPageAsync_WithNullHeaders_Works()
    {
        var handler = new StubHttpMessageHandler().Enqueue(request =>
        {
            Assert.False(request.Headers.TryGetValues("ConsistencyLevel", out _));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"value":[{"id":"1"}],"@odata.nextLink":null}""", System.Text.Encoding.UTF8, "application/json")
            };
        });

        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };
        var client = new ResilientGraphClient(http, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAttempts = 1,
            CircuitBreakerMinThroughput = 1000,
            AttemptTimeoutSeconds = 10,
            TotalTimeoutSeconds = 60
        });

        var page = await client.GetCollectionPageAsync("https://graph.microsoft.com/v1.0/users", CancellationToken.None, null);

        Assert.NotNull(page);
        Assert.Single(page.Value);
    }

    [Fact]
    public async Task GetCollectionPageAsync_WithNextLink_ReturnsNextLink()
    {
        var handler = new StubHttpMessageHandler().Enqueue(request =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"value":[{"id":"1"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=abc"}""", System.Text.Encoding.UTF8, "application/json")
            };
        });

        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };
        var client = new ResilientGraphClient(http, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAttempts = 1,
            CircuitBreakerMinThroughput = 1000,
            AttemptTimeoutSeconds = 10,
            TotalTimeoutSeconds = 60
        });

        var page = await client.GetCollectionPageAsync("https://graph.microsoft.com/v1.0/users", CancellationToken.None, null);

        Assert.NotNull(page);
        Assert.Single(page.Value);
        Assert.Equal("https://graph.microsoft.com/v1.0/users?$skiptoken=abc", page.NextLink);
    }

    [Fact]
    public async Task SendAsync_BodySizeLimit_ThrowsOnLargeBody()
    {
        var handler = new StubHttpMessageHandler().Enqueue(request =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"test"}""", System.Text.Encoding.UTF8, "application/json")
            };
        });

        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };
        var client = new ResilientGraphClient(http, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAttempts = 1,
            CircuitBreakerMinThroughput = 1000,
            AttemptTimeoutSeconds = 10,
            TotalTimeoutSeconds = 60
        });

        var largeString = new string('x', 5000000);
        var largeBody = new StringContent("{\"data\":\"" + largeString + "\"}", System.Text.Encoding.UTF8, "application/json");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.SendAsync(HttpMethod.Post, "https://graph.microsoft.com/v1.0/users", largeBody, null, CancellationToken.None));
    }
}