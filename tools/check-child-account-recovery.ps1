<#
.SYNOPSIS
    Checks that the child-account script can recover a partial setup, and that no
    script passes a SecurityIdentifier where Windows wants a LocalPrincipal.

.DESCRIPTION
    WHAT WENT WRONG ON WILMA

    apply\01-create-child-account.ps1 -Apply created KidShellChild (SID ...-1003,
    enabled) and then failed to add it to the built-in Users group:

        Add-LocalGroupMember -SID $usersSid -Member $created.SID

    -Member is typed LocalPrincipal[]. LocalPrincipal has two constructors, one
    empty and one taking a string, so a SecurityIdentifier object has no
    conversion to it and parameter binding failed before Windows was asked to do
    anything. The fallback passed the same thing and failed the same way.

    The script printed that as a yellow note, printed "Created 'KidShellChild'."
    in green, and exited 0. Then its own first statement made the state
    unrecoverable:

        if ($existing) { Write-Host 'already exists'; return }

    This gate does three separate jobs, and the third is the one a C# test cannot
    do:

      THE LINT       no script hands a SID OBJECT to a -Member parameter, and the
                     child-account script does not return early just because the
                     account exists.
      THE DECISIONS  the shipped binary's child-account-plan verb is asked about
                     all eight states, including WILMA's exact one, and every
                     answer is asserted.
      THE BINDING    on THIS machine, reproduce the failure: a SecurityIdentifier
                     does not convert to LocalPrincipal, and the forms the fix
                     uses do. By type conversion only - no cmdlet is called, so
                     no membership can change.

    STRICTLY READ-ONLY. It reads source files, runs a tool that only prints, and
    converts types. It creates no account, changes no membership, and runs no
    Apply script.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\check-child-account-recovery.ps1
#>
[CmdletBinding()]
param(
    [string] $Root,

    # Source lint and type-conversion checks only, skipping the verb. Used to
    # test the lint against a mutated copy of the tree, where no built tool
    # exists - without it, a mutant would be reported as caught when what
    # actually happened was that the tool could not be found.
    [switch] $LintOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }
$Root = (Resolve-Path $Root).Path

function Get-CodeLines {
    <#
        A script's lines with every comment blanked out, line numbers preserved.

        Line comments and block comments alike. The first version of this gate
        stripped only line comments and then flagged the example of the bad code
        inside its own documentation: a lint that cannot read its own source
        cannot be trusted about anybody else's.

        (And this comment may not contain a block-comment delimiter, because
        PowerShell block comments do not nest - the closing one would end this
        comment early and turn the rest of the function into a parse error.)
    #>
    param([Parameter(Mandatory)][string] $Text)

    $out = New-Object System.Collections.Generic.List[string]
    $inBlock = $false

    foreach ($line in ($Text -split "`r?`n")) {
        $kept = $line

        if ($inBlock) {
            $close = $kept.IndexOf('#>')

            if ($close -ge 0) { $kept = $kept.Substring($close + 2); $inBlock = $false }
            else { $kept = '' }
        }

        while ($kept.Contains('<#')) {
            $open = $kept.IndexOf('<#')
            $close = $kept.IndexOf('#>', $open + 2)

            if ($close -ge 0) { $kept = $kept.Substring(0, $open) + $kept.Substring($close + 2) }
            else { $kept = $kept.Substring(0, $open); $inBlock = $true; break }
        }

        if ($kept -match '^\s*#') { $kept = '' }

        $out.Add($kept)
    }

    return $out.ToArray()
}

$problems = @()
$checks = 0

Write-Host ''
Write-Host '===== Child account recovery (read-only) =====' -ForegroundColor Cyan

# ------------------------------------------------------------------ the lint

