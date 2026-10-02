<#
.SYNOPSIS
    Read-only check that the parent and child accounts are what the config says.

.DESCRIPTION
    STRICTLY READ-ONLY. It creates no account, changes no group membership and
    sets no password. Account creation lives under apply/ behind the interlock.

    The check that matters most is the dullest one: the two accounts must not be
    the same account. The entire architecture is the difference between them, so
    a run where they resolve to one SID would see every denial pass for the
    wrong reason and report a clean validation of nothing.
#>
[CmdletBinding()]
param(
    [string] $ConfigPath,
    [string] $RunPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'KidShellValidation.psm1') -Force

$config = Import-ValidationConfig -Path $ConfigPath
$stage = New-ValidationStage -Name 'CHILD AUTHORIZATION'

Write-Host ''
Write-Host '===== Accounts (read-only) =====' -ForegroundColor Cyan

if (-not $config) {
    Write-Host 'No validation config, so there is nothing to compare the accounts against.'
    Add-ValidationFinding -Stage $stage -Status NotRun `
        -Detail 'No validation config, so the parent and child accounts were not checked.'

    if ($RunPath) {
        Write-Evidence -RunPath $RunPath -Name 'accounts.json' -Data @{ checked = $false } | Out-Null
        Save-ValidationStage -RunPath $RunPath -Stage $stage
    }

    return
}

function Get-Account([string] $name) {
    try { return Get-LocalUser -Name $name -ErrorAction Stop }
    catch { return $null }
}

$privilegedGroups = @(
    'Administrators', 'Power Users', 'Backup Operators',
    'Remote Desktop Users', 'Remote Management Users', 'Hyper-V Administrators'
)

function Get-GroupMembership([string] $sid) {
    $found = New-Object System.Collections.Generic.List[string]

    foreach ($group in $privilegedGroups) {
        try {
            $members = Get-LocalGroupMember -Group $group -ErrorAction Stop

            if ($members | Where-Object { [string]$_.SID -eq $sid }) {
                $found.Add($group)
            }
        }
        catch { }
    }

    return $found.ToArray()
}

$parent = Get-Account $config.ParentAdminUser
$child = Get-Account $config.ChildUser

$evidence = [pscustomobject]@{
    checked = $true
    parent  = [pscustomobject]@{
        name    = $config.ParentAdminUser
        exists  = [bool]$parent
        enabled = $false
        sid     = ''
        groups  = @()
    }
    child   = [pscustomobject]@{
        name            = $config.ChildUser
        exists          = [bool]$child
        enabled         = $false
        sid             = ''
        groups          = @()
        sidMatchesConfig = $false
    }
}

if ($parent) {
    $evidence.parent.enabled = [bool]$parent.Enabled
    $evidence.parent.sid = [string]$parent.SID
    $evidence.parent.groups = @(Get-GroupMembership ([string]$parent.SID))
}

if ($child) {
    $evidence.child.enabled = [bool]$child.Enabled
    $evidence.child.sid = [string]$child.SID
    $evidence.child.groups = @(Get-GroupMembership ([string]$child.SID))
    $evidence.child.sidMatchesConfig =
        ($config.ExpectedChildSid -and [string]$child.SID -eq $config.ExpectedChildSid)
}

# ------------------------------------------------------------- the parent

if (-not $parent) {
    Add-ValidationFinding -Stage $stage -Status Fail `
        -Detail "The parent administrator account '$($config.ParentAdminUser)' does not exist."
}
else {
    if (-not $parent.Enabled) {
        Add-ValidationFinding -Stage $stage -Status Fail `
            -Detail "The parent administrator account '$($parent.Name)' is disabled. A second enabled administrator must exist at every moment."
    }

    if ('Administrators' -notin $evidence.parent.groups) {
        Add-ValidationFinding -Stage $stage -Status Fail `
            -Detail "The parent account '$($parent.Name)' is not in Administrators, so there is no recovery path."
    }
    else {
        Add-ValidationFinding -Stage $stage -Status Pass `
            -Detail "The parent account '$($parent.Name)' is an enabled administrator."
    }
}

# -------------------------------------------------------------- the child

if (-not $child) {
    Add-ValidationFinding -Stage $stage -Status NotRun `
        -Detail "The child account '$($config.ChildUser)' does not exist yet, so nothing was checked against it."
}
else {
    if (-not $child.Enabled) {
        Add-ValidationFinding -Stage $stage -Status Fail -Detail "The child account '$($child.Name)' is disabled."
    }

    $privileged = @($evidence.child.groups)

    if ($privileged.Count -gt 0) {
        Add-ValidationFinding -Stage $stage -Status Fail `
            -Detail "The child account is in: $($privileged -join ', '). It must be a plain standard user."
    }
    else {
        Add-ValidationFinding -Stage $stage -Status Pass `
            -Detail "The child account '$($child.Name)' is an enabled standard user in no privileged group."
    }

    if (-not $config.ExpectedChildSid) {
        Add-ValidationFinding -Stage $stage -Status NotRun `
            -Detail "ExpectedChildSid is not recorded in the config, so the SID the access lists are scoped to was not confirmed. Record $([string]$child.SID)."
    }
    elseif (-not $evidence.child.sidMatchesConfig) {
        # An account deleted and recreated with the same NAME has a different
        # SID, and every permission scoped to the old one now refers to nobody.
        Add-ValidationFinding -Stage $stage -Status Fail `
            -Detail "The child account's SID is $([string]$child.SID) and the config expects $($config.ExpectedChildSid). The access lists are scoped to the wrong account."
    }
    else {
        Add-ValidationFinding -Stage $stage -Status Pass -Detail 'The child SID matches the one recorded in the config.'
    }
}

# ------------------------------------------------- and the one that matters

if ($parent -and $child) {
    if ([string]$parent.SID -eq [string]$child.SID) {
        Add-ValidationFinding -Stage $stage -Status Fail `
            -Detail 'The parent and the child resolve to the SAME account. Every denial this validation checks would pass for the wrong reason.'
    }
    else {
        Add-ValidationFinding -Stage $stage -Status Pass -Detail 'The parent and the child are different accounts.'
    }
}

Write-Host ("Parent : {0} exists={1} enabled={2} groups={3}" -f `
    $evidence.parent.name, $evidence.parent.exists, $evidence.parent.enabled,
    (($evidence.parent.groups -join ',') -replace '^$', 'none'))
Write-Host ("Child  : {0} exists={1} enabled={2} groups={3}" -f `
    $evidence.child.name, $evidence.child.exists, $evidence.child.enabled,
    (($evidence.child.groups -join ',') -replace '^$', 'none'))

if ($RunPath) {
    Write-Evidence -RunPath $RunPath -Name 'accounts.json' -Data $evidence | Out-Null
    Save-ValidationStage -RunPath $RunPath -Stage $stage
}

Write-Host ''
Write-Host 'Nothing was changed.' -ForegroundColor Green
