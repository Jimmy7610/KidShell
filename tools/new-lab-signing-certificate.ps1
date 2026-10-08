<#
.SYNOPSIS
    Creates the local code-signing certificate used only for KidShell dedicated-lab builds.

.DESCRIPTION
    The private key stays in Cert:\CurrentUser\My on the development PC and is
    marked non-exportable. Only the public .cer is exported so a dedicated test
    device can trust the package.

    This certificate is NOT production signing material.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\new-lab-signing-certificate.ps1
#>

[CmdletBinding()]
param(
    [string] $OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repoRoot 'src\KidShell.App\Package.appxmanifest'

if (-not (Test-Path $manifestPath)) {
    throw "Package.appxmanifest was not found at '$manifestPath'."
}

[xml] $manifest = Get-Content $manifestPath
$identity = $manifest.SelectSingleNode("//*[local-name()='Identity']")

if (-not $identity) {
    throw 'Package.appxmanifest has no Identity element.'
}

$publisher = $identity.GetAttribute('Publisher')

if ([string]::IsNullOrWhiteSpace($publisher)) {
    throw 'Package.appxmanifest has no Publisher.'
}

if (-not $OutputPath) {
    $artifactRoot = Join-Path $repoRoot 'release-artifacts'
    New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
    $OutputPath = Join-Path $artifactRoot 'KidShell-DedicatedLab-Public.cer'
}

$expectedSubject = [Security.Cryptography.X509Certificates.X500DistinguishedName]::new($publisher)
$expectedSubjectHex = [Convert]::ToHexString($expectedSubject.RawData)

$existing = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object {
        [Convert]::ToHexString($_.SubjectName.RawData) -eq $expectedSubjectHex -and
        $_.HasPrivateKey -and
        $_.NotAfter -gt (Get-Date).AddDays(30)
    } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if ($existing) {
    $cert = $existing
    Write-Host "Reusing existing KidShell lab certificate: $($cert.Thumbprint)"
}
else {
    $cert = New-SelfSignedCertificate         -Type CodeSigningCert         -Subject $publisher         -CertStoreLocation 'Cert:\CurrentUser\My'         -KeyAlgorithm RSA         -KeyLength 3072         -HashAlgorithm SHA256         -KeyExportPolicy NonExportable         -NotAfter (Get-Date).AddYears(2)

    if (-not $cert) {
        throw 'The lab certificate could not be created.'
    }

    if ([Convert]::ToHexString($cert.SubjectName.RawData) -ne $expectedSubjectHex) {
        Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -Force -ErrorAction SilentlyContinue
        throw "The generated certificate subject '$($cert.Subject)' does not match the package Publisher '$publisher'."
    }

    Write-Host "Created KidShell lab certificate: $($cert.Thumbprint)"
}

# Trust it on the development PC so SignTool verification proves the complete
# signature chain during the build. This trusts only the public certificate;
# the private key remains in CurrentUser\My.
$trusted = Get-ChildItem Cert:\CurrentUser\TrustedPeople |
    Where-Object Thumbprint -eq $cert.Thumbprint |
    Select-Object -First 1

if (-not $trusted) {
    $tempCer = Join-Path $env:TEMP "KidShell-Lab-$($cert.Thumbprint).cer"

    try {
        Export-Certificate -Cert $cert -FilePath $tempCer -Force | Out-Null
        Import-Certificate -FilePath $tempCer -CertStoreLocation Cert:\CurrentUser\TrustedPeople | Out-Null
    }
    finally {
        Remove-Item $tempCer -Force -ErrorAction SilentlyContinue
    }
}

$parent = Split-Path -Parent $OutputPath
if ($parent) {
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
}

Export-Certificate -Cert $cert -FilePath $OutputPath -Force | Out-Null

Write-Host ''
Write-Host 'KIDSHELL DEDICATED-LAB CERTIFICATE' -ForegroundColor Yellow
Write-Host "  Subject    : $($cert.Subject)"
Write-Host "  Thumbprint : $($cert.Thumbprint)"
Write-Host "  Expires    : $($cert.NotAfter.ToString('yyyy-MM-dd'))"
Write-Host "  Public CER : $OutputPath"
Write-Host ''
Write-Host 'The private key was NOT exported.' -ForegroundColor Green
Write-Host 'Use this thumbprint with build-release.ps1 -LabSign -CertificateThumbprint <thumbprint>.'
Write-Host 'Trust only the public CER on the dedicated KidShell test device.'
