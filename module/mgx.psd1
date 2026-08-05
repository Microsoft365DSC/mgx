@{
    RootModule        = 'mgx.psm1'
    ModuleVersion     = '1.0.3'
    GUID              = 'a3f7e8d2-5b4c-4a1e-9f6d-2c8b0e3a7d5f'
    Author            = 'Thomas Maillo Grome'
    CompanyName       = 'Mgx'
    Copyright         = '(c) 2026 Thomas Maillo Grome. All rights reserved.'
    Description       = 'Resilient companion for Microsoft.Graph PowerShell. Adds retry, circuit breaker, rate limiting, streaming pagination, batching, and fan-out to any Graph API endpoint.'

    PowerShellVersion = '7.5'
    CompatiblePSEditions = @('Core')

    FormatsToProcess  = @('mgx.Format.ps1xml')

    # Pre-load Mgx.Engine.dll so it resolves into the same load context
    # as Mgx.Cmdlets.dll. Without this, MgxTelemetrySummary (a record type
    # returned by MgxTelemetryCollector.GetSummary()) fails to load at JIT
    # time with TypeLoadException when Get-MgxTelemetry is called.
    RequiredAssemblies = @('Mgx.Engine.dll')

    RequiredModules   = @(
        @{ ModuleName = 'Microsoft.Graph.Authentication'; ModuleVersion = '2.10.0' }
    )

    CmdletsToExport   = @(
        'Invoke-MgxRequest'
        'Invoke-MgxBatchRequest'
        'Export-MgxCollection'
        'Expand-MgxRelation'
        'Set-MgxOption'
        'Get-MgxOption'
        'Enable-MgxResilience'
        'Disable-MgxResilience'
        'Get-MgxResilience'
        'Get-MgxTelemetry'
        'Sync-MgxDelta'
    )

    FunctionsToExport = @()
    VariablesToExport = @()
    AliasesToExport   = @()

    PrivateData = @{
        PSData = @{
            Tags         = @('Microsoft', 'Graph', 'MicrosoftGraph', 'API', 'Azure', 'EntraID', 'Resilience', 'PowerShell', 'Polly', 'Retry', 'RateLimit', 'Batch', 'Delta', 'Throttling', 'Pagination')
            LicenseUri   = 'https://github.com/gromedev/mgx/blob/main/LICENSE'
            ProjectUri   = 'https://github.com/gromedev/mgx'
            ReleaseNotes = @'
v1.0.3 (BREAKING)
- BREAKING: Invoke-MgxRequest, Invoke-MgxBatchRequest, Expand-MgxRelation, and Sync-MgxDelta now emit case-insensitive Hashtables instead of PSObjects. Graph property order is no longer preserved, and the Mgx.User/Group/Application/ServicePrincipal/DirectoryRole/BatchResult table views were removed (PowerShell always renders a hashtable with the built-in Name/Value view). Use Format-Table or Select-Object to pick columns.
- BREAKING: `@odata.type` is now returned verbatim instead of being renamed to `ODataType`. Update any code reading `.ODataType` to read `.'@odata.type'`. Read results now round-trip cleanly into -Body on PATCH/POST.
- Expand-MgxRelation -InputObject and the Invoke-MgxRequest fan-out pipeline parameter accept hashtables, PSCustomObjects, and (for fan-out) bare ID strings.
- Invoke-MgxBatchRequest accepts hashtables with Url/Method/Body, so its own output can be piped back in.
'@
        }
    }
}
