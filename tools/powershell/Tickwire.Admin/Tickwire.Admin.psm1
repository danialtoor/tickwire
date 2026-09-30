#Requires -Version 7.0
Set-StrictMode -Version Latest

# Connection state for the current PowerShell session.
$script:Tickwire = @{
    ApiUrl   = $env:TICKWIRE_API ?? 'http://localhost:8080'
    AdminKey = $env:TICKWIRE_ADMIN_KEY
    Token    = $env:TICKWIRE_TOKEN
}

function Invoke-TickwireApi {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('GET', 'POST', 'PUT', 'DELETE')][string] $Method,
        [Parameter(Mandatory)][string] $Path,
        [object] $Body
    )
    $headers = @{}
    if ($script:Tickwire.AdminKey) { $headers['X-Admin-Key'] = $script:Tickwire.AdminKey }
    if ($script:Tickwire.Token) { $headers['Authorization'] = "Bearer $($script:Tickwire.Token)" }
    $params = @{
        Method  = $Method
        Uri     = ($script:Tickwire.ApiUrl.TrimEnd('/') + $Path)
        Headers = $headers
    }
    if ($PSBoundParameters.ContainsKey('Body')) {
        $params.Body = ($Body | ConvertTo-Json -Depth 8)
        $params.ContentType = 'application/json'
    }
    try {
        Invoke-RestMethod @params
    }
    catch {
        $detail = $_.ErrorDetails.Message
        if ($detail) {
            try { $detail = ($detail | ConvertFrom-Json).detail } catch { Write-Verbose 'Error body was not JSON' }
        }
        throw "Tickwire API $Method $Path failed: $($_.Exception.Message) $detail"
    }
}

<#
.SYNOPSIS
Sets the Tickwire API endpoint and credentials for later commands.
.EXAMPLE
Connect-Tickwire -ApiUrl https://tickwire-api.fly.dev -AdminKey $key
#>
function Connect-Tickwire {
    [CmdletBinding()]
    param(
        [string] $ApiUrl = $script:Tickwire.ApiUrl,
        [string] $AdminKey,
        [string] $Token
    )
    $script:Tickwire.ApiUrl = $ApiUrl
    if ($PSBoundParameters.ContainsKey('AdminKey')) { $script:Tickwire.AdminKey = $AdminKey }
    if ($PSBoundParameters.ContainsKey('Token')) { $script:Tickwire.Token = $Token }
    $health = Invoke-RestMethod -Uri ($ApiUrl.TrimEnd('/') + '/health')
    [pscustomobject]@{ ApiUrl = $ApiUrl; Health = $health; Admin = [bool]$script:Tickwire.AdminKey; Token = [bool]$script:Tickwire.Token }
}

<#
.SYNOPSIS
Lists FIX sessions with their state and sequence numbers.
.EXAMPLE
Get-FixSession | Where-Object State -eq Active
.EXAMPLE
Get-FixSession -ClientCompId BYO-7Q2K
#>
function Get-FixSession {
    [CmdletBinding()]
    param([string] $ClientCompId)
    $sessions = Invoke-TickwireApi -Method GET -Path '/api/sessions'
    foreach ($s in $sessions) {
        if ($ClientCompId -and $s.clientCompId -ne $ClientCompId) { continue }
        [pscustomobject]@{
            PSTypeName    = 'Tickwire.FixSession'
            ClientCompId  = $s.clientCompId
            ClientId      = $s.clientId
            State         = $s.state
            Transport     = $s.transport
            NextOutSeqNum = $s.nextSenderSeqNum
            NextInSeqNum  = $s.nextTargetSeqNum
            HeartBtInt    = $s.heartBtInt
            LastReceived  = $s.lastReceived
        }
    }
}

