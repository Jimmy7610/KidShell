<#
    Shared plumbing for the dedicated-device validation scripts.

    WHAT THIS MODULE DOES AND DOES NOT DECIDE

    It gathers facts from Windows and writes evidence. It decides nothing. Every
    judgement - whether the interlock opens, whether an access list matches the
    plan, whether a stage may be called a pass - is made by
    KidShell.DeviceValidation.exe, which is the same code the unit tests
    exercise.

    That split is deliberate. Rules written once in C# for the tests and again
    in PowerShell for the real run are two rules, and the second one is the one
    that would actually run on somebody's machine.

    NOTHING IN THIS FILE MUTATES ANYTHING. There is no Set-, New-, Remove-,
    Enable- or Disable- call against Windows security state anywhere in it, and
    the mutating scripts under apply/ are the only place there ever will be.
#>

Set-StrictMode -Version Latest

$script:Root = Split-Path -Parent $PSCommandPath

# The folder one level up. In a bundle that is the bundle root; in a checkout it
# is tools\. Named for what it is rather than what it is assumed to be, because
# assuming it was always the second is the whole of the defect below.
$script:Parent = Split-Path -Parent $script:Root
$script:RepoRoot = Split-Path -Parent $script:Parent

# Declared here, not on first use. Set-StrictMode turns reading an unset variable
# into a terminating error, so a cache only assigned inside the function it
# caches for fails the first time it is read - which it did, and running a real
# bundle is what caught it.
$script:Context = $null

# -------------------------------------------------------------- the context

<#
    WHERE AM I RUNNING FROM?

    THE DEFECT THIS SECTION EXISTS TO END

    These scripts ship inside a release bundle, and the bundle is meant to be
    self-contained: an operator copies it to a dedicated machine that has no
    checkout, no .NET SDK and no installed KidShell, and runs the preflight.

    That did not work. build-release.ps1 copied tools\device-validation to
    <bundle>\device-validation and put the decision tool in
    <bundle>\components\KidShell.DeviceValidation\ with the rest of its runtime.
    Get-ValidationTool searched beside the scripts, one level up, and three
    build-output paths under a repository root - none of which is where the tool
    is in a bundle. So on the WILMA test machine the preflight gathered every
    read-only fact correctly and then died on
    "KidShell.DeviceValidation.exe was not found".

    Three other things had the same shape and would have failed next:
    01-capture-baseline.ps1 and 15-capture-final-state.ps1 looked for
    tools\audit-windows-state.ps1 under a repository root when the bundle keeps
    it beside these scripts, and the evidence root defaulted to
    <repo>\validation-results, which in a bundle resolves to a sibling of the
    bundle rather than anywhere the operator chose.

    So the context is resolved ONCE, here, and everything else asks. One place to
    be wrong, and a gate that runs a real bundle from a real temporary directory
    to prove it is not.

    THE ONE PATH THAT CANNOT COME FROM InstallationLayout

    The C# layout is the authoritative source for every installed path, and the
    scripts use it through the tool - but they cannot use it to FIND the tool.
    That is a bootstrap, so the bundle-relative location is a constant here. It
    is the only one, it uses the same folder name the layout uses, and
    tools\check-bundle-standalone.ps1 proves the two agree by running a bundle
    rather than by comparing strings.
#>

