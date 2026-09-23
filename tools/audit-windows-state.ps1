<#
.SYNOPSIS
    Read-only snapshot of every Windows security setting KidShell could touch.

.DESCRIPTION
    STRICTLY READ-ONLY. Get-* and registry reads only. There is no Set-, New-,
    Remove-, Enable- or Disable- cmdlet anywhere in this file, and that is the
    point: the tool used to prove a machine is unchanged must not be capable of
    changing it.

    Used two ways:

      * On a development machine, run it before and after a session. The two
        outputs must be byte-identical, which is how this project has verified
        at every milestone that it changed nothing.

      * On a dedicated test device, run it before and after applying security.
        Every difference must correspond to a line in the recovery manifest.
        See docs/DEDICATED-DEVICE-VALIDATION.md.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File toolsudit-windows-state.ps1 -Out before.txt

.EXAMPLE
    Compare-Object (Get-Content before.txt) (Get-Content after.txt)
#>
param([Parameter(Mandatory = $true)][string]$Out)

$ErrorActionPreference = 'SilentlyContinue'
$lines = New-Object System.Collections.Generic.List[string]

function Emit([string]$text) { $lines.Add($text) }

function Reg([string]$path, [string]$name) {
    $v = (Get-ItemProperty -Path $path -Name $name -ErrorAction SilentlyContinue).$name
    if ($null -eq $v) { return '<absent>' }
    return [string]$v
}

Emit '===== WINDOWS IDENTITY ====='
$cv = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
Emit ("ProductName        : " + (Reg $cv 'ProductName'))
Emit ("EditionID          : " + (Reg $cv 'EditionID'))
Emit ("CompositionEdition : " + (Reg $cv 'CompositionEditionID'))
Emit ("DisplayVersion     : " + (Reg $cv 'DisplayVersion'))
Emit ("CurrentBuild       : " + (Reg $cv 'CurrentBuild'))
Emit ("UBR                : " + (Reg $cv 'UBR'))
Emit ("InstallationType   : " + (Reg $cv 'InstallationType'))

Emit ''
Emit '===== UAC ====='
$sys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
foreach ($n in 'EnableLUA', 'ConsentPromptBehaviorAdmin', 'ConsentPromptBehaviorUser',
    'PromptOnSecureDesktop', 'EnableInstallerDetection', 'FilterAdministratorToken') {
    Emit ("{0,-30}: {1}" -f $n, (Reg $sys $n))
}

Emit ''
Emit '===== WINLOGON ====='
$wl = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
foreach ($n in 'Shell', 'Userinit', 'AutoAdminLogon', 'DefaultUserName', 'LegalNoticeCaption') {
    Emit ("{0,-30}: {1}" -f $n, (Reg $wl $n))
}

