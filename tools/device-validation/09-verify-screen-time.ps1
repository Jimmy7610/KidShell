<#
.SYNOPSIS
    Read-only check of the screen-time counter's semantics and persistence.

.DESCRIPTION
    It reads the protected counter, records its semantic values and a digest,
    and compares against an earlier snapshot. It writes nothing to the store.

    THE INVARIANT IT IS LOOKING FOR

    Used time must never go DOWN across anything: an app restart, the child
    signing out and in, a service restart, a reboot. And a persistence failure
    must never make the product more permissive - a counter that could not be
    read means the day is spent, not that the day is fresh.

    So this is run repeatedly with -Snapshot, once per transition, and each run
    compares against the last. Any decrease is a failure; the digests are what
    make "it did not change" a comparison rather than a recollection.
#>
[CmdletBinding()]
param(
    [string] $RunPath,

    # A name for this point in the sequence: before-restart, after-restart,
    # after-signout, after-reboot.
    [string] $Snapshot = 'snapshot',

    # The previous snapshot to compare against.
    [string] $Against
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'KidShellValidation.psm1') -Force

$store = 'C:\ProgramData\KidShell\policy'
$counterPath = Join-Path $store 'screen-time-state.json'
$stage = New-ValidationStage -Name 'SCREEN TIME'

Write-Host ''
Write-Host '===== Screen time (read-only) =====' -ForegroundColor Cyan
Write-Host ("Snapshot : {0}" -f $Snapshot)

$current = [pscustomobject]@{
    snapshot      = $Snapshot
    capturedUtc   = (Get-Date).ToUniversalTime().ToString('u')
    present       = (Test-Path $counterPath)
    readable      = $false
    digest        = ''
    usedSeconds   = $null
    sequence      = $null
    localDate     = ''
    sessionState  = ''
    bonusMinutes  = $null
    unlimited     = $null
    clockEvents   = $null
}

if (-not $current.present) {
    Write-Host 'The protected counter does not exist.'

    Add-ValidationFinding -Stage $stage -Status NotRun `
        -Detail "No protected counter at $counterPath, so screen-time persistence was not checked."
}
else {
    $raw = Get-Content $counterPath -Raw
    $current.digest = (Get-FileHash -InputStream ([IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($raw))) `
        -Algorithm SHA256).Hash.ToLowerInvariant()

    try {
        $state = $raw | ConvertFrom-Json

        $current.readable = $true
        $current.usedSeconds = [int]$state.usedSeconds
        $current.sequence = [int]$state.sequence
        $current.localDate = [string]$state.localDate
        $current.sessionState = [string]$state.sessionState
        $current.bonusMinutes = [int]$state.bonusMinutes
        $current.unlimited = [bool]$state.unlimitedForToday
        $current.clockEvents = [int]$state.suspiciousClockEvents

        Write-Host ("Used     : {0}s  sequence {1}  day {2}  session {3}" -f `
            $current.usedSeconds, $current.sequence, $current.localDate, $current.sessionState)
        Write-Host ("Grants   : bonus {0} min, unlimited {1}, clock events {2}" -f `
            $current.bonusMinutes, $current.unlimited, $current.clockEvents)
    }
    catch {
        # Unreadable is a finding, not a gap. The product treats it as "the day
        # is spent", and a validation that shrugged at it would be validating
        # the wrong behaviour.
        Add-ValidationFinding -Stage $stage -Status Fail `
            -Detail 'The protected counter exists and could not be parsed. KidShell must treat this as the day being spent.'
    }

    if ($current.readable) {
        Add-ValidationFinding -Stage $stage -Status Pass `
            -Detail "Counter at '$Snapshot': $($current.usedSeconds)s used, sequence $($current.sequence), day $($current.localDate)."
    }
}

# ------------------------------------------------------------- comparison

if ($Against) {
    if (-not (Test-Path $Against)) {
        Add-ValidationFinding -Stage $stage -Status NotRun `
            -Detail "The earlier snapshot '$Against' was not found, so nothing was compared."
    }
    else {
        $previous = Get-Content $Against -Raw | ConvertFrom-Json

        Write-Host ''
        Write-Host ("Comparing against '{0}': {1}s -> {2}s" -f `
            $previous.snapshot, $previous.usedSeconds, $current.usedSeconds)

        if (-not $previous.readable -or -not $current.readable) {
            Add-ValidationFinding -Stage $stage -Status NotRun `
                -Detail 'One of the two snapshots was unreadable, so no comparison was made.'
        }
        elseif ($previous.localDate -ne $current.localDate) {
            # A new day is the one moment used time may legitimately fall, and
            # the grants must not come with it.
            if ($current.bonusMinutes -ne 0 -or $current.unlimited) {
                Add-ValidationFinding -Stage $stage -Status Fail `
                    -Detail "The day changed from $($previous.localDate) to $($current.localDate) and yesterday's grants carried over."
            }
            else {
                Add-ValidationFinding -Stage $stage -Status Pass `
                    -Detail "The day changed from $($previous.localDate) to $($current.localDate) and the grants did not carry over."
            }
        }
        else {
            if ($current.usedSeconds -lt $previous.usedSeconds) {
                Add-ValidationFinding -Stage $stage -Status Fail `
                    -Detail "Used time FELL from $($previous.usedSeconds)s to $($current.usedSeconds)s across '$($previous.snapshot)' to '$Snapshot'. Time was refunded."
            }
            else {
                Add-ValidationFinding -Stage $stage -Status Pass `
                    -Detail "Used time went from $($previous.usedSeconds)s to $($current.usedSeconds)s across '$($previous.snapshot)' to '$Snapshot'."
            }

            if ($current.sequence -lt $previous.sequence) {
                Add-ValidationFinding -Stage $stage -Status Fail `
                    -Detail "The sequence rolled back from $($previous.sequence) to $($current.sequence)."
            }

            if ($current.clockEvents -lt $previous.clockEvents) {
                Add-ValidationFinding -Stage $stage -Status Fail `
                    -Detail "The clock-event count fell from $($previous.clockEvents) to $($current.clockEvents). Evidence was erased."
            }

            if ($current.bonusMinutes -gt $previous.bonusMinutes) {
                Add-ValidationFinding -Stage $stage -Status Fail `
                    -Detail "Bonus minutes rose from $($previous.bonusMinutes) to $($current.bonusMinutes) with no parent grant in between."
            }

            if ($current.unlimited -and -not $previous.unlimited) {
                Add-ValidationFinding -Stage $stage -Status Fail `
                    -Detail 'Today became unlimited with no parent grant in between.'
            }
        }
    }
}
else {
    Write-Host ''
    Write-Host 'No earlier snapshot given, so this is a baseline only.' -ForegroundColor Yellow
}

if ($RunPath) {
    $name = "screen-time-$($Snapshot).json"

    $written = Write-Evidence -RunPath $RunPath -Name $name -Data $current
    Save-ValidationStage -RunPath $RunPath -Stage $stage

    Write-Host ''
    Write-Host "Snapshot written to $written"
    Write-Host "Pass it to the next run with -Against ""$written""."
}

Write-Host ''
Write-Host 'Nothing was changed.' -ForegroundColor Green
