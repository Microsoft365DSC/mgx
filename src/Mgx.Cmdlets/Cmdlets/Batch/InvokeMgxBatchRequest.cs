using System.Collections;
using System.Management.Automation;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mgx.Cmdlets.Base;
using Mgx.Engine.Http;
using Mgx.Engine.Models;
using Mgx.Engine.Pagination;
using Polly.CircuitBreaker;

namespace Mgx.Cmdlets.Cmdlets.Batch;

/// <summary>
/// Invoke-MgxBatchRequest: Bundle multiple Graph API requests into /$batch calls.
/// Supports GET, POST, PATCH, PUT, DELETE with optional request bodies.
/// Auto-chunks into 20-request batches per Graph API limit.
/// Returns Hashtables with Url, Method, Status, and Body keys per request.
/// Preferred over fan-out (Invoke-MgxRequest) for bulk writes: 3-4x faster due to fewer HTTP round-trips.
///
/// Pipeline input can be:
///   - String URLs (for GET, or combined with -Method/-Body for same method/body on all)
///   - Hashtables or PSObjects with Url, Method, Body members (for per-item method/body)
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "MgxBatchRequest", SupportsShouldProcess = true)]
[OutputType(typeof(Hashtable))]
public class InvokeMgxBatchRequest : MgxCmdletBase
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    [Alias("Url")]
    public object[] Uri { get; set; } = [];

    [Parameter]
    [ValidateSet("GET", "POST", "PATCH", "PUT", "DELETE")]
    public string Method { get; set; } = "GET";

    [Parameter]
    public object? Body { get; set; }

    /// <summary>
    /// ConsistencyLevel header added to each individual batch item.
    /// Required when any batch item URL contains $search (Graph advanced query capabilities).
    /// Graph requires this header on each item inside the batch JSON body, not the outer POST.
    /// </summary>
    [Parameter]
    [ArgumentCompleter(typeof(ConsistencyLevelCompleter))]
    public string? ConsistencyLevel { get; set; }

    [Parameter]
    public System.Collections.Hashtable? Headers { get; set; }

    /// <summary>
    /// Throttle priority hint for Graph API. Graph uses this to prioritize requests under throttling pressure.
    /// Valid values: Low, Normal, High. Sets x-ms-throttle-priority header on each batch item.
    /// </summary>
    [Parameter]
    [ValidateSet("Low", "Normal", "High", IgnoreCase = true)]
    [ArgumentCompleter(typeof(ThrottlePriorityCompleter))]
    public string? ThrottlePriority { get; set; }

    /// <summary>Graph API version. Default: v1.0. Use "beta" for preview endpoints.</summary>
    [Parameter]
    [ValidateSet("v1.0", "beta")]
    [ArgumentCompleter(typeof(ApiVersionCompleter))]
    public string ApiVersion { get; set; } = "v1.0";

    [Parameter]
    public string? DeadLetterPath { get; set; }

    /// <summary>
    /// Drain @odata.nextLink in sub-response bodies, merging the pages into each result. Off by
    /// default. Follow-up pages are submitted as further batches, so N partial collections drain
    /// in ceil(N/20) requests per page rather than N.
    /// </summary>
    [Parameter]
    public SwitchParameter FollowNextLink { get; set; }

    /// <summary>Ceiling on pages drained per sub-request. 0, the default, is unlimited.</summary>
    [Parameter]
    [ValidateRange(0, int.MaxValue)]
    public int MaxPage { get; set; }

    private string VersionedBaseUrl => $"{s_graphEndpoint}/{ApiVersion}";

    private readonly List<BatchInput> _collected = [];

    protected override void ProcessRecord()
    {
        foreach (var item in Uri)
        {
            var input = ParsePipelineInput(item);
            if (input != null)
                _collected.Add(input);
        }
    }

    protected override void EndProcessing()
    {
        GraphBatchClient? batchClient = null;
        try
        {
            if (_collected.Count == 0)
            {
                base.EndProcessing();
                return;
            }

            string? resolvedDeadLetterPath = DeadLetterPath != null
                ? GetUnresolvedProviderPathFromPSPath(DeadLetterPath)
                : null;

            var hasSearch = _collected.Any(c =>
                c.Url.Contains("$search", StringComparison.OrdinalIgnoreCase));
            if (hasSearch && string.IsNullOrEmpty(ConsistencyLevel))
            {
                ThrowTerminatingError(new ErrorRecord(
                    new ArgumentException(
                        "One or more batch URLs contain $search, which requires -ConsistencyLevel eventual. "
                        + "Without it, Graph returns empty or incomplete results."),
                    "ConsistencyLevelRequired", ErrorCategory.InvalidArgument, null));
                return;
            }

            // -WhatIf is documented as "the cmdlet is not run", without qualification, so the
            // gate covers reads as well. A read-only batch changes nothing on the server, but it
            // spends resource units, can be throttled, and emits objects into the pipeline -
            // none of which is "not run", and none of which the caller asked for.
            var writeOps = _collected.Where(c =>
                !string.Equals(c.Method, "GET", StringComparison.OrdinalIgnoreCase)).ToList();

            string target;
            if (writeOps.Count == 0)
            {
                target = $"GET {_collected.Count} requests via $batch";
            }
            else if (writeOps.All(o => string.Equals(o.Method, writeOps[0].Method, StringComparison.OrdinalIgnoreCase)))
            {
                target = $"{writeOps[0].Method} {_collected.Count} requests via $batch";
            }
            else
            {
                var breakdown = writeOps
                    .GroupBy(o => o.Method.ToUpperInvariant())
                    .OrderByDescending(g => g.Count())
                    .Select(g => $"{g.Count()} {g.Key}");
                target = $"{_collected.Count} requests ({string.Join(", ", breakdown)}) via $batch";
            }

            if (!ShouldProcess(target, "Send batch"))
                return;

            var client = GetClient();
            var mergedHeaders = Headers != null ? new System.Collections.Hashtable(Headers) : null;
            if (!string.IsNullOrEmpty(ThrottlePriority))
            {
                mergedHeaders ??= new System.Collections.Hashtable();
                mergedHeaders["x-ms-throttle-priority"] = ThrottlePriority;
            }
            var itemHeaders = BuildRequestHeaders(ConsistencyLevel, mergedHeaders);
            batchClient = new GraphBatchClient(client, VersionedBaseUrl,
                s_clientOptions.MaxRetryAfterSeconds, s_clientOptions.BatchChunkConcurrency,
                s_clientOptions.NoRateLimit ? 0 : s_clientOptions.BatchItemsPerSecond)
            {
                VerboseWriter = msg => WriteVerbose(msg),
                ItemHeaders = itemHeaders
            };

            // Convert to BatchOperation list. An item whose body is not valid JSON fails on
            // its own (non-terminating error) instead of aborting the whole batch. `submitted`
            // keeps result indices aligned with the operations actually sent.
            var operations = new List<BatchOperation>(_collected.Count);
            var submitted = new List<BatchInput>(_collected.Count);
            foreach (var input in _collected)
            {
                JsonElement? body = null;
                if (input.Body != null)
                {
                    var json = InvokeMgxRequest.SerializeBody(input.Body);
                    try
                    {
                        body = JsonSerializer.Deserialize<JsonElement>(json);
                    }
                    catch (JsonException ex)
                    {
                        WriteError(new ErrorRecord(
                            new ArgumentException(
                                $"Body for {input.Method} {input.Url} is not valid JSON: {ex.Message}", ex),
                            "InvalidBatchItemBody", ErrorCategory.InvalidArgument, input.Url));
                        continue;
                    }
                }

                operations.Add(new BatchOperation(NormalizeToRelativeUrl(input.Url), input.Method, body, input.Id));
                submitted.Add(input);
            }

            if (operations.Count == 0)
                return;

            var batchResult = batchClient.ExecuteBatchIndexedAsync(operations, CancellationToken)
                .GetAwaiter().GetResult();

            var results = batchResult.Results;
            var telemetry = batchResult.Telemetry;

            var drains = FollowNextLink.IsPresent
                ? DrainNextLinks(batchClient, results)
                : [];

            for (int i = 0; i < results.Count; i++)
            {
                var (_, item) = results[i];
                var input = submitted[i];

                var result = new Hashtable(StringComparer.OrdinalIgnoreCase)
                {
                    ["Url"] = input.Url,
                    ["Method"] = input.Method,
                    ["Status"] = item.Status,
                    ["Body"] = item.Body.HasValue && item.Body.Value.ValueKind != JsonValueKind.Null
                        ? JsonToHashtable(item.Body.Value)
                        : null
                };

                if (drains.TryGetValue(i, out var drain))
                    ApplyDrain(result, drain, input);

                // Only when the caller supplied one, so output is unchanged for callers that did not
                if (input.Id != null)
                    result["Id"] = input.Id;

                // Status 0 means the operation was never sent, because a chunk before it failed.
                // It is not a success and must not read as one - the caller has to be able to
                // tell a write that may have landed from one that certainly did not.
                if (item.Status == GraphBatchClient.NotSentStatus)
                    result["NotSent"] = true;

                // Single-argument WriteObject does not enumerate, so the Hashtable is emitted whole
                WriteObject(result);
            }

            // A chunk's POST failed after earlier chunks were applied. Their results are above;
            // this says why the rest never went, and NotSent names them.
            if (batchResult.ChunkFailure != null)
            {
                var notSent = batchResult.NotSent.Count;
                WriteError(new ErrorRecord(batchResult.ChunkFailure, "BatchChunkFailed",
                    ErrorCategory.NotSpecified,
                    $"{notSent} of {results.Count} operations were not sent"));
            }

            if (resolvedDeadLetterPath != null)
            {
                var failedCount = 0;
                try
                {
                    using var writer = new StreamWriter(resolvedDeadLetterPath, append: true);
                    for (int i = 0; i < results.Count; i++)
                    {
                        var (_, item) = results[i];
                        if (item.Status < 400 && item.Status != GraphBatchClient.NotSentStatus) continue;

                        var input = submitted[i];
                        var deadLetter = new JsonObject
                        {
                            ["Timestamp"] = DateTime.UtcNow.ToString("o"),
                            ["Url"] = input.Url,
                            ["Method"] = input.Method,
                            ["Status"] = item.Status,
                        };

                        // Carried so a dead-letter replay round-trips the caller correlation key
                        if (input.Id != null)
                            deadLetter["Id"] = input.Id;

                        if (input.Body != null)
                        {
                            var bodyJson = InvokeMgxRequest.SerializeBody(input.Body);
                            var bodyNode = JsonNode.Parse(bodyJson);
                            RedactSensitiveFields(bodyNode);
                            deadLetter["Body"] = bodyNode;
                        }

                        var errorMsg = TryExtractBatchErrorMessage(item);
                        if (errorMsg != null)
                            deadLetter["Error"] = errorMsg;

                        writer.WriteLine(deadLetter.ToJsonString());
                        failedCount++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    WriteWarning($"Failed to write dead-letter file '{resolvedDeadLetterPath}': {ex.Message}");
                }

                if (failedCount > 0)
                    WriteVerbose($"Wrote {failedCount} failed items to dead-letter file: {resolvedDeadLetterPath}");
            }

            // Per-item errors, so -ErrorAction Stop trips and $Error is populated
            for (int i = 0; i < results.Count; i++)
            {
                var (_, item) = results[i];
                if (item.Status == GraphBatchClient.NotSentStatus)
                {
                    var skipped = submitted[i];
                    WriteError(new ErrorRecord(
                        new InvalidOperationException(
                            $"{skipped.Method} {skipped.Url} was not sent: an earlier chunk failed."),
                        "BatchItemNotSent", ErrorCategory.NotSpecified, skipped.Url));
                }
                else if (item.Status >= 400)
                {
                    var input = submitted[i];
                    var graphMessage = TryExtractBatchErrorMessage(item);
                    var errorMessage = graphMessage != null
                        ? $"{input.Method} {input.Url}: {graphMessage}"
                        : $"HTTP {item.Status} for {input.Method} {input.Url}";
                    var itemError = new InvalidOperationException(errorMessage);
                    WriteError(new ErrorRecord(itemError, "BatchItemError",
                        MapStatusToCategory((HttpStatusCode)item.Status), input.Url));
                }
            }

            WriteBatchTelemetry(telemetry);
        }
        catch (Exception ex) when (ex is GraphServiceException or BrokenCircuitException or HttpRequestException)
        {
            WriteGraphError(ex, null);
        }
        catch (JsonException ex)
        {
            WriteError(new ErrorRecord(ex, "BatchSerializationError",
                ErrorCategory.InvalidData, null));
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
        {
            WriteWarning("Batch request cancelled by user.");
        }
        finally
        {
            // Drain verbose messages even on exception so retry/throttle history is visible
            DrainClientMessages();
            batchClient?.DrainVerboseMessages();
            base.EndProcessing();
        }
    }

    /// <summary>
    /// Parse pipeline input into a BatchInput. Supports:
    /// - String: use as URL with shared -Method/-Body parameters
    /// - Hashtable or PSObject with a Url member: use per-item Url/Method/Body, and an optional Id
    ///   echoed back on the matching result
    /// </summary>
    internal BatchInput? ParsePipelineInput(object item)
    {
        var value = UnwrapPSObject(item);

        if (value is string url)
        {
            return new BatchInput(url, Method, Body);
        }

        // Structured batch input: hashtable (including this cmdlet's own output) or PSCustomObject
        if (value is IDictionary or PSObject)
        {
            var urlValue = TryGetMember(value, "Url")?.ToString();
            if (urlValue != null)
            {
                var method = (TryGetMember(value, "Method")?.ToString() ?? Method).ToUpperInvariant();
                if (method is not ("GET" or "POST" or "PATCH" or "PUT" or "DELETE"))
                {
                    WriteWarning($"Skipping invalid HTTP method '{method}' for URL: {urlValue}");
                    return null;
                }
                var body = TryGetMember(value, "Body");
                // The caller id is theirs to choose and is echoed back on the result. The wire id
                // GraphBatchClient assigns is regenerated per retry attempt and stays internal
                var id = TryGetMember(value, "Id")?.ToString();
                return new BatchInput(urlValue, method, body, id);
            }
        }

        WriteWarning($"Skipping unrecognized pipeline input: {item}");
        return null;
    }

    private string NormalizeToRelativeUrl(string url)
    {
        if (url.StartsWith('/'))
            return url;

        if (url.StartsWith(VersionedBaseUrl, StringComparison.OrdinalIgnoreCase))
            return url[VersionedBaseUrl.Length..];

        if (System.Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var path = uri.PathAndQuery;
            string[] knownPrefixes = ["/v1.0/", "/beta/"];
            foreach (var prefix in knownPrefixes)
            {
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var version = prefix.Trim('/');
                    if (!string.Equals(ApiVersion, version, StringComparison.OrdinalIgnoreCase))
                        WriteWarning($"URL contains {prefix} but -ApiVersion is '{ApiVersion}'. The batch will use {ApiVersion}.");
                    return path[(prefix.Length - 1)..]; // -1 to keep leading slash
                }
            }
            return path;
        }

        WriteWarning($"Could not normalize URL to relative path: {url}");
        return url;
    }

    /// <summary>
    /// Redact sensitive fields (passwordProfile, credentials, secrets) from a JSON body
    /// before writing to the dead-letter file. Modifies the node in-place.
    /// </summary>
    internal static void RedactSensitiveFields(JsonNode? node)
    {
        if (node is JsonArray rootArr)
        {
            foreach (var item in rootArr)
                if (item is JsonObject arrObj)
                    RedactSensitiveFields(arrObj);
            return;
        }
        if (node is not JsonObject obj) return;
        foreach (var key in obj.Select(p => p.Key).ToArray())
        {
            if (key.Equals("passwordProfile", StringComparison.OrdinalIgnoreCase)
                || key.Equals("password", StringComparison.OrdinalIgnoreCase)
                || key.Equals("secretText", StringComparison.OrdinalIgnoreCase)
                || key.Equals("keyCredentials", StringComparison.OrdinalIgnoreCase)
                || key.Equals("passwordCredentials", StringComparison.OrdinalIgnoreCase)
                || key.Equals("clientSecret", StringComparison.OrdinalIgnoreCase)
                || key.Equals("appPassword", StringComparison.OrdinalIgnoreCase)
                || key.Equals("clientAssertion", StringComparison.OrdinalIgnoreCase))
            {
                obj[key] = "***REDACTED***";
            }
            else if (obj[key] is JsonObject child)
            {
                RedactSensitiveFields(child);
            }
            else if (obj[key] is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is JsonObject arrObj)
                        RedactSensitiveFields(arrObj);
                }
            }
        }
    }

    private static string? TryExtractBatchErrorMessage(GraphBatchResponseItem item)
    {
        if (!item.Body.HasValue) return null;
        try
        {
            var body = item.Body.Value;
            if (body.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var c) ? c.GetString() : null;
                var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
                if (!string.IsNullOrEmpty(code) && !string.IsNullOrEmpty(message))
                    return $"{code}: {message}";
                return code ?? message;
            }
        }
        catch (InvalidOperationException) { }
        return null;
    }

    private void WriteBatchTelemetry(BatchTelemetry telemetry)
    {
        if (telemetry.ThrottleEncounters > 0)
            MgxTelemetryCollector.Current.RecordBatchItemThrottles(telemetry.ThrottleEncounters);

        // Propagate item-retry delay time so Get-MgxTelemetry's RetryDelayMs reflects
        // batch retry waits
        if (telemetry.TotalRetryDelayMs > 0)
            MgxTelemetryCollector.Current.RecordBatchRetryDelay(telemetry.TotalRetryDelayMs);

        var elapsedSec = telemetry.TotalElapsedMs / 1000.0;
        var throughput = telemetry.TotalElapsedMs > 0 ? telemetry.TotalRequests / elapsedSec : 0;
        var summary = $"Batch: {telemetry.Succeeded} succeeded, {telemetry.Failed} failed out of {telemetry.TotalRequests} requests in {elapsedSec:F1}s ({throughput:F1}/sec).";
        if (telemetry.ItemRetries > 0)
            summary += $" Item retries: {telemetry.ItemRetries}.";
        if (telemetry.ThrottleEncounters > 0)
            summary += $" Throttle (429) encounters: {telemetry.ThrottleEncounters}.";
        if (telemetry.BatchLevelRetries > 0)
            summary += $" Batch-level retries: {telemetry.BatchLevelRetries}.";
        if (telemetry.TotalRetryDelayMs > 0)
            summary += $" Time in retry delays: {telemetry.TotalRetryDelayMs / 1000.0:F1}s.";
        WriteVerbose(summary);

        if (telemetry.Failed > 0)
        {
            WriteWarning(
                $"{telemetry.Failed} of {telemetry.TotalRequests} batch items failed after all retry attempts. "
                + "Check $Error for details on each failed item.");
        }
    }

    internal sealed record BatchInput(string Url, string Method, object? Body, string? Id = null);

    /// <summary>
    /// What draining produced for one sub-request: the extra items collected, and the failure that
    /// stopped it if it did not drain fully.
    /// </summary>
    private sealed class PageDrain
    {
        public List<JsonElement> Extra { get; } = [];
        public int? FailedStatus { get; set; }
        public string? FailedReason { get; set; }
        public string? FailedErrorId { get; set; }
        public bool Incomplete => FailedStatus.HasValue;
    }

    private static string? ReadNextLink(JsonElement? body)
    {
        if (body is not { ValueKind: JsonValueKind.Object } obj) return null;
        return obj.TryGetProperty("@odata.nextLink", out var link) && link.ValueKind == JsonValueKind.String
            ? link.GetString()
            : null;
    }

    private static IEnumerable<JsonElement> ReadValueItems(JsonElement? body)
    {
        if (body is not { ValueKind: JsonValueKind.Object } obj) yield break;
        if (!obj.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in value.EnumerateArray()) yield return item.Clone();
    }

    /// <summary>
    /// Follow @odata.nextLink for every sub-response that carries one, submitting each round as a
    /// further batch so the round-trip advantage of batching is kept.
    /// </summary>
    private Dictionary<int, PageDrain> DrainNextLinks(
        GraphBatchClient batchClient,
        IReadOnlyList<(BatchOperation Operation, GraphBatchResponseItem Response)> results)
    {
        var drains = new Dictionary<int, PageDrain>();
        var pending = new Dictionary<int, string>();
        var expectedHost = new Uri(s_graphEndpoint);

        for (var i = 0; i < results.Count; i++)
        {
            var link = ReadNextLink(results[i].Response.Body);
            if (link != null) pending[i] = link;
        }

        var page = 0;
        while (pending.Count > 0 && (MaxPage == 0 || page < MaxPage))
        {
            page++;
            var indices = new List<int>(pending.Count);
            var ops = new List<BatchOperation>(pending.Count);

            foreach (var (index, link) in pending)
            {
                var drain = drains.TryGetValue(index, out var d) ? d : drains[index] = new PageDrain();

                // The link comes out of a response body, so it is validated like every other
                // nextLink in the module before anything follows it
                if (NextLinkValidator.Validate(link, expectedHost) == null)
                {
                    drain.FailedStatus = results[index].Response.Status;
                    drain.FailedReason = $"the service returned an @odata.nextLink that failed validation ({link})";
                    drain.FailedErrorId = "BatchNextLinkRefused";
                    continue;
                }

                indices.Add(index);
                ops.Add(new BatchOperation(NormalizeToRelativeUrl(link), "GET"));
            }

            pending.Clear();
            if (ops.Count == 0) break;

            var followUp = batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken).GetAwaiter().GetResult();
            for (var j = 0; j < followUp.Results.Count; j++)
            {
                var index = indices[j];
                var drain = drains[index];
                var response = followUp.Results[j].Response;

                if (response.Status >= 400 || response.Status == GraphBatchClient.NotSentStatus)
                {
                    drain.FailedStatus = response.Status;
                    drain.FailedReason = "a page of the collection could not be read";
                    drain.FailedErrorId = "BatchPagingFailed";
                    continue;
                }

                drain.Extra.AddRange(ReadValueItems(response.Body));

                var next = ReadNextLink(response.Body);
                if (next != null) pending[index] = next;
            }
        }

        // Whatever is still pending ran into the ceiling rather than the end of the collection
        foreach (var (index, _) in pending)
        {
            var drain = drains.TryGetValue(index, out var d) ? d : drains[index] = new PageDrain();
            drain.FailedStatus = results[index].Response.Status;
            drain.FailedReason = $"stopped after -MaxPage {MaxPage} pages with more to read";
            drain.FailedErrorId = "BatchPagingTruncated";
        }

        return drains;
    }

    /// <summary>
    /// Fold a drain into the result: merge the extra pages into Body.value, and if the drain stopped
    /// early mark the result failed rather than letting a short collection read as a complete one.
    /// </summary>
    private void ApplyDrain(Hashtable result, PageDrain drain, BatchInput input)
    {
        if (result["Body"] is Hashtable body)
        {
            if (drain.Extra.Count > 0)
            {
                var merged = new List<object?>();
                if (body["value"] is IEnumerable existing and not string)
                    foreach (var item in existing) merged.Add(item);
                foreach (var item in drain.Extra) merged.Add(JsonToHashtable(item));
                body["value"] = merged.ToArray();
            }

        }

        if (!drain.Incomplete) return;

        // The failing page's status, not the first page's 200. A partially drained collection is a
        // failed read, and PagingIncomplete separates "nothing arrived" from "some of it did"
        result["Status"] = drain.FailedStatus!.Value;
        result["PagingIncomplete"] = true;

        var message = $"{input.Method} {input.Url}: {drain.FailedReason}. "
            + $"{drain.Extra.Count} additional item(s) were read before it stopped.";
        WriteError(new ErrorRecord(new InvalidOperationException(message),
            drain.FailedErrorId!, ErrorCategory.LimitsExceeded, input.Url));
    }
}
