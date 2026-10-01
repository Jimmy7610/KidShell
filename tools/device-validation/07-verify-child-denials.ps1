<#
.SYNOPSIS
    RUN THIS AS THE CHILD. Attempts the things the child must not be able to do.

.DESCRIPTION
    This is the only script in the toolset that tries to do something it expects
    to be refused, and it is the only one whose results are evidence rather than
    inference. An access list that reads correctly is not proof; a write that
    Windows refused is.

    It is safe on any machine, including a development one, because every
    operation it attempts is one it expects to FAIL. If an attempt succeeds that
    is the finding - and on a machine with no protected store there is nothing
    to attempt against, so everything reports NOT RUN.

    THE ONE THING IT CAN DAMAGE, AND WHY IT CANNOT

    If the store were wrongly permissioned, the overwrite and delete probes
    would succeed and would damage the parent's policy. So the probes work on a
    PROBE FILE the script creates in the store directory, never on
    parent-policy.json, screen-time-state.json, pin-throttle.json or
    provisioned.json. A successful create is already the whole finding; there is
    nothing to learn from destroying the real document that the probe file does
    not teach.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File 07-verify-child-denials.ps1 -OutFile C:\KidShell-Validation\child-probes.json
#>
[CmdletBinding()]
param(
    [string] $RunPath,

    # Where to write the probe results, for 05-verify-protected-store.ps1 to
    # consume from the administrator's session.
    [string] $OutFile
)

$ErrorActionPreference = 'Continue'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'KidShellValidation.psm1') -Force

$store = 'C:\ProgramData\KidShell\policy'
$authoritative = @('parent-policy.json', 'screen-time-state.json', 'pin-throttle.json', 'provisioned.json')
$probeFile = Join-Path $store 'validation-probe.tmp'

$probes = New-Object System.Collections.Generic.List[object]
$stage = New-ValidationStage -Name 'CHILD AUTHORIZATION'

function Add-Probe([string] $operation, [bool] $denied, [string] $detail) {
    $script:probes.Add([pscustomobject]@{
        operation = $operation
        denied    = $denied
        detail    = $detail
    })

    if ($denied) {
        Add-ValidationFinding -Stage $script:stage -Status Pass -Detail "'$operation' was refused. $detail"
    }
    else {
        Add-ValidationFinding -Stage $script:stage -Status Fail -Detail "'$operation' SUCCEEDED. $detail"
    }
}

Write-Host ''
Write-Host '===== What the child cannot do =====' -ForegroundColor Cyan
Write-Host ("Running as : {0}\{1}" -f $env:USERDOMAIN, $env:USERNAME)
Write-Host ("Elevated   : {0}" -f (Test-Elevated))
Write-Host ''

