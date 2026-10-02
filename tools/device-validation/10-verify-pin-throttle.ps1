<#
.SYNOPSIS
    Read-only check that a PIN cooldown survives everything it has to.

.DESCRIPTION
    It reads the protected throttle document and records WHETHER there is a
    cooldown and how many failures stand against it. It never writes it, and it
    never records the PIN, the hash, the salt or the document's contents - the
    digest is what makes two snapshots comparable, and the digest is all that
    is kept.

    Evidence redaction is automatic, through Write-Evidence, so even an
    accidental extra field cannot reach a file somebody will attach to a bug
    report.

    THE SEQUENCE THIS IS FOR

    Enter the wrong PIN four times, then take a snapshot. Then after closing and
    reopening KidShell, after restarting the service, after the child signs out
    and in, and after a reboot. The cooldown must still be there every time, and
    the failure count must never fall.
#>
[CmdletBinding()]
param(
    [string] $RunPath,
    [string] $Snapshot = 'snapshot',
    [string] $Against
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'KidShellValidation.psm1') -Force

$throttlePath = 'C:\ProgramData\KidShell\policy\pin-throttle.json'
$stage = New-ValidationStage -Name 'PIN THROTTLE'

Write-Host ''
Write-Host '===== PIN throttle (read-only) =====' -ForegroundColor Cyan
Write-Host ("Snapshot : {0}" -f $Snapshot)

$current = [pscustomobject]@{
    snapshot       = $Snapshot
    capturedUtc    = (Get-Date).ToUniversalTime().ToString('u')
    present        = (Test-Path $throttlePath)
    readable       = $false

    # A digest of the whole document, so two snapshots are comparable without
    # either of them containing it.
    digest         = ''

    # The two facts worth recording in words. Neither is a secret: a count and
    # whether a cooldown is in the future.
    failedAttempts = $null
    cooldownActive = $null
}

if (-not $current.present) {
    Write-Host 'The protected throttle document does not exist.'

    Add-ValidationFinding -Stage $stage -Status NotRun `
        -Detail "No protected throttle at $throttlePath, so the cooldown's persistence was not checked."
}
else {
    $raw = Get-Content $throttlePath -Raw

    $current.digest = (Get-FileHash -InputStream ([IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($raw))) `
        -Algorithm SHA256).Hash.ToLowerInvariant()

    try {
        $state = $raw | ConvertFrom-Json

        $current.readable = $true
        $current.failedAttempts = [int]$state.failedAttemptCount

        $until = $null
        if ($state.PSObject.Properties.Name -contains 'cooldownUntilUtc' -and $state.cooldownUntilUtc) {
            $until = [datetimeoffset]::Parse([string]$state.cooldownUntilUtc)
        }

        $current.cooldownActive = ($null -ne $until -and $until -gt [datetimeoffset]::UtcNow)

        Write-Host ("Failures : {0}" -f $current.failedAttempts)
        Write-Host ("Cooldown : {0}" -f $(if ($current.cooldownActive) { 'active' } else { 'none or expired' }))
    }
    catch {
        # A corrupt throttle must resolve towards "there is a cooldown", so an
        # unreadable one is a finding about the product rather than a gap.
        Add-ValidationFinding -Stage $stage -Status Fail `
            -Detail 'The protected throttle exists and could not be parsed. KidShell must treat this as a cooldown.'
    }

    if ($current.readable) {
        Add-ValidationFinding -Stage $stage -Status Pass `
            -Detail "Throttle at '$Snapshot': $($current.failedAttempts) failure(s), cooldown $(if ($current.cooldownActive) { 'active' } else { 'not active' })."
    }
}

if ($Against) {
    if (-not (Test-Path $Against)) {
        Add-ValidationFinding -Stage $stage -Status NotRun `
            -Detail "The earlier snapshot '$Against' was not found, so nothing was compared."
    }
    else {
        $previous = Get-Content $Against -Raw | ConvertFrom-Json

        Write-Host ''
        Write-Host ("Comparing against '{0}'" -f $previous.snapshot)

        if (-not $previous.readable -or -not $current.readable) {
            Add-ValidationFinding -Stage $stage -Status NotRun `
                -Detail 'One of the two snapshots was unreadable, so no comparison was made.'
        }
        else {
            if ($previous.cooldownActive -and -not $current.cooldownActive) {
                # The headline defect. A cooldown that disappears across a
                # restart prices nothing at all.
                Add-ValidationFinding -Stage $stage -Status Fail `
                    -Detail "The cooldown was active at '$($previous.snapshot)' and is gone at '$Snapshot'. It did not survive."
            }
            elseif ($previous.cooldownActive) {
                Add-ValidationFinding -Stage $stage -Status Pass `
                    -Detail "The cooldown survived from '$($previous.snapshot)' to '$Snapshot'."
            }

            if ($current.failedAttempts -lt $previous.failedAttempts) {
                Add-ValidationFinding -Stage $stage -Status Fail `
                    -Detail "The failure count fell from $($previous.failedAttempts) to $($current.failedAttempts) with no successful parent authentication in between."
            }
        }
    }
}

if ($RunPath) {
    $written = Write-Evidence -RunPath $RunPath -Name "pin-throttle-$($Snapshot).json" -Data $current
    Save-ValidationStage -RunPath $RunPath -Stage $stage

    Write-Host ''
    Write-Host "Snapshot written to $written"
}

Write-Host ''
Write-Host 'No PIN, hash, salt or payload was recorded.' -ForegroundColor Green
