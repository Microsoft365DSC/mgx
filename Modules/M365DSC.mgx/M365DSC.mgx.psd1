@{
    RootModule        = 'M365DSC.mgx.psm1'
    ModuleVersion     = '2.0.1'
    GUID              = 'f978315f-75c0-48f5-b929-ca7a7757d1d2'
    Author            = 'Thomas Maillo Grome, Fabien Tschanz'
    CompanyName       = 'Mgx'
    Copyright         = '(c) 2026 Thomas Maillo Grome, (c) 2026 Fabien Tschanz. All rights reserved.'
    Description       = 'Resilient companion for Microsoft.Graph PowerShell. Adds retry, circuit breaker, rate limiting, streaming pagination, batching, and fan-out to any Graph API endpoint.'

    PowerShellVersion = '7.6'
    CompatiblePSEditions = @('Core')

    FormatsToProcess  = @('M365DSC.mgx.Format.ps1xml')

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
            LicenseUri   = 'https://github.com/Microsoft365DSC/mgx/blob/main/LICENSE'
            ProjectUri   = 'https://github.com/Microsoft365DSC/mgx'
            ReleaseNotes = @'
v2.0.1
- Fixed an issue where Mgx cmdlets kept using the credentials of the first Connect-MgGraph call in a session. The cached HTTP client was keyed on tenant id alone, so reconnecting to the same tenant with a different application, certificate, account, or scope set silently reused the previous identity and kept returning Forbidden. It is now keyed on the whole auth context and rebuilt as soon as that changes.
- Fixed an issue where Enable-MgxResilience's wrapper around the Microsoft.Graph SDK client stayed bound to the pre-reconnect client. Resilience is now re-injected automatically when the identity changes.
- The auth context is read from GraphSession directly instead of invoking Get-MgContext on every request; Get-MgContext remains the fallback.
- Fixed an issue where a single throttling episode slowed Invoke-MgxBatchRequest for the rest of the session. The halved write pacing rate now recovers - clean chunks raise it, and five minutes without throttling restores the configured rate.
- Fixed an issue where Set-MgxOption -TotalTimeoutSeconds did not reach the HTTP client, whose Timeout is fixed once the first request has been sent. The client is now rebuilt when the value changes.
- Fixed an issue where the internal type cache was never invalidated, so re-importing Microsoft.Graph.Authentication left Mgx resolving GraphSession to the previous assembly's type. The cache is now dropped whenever an assembly loads.
'@
        }
    }
}
