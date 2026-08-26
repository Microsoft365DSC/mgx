using System.Net;
using System.Text;
using Mgx.Engine.Http;
using Mgx.IntegrationTests.Fakes;

namespace Mgx.IntegrationTests.Engine;

/// <summary>
/// ResiliencePipelineFactory caches one pipeline process-wide and MgxTelemetryCollector.Current
/// is a singleton, so these tests must not run concurrently with each other.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class ResilienceCollection
{
    public const string Name = "Resilience";
}

[Collection(ResilienceCollection.Name)]
public class ResilientGraphClientTests
{
    /// <summary>
    /// Fresh options instance per test: the factory caches by reference equality, so a new
    /// instance forces a rebuilt pipeline with clean circuit-breaker history. Throughput is
    /// set far above the request count so the breaker never trips mid-test.
    /// </summary>
    private static ResilientGraphClientOptions TestOptions(int maxRetryAttempts = 3) => new()
    {
        MaxRetryAttempts = maxRetryAttempts,
        NoRateLimit = true,
        CircuitBreakerMinThroughput = 1000,
        AttemptTimeoutSeconds = 10,
        TotalTimeoutSeconds = 60
    };

    private static (ResilientGraphClient Client, HttpClient Http) NewClient(
        StubHttpMessageHandler handler, ResilientGraphClientOptions? options = null)
    {
        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();

        var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };
        return (new ResilientGraphClient(http, options ?? TestOptions()), http);
    }

    /// <summary>Token that aborts the HTTP call if the test run is cancelled.</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Queue a 429 whose Retry-After is 0, so retries are exercised without a real delay.</summary>
    private static HttpResponseMessage Throttled()
    {
        var response = new HttpResponseMessage((HttpStatusCode)429)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        response.Headers.Add("Retry-After", "0");
        return response;
    }

    [Fact]
    public async Task Returns_successful_response_without_retrying()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.OK, """{"id":"abc"}""");
        var (client, http) = NewClient(handler);
        using var _ = http;
        using var __ = client;

        using var response = await client.GetAsync("https://graph.microsoft.com/v1.0/me", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Retries_429_and_honours_Retry_After()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueRepeated(2, _ => Throttled())
            .EnqueueJson(HttpStatusCode.OK, """{"id":"abc"}""");
        var (client, http) = NewClient(handler);
        using var _ = http;
        using var __ = client;

        using var response = await client.GetAsync("https://graph.microsoft.com/v1.0/me", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, handler.RequestCount);

        var telemetry = MgxTelemetryCollector.Current.GetSummary();
        Assert.Equal(2, telemetry.ThrottleRetries);
    }

    [Fact]
    public async Task Retries_429_on_POST_even_though_POST_is_not_idempotent()
    {
        // 429 means the request was rejected, not partially applied, so POST is safe to retry
        var handler = new StubHttpMessageHandler()
            .Enqueue(_ => Throttled())
            .EnqueueJson(HttpStatusCode.Created, """{"id":"new"}""");
        var (client, http) = NewClient(handler);
        using var _ = http;
        using var __ = client;

        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(
            HttpMethod.Post, "https://graph.microsoft.com/v1.0/users", content, cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Does_not_retry_POST_on_server_error()
    {
        // A 5xx on POST may mean the server already created the object; retrying duplicates it
        var handler = new StubHttpMessageHandler()
            .EnqueueRepeated(5, _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        var (client, http) = NewClient(handler);
        using var _ = http;
        using var __ = client;

        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(
            HttpMethod.Post, "https://graph.microsoft.com/v1.0/users", content, cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Does_not_retry_client_errors()
    {
        // 404/403 are terminal: retrying only burns throttle budget
        var handler = new StubHttpMessageHandler().EnqueueStatus(HttpStatusCode.NotFound);
        var (client, http) = NewClient(handler);
        using var _ = http;
        using var __ = client;

        using var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/missing", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Stops_retrying_after_MaxRetryAttempts()
    {
        var handler = new StubHttpMessageHandler().EnqueueRepeated(10, _ => Throttled());
        var (client, http) = NewClient(handler, TestOptions(maxRetryAttempts: 2));
        using var _ = http;
        using var __ = client;

        using var response = await client.GetAsync("https://graph.microsoft.com/v1.0/me", Ct);

        Assert.Equal((HttpStatusCode)429, response.StatusCode);
        // 1 initial attempt + 2 retries
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task Replays_the_request_body_on_retry()
    {
        // Content is buffered before the pipeline; without that the second attempt
        // sends an already-consumed stream and Graph sees an empty body.
        var bodies = new List<string>();
        var handler = new StubHttpMessageHandler()
            .EnqueueRepeated(2, request =>
            {
                bodies.Add(request.Content?.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult() ?? "");
                return Throttled();
            })
            .Enqueue(request =>
            {
                bodies.Add(request.Content?.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult() ?? "");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
            });
        var (client, http) = NewClient(handler);
        using var _ = http;
        using var __ = client;

        const string payload = """{"displayName":"Test User"}""";
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(
            HttpMethod.Patch, "https://graph.microsoft.com/v1.0/users/abc", content, cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, bodies.Count);
        Assert.All(bodies, body => Assert.Equal(payload, body));
    }

    [Fact]
    public async Task Rejects_request_bodies_over_the_Graph_size_limit()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, "{}");
        var (client, http) = NewClient(handler);
        using var _ = http;
        using var __ = client;

        // Graph rejects bodies larger than 4MB; failing locally avoids a wasted round-trip
        using var oversized = new ByteArrayContent(new byte[5 * 1024 * 1024]);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SendAsync(HttpMethod.Post, "https://graph.microsoft.com/v1.0/users", oversized,
                cancellationToken: Ct));

        Assert.Contains("4MB", ex.Message);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetCollectionPageAsync_surfaces_value_and_nextLink()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, """
            {
              "@odata.count": 2,
              "@odata.nextLink": "https://graph.microsoft.com/v1.0/users?$skiptoken=abc",
              "value": [ { "id": "1" }, { "id": "2" } ]
            }
            """);
        var (client, http) = NewClient(handler);
        using var _ = http;
        using var __ = client;

        var page = await client.GetCollectionPageAsync("https://graph.microsoft.com/v1.0/users", Ct);

        Assert.Equal(2, page.Value.Length);
        Assert.Equal("https://graph.microsoft.com/v1.0/users?$skiptoken=abc", page.NextLink);
        Assert.Equal(2, page.Count);
    }

    [Fact]
    public async Task GetCollectionPageAsync_reports_a_final_page_as_having_no_nextLink()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, """
            { "value": [ { "id": "1" } ] }
            """);
        var (client, http) = NewClient(handler);
        using var _ = http;
        using var __ = client;

        var page = await client.GetCollectionPageAsync("https://graph.microsoft.com/v1.0/users", Ct);

        Assert.Single(page.Value);
        Assert.Null(page.NextLink);
    }

    [Fact]
    public async Task Telemetry_counts_successes_and_failures()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.OK, "{}")
            .EnqueueStatus(HttpStatusCode.Forbidden);
        var (client, http) = NewClient(handler);
        using var _ = http;
        using var __ = client;

        (await client.GetAsync("https://graph.microsoft.com/v1.0/me", Ct)).Dispose();
        (await client.GetAsync("https://graph.microsoft.com/v1.0/users", Ct)).Dispose();

        var telemetry = MgxTelemetryCollector.Current.GetSummary();
        Assert.Equal(2, telemetry.TotalRequests);
        Assert.Equal(1, telemetry.Succeeded);
        Assert.Equal(1, telemetry.Failed);
    }
}
