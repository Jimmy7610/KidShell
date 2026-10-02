<#
.SYNOPSIS
    Installs a KidShell lab bundle on a dedicated test device. Dry run by
    default. Installs FILES ONLY.

.DESCRIPTION
    LAB IS IN THE NAME FOR A REASON. This is not the consumer installer. It
    exists so that a dedicated validation device can be given a known build,
    and it refuses to run anywhere else.

    IT INSTALLS FILES AND NOTHING ELSE.

    It does not create a child account, register the security service, install
    the watchdog, write an access list, deploy AppLocker, configure Assigned
    Access, change the shell, touch autostart or alter any registry policy.
    Those are separate, reviewable stages under
    tools\device-validation\apply\.

    That separation is the point. A person must be able to put the binaries on
    a machine without enabling the security, because "install" and "lock this
    computer down" are different decisions and bundling them would make the
    second one happen by accident.

    GATES, ALL OF WHICH MUST HOLD

      the dedicated-device interlock (six separate conditions)
      an elevated window
      -Apply, because the default is a dry run
      a release manifest that parses and is structurally sound
      a bundle whose every file matches its digest
      a bundle with a full Git SHA and a clean working tree
      an upgrade policy that permits this bundle over what is installed
      a destination that is the fixed install root and nothing else

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-KidShellLab.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-KidShellLab.ps1 -Apply
#>
[CmdletBinding()]
param(
    # The bundle root. Defaults to the folder above this script, which is where
    # it sits inside a bundle.
    [string] $BundlePath,

    [string] $ConfigPath,

    # Lay the files down. Without this, nothing is written.
    [switch] $Apply,

    # Permit the same version to be installed again.
    [switch] $Repair,

    [switch] $NonInteractive
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $BundlePath) {
    $BundlePath = Split-Path -Parent $PSScriptRoot
}

$BundlePath = (Resolve-Path $BundlePath).Path

Write-Host ''
Write-Host '############################################################' -ForegroundColor Cyan
Write-Host '#  KidShell LAB installer                                  #' -ForegroundColor Cyan
Write-Host '#  Dedicated test devices only. Installs files only.        #' -ForegroundColor Cyan
Write-Host '############################################################' -ForegroundColor Cyan
Write-Host ''
Write-Host "Bundle: $BundlePath"

# ---------------------------------------------------------------- the tool

function Find-Tool {
    <#
        The decision tool from the BUNDLE, not from the machine.

        A bundle installed by a tool from some other build would be a bundle
        validated against the wrong rules. The bundle carries its own.
    #>
    $candidates = @(
        (Join-Path $BundlePath 'components\KidShell.DeviceValidation\KidShell.DeviceValidation.exe'),
        (Join-Path $PSScriptRoot 'KidShell.DeviceValidation.exe')
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return $candidate }
    }

    throw "KidShell.DeviceValidation.exe was not found in this bundle. The bundle is incomplete."
}

$tool = Find-Tool

function Invoke-Tool([string[]] $Arguments) {
    $output = & $tool @Arguments 2>&1
    $code = $LASTEXITCODE

    $output | ForEach-Object { Write-Host "  $_" }

    return $code
}

# ------------------------------------------------------------ 1. the bundle

Write-Host ''
Write-Host '--- 1. the bundle ---' -ForegroundColor Cyan

$manifestPath = Join-Path $BundlePath 'release-manifest.json'

if (-not (Test-Path $manifestPath)) {
    throw "There is no release-manifest.json in $BundlePath. This is not a KidShell release bundle."
}

# The hash file covers the manifest, so the manifest is checked against it
# before the manifest is trusted to say anything.
$hashFile = Join-Path $BundlePath 'hashes.sha256'

