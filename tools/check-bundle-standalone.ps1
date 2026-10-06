<#
.SYNOPSIS
    Proves a release bundle's validation scripts run with no repository, no SDK
    and no installed KidShell.

.DESCRIPTION
    WHY THIS GATE EXISTS

    A dedicated-device bundle was shipped that could not run its own preflight.
    build-release.ps1 copied tools\device-validation into the bundle and put the
    decision tool in components\KidShell.DeviceValidation\, and the module looked
    for it beside the scripts, one level up, and under three repository build
    paths - none of which is where it is in a bundle. On the WILMA test machine
    the preflight gathered every read-only fact correctly and then died with
    "KidShell.DeviceValidation.exe was not found".

    Every existing check passed. The unit tests pass because they test the rules,
    not the scripts. The bundle built cleanly. hashes.sha256 verified with zero
    mismatches. The documentation gate was happy. Nothing anywhere ever RAN a
    script out of a bundle, so the one property the bundle exists for was the one
    property nothing checked.

    HOW THIS CHECKS IT

    By copying the bundle somewhere with no .git and no KidShell.sln above it,
    and running the scripts there. Not by comparing path strings: the defect was
    a disagreement between two correct-looking path expressions, and a string
    comparison would have been written from the same wrong assumption.

    It also asserts the resolved tool is INSIDE the copied bundle, so a
    development machine that happens to have a built tree cannot make this pass
    for the wrong reason.

    READ-ONLY with respect to the machine. It copies a bundle to a temporary
    directory, runs read-only scripts, and deletes the copy. It installs nothing,
    registers nothing and writes nothing under Program Files or ProgramData.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\check-bundle-standalone.ps1
