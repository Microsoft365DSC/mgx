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
    public async Task A_nextLink_pointing_elsewhere_fails_that_url()
    {
        var handler = RoutedBy(_ => Json(HttpStatusCode.OK,
            """{"value":[{"id":"a"}],"@odata.nextLink":"https://evil.example.com/v1.0/x"}"""));
        var (fanOut, client, http) = NewFanOut(handler);
        using var _ = http; using var __ = client;

        var url = $"{Host}/v1.0/groups/g1/members";
        var result = await fanOut.FetchAllAsync([url], cancellationToken: Ct);

        // A refused nextLink is an error for that url, not a short but successful result
        Assert.True(result.HasErrors);
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

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current) return;
        }
    }
}
