<#
.SYNOPSIS
    Read-only exercise of the broker's named pipe against a running service.

.DESCRIPTION
    It connects and sends. It installs nothing, changes no permission and writes
    no protected document that the broker does not already accept from the
    account this script runs as - and on a prepared device that account is the
    child, which may only make the child's situation stricter.

    REGRESSION CHECKS THIS DELIBERATELY INCLUDES

    Two defects were fixed just before PR #5 and both were invisible on a fast
    idle machine, so both are checked here on real hardware:

    1. The listener used to call Disconnect() after writing its reply, which
       DISCARDS anything the client has not read yet. A slow reader lost the
       answer and reported "the service did not respond" - indistinguishable
       from the service being absent, so a refusal the broker had correctly
       decided came back as ServiceUnavailable. The slow-reader check below is
       that defect.

    2. The caller's identity is read while impersonating at identification
       level, and such a token cannot load an assembly. The FIRST request in a
       service process therefore failed to identify its caller unless something
       had already touched a Windows identity. On a device this means the first
       request after every service start, which is why the first-request check
       runs immediately after a restart rather than later.
#>
[CmdletBinding()]
param(
    [string] $ConfigPath,
    [string] $RunPath,
    [switch] $AfterServiceRestart
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'KidShellValidation.psm1') -Force

$pipeName = 'KidShell.Security.v1'
$stage = New-ValidationStage -Name 'NAMED PIPE'

$evidence = [pscustomobject]@{
    pipeName            = $pipeName
    pipePresent         = $false
    serviceRunning      = $false
    checks              = @()
}

function Add-Check([string] $name, [string] $status, [string] $detail) {
    $script:evidence.checks += [pscustomobject]@{ name = $name; status = $status; detail = $detail }
    Add-ValidationFinding -Stage $script:stage -Status $status -Detail "$name - $detail"
}

Write-Host ''
Write-Host '===== Named pipe (read-only) =====' -ForegroundColor Cyan

$service = Get-Service -Name 'KidShellSecurityHost' -ErrorAction SilentlyContinue
$evidence.serviceRunning = ($service -and $service.Status -eq 'Running')

# The pipe namespace is enumerable without opening anything.
$evidence.pipePresent = [bool](Get-ChildItem '\\.\pipe\' -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -eq $pipeName })

Write-Host ("Service   : {0}" -f $(if ($evidence.serviceRunning) { 'running' } else { 'not running' }))
Write-Host ("Pipe      : {0}" -f $(if ($evidence.pipePresent) { 'present' } else { 'absent' }))

if (-not $evidence.pipePresent) {
    Add-Check 'endpoint' 'NotRun' "\\.\pipe\$pipeName does not exist. Install and start the service first."

    if ($RunPath) {
        Write-Evidence -RunPath $RunPath -Name 'pipe.json' -Data $evidence | Out-Null
        Save-ValidationStage -RunPath $RunPath -Stage $stage
    }

    Write-Host ''
    Write-Host 'Nothing was changed.' -ForegroundColor Green
    return
}

Add-Check 'endpoint' 'Pass' 'the service created the pipe'

function Send-BrokerFrame {
    <#
        One framed request, and the reply.

        Four-byte big-endian length then UTF-8 JSON, which is the broker's
        framing. $ReadDelayMs is what makes this able to catch defect 1: a
        client that is slow to read.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Json,
        [int] $ReadDelayMs = 0,
        [int] $ConnectTimeoutMs = 2000
    )

    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(
        '.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::Asynchronous,
        [System.Security.Principal.TokenImpersonationLevel]::Identification)

    try {
        $pipe.Connect($ConnectTimeoutMs)

        $payload = [Text.Encoding]::UTF8.GetBytes($Json)
        $prefix = [BitConverter]::GetBytes([int]$payload.Length)
        if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($prefix) }

        $pipe.Write($prefix, 0, 4)
        $pipe.Write($payload, 0, $payload.Length)
        $pipe.Flush()

        if ($ReadDelayMs -gt 0) { Start-Sleep -Milliseconds $ReadDelayMs }

        $header = New-Object byte[] 4
        $read = 0
        while ($read -lt 4) {
            $got = $pipe.Read($header, $read, 4 - $read)
            if ($got -le 0) { return $null }
            $read += $got
        }

        if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($header) }
        $length = [BitConverter]::ToInt32($header, 0)

        if ($length -le 0 -or $length -gt 65536) { return $null }

        $body = New-Object byte[] $length
        $read = 0
        while ($read -lt $length) {
            $got = $pipe.Read($body, $read, $length - $read)
            if ($got -le 0) { return $null }
            $read += $got
        }

        return [Text.Encoding]::UTF8.GetString($body) | ConvertFrom-Json
    }
    finally {
        $pipe.Dispose()
    }
}

