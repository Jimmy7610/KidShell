<#
.SYNOPSIS
    Brings the standard account KidShell runs as into the state it must be in.
    Interlocked, dry run by default, and safe to run again.

.DESCRIPTION
    Read apply\README.md first.

    IT IS A REPAIR, NOT JUST A CREATE. On WILMA this script created the child
    account and then failed to add it to the built-in Users group, because
    Add-LocalGroupMember's -Member parameter is typed LocalPrincipal[] and the
    script passed a SecurityIdentifier object, which has no conversion to it. It
    printed that failure as a yellow note, printed "Created 'KidShellChild'." in
    green, and exited 0.

    Then its first statement made that unrecoverable:

        if ($existing) { Write-Host 'already exists'; return }

    So re-running it after the fix would have reported the half-built account as
    fine. The only ways out were editing the machine by hand or deleting the
    child account - which on a real device means destroying a profile.

    Now: it observes, the decision tool decides, it acts, and then it reads the
    result back out of Windows. It never reports a stage as done because a cmdlet
    did not throw.

    WHAT IT WILL REPAIR is exactly the state it creates: the standard Users
    membership. It will not enable a disabled account, will not remove privilege,
    and will not reconcile a SID that does not match the config - each of those
    is a decision about a machine whose history this script does not know, and it
    says so instead.

    THE RULE THAT OUTRANKS EVERYTHING is checked first, on every path including
    a no-op: a second enabled administrator must already exist.

.NOTES
    Exit codes:
      0  SUCCESS   everything this script owns is in place
      1  REFUSED   deliberately did nothing, and said why
      4  PARTIAL   the account exists and something it needs does not; re-run
      5  FAILED    an attempt was made and did not work
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

$config = Import-ValidationConfig -Path $ConfigPath

if (-not $config -or -not $config.ChildUser) {
    throw 'The validation config does not name a ChildUser.'
}

Write-Host ''
Write-Host '===== The child account =====' -ForegroundColor Cyan

# ------------------------------------------------------------- 1. OBSERVE
#
# Every built-in group by SID. The display names are localized - on the Swedish
# validation machine Administrators is "Administratörer" - so nothing here is
# looked up by name.
$groupSids = Get-WellKnownGroupSid -Set all
$administratorsSid = $groupSids['Administrators']
$usersSid = $groupSids['Users']

$child = Get-LocalUser -Name $config.ChildUser -ErrorAction SilentlyContinue
$childSid = if ($child) { [string]$child.SID.Value } else { '' }

$expectedSid = ''
if ($config.ExpectedChildSid) { $expectedSid = [string]$config.ExpectedChildSid }

# The recovery administrator, by SID, excluding the child itself: an account that
# is both the child and the only administrator is not a recovery path.
$administrators = Get-LocalGroupMemberSid -GroupSid $administratorsSid
$recoveryAdmins = @()

if ($administrators.Exists -and $administrators.Inspected) {
    foreach ($memberSid in $administrators.MemberSids) {
        if ($childSid -and $memberSid -eq $childSid) { continue }

        $admin = Get-LocalUser -SID $memberSid -ErrorAction SilentlyContinue

        if ($admin -and $admin.Enabled) { $recoveryAdmins += $admin.Name }
    }
}
elseif (-not $administrators.Exists) {
    Write-Host "REFUSED: the Administrators group ($administratorsSid) does not exist on this machine." -ForegroundColor Red
    exit 1
}
else {
    Write-Host "REFUSED: the Administrators group ($administratorsSid) could not be enumerated: $($administrators.Error)" -ForegroundColor Red
    Write-Host 'The recovery path cannot be confirmed, so nothing will be changed.' -ForegroundColor Red
    exit 1
}

Write-Host ("Recovery administrators : {0}" -f $(if ($recoveryAdmins.Count -gt 0) { $recoveryAdmins -join ', ' } else { '<none>' }))

# The child's own state. Tri-state on purpose: 'unknown' is a real answer and
# must not arrive at the decision as 'false'.
$inUsers = 'unknown'
$privilegedFound = @()
$unreadable = @()

