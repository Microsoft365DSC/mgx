@{
    RootModule        = 'M365DSC.mgx.psm1'
    ModuleVersion     = '2.1.0'
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

    <#
    RequiredModules   = @(
        @{ ModuleName = 'Microsoft.Graph.Authentication'; ModuleVersion = '2.10.0' }
    )
    #>

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
        'Get-MgxContent'
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
v2.1.0
- Added Get-MgxContent, downloading file and media content whole or by byte range.
- Added adaptive request pacing, on by default, spacing requests per workload ahead of the token bucket. Opt out with Set-MgxOption -NoAdaptivePacing.
- Added Sync-MgxDelta -CheckpointPath, -Latest and -Prefer, with drive delta support and crash resume.
- Fixed -Top being discarded when combined with -All.
- Fixed enumeration returning a partial collection without error when a nextLink was refused.
- Fixed -Debug writing pre-authenticated download URLs verbatim.
- Merged upstream gromedev/mgx 2.1.1.
'@
        }
    }
}