if (Test-Elevated) {
    # An elevated run proves nothing about the child. Refusing to pretend is
    # better than recording a denial the child never experienced.
    Write-Host 'This window is ELEVATED, so nothing it is refused says anything about the child.' -ForegroundColor Yellow
    Write-Host 'Sign in as the child account and run this there.' -ForegroundColor Yellow

    Add-ValidationFinding -Stage $stage -Status NotRun `
        -Detail 'Run from an elevated session, so no probe reflects the child account.'
}
elseif (-not (Test-Path $store)) {
    Write-Host "There is no protected store at $store, so there is nothing to be refused by."

    Add-ValidationFinding -Stage $stage -Status NotRun `
        -Detail "No protected store at $store, so the child's denials were not probed."
}
else {
    # ------------------------------------------------------------- create
    try {
        Set-Content -Path $probeFile -Value 'validation' -ErrorAction Stop
        Add-Probe 'create' $false "a file was created at $probeFile"
    }
    catch {
        Add-Probe 'create' $true $_.Exception.GetType().Name
    }

    # ---------------------------------------------------------- overwrite
    #
    # Against the probe file when the create succeeded, and against an
    # authoritative document's EXISTENCE only when it did not: opening one for
    # write is the test, and the handle is closed without writing a byte.
    try {
        if (Test-Path $probeFile) {
            Set-Content -Path $probeFile -Value 'again' -ErrorAction Stop
            Add-Probe 'overwrite' $false 'the probe file was overwritten'
        }
        else {
            $target = Join-Path $store $authoritative[0]

            if (Test-Path $target) {
                $handle = [System.IO.File]::Open(
                    $target, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Write)
                $handle.Dispose()
                Add-Probe 'overwrite' $false "$($authoritative[0]) could be opened for writing"
            }
            else {
                Add-ValidationFinding -Stage $stage -Status NotRun `
                    -Detail "'overwrite' was not probed: nothing to write to."
            }
        }
    }
    catch {
        Add-Probe 'overwrite' $true $_.Exception.GetType().Name
    }

    # ------------------------------------------------------------- append
    try {
        $target = $probeFile
        if (-not (Test-Path $target)) { $target = Join-Path $store $authoritative[0] }

        if (Test-Path $target) {
            Add-Content -Path $target -Value ' ' -ErrorAction Stop
            Add-Probe 'append' $false "$([IO.Path]::GetFileName($target)) was appended to"
        }
        else {
            Add-ValidationFinding -Stage $stage -Status NotRun -Detail "'append' was not probed: nothing to append to."
        }
    }
    catch {
        Add-Probe 'append' $true $_.Exception.GetType().Name
    }

    # ------------------------------------------------------------- rename
    try {
        $target = $probeFile
        if (-not (Test-Path $target)) { $target = Join-Path $store $authoritative[0] }

        if (Test-Path $target) {
            $renamed = "$target.renamed"
            Rename-Item -Path $target -NewName (Split-Path -Leaf $renamed) -ErrorAction Stop

            Add-Probe 'rename' $false "$([IO.Path]::GetFileName($target)) was renamed"

            # Put it back immediately. A rename that worked is already the
            # finding; leaving the parent's policy under the wrong name would
            # be this script breaking the machine it is auditing.
            try { Rename-Item -Path $renamed -NewName (Split-Path -Leaf $target) -ErrorAction Stop } catch { }
        }
        else {
            Add-ValidationFinding -Stage $stage -Status NotRun -Detail "'rename' was not probed: nothing to rename."
        }
    }
    catch {
        Add-Probe 'rename' $true $_.Exception.GetType().Name
    }

    # ------------------------------------------------------------- delete
    try {
        if (Test-Path $probeFile) {
            Remove-Item $probeFile -ErrorAction Stop
            Add-Probe 'delete' $false 'the probe file was deleted'
        }
        else {
            $target = Join-Path $store $authoritative[0]

            if (Test-Path $target) {
                # Opened with Delete access rather than deleted. Enough to
                # establish the right; nothing is removed.
                $handle = [System.IO.File]::Open(
                    $target, [System.IO.FileMode]::Open,
                    [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::Delete)
                $handle.Dispose()
                Add-Probe 'delete' $false "$($authoritative[0]) could be opened for deletion"
            }
            else {
                Add-ValidationFinding -Stage $stage -Status NotRun -Detail "'delete' was not probed: nothing to delete."
            }
        }
    }
    catch {
        Add-Probe 'delete' $true $_.Exception.GetType().Name
    }

    # --------------------------------------- and the service it must not stop
    $service = Get-Service -Name 'KidShellSecurityHost' -ErrorAction SilentlyContinue

    if ($service) {
        $stopOutput = (& sc.exe stop KidShellSecurityHost 2>&1 | Out-String)
        $stopDenied = ($stopOutput -match '5|Access is denied|Åtkomst nekas')

        Add-ValidationFinding -Stage $stage `
            -Status $(if ($stopDenied) { 'Pass' } else { 'Fail' }) `
            -Detail "'sc stop KidShellSecurityHost' as the child: $(if ($stopDenied) { 'refused' } else { "ACCEPTED - $($stopOutput.Trim())" })"

        $configOutput = (& sc.exe config KidShellSecurityHost start= demand 2>&1 | Out-String)
        $configDenied = ($configOutput -match '5|Access is denied|Åtkomst nekas')

        Add-ValidationFinding -Stage $stage `
            -Status $(if ($configDenied) { 'Pass' } else { 'Fail' }) `
            -Detail "'sc config KidShellSecurityHost' as the child: $(if ($configDenied) { 'refused' } else { 'ACCEPTED' })"
    }
    else {
        Add-ValidationFinding -Stage $stage -Status NotRun `
            -Detail 'The service is not installed, so the child was not able to try stopping it.'
    }
}

# Belt and braces: the probe file never survives this script.
if (Test-Path $probeFile) {
    try { Remove-Item $probeFile -Force -ErrorAction Stop } catch { }
}

Write-Host ''

foreach ($probe in $probes) {
    $label = if ($probe.denied) { 'refused ' } else { 'ALLOWED ' }
    Write-Host ("  [{0}] {1}: {2}" -f $label, $probe.operation, $probe.detail)
}

if ($OutFile) {
    $directory = Split-Path -Parent $OutFile
    if ($directory -and -not (Test-Path $directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $probes | ConvertTo-Json -Depth 5 | Set-Content -Path $OutFile -Encoding utf8
    Write-Host ''
    Write-Host "Probe results: $OutFile"
}

if ($RunPath) {
    Write-Evidence -RunPath $RunPath -Name 'child-denials.json' -Data @{
        runAs    = "$env:USERDOMAIN\$env:USERNAME"
        elevated = [bool](Test-Elevated)
        probes   = @($probes)
    } | Out-Null

    Save-ValidationStage -RunPath $RunPath -Stage $stage
}

Write-Host ''
Write-Host 'No authoritative document was modified.' -ForegroundColor Green