$forbidden = @(
    @{
        Pattern = '-Member\s+\$[A-Za-z_][A-Za-z0-9_]*\.SID\b'
        Reason  = 'a SecurityIdentifier object is being passed to -Member; LocalPrincipal has no conversion from one, so this fails at parameter binding and the membership is never added'
    },
    @{
        Pattern = '-Member\s+\(\[Security\.Principal\.SecurityIdentifier\]'
        Reason  = 'a SecurityIdentifier is being cast straight into -Member, which cannot bind'
    },
    @{
        Pattern = '-Member\s+\$\w*[sS]id\b'
        Reason  = 'a bare SID variable is being passed to -Member; pass the LocalUser object or go through Add-LocalGroupMemberBySid, which verifies the result'
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
    # UTF-8 explicitly: Get-Content defaults to the ANSI code page on Windows
    # PowerShell 5.1, which would make this gate's own patterns unreliable.
    $source = [System.IO.File]::ReadAllText($script.FullName, [System.Text.Encoding]::UTF8)
    $relative = $script.FullName.Substring($Root.Length).TrimStart('\')

    # Comments may describe the defect - they have to be able to name it - so
    # they are stripped first, block comments included.
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

# The recovery path itself. A script that returns as soon as the account exists
# cannot repair anything, however correct the rest of it is.
$childScript = Join-Path $Root 'tools\device-validation\apply\01-create-child-account.ps1'

if (-not (Test-Path $childScript)) {
    $problems += 'apply\01-create-child-account.ps1 is missing. The gate is broken, not clean.'
}
else {
    $childSource = [System.IO.File]::ReadAllText($childScript, [System.Text.Encoding]::UTF8)

    # Checked against CODE, not documentation. A doc comment mentioning the right
    # call would otherwise satisfy a check that the code no longer makes it.
    $code = Get-CodeLines -Text $childSource
    $joined = $code -join "`n"

    $required = @(
        @{
            Pattern = 'child-account-plan'
            Reason  = 'the script must ask the decision tool what to do instead of deciding in PowerShell, where the eight states cannot be tested'
        },
        @{
            Pattern = 'Add-LocalGroupMemberBySid'
            Reason  = 'the membership must be added through the helper that verifies the result by SID; a cmdlet that did not throw is not evidence'
        },
        @{
            Pattern = 'exit 4'
            Reason  = 'a created account with no Users membership must exit PARTIAL, not 0 - that is the outcome WILMA got and reported as success'
        }
    )

    # And the exit code at each reporting site, not merely somewhere in the file.
    #
    # The first version of this rule only asked whether 'exit 4' appeared at all,
    # and a mutant that changed one of the two PARTIAL sites to 'exit 0' escaped
    # because the other site still matched. What matters is that EVERY place that
    # announces a partial or refused state leaves with a non-zero code.
    $exitRules = @(
        @{ Marker = 'PARTIAL'; Expected = '4'
           Reason = 'a PARTIAL state must exit 4; exiting 0 is exactly how WILMA''s half-built account was reported as a finished stage' },
        @{ Marker = 'REFUSED\.'; Expected = '1'
           Reason = 'a refusal must exit 1, so a caller reading only the exit code cannot carry on' }
    )

    foreach ($rule in $exitRules) {
        for ($i = 0; $i -lt $code.Count; $i++) {
            if ($code[$i] -notmatch $rule.Marker) { continue }

            $checks++
            $found = ''

            for ($j = $i; $j -lt [Math]::Min($i + 12, $code.Count); $j++) {
                if ($code[$j] -match '^\s*exit\s+([0-9]+)') { $found = $Matches[1]; break }
            }

            if ($found -ne $rule.Expected) {
                $problems += ("01-create-child-account.ps1`:$($i + 1) - the exit after this is '{0}', expected {1}: {2}" `
                    -f $(if ($found) { $found } else { '<none>' }), $rule.Expected, $rule.Reason)
                $problems += "    $($code[$i].Trim())"
            }
        }
    }

    foreach ($rule in $required) {
        $checks++

        if ($joined -notmatch $rule.Pattern) {
            $problems += "01-create-child-account.ps1 - missing '$($rule.Pattern)': $($rule.Reason)"
        }
    }

    # The early return, in any spelling: a check on an existing account whose
    # body is nothing but a return.
    for ($i = 0; $i -lt $code.Count; $i++) {
        $checks++

        if ($code[$i] -match 'if\s*\(\s*\$(existing|child)\s*\)\s*\{' ) {
            $window = ($code[$i..([Math]::Min($i + 4, $code.Count - 1))]) -join "`n"

            if ($window -match '\breturn\b' -and $window -notmatch 'exit\s+[0-9]') {
                $problems += "01-create-child-account.ps1`:$($i + 1) - returns as soon as the account exists, so a partial setup can never be repaired"
                $problems += "    $($code[$i].Trim())"
            }
        }
    }
}

# ------------------------------------------------------------- the binding
#
# Reproduced by type conversion. No cmdlet is invoked, so nothing can change.

Write-Host ''
Write-Host '--- what -Member (LocalPrincipal) accepts on THIS machine ---'

$me = Get-LocalUser -SID ([System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value) -ErrorAction SilentlyContinue

if (-not $me) {
    $problems += 'The current account could not be read, so the binding check could not run.'
}
else {
    function Test-Binds($value) {
        try {
            $null = [Microsoft.PowerShell.Commands.LocalPrincipal]$value
            return $true
        }
        catch { return $false }
    }

    $checks++

    # The defect. This MUST still fail: if a future Windows made it bind, the
    # lint above would be guarding against nothing and should be revisited
    # deliberately rather than quietly becoming decorative.
    if (Test-Binds $me.SID) {
        $problems += 'A SecurityIdentifier now converts to LocalPrincipal on this machine. The lint above is guarding against something that no longer fails - revisit it.'
        Write-Host '  SecurityIdentifier object  BINDS (unexpected)' -ForegroundColor Yellow
    }
    else {
        Write-Host '  SecurityIdentifier object  does not bind  <- the WILMA failure' -ForegroundColor DarkGray
    }

    # And the forms the fix uses must bind, or the repair cannot work.
    foreach ($form in @(
        @{ Label = 'LocalUser object'; Value = $me },
        @{ Label = 'SID as a string'; Value = [string]$me.SID.Value },
        @{ Label = 'MACHINE\name'; Value = ("{0}\{1}" -f $env:COMPUTERNAME, $me.Name) }
    )) {
        $checks++

        if (Test-Binds $form.Value) {
            Write-Host ("  {0,-26} binds" -f $form.Label)
        }
        else {
            $problems += "$($form.Label) does not convert to LocalPrincipal, so the repair cannot use it."
        }
    }
}

# --------------------------------------------------------------- the decisions

if ($LintOnly) {
    Write-Host ''
    Write-Host '--- decision checks SKIPPED (-LintOnly) ---' -ForegroundColor Yellow
}
else {
    try {
        Write-Host ''
        Write-Host '--- what the shipped binary decides ---'

        Import-Module (Join-Path $Root 'tools\device-validation\KidShellValidation.psm1') -Force

        $wilmaSid = 'S-1-5-21-3382208030-1057815629-3599114088-1003'

        # Each case: the facts a script would observe, and the action that is the
        # only safe answer. WILMA's real SID, so the state under test is the state
        # on the device.
        $cases = @(
            @{ Name = 'WILMA now: exists, enabled, right SID, not in Users'
               Args = @('--name', 'KidShellChild', '--sid', $wilmaSid, '--expected-sid', $wilmaSid,
                        '--exists', 'true', '--enabled', 'true', '--in-users', 'false', '--recovery-admin', 'true')
               Action = 'Repair'; AddUsers = 'True'; Password = 'False' },

            @{ Name = 'A: absent'
               Args = @('--name', 'KidShellChild', '--exists', 'false', '--recovery-admin', 'true')
               Action = 'Create'; AddUsers = 'True'; Password = 'True' },

            @{ Name = 'B: exists and already in Users'
               Args = @('--name', 'KidShellChild', '--sid', $wilmaSid, '--exists', 'true',
                        '--enabled', 'true', '--in-users', 'true', '--recovery-admin', 'true')
               Action = 'None'; AddUsers = 'False'; Password = 'False' },

            @{ Name = 'D: privileged'
               Args = @('--name', 'KidShellChild', '--sid', $wilmaSid, '--exists', 'true',
                        '--enabled', 'true', '--in-users', 'true', '--privileged', 'S-1-5-32-544',
                        '--recovery-admin', 'true')
               Action = 'Refuse'; AddUsers = 'False'; Password = 'False' },

            @{ Name = 'E: disabled'
               Args = @('--name', 'KidShellChild', '--sid', $wilmaSid, '--exists', 'true',
                        '--enabled', 'false', '--in-users', 'false', '--recovery-admin', 'true')
               Action = 'Refuse'; AddUsers = 'False'; Password = 'False' },

            @{ Name = 'Users membership unknown'
               Args = @('--name', 'KidShellChild', '--sid', $wilmaSid, '--exists', 'true',
                        '--enabled', 'true', '--in-users', 'unknown', '--recovery-admin', 'true')
               Action = 'Refuse'; AddUsers = 'False'; Password = 'False' },

            @{ Name = 'privileged group unreadable'
               Args = @('--name', 'KidShellChild', '--sid', $wilmaSid, '--exists', 'true',
                        '--enabled', 'true', '--in-users', 'false', '--unreadable', 'S-1-5-32-544',
                        '--recovery-admin', 'true')
               Action = 'Refuse'; AddUsers = 'False'; Password = 'False' },

            @{ Name = 'SID does not match the config'
               Args = @('--name', 'KidShellChild', '--sid', $wilmaSid,
                        '--expected-sid', 'S-1-5-21-9-9-9-1001', '--exists', 'true',
                        '--enabled', 'true', '--in-users', 'true', '--recovery-admin', 'true')
               Action = 'Refuse'; AddUsers = 'False'; Password = 'False' },

            @{ Name = 'no recovery administrator'
               Args = @('--name', 'KidShellChild', '--exists', 'false', '--recovery-admin', 'false')
               Action = 'Refuse'; AddUsers = 'False'; Password = 'False' }
        )

        foreach ($case in $cases) {
            $checks++

            $result = Invoke-ValidationTool -Arguments (@('child-account-plan') + $case.Args)

            $got = @{}
            foreach ($line in $result.Output) {
                if ($line -is [string] -and $line -match '^([A-Z_]+)=(.*)$' -and $Matches[1] -ne 'REASON') {
                    $got[$Matches[1]] = $Matches[2]
                }
            }

            $action = ''
            if ($got.ContainsKey('ACTION')) { $action = $got['ACTION'] }

            $addUsers = ''
            if ($got.ContainsKey('ADD_USERS')) { $addUsers = $got['ADD_USERS'] }

            $password = ''
            if ($got.ContainsKey('NEEDS_PASSWORD')) { $password = $got['NEEDS_PASSWORD'] }

            # A refusal must also be non-zero, so a script that reads only the
            # exit code still cannot act on it.
            $expectedCode = 0
            if ($case.Action -eq 'Refuse') { $expectedCode = 1 }

            $ok = ($action -eq $case.Action) -and ($addUsers -eq $case.AddUsers) -and
                  ($password -eq $case.Password) -and ($result.ExitCode -eq $expectedCode)

            if ($ok) {
                Write-Host ("  {0,-52} {1,-7} addUsers={2,-5} exit={3}" -f `
                    $case.Name, $action, $addUsers, $result.ExitCode) -ForegroundColor Green
            }
            else {
                $problems += ("{0}: expected {1}/addUsers={2}/password={3}/exit={4}, got {5}/{6}/{7}/{8}" -f `
                    $case.Name, $case.Action, $case.AddUsers, $case.Password, $expectedCode,
                    $action, $addUsers, $password, $result.ExitCode)
            }
        }
    }
    catch {
        $problems += "The decision checks could not run: $($_.Exception.Message)"
    }
}

# ---------------------------------------------------------------- the verdict

if ($problems.Count -gt 0) {
    Write-Host ''

    foreach ($problem in $problems) {
        Write-Host "::error::$problem"
    }

    Write-Host ''
    Write-Host "Child account recovery check FAILED with $($problems.Count) problem(s)." -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host ("Checked {0} rule/case(s) across {1} script(s); a partial child account is repairable." `
    -f $checks, $scripts.Count) -ForegroundColor Green
Write-Host 'Nothing was changed.' -ForegroundColor Green

exit 0
