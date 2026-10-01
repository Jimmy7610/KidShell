<#
.SYNOPSIS
    Reports whether this Windows can put the child in a restricted shell.
    Configures nothing.

.DESCRIPTION
    STRICTLY READ-ONLY. It does not create, set or clear an Assigned Access
    configuration, and it does not change the shell.

    Same honesty rule as AppLocker and the same edition line: Windows Home
    cannot do Assigned Access, so Secure Mode is unavailable there and Standard
    Mode is what a parent gets. The verdict comes from the decision tool, which
    has the edition list and the build floor with tests behind them.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File 14-check-assigned-access-capability.ps1
#>
[CmdletBinding()]
param(
    [string] $RunPath,
    $PlatformFactsObject,
    [switch] $Quiet
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'KidShellValidation.psm1') -Force

if (-not $PlatformFactsObject) {
    $cv = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'

    function Reg([string] $name) {
        try { return [string](Get-ItemProperty -Path $cv -Name $name -ErrorAction Stop).$name }
        catch { return '' }
    }

    $cmdlets = [bool](Get-Command Get-AssignedAccess -ErrorAction SilentlyContinue)
    $build = [int]((Reg 'CurrentBuild') -as [int])

    $PlatformFactsObject = [pscustomobject]@{
        editionId                    = (Reg 'EditionID')
        productName                  = (Reg 'ProductName')
        build                        = $build
        appIdServicePresent          = $false
        appIdServiceStartType        = ''
        appLockerCmdletsPresent      = $false
        appLockerPolicyReadable      = $false
        assignedAccessCmdletsPresent = $cmdlets
        multiAppKioskAvailable       = ($cmdlets -and $build -ge 17763)
    }
}

$factsPath = Join-Path ([System.IO.Path]::GetTempPath()) ("kidshell-platform-$([guid]::NewGuid().ToString('n')).json")

try {
    $PlatformFactsObject | ConvertTo-Json -Depth 5 | Set-Content -Path $factsPath -Encoding utf8
    $result = Invoke-ValidationTool -Arguments @('capability', '--facts', $factsPath)

    # Only this script's half of the tool's answer. See the matching note in
    # 13-check-applocker-capability.ps1.
    $verdict = 'NOT_SUPPORTED'
    $reason = ''
    $capture = $false

    foreach ($line in $result.Output) {
        if ($line -match '^ASSIGNED ACCESS CAPABILITY:') {
            $verdict = ($line -split ':', 2)[1].Trim()
            $capture = $true
            continue
        }

        if ($capture) {
            $reason = $line.Trim()
            $capture = $false
        }
    }

    if (-not $Quiet) {
        Write-Host ''
        Write-Host '===== Assigned Access capability (read-only) =====' -ForegroundColor Cyan
    }

    Write-Host "ASSIGNED ACCESS CAPABILITY: $verdict"
    Write-Host "  $reason"

    if ($RunPath) {
        $stage = New-ValidationStage -Name 'ASSIGNED ACCESS CAPABILITY'

        switch ($verdict) {
            'SUPPORTED' { Add-ValidationFinding -Stage $stage -Status Pass -Detail 'The multi-app restricted experience is available.' }
            'PARTIALLY_SUPPORTED' { Add-ValidationFinding -Stage $stage -Status Pass -Detail 'Assigned Access is available and the multi-app configuration was not confirmed.' -Warning }
            default { Add-ValidationFinding -Stage $stage -Status NotSupported -Detail 'This edition does not offer Assigned Access. Standard Mode is what a parent gets here.' }
        }

        Save-ValidationStage -RunPath $RunPath -Stage $stage
    }
}
finally {
    if (Test-Path $factsPath) { Remove-Item $factsPath -Force }
}
