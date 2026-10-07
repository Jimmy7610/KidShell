<#
.SYNOPSIS
    Checks that no security decision is made from a localized Windows group
    name, and proves on this machine that the SID path works.

.DESCRIPTION
    WINDOWS LOCALIZES ITS BUILT-IN GROUP NAMES. SIDS DO NOT CHANGE.

    On the Swedish machine used for physical validation, Administrators is
    "Administratörer" and Users is "Användare", and Get-LocalGroup -Name
    'Administrators' throws GroupNotFoundException. Three scripts asked Windows
    for groups by English name. Two failed closed. The third - the install
    verifier's "can an ordinary account write the install root" check - failed
    OPEN, reporting no escalation whatever the access list said, because it
    matched localized identity strings against the regex 'Users|Everyone|
    Authenticated'.

    A C# unit test cannot catch this. The defect lives in PowerShell, and the
    thing that proves the fix is Windows answering a question - so this gate
    does two separate jobs:

      THE LINT       no script may name a built-in group where it means an
                     identity.
      THE PROOF      on THIS machine, resolve the groups by SID, and confirm
                     that the English names really do fail. If the English names
                     happened to work here, the proof would be worthless and
                     this gate says so instead of passing.

    STRICTLY READ-ONLY. It resolves groups, reads memberships and reads source
    files. It creates nothing, changes no membership and runs no Apply script.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\check-localized-groups.ps1
