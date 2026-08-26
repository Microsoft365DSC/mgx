using System.Net;
using System.Text;
using System.Text.Json;
using Mgx.Engine.Http;
using Mgx.Engine.Models;
using Mgx.IntegrationTests.Fakes;

namespace Mgx.IntegrationTests.Engine;

/// <summary>
/// Additional tests for GraphBatchClient.
/// </summary>
[Collection(ResilienceCollection.Name)]
public class GraphBatchClientCoverageTests2
{
    private const string BaseUrl = "https://graph.microsoft.com/v1.0";

    private static (GraphBatchClient Batch, ResilientGraphClient Client, HttpClient Http) NewBatchClient(
        HttpMessageHandler handler, int batchItemsPerSecond = 0)
    {
        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();
        GraphBatchClient.ResetPacingState();

        var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };
        var options = new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAttempts = 3,
            CircuitBreakerMinThroughput = 1000,
            AttemptTimeoutSeconds = 10,
            TotalTimeoutSeconds = 60
        };
        var client = new ResilientGraphClient(http, options);
        return (new GraphBatchClient(client, "https://graph.microsoft.com/v1.0", maxRetryAfterSeconds: 1,
            batchChunkConcurrency: 1, batchItemsPerSecond), client, http);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static List<GraphBatchRequestItem> ReadRequests(HttpRequestMessage request)
    {
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        return JsonSerializer.Deserialize<GraphBatchRequest>(body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.Requests;
    }

    private static string Item(string id, int status, string body = "{}", string? retryAfter = null)
    {
        var headers = retryAfter is null ? "" : ",\"headers\":{\"Retry-After\":\"" + retryAfter + "\"}";
        return "{\"id\":\"" + id + "\",\"status\":" + status + headers + ",\"body\":" + body + "}";
    }

    private static string Batch(IEnumerable<string> items) =>
        "{\"responses\":[" + string.Join(",", items) + "]}";

    private static string RespondAllOk(List<GraphBatchRequestItem> requests) =>
        Batch(requests.Select(r => Item(r.Id, 200, "{\"url\":\"" + r.Url + "\"}")));

    [Fact]
    public async Task MaxRetryAfterSeconds_CapsRetryDelay()
    {
        var handler = new StubHttpMessageHandler()
            .Enqueue(request => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(100)) }
            })
            .Enqueue(request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"responses":[{"id":"1","status":200,"body":{}}]}""", Encoding.UTF8, "application/json")
            });

        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();

        var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };
        var client = new ResilientGraphClient(http, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAttempts = 3,
            MaxRetryAfterSeconds = 2,
            CircuitBreakerMinThroughput = 1000,
            AttemptTimeoutSeconds = 10,
            TotalTimeoutSeconds = 60
        });

        var batch = new GraphBatchClient(client, "https://graph.microsoft.com/v1.0", 2, 1, 0);

        var result = await batch.ExecuteBatchIndexedAsync([new BatchOperation("/users/u0")], CancellationToken.None);

        Assert.Equal(200, result.Results[0].Response.Status);
    }

    [Fact]
    public async Task ExecuteBatchIndexedAsync_UrlNormalization_HandlesAbsoluteUrls()
    {
        var handler = new StubHttpMessageHandler().Enqueue(request =>
        {
            var requests = JsonSerializer.Deserialize<GraphBatchRequest>(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.Requests;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"responses\":[" + string.Join(",", requests.Select(r => "{\"id\":\"" + r.Id + "\",\"status\":200,\"body\":{}}")) + "]}",
                    Encoding.UTF8, "application/json")
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

        // Test with absolute URL
        var result = await batch.ExecuteBatchIndexedAsync([new BatchOperation("https://graph.microsoft.com/v1.0/users/u0")], CancellationToken.None);

        Assert.Single(result.Results);
        Assert.Equal(200, result.Results[0].Response.Status);
    }

    [Fact]
    public async Task ExecuteBatchIndexedAsync_Delete_RetriesOn5xx()
    {
        var attempts = 0;
        var handler = new StubHttpMessageHandler().Enqueue(request =>
        {
            var requests = JsonSerializer.Deserialize<GraphBatchRequest>(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.Requests;
            attempts++;
            var status = attempts == 1 ? 503 : 200;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"responses\":[" + string.Join(",", requests.Select(r => "{\"id\":\"" + r.Id + "\",\"status\":" + status + ",\"body\":{}}")) + "]}",
                    Encoding.UTF8, "application/json")
            };
        });

        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();
        GraphBatchClient.ResetPacingState();

        var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };
        var client = new ResilientGraphClient(http, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAttempts = 3,
            CircuitBreakerMinThroughput = 1000,
            AttemptTimeoutSeconds = 10,
            TotalTimeoutSeconds = 60
        });

        var batch = new GraphBatchClient(client, "https://graph.microsoft.com/v1.0", 1, 1, 0);

        var result = await batch.ExecuteBatchIndexedAsync([new BatchOperation("/users/u0", "DELETE")], CancellationToken.None);

        Assert.Equal(200, result.Results[0].Response.Status);
    }

    [Fact]
    public async Task ExecuteBatchIndexedAsync_RetryDoesNotDuplicateBody()
    {
        var bodies = new List<string>();
        var handler = new StubHttpMessageHandler()
            .Enqueue(request =>
            {
                var requests = JsonSerializer.Deserialize<GraphBatchRequest>(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.Requests;
                bodies.AddRange(requests.Select(r => r.Body?.GetRawText() ?? ""));
                var responses = requests.Select(r => "{\"id\":\"" + r.Id + "\",\"status\":429,\"headers\":{\"Retry-After\":\"0\"},\"body\":{}}");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"responses\":[" + string.Join(",", responses) + "]}", Encoding.UTF8, "application/json")
                };
            })
            .Enqueue(request =>
            {
                var requests = JsonSerializer.Deserialize<GraphBatchRequest>(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.Requests;
                bodies.AddRange(requests.Select(r => r.Body?.GetRawText() ?? ""));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"responses\":[" + string.Join(",", requests.Select(r => "{\"id\":\"" + r.Id + "\",\"status\":200,\"body\":" + r.Body?.GetRawText() + "}")) + "]}",
                        Encoding.UTF8, "application/json")
                };
            });

        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();
        GraphBatchClient.ResetPacingState();

        var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };
        var client = new ResilientGraphClient(http, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAttempts = 3,
            CircuitBreakerMinThroughput = 1000,
            AttemptTimeoutSeconds = 10,
            TotalTimeoutSeconds = 60
        });

        var batch = new GraphBatchClient(client, "https://graph.microsoft.com/v1.0", 1, 1, 0);

        var body = JsonDocument.Parse("""{"data":"test"}""").RootElement;
        await batch.ExecuteBatchIndexedAsync([new BatchOperation("/users", "POST", body)], CancellationToken.None);

        // Body should be sent exactly twice (original + 1 retry)
        Assert.Equal(2, bodies.Count);
        Assert.All(bodies, b => Assert.Equal("""{"data":"test"}""", b));
    }
}
