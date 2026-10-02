<#
.SYNOPSIS
    Reports whether this machine is cleared for destructive validation.

.DESCRIPTION
    STRICTLY READ-ONLY, and the one script an operator should run first on a
    machine they are not certain about.

    It evaluates the interlock and prints every condition standing in the way.
    It does NOT ask for the confirmation phrase: a script whose job is to tell
    you whether you are allowed must not be the script that collects one of the
    permissions.

    Safe everywhere. On a working machine it refuses, lists why, and changes
    nothing - which is the behaviour the whole toolset is built around.
#>
[CmdletBinding()]
param(
    [string] $ConfigPath,
    [string] $RunPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'KidShellValidation.psm1') -Force

$resolved = Get-ValidationConfigPath -Path $ConfigPath

Write-Host ''
Write-Host '===== Is this a dedicated KidShell test device? =====' -ForegroundColor Cyan
Write-Host ''

if (-not $resolved) {
    Write-Host "No validation config. Copy device-validation.example.json to device-validation.json" -ForegroundColor Yellow
    Write-Host "in this folder and fill it in for THIS machine." -ForegroundColor Yellow
}
else {
    Write-Host "Config: $resolved"
    (Invoke-ValidationTool -Arguments @('config', '--config', $resolved)).Output |
        ForEach-Object { Write-Host $_ }
}

Write-Host ''

$facts = Get-InterlockFact
$factsPath = Join-Path ([System.IO.Path]::GetTempPath()) ("kidshell-verify-$([guid]::NewGuid().ToString('n')).json")
$allowed = $false

try {
    $facts | ConvertTo-Json -Depth 5 | Set-Content -Path $factsPath -Encoding utf8

    $arguments = @('interlock', '--facts', $factsPath)
    if ($resolved) { $arguments += @('--config', $resolved) }

    $result = Invoke-ValidationTool -Arguments $arguments
    $result.Output | ForEach-Object { Write-Host $_ }

    # Accepted here means "everything except the phrase is in place", because
    # the phrase was deliberately not collected. It is never a licence to
    # mutate: the mutating scripts ask for the phrase themselves.
    $allowed = $result.Accepted
}
finally {
    if (Test-Path $factsPath) { Remove-Item $factsPath -Force }
}

Write-Host ''

if ($facts.workingMachineSigns.Count -gt 0) {
    Write-Host 'Signs this is somebody''s actual computer:' -ForegroundColor Yellow
    $facts.workingMachineSigns | ForEach-Object { Write-Host "  - $_" -ForegroundColor Yellow }
    Write-Host ''
}

if ($RunPath) {
    Write-Evidence -RunPath $RunPath -Name 'test-machine.json' -Data ([pscustomobject]@{
        machineName         = $facts.machineName
        isElevated          = $facts.isElevated
        markerFilePresent   = $facts.markerFilePresent
        isDevelopmentBuild  = $facts.isDevelopmentBuild
        workingMachineSigns = $facts.workingMachineSigns
        configPresent       = [bool]$resolved
        interlockWouldOpen  = $allowed
    }) | Out-Null
}

Write-Host 'Nothing was changed.' -ForegroundColor Green