#>
[CmdletBinding()]
param(
    [string] $Root,

    # Run the source lint only, without asking Windows anything. Used to test
    # the lint against a mutated copy of the tree, where no built tool exists -
    # the first version of this gate reported those mutants as CAUGHT when what
    # had actually happened was that the tool could not be found, which is the
    # same false confidence this whole pass is about.
    [switch] $LintOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Resolved here rather than as a parameter default: $PSScriptRoot is not
# reliably populated while the param block is being evaluated on Windows
# PowerShell 5.1, and a gate that cannot find the repository is a gate that
# passes by accident.
if (-not $Root) {
    $Root = Split-Path -Parent $PSScriptRoot
}

$Root = (Resolve-Path $Root).Path

function Get-CodeLines {
    <#
        A file's lines with every comment blanked out, line numbers preserved.

        Line comments and block comments alike. This gate originally skipped only
        line comments, which was luck rather than design: the sibling gate
        tools\check-child-account-recovery.ps1 did the same and immediately
        flagged the example of the bad code inside its own documentation.

        (This comment may not contain a block-comment delimiter: PowerShell block
        comments do not nest, so the closing one would end the comment early.)
    #>
    param([Parameter(Mandatory)][string] $Text, [switch] $CStyle)

    $out = New-Object System.Collections.Generic.List[string]
    $inBlock = $false
    $open = if ($CStyle) { '/*' } else { '<#' }
    $close = if ($CStyle) { '*/' } else { '#>' }

    foreach ($line in ($Text -split "`r?`n")) {
        $kept = $line

        if ($inBlock) {
            $at = $kept.IndexOf($close)

            if ($at -ge 0) { $kept = $kept.Substring($at + 2); $inBlock = $false }
            else { $kept = '' }
        }

        while ($kept.Contains($open)) {
            $start = $kept.IndexOf($open)
            $end = $kept.IndexOf($close, $start + 2)

            if ($end -ge 0) { $kept = $kept.Substring(0, $start) + $kept.Substring($end + 2) }
            else { $kept = $kept.Substring(0, $start); $inBlock = $true; break }
        }

        if ($CStyle) {
            if ($kept -match '^\s*(//|\*)') { $kept = '' }
        }
        elseif ($kept -match '^\s*#') { $kept = '' }

        $out.Add($kept)
    }

    return $out.ToArray()
}

$problems = @()
$checks = 0

Write-Host ''
Write-Host '===== Localized group names (read-only) =====' -ForegroundColor Cyan

# ------------------------------------------------------------------ the lint

# The six names 03-verify-accounts.ps1 hard-coded, plus the standard group
# apply\01-create-child-account.ps1 added the child to. Each pattern matches the
# name being USED as an identity - passed to a cmdlet, compared with, or matched
# by a regex - not merely mentioned in a comment, because the comments
# explaining this defect have to be allowed to name it.
$forbidden = @(
    @{
        Pattern = "-Group\s+['`"](Administrators|Users|Power Users|Backup Operators|Remote Desktop Users|Remote Management Users|Hyper-V Administrators|Everyone|Authenticated Users|Guests)['`"]"
        Reason  = 'a built-in group is being asked for by English name; on a localized Windows that group does not exist'
    },
    @{
        Pattern = "-Name\s+['`"](Administrators|Users|Power Users|Backup Operators|Remote Desktop Users|Remote Management Users|Hyper-V Administrators)['`"]"
        Reason  = 'a built-in group is being looked up by English name'
    },
    @{
        Pattern = "IdentityReference(\.Value)?\s*-(eq|match|like|in)"
        Reason  = 'an access-list identity is being compared as a string; IdentityReference is a LOCALIZED NTAccount and must be translated to a SID first'
    },
    @{
        Pattern = "-match\s+['`"][^'`"]*\b(Users|Everyone|Authenticated|Administrators)\b[^'`"]*['`"]"
        Reason  = 'a security decision is being made by matching a display name with a regex - the exact shape of the install verifier that failed open'
    },
    @{
        Pattern = "privilegedGroups\s*=\s*@\(\s*'Administrators'"
        Reason  = 'the privileged-group list is a list of English names again'
    }
)

$scripts = @(Get-ChildItem (Join-Path $Root 'tools') -Recurse -File -Include '*.ps1', '*.psm1' |
    Where-Object { $_.FullName -notmatch '\\(release-artifacts|validation-results)\\' })

if ($scripts.Count -eq 0) {
    Write-Host '::error::No scripts were found to check. The gate is broken, not clean.'
    exit 1
}

Write-Host ("Scripts : {0}" -f $scripts.Count)

foreach ($script in $scripts) {
    # Read as UTF-8 explicitly. Get-Content defaults to the ANSI code page on
    # Windows PowerShell 5.1, which turns Administratörer into mojibake and
    # would make this gate's own patterns unreliable.
    $source = [System.IO.File]::ReadAllText($script.FullName, [System.Text.Encoding]::UTF8)
    $relative = $script.FullName.Substring($Root.Length).TrimStart('\')

    # Comments are allowed to name the groups. Nothing else is.
    $lines = Get-CodeLines -Text $source

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]

        if (-not $line.Trim()) { continue }

        foreach ($rule in $forbidden) {
            $checks++

            if ($line -match $rule.Pattern) {
                $problems += "$relative`:$($i + 1) - $($rule.Reason)"
                $problems += "    $($line.Trim())"
            }
        }
    }
}

# ----------------------------------------------- install ACL mask regression
#
# WILMA exposed a second verifier bug: FileSystemRights.Modify is a composite
# flags value that contains read bits. Using it in a bitwise "danger mask"
# therefore classified a plain ReadAndExecute ACE as writable. Prove the mask's
# semantics here so that exact regression cannot silently return.
$installVerifier = Join-Path $Root 'tools\install\Test-KidShellInstallation.ps1'