<#
.SYNOPSIS
Onboards a new FIX client: creates an account, provisions CompIDs, and optionally writes a QuickFIX config.
.EXAMPLE
New-FixClient -ConfigPath ./quickfix.cfg
.EXAMPLE
New-FixClient -WithDropCopy    # also provisions a receive-only drop copy session (DropCopyCompID)
#>
function New-FixClient {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [string] $ConfigPath,
        [ValidateSet('quickfixn.cfg', 'quickfixj.cfg')][string] $ConfigFlavor = 'quickfixn.cfg',
        [switch] $WithDropCopy
    )
    if (-not $PSCmdlet.ShouldProcess($script:Tickwire.ApiUrl, 'Provision a FIX client')) { return }
    $guest = Invoke-TickwireApi -Method POST -Path '/api/guest'
    $saved = $script:Tickwire.Token
    try {
        $script:Tickwire.Token = $guest.token
        $connect = Invoke-TickwireApi -Method POST -Path '/api/connect'
        $dropCopy = $WithDropCopy ? (Invoke-TickwireApi -Method POST -Path '/api/connect?role=dropcopy') : $null
    }
    finally {
        $script:Tickwire.Token = $saved
    }
    if ($ConfigPath) { Set-Content -Path $ConfigPath -Value $connect.configs.$ConfigFlavor -Encoding ascii }
    [pscustomobject]@{
        PSTypeName   = 'Tickwire.FixClient'
        ClientId     = $connect.clientId
        SenderCompID = $connect.senderCompID
        TargetCompID = $connect.targetCompID
        DropCopyCompID = if ($dropCopy) { $dropCopy.senderCompID } else { $null }
        Host         = $connect.host
        Port         = $connect.port
        Token        = $guest.token
        ExpiresAt    = $guest.expiresAt
        ConfigPath   = $ConfigPath
    }
}

<#
.SYNOPSIS
Changes a client's pre-trade risk limits. They apply to the next order without a restart.
.EXAMPLE
Set-RiskLimit -ClientId gst-7q2k -MaxOrderQty 50 -AllowedUnderlyings SPY,AAPL
#>
function Set-RiskLimit {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)][string] $ClientId,
        [decimal] $MaxOrderQty = 100,
        [decimal] $MaxNotional = 50000,
        [decimal] $PriceBandPct = 0.5,
        [decimal] $PriceBandMinAbs = 0.25,
        [int] $MaxOpenOrders = 25,
        [string[]] $AllowedUnderlyings,
        [ValidateSet('Limit', 'Market')][string[]] $AllowedOrderTypes = @('Limit', 'Market'),
        [ValidateSet('Day', 'ImmediateOrCancel', 'FillOrKill')][string[]] $AllowedTimeInForce = @('Day', 'ImmediateOrCancel', 'FillOrKill'),
        [int] $MaxMessagesPerSecond = 10,
        [bool] $CancelOnDisconnect = $true,
        [double] $MaxAbsDelta = 10000,
        [double] $MaxAbsVega = 10000
    )
    process {
        $body = [ordered]@{
            maxOrderQty          = $MaxOrderQty
            maxNotional          = $MaxNotional
            priceBandPct         = $PriceBandPct
            priceBandMinAbs      = $PriceBandMinAbs
            maxOpenOrders        = $MaxOpenOrders
            allowedUnderlyings   = $AllowedUnderlyings
            allowedOrderTypes    = $AllowedOrderTypes
            allowedTimeInForce   = $AllowedTimeInForce
            maxMessagesPerSecond = $MaxMessagesPerSecond
            cancelOnDisconnect   = $CancelOnDisconnect
            maxAbsDelta          = $MaxAbsDelta
            maxAbsVega           = $MaxAbsVega
        }
        if ($PSCmdlet.ShouldProcess($ClientId, 'Update risk limits')) {
            Invoke-TickwireApi -Method PUT -Path "/api/clients/$ClientId/limits" -Body $body
        }
    }
}

