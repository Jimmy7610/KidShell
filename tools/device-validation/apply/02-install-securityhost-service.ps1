<#
.SYNOPSIS
    Registers KidShellSecurityHost as a LocalSystem service. Interlocked, dry run
    by default.

.DESCRIPTION
    Read apply\README.md first.

    Two checks happen before anything is registered, and they are not
    formalities:

    The image must be under the protected install root. A LocalSystem service
    whose binary the child can replace is a privilege escalation with a service
    name, and no access list on the pipe would matter.

    The path must be registered QUOTED. An unquoted path containing a space is
    the classic way Windows ends up starting a different program as SYSTEM at
    every boot.

    Start type is Automatic, and that is a security property rather than a
    convenience: a named pipe belongs to whoever creates it first, so the service
    has to exist before anyone signs in.
#>
[CmdletBinding()]
param(
    [string] $ConfigPath,
    [switch] $Apply,
    [switch] $NonInteractive
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'KidShellValidation.psm1') -Force

$serviceName = 'KidShellSecurityHost'
$displayName = 'KidShell Security Host'

$config = Import-ValidationConfig -Path $ConfigPath
$installRoot = 'C:\Program Files\KidShell'

if ($config -and $config.KidShellInstallRoot) { $installRoot = $config.KidShellInstallRoot }

$image = Join-Path $installRoot 'KidShell.SecurityHost\KidShell.SecurityHost.exe'

Write-Host ''
Write-Host '===== Install the security service =====' -ForegroundColor Cyan
Write-Host ("Service : {0}" -f $serviceName)
Write-Host ("Image   : {0}" -f $image)
Write-Host ("Account : LocalSystem")
Write-Host ("Start   : Automatic")

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue

if ($existing) {
    Write-Host ''
    Write-Host ("The service already exists (status {0}, start {1})." -f $existing.Status, $existing.StartType)
    Write-Host 'Remove it first if you want to reinstall:' -ForegroundColor Yellow
    Write-Host "  sc.exe stop $serviceName ; sc.exe delete $serviceName"
    return
}

if (-not (Test-Path $image)) {
    Write-Host ''
    Write-Host "REFUSED: there is no binary at $image." -ForegroundColor Red
    Write-Host 'Install the KidShell release first.' -ForegroundColor Red
    exit 1
}

# The protected-location check, before registration rather than after.
$programFiles = Split-Path -Parent $installRoot

if ($image -notlike "$programFiles\*" -or $image -like '*..*') {
    Write-Host ''
    Write-Host "REFUSED: $image is not under a protected Program Files location." -ForegroundColor Red
    exit 1
}

$hash = (Get-FileHash $image -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host ("Hash    : {0}" -f $hash)

if (-not $Apply) {
    Write-Host ''
    Write-Host 'DRY RUN. Nothing was registered.' -ForegroundColor Yellow
    Write-Host 'With -Apply this would run:'
    Write-Host ("  sc.exe create {0} binPath= ""\""{1}\"""" obj= LocalSystem start= auto DisplayName= ""{2}""" -f `
        $serviceName, $image, $displayName)
    Write-Host ("  sc.exe failure {0} reset= 86400 actions= restart/60000/restart/60000/restart/60000" -f $serviceName)
    Write-Host ("  sc.exe start {0}" -f $serviceName)
    Write-Host ''
    return
}

Assert-DedicatedDevice -ConfigPath $ConfigPath -NonInteractive:$NonInteractive

Write-Host ''
Write-Host 'Registering...' -ForegroundColor Yellow

# Quoted, so a space in the path cannot make Windows start something else.
$binPath = '"' + $image + '"'

& sc.exe create $serviceName binPath= $binPath obj= LocalSystem start= auto DisplayName= $displayName | Out-Host

if ($LASTEXITCODE -ne 0) {
    Write-Host 'The service could not be created.' -ForegroundColor Red
    exit 1
}

# A restart action, so an unexpected stop recovers. The product fails closed
# without the service, so this is resilience rather than a security control.
& sc.exe failure $serviceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Host

& sc.exe description $serviceName 'Holds KidShell security state. Accepts only typed requests over one named pipe.' | Out-Host

& sc.exe start $serviceName | Out-Host

Start-Sleep -Seconds 3

$now = Get-Service -Name $serviceName -ErrorAction SilentlyContinue

Write-Host ''
Write-Host ("Status: {0}, start {1}" -f $now.Status, $now.StartType) -ForegroundColor Green
Write-Host ''
Write-Host 'Now run, in this order:' -ForegroundColor Yellow
Write-Host '  ..\04-verify-securityhost-service.ps1'
Write-Host '  ..\06-verify-pipe.ps1 -AfterServiceRestart'