function Get-ValidationContext {
    <#
        Which of the three situations this is, and where everything is in it.

        Resolved once and cached, so every script in a run agrees about where it
        is - and so the answer can be printed, which is worth more than it
        sounds: an operator who can see "Bundle" and the resolved tool path can
        tell a layout problem from a missing file in one glance.
    #>
    [CmdletBinding()]
    param([switch] $Refresh)

    if ($script:Context -and -not $Refresh) { return $script:Context }

    $componentFolder = 'KidShell.DeviceValidation'
    $exeName = 'KidShell.DeviceValidation.exe'

    $kind = 'Unknown'
    $root = $script:Parent
    $tool = $null
    $audit = $null
    $evidence = $null
    $gitSha = ''

    # A bundle announces itself. release-manifest.json is written by
    # build-release.ps1 and by nothing else.
    $bundleManifest = Join-Path $script:Parent 'release-manifest.json'

    if (Test-Path $bundleManifest) {
        $kind = 'Bundle'
        $root = $script:Parent
        $tool = Join-Path $root "components\$componentFolder\$exeName"

        # The bundle carries the audit script beside these ones.
        $audit = Join-Path $script:Root 'audit-windows-state.ps1'

        # Evidence stays inside the bundle folder unless the config says
        # otherwise. A sibling of the bundle is somewhere nobody chose.
        $evidence = Join-Path $root 'validation-results'

        # The bundle knows exactly which build it is, so the report can say so.
        try {
            $gitSha = ([string]((Get-Content $bundleManifest -Raw | ConvertFrom-Json).gitSha)).Trim()
        }
        catch { }
    }
    elseif ((Test-Path (Join-Path $script:RepoRoot '.git')) -or
            (Test-Path (Join-Path $script:RepoRoot 'KidShell.sln'))) {
        $kind = 'Repository'
        $root = $script:RepoRoot
        $audit = Join-Path $root 'tools\audit-windows-state.ps1'
        $evidence = Join-Path $root 'validation-results'

        foreach ($candidate in @(
                "src\$componentFolder\bin\x64\Release\net10.0-windows\$exeName",
                "src\$componentFolder\bin\x64\Debug\net10.0-windows\$exeName",
                "src\$componentFolder\bin\Release\net10.0-windows\$exeName")) {
            $path = Join-Path $root $candidate
            if (Test-Path $path) { $tool = $path; break }
        }
    }
    else {
        # Neither. Fall back to an installed release, which is where the tool
        # lives once Install-KidShellLab.ps1 has run.
        $kind = 'Installed'
        $root = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'KidShell'
        $tool = Join-Path $root "$componentFolder\$exeName"
        $audit = Join-Path $script:Root 'audit-windows-state.ps1'
        $evidence = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'KidShell\validation-results'
    }

    # Beside the scripts wins over everything, so an operator who drops the tool
    # in by hand is not overruled by a layout that disagrees with their machine.
    $beside = Join-Path $script:Root $exeName
    if (Test-Path $beside) { $tool = $beside }

    # A last resort: beside the scripts, in any context.
    #
    # RECORDED, not silent. A safety net that quietly rescues a wrong primary
    # path is the same class of defect as the one this whole file exists to fix -
    # it works, so nobody notices the path it was covering for has rotted. The
    # first version of this fallback made the standalone gate's audit-path
    # mutation ESCAPE, which is exactly that failure in miniature.
    #
    # So the context says how the answer was reached, and the gate asserts a
    # bundle resolved it from the bundle's own convention rather than from here.
    $auditSource = 'context'

    if (-not $audit -or -not (Test-Path $audit)) {
        $fallbackAudit = Join-Path $script:Root 'audit-windows-state.ps1'

        if (Test-Path $fallbackAudit) {
            $audit = $fallbackAudit
            $auditSource = 'fallback'
        }
    }

    $script:Context = [pscustomobject]@{
        Kind         = $kind
        Root         = $root
        ScriptRoot   = $script:Root
        Tool         = $tool
        ToolPresent  = ([bool]$tool -and (Test-Path $tool))
        AuditScript  = $audit
        AuditPresent = ([bool]$audit -and (Test-Path $audit))
        AuditSource  = $auditSource
        EvidenceRoot = $evidence
        GitSha       = $gitSha
    }

    return $script:Context
}

# ---------------------------------------------------------------- the tool

