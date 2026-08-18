#Requires -Version 7.0

<#
    .SYNOPSIS
        Pester harness for the mgx module.

    .DESCRIPTION
        Wraps Invoke-Pester so CI and local runs share one entry point.
        These tests cover the PowerShell-facing surface only: the module manifest,
        the format file, and the cmdlet/parameter contract of the built module.

        Engine and cmdlet internals (HTTP retry, pagination, JSON conversion) are
        covered by the xUnit suite in Tests/Mgx.IntegrationTests, run via `dotnet test`.
#>

$script:RepoRoot = Split-Path -Parent $PSScriptRoot
$script:ModuleName = 'M365DSC.mgx'
$script:ModuleRoot = Join-Path $script:RepoRoot 'Modules' $script:ModuleName
$script:ManifestPath = Join-Path $script:ModuleRoot "$script:ModuleName.psd1"

function Get-MgxTestPath
{
    <#
        .SYNOPSIS
            Paths the tests need, resolved from the repository layout.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param ()

    return @{
        RepoRoot     = $script:RepoRoot
        ModuleName   = $script:ModuleName
        ModuleRoot   = $script:ModuleRoot
        ManifestPath = $script:ManifestPath
        FormatPath   = Join-Path $script:ModuleRoot "$script:ModuleName.Format.ps1xml"
        HelpRoot     = Join-Path $script:ModuleRoot 'help'
    }
}

function Invoke-TestHarness
{
    <#
        .SYNOPSIS
            Run the Pester test suite for the mgx module.

        .PARAMETER TestResultsFile
            NUnit XML results path. Defaults to Tests/TestResults.xml.

        .PARAMETER IgnoreCodeCoverage
            Skip code coverage. Coverage of a binary module from Pester is not
            meaningful (there is no PowerShell source to instrument), so coverage
            is never collected; the switch exists for CI call compatibility.

        .PARAMETER TestPath
            Directory to search for *.Tests.ps1. Defaults to Tests/Unit.

        .OUTPUTS
            The Pester run object. Callers check $result.FailedCount.
    #>
    [CmdletBinding()]
    param
    (
        [Parameter()]
        [System.String]
        $TestResultsFile = (Join-Path $PSScriptRoot 'TestResults.xml'),

        [Parameter()]
        [Switch]
        $IgnoreCodeCoverage,

        [Parameter()]
        [System.String]
        $TestPath = (Join-Path $PSScriptRoot 'Unit')
    )

    $pesterModule = Get-Module -Name Pester -ListAvailable |
        Sort-Object -Property Version -Descending |
        Select-Object -First 1

    if ($null -eq $pesterModule)
    {
        throw 'Pester is not installed. Run: Install-PSResource -Name Pester -TrustRepository'
    }

    if ($pesterModule.Version.Major -lt 5)
    {
        throw "Pester 5.0 or later is required; found $($pesterModule.Version)."
    }

    Import-Module -Name $pesterModule.Path -Force

    # The module must be built before the surface tests can inspect it
    if (-not (Test-Path -Path $script:ManifestPath))
    {
        throw "Module manifest not found at '$script:ManifestPath'. Run ./build.ps1 first."
    }

    $configuration = New-PesterConfiguration
    $configuration.Run.Path = $TestPath
    $configuration.Run.PassThru = $true
    $configuration.Output.Verbosity = 'Detailed'
    $configuration.TestResult.Enabled = $true
    $configuration.TestResult.OutputFormat = 'NUnitXml'
    $configuration.TestResult.OutputPath = $TestResultsFile

    # Binary module: there is no PowerShell code to instrument, so coverage is
    # never enabled. -IgnoreCodeCoverage is accepted for CI call compatibility.
    $configuration.CodeCoverage.Enabled = $false

    if (-not $IgnoreCodeCoverage.IsPresent)
    {
        Write-Verbose -Message 'Code coverage is not collected for a binary module; continuing without it.'
    }

    $results = Invoke-Pester -Configuration $configuration

    Write-TestHarnessSummary -Result $results

    return $results
}

<#
.SYNOPSIS
    Renders the results of Invoke-TestHarness as a report.

.DESCRIPTION
    Writes the test counts and the per file code coverage of the run. Without a path the report
    goes to the console, with a path it is appended as GitHub flavoured markdown, which makes it
    usable as a GitHub Actions step summary.

.PARAMETER Result
    The object returned by Invoke-TestHarness.

.PARAMETER Path
    File to append the markdown report to.

.EXAMPLE
    Write-TestHarnessSummary -Result $results -Path $env:GITHUB_STEP_SUMMARY
#>
function Write-TestHarnessSummary
{
    [CmdletBinding()]
    [OutputType([System.Void])]
    param
    (
        [Parameter(Mandatory = $true)]
        [PSCustomObject]
        $Result,

        [Parameter()]
        [System.String]
        $Path
    )

    $lines = [System.Collections.Generic.List[System.String]]::new()

    $lines.Add('## Unit Test Results')
    $lines.Add('')
    $lines.Add('| Passed | Failed | Skipped |')
    $lines.Add('| ---: | ---: | ---: |')
    $lines.Add("| $($Result.PassedCount) | $($Result.FailedCount) | $($Result.SkippedCount) |")
    $lines.Add('')

    $coverage = $Result.CodeCoverage
    if ($null -ne $coverage)
    {
        $lines.Add('## Code Coverage')
        $lines.Add('')
        $lines.Add("**$([System.Math]::Round($coverage.CoveragePercent, 2))%** of $($coverage.CommandsAnalyzedCount) commands covered.")
        $lines.Add('')
        $lines.Add('| File | Covered | Missed |')
        $lines.Add('| :--- | ---: | ---: |')

        $perFile = @{}
        foreach ($command in @($coverage.CommandsExecuted) + @($coverage.CommandsMissed))
        {
            if ($null -eq $command)
            {
                continue
            }

            if (-not $perFile.ContainsKey($command.File))
            {
                $perFile[$command.File] = @{ Analyzed = 0; Missed = 0 }
            }

            $perFile[$command.File].Analyzed++
        }

        foreach ($command in @($coverage.CommandsMissed))
        {
            if ($null -eq $command)
            {
                continue
            }

            $perFile[$command.File].Missed++
        }

        foreach ($file in ($perFile.Keys | Sort-Object))
        {
            $analyzed = $perFile[$file].Analyzed
            $missed = $perFile[$file].Missed
            $percentage = [System.Math]::Round(($analyzed - $missed) / $analyzed * 100, 2)
            $lines.Add("| $(Split-Path -Path $file -Leaf) | $percentage% | $missed |")
        }

        $lines.Add('')
    }

    if ([System.String]::IsNullOrEmpty($Path))
    {
        $lines | ForEach-Object { Write-Host -Object $_ }
    }
    else
    {
        $lines | Out-File -FilePath $Path -Append -Encoding utf8
    }
}

Export-ModuleMember -Function Invoke-TestHarness, Get-MgxTestPath, Write-TestHarnessSummary
