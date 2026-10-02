<#
.SYNOPSIS
    Creates the standard account KidShell will run as. Interlocked, dry run by
    default.

.DESCRIPTION
    Read apply\README.md first.

    It creates ONE local account, enabled, with no privileged group membership,
    and prints its SID so the config can record it. It does not touch the
    administrator account, does not change any group except by not adding to
    one, and does not sign anybody out.

    THE RULE THAT OUTRANKS EVERYTHING is checked here too, before anything is
    created: a second enabled administrator must already exist. An account
    created on a machine with no recovery administrator is the first step
    towards a computer nobody can sign in to.
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
Write-Host '===== Create the child account =====' -ForegroundColor Cyan

$existing = Get-LocalUser -Name $config.ChildUser -ErrorAction SilentlyContinue

if ($existing) {
    Write-Host ("'{0}' already exists. SID: {1}" -f $existing.Name, $existing.SID) -ForegroundColor Green
    Write-Host 'Record that SID as ExpectedChildSid in the config.'
    return
}

# Before anything, and regardless of -Apply.
$administrators = @(Get-LocalGroupMember -Group 'Administrators' -ErrorAction SilentlyContinue)
$enabledAdmins = @()

foreach ($member in $administrators) {
    $name = ($member.Name -split '\\')[-1]
    $user = Get-LocalUser -Name $name -ErrorAction SilentlyContinue

    if ($user -and $user.Enabled) { $enabledAdmins += $user.Name }
}

Write-Host ("Enabled administrators: {0}" -f ($enabledAdmins -join ', '))

if ($enabledAdmins.Count -lt 1) {
    Write-Host 'REFUSED: no enabled administrator account was found.' -ForegroundColor Red
    Write-Host 'A second enabled administrator must exist at every moment.' -ForegroundColor Red
    exit 1
}

if (-not $Apply) {
    Write-Host ''
    Write-Host 'DRY RUN. Nothing was created.' -ForegroundColor Yellow
    Write-Host ("With -Apply this would create the local account '{0}':" -f $config.ChildUser)
    Write-Host '  enabled, password never expires, NOT in any privileged group.'
    Write-Host ''
    return
}

Assert-DedicatedDevice -ConfigPath $ConfigPath -NonInteractive:$NonInteractive

Write-Host ''
Write-Host 'The child account needs a password. It is read without echo, is never written' -ForegroundColor Yellow
Write-Host 'to a file, and does not appear in any evidence.' -ForegroundColor Yellow

$password = Read-Host -AsSecureString 'Password for the child account'

$created = New-LocalUser -Name $config.ChildUser `
    -Password $password `
    -FullName $config.ChildUser `
    -Description 'KidShell validation child account' `
    -PasswordNeverExpires `
    -ErrorAction Stop

# Deliberately NOT added to any group. New-LocalUser does not add to Users on
# its own in every edition, so it is added explicitly and nothing else is.
try {
    Add-LocalGroupMember -Group 'Users' -Member $created.Name -ErrorAction Stop
}
catch {
    Write-Host "Note: could not add to Users ($($_.Exception.Message)). Check the membership by hand." -ForegroundColor Yellow
}

Write-Host ''
Write-Host ("Created '{0}'." -f $created.Name) -ForegroundColor Green
Write-Host ("SID: {0}" -f $created.SID) -ForegroundColor Green
Write-Host ''
Write-Host 'Now:' -ForegroundColor Yellow
Write-Host ("  1. Record that SID as ExpectedChildSid in {0}" -f (Get-ValidationConfigPath -Path $ConfigPath))
Write-Host '  2. Sign in as the account once, so its registry hive is created.'
Write-Host '  3. Run ..\03-verify-accounts.ps1'