<#
.SYNOPSIS
Engages (or releases) the kill switch: cancels every open order and blocks new ones.
.EXAMPLE
Invoke-KillSwitch -Client gst-7q2k
.EXAMPLE
Invoke-KillSwitch -Global -Confirm:$false    # needs the admin key
#>
function Invoke-KillSwitch {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High', DefaultParameterSetName = 'Client')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'Client', ValueFromPipelineByPropertyName)][Alias('ClientId')][string] $Client,
        [Parameter(Mandatory, ParameterSetName = 'Global')][switch] $Global,
        [switch] $Release
    )
    process {
        $engaged = -not $Release
        $target = $Global ? 'ALL clients' : $Client
        $action = $engaged ? 'Engage kill switch (cancel open orders, block new ones)' : 'Release kill switch'
        if (-not $PSCmdlet.ShouldProcess($target, $action)) { return }
        $path = $Global ? '/api/admin/kill' : "/api/clients/$Client/kill"
        $result = Invoke-TickwireApi -Method POST -Path $path -Body @{ engaged = $engaged }
        [pscustomobject]@{ Target = $target; Engaged = $result.engaged; OrdersCanceled = $result.ordersCanceled }
    }
}

<#
.SYNOPSIS
Saves a session's recent FIX traffic as a '|'-delimited log, ready for Invoke-FixLogAnalysis or a support ticket.
.EXAMPLE
Export-FixLog -ClientCompId GST-7Q2K -Path ./session.log
#>
function Export-FixLog {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $ClientCompId,
        [Parameter(Mandatory)][string] $Path,
        [ValidateSet('venue', 'client')][string] $Side = 'venue',
        [ValidateRange(1, 1000)][int] $Limit = 1000
    )
    $traffic = Invoke-TickwireApi -Method GET -Path "/api/sessions/$ClientCompId/messages?limit=$Limit"
    $lines = foreach ($m in $traffic.messages | Where-Object side -eq $Side) {
        $direction = $m.direction -eq 'in' ? 'IN ' : 'OUT'
        '{0} {1} {2}' -f ([datetime]$m.time).ToUniversalTime().ToString('yyyyMMdd-HH:mm:ss.fff'), $direction, $m.raw
    }
    Set-Content -Path $Path -Value $lines -Encoding ascii
    Get-Item -Path $Path
}

<#
.SYNOPSIS
Runs the FIX Log Analyzer on a log file and returns its findings.
.EXAMPLE
Invoke-FixLogAnalysis ./session.log | Where-Object Severity -eq Error
#>
function Invoke-FixLogAnalysis {
    [CmdletBinding()]
    param([Parameter(Mandatory, Position = 0)][string] $Path)
    $text = Get-Content -Path $Path -Raw
    $result = Invoke-TickwireApi -Method POST -Path '/api/analyzer' -Body @{ text = $text }
    foreach ($d in $result.diagnostics) {
        [pscustomobject]@{
            PSTypeName  = 'Tickwire.Diagnostic'
            Severity    = $d.severity
            Code        = $d.code
            Line        = $d.line
            Title       = $d.title
            Explanation = $d.explanation
            Suggestion  = $d.suggestion
        }
    }
}

<#
.SYNOPSIS
Logs a session out and resets both sequence numbers to 1.
#>
function Reset-FixSession {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    param([Parameter(Mandatory, ValueFromPipelineByPropertyName)][string] $ClientCompId)
    process {
        if ($PSCmdlet.ShouldProcess($ClientCompId, 'Logout and reset sequence numbers')) {
            Invoke-TickwireApi -Method POST -Path "/api/sessions/$ClientCompId/reset" | Out-Null
            Get-FixSession -ClientCompId $ClientCompId
        }
    }
}

Export-ModuleMember -Function Connect-Tickwire, Get-FixSession, New-FixClient, Set-RiskLimit, Invoke-KillSwitch, Export-FixLog,
    Invoke-FixLogAnalysis, Reset-FixSession
