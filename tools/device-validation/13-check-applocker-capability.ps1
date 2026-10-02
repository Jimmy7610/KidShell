<#
.SYNOPSIS
    Reports whether this Windows can enforce AppLocker. Applies nothing.

.DESCRIPTION
    STRICTLY READ-ONLY. It does not create, import, set or deploy a policy, and
    it does not start the Application Identity service.

    The verdict comes from KidShell.DeviceValidation.exe, so the rule that says
    Windows Home cannot enforce AppLocker lives in one place with unit tests
    behind it rather than being restated here. That matters more than it looks:
    claiming Home can enforce AppLocker is the single most damaging overclaim
    this product could make, because a parent who believes the computer blocks
    unapproved programs supervises less.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File 13-check-applocker-capability.ps1
#>
[CmdletBinding()]
param(
    [string] $RunPath,

    # Passed by 00-preflight so the facts are gathered once.
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

    $appId = Get-Service -Name AppIDSvc -ErrorAction SilentlyContinue
    $startType = ''
    if ($appId) { $startType = [string]$appId.StartType }

    $readable = $false
    try { $null = Get-AppLockerPolicy -Effective -ErrorAction Stop; $readable = $true } catch { }

    $PlatformFactsObject = [pscustomobject]@{
        editionId                    = (Reg 'EditionID')
        productName                  = (Reg 'ProductName')
        build                        = [int]((Reg 'CurrentBuild') -as [int])
        appIdServicePresent          = [bool]$appId
        appIdServiceStartType        = $startType
        appLockerCmdletsPresent      = [bool](Get-Command Get-AppLockerPolicy -ErrorAction SilentlyContinue)
        appLockerPolicyReadable      = $readable
        assignedAccessCmdletsPresent = [bool](Get-Command Get-AssignedAccess -ErrorAction SilentlyContinue)
        multiAppKioskAvailable       = $false
    }
}

$factsPath = Join-Path ([System.IO.Path]::GetTempPath()) ("kidshell-platform-$([guid]::NewGuid().ToString('n')).json")

try {
    $PlatformFactsObject | ConvertTo-Json -Depth 5 | Set-Content -Path $factsPath -Encoding utf8
    $result = Invoke-ValidationTool -Arguments @('capability', '--facts', $factsPath)

    # The tool reports both capabilities in one call, because both come from
    # the same facts. This script owns one of them, so it prints that one: the
    # first version printed everything the tool said and the Assigned Access
    # verdict appeared twice, once here and once from its own script.
    $verdict = 'NOT_SUPPORTED'
    $reason = ''
    $capture = $false

    foreach ($line in $result.Output) {
        if ($line -match '^APPLOCKER CAPABILITY:') {
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
        Write-Host '===== AppLocker capability (read-only) =====' -ForegroundColor Cyan
    }

    Write-Host "APPLOCKER CAPABILITY: $verdict"
    Write-Host "  $reason"

    if ($RunPath) {
        $stage = New-ValidationStage -Name 'APPLOCKER CAPABILITY'

        # A capability report is information, never a failure. An edition that
        # cannot enforce AppLocker has not failed validation; it has a limit,
        # and the limit has to be visible.
        switch ($verdict) {
            'SUPPORTED' { Add-ValidationFinding -Stage $stage -Status Pass -Detail 'AppLocker can be enforced on this edition.' }
            'PARTIALLY_SUPPORTED' { Add-ValidationFinding -Stage $stage -Status Pass -Detail 'AppLocker is available and not currently active.' -Warning }
            default { Add-ValidationFinding -Stage $stage -Status NotSupported -Detail 'This edition has no supported channel for enforcing AppLocker.' }
        }

        Save-ValidationStage -RunPath $RunPath -Stage $stage
    }
}
finally {
    if (Test-Path $factsPath) { Remove-Item $factsPath -Force }
}