#>
[CmdletBinding()]
param(
    # An existing bundle. Without it the newest under release-artifacts is used.
    [string] $BundlePath,

    # Keep the isolated copy for inspection.
    [switch] $KeepCopy
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$problems = New-Object System.Collections.Generic.List[string]

function Fail([string] $message) {
    $script:problems.Add($message)
    Write-Host "::error::$message"
}

# ------------------------------------------------------------ find a bundle

if (-not $BundlePath) {
    $artifacts = Join-Path $repoRoot 'release-artifacts'

    if (-not (Test-Path $artifacts)) {
        Write-Host "::error::No release-artifacts directory. Build a bundle first: tools\build-release.ps1"
        exit 1
    }

    $newest = Get-ChildItem $artifacts -Directory |
        Where-Object { Test-Path (Join-Path $_.FullName 'release-manifest.json') } |
        Sort-Object Name -Descending |
        Select-Object -First 1

    if (-not $newest) {
        Write-Host '::error::No bundle with a release-manifest.json under release-artifacts.'
        exit 1
    }

    $BundlePath = $newest.FullName
}

if (-not (Test-Path (Join-Path $BundlePath 'release-manifest.json'))) {
    Write-Host "::error::'$BundlePath' has no release-manifest.json, so it is not a release bundle."
    exit 1
}

Write-Host "Bundle: $BundlePath"

# ---------------------------------------------------------- isolate a copy
#
# Under the system temp directory, which on a normal Windows machine has no
# repository above it. Asserted rather than assumed: if the check is running
# somewhere with a checkout above TEMP, it says so instead of passing for the
# wrong reason.

$isolated = Join-Path ([System.IO.Path]::GetTempPath()) ("kidshell-standalone-" + [guid]::NewGuid().ToString('n'))
$copy = Join-Path $isolated (Split-Path -Leaf $BundlePath)

New-Item -ItemType Directory -Force -Path $isolated | Out-Null

try {
    Copy-Item $BundlePath -Destination $isolated -Recurse -Force

    Write-Host "Isolated copy: $copy"

    $walk = $copy
    $contaminated = @()

    while ($walk) {
        foreach ($marker in @('.git', 'KidShell.sln')) {
            if (Test-Path (Join-Path $walk $marker)) {
                $contaminated += (Join-Path $walk $marker)
            }
        }

        $parent = Split-Path -Parent $walk
        if ($parent -eq $walk) { break }
        $walk = $parent
    }

    if ($contaminated.Count -gt 0) {
        Fail ("The isolated copy is not isolated: a repository marker sits above it (" +
              ($contaminated -join ', ') + "). This check cannot prove anything here.")
    }
    else {
        Write-Host '  no .git and no KidShell.sln above the copy.'
    }

    $validation = Join-Path $copy 'device-validation'

    if (-not (Test-Path $validation)) {
        Fail "The bundle has no device-validation folder."
    }

    # ------------------------------------------------ 1. the module resolves

    Write-Host ''
    Write-Host '--- the module resolves inside the bundle ---'

    $contextScript = @"
Set-StrictMode -Version Latest
Import-Module '$($validation -replace "'", "''")\KidShellValidation.psm1' -Force
`$c = Get-ValidationContext
"KIND=`$(`$c.Kind)"
"TOOL=`$(`$c.Tool)"
"TOOLPRESENT=`$(`$c.ToolPresent)"
"AUDIT=`$(`$c.AuditScript)"
"AUDITPRESENT=`$(`$c.AuditPresent)"
"AUDITSOURCE=`$(`$c.AuditSource)"
"EVIDENCE=`$(`$c.EvidenceRoot)"
"DEVBUILD=`$(Test-DevelopmentBuild)"
"@

    $contextFile = Join-Path $isolated 'context.ps1'
    Set-Content -Path $contextFile -Value $contextScript -Encoding utf8

    $contextOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $contextFile 2>&1
    $contextOutput | ForEach-Object { Write-Host "  $_" }

    function Field([string] $name) {
        $line = $contextOutput | Where-Object { $_ -is [string] -and $_ -like "$name=*" } | Select-Object -First 1
        if (-not $line) { return '' }
        return ($line -split '=', 2)[1]
    }

    if ((Field 'KIND') -ne 'Bundle') {
        Fail "The module detected context '$(Field 'KIND')' inside a bundle; it must detect 'Bundle'."
    }

    if ((Field 'TOOLPRESENT') -ne 'True') {
        Fail "The decision tool was not resolved inside the bundle. Looked at: $(Field 'TOOL')"
    }

    $resolvedTool = Field 'TOOL'

    if ($resolvedTool -and -not $resolvedTool.StartsWith($copy, [StringComparison]::OrdinalIgnoreCase)) {
        # The property that stops a built working tree making this pass for the
        # wrong reason.
        Fail "The resolved tool is OUTSIDE the isolated bundle: $resolvedTool"
    }

    if ((Field 'AUDITPRESENT') -ne 'True') {
        Fail "audit-windows-state.ps1 was not resolved inside the bundle. Looked at: $(Field 'AUDIT')"
    }

    if ((Field 'AUDITSOURCE') -ne 'context') {
        # Resolved by the last-resort fallback rather than by the bundle's own
        # convention. It would work, and the primary path would be wrong and
        # nobody would know - which is this pass's defect in miniature.
        Fail ("The audit script was resolved by the fallback, not by the Bundle context. " +
              "The bundle-relative path has rotted: $(Field 'AUDIT')")
    }

    $resolvedEvidence = Field 'EVIDENCE'

    if ($resolvedEvidence -and -not $resolvedEvidence.StartsWith($copy, [StringComparison]::OrdinalIgnoreCase)) {
        Fail "The default evidence root is outside the bundle: $resolvedEvidence"
    }

    if ((Field 'DEVBUILD') -ne 'False') {
        # A bundle is not a working copy. Treating it as one would close the
        # interlock on exactly the machines a bundle is built for.
        Fail 'The module reported a bundle as a development build.'
    }

    # --------------------------------------- installed service path contract
    #
    # The lab installer puts each helper under its component directory. WILMA
    # exposed a drift where the SecurityHost apply script still looked for the
    # old flat path under C:\Program Files\KidShell and therefore refused a
    # correctly installed bundle. Keep the bundle's apply script aligned with
    # the install layout before it reaches a physical device.
    Write-Host ''
    Write-Host '--- SecurityHost install path contract ---'

    $securityHostApply = Join-Path $validation 'apply\02-install-securityhost-service.ps1'

    if (-not (Test-Path $securityHostApply)) {
        Fail 'The bundle has no SecurityHost service installer.'
    }
    else {
        $serviceText = [System.IO.File]::ReadAllText($securityHostApply, [System.Text.Encoding]::UTF8)
        $canonical = "Join-Path `$installRoot 'KidShell.SecurityHost\KidShell.SecurityHost.exe'"
        $obsolete = "Join-Path `$installRoot 'KidShell.SecurityHost.exe'"

        if (-not $serviceText.Contains($canonical)) {
            Fail 'The SecurityHost service installer does not target the component directory used by the lab installer.'
        }

        if ($serviceText.Contains($obsolete)) {
            Fail 'The SecurityHost service installer still contains the obsolete flat install path.'
        }
    }

    # ------------------------------------------------------- 2. preflight

    Write-Host ''
    Write-Host '--- 00-preflight.ps1 ---'

    $preflight = & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $validation '00-preflight.ps1') 2>&1
    $preflightCode = $LASTEXITCODE

    $preflight | Select-Object -Last 6 | ForEach-Object { Write-Host "  $_" }

    if ($preflightCode -ne 0) {
        Fail "00-preflight.ps1 exited $preflightCode from a bundle."
    }

    foreach ($expected in @('Context        : Bundle', 'Nothing was changed.')) {
        if (-not ($preflight | Where-Object { $_ -is [string] -and $_ -like "*$expected*" })) {
            Fail "00-preflight.ps1 did not print '$expected'."
        }
    }

    if ($preflight | Where-Object { $_ -is [string] -and $_ -like '*was not found*' }) {
        Fail '00-preflight.ps1 reported something it could not find.'
    }

    # ------------------------------------------------------- 3. baseline
    #
    # Writes evidence inside the isolated copy and runs the machine-state audit,
    # which is read-only by construction - there is no Set-, New-, Remove-,
    # Enable- or Disable- cmdlet anywhere in it.

    Write-Host ''
    Write-Host '--- 01-capture-baseline.ps1 ---'

    $baseline = & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $validation '01-capture-baseline.ps1') 2>&1
    $baselineCode = $LASTEXITCODE

    $baseline | Select-Object -Last 8 | ForEach-Object { Write-Host "  $_" }

    if ($baselineCode -ne 0) {
        Fail "01-capture-baseline.ps1 exited $baselineCode from a bundle."
    }

    $runs = @()
    $evidenceRoot = Join-Path $copy 'validation-results'

    if (Test-Path $evidenceRoot) {
        $runs = @(Get-ChildItem $evidenceRoot -Directory)
    }

    if ($runs.Count -eq 0) {
        Fail "01-capture-baseline.ps1 produced no run directory under $evidenceRoot."
    }
    else {
        $run = $runs[0].FullName

        foreach ($file in @('before-machine-state.txt', 'run.json')) {
            if (-not (Test-Path (Join-Path $run $file))) {
                Fail "The baseline did not write $file."
            }
        }

        # The bundle knows which commit it is, so the report must record it.
        $runJson = Join-Path $run 'run.json'

        if (Test-Path $runJson) {
            $recorded = (Get-Content $runJson -Raw | ConvertFrom-Json).gitSha

            if (-not $recorded) {
                Fail 'run.json records no Git SHA, so the report would not say which build was validated.'
            }
            else {
                Write-Host "  run.json records commit $recorded"
            }
        }
    }

    # ------------------------------------------------ 4. the whole suite
    #
    # Every read-only stage, from the bundle. This is what caught the second
    # class of defect in this pass: 07-verify-child-denials.ps1 threw on every
    # run that gave it a -RunPath, because the array subexpression operator over
    # a generic List throws on Windows PowerShell 5.1. Running one script proves
    # one script works.

    Write-Host ''
    Write-Host '--- Run-ReadOnlyValidation.ps1 ---'

    $suite = & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $validation 'Run-ReadOnlyValidation.ps1') -NonInteractive 2>&1

    $threw = @($suite | Where-Object { $_ -is [string] -and $_ -match 'threw:' })

    foreach ($line in $threw) {
        Fail "A validation stage threw while running from a bundle: $line"
    }

    if (-not ($suite | Where-Object { $_ -is [string] -and $_ -like '*OVERALL:*' })) {
        Fail 'Run-ReadOnlyValidation.ps1 produced no report from a bundle.'
    }

    # INCOMPLETE is the correct answer on a machine with nothing installed. FAIL
    # is not, and neither is a missing verdict.
    $overall = ($suite | Where-Object { $_ -is [string] -and $_ -like '*OVERALL:*' } |
        Select-Object -First 1)

    if ($overall) {
        Write-Host "  $($overall.Trim())"

        if ($overall -notlike '*INCOMPLETE*') {
            Fail "Expected OVERALL: INCOMPLETE from a machine with nothing installed, got: $($overall.Trim())"
        }
    }

    $stageFiles = 0
    $suiteRuns = @()

    if (Test-Path (Join-Path $copy 'validation-results')) {
        $suiteRuns = @(Get-ChildItem (Join-Path $copy 'validation-results') -Directory)
    }

    foreach ($suiteRun in $suiteRuns) {
        $stageFiles += @(Get-ChildItem $suiteRun.FullName -Filter '*.stage.json' -File).Count
    }

    # Eleven required stages; a run that reached them all writes a stage file for
    # each one it executed. A stage that threw writes none, which is how a silent
    # failure used to hide inside an honest-looking INCOMPLETE report.
    if ($stageFiles -lt 8) {
        Fail "Only $stageFiles stage evidence file(s) were written; a complete read-only pass writes at least 8."
    }
    else {
        Write-Host "  $stageFiles stage evidence file(s) written."
    }

    # ------------------------------------------- 5. nothing left the sandbox

    Write-Host ''
    Write-Host '--- nothing escaped ---'

    foreach ($path in @(
            (Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'KidShell'),
            (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'KidShell'),
            'C:\ProgramData\KidShell-TestDevice')) {
        if (Test-Path $path) {
            Fail "A read-only check created or found $path. It must not exist on a development machine."
        }
    }

    if (@(Get-Service -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '*KidShell*' }).Count -gt 0) {
        Fail 'A KidShell service is registered on this machine.'
    }

    Write-Host '  no Program Files, no ProgramData, no service.'
}
finally {
    if (-not $KeepCopy) {
        Remove-Item $isolated -Recurse -Force -ErrorAction SilentlyContinue
    }
    else {
        Write-Host ''
        Write-Host "Kept: $isolated"
    }
}

Write-Host ''

if ($problems.Count -gt 0) {
    Write-Host "Bundle standalone check FAILED with $($problems.Count) problem(s)." -ForegroundColor Red
    exit 1
}

Write-Host 'The bundle runs its own validation scripts with no repository, no SDK and no installed KidShell.' -ForegroundColor Green
exit 0