function Get-ValidationTool {
    <#
        The decision tool, wherever this is running from.

        Every candidate comes from Get-ValidationContext, so there is one
        definition of where the tool lives per situation rather than a search
        list each caller has to keep current.
    #>
    [CmdletBinding()]
    param()

    $context = Get-ValidationContext

    if ($context.ToolPresent) { return $context.Tool }

    throw @"
KidShell.DeviceValidation.exe was not found.

  context  : $($context.Kind)
  looked in: $($context.Tool)

In a release bundle it belongs at components\KidShell.DeviceValidation\. If this
IS a bundle and the file is missing, the bundle is incomplete - check it against
hashes.sha256 and build it again rather than copying the executable by hand: it
needs the DLLs beside it.
"@
}

function Get-ValidationAuditScript {
    <# The machine-state audit script, wherever this context keeps it. #>
    [CmdletBinding()]
    param()

    $context = Get-ValidationContext

    if ($context.AuditPresent) { return $context.AuditScript }

    throw "audit-windows-state.ps1 was not found (context $($context.Kind), looked in $($context.AuditScript))."
}

function Invoke-ValidationTool {
    <# Runs the decision tool and returns its output plus its exit code. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string[]] $Arguments)

    $tool = Get-ValidationTool
    $output = & $tool @Arguments 2>&1
    $code = $LASTEXITCODE

    [pscustomobject]@{
        ExitCode = $code
        Output   = $output
        Accepted = ($code -eq 0)
    }
}

# ------------------------------------------------------------- the config

function Get-ValidationConfigPath {
    [CmdletBinding()]
    param([string] $Path)

    if ($Path) { return $Path }

    $local = Join-Path $script:Root 'device-validation.json'

    if (Test-Path $local) { return $local }

    return $null
}

function Import-ValidationConfig {
    <#
        Reads the config, or returns $null.

        Deliberately does not fall back to the example template. A run against
        a template is a run against nobody's decision.
    #>
    [CmdletBinding()]
    param([string] $Path)

    $resolved = Get-ValidationConfigPath -Path $Path

    if (-not $resolved) { return $null }

    try {
        return Get-Content $resolved -Raw | ConvertFrom-Json
    }
    catch {
        Write-Warning "The validation config at $resolved is not valid JSON."
        return $null
    }
}

# -------------------------------------------------------------- the facts

