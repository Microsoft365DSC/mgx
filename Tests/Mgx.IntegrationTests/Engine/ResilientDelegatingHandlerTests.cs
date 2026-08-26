using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.RateLimiting;
using Mgx.Engine.Http;
using Mgx.IntegrationTests.Fakes;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace Mgx.IntegrationTests.Engine;

[CollectionDefinition(Name, DisableParallelization = true)]
public class ResilientDelegatingHandlerCollection
{
    public const string Name = "ResilientDelegatingHandler";
}

[Collection(ResilientDelegatingHandlerCollection.Name)]
public class ResilientDelegatingHandlerTests
{
    private static ResiliencePipeline<HttpResponseMessage> BuildPipeline(int maxRetryAttempts = 3) =>
        new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = maxRetryAttempts,
                Delay = TimeSpan.Zero,
                BackoffType = DelayBackoffType.Constant,
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .HandleResult(r => (int)r.StatusCode == 429 || (int)r.StatusCode >= 500)
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .HandleResult(r => (int)r.StatusCode >= 500),
                SamplingDuration = TimeSpan.FromSeconds(1),
                MinimumThroughput = 10,
                FailureRatio = 0.5,
                BreakDuration = TimeSpan.FromSeconds(1)
            })
            .Build();

    private static TokenBucketRateLimiter CreateRateLimiter(int limit) =>
        new(new TokenBucketRateLimiterOptions
        {
            TokenLimit = limit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 100,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            TokensPerPeriod = limit,
            AutoReplenishment = true
        });

    private static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");

    [Fact]
    public void Constructor_NullPipeline_ThrowsArgumentNullException()
    {
        var rateLimiter = CreateRateLimiter(10);
        Assert.Throws<ArgumentNullException>(() => new ResilientDelegatingHandler(null!, rateLimiter));
    }

    [Fact]
    public void Constructor_NullRateLimiter_DoesNotThrow()
    {
        var pipeline = BuildPipeline();
        var handler = new ResilientDelegatingHandler(pipeline, null);
        Assert.NotNull(handler);
    }

    [Fact]
    public async Task SendAsync_WithoutRateLimiter_CallsPipeline()
    {
        var pipeline = BuildPipeline();
        var handler = new ResilientDelegatingHandler(pipeline, null)
        {
            InnerHandler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, """{"id":"1"}""")
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };

        using var response = await http.GetAsync("/v1.0/me", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SendAsync_WithRateLimiter_AcquiresLease()
    {
        var pipeline = BuildPipeline();
        var rateLimiter = CreateRateLimiter(1);
        var handler = new ResilientDelegatingHandler(pipeline, rateLimiter)
        {
            InnerHandler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, """{"id":"1"}""")
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };

        using var response = await http.GetAsync("/v1.0/me", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SendAsync_RateLimiterExhausted_ThrowsInvalidOperationException()
    {
        var pipeline = BuildPipeline();
        var rateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 1,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0,
            ReplenishmentPeriod = TimeSpan.FromSeconds(60),
            TokensPerPeriod = 1,
            AutoReplenishment = false
        });
        var handlerFactory = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.OK, """{"id":"1"}""");
        var handler = new ResilientDelegatingHandler(pipeline, rateLimiter)
        {
            InnerHandler = handlerFactory
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };

        // First request succeeds (uses the 1 token)
        using (await http.GetAsync("/v1.0/me", TestContext.Current.CancellationToken)) { }

        // Second request should fail - no tokens available and no auto-replenishment
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            http.GetAsync("/v1.0/me", TestContext.Current.CancellationToken));

        Assert.Contains("Rate limit exceeded", ex.Message);
    }

    [Fact]
    public async Task SendAsync_PostRequest_IsNotIdempotent()
    {
        var handlerFactory = new StubHttpMessageHandler()
            .EnqueueRepeated(2, _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = JsonContent("{}")
            })
            .EnqueueJson(HttpStatusCode.OK, """{"id":"1"}""");

        var pipeline = BuildPipeline();
        var rateLimiter = CreateRateLimiter(10);
        var handler = new ResilientDelegatingHandler(pipeline, rateLimiter)
        {
            InnerHandler = handlerFactory
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };

        using var content = JsonContent("{}");
        using var response = await http.PostAsync("/v1.0/users", content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, handlerFactory.RequestCount);
    }

    [Fact]
    public async Task SendAsync_GetRequest_IsIdempotent()
    {
        var handlerFactory = new StubHttpMessageHandler()
            .EnqueueRepeated(2, _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = JsonContent("{}")
            })
            .EnqueueJson(HttpStatusCode.OK, """{"id":"1"}""");

        var pipeline = BuildPipeline();
        var rateLimiter = CreateRateLimiter(10);
        var handler = new ResilientDelegatingHandler(pipeline, rateLimiter)
        {
            InnerHandler = handlerFactory
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };

        using var response = await http.GetAsync("/v1.0/me", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, handlerFactory.RequestCount);
    }

    [Fact]
    public async Task SendAsync_ClonesRequestContentOnRetry()
    {
        var bodies = new List<string>();
        var handlerFactory = new StubHttpMessageHandler()
            .EnqueueRepeated(2, request =>
            {
                var body = request.Content != null
                    ? request.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult()
                    : "";
                bodies.Add(body);
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = JsonContent("{}")
                };
            })
            .Enqueue(request =>
            {
                var body = request.Content != null
                    ? request.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult()
                    : "";
                bodies.Add(body);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent("""{"id":"1"}""")
                };
            });

        var pipeline = BuildPipeline();
        var rateLimiter = CreateRateLimiter(10);
        var handler = new ResilientDelegatingHandler(pipeline, rateLimiter)
        {
            InnerHandler = handlerFactory
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };

        const string payload = """{"displayName":"Test"}""";
        using var content = JsonContent(payload);
        using var response = await http.PostAsync("/v1.0/users", content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, bodies.Count);
        Assert.All(bodies, b => Assert.Equal(payload, b));
    }

    [Fact]
    public async Task SendAsync_PreservesRequestHeaders()
    {
        var capturedHeaders = new List<Dictionary<string, IEnumerable<string>>>();
        var handlerFactory = new StubHttpMessageHandler()
            .Enqueue(request =>
            {
                capturedHeaders.Add(request.Headers.ToDictionary(h => h.Key, h => h.Value));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent("""{"id":"1"}""")
                };
            });

        var pipeline = BuildPipeline();
        var rateLimiter = CreateRateLimiter(10);
        var handler = new ResilientDelegatingHandler(pipeline, rateLimiter)
        {
            InnerHandler = handlerFactory
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };

        var request = new HttpRequestMessage(HttpMethod.Get, "/v1.0/me");
        request.Headers.Add("X-Custom-Header", "test-value");
        request.Headers.Add("Authorization", "Bearer token123");
        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(capturedHeaders);
        Assert.True(capturedHeaders[0].ContainsKey("X-Custom-Header"));
        Assert.True(capturedHeaders[0].ContainsKey("Authorization"));
        Assert.True(capturedHeaders[0].ContainsKey("SdkVersion"));
    }

    [Fact]
    public async Task SendAsync_PreservesContentHeaders()
    {
        var capturedContentHeaders = new List<Dictionary<string, IEnumerable<string>>>();
        var handlerFactory = new StubHttpMessageHandler()
            .Enqueue(request =>
            {
                if (request.Content != null)
                {
                    capturedContentHeaders.Add(request.Content.Headers.ToDictionary(h => h.Key, h => h.Value));
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent("""{"id":"1"}""")
                };
            });

        var pipeline = BuildPipeline();
        var rateLimiter = CreateRateLimiter(10);
        var handler = new ResilientDelegatingHandler(pipeline, rateLimiter)
        {
            InnerHandler = handlerFactory
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };

        var content = JsonContent("{}");
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.Add("Content-Encoding", "gzip");
        content.Headers.Add("X-Content-Custom", "custom-value");

        using var response = await http.PostAsync("/v1.0/users", content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(capturedContentHeaders);
        Assert.True(capturedContentHeaders[0].ContainsKey("Content-Type"));
        Assert.True(capturedContentHeaders[0].ContainsKey("Content-Encoding"));
        Assert.True(capturedContentHeaders[0].ContainsKey("X-Content-Custom"));
    }

    [Fact]
    public async Task SendAsync_CopiesRequestOptions()
    {
        var capturedOptions = new List<Dictionary<string, object?>>();
        var handlerFactory = new StubHttpMessageHandler()
            .Enqueue(request =>
            {
                var opts = new Dictionary<string, object?>();
                foreach (var kvp in request.Options)
                {
                    opts[kvp.Key] = kvp.Value;
                }
                capturedOptions.Add(opts);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent("""{"id":"1"}""")
                };
            });

        var pipeline = BuildPipeline();
        var rateLimiter = CreateRateLimiter(10);
        var handler = new ResilientDelegatingHandler(pipeline, rateLimiter)
        {
            InnerHandler = handlerFactory
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };

        var request = new HttpRequestMessage(HttpMethod.Get, "/v1.0/me");
        request.Options.Set(new HttpRequestOptionsKey<string>("custom-option"), "option-value");
        request.Options.Set(new HttpRequestOptionsKey<int>("int-option"), 42);
        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(capturedOptions);
        Assert.True(capturedOptions[0].ContainsKey("custom-option"));
        Assert.Equal("option-value", capturedOptions[0]["custom-option"]);
        Assert.True(capturedOptions[0].ContainsKey("int-option"));
        Assert.Equal(42, capturedOptions[0]["int-option"]);
    }

    [Fact]
    public async Task SendAsync_VerboseWriterDrainsMessages()
    {
        var messages = new List<string>();
        var handlerFactory = new StubHttpMessageHandler()
            .Enqueue(request =>
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent("""{"id":"1"}""")
                };
            });

        var pipeline = BuildPipeline();
        var rateLimiter = CreateRateLimiter(10);
        var handler = new ResilientDelegatingHandler(pipeline, rateLimiter)
        {
            InnerHandler = handlerFactory,
            VerboseWriter = msg => messages.Add(msg)
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };

        using var response = await http.GetAsync("/v1.0/me", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SendAsync_NoVerboseWriter_DiscardsMessages()
    {
        var handlerFactory = new StubHttpMessageHandler()
            .Enqueue(request =>
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent("""{"id":"1"}""")
                };
            });

        var pipeline = BuildPipeline();
        var rateLimiter = CreateRateLimiter(10);
        var handler = new ResilientDelegatingHandler(pipeline, rateLimiter)
        {
            InnerHandler = handlerFactory,
            VerboseWriter = null
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };

        using var response = await http.GetAsync("/v1.0/me", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SendAsync_DisposesLeaseAndContext()
    {
        var rateLimiter = CreateRateLimiter(1);
        var handlerFactory = new StubHttpMessageHandler()
            .Enqueue(request =>
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent("""{"id":"1"}""")
                };
            });

        var pipeline = BuildPipeline();
        var handler = new ResilientDelegatingHandler(pipeline, rateLimiter)
        {
            InnerHandler = handlerFactory
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };

        using var response = await http.GetAsync("/v1.0/me", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var response2 = await http.GetAsync("/v1.0/me", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
    }

    [Fact]
    public async Task SendAsync_SetsSdkVersionHeader()
    {
        var capturedHeaders = new List<Dictionary<string, IEnumerable<string>>>();
        var handlerFactory = new StubHttpMessageHandler()
            .Enqueue(request =>
            {
                capturedHeaders.Add(request.Headers.ToDictionary(h => h.Key, h => h.Value));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent("""{"id":"1"}""")
                };
            });

        var pipeline = BuildPipeline();
        var rateLimiter = CreateRateLimiter(10);
        var handler = new ResilientDelegatingHandler(pipeline, rateLimiter)
        {
            InnerHandler = handlerFactory
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };

        using var response = await http.GetAsync("/v1.0/me", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(capturedHeaders);
        Assert.True(capturedHeaders[0].ContainsKey("SdkVersion"));
        Assert.NotEmpty(capturedHeaders[0]["SdkVersion"]);
    }

    [Fact]
    public async Task SendAsync_RequestVersionPreserved()
    {
        var capturedVersions = new List<Version>();
        var handlerFactory = new StubHttpMessageHandler()
            .Enqueue(request =>
            {
                capturedVersions.Add(request.Version);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent("""{"id":"1"}""")
                };
            });

        var pipeline = BuildPipeline();
        var rateLimiter = CreateRateLimiter(10);
        var handler = new ResilientDelegatingHandler(pipeline, rateLimiter)
        {
            InnerHandler = handlerFactory
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };

        var request = new HttpRequestMessage(HttpMethod.Get, "/v1.0/me") { Version = new Version(2, 0) };
        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(capturedVersions);
        Assert.Equal(new Version(2, 0), capturedVersions[0]);
    }

    [Fact]
    public async Task SendAsync_RequestVersionPolicyPreserved()
    {
        var capturedPolicies = new List<HttpVersionPolicy>();
        var handlerFactory = new StubHttpMessageHandler()
            .Enqueue(request =>
            {
                capturedPolicies.Add(request.VersionPolicy);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent("""{"id":"1"}""")
                };
            });

        var pipeline = BuildPipeline();
        var rateLimiter = CreateRateLimiter(10);
        var handler = new ResilientDelegatingHandler(pipeline, rateLimiter)
        {
            InnerHandler = handlerFactory
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };

        var request = new HttpRequestMessage(HttpMethod.Get, "/v1.0/me") { VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher };
        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(capturedPolicies);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrHigher, capturedPolicies[0]);
    }

    [Fact]
    public async Task SendAsync_MultipleRetries_AllAttemptsCloneContent()
    {
        var bodies = new List<string>();
        var handlerFactory = new StubHttpMessageHandler()
            .EnqueueRepeated(3, request =>
            {
                var body = request.Content != null
                    ? request.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult()
                    : "";
                bodies.Add(body);
                return new HttpResponseMessage((HttpStatusCode)429)
                {
                    Content = JsonContent("{}")
                };
            })
            .Enqueue(request =>
            {
                var body = request.Content != null
                    ? request.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult()
                    : "";
                bodies.Add(body);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent("""{"id":"1"}""")
                };
            });

        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.Zero,
                BackoffType = DelayBackoffType.Constant,
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .HandleResult(r => (int)r.StatusCode == 429)
            })
            .Build();

        var rateLimiter = CreateRateLimiter(10);
        var handler = new ResilientDelegatingHandler(pipeline, rateLimiter)
        {
            InnerHandler = handlerFactory
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };

        const string payload = """{"data":"test"}""";
        using var content = JsonContent(payload);
        using var response = await http.PostAsync("/v1.0/users", content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(4, bodies.Count);
        Assert.All(bodies, b => Assert.Equal(payload, b));
    }
}