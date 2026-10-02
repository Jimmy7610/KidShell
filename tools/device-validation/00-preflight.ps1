<#
.SYNOPSIS
    Read-only survey of the machine, before anything is decided about it.

.DESCRIPTION
    STRICTLY READ-ONLY. Get-* and registry reads only. Safe to run anywhere,
    including a development machine, and that is the point: this is the script
    that tells an operator whether the machine in front of them is the one the
    validation is for.

    It writes preflight.json for the report and prints the same facts for a
    human. It changes nothing and it decides nothing except whether the
    interlock WOULD open - which it reports without acting on.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File 00-preflight.ps1
#>
[CmdletBinding()]
param(
    [string] $ConfigPath,
    [string] $RunPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'KidShellValidation.psm1') -Force

function Reg([string] $path, [string] $name) {
    try {
        $value = (Get-ItemProperty -Path $path -Name $name -ErrorAction Stop).$name
        if ($null -eq $value) { return '' }
        return [string]$value
    }
    catch { return '' }
}

$cv = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$policies = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'

$appId = Get-Service -Name AppIDSvc -ErrorAction SilentlyContinue
$kidShellServices = @(Get-Service -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '*KidShell*' })

# Computed before the object literal rather than inside it. Windows PowerShell
# 5.1 is the floor here, and an if-expression inside a hashtable value is not
# something it reliably parses.
$appIdStartType = ''
if ($appId) { $appIdStartType = [string]$appId.StartType }

$kidShellServiceNames = 'none'
if ($kidShellServices.Count -gt 0) { $kidShellServiceNames = ($kidShellServices.Name -join ', ') }

# Capability facts, in the shape the decision tool expects. Gathered here,
# judged there.
$platform = [pscustomobject]@{
    editionId                   = (Reg $cv 'EditionID')
    productName                 = (Reg $cv 'ProductName')
    build                       = [int]((Reg $cv 'CurrentBuild') -as [int])
    appIdServicePresent         = [bool]$appId
    appIdServiceStartType       = $appIdStartType
    appLockerCmdletsPresent     = [bool](Get-Command Get-AppLockerPolicy -ErrorAction SilentlyContinue)
    appLockerPolicyReadable     = $false
    assignedAccessCmdletsPresent = [bool](Get-Command Get-AssignedAccess -ErrorAction SilentlyContinue)
    multiAppKioskAvailable      = $false
}

try {
    # Reading an effective policy changes nothing. A machine with no policy
    # throws, which is itself the answer.
    $null = Get-AppLockerPolicy -Effective -ErrorAction Stop
    $platform.appLockerPolicyReadable = $true
}
catch { }

# Multi-app Assigned Access is an edition and build question, and the
# configuration surface being reachable is a separate one. Both are reported;
# neither is assumed.
$platform.multiAppKioskAvailable =
    $platform.assignedAccessCmdletsPresent -and $platform.build -ge 17763

$bitlocker = 'not queried'
try {
    $volume = Get-BitLockerVolume -MountPoint 'C:' -ErrorAction Stop
    $bitlocker = "$($volume.VolumeStatus) / $($volume.ProtectionStatus)"
}
catch { $bitlocker = 'unavailable (needs elevation, or BitLocker is not present)' }

$defender = 'not queried'
try {
    $status = Get-MpComputerStatus -ErrorAction Stop
    $defender = "realtime=$($status.RealTimeProtectionEnabled) tamper=$($status.IsTamperProtected)"
}
catch { $defender = 'unavailable' }

$secureBoot = 'not queried'
try { $secureBoot = [string](Confirm-SecureBootUEFI -ErrorAction Stop) }
catch { $secureBoot = 'unavailable (legacy BIOS, or needs elevation)' }

# Smart App Control, read only. Absent key means the feature is not present.
$smartAppControl = Reg 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' 'VerifiedAndReputablePolicyState'

$smartAppControlState = 'absent'
if ($smartAppControl -ne '') { $smartAppControlState = $smartAppControl }

$config = Import-ValidationConfig -Path $ConfigPath
$facts = Get-InterlockFact

