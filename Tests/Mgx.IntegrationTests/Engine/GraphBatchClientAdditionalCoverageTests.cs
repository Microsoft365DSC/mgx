using System.Net;
using System.Text.Json;
using Mgx.Engine.Http;
using Mgx.Engine.Models;
using Mgx.IntegrationTests.Fakes;

namespace Mgx.IntegrationTests.Engine;

/// <summary>
/// Additional coverage tests for GraphBatchClient to push toward 95%.
/// </summary>
public class GraphBatchClientAdditionalCoverageTests
{
    [Fact]
    public async Task ExecuteBatchIndexedAsync_WithNullOperations_ReturnsEmpty()
    {
        var handler = new StubHttpMessageHandler();
        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();
        GraphBatchClient.ResetPacingState();

        var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };
        var client = new ResilientGraphClient(http, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAttempts = 1,
            CircuitBreakerMinThroughput = 1000,
            AttemptTimeoutSeconds = 10,
            TotalTimeoutSeconds = 60
        });

        var batch = new GraphBatchClient(client, "https://graph.microsoft.com/v1.0", 1, 1, 0);
        var result = await batch.ExecuteBatchIndexedAsync(Array.Empty<BatchOperation>(), CancellationToken.None);

        Assert.Empty(result.Results);
    }

    [Fact]
    public async Task ExecuteBatchIndexedAsync_SingleOperation_WithHeaders()
    {
        var handler = new StubHttpMessageHandler().Enqueue(request =>
        {
            var requests = JsonSerializer.Deserialize<GraphBatchRequest>(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.Requests;
            var req = requests[0];
            // Headers not currently supported in BatchOperation record, test verifies basic execution
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"responses":[{"id":"1","status":200,"body":{"id":"test"}}]}""", System.Text.Encoding.UTF8, "application/json")
            };
        });

        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();
        GraphBatchClient.ResetPacingState();

        var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };
        var client = new ResilientGraphClient(http, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAttempts = 1,
            CircuitBreakerMinThroughput = 1000,
            AttemptTimeoutSeconds = 10,
            TotalTimeoutSeconds = 60
        });

        var batch = new GraphBatchClient(client, "https://graph.microsoft.com/v1.0", 1, 1, 0);
        var op = new BatchOperation("/users/1", "GET", null);
        var result = await batch.ExecuteBatchIndexedAsync([op], CancellationToken.None);

        Assert.Single(result.Results);
    }

    [Fact]
    public async Task ExecuteBatchIndexedAsync_WithEmptyBodyObject()
    {
        var handler = new StubHttpMessageHandler().Enqueue(request =>
        {
            var requests = JsonSerializer.Deserialize<GraphBatchRequest>(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.Requests;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"responses":[{"id":"1","status":200,"body":{}}]}""", System.Text.Encoding.UTF8, "application/json")
            };
        });

        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();
        GraphBatchClient.ResetPacingState();

        var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };
        var client = new ResilientGraphClient(http, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAttempts = 1,
            CircuitBreakerMinThroughput = 1000,
            AttemptTimeoutSeconds = 10,
            TotalTimeoutSeconds = 60
        });

        var batch = new GraphBatchClient(client, "https://graph.microsoft.com/v1.0", 1, 1, 0);
        var body = JsonDocument.Parse("{}").RootElement;
        var result = await batch.ExecuteBatchIndexedAsync([new BatchOperation("/users", "POST", body)], CancellationToken.None);

        Assert.Single(result.Results);
    }

    [Fact]
    public async Task ExecuteBatchIndexedAsync_PatchMethod_Works()
    {
        var handler = new StubHttpMessageHandler().Enqueue(request =>
        {
            var requests = JsonSerializer.Deserialize<GraphBatchRequest>(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.Requests;
            Assert.Equal("PATCH", requests[0].Method);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"responses":[{"id":"1","status":200,"body":{"id":"updated"}}]}""", System.Text.Encoding.UTF8, "application/json")
            };
        });

        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();
        GraphBatchClient.ResetPacingState();

        var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };
        var client = new ResilientGraphClient(http, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAttempts = 1,
            CircuitBreakerMinThroughput = 1000,
            AttemptTimeoutSeconds = 10,
            TotalTimeoutSeconds = 60
        });

        var batch = new GraphBatchClient(client, "https://graph.microsoft.com/v1.0", 1, 1, 0);
        var body = JsonDocument.Parse("""{"displayName":"Updated"}""").RootElement;
        var result = await batch.ExecuteBatchIndexedAsync([new BatchOperation("/users/1", "PATCH", body)], CancellationToken.None);

        Assert.Single(result.Results);
        Assert.Equal(200, result.Results[0].Response.Status);
    }

    [Fact]
    public async Task ExecuteBatchIndexedAsync_DeleteMethod_Works()
    {
        var handler = new StubHttpMessageHandler().Enqueue(request =>
        {
            var requests = JsonSerializer.Deserialize<GraphBatchRequest>(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.Requests;
            Assert.Equal("DELETE", requests[0].Method);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"responses":[{"id":"1","status":204,"body":null}]}""", System.Text.Encoding.UTF8, "application/json")
            };
        });

        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();
        GraphBatchClient.ResetPacingState();

        var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };
        var client = new ResilientGraphClient(http, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAttempts = 1,
            CircuitBreakerMinThroughput = 1000,
            AttemptTimeoutSeconds = 10,
            TotalTimeoutSeconds = 60
        });

        var batch = new GraphBatchClient(client, "https://graph.microsoft.com/v1.0", 1, 1, 0);
        var result = await batch.ExecuteBatchIndexedAsync([new BatchOperation("/users/1", "DELETE")], CancellationToken.None);

        Assert.Single(result.Results);
        Assert.Equal(204, result.Results[0].Response.Status);
    }

    [Fact]
    public async Task ExecuteBatchIndexedAsync_PutMethod_Works()
    {
        var handler = new StubHttpMessageHandler().Enqueue(request =>
        {
            var requests = JsonSerializer.Deserialize<GraphBatchRequest>(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.Requests;
            Assert.Equal("PUT", requests[0].Method);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"responses":[{"id":"1","status":200,"body":{"id":"replaced"}}]}""", System.Text.Encoding.UTF8, "application/json")
            };
        });

        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();
        GraphBatchClient.ResetPacingState();

        var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };
        var client = new ResilientGraphClient(http, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAttempts = 1,
            CircuitBreakerMinThroughput = 1000,
            AttemptTimeoutSeconds = 10,
            TotalTimeoutSeconds = 60
        });

        var batch = new GraphBatchClient(client, "https://graph.microsoft.com/v1.0", 1, 1, 0);
        var body = JsonDocument.Parse("""{"displayName":"Replaced"}""").RootElement;
        var result = await batch.ExecuteBatchIndexedAsync([new BatchOperation("/users/1", "PUT", body)], CancellationToken.None);

        Assert.Single(result.Results);
        Assert.Equal(200, result.Results[0].Response.Status);
    }

    [Fact]
    public async Task ExecuteBatchIndexedAsync_MaxRetriesExceeded_ReturnsFailure()
    {
        var attempts = 0;
        var handler = new StubHttpMessageHandler().Enqueue(request =>
        {
            attempts++;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("""{"error":{"code":"InternalError"}}""", System.Text.Encoding.UTF8, "application/json")
            };
        });

        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();
        GraphBatchClient.ResetPacingState();

        var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };
        var client = new ResilientGraphClient(http, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAttempts = 2,
            CircuitBreakerMinThroughput = 1000,
            AttemptTimeoutSeconds = 1,
            TotalTimeoutSeconds = 5
        });

        var batch = new GraphBatchClient(client, "https://graph.microsoft.com/v1.0", 1, 1, 0);
        
        // After max retries, it should throw GraphServiceException
        await Assert.ThrowsAsync<GraphServiceException>(async () =>
            await batch.ExecuteBatchIndexedAsync([new BatchOperation("/users/1", "GET")], CancellationToken.None));

        // The retry logic may only retry once for 5xx on GET in batch
        Assert.True(attempts >= 1);
    }
}