<#
.SYNOPSIS
    Checks that the production container resolves the guarded implementations.

.DESCRIPTION
    The OPSV retest found the same shape of defect four times: a correct class,
    a thorough test suite for it, and a production application that used a
    different path. Protected storage was written and registered nowhere.
    ScreenTimeGuardedLauncher existed and the composition had to remember to
    prefer it. The core class being right proves nothing if the real app does
    not reach it.

    Unit tests cannot catch this. KidShell.App is a WinUI project and cannot be
    referenced from a test assembly, so nothing in the suite can ask the
    container what it would resolve. This reads the composition root instead.

    It is a lint, not a substitute for the integration tests in
    ProtectedStorageWiringTests and OpsvIntegrationTests. What it adds is a
    failure the day somebody wires an old implementation back in.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\check-composition.ps1
#>

[CmdletBinding()]
param(
    [string] $Path = 'src\KidShell.App\App.xaml.cs'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Test-Path $Path)) {
    Write-Host "::error::Composition root not found at $Path. The check is broken, not clean."
    exit 1
}

$source = [System.IO.File]::ReadAllText((Resolve-Path $Path))
$problems = @()

# Each rule is: a thing the production container MUST resolve, and the reason
# it matters. The reason is in the failure message, because a guard nobody
# understands gets deleted the first time it is inconvenient.
$required = @(
    @{
        Pattern = 'AddSingleton<IConfigurationStore>\(sp => new ProtectedConfigurationStore'
        Reason  = 'the parent policy would come from the file in the child''s own profile (OPSV 01)'
    },
    @{
        Pattern = 'AddSingleton<IScreenTimeStateStore>\(sp => new ProtectedScreenTimeStateStore'
        Reason  = 'the screen-time counter would be editable by the child (OPSV 01/04)'
    },
    @{
        Pattern = 'AddSingleton<IProtectedPolicyStore>'
        Reason  = 'nothing would decide between the real store and the development stand-in (OPSV 01)'
    },
    @{
        Pattern = 'AddSingleton<IUiDispatcher>'
        Reason  = 'background events would mutate WinUI state directly (OPSV 02)'
    },
    @{
        Pattern = 'AddSingleton<IParentSession>'
        Reason  = 'Parent Mode would never re-lock (OPSV 05B)'
    },
    @{
        Pattern = 'AddSingleton<IAppLauncher>\(sp => new ScreenTimeGuardedLauncher'
        Reason  = 'launches would skip the screen-time gate'
    }
)

foreach ($rule in $required) {
    if ($source -notmatch $rule.Pattern) {
        $problems += "missing registration: $($rule.Pattern) - without it, $($rule.Reason)"
    }
}

# And the implementations that must NOT be what the interface resolves to.
# Registering the concrete type is fine and deliberate - the decorators take it
# as a constructor argument. Registering it AS the interface is the defect.
$forbidden = @(
    @{
        Pattern = 'AddSingleton<IConfigurationStore>\(\s*sp => new JsonConfigurationStore'
        Reason  = 'resolves the parent policy straight from the child-writable file (OPSV 01)'
    },
    @{
        Pattern = 'AddSingleton<IScreenTimeStateStore>\(\s*sp => new JsonScreenTimeStateStore'
        Reason  = 'resolves the counter straight from the child-writable file (OPSV 01/04)'
    },
    @{
        Pattern = 'AddSingleton<IAppLauncher, AppLauncher>'
        Reason  = 'resolves the unguarded launcher'
    }
)

foreach ($rule in $forbidden) {
    if ($source -match $rule.Pattern) {
        $problems += "bypassed abstraction: $($rule.Pattern) - it $($rule.Reason)"
    }
}

if ($problems.Count -gt 0) {
    foreach ($problem in $problems) {
        Write-Host "::error::$problem"
    }

    Write-Host ''
    Write-Host "Composition check FAILED with $($problems.Count) problem(s) in $Path."
    exit 1
}

$count = $required.Count + $forbidden.Count
Write-Host "Checked $count composition rule(s) in $Path; production resolves the guarded implementations."
