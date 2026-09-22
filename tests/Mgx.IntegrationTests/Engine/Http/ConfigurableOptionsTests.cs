using System.Net;
using System.Threading.RateLimiting;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests;

[Collection("Pipeline")]
public class ConfigurableOptionsTests
{
    [Fact]
    public async Task Retry_RespectsCustomMaxRetryAttempts_LowerValue()
    {
        // MaxRetryAttempts=2: should make 3 total requests (1 initial + 2 retries)
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        for (int i = 0; i < 5; i++)
            handler.QueueResponse((HttpStatusCode)429, null, new() { ["Retry-After"] = "0" });

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient,
            new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 2 });

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/throttled");

        Assert.Equal((HttpStatusCode)429, response.StatusCode);
        Assert.Equal(3, handler.RequestCount); // 1 initial + 2 retries, NOT 8 (default)
        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task Retry_RespectsCustomMaxRetryAttempts_HigherValue()
    {
        // MaxRetryAttempts=10: 9 failures then success should work
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.QueueFailuresThenSuccess(
            failCount: 9,
            failStatus: (HttpStatusCode)429,
            successBody: TestData.SingleUser,
            failHeaders: new() { ["Retry-After"] = "0" });

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient,
            new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 10 });

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(10, handler.RequestCount); // 9 failures + 1 success
        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public void GetOrCreate_ReturnsCachedPipeline_ForSameOptions()
    {
        ResiliencePipelineFactory.Reset();
        var options = new ResilientGraphClientOptions { NoRateLimit = true };
        var (pipeline1, _) = ResiliencePipelineFactory.GetOrCreate(options);
        var (pipeline2, _) = ResiliencePipelineFactory.GetOrCreate(options);

        Assert.Same(pipeline1, pipeline2); // same reference = cached
        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public void GetOrCreate_RebuildsPipeline_WhenOptionsChange()
    {
        ResiliencePipelineFactory.Reset();
        var options1 = new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 3 };
        var (pipeline1, _) = ResiliencePipelineFactory.GetOrCreate(options1);

        var options2 = new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 5 };
        var (pipeline2, _) = ResiliencePipelineFactory.GetOrCreate(options2);

        Assert.NotSame(pipeline1, pipeline2); // different options object = rebuilt
        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public void GetOrCreate_CreatesRateLimiter_WhenEnabled()
    {
        ResiliencePipelineFactory.Reset();
        var options = new ResilientGraphClientOptions { NoRateLimit = false };
        var (_, rateLimiter) = ResiliencePipelineFactory.GetOrCreate(options);

        Assert.NotNull(rateLimiter);
        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public void GetOrCreate_ReturnsNullRateLimiter_WhenDisabled()
    {
        ResiliencePipelineFactory.Reset();
        var options = new ResilientGraphClientOptions { NoRateLimit = true };
        var (_, rateLimiter) = ResiliencePipelineFactory.GetOrCreate(options);

        Assert.Null(rateLimiter);
        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public void Options_DefaultValues_AreCorrect()
    {
        var options = ResilientGraphClientOptions.Default;
        Assert.Equal(200, options.RateLimitBurst);
        Assert.Equal(50, options.RateLimitPerSecond);
        Assert.False(options.NoRateLimit);
        Assert.Equal(500, options.RateLimitQueueLimit);
        Assert.Equal(7, options.MaxRetryAttempts);
        Assert.Equal(120, options.MaxRetryAfterSeconds);
        Assert.Equal(300, options.TotalTimeoutSeconds);
        Assert.Equal(30, options.AttemptTimeoutSeconds);
        Assert.Equal(15, options.CircuitBreakerDurationSeconds);
        Assert.Equal(0.1, options.CircuitBreakerFailureRatio);
        Assert.Equal(40, options.CircuitBreakerMinThroughput);
        Assert.Equal(30, options.CircuitBreakerSamplingDurationSeconds);
        Assert.Equal(1, options.BatchChunkConcurrency);
        Assert.Equal(20, options.BatchItemsPerSecond);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(51)]
    public void MaxRetryAttempts_RejectsInvalidValues(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ResilientGraphClientOptions { MaxRetryAttempts = value });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3601)]
    public void TotalTimeoutSeconds_RejectsInvalidValues(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ResilientGraphClientOptions { TotalTimeoutSeconds = value });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(301)]
    public void AttemptTimeoutSeconds_RejectsInvalidValues(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ResilientGraphClientOptions { AttemptTimeoutSeconds = value });
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public void CircuitBreakerFailureRatio_RejectsInvalidValues(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ResilientGraphClientOptions { CircuitBreakerFailureRatio = value });
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void CircuitBreakerFailureRatio_AcceptsValidValues(double value)
    {
        var options = new ResilientGraphClientOptions { CircuitBreakerFailureRatio = value };
        Assert.Equal(value, options.CircuitBreakerFailureRatio);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(1001)]
    public void CircuitBreakerMinThroughput_RejectsInvalidValues(int value)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new ResilientGraphClientOptions { CircuitBreakerMinThroughput = value });
        // Refused in the option's own terms, with the range it will take. Polly refuses 1 as
        // well, but it does so while a later call is building the pipeline.
        Assert.Contains("between 2 and 1,000", ex.Message);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(40)]
    [InlineData(1000)]
    public void CircuitBreakerMinThroughput_EveryAcceptedValueBuildsAPipeline(int value)
    {
        // The floor, the default and the ceiling. A value the option takes but the pipeline
        // cannot hold would fail on the next request instead of at the set site, which is the
        // whole point of the option validating at all.
        ResiliencePipelineFactory.Reset();
        try
        {
            var options = new ResilientGraphClientOptions
            {
                NoRateLimit = true,
                CircuitBreakerMinThroughput = value
            };

            var (pipeline, _) = ResiliencePipelineFactory.GetOrCreate(options);

            Assert.NotNull(pipeline);
        }
        finally
        {
            ResiliencePipelineFactory.Reset();
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(301)]
    public void CircuitBreakerSamplingDurationSeconds_RejectsInvalidValues(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ResilientGraphClientOptions { CircuitBreakerSamplingDurationSeconds = value });
    }

    [Theory]
    [InlineData(5)]
    [InlineData(30)]
    [InlineData(300)]
    public void CircuitBreakerSamplingDurationSeconds_AcceptsValidValues(int value)
    {
        var options = new ResilientGraphClientOptions { CircuitBreakerSamplingDurationSeconds = value };
        Assert.Equal(value, options.CircuitBreakerSamplingDurationSeconds);
    }

    [Fact]
    public void RateLimitQueueLimit_RejectsNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ResilientGraphClientOptions { RateLimitQueueLimit = -1 });
    }

    [Fact]
    public void RateLimitQueueLimit_AcceptsZero()
    {
        var options = new ResilientGraphClientOptions { RateLimitQueueLimit = 0 };
        Assert.Equal(0, options.RateLimitQueueLimit);
    }

    [Fact]
    public async Task GetOrCreate_LeavesTheOldRateLimiterUsable()
    {
        ResiliencePipelineFactory.Reset();

        var options1 = new ResilientGraphClientOptions
        {
            NoRateLimit = false,
            TotalTimeoutSeconds = 1
        };
        var (_, rateLimiter1) = ResiliencePipelineFactory.GetOrCreate(options1);
        Assert.NotNull(rateLimiter1);

        var options2 = new ResilientGraphClientOptions
        {
            NoRateLimit = false,
            TotalTimeoutSeconds = 1,
            RateLimitBurst = 200
        };
        var (_, rateLimiter2) = ResiliencePipelineFactory.GetOrCreate(options2);
        Assert.NotNull(rateLimiter2);
        Assert.NotSame(rateLimiter1, rateLimiter2);

        var lease = rateLimiter1!.AttemptAcquire();
        lease.Dispose();  // Clean up lease

        // Past the TotalTimeoutSeconds window a timer-based dispose would have used
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        // Every ResilientGraphClient captures its limiter as a readonly field, and disposing a
        // retired limiter on a timer left SDK cmdlets throwing ObjectDisposedException later
        using (var stillWorks = rateLimiter1.AttemptAcquire())
        {
            Assert.NotNull(stillWorks);
        }

        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task Reset_LeavesTheOldRateLimiterUsable()
    {
        ResiliencePipelineFactory.Reset();

        var options = new ResilientGraphClientOptions
        {
            NoRateLimit = false,
            TotalTimeoutSeconds = 1
        };
        var (_, rateLimiter) = ResiliencePipelineFactory.GetOrCreate(options);
        Assert.NotNull(rateLimiter);

        ResiliencePipelineFactory.Reset();

        var lease = rateLimiter!.AttemptAcquire();
        lease.Dispose();

        // Past the TotalTimeoutSeconds window a timer-based dispose would have used
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        // In-flight clients hold this instance and disposing it would break them mid-request
        using (var stillWorks = rateLimiter.AttemptAcquire())
        {
            Assert.NotNull(stillWorks);
        }

        ResiliencePipelineFactory.Reset();
    }
}