if ($child) {
    $membership = Test-LocalGroupMembership -GroupSid $usersSid -MemberSid $childSid

    if ($null -ne $membership) { $inUsers = ([string]$membership).ToLowerInvariant() }

    foreach ($label in (Get-WellKnownGroupSid -Set privileged).Keys) {
        $sid = $groupSids[$label]
        $group = Get-LocalGroupMemberSid -GroupSid $sid

        if (-not $group.Exists) { continue }
        if (-not $group.Inspected) { $unreadable += $sid; continue }

        if ($group.MemberSids -contains $childSid) { $privilegedFound += $sid }
    }

    Write-Host ("Account                 : '{0}' exists, enabled={1}" -f $child.Name, $child.Enabled)
    Write-Host ("SID                     : {0}" -f $childSid)
    Write-Host ("In Users ({0}) : {1}" -f $usersSid, $inUsers)
}
else {
    Write-Host ("Account                 : '{0}' does not exist" -f $config.ChildUser)
}

# ---------------------------------------------------------------- 2. DECIDE
#
# Asked, not worked out here. The decision has eight states to get right and
# lives in KidShell.Core where every one of them is covered by a test.
# Built up as plain variables first. An `if` used as an expression inside an
# array literal is not valid on Windows PowerShell 5.1, which is what runs on the
# validation machines.
$existsFlag = ([string][bool]$child).ToLowerInvariant()
$enabledFlag = 'false'
if ($child -and $child.Enabled) { $enabledFlag = 'true' }
$recoveryFlag = ([string]($recoveryAdmins.Count -gt 0)).ToLowerInvariant()

# Flags with nothing to say are left out rather than passed as empty strings: an
# empty element cannot bind to a mandatory [string[]], and the tool already
# treats an absent flag as "not observed". Found by running this, not by reading
# it.
$planArgs = @('child-account-plan', '--name', $config.ChildUser)

if ($childSid) { $planArgs += @('--sid', $childSid) }
if ($expectedSid) { $planArgs += @('--expected-sid', $expectedSid) }

$planArgs += @('--exists', $existsFlag, '--enabled', $enabledFlag, '--in-users', $inUsers)

if ($privilegedFound.Count -gt 0) { $planArgs += @('--privileged', ($privilegedFound -join ',')) }
if ($unreadable.Count -gt 0) { $planArgs += @('--unreadable', ($unreadable -join ',')) }

$planArgs += @('--recovery-admin', $recoveryFlag)

$plan = Invoke-ValidationTool -Arguments $planArgs

$decision = @{}
foreach ($line in $plan.Output) {
    if ($line -is [string] -and $line -match '^([A-Z_]+)=(.*)$') {
        if ($Matches[1] -eq 'REASON') { continue }
        $decision[$Matches[1]] = $Matches[2]
    }
}

Write-Host ''
foreach ($line in $plan.Output) {
    if ($line -is [string] -and $line -match '^REASON=(.*)$') {
        Write-Host ("  - {0}" -f $Matches[1])
    }
}

if (-not $decision.ContainsKey('ACTION')) {
    Write-Host ''
    Write-Host 'FAILED: the decision tool returned no plan, so nothing will be changed.' -ForegroundColor Red
    $plan.Output | ForEach-Object { Write-Host "  $_" }
    exit 5
}

$action = $decision['ACTION']
$addUsers = ($decision['ADD_USERS'] -eq 'True')
$needsPassword = ($decision['NEEDS_PASSWORD'] -eq 'True')

Write-Host ''
Write-Host ("PLAN: {0}" -f $action) -ForegroundColor Cyan

if ($action -eq 'Refuse') {
    Write-Host ''
    Write-Host 'REFUSED. Nothing was changed.' -ForegroundColor Red
    exit 1
}

if ($action -eq 'None') {
    Write-Host ''
    Write-Host 'SUCCESS. The child account is already exactly what it must be; nothing was changed.' -ForegroundColor Green
    exit 0
}

if (-not $Apply) {
    Write-Host ''
    Write-Host 'DRY RUN. Nothing was changed.' -ForegroundColor Yellow

    if ($action -eq 'Create') {
        Write-Host ("With -Apply this would create the local account '{0}':" -f $config.ChildUser)
        Write-Host '  enabled, password never expires, NOT in any privileged group.'
    }
    else {
        Write-Host ("With -Apply this would repair '{0}' and change nothing else:" -f $config.ChildUser)
    }

    if ($addUsers) {
        Write-Host ("  add it to the standard users group {0}, targeted by SID, then read the" -f $usersSid)
        Write-Host '  membership back to confirm it.'
    }

    Write-Host ''
    exit 0
}