if (-not (Test-Path $installVerifier)) {
    $problems += 'The install verifier is missing, so its ACL write-mask semantics could not be checked.'
}
else {
    $verifierText = [System.IO.File]::ReadAllText($installVerifier, [System.Text.Encoding]::UTF8)
    $verifierCode = (Get-CodeLines -Text $verifierText) -join "`n"

    if ($verifierCode -match '\$writeMask\s*=([\s\S]*?)(?=\n\s*try\s*\{)') {
        $maskSource = $Matches[1]

        if ($maskSource -match 'FileSystemRights\]::(Modify|FullControl|ReadAndExecute|Read|ExecuteFile)') {
            $problems += 'The install verifier write mask contains a composite/read right. Read-only ACEs can overlap it and be falsely classified as writable.'
        }
    }
    else {
        $problems += 'The install verifier write mask could not be located, so its semantics were not checked.'
    }

    $dangerMask = [Security.AccessControl.FileSystemRights]::WriteData -bor
                  [Security.AccessControl.FileSystemRights]::AppendData -bor
                  [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor
                  [Security.AccessControl.FileSystemRights]::WriteAttributes -bor
                  [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
                  [Security.AccessControl.FileSystemRights]::Delete -bor
                  [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
                  [Security.AccessControl.FileSystemRights]::TakeOwnership

    $wilmaReadOnly = [Security.AccessControl.FileSystemRights]::ReadAndExecute -bor
                     [Security.AccessControl.FileSystemRights]::Synchronize

    if (([int]$wilmaReadOnly -band [int]$dangerMask) -ne 0) {
        $problems += 'The ACL danger mask classifies WILMA''s ReadAndExecute, Synchronize ACE as writable.'
    }

    if (([int][Security.AccessControl.FileSystemRights]::WriteData -band [int]$dangerMask) -eq 0) {
        $problems += 'The ACL danger mask does not catch WriteData.'
    }

    if (([int][Security.AccessControl.FileSystemRights]::Modify -band [int]$dangerMask) -eq 0) {
        $problems += 'The ACL danger mask does not catch Modify.'
    }

    if (([int][Security.AccessControl.FileSystemRights]::FullControl -band [int]$dangerMask) -eq 0) {
        $problems += 'The ACL danger mask does not catch FullControl.'
    }
}

# ------------------------------------------ service ACL mask regression
#
# WILMA then exposed the same flags-enum trap in the SecurityHost verifier.
# Keep that script from reintroducing composite write flags, and prove the
# exact Program Files RX shape does not overlap the mutation mask.
$serviceVerifier = Join-Path $Root 'tools\device-validation\04-verify-securityhost-service.ps1'

if (-not (Test-Path $serviceVerifier)) {
    $problems += 'The SecurityHost service verifier is missing, so its binary ACL semantics could not be checked.'
}
else {
    $serviceText = [System.IO.File]::ReadAllText($serviceVerifier, [System.Text.Encoding]::UTF8)
    $serviceCode = (Get-CodeLines -Text $serviceText) -join "`n"

    if ($serviceCode -match '\$writeMask\s*=([\s\S]*?)(?=\n\s*try\s*\{)') {
        $maskSource = $Matches[1]

        if ($maskSource -match 'FileSystemRights\]::(Modify|FullControl|ReadAndExecute|Read|ExecuteFile)') {
            $problems += 'The SecurityHost verifier write mask contains a composite/read right. A read-only service-binary ACE can be falsely classified as writable.'
        }
    }
    else {
        $problems += 'The SecurityHost verifier write mask could not be located, so its semantics were not checked.'
    }

    $serviceDangerMask = [Security.AccessControl.FileSystemRights]::WriteData -bor
                         [Security.AccessControl.FileSystemRights]::AppendData -bor
                         [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor
                         [Security.AccessControl.FileSystemRights]::WriteAttributes -bor
                         [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
                         [Security.AccessControl.FileSystemRights]::Delete -bor
                         [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
                         [Security.AccessControl.FileSystemRights]::TakeOwnership

    $programFilesReadOnly = [Security.AccessControl.FileSystemRights]::ReadAndExecute -bor
                            [Security.AccessControl.FileSystemRights]::Synchronize

    if (([int]$programFilesReadOnly -band [int]$serviceDangerMask) -ne 0) {
        $problems += 'The SecurityHost ACL danger mask classifies Program Files ReadAndExecute, Synchronize as writable.'
    }

    if (([int][Security.AccessControl.FileSystemRights]::WriteData -band [int]$serviceDangerMask) -eq 0) {
        $problems += 'The SecurityHost ACL danger mask does not catch WriteData.'
    }
}

# ------------------------------------ protected-store ACL regression
#
# WILMA exposed the same flags-enum trap in 05-verify-protected-store.ps1:
# ReadAndExecute,Synchronize was reported as FullControl because the verifier
# used composite FileSystemRights values as bit-test masks.
$storeVerifier = Join-Path $Root 'tools\device-validation\05-verify-protected-store.ps1'

if (-not (Test-Path $storeVerifier)) {
    $problems += 'The protected-store verifier is missing, so its ACL mask semantics could not be checked.'
}
else {
    $storeText = [System.IO.File]::ReadAllText($storeVerifier, [System.Text.Encoding]::UTF8)
    $storeCode = (Get-CodeLines -Text $storeText) -join "`n"

    foreach ($maskName in 'writeMask', 'deleteMask', 'readMask', 'changeMask') {
        if ($storeCode -notmatch ('\

# The same assumption in C#. Most of this codebase already resolves groups from
# well-known SIDs, but WindowsLocalAccountService.AdministratorsGroupName() ended
# its try/catch with `return "Administrators";` - a fallback to a name that
# names no group on this machine, which would have made every administrator read
# as a standard user. Nothing in the suite could have caught that, so:
$forbiddenCSharp = @(
    @{
        Pattern = 'return\s+"(Administrators|Users|Power Users|Backup Operators|Remote Desktop Users|Remote Management Users|Hyper-V Administrators|Everyone|Guests)"'
        Reason  = 'a built-in group name is being returned as a fallback; on a localized Windows it names nothing and the caller reads an empty group as an empty answer'
    },
    @{
        Pattern = '(==|!=)\s*"(Administrators|Users|Everyone|Authenticated Users|Guests)"'
        Reason  = 'a built-in group is being compared by name rather than by SID'
    },
    @{
        Pattern = '\.Equals\("(Administrators|Users|Everyone|Authenticated Users|Guests)"'
        Reason  = 'a built-in group is being compared by name rather than by SID'
    }
)

$sources = @(Get-ChildItem (Join-Path $Root 'src') -Recurse -File -Filter '*.cs' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })

Write-Host ("Sources : {0}" -f $sources.Count)

foreach ($source in $sources) {
    $text = [System.IO.File]::ReadAllText($source.FullName, [System.Text.Encoding]::UTF8)
    $relative = $source.FullName.Substring($Root.Length).TrimStart('\')

    # Comments and doc comments are allowed to name the groups; the explanations
    # of this defect have to be able to say what it was. /* */ blocks included.
    $lines = Get-CodeLines -Text $text -CStyle

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]

        if (-not $line.Trim()) { continue }

        foreach ($rule in $forbiddenCSharp) {
            $checks++

            if ($line -match $rule.Pattern) {
                $problems += "$relative`:$($i + 1) - $($rule.Reason)"
                $problems += "    $($line.Trim())"
            }
        }
    }
}

# ----------------------------------------------------------------- the proof
#
# Reported through $problems, never by throwing. An unhandled throw here would
# abort before the lint's findings were printed and still exit non-zero, so a
# genuine lint failure would look identical to a missing tool.

if ($LintOnly) {
    Write-Host ''
    Write-Host '--- runtime proof SKIPPED (-LintOnly) ---' -ForegroundColor Yellow
}
else {
    try {

Write-Host ''
Write-Host '--- resolving the groups on THIS machine ---'

Import-Module (Join-Path $Root 'tools\device-validation\KidShellValidation.psm1') -Force

$groups = Get-WellKnownGroupSid -Set all

foreach ($label in 'Administrators', 'Users') {
    $sid = $groups[$label]
    $group = Get-BuiltinGroupBySid -Sid $sid

    if (-not $group) {
        $problems += "$label ($sid) could not be resolved by SID on this machine. Every account check depends on this working."
        continue
    }

    # The localized name, printed so the evidence shows WHICH name the English
    # lookup would have needed to be.
    Write-Host ("  {0,-16} {1,-14} -> '{2}'" -f $label, $sid, $group.Name)

    if ($group.Name -eq $label) {
        Write-Host ("    this Windows calls it the English name, so nothing is proved here") -ForegroundColor Yellow
    }
}

# Membership resolution has to work too: knowing the group exists is not the
# same as being able to answer "is this account in it".
$administrators = Get-LocalGroupMemberSid -GroupSid $groups['Administrators']

if (-not $administrators.Exists) {
    $problems += 'The Administrators group does not exist on this machine, which cannot be true. The SID resolution is broken.'
}
elseif (-not $administrators.Inspected) {
    $problems += "The Administrators group could not be enumerated: $($administrators.Error)"
}
else {
    Write-Host ("  members of {0}: {1}" -f $groups['Administrators'], $administrators.MemberSids.Count)

    if ($administrators.MemberSids.Count -eq 0) {
        # Not possible on a working Windows, and exactly what the old English
        # lookup produced: an empty membership that reads as "no administrators".
        $problems += 'The Administrators group reports no members at all. That is what the defect being fixed looked like.'
    }
}

# A group that genuinely is not here. Absence must be an answer, not an error.
$absent = Get-LocalGroupMemberSid -GroupSid 'S-1-5-32-547'   # Power Users

if (-not $absent.Exists) {
    Write-Host '  Power Users (S-1-5-32-547) is not present on this edition, and that is reported as absence, not failure.'
}
elseif (-not $absent.Inspected) {
    $problems += "Power Users exists here and could not be enumerated: $($absent.Error)"
}

# ----------------------------------------- and that the English names fail

Write-Host ''
Write-Host '--- what the old code asked for ---'

$englishFailures = 0
$englishNames = @('Administrators', 'Users', 'Power Users', 'Backup Operators', 'Remote Desktop Users')

foreach ($name in $englishNames) {
    try {
        $null = Get-LocalGroup -Name $name -ErrorAction Stop
        Write-Host ("  '{0}' resolves by name on this machine" -f $name)
    }
    catch {
        $englishFailures++
        Write-Host ("  '{0}' -> {1}" -f $name, $_.Exception.GetType().Name) -ForegroundColor DarkGray
    }
}

Write-Host ("  {0} of {1} English names do not resolve here." -f $englishFailures, $englishNames.Count)

if ($englishFailures -eq 0) {
    # This machine is English, or the groups all happen to exist under their
    # English names. The lint above still holds; this half of the gate simply
    # cannot demonstrate anything, and saying so is the honest outcome.
    Write-Host ''
    Write-Host '  NOTE: every English name resolves on this machine, so the runtime half of this' -ForegroundColor Yellow
    Write-Host '  gate proves nothing. Run it on a localized Windows to see it bite.' -ForegroundColor Yellow
}

    }
    catch {
        $problems += "The runtime proof could not run: $($_.Exception.Message)"
    }
}

# ---------------------------------------------------------------- the verdict

if ($problems.Count -gt 0) {
    Write-Host ''

    foreach ($problem in $problems) {
        Write-Host "::error::$problem"
    }

    Write-Host ''
    Write-Host "Localized group check FAILED with $($problems.Count) problem(s)." -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host ("Checked {0} line/rule pair(s) across {1} script(s) and {2} C# source(s); every built-in group is resolved by SID." `
    -f $checks, $scripts.Count, $sources.Count) -ForegroundColor Green
Write-Host 'Nothing was changed.' -ForegroundColor Green

exit 0
 + $maskName + '\s*=')) {
            $problems += "The protected-store verifier is missing $maskName."
        }
    }

    if ($storeCode -match '\$(writeMask|deleteMask|changeMask)\s*=([\s\S]*?)(?=\n\s*\$|\n\s*function)') {
        # Individual source checks below catch any forbidden composite mutation
        # flag no matter which mutation mask it appears in.
    }

    foreach ($bad in 'Write', 'Modify', 'FullControl', 'ReadAndExecute', 'Read') {
        if ($storeCode -match ('\$(writeMask|deleteMask|changeMask)\s*=([\s\S]*?)FileSystemRights\]::' + $bad + '\b')) {
            $problems += "The protected-store verifier mutation masks contain composite/read right '$bad'."
        }
    }

    $storeWriteMask = [Security.AccessControl.FileSystemRights]::WriteData -bor
                      [Security.AccessControl.FileSystemRights]::AppendData -bor
                      [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor
                      [Security.AccessControl.FileSystemRights]::WriteAttributes

    $storeDeleteMask = [Security.AccessControl.FileSystemRights]::Delete -bor
                       [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles

    $storeChangeMask = [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
                       [Security.AccessControl.FileSystemRights]::TakeOwnership

    $storeReadMask = [Security.AccessControl.FileSystemRights]::ReadData -bor
                     [Security.AccessControl.FileSystemRights]::ReadExtendedAttributes -bor
                     [Security.AccessControl.FileSystemRights]::ReadAttributes -bor
                     [Security.AccessControl.FileSystemRights]::ReadPermissions -bor
                     [Security.AccessControl.FileSystemRights]::ExecuteFile

    $wilmaChildReadOnly = [Security.AccessControl.FileSystemRights]::ReadAndExecute -bor
                          [Security.AccessControl.FileSystemRights]::Synchronize

    if (([int]$wilmaChildReadOnly -band [int]$storeWriteMask) -ne 0) {
        $problems += 'The protected-store write mask classifies WILMA child ReadAndExecute,Synchronize as writable.'
    }

    if (([int]$wilmaChildReadOnly -band [int]$storeDeleteMask) -ne 0) {
        $problems += 'The protected-store delete mask classifies WILMA child ReadAndExecute,Synchronize as deletable.'
    }

    if (([int]$wilmaChildReadOnly -band [int]$storeChangeMask) -ne 0) {
        $problems += 'The protected-store change-permissions mask classifies WILMA child ReadAndExecute,Synchronize as permission-changing.'
    }

    if (([int]$wilmaChildReadOnly -band [int]$storeReadMask) -eq 0) {
        $problems += 'The protected-store read mask does not recognize WILMA child ReadAndExecute,Synchronize as readable.'
    }
}

# -------------------------------------------------------------- the C# lint

# The same assumption in C#. Most of this codebase already resolves groups from
# well-known SIDs, but WindowsLocalAccountService.AdministratorsGroupName() ended
# its try/catch with `return "Administrators";` - a fallback to a name that
# names no group on this machine, which would have made every administrator read
# as a standard user. Nothing in the suite could have caught that, so:
$forbiddenCSharp = @(
    @{
        Pattern = 'return\s+"(Administrators|Users|Power Users|Backup Operators|Remote Desktop Users|Remote Management Users|Hyper-V Administrators|Everyone|Guests)"'
        Reason  = 'a built-in group name is being returned as a fallback; on a localized Windows it names nothing and the caller reads an empty group as an empty answer'
    },
    @{
        Pattern = '(==|!=)\s*"(Administrators|Users|Everyone|Authenticated Users|Guests)"'
        Reason  = 'a built-in group is being compared by name rather than by SID'
    },
    @{
        Pattern = '\.Equals\("(Administrators|Users|Everyone|Authenticated Users|Guests)"'
        Reason  = 'a built-in group is being compared by name rather than by SID'
    }
)

$sources = @(Get-ChildItem (Join-Path $Root 'src') -Recurse -File -Filter '*.cs' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })

Write-Host ("Sources : {0}" -f $sources.Count)

foreach ($source in $sources) {
    $text = [System.IO.File]::ReadAllText($source.FullName, [System.Text.Encoding]::UTF8)
    $relative = $source.FullName.Substring($Root.Length).TrimStart('\')

    # Comments and doc comments are allowed to name the groups; the explanations
    # of this defect have to be able to say what it was. /* */ blocks included.
    $lines = Get-CodeLines -Text $text -CStyle

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]

        if (-not $line.Trim()) { continue }

        foreach ($rule in $forbiddenCSharp) {
            $checks++

            if ($line -match $rule.Pattern) {
                $problems += "$relative`:$($i + 1) - $($rule.Reason)"
                $problems += "    $($line.Trim())"
            }
        }
    }
}

# ----------------------------------------------------------------- the proof
#
# Reported through $problems, never by throwing. An unhandled throw here would
# abort before the lint's findings were printed and still exit non-zero, so a
# genuine lint failure would look identical to a missing tool.

if ($LintOnly) {
    Write-Host ''
    Write-Host '--- runtime proof SKIPPED (-LintOnly) ---' -ForegroundColor Yellow
}
else {
    try {

Write-Host ''
Write-Host '--- resolving the groups on THIS machine ---'

Import-Module (Join-Path $Root 'tools\device-validation\KidShellValidation.psm1') -Force

$groups = Get-WellKnownGroupSid -Set all

foreach ($label in 'Administrators', 'Users') {
    $sid = $groups[$label]
    $group = Get-BuiltinGroupBySid -Sid $sid

    if (-not $group) {
        $problems += "$label ($sid) could not be resolved by SID on this machine. Every account check depends on this working."
        continue
    }

    # The localized name, printed so the evidence shows WHICH name the English
    # lookup would have needed to be.
    Write-Host ("  {0,-16} {1,-14} -> '{2}'" -f $label, $sid, $group.Name)

    if ($group.Name -eq $label) {
        Write-Host ("    this Windows calls it the English name, so nothing is proved here") -ForegroundColor Yellow
    }
}

# Membership resolution has to work too: knowing the group exists is not the
# same as being able to answer "is this account in it".
$administrators = Get-LocalGroupMemberSid -GroupSid $groups['Administrators']

if (-not $administrators.Exists) {
    $problems += 'The Administrators group does not exist on this machine, which cannot be true. The SID resolution is broken.'
}
elseif (-not $administrators.Inspected) {
    $problems += "The Administrators group could not be enumerated: $($administrators.Error)"
}
else {
    Write-Host ("  members of {0}: {1}" -f $groups['Administrators'], $administrators.MemberSids.Count)

    if ($administrators.MemberSids.Count -eq 0) {
        # Not possible on a working Windows, and exactly what the old English
        # lookup produced: an empty membership that reads as "no administrators".
        $problems += 'The Administrators group reports no members at all. That is what the defect being fixed looked like.'
    }
}

# A group that genuinely is not here. Absence must be an answer, not an error.
$absent = Get-LocalGroupMemberSid -GroupSid 'S-1-5-32-547'   # Power Users

if (-not $absent.Exists) {
    Write-Host '  Power Users (S-1-5-32-547) is not present on this edition, and that is reported as absence, not failure.'
}
elseif (-not $absent.Inspected) {
    $problems += "Power Users exists here and could not be enumerated: $($absent.Error)"
}

# ----------------------------------------- and that the English names fail

Write-Host ''
Write-Host '--- what the old code asked for ---'

$englishFailures = 0
$englishNames = @('Administrators', 'Users', 'Power Users', 'Backup Operators', 'Remote Desktop Users')

foreach ($name in $englishNames) {
    try {
        $null = Get-LocalGroup -Name $name -ErrorAction Stop
        Write-Host ("  '{0}' resolves by name on this machine" -f $name)
    }
    catch {
        $englishFailures++
        Write-Host ("  '{0}' -> {1}" -f $name, $_.Exception.GetType().Name) -ForegroundColor DarkGray
    }
}

Write-Host ("  {0} of {1} English names do not resolve here." -f $englishFailures, $englishNames.Count)

if ($englishFailures -eq 0) {
    # This machine is English, or the groups all happen to exist under their
    # English names. The lint above still holds; this half of the gate simply
    # cannot demonstrate anything, and saying so is the honest outcome.
    Write-Host ''
    Write-Host '  NOTE: every English name resolves on this machine, so the runtime half of this' -ForegroundColor Yellow
    Write-Host '  gate proves nothing. Run it on a localized Windows to see it bite.' -ForegroundColor Yellow
}

    }
    catch {
        $problems += "The runtime proof could not run: $($_.Exception.Message)"
    }
}

# ---------------------------------------------------------------- the verdict

if ($problems.Count -gt 0) {
    Write-Host ''

    foreach ($problem in $problems) {
        Write-Host "::error::$problem"
    }

    Write-Host ''
    Write-Host "Localized group check FAILED with $($problems.Count) problem(s)." -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host ("Checked {0} line/rule pair(s) across {1} script(s) and {2} C# source(s); every built-in group is resolved by SID." `
    -f $checks, $scripts.Count, $sources.Count) -ForegroundColor Green
Write-Host 'Nothing was changed.' -ForegroundColor Green

exit 0
