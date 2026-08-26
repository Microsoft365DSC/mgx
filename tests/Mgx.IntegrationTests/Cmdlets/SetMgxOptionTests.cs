using System.Management.Automation;
using System.Net;
using Mgx.Cmdlets.Cmdlets.Configuration;
using Mgx.Cmdlets.Base;
using Mgx.Engine.Http;
using Mgx.IntegrationTests.Fakes;
using Mgx.IntegrationTests.Infrastructure;
using Mgx.IntegrationTests.Engine;

namespace Mgx.IntegrationTests.Cmdlets;

/// <summary>
/// Tests for Set-MgxOption cmdlet.
/// </summary>
[Collection(ResilienceCollection.Name)]
public class SetMgxOptionTests
{
    private void ResetOptions()
    {
        MgxCmdletBase.SetClientOptions(ResilientGraphClientOptions.Default);
    }

    [Fact]
    public void ProcessRecord_Reset_SetsDefaults()
    {
        ResetOptions();

        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, """{"id":"1"}""");
        using var host = new MgxTestHost(handler);

        // Change some options first (within valid ranges)
        MgxCmdletBase.SetClientOptions(new ResilientGraphClientOptions
        {
            RateLimitBurst = 50,
            RateLimitPerSecond = 50,
            NoRateLimit = true,
            MaxRetryAttempts = 50,
        });

        var result = host.Run(ps =>
        {
            ps.AddCommand("Set-MgxOption").AddParameter("Reset", true);
        });

        var options = MgxCmdletBase.s_clientOptions;
        Assert.Equal(ResilientGraphClientOptions.Default.RateLimitBurst, options.RateLimitBurst);
        Assert.Equal(ResilientGraphClientOptions.Default.RateLimitPerSecond, options.RateLimitPerSecond);
        Assert.Equal(ResilientGraphClientOptions.Default.NoRateLimit, options.NoRateLimit);
        Assert.Equal(ResilientGraphClientOptions.Default.MaxRetryAttempts, options.MaxRetryAttempts);
    }

    [Fact]
    public void ProcessRecord_NoParameters_DoesNothing()
    {
        // Reset to known state first
        ResetOptions();

        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, """{"id":"1"}""");
        using var host = new MgxTestHost(handler);

        var originalOptions = MgxCmdletBase.s_clientOptions;

        var result = host.Run(ps =>
        {
            ps.AddCommand("Set-MgxOption");
        });

        // Options should not change when no parameters provided
        Assert.Equal(originalOptions.RateLimitBurst, MgxCmdletBase.s_clientOptions.RateLimitBurst);
        Assert.Equal(originalOptions.RateLimitPerSecond, MgxCmdletBase.s_clientOptions.RateLimitPerSecond);
        Assert.Equal(originalOptions.NoRateLimit, MgxCmdletBase.s_clientOptions.NoRateLimit);
        Assert.Equal(originalOptions.MaxRetryAttempts, MgxCmdletBase.s_clientOptions.MaxRetryAttempts);
    }

    [Fact]
    public void ProcessRecord_SetsIndividualParameters()
    {
        ResetOptions();

        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, """{"id":"1"}""");
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps =>
        {
            ps.AddCommand("Set-MgxOption")
                .AddParameter("RateLimitBurst", 123)
                .AddParameter("RateLimitPerSecond", 456)
                .AddParameter("MaxRetryAttempts", 7)
                .AddParameter("TotalTimeoutSeconds", 300)
                .AddParameter("AttemptTimeoutSeconds", 30)
                .AddParameter("CircuitBreakerDurationSeconds", 60)
                .AddParameter("CircuitBreakerFailureRatio", 0.5)
                .AddParameter("CircuitBreakerMinThroughput", 10)
                .AddParameter("CircuitBreakerSamplingDurationSeconds", 30)
                .AddParameter("BatchChunkConcurrency", 3)
                .AddParameter("BatchItemsPerSecond", 100);
        });

        var options = MgxCmdletBase.s_clientOptions;
        Assert.Equal(123, options.RateLimitBurst);
        Assert.Equal(456, options.RateLimitPerSecond);
        Assert.Equal(7, options.MaxRetryAttempts);
        Assert.Equal(300, options.TotalTimeoutSeconds);
        Assert.Equal(30, options.AttemptTimeoutSeconds);
        Assert.Equal(60, options.CircuitBreakerDurationSeconds);
        Assert.Equal(0.5, options.CircuitBreakerFailureRatio);
        Assert.Equal(10, options.CircuitBreakerMinThroughput);
        Assert.Equal(30, options.CircuitBreakerSamplingDurationSeconds);
        Assert.Equal(3, options.BatchChunkConcurrency);
        Assert.Equal(100, options.BatchItemsPerSecond);
    }

    [Fact]
    public void ProcessRecord_NoRateLimitImplicitlyDisabled_WhenRateParamsSet()
    {
        ResetOptions();

        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, """{"id":"1"}""");
        using var host = new MgxTestHost(handler);

        MgxCmdletBase.SetClientOptions(new ResilientGraphClientOptions { NoRateLimit = true });

        var result = host.Run(ps =>
        {
            ps.AddCommand("Set-MgxOption").AddParameter("RateLimitBurst", 10);
        });

        var options = MgxCmdletBase.s_clientOptions;
        Assert.False(options.NoRateLimit);
    }

    [Fact]
    public void ProcessRecord_Warns_WhenAttemptTimeoutExceedsTotalTimeout()
    {
        ResetOptions();

        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, """{"id":"1"}""");
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps =>
        {
            ps.AddCommand("Set-MgxOption")
                .AddParameter("AttemptTimeoutSeconds", 100)
                .AddParameter("TotalTimeoutSeconds", 50);
        });

        Assert.Contains(result.Warnings, w => w.Contains("Retries are effectively disabled because the total timeout will fire on the first attempt"));

        var options = MgxCmdletBase.s_clientOptions;
        Assert.Equal(100, options.AttemptTimeoutSeconds);
        Assert.Equal(50, options.TotalTimeoutSeconds);
    }

    [Fact]
    public void WarnIfResilienceActive_WritesWarning_WhenEnabled()
    {
        ResetOptions();

        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, """{"id":"1"}""");
        using var host = new MgxTestHost(handler);

        EnableMgxResilience.IsEnabled = true;

        var result = host.Run(ps =>
        {
            ps.AddCommand("Set-MgxOption").AddParameter("RateLimitBurst", 10);
        });

        Assert.True(EnableMgxResilience.IsEnabled);
        Assert.Contains(result.Warnings, w => w.Contains("MgxResilience is active"));
    }

    [Fact]
    public void ProcessRecord_QueueLimitSet()
    {
        ResetOptions();

        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, """{"id":"1"}""");
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps =>
        {
            ps.AddCommand("Set-MgxOption").AddParameter("RateLimitQueueLimit", 5000);
        });

        var options = MgxCmdletBase.s_clientOptions;
        Assert.Equal(5000, options.RateLimitQueueLimit);
    }

    [Fact]
    public void ProcessRecord_MaxRetryAfterSecondsSet()
    {
        ResetOptions();

        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, """{"id":"1"}""");
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps =>
        {
            ps.AddCommand("Set-MgxOption").AddParameter("MaxRetryAfterSeconds", 120);
        });

        var options = MgxCmdletBase.s_clientOptions;
        Assert.Equal(120, options.MaxRetryAfterSeconds);
    }
}