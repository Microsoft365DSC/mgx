#Requires -Version 7.0

<#
    .SYNOPSIS
        Writes the merged test and coverage report to the GitHub Actions run summary.

    .DESCRIPTION
        Reads the .trx files and pester-summary.json written by the test jobs, plus the merged
        Cobertura report, and writes the same sections Write-TestHarnessSummary produces for a
        local run: C# results, C# coverage, then PowerShell results.

        The C# table carries a Suite column because CI runs two C# suites on two operating
        systems, and collapsing them would hide which one failed.

    .PARAMETER ArtifactRoot
        Directory the job artifacts were downloaded into.

    .PARAMETER CoverageReport
        Merged Cobertura report produced by ReportGenerator.

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
    $CoverageReport = 'coverage/report/Cobertura.xml',

    [Parameter()]
    [System.String]
    $SummaryPath = $env:GITHUB_STEP_SUMMARY
)

$ErrorActionPreference = 'Stop'

Import-Module -Name (Join-Path $PSScriptRoot '..' '..' 'tests' 'TestHarness.psm1') -Force

$lines = [System.Collections.Generic.List[System.String]]::new()

$dotNetSuites = [System.Collections.Generic.List[object]]::new()

foreach ($trx in Get-ChildItem -Path $ArtifactRoot -Recurse -Filter '*.trx' -ErrorAction SilentlyContinue)
{
    $counts = Get-TrxSummary -Path $trx.FullName
    $label = if ($trx.Name -match 'E2E') { 'Cmdlet E2E (Linux, WireMock)' } else { 'Engine and cmdlet units (Windows)' }

    $dotNetSuites.Add([pscustomobject]@{
        Suite   = $label
        Passed  = $counts.Passed
        Failed  = $counts.Failed
        Skipped = $counts.Skipped
    })
}

$lines.Add('## C# Unit Test Results')
$lines.Add('')

if ($dotNetSuites.Count -eq 0)
{
    $lines.Add('> No C# results were found. Check whether the test jobs ran.')
}
else
{
    $lines.Add('| Suite | Passed | Failed | Skipped |')
    $lines.Add('| :--- | ---: | ---: | ---: |')

    foreach ($suite in ($dotNetSuites | Sort-Object -Property Suite))
    {
        $lines.Add("| $($suite.Suite) | $($suite.Passed) | $($suite.Failed) | $($suite.Skipped) |")
    }

    $lines.Add("| **Total** | **$(($dotNetSuites | Measure-Object -Property Passed -Sum).Sum)** | " +
               "**$(($dotNetSuites | Measure-Object -Property Failed -Sum).Sum)** | " +
               "**$(($dotNetSuites | Measure-Object -Property Skipped -Sum).Sum)** |")
}

$lines.Add('')

if (Test-Path -Path $CoverageReport)
{
    $coverage = Get-CoberturaSummary -Path $CoverageReport

    $lines.Add('## C# Code Coverage')
    $lines.Add('')
    $lines.Add("**$($coverage.CoveragePercent)%** of $($coverage.LinesValid) lines covered.")
    $lines.Add('')
    $lines.Add('| Type | Covered | Missed |')
    $lines.Add('| :--- | ---: | ---: |')

    foreach ($class in $coverage.Classes)
    {
        $lines.Add("| $($class.Name) | $($class.CoveragePercent)% | $($class.LinesValid - $class.LinesCovered) |")
    }

    $lines.Add('')
}
else
{
    $lines.Add('## C# Code Coverage')
    $lines.Add('')
    $lines.Add('> No coverage report was produced. Check the `XPlat Code Coverage` collector output.')
    $lines.Add('')
}

foreach ($json in Get-ChildItem -Path $ArtifactRoot -Recurse -Filter 'pester-summary.json' -ErrorAction SilentlyContinue)
{
    $pester = Get-Content -Path $json.FullName -Raw | ConvertFrom-Json

    $lines.Add('## PowerShell Unit Test Results')
    $lines.Add('')
    $lines.Add('| Passed | Failed | Skipped |')
    $lines.Add('| ---: | ---: | ---: |')
    $lines.Add("| $([int]$pester.Passed) | $([int]$pester.Failed) | $([int]$pester.Skipped) |")
    $lines.Add('')
}

$lines | Out-File -FilePath $SummaryPath -Append -Encoding utf8
