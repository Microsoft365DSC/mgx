using System.Management.Automation;
using System.Net;
using System.Reflection;
using Mgx.Engine.Http;
using Mgx.Cmdlets.Base;

namespace Mgx.Cmdlets.Cmdlets.Configuration;

/// <summary>
/// Injects Polly resilience (retry, circuit breaker, rate limiting) into the
/// Microsoft.Graph SDK's HTTP transport. After calling this, all SDK cmdlets
/// (Get-MgUser, Get-MgGroup, etc.) automatically gain resilience with zero
/// script changes required.
///
/// Wraps the existing SDK HttpClient (preserving its full handler chain:
/// ODataQueryOptionsHandler, NationalCloudHandler, RedirectHandler,
/// AuthenticationHandler, etc.) with a ResilientDelegatingHandler on top.
///
/// Calling Enable-MgxResilience when already enabled re-injects if the SDK
/// reset the client (e.g., after Connect-MgGraph or Set-MgRequestContext).
/// The SDK's built-in RetryHandler still runs inside the wrapped chain;
/// retries can compound, bounded by TotalTimeoutSeconds and circuit breaker.
/// </summary>
[Cmdlet(VerbsLifecycle.Enable, "MgxResilience", SupportsShouldProcess = true)]
public class EnableMgxResilience : PSCmdlet
{
    // Lock protecting all static state transitions. Used by both Enable and Disable.
    internal static readonly object StateLock = new();

    internal static HttpClient? OriginalSdkClient { get; set; }
    internal static HttpClient? ResilientSdkClient { get; set; }
    internal static bool IsEnabled { get; set; }
    internal static ResilientDelegatingHandler? ActiveHandler { get; set; }

    protected override void ProcessRecord()
    {
        lock (StateLock)
        {
            var graphSessionType = MgxCmdletBase.FindType(
                "Microsoft.Graph.PowerShell.Authentication.GraphSession");
            if (graphSessionType == null)
            {
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException(
                        "Microsoft.Graph.Authentication module not loaded. Run Connect-MgGraph first."),
                    "GraphSessionNotFound", ErrorCategory.ObjectNotFound, null));
                return;
            }

