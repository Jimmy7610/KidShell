<#
.SYNOPSIS
    Permissions %ProgramData%\KidShell\policy. Interlocked, dry run by default.

.DESCRIPTION
    Read apply\README.md first.

    The plan this applies is KidShell's own, from ProtectedStorePlan:

      SYSTEM                  full control
      BUILTIN\Administrators  full control
      the child's account      read, and only read
      everyone else            nothing
      inherited permissions    removed

    WHY INHERITANCE IS REMOVED RATHER THAN ADDED TO

    ProgramData grants CREATOR OWNER full control of what it creates. A store
    permissioned without breaking inheritance would be owned and fully
    controlled by whoever created it, which on a locked-down machine could be
    the child - a store that looks like protection and is not.

    WHY THE CHILD GETS READ

    KidShell runs AS the child and has to load the policy it is enforcing. Read
    is not a weakness: the PIN is a salted hash, and everything else is the list
    of rules the child is already being shown.

    This script creates the directory as an administrator, deliberately, because
    KidShell never creates it - one created by KidShell would be owned by
    whoever ran KidShell.
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

$directory = 'C:\ProgramData\KidShell\policy'
$config = Import-ValidationConfig -Path $ConfigPath

if (-not $config -or -not $config.ExpectedChildSid) {
    throw 'The config must record ExpectedChildSid before the store can be scoped to the child. Run ..\03-verify-accounts.ps1.'
}

$childSid = $config.ExpectedChildSid

Write-Host ''
Write-Host '===== Permission the protected store =====' -ForegroundColor Cyan
Write-Host ("Directory : {0}" -f $directory)
Write-Host ("Child SID : {0}" -f $childSid)
Write-Host ''
Write-Host 'Plan:'
Write-Host '  SYSTEM                  FullControl'
Write-Host '  BUILTIN\Administrators  FullControl'
Write-Host '  the child               Read'
Write-Host '  everyone else           nothing'
Write-Host '  inheritance             removed'

try {
    $null = New-Object Security.Principal.SecurityIdentifier($childSid)
}
catch {
    Write-Host ''
    Write-Host "REFUSED: '$childSid' is not a SID Windows recognises." -ForegroundColor Red
    exit 1
}

if (-not $Apply) {
    Write-Host ''
    Write-Host 'DRY RUN. No directory was created and no permission was changed.' -ForegroundColor Yellow
    Write-Host 'With -Apply this would create the directory if absent, remove inherited'
    Write-Host 'permissions, and set exactly the three entries above.'
    Write-Host ''
    return
}

Assert-DedicatedDevice -ConfigPath $ConfigPath -NonInteractive:$NonInteractive

if (-not (Test-Path $directory)) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    Write-Host ''
    Write-Host 'Directory created.' -ForegroundColor Yellow
}

$acl = Get-Acl $directory

# Inheritance removed and inherited entries DISCARDED rather than copied.
# Copying them would keep exactly the ProgramData entries the plan exists to
# get rid of.
$acl.SetAccessRuleProtection($true, $false)

foreach ($rule in @($acl.Access)) {
    $null = $acl.RemoveAccessRule($rule)
}

$inherit = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor `
           [Security.AccessControl.InheritanceFlags]::ObjectInherit

function New-Rule([string] $sid, $rights) {
    New-Object Security.AccessControl.FileSystemAccessRule(
        (New-Object Security.Principal.SecurityIdentifier($sid)),
        $rights,
        $inherit,
        [Security.AccessControl.PropagationFlags]::None,
        [Security.AccessControl.AccessControlType]::Allow)
}

# From the one table, rather than two more SID literals written out here. These
# were already SIDs and already correct; they come from the shared table now so
# that there is exactly one place where a built-in group is defined.
$groupSids = Get-WellKnownGroupSid -Set all

$acl.AddAccessRule((New-Rule $groupSids['System'] ([Security.AccessControl.FileSystemRights]::FullControl)))
$acl.AddAccessRule((New-Rule $groupSids['Administrators'] ([Security.AccessControl.FileSystemRights]::FullControl)))
$acl.AddAccessRule((New-Rule $childSid ([Security.AccessControl.FileSystemRights]::ReadAndExecute)))

# Owner set to Administrators, so the parent can always repair it.
$acl.SetOwner((New-Object Security.Principal.SecurityIdentifier($groupSids['Administrators'])))

Set-Acl -Path $directory -AclObject $acl

Write-Host ''
Write-Host 'Applied. Resulting access list:' -ForegroundColor Green

(Get-Acl $directory).Access | ForEach-Object {
    Write-Host ("  {0} {1} {2} (inherited={3})" -f `
        $_.AccessControlType, $_.IdentityReference, $_.FileSystemRights, $_.IsInherited)
}

Write-Host ''
Write-Host 'Now run, in this order:' -ForegroundColor Yellow
Write-Host '  ..\05-verify-protected-store.ps1'
Write-Host '  then sign in as the child and run ..\07-verify-child-denials.ps1 -OutFile <path>'
Write-Host '  then, as the administrator, ..\05-verify-protected-store.ps1 -ProbeResultsPath <that path>'
