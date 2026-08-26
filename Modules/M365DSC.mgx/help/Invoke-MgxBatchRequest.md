---
external help file: Mgx.Cmdlets.dll-Help.xml
Module Name: Mgx
online version: https://github.com/Microsoft365DSC/mgx/blob/main/Modules/M365DSC.mgx/help/Invoke-MgxBatchRequest.md
schema: 2.0.0
---

# Invoke-MgxBatchRequest

## SYNOPSIS
Bundle multiple Graph API requests into /$batch calls.

## SYNTAX

```
Invoke-MgxBatchRequest [-Uri] <Object[]> [-Method <String>] [-Body <Object>] [-ConsistencyLevel <String>]
 [-Headers <Hashtable>] [-ThrottlePriority <String>] [-ApiVersion <String>] [-ProgressAction <ActionPreference>]
 [-WhatIf] [-Confirm] [<CommonParameters>]
```

## DESCRIPTION
Invoke-MgxBatchRequest bundles multiple Microsoft Graph API requests into /$batch calls, sending up to 20 requests per HTTP round-trip (the Graph API maximum). This is 3-4x faster than individual requests for bulk operations.

Supports GET, POST, PATCH, PUT, and DELETE methods with optional request bodies. Auto-chunks input into 20-request batches.

Source: [Combine multiple HTTP requests using JSON batching](https://learn.microsoft.com/en-us/graph/json-batching)

Pipeline input can be string URLs (for GET, or combined with -Method/-Body for the same operation on all) or hashtables or PSCustomObjects with Url, Method, and Body members for per-item control. An optional Id member is echoed back on the matching result, so you can correlate on your own key instead of on URL or position.

Failed items are surfaced as PowerShell ErrorRecords. Use -ErrorAction Stop to halt on the first failure, or inspect $Error after completion.

## EXAMPLES

### Example 1: Batch GET multiple users
```powershell
@("/users/id1", "/users/id2", "/users/id3") | Invoke-MgxBatchRequest
```

Retrieves three users in a single HTTP round-trip.

### Example 2: Batch POST to create multiple entities
```powershell
$requests = 1..100 | ForEach-Object {
    [PSCustomObject]@{
        Url = "/users"
        Method = "POST"
        Body = @{
            displayName = "User $_"
            mailNickname = "user$_"
            userPrincipalName = "user$_@contoso.com"
            accountEnabled = $true
            passwordProfile = @{ password = "P@ss$(Get-Random)!" }
        }
    }
}
$requests | Invoke-MgxBatchRequest
```

Creates 100 users in 5 batches of 20. Each result includes Url, Status, and Body properties.

### Example 3: Batch PATCH with shared body
```powershell
@("/users/id1", "/users/id2") | Invoke-MgxBatchRequest -Method PATCH -Body @{ department = "HR" }
```

Updates the department for two users in a single batch call.

### Example 4: Batch DELETE multiple entities
```powershell
# Delete all decommissioned users in batches of 20
$users = Invoke-MgxRequest /users -Filter "department eq 'Decommissioned'" -All -Property id
$users | ForEach-Object { "/users/$($_.id)" } | Invoke-MgxBatchRequest -Method DELETE
# For large-scale deletes (1,000+), chunk and report progress:
# $urls = $users | ForEach-Object { "/users/$($_.id)" }
# for ($i = 0; $i -lt $urls.Count; $i += 1000) {
#     $urls[$i..([math]::Min($i+999, $urls.Count-1))] |
#         Invoke-MgxBatchRequest -Method DELETE -ErrorAction SilentlyContinue
# }
```

Deletes all matching users in batches of 20. Failed items (e.g., 404 for already-deleted) are emitted as ErrorRecords. Use `-ErrorAction SilentlyContinue` to suppress expected 404s during cleanup.

### Example 5: Deprioritize background cleanup under throttling
```powershell
$staleGroups | ForEach-Object { "/groups/$($_.id)" } |
    Invoke-MgxBatchRequest -Method DELETE -ThrottlePriority Low -ErrorAction SilentlyContinue
```

Sets `x-ms-throttle-priority: Low` on each batch item, telling Graph to throttle these requests first if the tenant is under pressure. Useful for background cleanup jobs that shouldn't compete with interactive workloads.

### Example 6: Custom per-item headers
```powershell
@("/users/id1", "/users/id2") | Invoke-MgxBatchRequest -Headers @{
    "Prefer" = "outlook.body-content-type=text"
}
```

Passes custom headers to each individual batch item. Headers are merged with -ConsistencyLevel (if specified).

### Example 7: Search with ConsistencyLevel
```powershell
@('/users?$search="displayName:John"', '/groups?$search="displayName:Sales"') |
    Invoke-MgxBatchRequest -ConsistencyLevel eventual
```

Batches search queries that require the ConsistencyLevel header.

### Example 8: Capture failures to a dead-letter file
```powershell
1..1000 | ForEach-Object {
    [PSCustomObject]@{ Url = "/users"; Method = "POST"; Body = @{ displayName = "User-$_"; mailNickname = "user$_"; userPrincipalName = "user$_@contoso.com"; passwordProfile = @{ password = "P@ss$(Get-Random -Minimum 10000)!" } } }
} | Invoke-MgxBatchRequest -DeadLetterPath ./failed-users.jsonl
```

Creates 1000 users via batch. Any failures (status >= 400) are appended to the JSONL dead-letter file with Url, Method, Body (passwords redacted), Status, and Error. Sensitive fields like passwordProfile are automatically replaced with ***REDACTED***.

### Example 9: Pipeline usage at scale (recommended pattern)
```powershell
# Correct: pipe all items into one call
$urls = 1..10000 | ForEach-Object { "/users/$_" }
$urls | Invoke-MgxBatchRequest

# Avoid: calling in a tight loop - many more round-trips than one chunked call
foreach ($url in $urls) { $url | Invoke-MgxBatchRequest }
```

Always pipe all items into a single Invoke-MgxBatchRequest call. This is both faster (one pipeline setup) and avoids PowerShell pipeline threading issues at high invocation rates.

## PARAMETERS

### -ApiVersion
Graph API version. Default: v1.0. Use "beta" for preview endpoints.

```yaml
Type: String
Parameter Sets: (All)
Aliases:
Accepted values: v1.0, beta

Required: False
Position: Named
Default value: v1.0
Accept pipeline input: False
Accept wildcard characters: False
```

### -Body
Request body for all requests when piping string URLs. Ignored when pipeline input carries its own Body member.

```yaml
Type: Object
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```


### -FollowNextLink
Drain `@odata.nextLink` in sub-response bodies, merging the pages into each result. Off by default.

A batched sub-request against a collection returns only its first page, and `/$batch` does not page. Without this switch each such result is a partial collection, and you cannot tell: the conversion to a hashtable strips `@odata.nextLink` along with the rest of the `@odata.*` metadata, so a truncated read looks exactly like a complete one.

Follow-up pages are submitted as further batches rather than per-item GETs, so N partial collections drain in ceil(N/20) requests per page.

If a page cannot be read - an HTTP failure, or a `nextLink` that fails host validation - that one sub-request is marked failed: `Status` becomes the failing page's status, `PagingIncomplete` is set to `$true`, and a non-terminating error is written (`BatchPagingFailed`, `BatchNextLinkRefused`, or `BatchPagingTruncated`). The items already collected stay in `Body`, so you can use them deliberately. One failed link never discards the other results in the batch.

```yaml
Type: SwitchParameter
Required: False
Position: Named
Default value: False
Accept pipeline input: False
Accept wildcard characters: False
```

### -MaxPage
Ceiling on the number of pages drained per sub-request when `-FollowNextLink` is used. The default, 0, is unlimited. Hitting the ceiling marks the result `PagingIncomplete` and writes a `BatchPagingTruncated` error, so a bounded read is never mistaken for a complete one.

```yaml
Type: Int32
Required: False
Position: Named
Default value: 0
Accept pipeline input: False
Accept wildcard characters: False
```
### -DeadLetterPath
Path to a JSONL file where failed batch items (status >= 400) are appended, along with any that were never sent because an earlier chunk failed. Each line contains Url, Method, Body (with sensitive fields redacted), Status, Error, Timestamp, and the Id if one was supplied. It is a record of what to retry, not a request you can replay directly: the redaction that keeps passwords out of the file also means a redacted Body is no longer the body that was sent. Read it to decide what to resubmit, and supply the sensitive fields again yourself.

```yaml
Type: String
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -Confirm
Prompts you for confirmation before running the cmdlet.

```yaml
Type: SwitchParameter
Parameter Sets: (All)
Aliases: cf

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -ConsistencyLevel
ConsistencyLevel header added to each individual batch item. Required when any batch item URL contains $search (Graph advanced query capabilities). Takes precedence over the same key in -Headers. Source: [Advanced query capabilities on Microsoft Entra ID objects](https://learn.microsoft.com/en-us/graph/aad-advanced-queries)

### -Headers
Custom headers applied to each individual batch item. Accepts a hashtable of key-value pairs. Merged with -ConsistencyLevel and -ThrottlePriority (dedicated parameters take precedence over matching keys in -Headers).

```yaml
Type: Hashtable
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -ThrottlePriority
Throttle priority hint for Graph API. Graph uses this to decide which requests to throttle first under pressure. Valid values: Low, Normal, High.

Sets `x-ms-throttle-priority` header on each batch item. Use `Low` for background jobs that should yield to interactive workloads.

```yaml
Type: String
Parameter Sets: (All)
Aliases:
Accepted values: Low, Normal, High

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

```yaml
Type: String
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -Method
HTTP method for all requests when piping string URLs. Default: GET. Ignored when pipeline input carries its own Method member.

```yaml
Type: String
Parameter Sets: (All)
Aliases:
Accepted values: GET, POST, PATCH, PUT, DELETE

Required: False
Position: Named
Default value: GET
Accept pipeline input: False
Accept wildcard characters: False
```

### -Uri
Graph API URLs to batch. Accepts absolute URLs (https://graph.microsoft.com/v1.0/users/id) or relative URLs (/users/id). Also accepts hashtables or PSCustomObjects with Url, Method, and Body members for per-item control, plus an optional Id that is echoed back on the matching result.

```yaml
Type: Object[]
Parameter Sets: (All)
Aliases: Url

Required: True
Position: 0
Default value: None
Accept pipeline input: True (ByValue)
Accept wildcard characters: False
```

### -WhatIf
Shows what would happen if the cmdlet runs. The cmdlet is not run.

```yaml
Type: SwitchParameter
Parameter Sets: (All)
Aliases: wi

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -ProgressAction
Determines how the cmdlet responds to progress updates.

```yaml
Type: ActionPreference
Parameter Sets: (All)
Aliases: proga

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### CommonParameters
This cmdlet supports the common parameters: -Debug, -ErrorAction, -ErrorVariable, -InformationAction, -InformationVariable, -OutVariable, -OutBuffer, -PipelineVariable, -Verbose, -WarningAction, and -WarningVariable. For more information, see [about_CommonParameters](http://go.microsoft.com/fwlink/?LinkID=113216).

## INPUTS

### System.Object[]
String URLs, or hashtables or PSCustomObjects with Url, Method, and Body members.

## OUTPUTS

### System.Collections.Hashtable
Per-request results with Url, Method, Status, and Body keys, plus Id when the request supplied one, NotSent on any operation that was never sent because an earlier chunk failed, and PagingIncomplete when -FollowNextLink could not drain the collection. Body here is the RESPONSE body - for a failure that is the error envelope, not the request - so piping results straight back resubmits the wrong thing. Use Url and Method to rebuild the requests you want to retry.

## NOTES
Each batch item is retried individually on 429 (throttled) or 5xx errors (for idempotent methods). POST requests only retry on 429 because POST is non-idempotent - retrying a failed POST on 5xx could create duplicates if the server processed the request before the error. This matches the Kiota SDK retry behavior. Source: [Microsoft Graph error responses and resource types](https://learn.microsoft.com/en-us/graph/errors)

Items that exhaust per-chunk retries get one additional batch-level retry pass.

Use -Verbose to see retry counts, throttle encounters, and timing.

Batching reduces HTTP round-trips (20 operations per request instead of 1), but Graph counts each item inside a batch individually against the server-side write quota (3,000 writes / 2.5 min per app+tenant). Sustained write throughput caps at ~20/sec regardless of batching. See [Set-MgxOption](Set-MgxOption.md) for the full throttle limits table and tuning guidance. Source: [Microsoft Graph service-specific throttling limits](https://learn.microsoft.com/en-us/graph/throttling-limits)

Pipe all items into a single Invoke-MgxBatchRequest call rather than calling it in a loop. One call chunks the items itself, so it makes far fewer round-trips than a loop of single-item calls and reuses one HTTP client, one rate limiter and one circuit breaker. Repeated invocation is safe at any rate the host can drive.

## RELATED LINKS
[Invoke-MgxRequest](Invoke-MgxRequest.md)
[Set-MgxOption](Set-MgxOption.md)