Emit ''
Emit '===== TASK MANAGER / LOCKDOWN POLICY ====='
Emit ("HKLM DisableTaskMgr           : " + (Reg $sys 'DisableTaskMgr'))
Emit ("HKCU DisableTaskMgr           : " + (Reg 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\System' 'DisableTaskMgr'))
Emit ("HKCU DisableRegistryTools     : " + (Reg 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\System' 'DisableRegistryTools'))
Emit ("HKCU NoControlPanel           : " + (Reg 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer' 'NoControlPanel'))
Emit ("HKCU NoRun                    : " + (Reg 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer' 'NoRun'))
Emit ("HKLM SettingsPageVisibility   : " + (Reg 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer' 'SettingsPageVisibility'))

Emit ''
Emit '===== GROUP POLICY TREES (presence only) ====='
foreach ($p in 'HKLM:\SOFTWARE\Policies\Microsoft\Windows',
    'HKLM:\SOFTWARE\Policies\Microsoft\Edge',
    'HKLM:\SOFTWARE\Policies\Microsoft\Windows\SrpV2',
    'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System',
    'HKCU:\SOFTWARE\Policies\Microsoft\Windows') {
    $exists = Test-Path $p
    $children = if ($exists) { (Get-ChildItem $p -ErrorAction SilentlyContinue | Measure-Object).Count } else { 0 }
    Emit ("{0,-55}: exists={1} childKeys={2}" -f $p, $exists, $children)
}

Emit ''
Emit '===== APPLOCKER ====='
$srp = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\SrpV2'
Emit ("SrpV2 key exists              : " + (Test-Path $srp))
foreach ($c in 'Appx', 'Dll', 'Exe', 'Msi', 'Script') {
    $k = Join-Path $srp $c
    $n = if (Test-Path $k) { (Get-ChildItem $k -ErrorAction SilentlyContinue | Measure-Object).Count } else { 0 }
    Emit ("  rule collection {0,-8}: exists={1} rules={2}" -f $c, (Test-Path $k), $n)
}
$appId = Get-Service -Name AppIDSvc -ErrorAction SilentlyContinue
Emit ("AppIDSvc status/start         : " + $(if ($appId) { "$($appId.Status)/$($appId.StartType)" } else { '<absent>' }))
$effective = Get-AppLockerPolicy -Effective -ErrorAction SilentlyContinue
if ($effective) {
    $xml = $effective.ToXml()
    Emit ("Effective policy rule count   : " + ([regex]::Matches($xml, '<FilePathRule|<FilePublisherRule|<FileHashRule')).Count)
} else {
    Emit 'Effective policy              : <none / cmdlet unavailable>'
}

Emit ''
Emit '===== ASSIGNED ACCESS / KIOSK ====='
foreach ($p in 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AssignedAccessConfiguration',
    'HKLM:\SOFTWARE\Microsoft\Windows\AssignedAccessConfiguration',
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI\AssignedAccess') {
    Emit ("{0,-70}: exists={1}" -f $p, (Test-Path $p))
}
$aa = Get-Command Get-AssignedAccess -ErrorAction SilentlyContinue
if ($aa) {
    $cfg = Get-AssignedAccess -ErrorAction SilentlyContinue
    Emit ("Get-AssignedAccess            : " + $(if ($cfg) { ($cfg | Out-String).Trim() } else { '<not configured>' }))
} else {
    Emit 'Get-AssignedAccess            : <cmdlet unavailable on this edition>'
}

Emit ''
Emit '===== LOCAL USERS ====='
$users = Get-LocalUser -ErrorAction SilentlyContinue | Sort-Object Name
if ($users) {
    foreach ($u in $users) {
        Emit ("{0,-24} enabled={1,-5} sid={2}" -f $u.Name, $u.Enabled, $u.SID.Value)
    }
    Emit ("TOTAL LOCAL USERS             : " + ($users | Measure-Object).Count)
} else {
    Emit '<Get-LocalUser unavailable>'
}

Emit ''
Emit '===== LOCAL GROUP MEMBERSHIP ====='
# Group names are localized (Administratorer on a Swedish install), so resolve
# them by well-known SID instead of by name.
foreach ($sid in 'S-1-5-32-544', 'S-1-5-32-545') {
    $grp = Get-LocalGroup -SID $sid -ErrorAction SilentlyContinue
    if (-not $grp) { Emit ("[$sid] <group unavailable>"); continue }
    Emit ("[" + $grp.Name + " / " + $sid + "]")
    $m = Get-LocalGroupMember -SID $sid -ErrorAction SilentlyContinue
    if ($m) { foreach ($x in ($m | Sort-Object Name)) { Emit ("  " + $x.Name + "  (" + $x.ObjectClass + ")") } }
    else { Emit '  <no members readable>' }
}

Emit ''
Emit '===== SERVICES (count + KidShell-related) ====='
$svc = Get-Service -ErrorAction SilentlyContinue
Emit ("TOTAL SERVICES                : " + ($svc | Measure-Object).Count)
$kid = $svc | Where-Object { $_.Name -like '*kid*' -or $_.DisplayName -like '*KidShell*' -or $_.Name -like '*watchdog*' }
Emit ("KidShell/watchdog services    : " + $(if ($kid) { ($kid | ForEach-Object { $_.Name }) -join ', ' } else { '<none>' }))

Emit ''
Emit '===== SHELL / EXPLORER ====='
Emit ("HKCU Shell override           : " + (Reg 'HKCU:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' 'Shell'))
Emit ("Explorer running              : " + (($null -ne (Get-Process explorer -ErrorAction SilentlyContinue))))

$text = $lines -join "`r`n"
Set-Content -Path $Out -Value $text -Encoding UTF8
Write-Output "WROTE $Out ($($lines.Count) lines)"