            var instance = graphSessionType.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (instance == null)
            {
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException("GraphSession.Instance is null. Run Connect-MgGraph first."),
                    "GraphSessionNull", ErrorCategory.InvalidOperation, null));
                return;
            }

            var clientProp = instance.GetType().GetProperty("GraphHttpClient");
            var currentClient = clientProp?.GetValue(instance) as HttpClient;

            // Pre-initialize Mgx's own HTTP client before the SDK probe runs.
            // The probe calls Invoke-MgGraphRequest which changes Azure Identity internal
            // state and breaks GetAuthenticationProviderAsync for subsequent callers.
            // Building Mgx's clean client first ensures it is cached before that happens.
            MgxCmdletBase.TryPreInitHttpClient(WriteWarning, WriteVerbose);

            if (currentClient == null)
            {
                WriteVerbose("GraphHttpClient not initialized. Triggering initialization...");
                currentClient = ForceInitializeAndGetClient(instance, clientProp);
            }

            if (currentClient == null)
            {
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException(
                        "Could not access GraphHttpClient. Ensure Connect-MgGraph has been called."),
                    "HttpClientNotFound", ErrorCategory.ConnectionError, null));
                return;
            }

            if (IsEnabled)
            {
                if (ReferenceEquals(currentClient, ResilientSdkClient))
                {
                    WriteVerbose("MgxResilience is already active.");
                    return;
                }
                // Our client was replaced (e.g., by Connect-MgGraph or Set-MgRequestContext).
                // Dispose the old wrapped client to release its handler chain and sockets.
                WriteVerbose("MgxResilience was reset by SDK. Re-injecting resilience...");
                // Not disposed: HttpClient.Dispose cancels its pending-request token source and
                // the bridge handler forwards that token inward, so SDK requests already in
                // flight die. Restoring GraphSession.GraphHttpClient stops new traffic. The old
                // client is collected once the requests still using it finish.
                _ = ResilientSdkClient;
                ResilientSdkClient = null;
                // Reset circuit breaker / rate limiter state from the previous tenant
                ResiliencePipelineFactory.Reset();
            }

            if (!ShouldProcess("Microsoft.Graph SDK HttpClient",
                "Replace with Polly resilience pipeline (retry, circuit breaker, rate limiting)"))
                return;

            OriginalSdkClient = currentClient;

            var resilientClient = BuildResilientSdkClient(currentClient, WriteWarning);
            if (resilientClient == null)
            {
                OriginalSdkClient = null;
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException(
                        "Failed to build resilient HTTP client."),
                    "ResilientClientBuildFailed", ErrorCategory.InvalidOperation, null));
                return;
            }

            clientProp!.SetValue(instance, resilientClient);
            ResilientSdkClient = resilientClient;
            IsEnabled = true;

            WriteVerbose("MgxResilience enabled. All Microsoft.Graph SDK cmdlets now use " +
                          "Polly retry, circuit breaker, and rate limiting.");
        }
    }

    private HttpClient? ForceInitializeAndGetClient(object instance, PropertyInfo? clientProp)
    {
        var endpoint = MgxCmdletBase.GetGraphEndpoint(WriteWarning, WriteVerbose) ?? "https://graph.microsoft.com";

        // Save AzureADEndpoint before probe. Invoke-MgGraphRequest replaces
        // GraphSession.Environment with a new object that has an empty AzureADEndpoint,
        // which permanently breaks GetAuthenticationProviderAsync (GetAuthorityUrl returns
        // "/tenantId" instead of "https://login.microsoftonline.com/tenantId").
        var envObj = instance.GetType().GetProperty("Environment")?.GetValue(instance);
        var savedAadEndpoint = envObj?.GetType().GetProperty("AzureADEndpoint")?.GetValue(envObj)?.ToString();

        using var ps = PowerShell.Create(RunspaceMode.CurrentRunspace);
        ps.AddCommand("Invoke-MgGraphRequest")
            .AddParameter("Method", "GET")
            .AddParameter("Uri", $"{endpoint}/v1.0/organization?$top=1&$select=id")
            .AddParameter("ErrorAction", "Stop")
            .AddParameter("WarningAction", "SilentlyContinue"); // suppress incidental probe warnings
        ps.AddCommand("Out-Null"); // suppress output from flowing to caller's pipeline
        try { ps.Invoke(); }
        catch (Exception ex)
        {
            WriteVerbose($"Initialization probe failed (expected): {ex.Message}");
        }

        // Restore AzureADEndpoint on the NEW Environment object the probe created.
        MgxCmdletBase.RestoreAzureADEndpoint(instance, savedAadEndpoint, WriteVerbose);

        return clientProp?.GetValue(instance) as HttpClient;
    }

    /// <summary>
    /// Re-injects resilience after the Graph identity changed. Connect-MgGraph builds a new
    /// SDK HttpClient, so the wrapper installed by Enable-MgxResilience is left bridging to a
    /// client that still carries the previous credentials. Called by MgxCmdletBase.GetClient()
    /// once it detects a new identity, which keeps SDK cmdlets (Get-MgUser and friends) both
    /// resilient and correctly authenticated without a second Enable-MgxResilience call.
    ///
    /// Lock ordering: never call this while holding MgxCmdletBase's init lock. ProcessRecord
    /// takes StateLock and then that lock via TryPreInitHttpClient, so the reverse order can
    /// deadlock.
    /// </summary>
    internal static void RefreshInjectedClient(Action<string> warn, Action<string> verbose)
    {
        lock (StateLock)
        {
            if (!IsEnabled) return;

            var instance = MgxCmdletBase.TryGetGraphSessionInstance();
            var clientProp = instance?.GetType().GetProperty("GraphHttpClient");
            if (instance == null || clientProp == null)
            {
                IsEnabled = false;
                warn("Graph identity changed but GraphSession is unavailable, so Mgx resilience "
                    + "could not be re-injected. Run Enable-MgxResilience again.");
                return;
            }

            var currentClient = clientProp.GetValue(instance) as HttpClient;
            if (ReferenceEquals(currentClient, ResilientSdkClient))
            {
                // The SDK kept our wrapper, so it still bridges to the pre-reconnect client.
                // Clear it and let the SDK rebuild lazily against the new AuthContext.
                clientProp.SetValue(instance, null);
                currentClient = null;
            }

            // Not disposed - see the note above. In-flight SDK requests would be cancelled.
            _ = ResilientSdkClient;
            ResilientSdkClient = null;
            ActiveHandler = null;
            OriginalSdkClient = null;
            ResiliencePipelineFactory.Reset();

            if (currentClient == null)
            {
                IsEnabled = false;
                warn("Graph identity changed. Mgx resilience was removed from the Microsoft.Graph "
                    + "SDK client; run Enable-MgxResilience again to re-inject it.");
                return;
            }

            var refreshed = BuildResilientSdkClient(currentClient, warn);
            if (refreshed == null)
            {
                IsEnabled = false;
                warn("Graph identity changed but resilience could not be re-injected into the "
                    + "Microsoft.Graph SDK client. Run Enable-MgxResilience again.");
                return;
            }

            OriginalSdkClient = currentClient;
            clientProp.SetValue(instance, refreshed);
            ResilientSdkClient = refreshed;
            verbose("Re-injected Mgx resilience into the Microsoft.Graph SDK client "
                + "after the Graph identity changed.");
        }
    }

    /// <summary>
    /// Turns off the SDK own retry handler for requests passing through the wrap.
    ///
    /// That handler sits inside the wrapped chain and answers 429 and 503 itself, so a throttle
    /// never reaches the Mgx pipeline. The pacer learns nothing, telemetry books a throttled
    /// session as zero retries, and the two retriers compound.
    ///
    /// Kiota reads this option per request. The type is resolved reflectively, so neither
    /// assembly needs a reference to it and a rename leaves the wrap working as before.
    ///
    /// Throws rather than warns, because it runs on a request thread with no pipeline to write
    /// a warning to. The handler catches it and carries on with the inner handler as it was.
    /// </summary>
    internal static IReadOnlyDictionary<string, object?>? BuildInnerRetryOverride()
    {
        const string OptionType = "Microsoft.Kiota.Http.HttpClientLibrary.Middleware.Options.RetryHandlerOption";

        var type = MgxCmdletBase.FindType(OptionType)
            ?? throw new InvalidOperationException(
                "the Graph SDK's retry option type was not found, so its own retry handler stays "
                + "active inside the wrap and throttling will not reach Mgx's pacer or telemetry "
                + "on this path");

        var option = Activator.CreateInstance(type);
        var maxRetry = type.GetProperty("MaxRetry");
        if (option == null || maxRetry == null || !maxRetry.CanWrite)
            throw new InvalidOperationException(
                "the Graph SDK's retry option could not be configured, so its own retry handler "
                + "stays active inside the wrap");

        // MaxRetry is the only lever that removes a retry. ShouldRetry cannot suppress one,
        // because the handler ORs it with its own status check.
        // The cost is that the handler 503 and 504 retries go too, including on writes, which
        // the Mgx pipeline will not take over since it refuses to retry a non-idempotent
        // request on a
        // 5xx because the write may already have been applied. 429 is unaffected - the pipeline
        // retries that for every method - so throttled writes still complete.
        maxRetry.SetValue(option, 0);

        return new Dictionary<string, object?> { [OptionType] = option };
    }

    private static HttpClient? BuildResilientSdkClient(HttpClient sdkClient, Action<string> warn)
    {
        try
        {
            var (pipeline, rateLimiter) = ResiliencePipelineFactory.GetOrCreate(MgxCmdletBase.s_clientOptions);

            // Wrap the existing SDK client, preserving its full handler chain, with the
            // resilience layer on top. The chain is ResilientDelegatingHandler, then
            // SdkClientBridgeHandler, then the SDK client and its own handlers.
            // AdditionalRequestOptionsFactory disarms the SDK retry handler per request. If that
            // cannot be arranged it stays armed and the session behaves as before, without the
            // measurement. Both paths share one pipeline, rate limiter and circuit breaker
            var resilientHandler = new ResilientDelegatingHandler(pipeline, rateLimiter)
            {
                InnerHandler = new SdkClientBridgeHandler(sdkClient),
                AdditionalRequestOptionsFactory = BuildInnerRetryOverride
            };
            ActiveHandler = resilientHandler;

            // BaseAddress must come along: Invoke-MgGraphRequest resolves a relative -Uri
            // against the active client's BaseAddress before any handler runs. Default
            // request headers are NOT copied - the bridge delegates to sdkClient.SendAsync,
            // which applies the original client's defaults to each request anyway.
            return new HttpClient(resilientHandler)
            {
                BaseAddress = sdkClient.BaseAddress,
                Timeout = sdkClient.Timeout
            };
        }
        catch (Exception ex)
        {
            warn($"Failed to build resilient client: {ex.Message}");
            return null;
        }
    }

    private sealed class SdkClientBridgeHandler : HttpMessageHandler
    {
        private readonly HttpClient _sdkClient;
        internal SdkClientBridgeHandler(HttpClient sdkClient) => _sdkClient = sdkClient;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => _sdkClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }
}
