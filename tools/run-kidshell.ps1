<#
    Builds, registers and starts KidShell from the loose build output.

    This is the everyday developer loop for a packaged WinUI 3 app without
    opening Visual Studio. It requires Developer Mode to be enabled in
    Windows Settings (Settings > System > For developers), which is the only
    machine setting KidShell development depends on - and it is a setting the
    developer turns on for themselves, not something KidShell changes.

    Usage:
        powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-kidshell.ps1
        powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-kidshell.ps1 -SkipBuild
        powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-kidshell.ps1 -Unregister
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [ValidateSet('x64', 'ARM64')]
    [string]$Platform = 'x64',

    [switch]$SkipBuild,

    [switch]$Unregister,

    [string]$RepositoryRoot
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $RepositoryRoot = Split-Path -Parent $scriptDir
}

$packageFamilyName = 'KidShell.Barnlage.Dev'

if ($Unregister) {
    Get-Process -Name 'KidShell' -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-AppxPackage -Name $packageFamilyName | ForEach-Object {
        Write-Host "Removing $($_.PackageFullName)"
        Remove-AppxPackage -Package $_.PackageFullName
    }
    Write-Host 'KidShell unregistered.'
    return
}

$project = Join-Path $RepositoryRoot 'src\KidShell.App\KidShell.App.csproj'

# A running instance locks KidShell.exe and the package layout, so it has to
# go before either the build or the re-registration.
Get-Process -Name 'KidShell' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

if (-not $SkipBuild) {
    Write-Host "Building KidShell ($Configuration|$Platform)..."
    & dotnet build $project -c $Configuration -p:Platform=$Platform
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
}

$outputDir = Join-Path $RepositoryRoot "src\KidShell.App\bin\$Platform\$Configuration\net10.0-windows10.0.26100.0"
$manifest = Join-Path $outputDir 'AppxManifest.xml'

if (-not (Test-Path $manifest)) {
    throw "No AppxManifest.xml at '$manifest'. Build the app first."
}

Write-Host 'Registering the package layout...'
Add-AppxPackage -Register $manifest -ForceUpdateFromAnyVersion

$package = Get-AppxPackage -Name $packageFamilyName
if (-not $package) { throw 'Registration reported success but the package is not present.' }

$appId = (Get-AppxPackageManifest $package).Package.Applications.Application.Id
$aumid = "$($package.PackageFamilyName)!$appId"

Write-Host "Starting $aumid"
Start-Process "shell:AppsFolder\$aumid"
Write-Host 'KidShell started.'
