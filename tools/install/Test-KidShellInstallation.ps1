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
$toolCode = $code

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

# THIS CHECK USED TO FAIL OPEN.
#
# It compared each access rule's IdentityReference.Value - a LOCALIZED NTAccount
# name such as 'BUILTIN\Användare' - first against a SID string, which can never
# match a name, and then against the regex 'Users|Everyone|Authenticated', which
# matches no Swedish name either. So on a localized machine the one check whose
# job is to catch "an ordinary account can replace a LocalSystem service's
# binary" printed "no ordinary account has write access to the install root"
# whatever the access list actually said.
#
# Now: every identity is translated to its SID and compared against the SIDs the
# decision tool reports. And if the SID set cannot be obtained, the check is
# declared NOT PERFORMED rather than performed against a guess - this script's
# own rule is that a skipped check is not a pass.

$permissionsChecked = $false
$ordinaryAccountSids = @{}

$groupResult = & $tool @('security-groups', '--set', 'ordinary') 2>&1

if ($LASTEXITCODE -eq 0) {
    foreach ($line in $groupResult) {
        if ($line -is [string] -and $line -match '^(.+?)=(S-1-[0-9-]+)$') {
            $ordinaryAccountSids[$Matches[2]] = $Matches[1]
        }
    }
}

if ($ordinaryAccountSids.Count -eq 0) {
    Write-Host '  NOT PERFORMED: the ordinary-account SIDs could not be read from the decision tool.' -ForegroundColor Yellow
    Write-Host '  A permission check made against a guessed identity list would be worse than none.' -ForegroundColor Yellow
}
elseif (-not (Test-Path $installRoot)) {
    Write-Host '  the install root does not exist.'
}
else {
    $writeMask = [Security.AccessControl.FileSystemRights]::Write -bor
                 [Security.AccessControl.FileSystemRights]::Modify -bor
                 [Security.AccessControl.FileSystemRights]::FullControl -bor
                 [Security.AccessControl.FileSystemRights]::WriteData

    try {
        $acl = Get-Acl $installRoot
        $writable = @()
        $untranslatable = @()

        foreach ($rule in $acl.Access) {
            if ($rule.AccessControlType -ne 'Allow') { continue }
            if (([int]$rule.FileSystemRights -band [int]$writeMask) -eq 0) { continue }

            $sid = $null

            try {
                $sid = [string]$rule.IdentityReference.Translate(
                    [Security.Principal.SecurityIdentifier]).Value
            }
            catch {
                # An identity that will not translate is reported, not ignored.
                # Silently skipping it is how the old check lost its meaning.
                $untranslatable += [string]$rule.IdentityReference.Value
                continue
            }

            if ($ordinaryAccountSids.ContainsKey($sid)) {
                $writable += [pscustomobject]@{
                    Label  = $ordinaryAccountSids[$sid]
                    Sid    = $sid
                    Rights = $rule.FileSystemRights
                }
            }
        }

        $permissionsChecked = ($untranslatable.Count -eq 0)

        if ($writable.Count -gt 0) {
            # A LocalSystem service whose image an ordinary account can replace
            # is a privilege escalation with a service name.
            Write-Host '  WRITABLE BY ORDINARY ACCOUNTS:' -ForegroundColor Red

            foreach ($rule in $writable) {
                Write-Host ("    {0} ({1}) {2}" -f $rule.Label, $rule.Sid, $rule.Rights) -ForegroundColor Red
            }

            # Found, so it counts. Printing an escalation in red and then exiting
            # PASS is the same defect in a different costume.
            if ($code -eq 0) { $code = 1 }
        }
        else {
            Write-Host ("  no ordinary account has write access to the install root ({0} checked by SID)." `
                -f ($ordinaryAccountSids.Keys -join ', '))
        }

        foreach ($identity in $untranslatable) {
            Write-Host "  could not resolve the identity '$identity' to a SID, so it was not judged." -ForegroundColor Yellow
        }
    }
    catch {
        Write-Host "  could not read the access list: $($_.Exception.GetType().Name)" -ForegroundColor Yellow
    }
}

if (-not $permissionsChecked -and $code -eq 0) {
    # INCOMPLETE, not PASS.
    $code = 2
}

# The decision tool printed its own verdict above, before the access list was
# read. If what was found here contradicts it, say so here in words - an exit
# code that disagrees with the verdict on screen is a verdict nobody will see.
if ($code -ne $toolCode) {
    Write-Host ''

    if ($code -eq 2) {
        Write-Host 'INSTALLATION: INCOMPLETE' -ForegroundColor Yellow
        Write-Host '  The permission check above did not run to a conclusion, so the verdict printed earlier does not stand.' -ForegroundColor Yellow
    }
    else {
        Write-Host 'INSTALLATION: FAIL' -ForegroundColor Red
        Write-Host '  An ordinary account can write the install root. The verdict printed earlier does not stand.' -ForegroundColor Red
    }
}

Write-Host ''
Write-Host 'Nothing was changed.' -ForegroundColor Green

exit $code
