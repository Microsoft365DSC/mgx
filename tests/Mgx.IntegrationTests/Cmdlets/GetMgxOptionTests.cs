using System.Management.Automation;
using Mgx.Cmdlets.Cmdlets.Configuration;
using Mgx.Cmdlets.Models;

namespace Mgx.IntegrationTests.Cmdlets;

/// <summary>
/// Tests for Get-MgxOption cmdlet.
/// </summary>
public class GetMgxOptionTests
{
    [Fact]
    public void ProcessRecord_ReturnsMgxOptionOutputWithAllProperties()
    {
        var options = Mgx.Cmdlets.Base.MgxCmdletBase.s_clientOptions;

        var result = new MgxOptionOutput
        {
            RateLimitBurst = options.RateLimitBurst,
            RateLimitPerSecond = options.RateLimitPerSecond,
            NoRateLimit = options.NoRateLimit,
            RateLimitQueueLimit = options.RateLimitQueueLimit,
            MaxRetryAttempts = options.MaxRetryAttempts,
            MaxRetryAfterSeconds = options.MaxRetryAfterSeconds,
            TotalTimeoutSeconds = options.TotalTimeoutSeconds,
            AttemptTimeoutSeconds = options.AttemptTimeoutSeconds,
            CircuitBreakerDurationSeconds = options.CircuitBreakerDurationSeconds,
            CircuitBreakerFailureRatio = options.CircuitBreakerFailureRatio,
            CircuitBreakerMinThroughput = options.CircuitBreakerMinThroughput,
            CircuitBreakerSamplingDurationSeconds = options.CircuitBreakerSamplingDurationSeconds,
            BatchChunkConcurrency = options.BatchChunkConcurrency,
            BatchItemsPerSecond = options.BatchItemsPerSecond,
        };

        Assert.Equal(options.RateLimitBurst, result.RateLimitBurst);
        Assert.Equal(options.RateLimitPerSecond, result.RateLimitPerSecond);
        Assert.Equal(options.NoRateLimit, result.NoRateLimit);
        Assert.Equal(options.RateLimitQueueLimit, result.RateLimitQueueLimit);
        Assert.Equal(options.MaxRetryAttempts, result.MaxRetryAttempts);
        Assert.Equal(options.MaxRetryAfterSeconds, result.MaxRetryAfterSeconds);
        Assert.Equal(options.TotalTimeoutSeconds, result.TotalTimeoutSeconds);
        Assert.Equal(options.AttemptTimeoutSeconds, result.AttemptTimeoutSeconds);
        Assert.Equal(options.CircuitBreakerDurationSeconds, result.CircuitBreakerDurationSeconds);
        Assert.Equal(options.CircuitBreakerFailureRatio, result.CircuitBreakerFailureRatio);
        Assert.Equal(options.CircuitBreakerMinThroughput, result.CircuitBreakerMinThroughput);
        Assert.Equal(options.CircuitBreakerSamplingDurationSeconds, result.CircuitBreakerSamplingDurationSeconds);
        Assert.Equal(options.BatchChunkConcurrency, result.BatchChunkConcurrency);
        Assert.Equal(options.BatchItemsPerSecond, result.BatchItemsPerSecond);
    }
}