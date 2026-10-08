<#
.SYNOPSIS
    Installs the signed KidShell MSIX for the configured child account.

.DESCRIPTION
    Run this from an ordinary, non-elevated PowerShell while signed in as the
    KidShell child. It refuses another account and refuses unsigned or
    production bundles.
#>
[CmdletBinding()]
param(
    [string] $BundlePath,
    [string] $ConfigPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $BundlePath) {
    $BundlePath = Split-Path -Parent $PSScriptRoot
}
$BundlePath = (Resolve-Path $BundlePath).Path

if (-not $ConfigPath) {
    $ConfigPath = Join-Path $BundlePath 'device-validation\device-validation.json'
}
if (-not (Test-Path $ConfigPath)) {
    throw "Validation config was not found at '$ConfigPath'."
}

$config = Get-Content $ConfigPath -Raw | ConvertFrom-Json
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$currentSid = $identity.User.Value

$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this as the ordinary KidShell child account, not from an elevated PowerShell.'
}

if ([string]::IsNullOrWhiteSpace([string]$config.expectedChildSid)) {
    throw 'expectedChildSid is empty in device-validation.json.'
}
if ($currentSid -ne [string]$config.expectedChildSid) {
    throw "This account is SID $currentSid, but the configured child is $($config.expectedChildSid). Refusing to register KidShell for the wrong user."
}

$manifestPath = Join-Path $BundlePath 'release-manifest.json'
if (-not (Test-Path $manifestPath)) { throw 'release-manifest.json is missing.' }

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
if ($manifest.channel -ne 'DedicatedLabSigned' -or -not [bool]$manifest.signed) {
    throw "This child installer accepts only DedicatedLabSigned bundles (channel=$($manifest.channel), signed=$($manifest.signed))."
}

$msix = Get-ChildItem (Join-Path $BundlePath 'package') -Recurse -File -Filter '*.msix' |
    Where-Object { $_.Name -like 'KidShell.App_*' } |
    Select-Object -First 1

if (-not $msix) {
    throw 'The KidShell MSIX was not found in the bundle.'
}

$signature = Get-AuthenticodeSignature -FilePath $msix.FullName
if ($signature.Status -ne 'Valid') {
    throw "The KidShell MSIX signature is not trusted for this account/device (status: $($signature.Status)). Run Trust-KidShellLabCertificate.ps1 as Administrator first."
}

Write-Host ''
Write-Host '===== KidShell child app install =====' -ForegroundColor Cyan
Write-Host "Account : $($identity.Name)"
Write-Host "SID     : $currentSid"
Write-Host "Bundle  : $($manifest.gitSha)"
Write-Host "Package : $($msix.FullName)"
Write-Host ''

Add-AppxPackage -Path $msix.FullName

$installed = Get-AppxPackage | Where-Object { $_.Name -eq $manifest.packageIdentity } | Select-Object -First 1
if (-not $installed) {
    throw 'Add-AppxPackage returned without an error, but KidShell is not registered for this account.'
}

Write-Host 'KIDSHELL APP: INSTALLED FOR CHILD' -ForegroundColor Green
Write-Host "  Package: $($installed.PackageFullName)"
Write-Host 'No Windows lockdown setting was changed.'