function New-Request([string] $kind) {
    $id = [guid]::NewGuid().ToString('n')
    return "{""protocolVersion"":1,""kind"":""$kind"",""requestId"":""$id"",""dryRun"":false}"
}

# ------------------------------------------------------------ reachability

try {
    $probe = Send-BrokerFrame -Json (New-Request 'Probe')

    if ($null -eq $probe) {
        Add-Check 'reachable' 'Fail' 'the service accepted a connection and sent no reply'
    }
    elseif ($probe.success) {
        Add-Check 'reachable' 'Pass' 'a probe was answered'
    }
    else {
        Add-Check 'reachable' 'Fail' "a probe was refused: $($probe.reason) $($probe.message)"
    }
}
catch {
    Add-Check 'reachable' 'Fail' "the pipe could not be used: $($_.Exception.GetType().Name)"
}

# --------------------------------------------- defect 1: the slow reader

try {
    $slow = Send-BrokerFrame -Json (New-Request 'Probe') -ReadDelayMs 250

    if ($null -eq $slow) {
        Add-Check 'slow-reader' 'Fail' `
            'the reply was LOST when the client waited 250ms before reading. The listener is discarding unread replies again (Disconnect regression).'
    }
    elseif ($slow.success) {
        Add-Check 'slow-reader' 'Pass' 'a client that waited 250ms before reading still received its reply'
    }
    else {
        Add-Check 'slow-reader' 'Fail' "the delayed read was refused: $($slow.reason)"
    }
}
catch {
    Add-Check 'slow-reader' 'Fail' "the delayed read threw $($_.Exception.GetType().Name)"
}

# ------------------------------- defect 2: the first request after a start

if ($AfterServiceRestart) {
    # Only meaningful immediately after a restart, which is why this is a
    # switch rather than something the script decides for itself.
    try {
        $first = Send-BrokerFrame -Json (New-Request 'Probe')

        if ($null -ne $first -and $first.success) {
            Add-Check 'first-request-after-restart' 'Pass' `
                'the first request after a service start identified its caller'
        }
        else {
            Add-Check 'first-request-after-restart' 'Fail' `
                "the first request after a service start was refused ($($first.reason)). Identity resolution under impersonation is failing again."
        }
    }
    catch {
        Add-Check 'first-request-after-restart' 'Fail' "the first request threw $($_.Exception.GetType().Name)"
    }
}
else {
    Add-Check 'first-request-after-restart' 'NotRun' `
        'run this again with -AfterServiceRestart immediately after restarting the service'
}

# ------------------------------------------------------- malformed input

try {
    $malformed = Send-BrokerFrame -Json '{ not json'

    if ($null -eq $malformed -or -not $malformed.success) {
        Add-Check 'malformed-request' 'Pass' 'a malformed request was refused'
    }
    else {
        Add-Check 'malformed-request' 'Fail' 'a malformed request was ACCEPTED'
    }
}
catch {
    Add-Check 'malformed-request' 'Pass' 'a malformed request was refused at the transport'
}

try {
    $unknown = Send-BrokerFrame -Json `
        '{"protocolVersion":1,"kind":"Nonsense","requestId":"validation","dryRun":false}'

    if ($null -eq $unknown -or -not $unknown.success) {
        Add-Check 'unknown-operation' 'Pass' 'an unknown operation was refused'
    }
    else {
        Add-Check 'unknown-operation' 'Fail' 'an unknown operation was ACCEPTED'
    }
}
catch {
    Add-Check 'unknown-operation' 'Pass' 'an unknown operation was refused at the transport'
}

try {
    $bad = Send-BrokerFrame -Json `
        '{"protocolVersion":99,"kind":"Probe","requestId":"validation","dryRun":false}'

    if ($null -eq $bad -or -not $bad.success) {
        Add-Check 'unsupported-protocol' 'Pass' 'an unknown protocol version was refused'
    }
    else {
        Add-Check 'unsupported-protocol' 'Fail' 'an unknown protocol version was ACCEPTED'
    }
}
catch {
    Add-Check 'unsupported-protocol' 'Pass' 'an unknown protocol version was refused at the transport'
}

# --------------------------------------------------------- oversized frame

try {
    # Announces more than the protocol allows. The service must refuse at the
    # length rather than allocate for it.
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(
        '.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::Asynchronous,
        [System.Security.Principal.TokenImpersonationLevel]::Identification)

    try {
        $pipe.Connect(2000)

        $prefix = [BitConverter]::GetBytes([int]0x7FFFFFFF)
        if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($prefix) }

        $pipe.Write($prefix, 0, 4)
        $pipe.Flush()

        $header = New-Object byte[] 4
        $got = $pipe.Read($header, 0, 4)

        if ($got -le 0) {
            Add-Check 'oversized-frame' 'Pass' 'an oversized frame was refused without a reply'
        }
        else {
            Add-Check 'oversized-frame' 'Fail' 'an oversized frame was answered rather than refused'
        }
    }
    finally { $pipe.Dispose() }
}
catch {
    Add-Check 'oversized-frame' 'Pass' "an oversized frame was refused ($($_.Exception.GetType().Name))"
}