function Test-Elevated {
    [CmdletBinding()]
    param()

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-DevelopmentBuild {
    <#
        Whether this toolset is running out of somebody's working copy.

        A git checkout is the signal, and it is an honest one: validation is
        meant to be run from an installed release on a prepared machine, so a
        .git directory above the scripts means this is a development tree. That
        is the one interlock condition that travels with the CODE rather than
        the machine, which is the point - a working copy must not be able to
        apply real lockdown anywhere at all.
    #>
    [CmdletBinding()]
    param()

    # Asked of the context rather than recomputed, so "am I in a checkout" has
    # one answer. A bundle is deliberately NOT a development build: being able to
    # install a bundle on a dedicated device is the entire point of building one.
    return (Get-ValidationContext).Kind -eq 'Repository'
}

function Get-WorkingMachineSign {
    <#
        Signs that this is somebody's actual computer.

        Not authoritative, and not a substitute for the marker file: a clean
        test machine could be domain-joined, and a sacrificial one could have a
        user profile. It is the last line against the case the other conditions
        cannot catch - a correctly prepared config and marker copied onto the
        wrong machine by somebody in a hurry.

        Read-only throughout.
    #>
    [CmdletBinding()]
    param()

    $signs = New-Object System.Collections.Generic.List[string]

    try {
        $cs = Get-CimInstance Win32_ComputerSystem -ErrorAction Stop

        if ($cs.PartOfDomain) {
            $signs.Add("joined to the domain '$($cs.Domain)'")
        }
    }
    catch { }

    try {
        # Profiles that are not the built-in ones. A test rig has the
        # administrator and the child; a family computer has more, and they
        # have been used.
        $profiles = @(Get-ChildItem 'C:\Users' -Directory -ErrorAction Stop |
            Where-Object { $_.Name -notin @('Public', 'Default', 'Default User', 'All Users') })

        if ($profiles.Count -gt 3) {
            $signs.Add("$($profiles.Count) user profiles under C:\Users")
        }
    }
    catch { }

    try {
        # A repository checkout is a developer's machine, not a test rig.
        # Only when these scripts are RUNNING from a checkout. A bundle that
        # happens to sit on a machine with a checkout elsewhere is not what this
        # sign is about, and reporting it would close the interlock on a good
        # test device for the wrong reason.
        if ((Get-ValidationContext).Kind -eq 'Repository') {
            $signs.Add('these scripts are running from a git working copy')
        }
    }
    catch { }

    foreach ($marker in @(
            "$env:LOCALAPPDATA\Microsoft\Outlook",
            "$env:APPDATA\Microsoft\Signatures",
            "$env:LOCALAPPDATA\Google\Chrome\User Data\Default\Login Data")) {
        if (Test-Path $marker) {
            $signs.Add("signed-in user data at $marker")
        }
    }

    return $signs.ToArray()
}

function Get-InterlockFact {
    <# Everything the interlock needs, gathered read-only. #>
    [CmdletBinding()]
    param([string] $ConfirmationPhrase = '')

    [pscustomobject]@{
        machineName         = $env:COMPUTERNAME
        isElevated          = [bool](Test-Elevated)
        markerFilePresent   = (Test-Path 'C:\ProgramData\KidShell-TestDevice\ALLOW-KIDSHELL-VALIDATION.txt')
        confirmationPhrase  = $ConfirmationPhrase
        isDevelopmentBuild  = [bool](Test-DevelopmentBuild)
        workingMachineSigns = @(Get-WorkingMachineSign)
    }
}

# ---------------------------------------------------------- the interlock

function Assert-DedicatedDevice {
    <#
        THE GATE. Throws unless every interlock condition holds.

        Six conditions of deliberately different kinds: a file on the machine,
        a phrase the operator types, a declaration in a config, the machine's
        own name, the process's token, and the build's own identity. Getting all
        six wrong at once is not something a stray flag can do, which is the
        entire design.

        Read-only itself. Every caller that could mutate anything calls this
        FIRST and still defaults to a dry run afterwards.
    #>
    [CmdletBinding()]
    param(
        [string] $ConfigPath,
        [switch] $NonInteractive
    )

    $phrase = ''

    if (-not $NonInteractive) {
        Write-Host ''
        Write-Host 'This stage can permanently change the machine it runs on:' -ForegroundColor Yellow
        Write-Host '  accounts, access lists, the shell, the ability to sign in.' -ForegroundColor Yellow
        Write-Host ''
        Write-Host 'To continue, type the confirmation phrase exactly:' -ForegroundColor Yellow
        Write-Host '  I UNDERSTAND THIS IS A DEDICATED KIDSHELL TEST DEVICE'
        Write-Host ''
        $phrase = Read-Host 'Phrase'
    }

    $facts = Get-InterlockFact -ConfirmationPhrase $phrase
    $factsPath = Join-Path ([System.IO.Path]::GetTempPath()) ("kidshell-interlock-$([guid]::NewGuid().ToString('n')).json")

    try {
        $facts | ConvertTo-Json -Depth 5 | Set-Content -Path $factsPath -Encoding utf8

        $arguments = @('interlock', '--facts', $factsPath)
        $resolvedConfig = Get-ValidationConfigPath -Path $ConfigPath

        if ($resolvedConfig) { $arguments += @('--config', $resolvedConfig) }

        $result = Invoke-ValidationTool -Arguments $arguments
        $result.Output | ForEach-Object { Write-Host $_ }

        if (-not $result.Accepted) {
            throw 'The dedicated-device interlock is CLOSED. Nothing has been changed.'
        }
    }
    finally {
        # The phrase the operator typed does not linger in a temp file.
        if (Test-Path $factsPath) { Remove-Item $factsPath -Force }
    }
}

# ----------------------------------------------------------- the evidence

function New-ValidationRun {
    <# Creates a run directory named for the moment and the machine. #>
    [CmdletBinding()]
    param([string] $EvidenceRoot)

    if (-not $EvidenceRoot) {
        $EvidenceRoot = (Get-ValidationContext).EvidenceRoot
    }

    $runId = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $env:COMPUTERNAME
    $path = Join-Path $EvidenceRoot $runId

    New-Item -ItemType Directory -Force -Path $path | Out-Null

    [pscustomobject]@{
        RunId = $runId
        Path  = $path
    }
}

function Get-LatestValidationRun {
    [CmdletBinding()]
    param([string] $EvidenceRoot)

    if (-not $EvidenceRoot) {
        $EvidenceRoot = (Get-ValidationContext).EvidenceRoot
    }

    if (-not (Test-Path $EvidenceRoot)) { return $null }

    Get-ChildItem $EvidenceRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1
}

function Write-Evidence {
    <#
        Writes an evidence document, REDACTED.

        Through the tool rather than by hand, and refused rather than written if
        the redaction could not run. An evidence file nobody could redact is one
        nobody should publish - it will end up attached to a bug report.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $RunPath,
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)] $Data
    )

    $raw = Join-Path ([System.IO.Path]::GetTempPath()) ("kidshell-evidence-$([guid]::NewGuid().ToString('n')).json")
    $destination = Join-Path $RunPath $Name

    try {
        $Data | ConvertTo-Json -Depth 12 | Set-Content -Path $raw -Encoding utf8

        $result = Invoke-ValidationTool -Arguments @('redact', '--in', $raw, '--out', $destination)

        if (-not $result.Accepted) {
            $result.Output | ForEach-Object { Write-Warning $_ }
            throw "Evidence '$Name' could not be redacted and was not written."
        }
    }
    finally {
        if (Test-Path $raw) { Remove-Item $raw -Force }
    }

    return $destination
}