# ------------------------------------------------------------------ 3. ACT

Assert-DedicatedDevice -ConfigPath $ConfigPath -NonInteractive:$NonInteractive

$created = $false

# Declared before use: Set-StrictMode makes reading an unset variable a
# terminating error, and this one is only assigned on one branch.
$password = $null

if ($action -eq 'Create') {
    if (-not $needsPassword) {
        # Creating without one would make an account with a blank password.
        Write-Host ''
        Write-Host 'FAILED: the plan says to create the account but not to ask for a password.' -ForegroundColor Red
        exit 5
    }

    Write-Host ''
    Write-Host 'The child account needs a password. It is read without echo, is never written' -ForegroundColor Yellow
    Write-Host 'to a file, and does not appear in any evidence.' -ForegroundColor Yellow

    $password = Read-Host -AsSecureString 'Password for the child account'

    $child = New-LocalUser -Name $config.ChildUser `
        -Password $password `
        -FullName $config.ChildUser `
        -Description 'KidShell validation child account' `
        -PasswordNeverExpires `
        -ErrorAction Stop

    $childSid = [string]$child.SID.Value
    $created = $true

    Write-Host ''
    Write-Host ("Created '{0}' with SID {1}." -f $child.Name, $childSid) -ForegroundColor Green
}

# New-LocalUser adds the account to no group at all, so the standard membership
# is added explicitly - and verified. This is the step that failed on WILMA.
if ($addUsers) {
    $result = Add-LocalGroupMemberBySid -GroupSid $usersSid -MemberSid $childSid

    foreach ($attempt in $result.Attempts) {
        Write-Host ("  {0}" -f $attempt) -ForegroundColor DarkGray
    }

    if (-not $result.Verified) {
        Write-Host ''
        Write-Host ("The account is NOT in the standard users group {0}." -f $usersSid) -ForegroundColor Red
        Write-Host ("  {0}" -f $result.Error) -ForegroundColor Red
        Write-Host ''

        if ($created) {
            # The WILMA outcome, now reported as what it is. A green "Created"
            # here is what cost a physical validation run.
            Write-Host 'PARTIAL STATE. The account was created and is NOT fully configured.' -ForegroundColor Red
            Write-Host ("  '{0}' exists with SID {1}." -f $config.ChildUser, $childSid) -ForegroundColor Red
            Write-Host '  Do NOT delete it. Run this script again once the cause is fixed; it will' -ForegroundColor Red
            Write-Host '  detect the missing membership and repair only that.' -ForegroundColor Red
            Write-Host ''
            Write-Host 'Part 3 is NOT complete.' -ForegroundColor Red
            exit 4
        }

        Write-Host 'FAILED. The membership could not be repaired, and nothing else was changed.' -ForegroundColor Red
        exit 5
    }

    Write-Host ("Verified: {0} is in the standard users group {1}." -f $childSid, $usersSid) -ForegroundColor Green
}

# --------------------------------------------------------------- 4. RE-OBSERVE
#
# Read the end state out of Windows rather than assuming the steps above added
# up to it.
$final = Get-LocalUser -SID $childSid -ErrorAction SilentlyContinue
$finalInUsers = if ($final) { Test-LocalGroupMembership -GroupSid $usersSid -MemberSid $childSid } else { $null }

if (-not $final -or $finalInUsers -ne $true) {
    Write-Host ''
    Write-Host 'PARTIAL STATE. The end state could not be confirmed after the change.' -ForegroundColor Red
    Write-Host ("  account present: {0}; in {1}: {2}" -f [bool]$final, $usersSid, $finalInUsers) -ForegroundColor Red
    Write-Host '  Run this script again; it will repair only what is missing.' -ForegroundColor Red
    exit 4
}

Write-Host ''
Write-Host ("SUCCESS. '{0}' is an enabled standard user in {1}." -f $final.Name, $usersSid) -ForegroundColor Green
Write-Host ("SID: {0}" -f $childSid) -ForegroundColor Green
Write-Host ''
Write-Host 'Now:' -ForegroundColor Yellow
Write-Host ("  1. Record that SID as ExpectedChildSid in {0}" -f (Get-ValidationConfigPath -Path $ConfigPath))
Write-Host '  2. Sign in as the account once, so its registry hive is created.'
Write-Host '  3. Run ..\03-verify-accounts.ps1'

exit 0
