using System;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Mgx.Engine;
using Mgx.Engine.Http;
using Mgx.Engine.Models;
using Mgx.Engine.Pagination;
using Mgx.IntegrationTests.Fakes;

namespace Mgx.IntegrationTests.Engine;

/// <summary>
/// Tests for GraphBatchClient.
/// </summary>
[Collection(ResilienceCollection.Name)]
public class GraphBatchClientCoverageTests
{
    private static ResilientGraphClientOptions TestOptions() => new()
    {
        NoRateLimit = true,
        MaxRetryAttempts = 3,
        CircuitBreakerMinThroughput = 1000,
        AttemptTimeoutSeconds = 10,
        TotalTimeoutSeconds = 60
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpResponseMessage Throttled()
    {
        var response = new HttpResponseMessage((HttpStatusCode)429)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        response.Headers.Add("Retry-After", "0");
        return response;
    }

    private static ResilientGraphClient CreateClient(StubHttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };
        return new ResilientGraphClient(http, TestOptions());
    }

    [Fact]
    public async Task ExecuteBatchIndexedAsync_EmptyOperations_ReturnsEmptyResult()
    {
        var handler = new StubHttpMessageHandler();
        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();

        var client = new GraphBatchClient(CreateClient(handler), "https://graph.microsoft.com/v1.0", 10, 5, 0)
        {
            VerboseWriter = _ => { }
        };

        var result = await client.ExecuteBatchIndexedAsync(new List<BatchOperation>(), Ct);

        Assert.Empty(result.Results);
        Assert.Equal(0, result.Telemetry.TotalRequests);
    }

    [Fact]
    public async Task ExecuteBatchIndexedAsync_SingleOperation_Success()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, """
            {
                "responses": [
                    { "id": "1", "status": 200, "body": { "id": "user1" } }
                ]
            }
            """);

        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();

        var client = new GraphBatchClient(CreateClient(handler), "https://graph.microsoft.com/v1.0", 10, 5, 0)
        {
            VerboseWriter = _ => { }
        };

        var ops = new List<BatchOperation>
        {
            new BatchOperation("/users/user1", "GET")
        };

        var result = await client.ExecuteBatchIndexedAsync(ops, Ct);

        Assert.Single(result.Results);
        Assert.Equal(200, result.Results[0].Response.Status);
        Assert.Equal(1, result.Telemetry.TotalRequests);
        Assert.Equal(1, result.Telemetry.Succeeded);
    }

    [Fact]
    public async Task ExecuteBatchIndexedAsync_FailedItem_TelemetryRecordsFailure()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, """
            {
                "responses": [
                    { "id": "1", "status": 404, "body": { "error": { "code": "NotFound" } } }
                ]
            }
            """);

        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();

        var client = new GraphBatchClient(CreateClient(handler), "https://graph.microsoft.com/v1.0", 10, 5, 0)
        {
            VerboseWriter = _ => { }
        };

        var ops = new List<BatchOperation>
        {
            new BatchOperation("/users/missing", "GET")
        };

        var result = await client.ExecuteBatchIndexedAsync(ops, Ct);

        Assert.Single(result.Results);
        Assert.Equal(404, result.Results[0].Response.Status);
        Assert.Equal(1, result.Telemetry.Failed);
    }

    [Fact]
    public async Task ExecuteBatchIndexedAsync_DuplicateUrls_KeepsSeparate()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, """
            {
                "responses": [
                    { "id": "1", "status": 200, "body": { "id": "user1" } },
                    { "id": "2", "status": 200, "body": { "id": "user1" } }
                ]
            }
            """);

        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();

        var client = new GraphBatchClient(CreateClient(handler), "https://graph.microsoft.com/v1.0", 10, 5, 0)
        {
            VerboseWriter = _ => { }
        };

        var ops = new List<BatchOperation>
        {
            new BatchOperation("/users/user1", "GET"),
            new BatchOperation("/users/user1", "GET")
        };

        var result = await client.ExecuteBatchIndexedAsync(ops, Ct);

        Assert.Equal(2, result.Results.Count);
        Assert.Equal("/users/user1", result.Results[0].Operation.Url);
        Assert.Equal("/users/user1", result.Results[1].Operation.Url);
    }

    [Fact]
    public async Task ExecuteBatchIndexedAsync_EmptyResponsesArray_Throws()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, """
            { "responses": [] }
            """);

        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();

        var client = new GraphBatchClient(CreateClient(handler), "https://graph.microsoft.com/v1.0", 10, 5, 0)
        {
            VerboseWriter = _ => { }
        };

        var ops = new List<BatchOperation>
        {
            new BatchOperation("/users/user1", "GET")
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ExecuteBatchIndexedAsync(ops, Ct));
    }
}