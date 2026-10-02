<#
.SYNOPSIS
    Writes the dedicated-device marker. The first mutation, and the smallest.

.DESCRIPTION
    THE CHICKEN AND EGG, AND HOW IT IS RESOLVED

    Every other mutating script requires the marker. This one creates it, so it
    cannot require it - which would make it the one script with a weaker gate,
    and therefore the only one that matters.

    So it requires everything else instead, and one thing more: the
    working-machine signs must be EMPTY. On a computer with a user's browser
    profile, mail data, a git checkout or a domain membership, this refuses -
    and refuses before writing a file whose entire purpose is to say "destroy
    this machine".

    The file it writes is harmless on its own. What it authorises is not.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File apply\00-declare-test-device.ps1 -Apply
#>
[CmdletBinding()]
param(
    [string] $ConfigPath,

    # Without this, nothing is written.
    [switch] $Apply
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'KidShellValidation.psm1') -Force

$markerDirectory = 'C:\ProgramData\KidShell-TestDevice'
$markerPath = Join-Path $markerDirectory 'ALLOW-KIDSHELL-VALIDATION.txt'

Write-Host ''
Write-Host '===== Declare this machine a dedicated KidShell test device =====' -ForegroundColor Cyan
Write-Host ''

if (Test-Path $markerPath) {
    Write-Host "The marker already exists at $markerPath" -ForegroundColor Green
    Get-Content $markerPath | ForEach-Object { Write-Host "  $_" }
    return
}

$config = Import-ValidationConfig -Path $ConfigPath
$facts = Get-InterlockFact

$refusals = New-Object System.Collections.Generic.List[string]

if (-not $facts.isElevated) {
    $refusals.Add('This window is not elevated.')
}

if (-not $config) {
    $refusals.Add('There is no validation config for this machine.')
}
else {
    if (-not $config.DedicatedTestDevice) {
        $refusals.Add('The config does not say DedicatedTestDevice = true.')
    }

    if ($config.MachineName -ne $env:COMPUTERNAME) {
        $refusals.Add("The config is for '$($config.MachineName)', and this machine is '$env:COMPUTERNAME'.")
    }

    $problems = (Invoke-ValidationTool -Arguments @('config', '--config', (Get-ValidationConfigPath -Path $ConfigPath)))

    if (-not $problems.Accepted) {
        $refusals.Add('The validation config is incomplete. Run 02-verify-test-machine.ps1.')
    }
}

if ($facts.isDevelopmentBuild) {
    $refusals.Add('This is a development build. Run the validation from an installed release.')
}

if ($facts.workingMachineSigns.Count -gt 0) {
    # The condition that exists only here. Writing the marker is the moment a
    # machine becomes expendable, so the signs are a hard refusal at this step
    # rather than a warning.
    $refusals.Add('This machine shows signs of being somebody''s actual computer:')

    foreach ($sign in $facts.workingMachineSigns) {
        $refusals.Add("    - $sign")
    }
}

if ($refusals.Count -gt 0) {
    Write-Host 'REFUSED. The marker was not written.' -ForegroundColor Red
    $refusals | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    Write-Host ''
    Write-Host 'If this really is a sacrificial test machine, fix each line above.' -ForegroundColor Yellow
    exit 1
}

Write-Host 'Everything except your confirmation is in place.' -ForegroundColor Yellow
Write-Host ''
Write-Host 'Writing this marker tells the KidShell validation tooling that this machine' -ForegroundColor Yellow
Write-Host 'may be destroyed: accounts, access lists, the shell, the ability to sign in.' -ForegroundColor Yellow
Write-Host ''
Write-Host 'Type the confirmation phrase exactly:' -ForegroundColor Yellow
Write-Host '  I UNDERSTAND THIS IS A DEDICATED KIDSHELL TEST DEVICE'
Write-Host ''

$phrase = Read-Host 'Phrase'

if ($phrase.Trim() -cne 'I UNDERSTAND THIS IS A DEDICATED KIDSHELL TEST DEVICE') {
    Write-Host 'The phrase did not match. Nothing was written.' -ForegroundColor Red
    exit 1
}

if (-not $Apply) {
    # DRY RUN EVEN NOW. Past every condition and past the phrase, a mutating
    # script still says what it would do unless told to do it.
    Write-Host ''
    Write-Host 'DRY RUN. Nothing was written.' -ForegroundColor Yellow
    Write-Host "With -Apply this would create $markerPath containing:"
    Write-Host ''
    Write-Host '  This machine is a dedicated KidShell validation device.'
    Write-Host '  ... Machine: ' -NoNewline; Write-Host $env:COMPUTERNAME
    Write-Host ''
    exit 0
}

New-Item -ItemType Directory -Force -Path $markerDirectory | Out-Null

$content = @"
This machine is a dedicated KidShell validation device.

Everything on it may be destroyed by KidShell's security validation:
accounts, policy, the shell, the ability to sign in.

Machine: $env:COMPUTERNAME
Declared: $((Get-Date).ToUniversalTime().ToString('u'))

Delete this file to stop the validation tooling from running here.
"@

Set-Content -Path $markerPath -Value $content -Encoding utf8

Write-Host ''
Write-Host "Marker written: $markerPath" -ForegroundColor Green
Write-Host 'Delete that file at any time to close the interlock again.' -ForegroundColor Green
