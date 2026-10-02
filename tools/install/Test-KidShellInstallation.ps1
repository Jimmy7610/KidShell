<#
.SYNOPSIS
    READ-ONLY check that an installation is what its receipt says it is.

.DESCRIPTION
    STRICTLY READ-ONLY. It reads files, hashes them, queries services and prints
    a verdict. It copies nothing, removes nothing and changes nothing.

    Returns PASS, FAIL or INCOMPLETE.

    THE RULE THAT MATTERS: a skipped check is not a pass. If the receipt says
    the build was signed and no signature was verified, the answer is INCOMPLETE
    - because the entire value of signing is that somebody later relies on the
    check having happened, and a green tick that skipped it is worse than a
    missing one.

    Safe on a development machine, where it will correctly report INCOMPLETE
    because there is no installation to check.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\Test-KidShellInstallation.ps1
#>
[CmdletBinding()]
param(
    [string] $ReceiptPath,

    # Verify Authenticode signatures as well. Only meaningful on a signed build.
    [switch] $CheckSignatures
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$programFiles = [Environment]::GetFolderPath('ProgramFiles')
$programData = [Environment]::GetFolderPath('CommonApplicationData')

$installRoot = Join-Path $programFiles 'KidShell'

Write-Host ''
Write-Host '===== KidShell installation (read-only) =====' -ForegroundColor Cyan
Write-Host "Install root : $installRoot"

function Find-Tool {
    $candidates = @(
        (Join-Path $installRoot 'KidShell.DeviceValidation\KidShell.DeviceValidation.exe'),
        (Join-Path $PSScriptRoot 'KidShell.DeviceValidation.exe'),
        (Join-Path (Split-Path -Parent $PSScriptRoot) 'components\KidShell.DeviceValidation\KidShell.DeviceValidation.exe'),
        (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'src\KidShell.DeviceValidation\bin\x64\Release\net10.0-windows\KidShell.DeviceValidation.exe')
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return $candidate }
    }

    return $null
}

$tool = Find-Tool

if (-not $tool) {
    Write-Host ''
    Write-Host 'INSTALLATION: INCOMPLETE' -ForegroundColor Yellow
    Write-Host '  KidShell.DeviceValidation.exe was not found, so nothing could be verified.'
    exit 1
}

# ------------------------------------------------- the signature question

$signaturesChecked = $false
$signatureFindings = @()

if ($CheckSignatures) {
    $executables = @()

    if (Test-Path $installRoot) {
        $executables = @(Get-ChildItem $installRoot -Recurse -File -Include '*.exe', '*.dll' -ErrorAction SilentlyContinue)
    }

    if ($executables.Count -eq 0) {
        $signatureFindings += 'There are no binaries under the install root to check.'
    }
    else {
        $unsigned = 0

        foreach ($file in $executables) {
            $signature = Get-AuthenticodeSignature -FilePath $file.FullName -ErrorAction SilentlyContinue

            if (-not $signature -or $signature.Status -ne 'Valid') {
                $unsigned++
            }
        }

        $signatureFindings += "$($executables.Count - $unsigned) of $($executables.Count) binaries carry a valid signature."

        # Checked means checked, whatever the answer. The verifier below is told
        # the check HAPPENED; whether it passed is a separate finding.
        $signaturesChecked = $true

        if ($unsigned -gt 0) {
            $signatureFindings += "$unsigned binary file(s) are unsigned or have an invalid signature."
        }
    }
}
else {
    $signatureFindings += 'Signatures were NOT verified (-CheckSignatures was not given).'
}

# --------------------------------------------------------- the comparison

$arguments = @('verify-install')
if ($ReceiptPath) { $arguments += @('--receipt', $ReceiptPath) }
if ($signaturesChecked) { $arguments += '--signature-checked' }

$output = & $tool @arguments 2>&1
$code = $LASTEXITCODE

$output | Where-Object { $_ -notmatch '^\s*[{}\[\]"]' } | ForEach-Object { Write-Host $_ }

Write-Host ''
Write-Host '--- signatures ---'

foreach ($finding in $signatureFindings) {
    Write-Host "  $finding"
}

# ------------------------------------------------- what validation expects

Write-Host ''
Write-Host '--- where validation expects the binaries ---'

$expected = @{
    'KidShell.SecurityHost'     = 'KidShell.SecurityHost.exe'
    'KidShell.Watchdog'         = 'KidShell.Watchdog.exe'
    'KidShell.Recovery'         = 'KidShell.Recovery.exe'
    'KidShell.DeviceValidation' = 'KidShell.DeviceValidation.exe'
}

$missing = 0

foreach ($folder in $expected.Keys | Sort-Object) {
    $path = Join-Path (Join-Path $installRoot $folder) $expected[$folder]
    $present = Test-Path $path

    if (-not $present) { $missing++ }

    Write-Host ("  [{0}] {1}" -f $(if ($present) { 'ok     ' } else { 'MISSING' }), $path)
}

# The service's binary has to be where the service validator looks for it, and
# that is the half of the old defect nobody would have noticed until a device.
if ($missing -gt 0) {
    Write-Host ''
    Write-Host "  $missing expected binary/binaries are not where the validation scripts look." -ForegroundColor Yellow
}

# ------------------------------------------- writable by ordinary accounts

Write-Host ''
Write-Host '--- permissions ---'

if (Test-Path $installRoot) {
    $usersSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-545')
    $writeMask = [Security.AccessControl.FileSystemRights]::Write -bor
                 [Security.AccessControl.FileSystemRights]::Modify -bor
                 [Security.AccessControl.FileSystemRights]::FullControl -bor
                 [Security.AccessControl.FileSystemRights]::WriteData

    try {
        $acl = Get-Acl $installRoot

        $writable = @($acl.Access | Where-Object {
            $_.AccessControlType -eq 'Allow' -and
            ([int]$_.FileSystemRights -band [int]$writeMask) -ne 0 -and
            (
                $_.IdentityReference.Value -eq $usersSid.Value -or
                $_.IdentityReference.Value -match 'Users|Everyone|Authenticated'
            )
        })

        if ($writable.Count -gt 0) {
            # A LocalSystem service whose image an ordinary account can replace
            # is a privilege escalation with a service name.
            Write-Host '  WRITABLE BY ORDINARY ACCOUNTS:' -ForegroundColor Red

            foreach ($rule in $writable) {
                Write-Host "    $($rule.IdentityReference) $($rule.FileSystemRights)" -ForegroundColor Red
            }
        }
        else {
            Write-Host '  no ordinary account has write access to the install root.'
        }
    }
    catch {
        Write-Host "  could not read the access list: $($_.Exception.GetType().Name)" -ForegroundColor Yellow
    }
}
else {
    Write-Host '  the install root does not exist.'
}

Write-Host ''
Write-Host 'Nothing was changed.' -ForegroundColor Green

exit $code
