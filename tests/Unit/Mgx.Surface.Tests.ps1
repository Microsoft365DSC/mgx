#Requires -Modules Pester

$script:GraphDataCmdlets = @(
    'Invoke-MgxRequest'
    'Invoke-MgxBatchRequest'
    'Expand-MgxRelation'
    'Sync-MgxDelta'
)

$script:ExpectedCmdlets = @(
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

BeforeAll {
    Import-Module -Name (Join-Path (Split-Path -Parent $PSScriptRoot) 'TestHarness.psm1') -Force
    $script:Paths = Get-MgxTestPath

    Import-Module -Name $script:Paths.ManifestPath -Force -ErrorAction Stop

    # Discovery and run use separate scopes, so the lists above are not visible
    # inside It blocks. Re-bind them here for the run phase.
    $script:ExpectedCmdletNames = @(
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
}

# No AfterAll teardown: Remove-Module runs the module's OnRemove handler,
# which fails to resolve Polly.Core out of the isolated load context and throws.
# The module is left loaded; the Pester run is a short-lived process.

Describe 'Module import' {
    It 'imports without error' {
        Get-Module -Name $script:Paths.ModuleName | Should -Not -BeNullOrEmpty
    }

    It 'exports exactly the cmdlets listed in the manifest' {
        $exported = (Get-Module -Name $script:Paths.ModuleName).ExportedCmdlets.Keys

        $exported | Should -HaveCount $script:ExpectedCmdletNames.Count
        foreach ($cmdlet in $script:ExpectedCmdletNames)
        {
            $exported | Should -Contain $cmdlet
        }
    }

    It 'loads the format file without error' {
        # A malformed ps1xml surfaces here rather than at first output
        { Update-FormatData -PrependPath $script:Paths.FormatPath -ErrorAction Stop } |
            Should -Not -Throw
    }
}

Describe 'Output contract' {
    It '<_> declares Hashtable output' -ForEach $script:GraphDataCmdlets {
        (Get-Command -Name $_).OutputType.Type.FullName |
            Should -Contain 'System.Collections.Hashtable'
    }

    It '<_> does not declare PSObject output' -ForEach $script:GraphDataCmdlets {
        (Get-Command -Name $_).OutputType.Type.FullName |
            Should -Not -Contain 'System.Management.Automation.PSObject'
    }

    It 'Invoke-MgxRequest also declares String output for -Raw' {
        (Get-Command -Name Invoke-MgxRequest).OutputType.Type.FullName |
            Should -Contain 'System.String'
    }

    It 'informational cmdlets keep their strongly typed output' {
        (Get-Command -Name Get-MgxOption).OutputType.Type.FullName |
            Should -Contain 'Mgx.Cmdlets.Models.MgxOptionOutput'
        (Get-Command -Name Get-MgxTelemetry).OutputType.Type.FullName |
            Should -Contain 'Mgx.Cmdlets.Models.MgxTelemetryOutput'
    }
}

Describe 'Pipeline input contract' {
    It 'Invoke-MgxRequest accepts Object for fan-out input' {
        # A [string] parameter would silently bind a piped hashtable as its type name
        $parameter = (Get-Command -Name Invoke-MgxRequest).Parameters['InputObject']

        $parameter.ParameterType.FullName | Should -Be 'System.Object'
    }

    It 'Invoke-MgxRequest binds fan-out input by value, not by property name' {
        # ValueFromPipelineByPropertyName cannot read dictionary keys, so the id is
        # extracted explicitly in ProcessRecord instead
        $attribute = (Get-Command -Name Invoke-MgxRequest).Parameters['InputObject'].Attributes |
            Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] } |
            Select-Object -First 1

        $attribute.ValueFromPipeline | Should -BeTrue
        $attribute.ValueFromPipelineByPropertyName | Should -BeFalse
    }

    It 'Invoke-MgxRequest keeps the Id alias on the fan-out parameter' {
        (Get-Command -Name Invoke-MgxRequest).Parameters['InputObject'].Aliases |
            Should -Contain 'Id'
    }

    It 'Expand-MgxRelation accepts Object so hashtables can be enriched' {
        $parameter = (Get-Command -Name Expand-MgxRelation).Parameters['InputObject']

        $parameter.ParameterType.FullName | Should -Be 'System.Object'
    }

    It 'Invoke-MgxBatchRequest accepts an object array of URLs or request descriptions' {
        $parameter = (Get-Command -Name Invoke-MgxBatchRequest).Parameters['Uri']

        $parameter.ParameterType.FullName | Should -Be 'System.Object[]'
        $parameter.Aliases | Should -Contain 'Url'
    }
}

Describe 'Cmdlet safety attributes' {
    It 'Invoke-MgxRequest supports ShouldProcess for write operations' {
        (Get-Command -Name Invoke-MgxRequest).Parameters.Keys | Should -Contain 'WhatIf'
    }

    It 'Invoke-MgxBatchRequest supports ShouldProcess' {
        (Get-Command -Name Invoke-MgxBatchRequest).Parameters.Keys | Should -Contain 'WhatIf'
    }

    It 'restricts <Cmdlet> -<Parameter> to the documented values' -ForEach @(
        @{ Cmdlet = 'Invoke-MgxRequest';      Parameter = 'ApiVersion'; Valid = @('v1.0', 'beta') }
        @{ Cmdlet = 'Invoke-MgxRequest';      Parameter = 'Method';     Valid = @('GET', 'POST', 'PATCH', 'PUT', 'DELETE') }
        @{ Cmdlet = 'Invoke-MgxBatchRequest'; Parameter = 'Method';     Valid = @('GET', 'POST', 'PATCH', 'PUT', 'DELETE') }
    ) {
        $validateSet = (Get-Command -Name $Cmdlet).Parameters[$Parameter].Attributes |
            Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] } |
            Select-Object -First 1

        $validateSet | Should -Not -BeNullOrEmpty
        foreach ($value in $Valid)
        {
            $validateSet.ValidValues | Should -Contain $value
        }
    }
}

Describe 'Help' {
    It 'ships help content for <_>' -ForEach $script:ExpectedCmdlets {
        $help = Get-Help -Name $_ -ErrorAction SilentlyContinue

        $help | Should -Not -BeNullOrEmpty
        $help.Synopsis | Should -Not -BeNullOrEmpty
        $help.Synopsis | Should -Not -Match '^\s*$'
    }
}
