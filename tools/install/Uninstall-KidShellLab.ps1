<#
.SYNOPSIS
    Removes a KidShell lab installation, using the receipt. Dry run by default.

.DESCRIPTION
    IT REMOVES WHAT THE RECEIPT SAYS WAS INSTALLED, AND NOTHING ELSE.

    A file under the install root that the receipt does not list is reported and
    LEFT. Something put it there; on a lab device that something is usually the
    operator, and deleting it would be this tool guessing with administrator
    rights.

    WHAT IT WILL NOT DO

    It does not run `Remove-Item C:\ProgramData\KidShell -Recurse -Force`, which
    is what the runbook used to suggest by hand. Under that path live the
    recovery manifests - the record of how to undo a security change - and the
    protected store, which may still hold the policy the device is running on. A
    blind recursive delete there takes away the one thing a parent needs if the
    uninstall itself goes wrong.

    So ProgramData is preserved by default. -RemoveState is how somebody asks
    for it explicitly, and it still refuses to touch the recovery manifests
    unless they ask for those separately.

    SERVICES ARE NOT STOPPED BY DEFAULT. A lab installation may have had the
    security service registered by a later, separate stage. Removing the files
    out from under a running LocalSystem service is a worse state than either
    one, so this refuses unless -StopServices is given, and then it uses the
    supported service-control path rather than deleting a binary and hoping.
#>
[CmdletBinding()]
param(
    [string] $ConfigPath,

    # Remove the files. Without this, nothing happens.
    [switch] $Apply,

    # Also stop and deregister KidShell services. Needs -Apply.
    [switch] $StopServices,

    # Also remove C:\ProgramData\KidShell\installation. Recovery manifests and
    # the protected store are still preserved.
    [switch] $RemoveState,

    [switch] $NonInteractive
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$programFiles = [Environment]::GetFolderPath('ProgramFiles')
$programData = [Environment]::GetFolderPath('CommonApplicationData')

$installRoot = Join-Path $programFiles 'KidShell'
$receiptPath = Join-Path $programData 'KidShell\installation\install-receipt.json'

Write-Host ''
Write-Host '############################################################' -ForegroundColor Cyan
Write-Host '#  KidShell LAB uninstaller                                #' -ForegroundColor Cyan
Write-Host '############################################################' -ForegroundColor Cyan
Write-Host ''

function Find-Tool {
    $candidates = @(
        (Join-Path $installRoot 'KidShell.DeviceValidation\KidShell.DeviceValidation.exe'),
        (Join-Path $PSScriptRoot 'KidShell.DeviceValidation.exe'),
        (Join-Path (Split-Path -Parent $PSScriptRoot) 'components\KidShell.DeviceValidation\KidShell.DeviceValidation.exe')
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return $candidate }
    }

    return $null
}

$tool = Find-Tool

if (-not $tool) {
    Write-Host 'KidShell.DeviceValidation.exe was not found, so the receipt cannot be read.' -ForegroundColor Yellow
    Write-Host 'Without it this tool will not guess what to delete. Remove the files by hand' -ForegroundColor Yellow
    Write-Host "from $installRoot if you are sure, and leave $programData\KidShell alone." -ForegroundColor Yellow
    exit 1
}

if (-not (Test-Path $receiptPath)) {
    Write-Host "There is no install receipt at $receiptPath." -ForegroundColor Yellow
    Write-Host 'Nothing will be removed: without a receipt, what this product installed is' -ForegroundColor Yellow
    Write-Host 'not known, and deleting a directory because of its name is not an uninstall.' -ForegroundColor Yellow
    exit 1
}

# ------------------------------------------------------------ what is there

Write-Host '--- what is installed ---' -ForegroundColor Cyan

$arguments = @('uninstall')
if ($Apply) { $arguments += '--apply' }

if (-not $Apply) {
    & $tool @arguments 2>&1 | ForEach-Object { Write-Host "  $_" }

    Write-Host ''
    Write-Host 'DRY RUN. Nothing was removed.' -ForegroundColor Yellow
    Write-Host 'Run again with -Apply to remove the files listed above.' -ForegroundColor Yellow
    exit 0
}

# ---------------------------------------------------------------- the gates

$module = Join-Path (Split-Path -Parent $PSScriptRoot) 'device-validation\KidShellValidation.psm1'

if (-not (Test-Path $module)) {
    $module = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'tools\device-validation\KidShellValidation.psm1'
}

