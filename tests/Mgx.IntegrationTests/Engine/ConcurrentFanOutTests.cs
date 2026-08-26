using System.Net;
using System.Text;
using System.Text.Json;
using Mgx.Engine.Http;
using Mgx.Engine.Models;
using Mgx.Engine.Pagination;
using Mgx.IntegrationTests.Fakes;

namespace Mgx.IntegrationTests.Engine;

/// <summary>
/// ConcurrentFanOut exists for partial success, so one group returning 404 must not discard the
/// members already fetched for every other group.
/// </summary>
[Collection(ResilienceCollection.Name)]
public class ConcurrentFanOutTests
{
    private const string Host = "https://graph.microsoft.com";

    private static (ConcurrentFanOut FanOut, ResilientGraphClient Client, HttpClient Http) NewFanOut(
        HttpMessageHandler handler, int maxConcurrency = 5)
    {
        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();

        var http = new HttpClient(handler) { BaseAddress = new Uri(Host) };
        var options = new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            CircuitBreakerMinThroughput = 1000,
            MaxRetryAttempts = 1,
            AttemptTimeoutSeconds = 10,
            TotalTimeoutSeconds = 60
        };
        var client = new ResilientGraphClient(http, options);
        return (new ConcurrentFanOut(client, maxConcurrency), client, http);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Answers on request path rather than arrival order, which concurrency makes unpredictable.</summary>
    private static StubHttpMessageHandler RoutedBy(Func<string, HttpResponseMessage> route)
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(req => route(req.RequestUri!.ToString()));
        return handler;
    }

    private static string Collection(params string[] ids) =>
        $$"""{"value":[{{string.Join(",", ids.Select(i => $$"""{"id":"{{i}}"}"""))}}]}""";

    [Fact]
    public async Task Every_url_gets_its_own_result_set()
    {
        var handler = RoutedBy(url => url.Contains("g1")
            ? Json(HttpStatusCode.OK, Collection("a", "b"))
            : Json(HttpStatusCode.OK, Collection("c")));
        var (fanOut, client, http) = NewFanOut(handler);
        using var _ = http; using var __ = client;

        var urls = new[] { $"{Host}/v1.0/groups/g1/members", $"{Host}/v1.0/groups/g2/members" };
        var result = await fanOut.FetchAllAsync(urls, cancellationToken: Ct);

        Assert.False(result.HasErrors);
        Assert.Equal(2, result.Results[urls[0]].Length);
        Assert.Single(result.Results[urls[1]]);
    }

    [Fact]
    public async Task One_failing_url_does_not_discard_the_others()
    {
        var handler = RoutedBy(url => url.Contains("missing")
            ? Json(HttpStatusCode.NotFound, """{"error":{"code":"Request_ResourceNotFound","message":"gone"}}""")
            : Json(HttpStatusCode.OK, Collection("a")));
        var (fanOut, client, http) = NewFanOut(handler);
        using var _ = http; using var __ = client;

        var ok = $"{Host}/v1.0/groups/good/members";
        var bad = $"{Host}/v1.0/groups/missing/members";
        var result = await fanOut.FetchAllAsync([ok, bad], cancellationToken: Ct);

        Assert.True(result.HasErrors);
        Assert.Single(result.Results[ok]);
        Assert.IsType<GraphServiceException>(result.Errors[bad]);
        Assert.False(result.Results.ContainsKey(bad));
    }

    [Fact]
    public async Task Pages_are_followed_per_url()
    {
        var handler = RoutedBy(url => url.Contains("skiptoken")
            ? Json(HttpStatusCode.OK, Collection("b"))
            : Json(HttpStatusCode.OK,
                $$"""{"value":[{"id":"a"}],"@odata.nextLink":"{{Host}}/v1.0/groups/g1/members?$skiptoken=p2"}"""));
        var (fanOut, client, http) = NewFanOut(handler);
        using var _ = http; using var __ = client;

        var url = $"{Host}/v1.0/groups/g1/members";
        var result = await fanOut.FetchAllAsync([url], cancellationToken: Ct);

        Assert.Equal(2, result.Results[url].Length);
    }

    [Fact]
    public async Task A_nextLink_pointing_elsewhere_ends_that_url_without_failing_it()
    {
        var handler = RoutedBy(_ => Json(HttpStatusCode.OK,
            """{"value":[{"id":"a"}],"@odata.nextLink":"https://evil.example.com/v1.0/x"}"""));
        var (fanOut, client, http) = NewFanOut(handler);
        using var _ = http; using var __ = client;

        var url = $"{Host}/v1.0/groups/g1/members";
        var result = await fanOut.FetchAllAsync([url], cancellationToken: Ct);

        Assert.False(result.HasErrors);
        Assert.Single(result.Results[url]);
    }

    [Fact]
    public async Task maxItemsPerUrl_truncates_and_stops_paging()
    {
        var handler = RoutedBy(_ => Json(HttpStatusCode.OK,
            $$"""{"value":[{"id":"a"},{"id":"b"},{"id":"c"}],"@odata.nextLink":"{{Host}}/v1.0/groups/g1/members?$skiptoken=p2"}"""));
        var (fanOut, client, http) = NewFanOut(handler);
        using var _ = http; using var __ = client;

        var url = $"{Host}/v1.0/groups/g1/members";
        var result = await fanOut.FetchAllAsync([url], maxItemsPerUrl: 2, cancellationToken: Ct);

        Assert.Equal(2, result.Results[url].Length);
    }

    [Fact]
    public async Task Concurrency_never_exceeds_the_configured_limit()
    {
        var inFlight = 0;
        var peak = 0;

        var handler = RoutedBy(_ =>
        {
            var now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref peak, now);
            Thread.Sleep(30);
            Interlocked.Decrement(ref inFlight);
            return Json(HttpStatusCode.OK, Collection("a"));
        });
        var (fanOut, client, http) = NewFanOut(handler, maxConcurrency: 2);
        using var _ = http; using var __ = client;

        var urls = Enumerable.Range(0, 8).Select(i => $"{Host}/v1.0/groups/g{i}/members").ToArray();
        var result = await fanOut.FetchAllAsync(urls, cancellationToken: Ct);

        Assert.Equal(8, result.Results.Count);
        Assert.True(peak <= 2, $"peak concurrency was {peak}, expected at most 2");
    }

    [Fact]
    public async Task A_concurrency_below_one_is_clamped_rather_than_deadlocking()
    {
        var handler = RoutedBy(_ => Json(HttpStatusCode.OK, Collection("a")));
        var (fanOut, client, http) = NewFanOut(handler, maxConcurrency: 0);
        using var _ = http; using var __ = client;

        var result = await fanOut.FetchAllAsync([$"{Host}/v1.0/groups/g1/members"], cancellationToken: Ct);

        Assert.Single(result.Results);
    }

    [Fact]
    public async Task ForEachAsync_collects_failures_per_item_and_keeps_going()
    {
        var handler = RoutedBy(_ => Json(HttpStatusCode.OK, Collection("a")));
        var (fanOut, client, http) = NewFanOut(handler);
        using var _ = http; using var __ = client;

        var processed = new System.Collections.Concurrent.ConcurrentBag<int>();
        var errors = await fanOut.ForEachAsync<int>(
            [1, 2, 3, 4],
            (item, _) =>
            {
                if (item == 3) throw new InvalidOperationException("item 3 is bad");
                processed.Add(item);
                return Task.CompletedTask;
            },
            Ct);

        Assert.Equal([3], errors.Keys);
        Assert.Equal("item 3 is bad", errors[3].Message);
        Assert.Equal([1, 2, 4], processed.OrderBy(x => x));
    }

    [Fact]
    public async Task BulkWriteAsync_reports_created_bodies_against_their_operation_id()
    {
        var handler = RoutedBy(_ => Json(HttpStatusCode.Created, """{"id":"new-1"}"""));
        var (fanOut, client, http) = NewFanOut(handler);
        using var _ = http; using var __ = client;

        var result = await fanOut.BulkWriteAsync(
            HttpMethod.Post,
            [("op-1", $"{Host}/v1.0/users")],
            """{"displayName":"A"}""",
            cancellationToken: Ct);

        Assert.Equal(1, result.Succeeded);
        Assert.Equal(0, result.Failed);
        var (id, body) = Assert.Single(result.Responses);
        Assert.Equal("op-1", id);
        Assert.Equal("new-1", body.GetProperty("id").GetString());
    }

    [Fact]
    public async Task BulkWriteAsync_surfaces_the_graph_error_code_not_just_the_status()
    {
        var handler = RoutedBy(_ => Json(HttpStatusCode.BadRequest,
            """{"error":{"code":"Request_BadRequest","message":"Invalid value"}}"""));
        var (fanOut, client, http) = NewFanOut(handler);
        using var _ = http; using var __ = client;

        var result = await fanOut.BulkWriteAsync(
            HttpMethod.Post, [("op-1", $"{Host}/v1.0/users")], "{}", cancellationToken: Ct);

        Assert.Equal(0, result.Succeeded);
        var error = Assert.Single(result.Errors);
        Assert.Equal("op-1", error.Id);
        Assert.Equal(400, error.StatusCode);
        Assert.Equal("Request_BadRequest: Invalid value", error.Message);
    }

    [Fact]
    public async Task BulkWriteAsync_falls_back_to_the_status_when_the_body_is_not_a_graph_error()
    {
        var handler = RoutedBy(_ => Json(HttpStatusCode.BadRequest, "not json at all"));
        var (fanOut, client, http) = NewFanOut(handler);
        using var _ = http; using var __ = client;

        var result = await fanOut.BulkWriteAsync(
            HttpMethod.Post, [("op-1", $"{Host}/v1.0/users")], "{}", cancellationToken: Ct);

        Assert.Equal("HTTP 400", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public async Task BulkWriteAsync_counts_a_204_as_success_with_no_response_body()
    {
        var handler = RoutedBy(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var (fanOut, client, http) = NewFanOut(handler);
        using var _ = http; using var __ = client;

        var result = await fanOut.BulkWriteAsync(
            HttpMethod.Delete, [("op-1", $"{Host}/v1.0/users/u1")], null, cancellationToken: Ct);

        Assert.Equal(1, result.Succeeded);
        Assert.Empty(result.Responses);
    }

    [Fact]
    public async Task BulkWriteAsync_keeps_successes_alongside_failures()
    {
        var handler = RoutedBy(url => url.EndsWith("bad")
            ? Json(HttpStatusCode.Forbidden, """{"error":{"code":"Authorization_RequestDenied","message":"no"}}""")
            : Json(HttpStatusCode.Created, """{"id":"ok"}"""));
        var (fanOut, client, http) = NewFanOut(handler);
        using var _ = http; using var __ = client;

        var result = await fanOut.BulkWriteAsync(
            HttpMethod.Post,
            [("good", $"{Host}/v1.0/users/good"), ("bad", $"{Host}/v1.0/users/bad")],
            "{}",
            cancellationToken: Ct);

        Assert.Equal(1, result.Succeeded);
        Assert.Equal(1, result.Failed);
        Assert.Equal("bad", Assert.Single(result.Errors).Id);
    }

    [Fact]
    public async Task BulkWriteAsync_reports_progress_once_per_operation()
    {
        var handler = RoutedBy(_ => Json(HttpStatusCode.Created, """{"id":"x"}"""));
        var (fanOut, client, http) = NewFanOut(handler, maxConcurrency: 1);
        using var _ = http; using var __ = client;

        var progress = new List<(int Done, int Total)>();
        var ops = Enumerable.Range(0, 3).Select(i => ($"op{i}", $"{Host}/v1.0/users/u{i}")).ToArray();

        await fanOut.BulkWriteAsync(
            HttpMethod.Post, ops, "{}",
            onProgress: (done, total) => progress.Add((done, total)),
            cancellationToken: Ct);

        Assert.Equal([(1, 3), (2, 3), (3, 3)], progress);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current) return;
        }
    }
}
