#Requires -Version 7.0

<#
    .SYNOPSIS
        Test harness for the mgx module.

    .DESCRIPTION
        Runs both suites behind one entry point and reports them the same way:
        the xUnit suite in tests/Mgx.IntegrationTests, and the Pester suite in
        tests/Unit covering the PowerShell-facing surface.

        The E2E suite in tests/Mgx.E2ETests is not run here. It needs a Linux
        container host, so CI runs it as its own job.
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

function Get-TrxSummary
{
    <#
        .SYNOPSIS
            Read the pass, fail and skip counts out of a .trx file.

        .PARAMETER Path
            The .trx written by dotnet test.
    #>
    [CmdletBinding()]
    [OutputType([System.Collections.Hashtable])]
    param
    (
        [Parameter(Mandatory = $true)]
        [System.String]
        $Path
    )

    $summary = @{
        Total   = 0
        Passed  = 0
        Failed  = 0
        Skipped = 0
    }

    if (-not (Test-Path -Path $Path))
    {
        return $summary
    }

    $document = [System.Xml.XmlDocument]::new()
    $document.Load($Path)

    $namespaceManager = [System.Xml.XmlNamespaceManager]::new($document.NameTable)
    $namespaceManager.AddNamespace('trx', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')

    $counters = $document.SelectSingleNode('//trx:ResultSummary/trx:Counters', $namespaceManager)
    if ($null -eq $counters)
    {
        return $summary
    }

    $summary.Total = [int]$counters.total
    $summary.Passed = [int]$counters.passed
    # An errored test is a failed test as far as a reader of the summary is concerned
    $summary.Failed = [int]$counters.failed + [int]$counters.error
    $summary.Skipped = [int]$counters.total - [int]$counters.executed

    return $summary
}

function Get-CoberturaSummary
{
    <#
        .SYNOPSIS
            Roll a Cobertura report up to one line-coverage figure per type.

        .PARAMETER Path
            The cobertura XML written by the coverage collector.
    #>
    [CmdletBinding()]
    [OutputType([System.Collections.Hashtable])]
    param
    (
        [Parameter(Mandatory = $true)]
        [System.String]
        $Path
    )

    if (-not (Test-Path -Path $Path))
    {
        return @{
            CoveragePercent = 0
            LinesCovered    = 0
            LinesValid      = 0
            Classes         = @()
        }
    }

    $document = [System.Xml.XmlDocument]::new()
    $document.Load($Path)

    $byType = [ordered]@{}
    $totalLines = 0
    $coveredLines = 0

    foreach ($class in $document.SelectNodes('//class'))
    {
        # Compiler-generated closures and async state machines belong to the type that declares
        # them, so they are folded back into it. The separator is the collector's choice: coverlet
        # writes Type/<Method>d__0, other emitters use . or +
        $typeName = $class.name -replace '[./+]<.*$', ''

        if (-not $byType.Contains($typeName))
        {
            $byType[$typeName] = @{ Covered = 0; Total = 0 }
        }

        foreach ($line in $class.SelectNodes('lines/line'))
        {
            $byType[$typeName].Total++
            $totalLines++

            if ([int]$line.hits -gt 0)
            {
                $byType[$typeName].Covered++
                $coveredLines++
            }
        }
    }

    $classes = foreach ($typeName in $byType.Keys)
    {
        $entry = $byType[$typeName]
        [PSCustomObject]@{
            Name            = $typeName
            LinesCovered    = $entry.Covered
            LinesValid      = $entry.Total
            CoveragePercent = if ($entry.Total -gt 0) { [System.Math]::Round($entry.Covered / $entry.Total * 100, 2) } else { 0 }
        }
    }

    return @{
        CoveragePercent = if ($totalLines -gt 0) { [System.Math]::Round($coveredLines / $totalLines * 100, 2) } else { 0 }
        LinesCovered    = $coveredLines
        LinesValid      = $totalLines
        # Least covered first, since that is the part worth reading
        Classes         = @($classes | Sort-Object -Property CoveragePercent)
    }
}

function Invoke-DotNetTest
{
    <#
        .SYNOPSIS
            Run the xUnit suite and return its counts and coverage.

        .PARAMETER Configuration
            Build configuration to test against.

        .PARAMETER IgnoreCodeCoverage
            Skip coverage collection.

        .PARAMETER NoBuild
            Test the existing build output instead of rebuilding.
    #>
    [CmdletBinding()]
    [OutputType([System.Collections.Hashtable])]
    param
    (
        [Parameter()]
        [System.String]
        $Configuration = 'Release',

        [Parameter()]
        [Switch]
        $IgnoreCodeCoverage,

        [Parameter()]
        [Switch]
        $NoBuild
    )

    $projectPath = Join-Path $script:RepoRoot 'tests' 'Mgx.IntegrationTests' 'Mgx.IntegrationTests.csproj'
    $resultsDirectory = Join-Path $script:RepoRoot 'TestResults' 'harness'
    $trxFileName = 'Mgx.IntegrationTests.trx'

    if (Test-Path -Path $resultsDirectory)
    {
        Remove-Item -Path $resultsDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
    New-Item -Path $resultsDirectory -ItemType Directory -Force | Out-Null

    $arguments = @(
        'test', $projectPath
        '--configuration', $Configuration
        '--logger', "trx;LogFileName=$trxFileName"
        '--results-directory', $resultsDirectory
        '--nologo'
    )

    if ($NoBuild.IsPresent)
    {
        $arguments += '--no-build'
    }

    if (-not $IgnoreCodeCoverage.IsPresent)
    {
        $arguments += @(
            '--settings', (Join-Path $script:RepoRoot 'tests' 'coverlet.runsettings')
            '--collect:XPlat Code Coverage'
        )
    }

    Write-Host -Object 'Running all mgx C# Unit Tests'
    & dotnet @arguments | Out-Host

    $result = Get-TrxSummary -Path (Join-Path $resultsDirectory $trxFileName)

    $result.Coverage = if ($IgnoreCodeCoverage.IsPresent)
    {
        $null
    }
    else
    {
        # The XPlat collector writes into a per-run subdirectory, so the file is found rather
        # than named
        $cobertura = Get-ChildItem -Path $resultsDirectory -Recurse -Filter 'coverage.cobertura.xml' -ErrorAction SilentlyContinue |
            Select-Object -First 1

        if ($null -eq $cobertura) { $null } else { Get-CoberturaSummary -Path $cobertura.FullName }
    }

    return $result
}

function Invoke-PesterTest
{
    <#
        .SYNOPSIS
            Run the Pester suite against the built module.

        .PARAMETER TestResultsFile
            NUnit XML results path.

        .PARAMETER TestPath
            Directory to search for *.Tests.ps1.

        .PARAMETER IncludeLive
            Include Live-tagged blocks, which need a connected tenant.
    #>
    [CmdletBinding()]
    param
    (
        [Parameter()]
        [System.String]
        $TestResultsFile,

        [Parameter()]
        [System.String]
        $TestPath,

        [Parameter()]
        [Switch]
        $IncludeLive
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

    if (-not (Test-Path -Path $script:ManifestPath))
    {
        throw "Module manifest not found at '$script:ManifestPath'. Run ./build.ps1 first."
    }

    $configuration = New-PesterConfiguration
    $configuration.Run.Path = $TestPath
    if (-not $IncludeLive.IsPresent)
    {
        $configuration.Filter.ExcludeTag = 'Live'
    }
    $configuration.Run.PassThru = $true
    $configuration.Output.Verbosity = 'Detailed'
    $configuration.TestResult.Enabled = $true
    $configuration.TestResult.OutputFormat = 'NUnitXml'
    $configuration.TestResult.OutputPath = $TestResultsFile

    # M365DSC.mgx is a binary module. There is no PowerShell source to instrument, so Pester
    # coverage is never collected and the report carries no PowerShell coverage section
    $configuration.CodeCoverage.Enabled = $false

    Write-Host -Object 'Running all mgx PowerShell Unit Tests'
    return Invoke-Pester -Configuration $configuration
}

<#
.SYNOPSIS
    Runs the mgx C# and PowerShell test suites and collects their results.

.DESCRIPTION
    Runs the xUnit suite with Cobertura coverage and the Pester suite against the staged module,
    and returns both as a single object. Failures are reported rather than thrown, so the caller
    decides how to fail the build.

.PARAMETER TestResultsFile
    NUnit XML results path for the Pester run. Defaults to tests/TestResults.xml.

.PARAMETER TestPath
    Directory to search for *.Tests.ps1. Defaults to tests/Unit.

.PARAMETER IgnoreCodeCoverage
    Skips coverage collection for the C# suite. Pester coverage is never collected, because the
    module is binary.

.PARAMETER IncludeLive
    Includes Live-tagged Pester blocks, which need a connected tenant.

.PARAMETER SkipDotNetTests
    Runs only the PowerShell suite.

.PARAMETER SkipPesterTests
    Runs only the C# suite.

.PARAMETER NoBuild
    Tests the existing build output instead of rebuilding.

.PARAMETER Configuration
    Build configuration to test against. Defaults to Release.

.OUTPUTS
    An object with Pester, DotNet and Duration. Callers check $result.Pester.FailedCount and
    $result.DotNet.Failed.

.EXAMPLE
    $results = Invoke-TestHarness
    Write-TestHarnessSummary -Result $results

.EXAMPLE
    Invoke-TestHarness -SkipDotNetTests -NoBuild
#>
function Invoke-TestHarness
{
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param
    (
        [Parameter()]
        [System.String]
        $TestResultsFile = (Join-Path $PSScriptRoot 'TestResults.xml'),

        [Parameter()]
        [System.String]
        $TestPath = (Join-Path $PSScriptRoot 'Unit'),

        [Parameter()]
        [Switch]
        $IgnoreCodeCoverage,

        [Parameter()]
        [Switch]
        $IncludeLive,

        [Parameter()]
        [Switch]
        $SkipDotNetTests,

        [Parameter()]
        [Switch]
        $SkipPesterTests,

        [Parameter()]
        [Switch]
        $NoBuild,

        [Parameter()]
        [ValidateSet('Debug', 'Release')]
        [System.String]
        $Configuration = 'Release'
    )

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

    $dotNetResults = $null
    if (-not $SkipDotNetTests.IsPresent)
    {
        $dotNetResults = Invoke-DotNetTest -Configuration $Configuration `
            -IgnoreCodeCoverage:$IgnoreCodeCoverage `
            -NoBuild:$NoBuild
    }

    $pesterResults = $null
    if (-not $SkipPesterTests.IsPresent)
    {
        $pesterResults = Invoke-PesterTest -TestResultsFile $TestResultsFile `
            -TestPath $TestPath `
            -IncludeLive:$IncludeLive
    }

    $stopwatch.Stop()

    $message = 'Running the tests took {0} hours, {1} minutes, {2} seconds' -f `
        $stopwatch.Elapsed.Hours, $stopwatch.Elapsed.Minutes, $stopwatch.Elapsed.Seconds
    Write-Host -Object $message

    $result = [PSCustomObject]@{
        Pester   = $pesterResults
        DotNet   = $dotNetResults
        Duration = $stopwatch.Elapsed
    }

    Write-TestHarnessSummary -Result $result

    return $result
}

<#
.SYNOPSIS
    Renders the results of Invoke-TestHarness as a report.

.DESCRIPTION
    Writes a combined test and coverage report for both suites. Without a path the report goes
    to the console, with a path it is appended as GitHub flavoured markdown, which makes it
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

    if ($null -ne $Result.DotNet)
    {
        $lines.Add('## C# Unit Test Results')
        $lines.Add('')
        $lines.Add('| Passed | Failed | Skipped |')
        $lines.Add('| ---: | ---: | ---: |')
        $lines.Add("| $($Result.DotNet.Passed) | $($Result.DotNet.Failed) | $($Result.DotNet.Skipped) |")
        $lines.Add('')

        if ($null -ne $Result.DotNet.Coverage)
        {
            $coverage = $Result.DotNet.Coverage
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
    }

    if ($null -ne $Result.Pester)
    {
        $lines.Add('## PowerShell Unit Test Results')
        $lines.Add('')
        $lines.Add('| Passed | Failed | Skipped |')
        $lines.Add('| ---: | ---: | ---: |')
        $lines.Add("| $($Result.Pester.PassedCount) | $($Result.Pester.FailedCount) | $($Result.Pester.SkippedCount) |")
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

Export-ModuleMember -Function Invoke-TestHarness, Invoke-DotNetTest, Invoke-PesterTest,
    Get-MgxTestPath, Get-TrxSummary, Get-CoberturaSummary, Write-TestHarnessSummary
