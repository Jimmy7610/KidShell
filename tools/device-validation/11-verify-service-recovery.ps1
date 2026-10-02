<#
.SYNOPSIS
    Checks that KidShell fails CLOSED when the broker is unavailable.

.DESCRIPTION
    This is the one verifier that needs the service stopped, and stopping a
    service is a mutation - so it is behind the interlock and defaults to a DRY
    RUN. Without -Apply it reports what it would do and checks nothing.

    WHAT FAILING CLOSED MEANS HERE

    With the broker gone, four things must be true, and a product that gets any
    of them wrong is worse than one with no protection at all, because a parent
    has been told it is protected:

      screen time must not silently become unlimited
      the policy must not fall back to the child's own JSON
      the PIN throttle must not revert to memory only
      the parent's policy must not become writable by the child

    The first three are observations in KidShell's own UI, so this script asks.
    The fourth it can check: the protected documents must still be refused.
#>
[CmdletBinding()]
param(
    [string] $ConfigPath,
    [string] $RunPath,

    # Without this, nothing is stopped and nothing is checked.
    [switch] $Apply,

    [switch] $NonInteractive
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'KidShellValidation.psm1') -Force

$serviceName = 'KidShellSecurityHost'
$stage = New-ValidationStage -Name 'SERVICE RECOVERY'

Write-Host ''
Write-Host '===== Service recovery =====' -ForegroundColor Cyan

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue

if (-not $service) {
    Write-Host 'The service is not installed, so there is nothing to stop.'

    Add-ValidationFinding -Stage $stage -Status NotRun `
        -Detail 'The service is not installed, so fail-closed behaviour was not checked.'

    if ($RunPath) {
        Write-Evidence -RunPath $RunPath -Name 'recovery.json' -Data @{ checked = $false } | Out-Null
        Save-ValidationStage -RunPath $RunPath -Stage $stage
    }

    return
}

if (-not $Apply) {
    # DRY RUN BY DEFAULT. Even past the interlock, a mutating stage says what it
    # would do before it does it.
    Write-Host ''
    Write-Host 'DRY RUN. Nothing will be stopped.' -ForegroundColor Yellow
    Write-Host ''
    Write-Host 'With -Apply this stage would:'
    Write-Host "  1. stop $serviceName"
    Write-Host '  2. ask you what KidShell now reports about screen time, the policy and the PIN'
    Write-Host '  3. confirm the protected documents are still refused to the child'
    Write-Host "  4. start $serviceName again and confirm the first request after the restart succeeds"
    Write-Host ''

    Add-ValidationFinding -Stage $stage -Status NotRun `
        -Detail 'Dry run: the service was not stopped, so fail-closed behaviour was not observed.'

    if ($RunPath) {
        Write-Evidence -RunPath $RunPath -Name 'recovery.json' -Data @{ checked = $false; dryRun = $true } | Out-Null
        Save-ValidationStage -RunPath $RunPath -Stage $stage
    }

    return
}

Assert-DedicatedDevice -ConfigPath $ConfigPath -NonInteractive:$NonInteractive

$evidence = [pscustomobject]@{
    checked          = $true
    stoppedOk        = $false
    restartedOk      = $false
    firstRequestOk   = $null
    observations     = @{}
}

Write-Host ''
Write-Host "Stopping $serviceName..." -ForegroundColor Yellow

Stop-Service -Name $serviceName -Force -ErrorAction Stop
Start-Sleep -Seconds 2

$evidence.stoppedOk = ((Get-Service -Name $serviceName).Status -eq 'Stopped')

