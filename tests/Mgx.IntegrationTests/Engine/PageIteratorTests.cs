using System.Text.Json;
using Mgx.Engine.Http;
using Mgx.Engine.Pagination;
using Mgx.IntegrationTests.Fakes;

namespace Mgx.IntegrationTests.Engine;

/// <summary>
/// The parts of PageIterator worth pinning are the ones that fail silently. A rejected nextLink
/// ends the stream without an error, and the empty-page guard is all that stands between a Graph
/// bug and an infinite loop.
/// </summary>
[Collection(ResilienceCollection.Name)]
public class PageIteratorTests
{
    private const string Start = "https://graph.microsoft.com/v1.0/users";

    private static (PageIterator Iterator, HttpClient Http) NewIterator(StubHttpMessageHandler handler)
    {
        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();

        var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };
        var options = new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            CircuitBreakerMinThroughput = 1000,
            AttemptTimeoutSeconds = 10,
            TotalTimeoutSeconds = 60
        };
        return (new PageIterator(new ResilientGraphClient(http, options)), http);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A page of objects with sequential ids, optionally pointing at a next page.</summary>
    private static string Page(int firstId, int count, string? nextLink = null, long? total = null)
    {
        var items = string.Join(",", Enumerable.Range(firstId, count).Select(i => $$"""{"id":"u{{i}}"}"""));
        var parts = new List<string> { $"\"value\":[{items}]" };
        if (nextLink != null) parts.Add($"\"@odata.nextLink\":\"{nextLink}\"");
        if (total.HasValue) parts.Add($"\"@odata.count\":{total.Value}");
        return "{" + string.Join(",", parts) + "}";
    }

    private static async Task<List<string>> IdsOf(IAsyncEnumerable<JsonElement> stream)
    {
        var ids = new List<string>();
        await foreach (var item in stream)
            ids.Add(item.GetProperty("id").GetString()!);
        return ids;
    }

    [Fact]
    public async Task Follows_nextLink_across_every_page()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(System.Net.HttpStatusCode.OK, Page(1, 2, $"{Start}?$skiptoken=p2"))
            .EnqueueJson(System.Net.HttpStatusCode.OK, Page(3, 2, $"{Start}?$skiptoken=p3"))
            .EnqueueJson(System.Net.HttpStatusCode.OK, Page(5, 1));
        var (iterator, http) = NewIterator(handler);
        using var _ = http;

        var ids = await IdsOf(iterator.StreamAllWithCountAsync(Start, 0, null, cancellationToken: Ct));

        Assert.Equal(["u1", "u2", "u3", "u4", "u5"], ids);
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task Reports_the_odata_count_from_the_first_page_only()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(System.Net.HttpStatusCode.OK, Page(1, 1, $"{Start}?$skiptoken=p2", total: 42))
            .EnqueueJson(System.Net.HttpStatusCode.OK, Page(2, 1, total: 99));
        var (iterator, http) = NewIterator(handler);
        using var _ = http;

        var counts = new List<long>();
        await IdsOf(iterator.StreamAllWithCountAsync(Start, 0, counts.Add, cancellationToken: Ct));

        Assert.Equal([42], counts);
    }

    [Fact]
    public async Task Stops_at_maxItems_without_fetching_another_page()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(System.Net.HttpStatusCode.OK, Page(1, 5, $"{Start}?$skiptoken=p2"));
        var (iterator, http) = NewIterator(handler);
        using var _ = http;

        var ids = await IdsOf(iterator.StreamAllWithCountAsync(Start, 3, null, cancellationToken: Ct));

        Assert.Equal(["u1", "u2", "u3"], ids);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Pagination_stops_when_the_nextLink_points_at_another_host()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(System.Net.HttpStatusCode.OK, Page(1, 1, "https://evil.example.com/v1.0/users?$skiptoken=p2"))
            .EnqueueJson(System.Net.HttpStatusCode.OK, Page(2, 1));
        var (iterator, http) = NewIterator(handler);
        using var _ = http;

        var ids = await IdsOf(iterator.StreamAllWithCountAsync(Start, 0, null, cancellationToken: Ct));

        Assert.Equal(["u1"], ids);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Pagination_stops_when_the_nextLink_downgrades_to_http()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(System.Net.HttpStatusCode.OK, Page(1, 1, "http://graph.microsoft.com/v1.0/users?$skiptoken=p2"));
        var (iterator, http) = NewIterator(handler);
        using var _ = http;

        var ids = await IdsOf(iterator.StreamAllWithCountAsync(Start, 0, null, cancellationToken: Ct));

        Assert.Equal(["u1"], ids);
    }

    [Fact]
    public async Task Resume_skips_the_items_already_emitted_on_the_first_page()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(System.Net.HttpStatusCode.OK, Page(1, 4));
        var (iterator, http) = NewIterator(handler);
        using var _ = http;

        var resume = new ResumeState($"{Start}?$skiptoken=p2", SkipOnFirstPage: 2, ItemsAlreadyCollected: 2);
        var ids = await IdsOf(iterator.StreamAllWithCountAsync(Start, 0, null, resume: resume, cancellationToken: Ct));

        Assert.Equal(["u3", "u4"], ids);
    }

    [Fact]
    public async Task Resume_counts_already_collected_items_against_maxItems()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(System.Net.HttpStatusCode.OK, Page(1, 10));
        var (iterator, http) = NewIterator(handler);
        using var _ = http;

        var resume = new ResumeState(Start, SkipOnFirstPage: 0, ItemsAlreadyCollected: 8);
        var ids = await IdsOf(iterator.StreamAllWithCountAsync(Start, 10, null, resume: resume, cancellationToken: Ct));

        Assert.Equal(["u1", "u2"], ids);
    }

    [Fact]
    public async Task Page_completion_reports_the_url_that_will_be_fetched_next()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(System.Net.HttpStatusCode.OK, Page(1, 1, $"{Start}?$skiptoken=p2"))
            .EnqueueJson(System.Net.HttpStatusCode.OK, Page(2, 1));
        var (iterator, http) = NewIterator(handler);
        using var _ = http;

        var completions = new List<string?>();
        await IdsOf(iterator.StreamAllWithCountAsync(
            Start, 0, null, onPageComplete: info => completions.Add(info.NextPageUrl), cancellationToken: Ct));

        Assert.Equal([$"{Start}?$skiptoken=p2", null], completions);
    }

    [Fact]
    public async Task Delta_link_is_surfaced_once_the_final_page_arrives()
    {
        var deltaLink = $"{Start}/delta?$deltatoken=xyz";
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(System.Net.HttpStatusCode.OK, $$"""{"value":[{"id":"u1"}],"@odata.deltaLink":"{{deltaLink}}"}""");
        var (iterator, http) = NewIterator(handler);
        using var _ = http;

        var captured = new List<string>();
        await IdsOf(iterator.StreamAllWithCountAsync(
            Start, 0, null, onDeltaLink: captured.Add, cancellationToken: Ct));

        Assert.Equal([deltaLink], captured);
    }

    [Fact]
    public async Task A_delta_link_on_another_host_is_never_surfaced()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(System.Net.HttpStatusCode.OK,
                """{"value":[{"id":"u1"}],"@odata.deltaLink":"https://evil.example.com/v1.0/users/delta?$deltatoken=xyz"}""");
        var (iterator, http) = NewIterator(handler);
        using var _ = http;

        var captured = new List<string>();
        await IdsOf(iterator.StreamAllWithCountAsync(
            Start, 0, null, onDeltaLink: captured.Add, cancellationToken: Ct));

        Assert.Empty(captured);
    }

    [Fact]
    public async Task Three_consecutive_empty_pages_end_a_regular_stream()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueRepeated(10, _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"value":[],"@odata.nextLink":"{{Start}}?$skiptoken=loop"}""",
                    System.Text.Encoding.UTF8, "application/json")
            });
        var (iterator, http) = NewIterator(handler);
        using var _ = http;

        var ids = await IdsOf(iterator.StreamAllWithCountAsync(Start, 0, null, cancellationToken: Ct));

        Assert.Empty(ids);
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task A_delta_stream_tolerates_more_empty_pages_than_a_regular_one()
    {
        var deltaLink = $"{Start}/delta?$deltatoken=xyz";
        var handler = new StubHttpMessageHandler();
        for (var i = 0; i < 5; i++)
            handler.EnqueueJson(System.Net.HttpStatusCode.OK,
                $$"""{"value":[],"@odata.nextLink":"{{Start}}?$skiptoken=e{{i}}"}""");
        handler.EnqueueJson(System.Net.HttpStatusCode.OK,
            $$"""{"value":[{"id":"u1"}],"@odata.deltaLink":"{{deltaLink}}"}""");

        var (iterator, http) = NewIterator(handler);
        using var _ = http;

        var captured = new List<string>();
        var ids = await IdsOf(iterator.StreamAllWithCountAsync(
            Start, 0, null, onDeltaLink: captured.Add, cancellationToken: Ct));

        Assert.Equal(["u1"], ids);
        Assert.Equal([deltaLink], captured);
        Assert.Equal(6, handler.RequestCount);
    }

    [Fact]
    public async Task A_run_of_empty_pages_is_forgiven_once_items_appear_again()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(System.Net.HttpStatusCode.OK, $$"""{"value":[],"@odata.nextLink":"{{Start}}?$skiptoken=a"}""")
            .EnqueueJson(System.Net.HttpStatusCode.OK, $$"""{"value":[],"@odata.nextLink":"{{Start}}?$skiptoken=b"}""")
            .EnqueueJson(System.Net.HttpStatusCode.OK, Page(1, 1, $"{Start}?$skiptoken=c"))
            .EnqueueJson(System.Net.HttpStatusCode.OK, $$"""{"value":[],"@odata.nextLink":"{{Start}}?$skiptoken=d"}""")
            .EnqueueJson(System.Net.HttpStatusCode.OK, Page(2, 1));
        var (iterator, http) = NewIterator(handler);
        using var _ = http;

        var ids = await IdsOf(iterator.StreamAllWithCountAsync(Start, 0, null, cancellationToken: Ct));

        Assert.Equal(["u1", "u2"], ids);
        Assert.Equal(5, handler.RequestCount);
    }
}