function New-ValidationStage {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $Name)

    [pscustomobject]@{
        name     = $Name
        findings = New-Object System.Collections.Generic.List[object]
    }
}

function Add-ValidationFinding {
    <#
        Adds one observation.

        Status is one of Pass, Fail, NotRun, NotSupported. There is no
        "probably" and no default: a stage with nothing in it is NOT RUN, which
        is what stops a skipped stage reading as a pass.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Stage,
        [Parameter(Mandatory)][ValidateSet('Pass', 'Fail', 'NotRun', 'NotSupported')][string] $Status,
        [Parameter(Mandatory)][string] $Detail,
        [switch] $Warning
    )

    $Stage.findings.Add([pscustomobject]@{
        status     = $Status
        detail     = $Detail
        isWarning  = [bool]$Warning
    })
}

function Save-ValidationStage {
    <# Writes a stage so the report generator can find it. #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $RunPath,
        [Parameter(Mandatory)] $Stage
    )

    $slug = ($Stage.name -replace '[^A-Za-z0-9]+', '-').Trim('-').ToLowerInvariant()

    Write-Evidence -RunPath $RunPath -Name "$slug.stage.json" -Data $Stage | Out-Null

    $counts = $Stage.findings | Group-Object status | ForEach-Object { "$($_.Count) $($_.Name)" }

    Write-Host ("{0}: {1}" -f $Stage.name, ($counts -join ', ')) -ForegroundColor Cyan
}

Export-ModuleMember -Function @(
    'Get-ValidationContext', 'Get-ValidationAuditScript', 'Get-ValidationTool', 'Invoke-ValidationTool', 'Get-ValidationConfigPath', 'Import-ValidationConfig',
    'Test-Elevated', 'Test-DevelopmentBuild', 'Get-WorkingMachineSign', 'Get-InterlockFact',
    'Assert-DedicatedDevice', 'New-ValidationRun', 'Get-LatestValidationRun', 'Write-Evidence',
    'New-ValidationStage', 'Add-ValidationFinding', 'Save-ValidationStage'
)
