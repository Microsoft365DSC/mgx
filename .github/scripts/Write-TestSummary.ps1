#Requires -Version 7.0

<#
    .SYNOPSIS
        Writes the merged test and coverage tables to the GitHub Actions run summary.

    .DESCRIPTION
        Reads the xUnit .trx files and pester-summary.json written by the test jobs, and appends
        the combined counts followed by the ReportGenerator coverage tables.

    .PARAMETER ArtifactRoot
        Directory the job artifacts were downloaded into.

    .PARAMETER CoverageSummary
        ReportGenerator markdown summary to append after the test table.

    .PARAMETER SummaryPath
        File to append to. Defaults to the Actions run summary.
#>
[CmdletBinding()]
param
(
    [Parameter()]
    [System.String]
    $ArtifactRoot = 'artifacts',

    [Parameter()]
    [System.String]
    $CoverageSummary = 'coverage/report/SummaryGithub.md',

    [Parameter()]
    [System.String]
    $SummaryPath = $env:GITHUB_STEP_SUMMARY
)

$ErrorActionPreference = 'Stop'

$rows = [System.Collections.Generic.List[object]]::new()

foreach ($trx in Get-ChildItem -Path $ArtifactRoot -Recurse -Filter '*.trx' -ErrorAction SilentlyContinue)
{
    $counters = ([xml](Get-Content -Path $trx.FullName -Raw)).TestRun.ResultSummary.Counters
    $label = if ($trx.Name -match 'E2E') { 'xUnit - cmdlet E2E (Linux, WireMock)' }
             else                        { 'xUnit - engine and cmdlet units' }

    $rows.Add([pscustomobject]@{
        Suite   = $label
        Passed  = [int]$counters.passed
        Failed  = [int]$counters.failed + [int]$counters.error
        Skipped = [int]$counters.notExecuted
    })
}

foreach ($json in Get-ChildItem -Path $ArtifactRoot -Recurse -Filter 'pester-summary.json' -ErrorAction SilentlyContinue)
{
    $pester = Get-Content -Path $json.FullName -Raw | ConvertFrom-Json
    $rows.Add([pscustomobject]@{
        Suite   = $pester.Suite
        Passed  = [int]$pester.Passed
        Failed  = [int]$pester.Failed
        Skipped = [int]$pester.Skipped
    })
}

$summary = [System.Collections.Generic.List[string]]::new()
$summary.Add('## Test Results')
$summary.Add('')

if ($rows.Count -eq 0)
{
    $summary.Add('> No test results were found. Check whether the test jobs ran.')
}
else
{
    $summary.Add('| Suite | Passed | Failed | Skipped |')
    $summary.Add('| :--- | ---: | ---: | ---: |')

    foreach ($row in ($rows | Sort-Object -Property Suite))
    {
        $summary.Add("| $($row.Suite) | $($row.Passed) | $($row.Failed) | $($row.Skipped) |")
    }

    $summary.Add("| **Total** | **$(($rows | Measure-Object -Property Passed -Sum).Sum)** | " +
                 "**$(($rows | Measure-Object -Property Failed -Sum).Sum)** | " +
                 "**$(($rows | Measure-Object -Property Skipped -Sum).Sum)** |")
}

$summary.Add('')

if (Test-Path -Path $CoverageSummary)
{
    $summary.Add((Get-Content -Path $CoverageSummary -Raw))
}
else
{
    $summary.Add('## Code Coverage')
    $summary.Add('')
    $summary.Add('> No coverage report was produced. Check the `XPlat Code Coverage` collector output.')
}

$summary | Out-File -FilePath $SummaryPath -Append -Encoding utf8