if (-not $evidence.stoppedOk) {
    Add-ValidationFinding -Stage $stage -Status Fail -Detail 'The service could not be stopped, so nothing was observed.'
}
else {
    Add-ValidationFinding -Stage $stage -Status Pass -Detail 'An administrator could stop the service.'

    $questions = @(
        @{ Key = 'screen-time-not-unlimited'
           Ask = 'With the broker stopped, does KidShell REFUSE rather than report unlimited screen time?'
           Pass = 'Screen time failed closed with the broker stopped.'
           Fail = 'Screen time became unlimited with the broker stopped.' }

        @{ Key = 'policy-not-from-child-file'
           Ask = 'Does KidShell refuse to load the policy rather than falling back to the child''s own file?'
           Pass = 'The policy did not fall back to the child-writable file.'
           Fail = 'The policy fell back to the child-writable file.' }

        @{ Key = 'throttle-not-memory-only'
           Ask = 'Is a PIN cooldown still enforced rather than starting fresh?'
           Pass = 'The PIN throttle did not revert to memory only.'
           Fail = 'The PIN throttle reverted to memory only.' }

        @{ Key = 'ui-honest'
           Ask = 'Does the UI say plainly that enforcement is unavailable?'
           Pass = 'The UI reported the situation honestly.'
           Fail = 'The UI claimed protection it did not have.' }
    )

    foreach ($question in $questions) {
        if ($NonInteractive) {
            Add-ValidationFinding -Stage $stage -Status NotRun -Detail "$($question.Ask) (not observed)"
            $evidence.observations[$question.Key] = 'not observed'
            continue
        }

        Write-Host ''
        Write-Host $question.Ask

        switch ((Read-Host '  yes / no / (return for not observed)').Trim().ToLowerInvariant()) {
            { $_ -in @('yes', 'y') } {
                Add-ValidationFinding -Stage $stage -Status Pass -Detail $question.Pass
                $evidence.observations[$question.Key] = 'yes'
            }
            { $_ -in @('no', 'n') } {
                Add-ValidationFinding -Stage $stage -Status Fail -Detail $question.Fail
                $evidence.observations[$question.Key] = 'no'
            }
            default {
                Add-ValidationFinding -Stage $stage -Status NotRun -Detail "$($question.Ask) (not observed)"
                $evidence.observations[$question.Key] = 'not observed'
            }
        }
    }
}

# Always restarted, whatever was observed. Leaving the machine with its security
# service stopped would be this script breaking the thing it came to audit.
Write-Host ''
Write-Host "Starting $serviceName again..." -ForegroundColor Yellow

try {
    Start-Service -Name $serviceName -ErrorAction Stop
    Start-Sleep -Seconds 3

    $evidence.restartedOk = ((Get-Service -Name $serviceName).Status -eq 'Running')
}
catch {
    $evidence.restartedOk = $false
}

if ($evidence.restartedOk) {
    Add-ValidationFinding -Stage $stage -Status Pass -Detail 'The service started again.'

    # The first request after a start is the one that used to fail identity
    # resolution under impersonation, so it is checked here rather than assumed.
    $pipe = & (Join-Path $PSScriptRoot '06-verify-pipe.ps1') -ConfigPath $ConfigPath -AfterServiceRestart 2>&1
    $pipe | ForEach-Object { Write-Host $_ }

    $evidence.firstRequestOk = -not ($pipe -match 'first-request-after-restart.*Fail')

    Add-ValidationFinding -Stage $stage `
        -Status $(if ($evidence.firstRequestOk) { 'Pass' } else { 'Fail' }) `
        -Detail "The first request after the restart $(if ($evidence.firstRequestOk) { 'succeeded' } else { 'FAILED' })."
}
else {
    Add-ValidationFinding -Stage $stage -Status Fail `
        -Detail 'The service did NOT start again. Start it by hand before going any further.'
}

if ($RunPath) {
    Write-Evidence -RunPath $RunPath -Name 'recovery.json' -Data $evidence | Out-Null
    Save-ValidationStage -RunPath $RunPath -Stage $stage
}

Write-Host ''
Write-Host "Service status: $((Get-Service -Name $serviceName).Status)" -ForegroundColor Green
