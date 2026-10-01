<#
.SYNOPSIS
    Runs every read-only stage in order and produces a report. Changes nothing.

.DESCRIPTION
    SAFE ON ANY MACHINE, INCLUDING A DEVELOPMENT ONE. Every script it calls is
    read-only; nothing under apply\ is invoked, and nothing that needs -Apply is
    given it.

    What it is for: proving the toolset works, and producing an honest report on
    a machine where most of the answer is NOT RUN. That is the expected result
    off a dedicated device and it is the correct one - a report that said PASS
    here would be the bug.

    On a dedicated device it is the first pass, before the child account exists,
    and then again after each apply step.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File Run-ReadOnlyValidation.ps1
#>
[CmdletBinding()]
param(
    [string] $ConfigPath,
    [string] $EvidenceRoot,
    [switch] $NonInteractive
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'KidShellValidation.psm1') -Force

Write-Host ''
Write-Host '############################################################' -ForegroundColor Cyan
Write-Host '#  KidShell device validation - READ ONLY                  #' -ForegroundColor Cyan
Write-Host '#  Nothing in this run changes the machine.                 #' -ForegroundColor Cyan
Write-Host '############################################################' -ForegroundColor Cyan

$run = & (Join-Path $PSScriptRoot '01-capture-baseline.ps1') `
    -ConfigPath $ConfigPath -EvidenceRoot $EvidenceRoot

$runPath = $run.Path

$stages = @(
    @{ Script = '00-preflight.ps1'; Arguments = @{ RunPath = $runPath; ConfigPath = $ConfigPath } }
    @{ Script = '02-verify-test-machine.ps1'; Arguments = @{ RunPath = $runPath; ConfigPath = $ConfigPath } }
    @{ Script = '03-verify-accounts.ps1'; Arguments = @{ RunPath = $runPath; ConfigPath = $ConfigPath } }
    @{ Script = '04-verify-securityhost-service.ps1'; Arguments = @{ RunPath = $runPath; ConfigPath = $ConfigPath } }
    @{ Script = '05-verify-protected-store.ps1'; Arguments = @{ RunPath = $runPath; ConfigPath = $ConfigPath } }
    @{ Script = '06-verify-pipe.ps1'; Arguments = @{ RunPath = $runPath; ConfigPath = $ConfigPath } }
    @{ Script = '07-verify-child-denials.ps1'; Arguments = @{ RunPath = $runPath } }
    @{ Script = '09-verify-screen-time.ps1'; Arguments = @{ RunPath = $runPath; Snapshot = 'read-only' } }
    @{ Script = '10-verify-pin-throttle.ps1'; Arguments = @{ RunPath = $runPath; Snapshot = 'read-only' } }
    @{ Script = '11-verify-service-recovery.ps1'; Arguments = @{ RunPath = $runPath; ConfigPath = $ConfigPath } }
    @{ Script = '13-check-applocker-capability.ps1'; Arguments = @{ RunPath = $runPath } }
    @{ Script = '14-check-assigned-access-capability.ps1'; Arguments = @{ RunPath = $runPath } }
)

foreach ($stage in $stages) {
    $path = Join-Path $PSScriptRoot $stage.Script

    Write-Host ''
    Write-Host ("--- {0} " -f $stage.Script).PadRight(60, '-') -ForegroundColor DarkCyan

    try {
        # Splatting takes @variable, not @(...). Wrapping the hashtable in an
        # array made every stage receive one positional argument and throw.
        $splat = $stage.Arguments

        & $path @splat *>&1 | ForEach-Object { Write-Host $_ }
    }
    catch {
        # A stage that threw has not run. Recorded as such rather than
        # abandoning the whole pass: the report is more useful with eleven
        # honest stages than with none.
        Write-Host ("{0} threw: {1}" -f $stage.Script, $_.Exception.Message) -ForegroundColor Red
    }
}

# 08 and 12 need a human at the keyboard or a reboot, so they are only invoked
# when somebody is actually there to answer.
if (-not $NonInteractive) {
    Write-Host ''
    Write-Host ('--- 08-verify-parent-actions.ps1 '.PadRight(60, '-')) -ForegroundColor DarkCyan
    & (Join-Path $PSScriptRoot '08-verify-parent-actions.ps1') -RunPath $runPath
}
else {
    & (Join-Path $PSScriptRoot '08-verify-parent-actions.ps1') -RunPath $runPath -NonInteractive
}

Write-Host ''
Write-Host ('--- 15-capture-final-state.ps1 '.PadRight(60, '-')) -ForegroundColor DarkCyan
& (Join-Path $PSScriptRoot '15-capture-final-state.ps1') -RunPath $runPath -ConfigPath $ConfigPath

Write-Host ''
Write-Host ('--- 16-generate-report.ps1 '.PadRight(60, '-')) -ForegroundColor DarkCyan

& (Join-Path $PSScriptRoot '16-generate-report.ps1') -RunPath $runPath

Write-Host ''
Write-Host ("Evidence: {0}" -f $runPath) -ForegroundColor Green
