# Tickwire.Admin (PowerShell 7)

Operator commands for a Tickwire gateway, the way a support desk would script client onboarding and incident response.

```powershell
Import-Module ./tools/powershell/Tickwire.Admin
Connect-Tickwire -ApiUrl https://tickwire-api.fly.dev

$c = New-FixClient -ConfigPath ./quickfix.cfg      # CompIDs + a ready QuickFIX/n config
Connect-Tickwire -Token $c.Token                  # act as that client
Set-RiskLimit -ClientId $c.ClientId -MaxOrderQty 25 -AllowedUnderlyings SPY,AAPL
Get-FixSession -ClientCompId $c.SenderCompID
Export-FixLog -ClientCompId $c.SenderCompID -Path ./session.log
Invoke-FixLogAnalysis ./session.log | Format-Table Severity, Code, Title
Invoke-KillSwitch -Client $c.ClientId             # asks for confirmation
```

Admin-only commands (`Invoke-KillSwitch -Global`) need `Connect-Tickwire -AdminKey ...`.
Tests: `Invoke-Pester ./tools/powershell` with an API running.