if (Test-Path $module) {
    Import-Module $module -Force
    Assert-DedicatedDevice -ConfigPath $ConfigPath -NonInteractive:$NonInteractive
}
else {
    # No module means no interlock, and an uninstall is a mutation. It is a
    # smaller one than an install - it removes what a receipt says this product
    # put there - so elevation plus the receipt is the floor rather than a
    # refusal, and the gap is stated rather than hidden.
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $elevated = (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)

    if (-not $elevated) {
        throw 'This window is not elevated.'
    }

    Write-Warning 'The validation module was not found, so the dedicated-device interlock was not evaluated.'
    Write-Warning 'Proceeding on elevation and the receipt alone.'
}

# -------------------------------------------------------------- services

if ($StopServices) {
    Write-Host ''
    Write-Host '--- services ---' -ForegroundColor Cyan

    foreach ($name in @('KidShellSecurityHost', 'KidShellWatchdog')) {
        $service = Get-Service -Name $name -ErrorAction SilentlyContinue

        if (-not $service) {
            Write-Host "  $name is not installed."
            continue
        }

        Write-Host "  stopping $name..."

        # Stopped before the registration goes. A service left running with its
        # registration deleted keeps its handles open, and the next install
        # finds the name taken by a process nothing can address.
        & sc.exe stop $name | Out-Null
        Start-Sleep -Seconds 2
        & sc.exe delete $name | Out-Null

        $after = Get-Service -Name $name -ErrorAction SilentlyContinue

        Write-Host "  $name $(if ($after) { 'COULD NOT BE REMOVED' } else { 'removed' })"
    }
}
else {
    $running = @(Get-Service -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '*KidShell*' })

    if ($running.Count -gt 0) {
        Write-Host ''
        Write-Host 'REFUSED: KidShell services are registered and -StopServices was not given.' -ForegroundColor Red

        foreach ($service in $running) {
            Write-Host "  $($service.Name) ($($service.Status))" -ForegroundColor Red
        }

        Write-Host ''
        Write-Host 'Removing files from under a running LocalSystem service leaves a worse state' -ForegroundColor Red
        Write-Host 'than either one. Pass -StopServices, or stop them yourself first.' -ForegroundColor Red
        exit 1
    }
}

# ----------------------------------------------------------------- the files

Write-Host ''
Write-Host '--- removing files ---' -ForegroundColor Cyan

& $tool @arguments 2>&1 | ForEach-Object { Write-Host "  $_" }
$code = $LASTEXITCODE

# ------------------------------------------------------------------- state

Write-Host ''
Write-Host '--- state ---' -ForegroundColor Cyan

$preserved = @(
    (Join-Path $programData 'KidShell\security\recovery'),
    (Join-Path $programData 'KidShell\policy'),
    (Join-Path $programData 'KidShell\logs')
)

if ($RemoveState) {
    $installationDirectory = Join-Path $programData 'KidShell\installation'

    if (Test-Path $installationDirectory) {
        Remove-Item $installationDirectory -Recurse -Force
        Write-Host "  removed $installationDirectory"
    }

    Write-Host '  recovery manifests, the protected store and the logs were KEPT.'
    Write-Host '  Those are how a parent undoes a security change; remove them by hand if'
    Write-Host '  you are certain, and only after reading docs\RECOVERY.md.'
}
else {
    Write-Host '  nothing under ProgramData was touched. Pass -RemoveState to remove the'
    Write-Host '  installation record; the recovery manifests and protected store are'
    Write-Host '  preserved either way.'
}

foreach ($path in $preserved) {
    if (Test-Path $path) {
        $count = @(Get-ChildItem $path -Recurse -File -ErrorAction SilentlyContinue).Count
        Write-Host "  PRESERVED: $path ($count file(s))"
    }
}

# --------------------------------------------------------------- leftovers

Write-Host ''

if (Test-Path $installRoot) {
    $leftovers = @(Get-ChildItem $installRoot -Recurse -File -ErrorAction SilentlyContinue)

    if ($leftovers.Count -gt 0) {
        Write-Host "$($leftovers.Count) file(s) remain under $installRoot :" -ForegroundColor Yellow

        foreach ($file in $leftovers | Select-Object -First 20) {
            Write-Host "  $($file.FullName)" -ForegroundColor Yellow
        }

        Write-Host ''
        Write-Host 'These were not in the receipt, so they were left alone deliberately.' -ForegroundColor Yellow
    }
    else {
        Write-Host "$installRoot is empty." -ForegroundColor Green
    }
}
else {
    Write-Host "$installRoot is gone." -ForegroundColor Green
}

exit $code
