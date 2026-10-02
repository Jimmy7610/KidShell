<#
.SYNOPSIS
    Writes a checkpoint before a reboot, and compares against it afterwards.

.DESCRIPTION
    IT NEVER REBOOTS ANYTHING. A human decides that, and this records the state
    they will want afterwards.

    Several things can only be proven across a restart: that the service is
    listening before anyone signs in, that screen time is not a fresh day, that
    a PIN cooldown survives. A reboot ends the process doing the proving, so what
    it had established is written to a file first - with digests, so "the counter
    survived" is a comparison rather than a recollection.

    The checkpoint is refused rather than resumed if it belongs to another
    machine, another run, or a schema this build does not know. Resuming the
    wrong one would compare one run's state against another's expectations and
    report a pass.

.EXAMPLE
    # Before you reboot:
    .\12-verify-reboot-persistence.ps1 -RunPath <run> -Save

    # Reboot by hand, sign in as the child, then:
    .\12-verify-reboot-persistence.ps1 -RunPath <run> -Compare
#>
[CmdletBinding()]
param(
    [string] $ConfigPath,
    [Parameter(Mandatory)][string] $RunPath,
    [switch] $Save,
    [switch] $Compare
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'KidShellValidation.psm1') -Force

$store = 'C:\ProgramData\KidShell\policy'
$documents = @('parent-policy.json', 'screen-time-state.json', 'pin-throttle.json', 'provisioned.json')
$checkpointPath = Join-Path $RunPath 'reboot-checkpoint.json'
$stage = New-ValidationStage -Name 'REBOOT PERSISTENCE'

function Get-StateDigest {
    $digests = @{}

    foreach ($name in $documents) {
        $path = Join-Path $store $name

        if (Test-Path $path) {
            $digests[$name] = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }

    return $digests
}

$config = Import-ValidationConfig -Path $ConfigPath
$runId = Split-Path -Leaf $RunPath.TrimEnd('\', '/')
$service = Get-Service -Name 'KidShellSecurityHost' -ErrorAction SilentlyContinue

Write-Host ''
Write-Host '===== Reboot persistence =====' -ForegroundColor Cyan

if (-not $Save -and -not $Compare) {
    Write-Host 'Pass -Save before the reboot, or -Compare after it.' -ForegroundColor Yellow
    return
}

if ($Save) {
    $childSid = ''
    if ($config -and $config.ExpectedChildSid) { $childSid = $config.ExpectedChildSid }

    $machineStateHash = ''
    $before = Join-Path $RunPath 'before-machine-state.txt'
    if (Test-Path $before) {
        $machineStateHash = (Get-FileHash $before -Algorithm SHA256).Hash
    }

    $checkpoint = [pscustomobject]@{
        schemaVersion    = 1
        runId            = $runId
        machineName      = $env:COMPUTERNAME
        stage            = 'REBOOT PERSISTENCE'
        writtenUtc       = (Get-Date).ToUniversalTime().ToString('o')
        serviceRunning   = [bool]($service -and $service.Status -eq 'Running')
        childSid         = $childSid
        stateDigests     = (Get-StateDigest)
        observations     = @{
            serviceStartType = $(if ($service) { [string]$service.StartType } else { 'absent' })
        }
        machineStateHash = $machineStateHash
    }

    # Written directly rather than through Write-Evidence: this file is read
    # back by the tool, and a redacted copy would not round-trip. It contains
    # digests and decisions only, which is exactly what redaction would leave.
    $checkpoint | ConvertTo-Json -Depth 8 | Set-Content -Path $checkpointPath -Encoding utf8

    Write-Host ("Checkpoint written: {0}" -f $checkpointPath)
    Write-Host ("Documents hashed  : {0}" -f $checkpoint.stateDigests.Count)
    Write-Host ''
    Write-Host 'NOW REBOOT THE MACHINE YOURSELF, sign in as the child, and run this again' -ForegroundColor Yellow
    Write-Host 'with -Compare. This script will not reboot anything.' -ForegroundColor Yellow

    return
}

# ------------------------------------------------------------- comparison

$load = Invoke-ValidationTool -Arguments @(
    'checkpoint', '--in', $checkpointPath, '--machine', $env:COMPUTERNAME, '--run', $runId)

$load.Output | ForEach-Object { Write-Host $_ }

if (-not $load.Accepted) {
    Add-ValidationFinding -Stage $stage -Status NotRun `
        -Detail 'The reboot checkpoint could not be resumed, so nothing was compared across the restart.'

    Save-ValidationStage -RunPath $RunPath -Stage $stage
    return
}

$checkpoint = Get-Content $checkpointPath -Raw | ConvertFrom-Json
$now = Get-StateDigest

$changed = New-Object System.Collections.Generic.List[string]
$names = @($checkpoint.stateDigests.PSObject.Properties.Name) + @($now.Keys) | Select-Object -Unique

foreach ($name in $names) {
    $then = '(absent)'
    if ($checkpoint.stateDigests.PSObject.Properties.Name -contains $name) {
        $then = [string]$checkpoint.stateDigests.$name
    }

    $nowValue = '(absent)'
    if ($now.ContainsKey($name)) { $nowValue = $now[$name] }

    if ($then -ne $nowValue) { $changed.Add($name) }
}

Write-Host ''
Write-Host ("Service now : {0}" -f $(if ($service) { $service.Status } else { 'absent' }))
Write-Host ("Changed     : {0}" -f $(if ($changed.Count) { ($changed -join ', ') } else { 'nothing' }))

# A document that vanished across a reboot is exactly what this is looking for.
foreach ($name in $changed) {
    if (-not $now.ContainsKey($name)) {
        Add-ValidationFinding -Stage $stage -Status Fail `
            -Detail "$name did not survive the reboot. It is gone."
    }
}

if ($service -and $service.Status -eq 'Running') {
    Add-ValidationFinding -Stage $stage -Status Pass `
        -Detail 'The security service is running after the reboot.'
}
else {
    Add-ValidationFinding -Stage $stage -Status Fail `
        -Detail 'The security service is NOT running after the reboot, so it did not start automatically.'
}

if ($checkpoint.stateDigests.PSObject.Properties.Name -contains 'pin-throttle.json' -and
    -not $now.ContainsKey('pin-throttle.json')) {
    Add-ValidationFinding -Stage $stage -Status Fail `
        -Detail 'The PIN throttle document is gone after the reboot. A cooldown did not survive.'
}

Write-Host ''
Write-Host 'Compare the screen-time and PIN snapshots as well:' -ForegroundColor Yellow
Write-Host '  .\09-verify-screen-time.ps1 -RunPath <run> -Snapshot after-reboot -Against <before file>'
Write-Host '  .\10-verify-pin-throttle.ps1 -RunPath <run> -Snapshot after-reboot -Against <before file>'

Write-Evidence -RunPath $RunPath -Name 'reboot-persistence.json' -Data @{
    resumed     = $true
    # ToArray() for the reason given in 07-verify-child-denials.ps1: the array
    # subexpression operator over a generic List throws on Windows PowerShell 5.1.
    changed     = $changed.ToArray()
    serviceNow  = $(if ($service) { [string]$service.Status } else { 'absent' })
} | Out-Null

Save-ValidationStage -RunPath $RunPath -Stage $stage

Write-Host ''
Write-Host 'Nothing was changed.' -ForegroundColor Green