$preflight = [pscustomobject]@{
    capturedUtc        = (Get-Date).ToUniversalTime().ToString('u')
    machineName        = $env:COMPUTERNAME
    currentUser        = "$env:USERDOMAIN\$env:USERNAME"
    isElevated         = $facts.isElevated
    windows            = [pscustomobject]@{
        productName    = $platform.productName
        editionId      = $platform.editionId
        displayVersion = (Reg $cv 'DisplayVersion')
        build          = $platform.build
        ubr            = (Reg $cv 'UBR')
    }
    secureBoot         = $secureBoot
    bitLocker          = $bitlocker
    defender           = $defender
    smartAppControlState = $smartAppControlState
    uac                = [pscustomobject]@{
        enableLua                  = (Reg $policies 'EnableLUA')
        consentPromptBehaviorAdmin = (Reg $policies 'ConsentPromptBehaviorAdmin')
    }
    platform           = $platform
    kidShell           = [pscustomobject]@{
        services                = @($kidShellServices | ForEach-Object {
                                     [pscustomobject]@{ name = $_.Name; status = [string]$_.Status }
                                  })
        programDataPresent      = (Test-Path 'C:\ProgramData\KidShell')
        protectedStorePresent   = (Test-Path 'C:\ProgramData\KidShell\policy')
        installRootPresent      = (Test-Path 'C:\Program Files\KidShell')
        testDeviceMarkerPresent = $facts.markerFilePresent
    }
    accounts           = @(Get-LocalUser -ErrorAction SilentlyContinue | ForEach-Object {
                              [pscustomobject]@{
                                  name    = $_.Name
                                  enabled = $_.Enabled
                                  sid     = [string]$_.SID
                              }
                          })
    existingPolicy     = [pscustomobject]@{
        appLockerPolicyReadable = $platform.appLockerPolicyReadable
        assignedAccessPresent   = $false
    }
    interlock          = [pscustomobject]@{
        configFound        = [bool]$config
        isDevelopmentBuild = $facts.isDevelopmentBuild
        workingMachineSigns = $facts.workingMachineSigns
    }
}

try {
    $assigned = Get-CimInstance -Namespace 'root/cimv2/mdm/dmmap' `
        -ClassName 'MDM_AssignedAccess' -ErrorAction Stop
    $preflight.existingPolicy.assignedAccessPresent = [bool]$assigned.Configuration
}
catch { }

Write-Host ''
Write-Host '===== KidShell preflight (read-only) =====' -ForegroundColor Cyan
Write-Host ("Machine        : {0}" -f $preflight.machineName)
Write-Host ("User           : {0} (elevated: {1})" -f $preflight.currentUser, $preflight.isElevated)
Write-Host ("Windows        : {0} [{1}] build {2}.{3}" -f `
    $preflight.windows.productName, $preflight.windows.editionId,
    $preflight.windows.build, $preflight.windows.ubr)
Write-Host ("Secure Boot    : {0}" -f $preflight.secureBoot)
Write-Host ("BitLocker      : {0}" -f $preflight.bitLocker)
Write-Host ("Defender       : {0}" -f $preflight.defender)
Write-Host ("Smart App Ctrl : {0}" -f $preflight.smartAppControlState)
Write-Host ("AppID service  : present={0} start={1}" -f `
    $platform.appIdServicePresent, $platform.appIdServiceStartType)
Write-Host ("KidShell svcs  : {0}" -f $kidShellServiceNames)
Write-Host ("ProgramData    : KidShell={0} policy={1}" -f `
    $preflight.kidShell.programDataPresent, $preflight.kidShell.protectedStorePresent)
Write-Host ("Test marker    : {0}" -f $preflight.kidShell.testDeviceMarkerPresent)
Write-Host ''

& (Join-Path $PSScriptRoot '13-check-applocker-capability.ps1') -PlatformFactsObject $platform -Quiet
& (Join-Path $PSScriptRoot '14-check-assigned-access-capability.ps1') -PlatformFactsObject $platform -Quiet

Write-Host ''
Write-Host '--- would the interlock open here? ---' -ForegroundColor Cyan

$factsPath = Join-Path ([System.IO.Path]::GetTempPath()) ("kidshell-preflight-$([guid]::NewGuid().ToString('n')).json")

try {
    # Deliberately with an EMPTY phrase: the preflight reports what stands in
    # the way, and never asks for the confirmation that would remove one of
    # those obstacles.
    $facts | ConvertTo-Json -Depth 5 | Set-Content -Path $factsPath -Encoding utf8

    $arguments = @('interlock', '--facts', $factsPath)
    $resolved = Get-ValidationConfigPath -Path $ConfigPath
    if ($resolved) { $arguments += @('--config', $resolved) }

    (Invoke-ValidationTool -Arguments $arguments).Output | ForEach-Object { Write-Host $_ }
}
finally {
    if (Test-Path $factsPath) { Remove-Item $factsPath -Force }
}

if ($RunPath) {
    Write-Evidence -RunPath $RunPath -Name 'preflight.json' -Data $preflight | Out-Null
    Write-Host ''
    Write-Host "Evidence: $(Join-Path $RunPath 'preflight.json')"
}

Write-Host ''
Write-Host 'Nothing was changed.' -ForegroundColor Green
