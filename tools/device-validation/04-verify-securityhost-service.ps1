<#
.SYNOPSIS
    Read-only check of the KidShellSecurityHost service against the product's
    own expectations.

.DESCRIPTION
    STRICTLY READ-ONLY. It does not install, start, stop, reconfigure or remove
    a service. Installation lives under apply/ behind the interlock.

    The verdict comes from KidShell.DeviceValidation.exe, which compares what
    this script observed against SecurityHostService - the same constants the
    product uses and the install operation enforces.

    Two observations it cannot make on its own are the ones that matter most:
    whether the CHILD is actually refused when it tries to stop or reconfigure
    the service. Those need the child's own session, so they are recorded as NOT
    RUN unless this script is given their results. An unprobed denial is never
    reported as a pass.
#>
[CmdletBinding()]
param(
    [string] $ConfigPath,
    [string] $RunPath,

    # Set by 07-verify-child-denials.ps1 when it has actually run as the child.
    [AllowNull()][System.Nullable[bool]] $ChildStopDenied,
    [AllowNull()][System.Nullable[bool]] $ChildReconfigureDenied
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'KidShellValidation.psm1') -Force

$config = Import-ValidationConfig -Path $ConfigPath
$installRoot = 'C:\Program Files\KidShell'
$childSid = ''

if ($config) {
    if ($config.KidShellInstallRoot) { $installRoot = $config.KidShellInstallRoot }
    if ($config.ExpectedChildSid) { $childSid = $config.ExpectedChildSid }
}

$serviceName = 'KidShellSecurityHost'
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue

$observed = [pscustomobject]@{
    installed              = [bool]$service
    running                = $false
    startType              = ''
    account                = ''
    imagePath              = ''
    imageExists            = $false
    imageSha256            = ''
    imageChildWritable     = $true
    recoveryConfigured     = $false
    childStopDenied        = $ChildStopDenied
    childReconfigureDenied = $ChildReconfigureDenied
}

Write-Host ''
Write-Host '===== KidShellSecurityHost service (read-only) =====' -ForegroundColor Cyan

