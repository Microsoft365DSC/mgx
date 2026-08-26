using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using Mgx.Cmdlets.Base;
using Mgx.Cmdlets.Cmdlets;
using Mgx.Cmdlets.Cmdlets.Batch;
using Mgx.Cmdlets.Cmdlets.Configuration;
using Mgx.Cmdlets.Cmdlets.Delta;
using Mgx.Cmdlets.Cmdlets.Expand;
using Mgx.Cmdlets.Cmdlets.Export;
using Mgx.Engine.Http;

namespace Mgx.E2ETests.Infrastructure;

public sealed record MgxResult(
    IReadOnlyList<PSObject> Output,
    IReadOnlyList<ErrorRecord> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Verbose,
    ErrorRecord? Terminating);

/// <summary>
/// Hosts the Mgx cmdlets in an in-process runspace. Cmdlets are registered against the types this
/// assembly already references rather than by importing the built module, because importing would
/// load Mgx.Cmdlets.dll a second time from another path and give MgxCmdletBase a second set of
/// statics, leaving the test transport seam silently inert.
/// </summary>
public sealed class MgxCmdletHost : IDisposable
{
    private static readonly Type[] CmdletTypes =
    [
        typeof(InvokeMgxRequest), typeof(InvokeMgxBatchRequest), typeof(SyncMgxDelta),
        typeof(ExportMgxCollection), typeof(ExpandMgxRelation),
        typeof(SetMgxOption), typeof(GetMgxOption), typeof(GetMgxTelemetry),
        typeof(GetMgxResilience), typeof(EnableMgxResilience), typeof(DisableMgxResilience)
    ];

    /// <summary>Tuned so a test never waits on a rate limiter, a backoff, or a circuit breaker.</summary>
    public static ResilientGraphClientOptions FastOptions => new()
    {
        NoRateLimit = true,
        MaxRetryAttempts = 2,
        AttemptTimeoutSeconds = 10,
        TotalTimeoutSeconds = 30,
        CircuitBreakerMinThroughput = 1000,
        MaxRetryAfterSeconds = 1,
        BatchItemsPerSecond = 0
    };

    private readonly Runspace _runspace;

    public MgxCmdletHost(HttpClient transport, string graphEndpoint,
        ResilientGraphClientOptions? options = null)
    {
        // CreateDefault would throw here because the SMA package ships without the
        // Microsoft.PowerShell.Commands assemblies it expects.
        var iss = InitialSessionState.CreateDefault2();
        foreach (var type in CmdletTypes)
        {
            var attribute = type.GetCustomAttribute<CmdletAttribute>()!;
            iss.Commands.Add(new SessionStateCmdletEntry(
                $"{attribute.VerbName}-{attribute.NounName}", type, helpFileName: null));
        }

        _runspace = RunspaceFactory.CreateRunspace(iss);
        _runspace.Open();

        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();
        GraphBatchClient.ResetPacingState();
        MgxCmdletBase.SetClientOptions(options ?? FastOptions);
        MgxCmdletBase.s_graphEndpoint = graphEndpoint;
        MgxCmdletBase.s_testTransportFactory = () => transport;
    }

    public MgxResult Run(Action<PowerShell> build)
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        build(ps);

        ErrorRecord? terminating = null;
        var output = new List<PSObject>();
        try
        {
            output.AddRange(ps.Invoke());
        }
        catch (RuntimeException ex)
        {
            terminating = ex.ErrorRecord;
        }

        return new MgxResult(
            output,
            [.. ps.Streams.Error],
            [.. ps.Streams.Warning.Select(w => w.Message)],
            [.. ps.Streams.Verbose.Select(v => v.Message)],
            terminating);
    }

    public void Dispose()
    {
        MgxCmdletBase.s_testTransportFactory = null;
        MgxCmdletBase.s_graphEndpoint = "https://graph.microsoft.com";
        MgxCmdletBase.SetClientOptions(ResilientGraphClientOptions.Default);
        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();
        GraphBatchClient.ResetPacingState();
        _runspace.Dispose();
    }
}
