<#
.SYNOPSIS
    Trusts the public certificate from a DedicatedLabSigned KidShell bundle.
    Dry run unless -Apply.

.DESCRIPTION
    Dedicated test devices only. Imports ONLY the public certificate carried by
    the bundle into LocalMachine\TrustedPeople so a standard child account can
    install the signed KidShell MSIX.

    The private key never leaves the development PC.
#>
[CmdletBinding()]
param(
    [string] $BundlePath,
    [string] $ConfigPath,
    [switch] $Apply,
    [switch] $NonInteractive
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $BundlePath) {
    $BundlePath = Split-Path -Parent $PSScriptRoot
}
$BundlePath = (Resolve-Path $BundlePath).Path

$module = Join-Path $BundlePath 'device-validation\KidShellValidation.psm1'
if (-not (Test-Path $module)) {
    throw 'KidShellValidation.psm1 was not found in this bundle.'
}
Import-Module $module -Force

Assert-DedicatedDevice -ConfigPath $ConfigPath -NonInteractive:$NonInteractive

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Open PowerShell as Administrator. Trusting a machine certificate is an administrative action.'
}

$manifestPath = Join-Path $BundlePath 'release-manifest.json'
$cerPath = Join-Path $BundlePath 'manifests\KidShell-DedicatedLab-Public.cer'

if (-not (Test-Path $manifestPath)) { throw 'release-manifest.json is missing.' }
if (-not (Test-Path $cerPath)) { throw 'The bundle has no KidShell-DedicatedLab-Public.cer.' }

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
if ($manifest.channel -ne 'DedicatedLabSigned' -or -not [bool]$manifest.signed) {
    throw "This is not a DedicatedLabSigned bundle (channel=$($manifest.channel), signed=$($manifest.signed))."
}

$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($cerPath)
$store = [Security.Cryptography.X509Certificates.X509Store]::new(
    [Security.Cryptography.X509Certificates.StoreName]::TrustedPeople,
    [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
$store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
try {
    $alreadyTrusted = $store.Certificates |
        Where-Object Thumbprint -eq $certificate.Thumbprint |
        Select-Object -First 1
}
finally {
    $store.Close()
}

Write-Host ''
Write-Host '===== KidShell dedicated-lab certificate trust =====' -ForegroundColor Cyan
Write-Host "Bundle     : $BundlePath"
Write-Host "Channel    : $($manifest.channel)"
Write-Host "Thumbprint : $($certificate.Thumbprint)"
Write-Host "Expires    : $($certificate.NotAfter.ToString('yyyy-MM-dd'))"
Write-Host "Trusted    : $([bool]$alreadyTrusted)"

if (-not $Apply) {
    Write-Host ''
    Write-Host 'DRY RUN. Nothing was changed.' -ForegroundColor Yellow
    Write-Host 'Run again with -Apply to trust this PUBLIC certificate on the dedicated test device.'
    exit 0
}

if (-not $alreadyTrusted) {
    Import-Certificate -FilePath $cerPath -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
}

Write-Host ''
Write-Host 'LAB CERTIFICATE TRUSTED.' -ForegroundColor Green
Write-Host 'This grants trust only to this lab certificate. It is not production signing authority.'