if ($service) {
    $observed.running = ($service.Status -eq 'Running')
    $observed.startType = [string]$service.StartType

    try {
        $wmi = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction Stop
        $observed.account = [string]$wmi.StartName
        $observed.imagePath = [string]$wmi.PathName
    }
    catch { }

    # The executable out of a registration that may be quoted and may carry
    # arguments. The tool does this too; here it is only to hash the file.
    $exe = $observed.imagePath.Trim()

    if ($exe.StartsWith('"')) {
        $closing = $exe.IndexOf('"', 1)
        if ($closing -gt 0) { $exe = $exe.Substring(1, $closing - 1) }
    }
    elseif ($exe -match '^(.*?\.exe)') {
        $exe = $Matches[1]
    }

    if ($exe -and (Test-Path $exe)) {
        $observed.imageExists = $true
        $observed.imageSha256 = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()

        if ($childSid) {
            # WILMA exposed two defects in the first version of this check.
            #
            # 1. FileSystemRights.Modify and FullControl are composite flag
            #    values containing read bits, so a plain ReadAndExecute ACE
            #    overlapped the old "write mask" and looked writable.
            # 2. Every ACE was translated inside one Where-Object expression.
            #    Localized App Package identities can fail translation; one
            #    harmless read-only ACE then threw the WHOLE check into catch,
            #    which conservatively reported the child writable.
            #
            # Examine only ACEs that carry an atomic mutation capability, then
            # translate those identities one at a time. Also include the
            # ordinary-account well-known SIDs: a child can inherit rights from
            # Users/Everyone/Authenticated Users even when its own SID has no
            # explicit ACE.
            $writeMask = [Security.AccessControl.FileSystemRights]::WriteData -bor
                         [Security.AccessControl.FileSystemRights]::AppendData -bor
                         [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor
                         [Security.AccessControl.FileSystemRights]::WriteAttributes -bor
                         [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
                         [Security.AccessControl.FileSystemRights]::Delete -bor
                         [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
                         [Security.AccessControl.FileSystemRights]::TakeOwnership

            try {
                $ordinarySids = @{}
                $ordinarySids[$childSid] = 'child'

                $groupResult = Invoke-ValidationTool -Arguments @('security-groups', '--set', 'ordinary')

                if ($groupResult.ExitCode -eq 0) {
                    foreach ($line in $groupResult.Output) {
                        if ($line -is [string] -and $line -match '^(.+?)=(S-1-[0-9-]+)
    }

    # A restart action in the SCM. Absent is a resilience gap, not a hole,
    # because the product fails closed without the service.
    try {
        $failure = & sc.exe qfailure $serviceName 2>&1 | Out-String
        $observed.recoveryConfigured = ($failure -match 'RESTART')
    }
    catch { }

    Write-Host ("Installed  : yes (status {0}, start {1})" -f $service.Status, $observed.startType)
    Write-Host ("Account    : {0}" -f $observed.account)
    Write-Host ("Image      : {0}" -f $observed.imagePath)
    Write-Host ("Image hash : {0}" -f $observed.imageSha256)
}
else {
    Write-Host 'Installed  : no'
}

$observedPath = Join-Path ([System.IO.Path]::GetTempPath()) ("kidshell-service-$([guid]::NewGuid().ToString('n')).json")

try {
    $observed | ConvertTo-Json -Depth 5 | Set-Content -Path $observedPath -Encoding utf8

    $arguments = @('expect', '--kind', 'service', '--observed', $observedPath)
    $resolved = Get-ValidationConfigPath -Path $ConfigPath
    if ($resolved) { $arguments += @('--config', $resolved) }

    $result = Invoke-ValidationTool -Arguments $arguments

    # The tool prints the human lines and then the stage as JSON. The JSON is
    # what the report consumes, so it is written straight through rather than
    # re-derived here.
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

        Write-Evidence -RunPath $RunPath -Name 'service.json' -Data $observed | Out-Null
        Write-Evidence -RunPath $RunPath -Name 'securityhost-service.stage.json' `
            -Data ($stageJson | ConvertFrom-Json) | Out-Null
    }
}
finally {
    if (Test-Path $observedPath) { Remove-Item $observedPath -Force }
}

Write-Host ''
Write-Host 'Nothing was changed.' -ForegroundColor Green
) {
                            $ordinarySids[$Matches[2]] = $Matches[1]
                        }
                    }
                }

                $acl = Get-Acl $exe
                $writable = @()
                $untranslatableWriteAce = $false

                foreach ($rule in $acl.Access) {
                    if ($rule.AccessControlType -ne 'Allow') { continue }
                    if (([int]$rule.FileSystemRights -band [int]$writeMask) -eq 0) { continue }

                    try {
                        $sid = [string]$rule.IdentityReference.Translate(
                            [Security.Principal.SecurityIdentifier]).Value
                    }
                    catch {
                        # A WRITE-capable ACE whose identity cannot be resolved
                        # cannot honestly be declared safe.
                        $untranslatableWriteAce = $true
                        continue
                    }

                    if ($ordinarySids.ContainsKey($sid)) {
                        $writable += $rule
                    }
                }

                $observed.imageChildWritable = ($writable.Count -gt 0 -or $untranslatableWriteAce)
            }
            catch {
                # Could not complete the check, so it is not claimed safe.
                $observed.imageChildWritable = $true
            }
        }
    }

    # A restart action in the SCM. Absent is a resilience gap, not a hole,
    # because the product fails closed without the service.
    try {
        $failure = & sc.exe qfailure $serviceName 2>&1 | Out-String
        $observed.recoveryConfigured = ($failure -match 'RESTART')
    }
    catch { }

    Write-Host ("Installed  : yes (status {0}, start {1})" -f $service.Status, $observed.startType)
    Write-Host ("Account    : {0}" -f $observed.account)
    Write-Host ("Image      : {0}" -f $observed.imagePath)
    Write-Host ("Image hash : {0}" -f $observed.imageSha256)
}
else {
    Write-Host 'Installed  : no'
}

$observedPath = Join-Path ([System.IO.Path]::GetTempPath()) ("kidshell-service-$([guid]::NewGuid().ToString('n')).json")

try {
    $observed | ConvertTo-Json -Depth 5 | Set-Content -Path $observedPath -Encoding utf8

    $arguments = @('expect', '--kind', 'service', '--observed', $observedPath)
    $resolved = Get-ValidationConfigPath -Path $ConfigPath
    if ($resolved) { $arguments += @('--config', $resolved) }

    $result = Invoke-ValidationTool -Arguments $arguments

    # The tool prints the human lines and then the stage as JSON. The JSON is
    # what the report consumes, so it is written straight through rather than
    # re-derived here.
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

        Write-Evidence -RunPath $RunPath -Name 'service.json' -Data $observed | Out-Null
        Write-Evidence -RunPath $RunPath -Name 'securityhost-service.stage.json' `
            -Data ($stageJson | ConvertFrom-Json) | Out-Null
    }
}
finally {
    if (Test-Path $observedPath) { Remove-Item $observedPath -Force }
}

Write-Host ''
Write-Host 'Nothing was changed.' -ForegroundColor Green