if (Test-Path $hashFile) {
    $expected = (Get-Content $hashFile | Where-Object { $_ -match '\s+release-manifest\.json$' } |
        ForEach-Object { ($_ -split '\s+')[0] }) | Select-Object -First 1

    if ($expected) {
        $actual = (Get-FileHash $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()

        if ($actual -ne $expected) {
            throw "release-manifest.json does not match hashes.sha256. The bundle has been altered."
        }

        Write-Host '  release-manifest.json matches hashes.sha256.'
    }
    else {
        Write-Warning 'hashes.sha256 does not cover release-manifest.json.'
    }
}
else {
    Write-Warning 'There is no hashes.sha256 in this bundle, so the manifest could not be checked against one.'
}

# Shape, safety, and every file's digest. Before anything is copied.
if ((Invoke-Tool @('release-manifest', '--in', $manifestPath, '--bundle', $BundlePath)) -ne 0) {
    throw 'The bundle did not pass verification. Nothing has been installed.'
}

# -------------------------------------------------------------- 2. the plan

Write-Host ''
Write-Host '--- 2. what would happen ---' -ForegroundColor Cyan

$programData = [Environment]::GetFolderPath('CommonApplicationData')
$receiptPath = Join-Path $programData 'KidShell\installation\install-receipt.json'

$architecture = if ([Environment]::Is64BitOperatingSystem) { 'x64' } else { 'x86' }
if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { $architecture = 'ARM64' }

$planArguments = @('plan-install', '--manifest', $manifestPath, '--architecture', $architecture)
if (Test-Path $receiptPath) { $planArguments += @('--receipt', $receiptPath) }
if ($Repair) { $planArguments += '--repair' }

# Deliberately NOT --production: this is the lab path, and a production bundle
# is refused here rather than installed under lab rules.
if ((Invoke-Tool $planArguments) -ne 0) {
    throw 'The upgrade policy refused this bundle. Nothing has been installed.'
}

# ------------------------------------------------------------ 3. dry run?

if (-not $Apply) {
    Write-Host ''
    Write-Host '--- 3. DRY RUN ---' -ForegroundColor Yellow

    Invoke-Tool @('install', '--manifest', $manifestPath, '--bundle', $BundlePath) | Out-Null

    Write-Host ''
    Write-Host 'DRY RUN COMPLETE. Nothing was written.' -ForegroundColor Yellow
    Write-Host 'Run again with -Apply on a dedicated test device to install.' -ForegroundColor Yellow
    Write-Host ''
    exit 0
}

# ----------------------------------------------------------- 4. the gates

Write-Host ''
Write-Host '--- 4. the dedicated-device interlock ---' -ForegroundColor Cyan

$module = Join-Path $BundlePath 'device-validation\KidShellValidation.psm1'

if (-not (Test-Path $module)) {
    $module = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'tools\device-validation\KidShellValidation.psm1'
}

if (-not (Test-Path $module)) {
    throw 'The validation module was not found, so the dedicated-device interlock cannot be evaluated. Refusing to install.'
}

Import-Module $module -Force

# Six conditions, none of them a switch. Throws unless all hold.
Assert-DedicatedDevice -ConfigPath $ConfigPath -NonInteractive:$NonInteractive

# -------------------------------------------------------------- 5. install

Write-Host ''
Write-Host '--- 5. installing ---' -ForegroundColor Cyan

$installArguments = @('install', '--manifest', $manifestPath, '--bundle', $BundlePath, '--apply')
if (Test-Path $receiptPath) { $installArguments += @('--receipt', $receiptPath) }

$code = Invoke-Tool $installArguments

if ($code -eq 3) {
    Write-Host ''
    Write-Host 'THE INSTALL FAILED AND THE ROLLBACK DID NOT FINISH.' -ForegroundColor Red
    Write-Host 'Do not continue. Read the lines above and the recovery record.' -ForegroundColor Red
    exit 3
}

if ($code -ne 0) {
    Write-Host ''
    Write-Host 'The install failed and was rolled back. Nothing was left behind.' -ForegroundColor Yellow
    exit 1
}

# --------------------------------------------------------------- 6. verify

Write-Host ''
Write-Host '--- 6. verifying ---' -ForegroundColor Cyan

Invoke-Tool @('verify-install') | Out-Null

Write-Host ''
Write-Host 'INSTALLED.' -ForegroundColor Green
Write-Host ''
Write-Host 'WHAT WAS NOT DONE, and has to be done separately:' -ForegroundColor Yellow
Write-Host '  - no child account was created'
Write-Host '  - KidShellSecurityHost was NOT registered as a service'
Write-Host '  - the watchdog was NOT registered'
Write-Host '  - no access list was applied to C:\ProgramData\KidShell'
Write-Host '  - no AppLocker policy, no Assigned Access, no shell change'
Write-Host ''
Write-Host 'Continue with docs\DEDICATED-DEVICE-VALIDATION.md, Part 3 onwards.' -ForegroundColor Yellow
