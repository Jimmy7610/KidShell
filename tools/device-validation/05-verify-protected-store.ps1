<#
.SYNOPSIS
    Read-only check of the protected store's permissions against the product's
    own plan.

.DESCRIPTION
    STRICTLY READ-ONLY. It reads an access list; it does not set one, and it
    does not create the directory. KidShell never creates it either - a store
    created by KidShell would be owned by whoever ran KidShell, which on a
    locked-down machine could be the child.

    IT RESOLVES EFFECTIVE RIGHTS, NOT ACL TEXT. An access list can read
    correctly and grant something else: an inherited entry nobody noticed, a
    group the child happens to be in, CREATOR OWNER on a directory the child
    created. So each principal's rights are resolved and recorded.

    Even that is inference. The denial is only evidence when the child's own
    session has actually tried and been refused, which is
    07-verify-child-denials.ps1 - and until it has run, every probe here is NOT
    RUN rather than a pass.
#>
[CmdletBinding()]
param(
    [string] $ConfigPath,
    [string] $RunPath,

    # Written by 07-verify-child-denials.ps1, from the child's own session.
    [string] $ProbeResultsPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'KidShellValidation.psm1') -Force

$config = Import-ValidationConfig -Path $ConfigPath
$childSid = ''
if ($config -and $config.ExpectedChildSid) { $childSid = $config.ExpectedChildSid }

$directory = 'C:\ProgramData\KidShell\policy'

$wellKnown = @{
    System         = 'S-1-5-18'
    Administrators = 'S-1-5-32-544'
    Users          = 'S-1-5-32-545'
}

$writeMask = [Security.AccessControl.FileSystemRights]::WriteData -bor
             [Security.AccessControl.FileSystemRights]::AppendData -bor
             [Security.AccessControl.FileSystemRights]::Write -bor
             [Security.AccessControl.FileSystemRights]::Modify -bor
             [Security.AccessControl.FileSystemRights]::FullControl

$deleteMask = [Security.AccessControl.FileSystemRights]::Delete -bor
              [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
              [Security.AccessControl.FileSystemRights]::Modify -bor
              [Security.AccessControl.FileSystemRights]::FullControl

$readMask = [Security.AccessControl.FileSystemRights]::ReadData -bor
            [Security.AccessControl.FileSystemRights]::Read -bor
            [Security.AccessControl.FileSystemRights]::ReadAndExecute -bor
            [Security.AccessControl.FileSystemRights]::Modify -bor
            [Security.AccessControl.FileSystemRights]::FullControl

$changeMask = [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
              [Security.AccessControl.FileSystemRights]::TakeOwnership -bor
              [Security.AccessControl.FileSystemRights]::FullControl

function Get-Rights($access, [string] $sid) {
    <#
        The rights one principal actually has, as the names
        ProtectedStoreRights uses. Allow entries only: a Deny entry removes
        access, so counting it would overstate what the principal can do.
    #>
    $entries = @($access | Where-Object {
        $_.AccessControlType -eq 'Allow' -and (Resolve-Sid $_.IdentityReference) -eq $sid
    })

    if ($entries.Count -eq 0) { return 'None' }

    $rights = 0
    foreach ($entry in $entries) { $rights = $rights -bor [int]$entry.FileSystemRights }

    $names = New-Object System.Collections.Generic.List[string]
    if ($rights -band $readMask) { $names.Add('Read') }
    if ($rights -band $writeMask) { $names.Add('Write') }
    if ($rights -band $deleteMask) { $names.Add('Delete') }
    if ($rights -band $changeMask) { $names.Add('ChangePermissions') }

    if ($names.Count -eq 0) { return 'None' }
    if ($names.Count -eq 4) { return 'FullControl' }

    return ($names -join ', ')
}

function Resolve-Sid($identity) {
    try { return [string]$identity.Translate([Security.Principal.SecurityIdentifier]).Value }
    catch { return [string]$identity.Value }
}

$observation = [pscustomobject]@{
    directory          = $directory
    programDataPath    = 'C:\ProgramData'
    exists             = (Test-Path $directory)
    owner              = ''
    inheritanceRemoved = $false
    effective          = @{}
    accessEntries      = @()
    probes             = @()
}

Write-Host ''
Write-Host '===== Protected store permissions (read-only) =====' -ForegroundColor Cyan
Write-Host ("Directory : {0}" -f $directory)

if ($observation.exists) {
    $acl = Get-Acl $directory

    $observation.owner = [string]$acl.Owner
    $observation.inheritanceRemoved = $acl.AreAccessRulesProtected
    $observation.accessEntries = @($acl.Access | ForEach-Object {
        '{0} {1} {2} (inherited={3})' -f `
            $_.AccessControlType, $_.IdentityReference, $_.FileSystemRights, $_.IsInherited
    })

    $effective = @{}

    foreach ($name in $wellKnown.Keys) {
        $effective[$name] = Get-Rights $acl.Access $wellKnown[$name]
    }

    if ($childSid) {
        $effective['Child'] = Get-Rights $acl.Access $childSid
    }

    $observation.effective = $effective

    Write-Host ("Owner     : {0}" -f $observation.owner)
    Write-Host ("Inherit   : removed={0}" -f $observation.inheritanceRemoved)

    foreach ($name in $effective.Keys) {
        Write-Host ("  {0,-15}: {1}" -f $name, $effective[$name])
    }

    if (-not $childSid) {
        Write-Host '  Child          : not resolved (ExpectedChildSid is not in the config)' -ForegroundColor Yellow
    }
}
else {
    Write-Host 'Exists    : no'
}

if ($ProbeResultsPath -and (Test-Path $ProbeResultsPath)) {
    $observation.probes = @((Get-Content $ProbeResultsPath -Raw | ConvertFrom-Json))
    Write-Host ("Probes    : {0} recorded from the child's session" -f $observation.probes.Count)
}
else {
    Write-Host 'Probes    : none. The denials are inference until 07-verify-child-denials.ps1 has run.' -ForegroundColor Yellow
}

$observedPath = Join-Path ([System.IO.Path]::GetTempPath()) ("kidshell-store-$([guid]::NewGuid().ToString('n')).json")

try {
    $observation | ConvertTo-Json -Depth 8 | Set-Content -Path $observedPath -Encoding utf8

    $result = Invoke-ValidationTool -Arguments @('expect', '--kind', 'store', '--observed', $observedPath)

    $jsonStart = -1
    for ($i = 0; $i -lt $result.Output.Count; $i++) {
        if ($result.Output[$i] -match '^\{') { $jsonStart = $i; break }
    }

    if ($jsonStart -lt 0) {
        $result.Output | ForEach-Object { Write-Host $_ }
        throw 'The decision tool did not produce a stage.'
    }

    $result.Output[0..($jsonStart - 1)] | ForEach-Object { Write-Host $_ }

    if ($RunPath) {
        $stageJson = ($result.Output[$jsonStart..($result.Output.Count - 1)] -join "`n")

        Write-Evidence -RunPath $RunPath -Name 'acl.json' -Data $observation | Out-Null
        Write-Evidence -RunPath $RunPath -Name 'protected-store-acl.stage.json' `
            -Data ($stageJson | ConvertFrom-Json) | Out-Null
    }
}
finally {
    if (Test-Path $observedPath) { Remove-Item $observedPath -Force }
}

Write-Host ''
Write-Host 'Nothing was changed.' -ForegroundColor Green
