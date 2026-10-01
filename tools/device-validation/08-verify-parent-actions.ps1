<#
.SYNOPSIS
    Guides and records the parent-policy authority checks. Changes nothing itself.

.DESCRIPTION
    The staged-then-approved policy flow cannot be driven from a script, and
    should not be: the whole point is that a human answers a consent prompt. So
    this script states each observation the operator has to make, collects the
    answer, and records it as evidence.

    IT ASKS RATHER THAN ASSUMES. Every answer defaults to "not observed", which
    becomes NOT RUN - never a pass. An operator who presses return through this
    produces a report that says nothing was checked, which is the truth.

    Run it from the CHILD's session, with the shell in front of you, because the
    thing being tested is what the child's process can and cannot commit.
#>
[CmdletBinding()]
param(
    [string] $RunPath,
    [switch] $NonInteractive
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'KidShellValidation.psm1') -Force

$stage = New-ValidationStage -Name 'PARENT AUTHORIZATION'

$observations = @(
    @{ Key = 'child-cannot-commit'
       Ask = 'Did KidShell refuse to save a policy change WITHOUT a consent prompt appearing?'
       Pass = 'The child process could not commit a policy change on its own.'
       Fail = 'A policy change was committed with no consent prompt. The child session has policy authority.' }

    @{ Key = 'one-prompt'
       Ask = 'When you saved a change in Foraldralage, did EXACTLY ONE consent prompt appear?'
       Pass = 'Saving settings raised exactly one consent prompt.'
       Fail = 'Saving settings did not raise exactly one prompt.' }

    @{ Key = 'declined-changes-nothing'
       Ask = 'After CANCELLING the prompt, were the previous settings still in effect?'
       Pass = 'A declined approval left the policy unchanged.'
       Fail = 'A declined approval changed the policy anyway.' }

    @{ Key = 'approved-applies'
       Ask = 'After APPROVING the prompt, did the change take effect?'
       Pass = 'An approved change became the live policy.'
       Fail = 'An approved change did not take effect.' }

    @{ Key = 'pin-change-needs-approval'
       Ask = 'Did changing the parent PIN also require the consent prompt?'
       Pass = 'A PIN change required an administrator.'
       Fail = 'A PIN change did not require an administrator.' }

    @{ Key = 'provisioning-marker'
       Ask = 'Is C:\ProgramData\KidShell\policy\provisioned.json still present and unchanged?'
       Pass = 'The provisioning marker was not writable from the child session.'
       Fail = 'The provisioning marker changed.' }

    @{ Key = 'survives-service-restart'
       Ask = 'After restarting the service, did the staged/approved behaviour still work the same way?'
       Pass = 'The staging and approval behaviour survived a service restart.'
       Fail = 'The staging or approval behaviour changed after a service restart.' }
)

Write-Host ''
Write-Host '===== Parent policy authority =====' -ForegroundColor Cyan
Write-Host ''
Write-Host 'Work through these in KidShell, then answer. Anything you did not actually' -ForegroundColor Yellow
Write-Host 'observe should be left as "not observed" - the report will say NOT RUN, which' -ForegroundColor Yellow
Write-Host 'is better than a pass nobody checked.' -ForegroundColor Yellow
Write-Host ''

$recorded = @{}

foreach ($observation in $observations) {
    if ($NonInteractive) {
        Add-ValidationFinding -Stage $stage -Status NotRun `
            -Detail "$($observation.Ask) (not observed: run interactively on the device)"

        $recorded[$observation.Key] = 'not observed'
        continue
    }

    Write-Host $observation.Ask
    $answer = Read-Host '  yes / no / (return for not observed)'

    switch ($answer.Trim().ToLowerInvariant()) {
        'yes' {
            Add-ValidationFinding -Stage $stage -Status Pass -Detail $observation.Pass
            $recorded[$observation.Key] = 'yes'
        }
        'y' {
            Add-ValidationFinding -Stage $stage -Status Pass -Detail $observation.Pass
            $recorded[$observation.Key] = 'yes'
        }
        'no' {
            Add-ValidationFinding -Stage $stage -Status Fail -Detail $observation.Fail
            $recorded[$observation.Key] = 'no'
        }
        'n' {
            Add-ValidationFinding -Stage $stage -Status Fail -Detail $observation.Fail
            $recorded[$observation.Key] = 'no'
        }
        default {
            Add-ValidationFinding -Stage $stage -Status NotRun -Detail "$($observation.Ask) (not observed)"
            $recorded[$observation.Key] = 'not observed'
        }
    }

    Write-Host ''
}

if ($RunPath) {
    Write-Evidence -RunPath $RunPath -Name 'parent-policy.json' -Data @{
        runAs        = "$env:USERDOMAIN\$env:USERNAME"
        elevated     = [bool](Test-Elevated)
        observations = $recorded
    } | Out-Null

    Save-ValidationStage -RunPath $RunPath -Stage $stage
}

Write-Host 'Nothing was changed by this script.' -ForegroundColor Green
