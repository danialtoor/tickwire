@{
    RootModule        = 'Tickwire.Admin.psm1'
    ModuleVersion     = '1.0.0'
    GUID              = '5d3c1f5e-8a0b-4c55-9d8e-2f6b7c1a9e41'
    Author            = 'Tickwire contributors'
    Description       = 'Operator tools for a Tickwire FIX gateway: sessions, client onboarding, risk limits, kill switch and log analysis.'
    PowerShellVersion = '7.0'
    FunctionsToExport = @('Connect-Tickwire', 'Get-FixSession', 'New-FixClient', 'Set-RiskLimit', 'Invoke-KillSwitch', 'Export-FixLog',
        'Invoke-FixLogAnalysis', 'Reset-FixSession')
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @()
    PrivateData       = @{ PSData = @{ Tags = @('FIX', 'trading', 'operations'); LicenseUri = 'https://opensource.org/licenses/MIT' } }
}
