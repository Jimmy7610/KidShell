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

# The privileged groups, by SID, from the one table in KidShell.Core.
#
# These used to be six English strings. Windows localizes built-in group names,
# so on the Swedish validation machine every one of those lookups threw
# GroupNotFoundException, the catch swallowed it, and the child was reported as
# being in no privileged group because no group could be found at all.
#
# The labels below are for reading. The SID is what is asked for.
$privilegedGroups = Get-WellKnownGroupSid -Set privileged
$administratorsSid = (Get-WellKnownGroupSid -Set all)['Administrators']

function Get-GroupMembership([string] $sid) {
    <#
        Which privileged groups this account is in, as canonical labels, plus
        any group that could not be read at all.

        Absence is not failure: Windows Home has no Power Users, Backup Operators
        or Remote Desktop Users, and an account provably is not in a group the
        machine does not have. A group that EXISTS and will not enumerate is a
        different matter and is reported, because "I could not look" must never
        be presented as "there is nothing there".
    #>
    $found = New-Object System.Collections.Generic.List[string]
    $unreadable = New-Object System.Collections.Generic.List[string]
    $absent = New-Object System.Collections.Generic.List[string]

    foreach ($label in $privilegedGroups.Keys) {
        $membership = Get-LocalGroupMemberSid -GroupSid $privilegedGroups[$label]

        if (-not $membership.Exists) { $absent.Add($label); continue }
        if (-not $membership.Inspected) { $unreadable.Add($label); continue }

        if ($membership.MemberSids -contains $sid) { $found.Add($label) }
    }

    return [pscustomobject]@{
        Groups     = $found.ToArray()
        Unreadable = $unreadable.ToArray()
        Absent     = $absent.ToArray()
    }
}

$parent = Get-Account $config.ParentAdminUser
$child = Get-Account $config.ChildUser

$evidence = [pscustomobject]@{
    checked = $true

    # The mapping this run actually used. Written down so a run on Swedish
    # Windows and a run on English Windows produce the same document, and so a
    # reader can see that the decision was made from SIDs.
    privilegedGroupSids = $privilegedGroups

    parent  = [pscustomobject]@{
        name    = $config.ParentAdminUser
        exists  = [bool]$parent
        enabled = $false
        sid     = ''
        groups  = @()
        groupsUnreadable = @()
        groupsAbsent     = @()
        inAdministrators = $null
    }
    child   = [pscustomobject]@{
        name            = $config.ChildUser
        exists          = [bool]$child
        enabled         = $false
        sid             = ''
        groups          = @()
        groupsUnreadable = @()
        groupsAbsent     = @()
        sidMatchesConfig = $false
    }
}

if ($parent) {
    $membership = Get-GroupMembership ([string]$parent.SID)

    $evidence.parent.enabled = [bool]$parent.Enabled
    $evidence.parent.sid = [string]$parent.SID
    $evidence.parent.groups = @($membership.Groups)
    $evidence.parent.groupsUnreadable = @($membership.Unreadable)
    $evidence.parent.groupsAbsent = @($membership.Absent)

    # Asked of the Administrators group by SID, not inferred from the label list
    # above. The recovery path is the single most important fact in this stage,
    # so it is established directly and it may come back $null.
    $evidence.parent.inAdministrators =
        Test-LocalGroupMembership -GroupSid $administratorsSid -MemberSid ([string]$parent.SID)
}

if ($child) {
    $membership = Get-GroupMembership ([string]$child.SID)

    $evidence.child.enabled = [bool]$child.Enabled
    $evidence.child.sid = [string]$child.SID
    $evidence.child.groups = @($membership.Groups)
    $evidence.child.groupsUnreadable = @($membership.Unreadable)
    $evidence.child.groupsAbsent = @($membership.Absent)
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

    # By SID. The string 'Administrators' appears nowhere in this decision,
    # because on this machine the group is called Administratörer.
    if ($null -eq $evidence.parent.inAdministrators) {
        Add-ValidationFinding -Stage $stage -Status Fail `
            -Detail "The Administrators group ($administratorsSid) could not be read, so the parent's recovery path could not be confirmed. An unconfirmed recovery path is treated as no recovery path."
    }
    elseif (-not $evidence.parent.inAdministrators) {
        Add-ValidationFinding -Stage $stage -Status Fail `
            -Detail "The parent account '$($parent.Name)' is not in Administrators ($administratorsSid), so there is no recovery path."
    }
    else {
        Add-ValidationFinding -Stage $stage -Status Pass `
            -Detail "The parent account '$($parent.Name)' is an enabled administrator (member of $administratorsSid)."
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
    $unreadable = @($evidence.child.groupsUnreadable)

    if ($privileged.Count -gt 0) {
        Add-ValidationFinding -Stage $stage -Status Fail `
            -Detail "The child account is in: $($privileged -join ', '). It must be a plain standard user."
    }
    elseif ($unreadable.Count -gt 0) {
        # Not a pass. The question "is the child privileged" was not answered for
        # these groups, and the whole point of this pass is that an unanswered
        # security question must not read as a clean one.
        Add-ValidationFinding -Stage $stage -Status Fail `
            -Detail "These privileged groups exist on this machine and could not be enumerated: $($unreadable -join ', '). The child's privileges are unknown, not clean."
    }
    else {
        $absent = @($evidence.child.groupsAbsent)
        $note = ''

        # Windows Home has no Power Users, Backup Operators or Remote Desktop
        # Users. Saying so is honest, and stops a reader wondering whether the
        # check silently skipped them.
        if ($absent.Count -gt 0) {
            $note = " Not present on this edition, so membership is impossible: $($absent -join ', ')."
        }

        # Enabled-ness is the finding above, and saying "enabled" here as well
        # produced a Pass that contradicted the Fail two lines up.
        Add-ValidationFinding -Stage $stage -Status Pass `
            -Detail "The child account '$($child.Name)' is in no privileged group.$note"
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
