using System.Net;
using System.Text;
using System.Text.Json;
using Mgx.Engine.Http;
using Mgx.Engine.Models;
using Mgx.IntegrationTests.Fakes;

namespace Mgx.IntegrationTests.Engine;

/// <summary>
/// Graph answers /$batch with HTTP 200 even when individual items failed, so Polly never sees
/// per-item errors and GraphBatchClient has to do that retrying itself.
/// </summary>
[Collection(ResilienceCollection.Name)]
public class GraphBatchClientTests
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
            MaxRetryAttempts = 1,
            CircuitBreakerMinThroughput = 1000,
            AttemptTimeoutSeconds = 10,
            TotalTimeoutSeconds = 60
        };
        var client = new ResilientGraphClient(http, options);
        return (new GraphBatchClient(client, BaseUrl, maxRetryAfterSeconds: 1,
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

    /// <summary>Built by concatenation because the JSON braces fight raw-string interpolation.</summary>
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
    public async Task Results_come_back_in_the_order_the_operations_were_given()
    {
        // Ids are renumbered per chunk, so keying results by id alone would scramble
        // everything past the first twenty.
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(request =>
        {
            var requests = ReadRequests(request);
            // Reversed to prove the ordering comes from the id map and not arrival order.
            requests.Reverse();
            return Ok(RespondAllOk(requests));
        });
        var (batch, client, http) = NewBatchClient(handler);
        using var _ = http; using var __ = client;

        var ops = Enumerable.Range(0, 25).Select(i => new BatchOperation($"/users/u{i}")).ToList();
        var result = await batch.ExecuteBatchIndexedAsync(ops, Ct);

        for (var i = 0; i < 25; i++)
        {
            Assert.Equal($"/users/u{i}", result.Results[i].Operation.Url);
            Assert.Equal($"/users/u{i}", result.Results[i].Response.Body!.Value.GetProperty("url").GetString());
        }
    }

    [Fact]
    public async Task Duplicate_urls_are_kept_as_separate_operations()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(request => Ok(RespondAllOk(ReadRequests(request))));
        var (batch, client, http) = NewBatchClient(handler);
        using var _ = http; using var __ = client;

        var ops = new List<BatchOperation>
        {
            new("/users", "POST"), new("/users", "POST"), new("/users", "POST")
        };
        var result = await batch.ExecuteBatchIndexedAsync(ops, Ct);

        Assert.Equal(3, result.Results.Count);
    }

    [Fact]
    public async Task A_throttled_item_is_retried_on_its_own()
    {
        var attempts = new List<List<string>>();
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(request =>
        {
            var requests = ReadRequests(request);
            attempts.Add(requests.Select(r => r.Url).ToList());

            var responses = requests.Select(r => attempts.Count == 1 && r.Url.EndsWith("u1")
                ? Item(r.Id, 429, retryAfter: "0")
                : Item(r.Id, 200, "{\"url\":\"" + r.Url + "\"}"));
            return Ok(Batch(responses));
        });
        var (batch, client, http) = NewBatchClient(handler);
        using var _ = http; using var __ = client;

        var ops = new List<BatchOperation> { new("/users/u0"), new("/users/u1"), new("/users/u2") };
        var result = await batch.ExecuteBatchIndexedAsync(ops, Ct);

        Assert.Equal(3, attempts[0].Count);
        Assert.Equal(["/users/u1"], attempts[1]);
        Assert.All(result.Results, r => Assert.Equal(200, r.Response.Status));
        Assert.Equal(1, result.Telemetry.ThrottleEncounters);
    }

    [Fact]
    public async Task A_permanent_item_failure_is_returned_rather_than_retried_forever()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(request =>
        {
            var requests = ReadRequests(request);
            var responses = requests.Select(r => r.Url.EndsWith("gone")
                ? Item(r.Id, 404, """{"error":{"code":"Request_ResourceNotFound"}}""")
                : Item(r.Id, 200));
            return Ok(Batch(responses));
        });
        var (batch, client, http) = NewBatchClient(handler);
        using var _ = http; using var __ = client;

        var result = await batch.ExecuteBatchIndexedAsync(
            [new BatchOperation("/users/ok"), new BatchOperation("/users/gone")], Ct);

        Assert.Equal(200, result.Results[0].Response.Status);
        Assert.Equal(404, result.Results[1].Response.Status);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task A_failed_POST_item_is_not_retried_even_on_a_5xx()
    {
        // Resending a POST that may already have created the entity risks a duplicate.
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(request =>
        {
            var requests = ReadRequests(request);
            return Ok(Batch([Item(requests[0].Id, 503)]));
        });
        var (batch, client, http) = NewBatchClient(handler);
        using var _ = http; using var __ = client;

        var result = await batch.ExecuteBatchIndexedAsync([new BatchOperation("/users", "POST")], Ct);

        Assert.Equal(503, result.Results[0].Response.Status);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task A_failed_GET_item_is_retried_on_a_5xx()
    {
        var attempts = 0;
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(request =>
        {
            var requests = ReadRequests(request);
            attempts++;
            var status = attempts == 1 ? 503 : 200;
            return Ok(Batch([Item(requests[0].Id, status)]));
        });
        var (batch, client, http) = NewBatchClient(handler);
        using var _ = http; using var __ = client;

        var result = await batch.ExecuteBatchIndexedAsync([new BatchOperation("/users/u0")], Ct);

        Assert.Equal(200, result.Results[0].Response.Status);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task A_failing_batch_POST_is_returned_as_a_chunk_failure()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueRepeated(5, _ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"error":{"code":"Authorization_RequestDenied"}}""",
                Encoding.UTF8, "application/json")
        });
        var (batch, client, http) = NewBatchClient(handler);
        using var _ = http; using var __ = client;

        // The chunk POST failure is handed back rather than thrown, so results of chunks that
        // already landed are not discarded with it
        var result = await batch.ExecuteBatchIndexedAsync([new BatchOperation("/users/u0")], Ct);

        var ex = Assert.IsType<GraphServiceException>(result.ChunkFailure);
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Single(result.NotSent);
    }

    [Fact]
    public async Task Telemetry_counts_every_request_and_retry()
    {
        var first = true;
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(request =>
        {
            var requests = ReadRequests(request);
            var responses = requests.Select(r =>
            {
                var throttled = first && r.Url.EndsWith("u1");
                return throttled ? Item(r.Id, 429, retryAfter: "0") : Item(r.Id, 200);
            }).ToList();
            first = false;
            return Ok(Batch(responses));
        });
        var (batch, client, http) = NewBatchClient(handler);
        using var _ = http; using var __ = client;

        var result = await batch.ExecuteBatchIndexedAsync(
            [new BatchOperation("/users/u0"), new BatchOperation("/users/u1")], Ct);

        Assert.Equal(2, result.Telemetry.TotalRequests);
        Assert.Equal(1, result.Telemetry.ThrottleEncounters);
        Assert.True(result.Telemetry.ItemRetries >= 1);
    }

    [Fact]
    public async Task Verbose_messages_are_drained_once_and_only_once()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(request => Ok(RespondAllOk(ReadRequests(request))));
        var (batch, client, http) = NewBatchClient(handler, batchItemsPerSecond: 1000);
        using var _ = http; using var __ = client;

        var messages = new List<string>();
        batch.VerboseWriter = messages.Add;

        var ops = Enumerable.Range(0, 25).Select(i => new BatchOperation($"/users/u{i}", "POST")).ToList();
        await batch.ExecuteBatchIndexedAsync(ops, Ct);

        batch.DrainVerboseMessages();
        var afterFirstDrain = messages.Count;
        batch.DrainVerboseMessages();

        Assert.NotEmpty(messages);
        Assert.Equal(afterFirstDrain, messages.Count);
    }
}