# ------------------------------------ a silent client must not kill it

try {
    $silent = New-Object System.IO.Pipes.NamedPipeClientStream(
        '.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::Asynchronous,
        [System.Security.Principal.TokenImpersonationLevel]::Identification)

    $silent.Connect(2000)
    $silent.Dispose()

    $after = Send-BrokerFrame -Json (New-Request 'Probe') -ConnectTimeoutMs 15000

    if ($null -ne $after -and $after.success) {
        Add-Check 'silent-client' 'Pass' 'a connected client that said nothing did not stop the listener'
    }
    else {
        Add-Check 'silent-client' 'Fail' 'the listener stopped serving after a silent client'
    }
}
catch {
    Add-Check 'silent-client' 'Fail' "after a silent client the broker could not be reached: $($_.Exception.GetType().Name)"
}

# ------------------------- the child must not be able to serve the name

try {
    $squat = New-Object System.IO.Pipes.NamedPipeServerStream(
        $pipeName, [System.IO.Pipes.PipeDirection]::InOut, 1)

    $squat.Dispose()

    Add-Check 'instance-creation' 'Fail' `
        'this account CREATED an instance of the broker pipe. It could answer a client as though it were the service.'
}
catch {
    Add-Check 'instance-creation' 'Pass' `
        "this account cannot create an instance of the broker pipe ($($_.Exception.GetType().Name))"
}

Write-Host ''

foreach ($check in $evidence.checks) {
    Write-Host ("  [{0,-12}] {1}: {2}" -f $check.status, $check.name, $check.detail)
}

if ($RunPath) {
    Write-Evidence -RunPath $RunPath -Name 'pipe.json' -Data $evidence | Out-Null
    Save-ValidationStage -RunPath $RunPath -Stage $stage
}

Write-Host ''
Write-Host 'Nothing was changed.' -ForegroundColor Green
