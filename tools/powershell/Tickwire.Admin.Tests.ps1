#Requires -Modules Pester
# Runs against a live API: $env:TICKWIRE_API (default http://localhost:8080). CI starts one with docker compose.
BeforeAll {
    Import-Module (Join-Path $PSScriptRoot 'Tickwire.Admin') -Force
    Connect-Tickwire -ApiUrl ($env:TICKWIRE_API ?? 'http://localhost:8080') | Out-Null
}

Describe 'Tickwire.Admin' {
    It 'exports the documented commands' {
        (Get-Command -Module Tickwire.Admin).Name | Should -Contain 'Invoke-KillSwitch'
        (Get-Command -Module Tickwire.Admin).Count | Should -Be 8
    }

    It 'onboards a client, changes its limits and kills it' {
        $client = New-FixClient -Confirm:$false
        $client.SenderCompID | Should -Match '^BYO-'
        Connect-Tickwire -Token $client.Token | Out-Null

        $limits = Set-RiskLimit -ClientId $client.ClientId -MaxOrderQty 5 -AllowedUnderlyings SPY -Confirm:$false
        $limits.maxOrderQty | Should -Be 5

        $kill = Invoke-KillSwitch -Client $client.ClientId -Confirm:$false
        $kill.Engaged | Should -BeTrue
        (Invoke-KillSwitch -Client $client.ClientId -Release -Confirm:$false).Engaged | Should -BeFalse
    }

    It 'analyzes a log file' {
        $sample = Join-Path $PSScriptRoot '../../samples/02-sequence-too-low.log'
        $findings = Invoke-FixLogAnalysis $sample
        $findings.Code | Should -Contain 'SEQ_TOO_LOW'
    }

    It 'lists sessions' {
        Get-FixSession | Should -Not -BeNullOrEmpty
    }
}
