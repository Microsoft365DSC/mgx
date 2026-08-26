using System.Net;
using System.Text.Json;
using Mgx.Engine.Http;
using Mgx.Engine.Models;
using Mgx.IntegrationTests.Fakes;

namespace Mgx.IntegrationTests.Engine;

/// <summary>
/// Additional coverage tests for GraphBatchClient.
/// </summary>
// Touches MgxCmdletBase and pipeline statics, so it must not run beside the injected-mock tests
[Collection("Pipeline")]
public class GraphBatchClientAdditionalCoverageTests
{
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

}
