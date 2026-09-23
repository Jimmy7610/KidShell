<#
.SYNOPSIS
    Produces a KidShell release candidate.

.DESCRIPTION
    One command, from a clean checkout to signed artifacts with checksums.

        powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-release.ps1

    Everything lands in release-artifacts\, which .gitignore excludes. No
    binaries are committed, and no secret is read from the repository.

    SIGNING IS A BOUNDARY, NOT A STEP
    ---------------------------------
    Without signing material this script still produces a package, and labels
    it UNSIGNED - DEVELOPMENT ONLY in the report, in the artifact file name and
    in a README beside it. It does not quietly produce something that looks
    shippable.

    With -Sign it requires real material and fails if it is absent, because
    "sign if you can, otherwise don't" is how an unsigned build reaches users.

    The certificate is never read from the repository: pass -CertificateThumbprint
    to use one from the current user's certificate store, or -SigningKeyVaultUrl
    with an external signing service. A password is never accepted as a
    parameter, never echoed, and never written to a log.

.PARAMETER Configuration
    Release by default. Debug produces a developer build and says so.

.PARAMETER Platform
    x64 (default) or ARM64.

.PARAMETER Sign
    Require signing. Fails if no signing material is supplied.

.PARAMETER CertificateThumbprint
    Thumbprint of a certificate in Cert:\CurrentUser\My. The private key stays
    in the store; this script never touches a .pfx on disk.

.PARAMETER TimestampUrl
    RFC 3161 timestamp server, so signatures outlive the certificate.

.PARAMETER SkipTests
    Skip the test run. Refused together with -Sign: an unverified build must
    not be signed.

.PARAMETER AllowDirty
    Package with uncommitted changes. The artifact is marked accordingly.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-release.ps1

.EXAMPLE
    .\tools\build-release.ps1 -Sign -CertificateThumbprint ABC123... -TimestampUrl http://timestamp.digicert.com
#>

[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',

    [ValidateSet('x64', 'ARM64')]
    [string] $Platform = 'x64',

    [switch] $Sign,

    [string] $CertificateThumbprint,

    [string] $TimestampUrl = 'http://timestamp.digicert.com',

    [switch] $SkipTests,

    [switch] $AllowDirty
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $repoRoot 'release-artifacts'
$startedAt = Get-Date
$warnings = New-Object System.Collections.Generic.List[string]

function Write-Step([string] $Message) {
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Warn([string] $Message) {
    $warnings.Add($Message)
    Write-Host "  ! $Message" -ForegroundColor Yellow
}

function Stop-Build([string] $Message) {
    Write-Host ''
    Write-Host "BUILD STOPPED: $Message" -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------- 1. checks

Write-Step 'Checking prerequisites'

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { Stop-Build 'The .NET SDK is not on PATH.' }

$sdkVersion = (& dotnet --version).Trim()
Write-Host "  .NET SDK $sdkVersion"

if (-not $sdkVersion.StartsWith('10.')) {
    Write-Warn "KidShell targets .NET 10; this SDK reports $sdkVersion."
}

# The version is authoritative in Directory.Build.props. Read it rather than
# accepting one as a parameter, so an artifact cannot be labelled differently
# from what is inside it.
[xml] $buildProps = Get-Content (Join-Path $repoRoot 'Directory.Build.props')

# XPath rather than property access: Set-StrictMode turns a missing property
# into a terminating error, and PropertyGroup is a collection whose members
# differ.
function Get-BuildProperty([string] $Name) {
    $node = $buildProps.SelectSingleNode("/Project/PropertyGroup/$Name")
    if (-not $node) { Stop-Build "Directory.Build.props has no $Name." }
    return $node.InnerText.Trim()
}

$version = Get-BuildProperty 'KidShellVersion'
$prerelease = Get-BuildProperty 'KidShellPrerelease'
$packageVersion = Get-BuildProperty 'KidShellPackageVersion'

$productVersion = if ($prerelease) { "$version-$prerelease" } else { $version }
Write-Host "  KidShell $productVersion (package $packageVersion)"

# The manifest and the build props must agree. A package whose manifest says
# one version while the binaries say another is how an update check starts
# lying.
$manifestPath = Join-Path $repoRoot 'src\KidShell.App\Package.appxmanifest'
[xml] $manifest = Get-Content $manifestPath
$identity = $manifest.SelectSingleNode("//*[local-name()='Identity']")
if (-not $identity) { Stop-Build 'Package.appxmanifest has no Identity element.' }
$manifestVersion = $identity.GetAttribute('Version')

if ($manifestVersion -ne $packageVersion) {
    Stop-Build "Package.appxmanifest says $manifestVersion but Directory.Build.props says $packageVersion."
}
Write-Host "  Manifest version agrees: $manifestVersion"

# ------------------------------------------------------------ 2. repo state

Write-Step 'Checking repository state'

$gitAvailable = $null -ne (Get-Command git -ErrorAction SilentlyContinue)
$commit = 'unknown'
$branch = 'unknown'
$isDirty = $false

if ($gitAvailable) {
    Push-Location $repoRoot
    try {
        $commit = (& git rev-parse --short HEAD 2>$null)
        $branch = (& git rev-parse --abbrev-ref HEAD 2>$null)
        $status = & git status --porcelain 2>$null
        $isDirty = -not [string]::IsNullOrWhiteSpace(($status | Out-String).Trim())
    } finally {
        Pop-Location
    }

    Write-Host "  $branch @ $commit"

    if ($isDirty) {
        if ($AllowDirty) {
            Write-Warn 'The working tree has uncommitted changes. The artifact is marked dirty.'
        } else {
            Stop-Build 'The working tree has uncommitted changes. Commit them, or pass -AllowDirty.'
        }
    }
} else {
    Write-Warn 'git is not available; the artifact cannot record a commit.'
}

# --------------------------------------------------------- 3. signing gate

Write-Step 'Checking signing material'

$signingCertificate = $null

if ($CertificateThumbprint) {
    $signingCertificate = Get-ChildItem -Path Cert:\CurrentUser\My |
        Where-Object { $_.Thumbprint -eq $CertificateThumbprint } |
        Select-Object -First 1

    if (-not $signingCertificate) {
        Stop-Build "No certificate with thumbprint $CertificateThumbprint in Cert:\CurrentUser\My."
    }

    if (-not $signingCertificate.HasPrivateKey) {
        Stop-Build 'That certificate has no private key, so it cannot sign.'
    }

    # Never printed: the subject can carry a real identity.
    Write-Host "  Certificate found, expires $($signingCertificate.NotAfter.ToString('yyyy-MM-dd'))"

    if ($signingCertificate.NotAfter -lt (Get-Date)) {
        Stop-Build 'That certificate has expired.'
    }
}

if ($Sign -and -not $signingCertificate) {
    # The boundary. "Sign if you can" is how unsigned builds reach users.
    Stop-Build '-Sign was requested but no signing material was supplied. Pass -CertificateThumbprint.'
}

if ($Sign -and $SkipTests) {
    Stop-Build 'Refusing to sign a build whose tests were skipped.'
}

$willSign = $null -ne $signingCertificate

if (-not $willSign) {
    Write-Host '  No signing material. The package will be marked UNSIGNED.' -ForegroundColor Yellow
}

# ------------------------------------------------------------- 4. build

Write-Step 'Restoring'
& dotnet restore (Join-Path $repoRoot 'KidShell.sln') | Out-Host
if ($LASTEXITCODE -ne 0) { Stop-Build 'Restore failed.' }

Write-Step "Building $Configuration / $Platform"
& dotnet build (Join-Path $repoRoot 'KidShell.sln') -c $Configuration -p:Platform=$Platform --no-restore | Out-Host
if ($LASTEXITCODE -ne 0) { Stop-Build 'Build failed.' }

# -------------------------------------------------------------- 5. tests

$testCount = 'skipped'

if ($SkipTests) {
    Write-Warn 'Tests were skipped.'
} else {
    Write-Step 'Testing'

    $testOutput = & dotnet test (Join-Path $repoRoot 'KidShell.sln') -c $Configuration --no-build 2>&1
    $testOutput | Out-Host

    if ($LASTEXITCODE -ne 0) { Stop-Build 'Tests failed. A release is not built from a red suite.' }

    $passed = 0
    foreach ($line in $testOutput) {
        if ("$line" -match 'Passed:\s+(\d+)') { $passed += [int] $Matches[1] }
    }
    $testCount = "$passed passed"
    Write-Host "  $testCount"
}

# ------------------------------------------------------------ 6. self-tests

Write-Step 'Running component self-tests'

foreach ($component in @('KidShell.SecurityHost', 'KidShell.Watchdog')) {
    $exe = Join-Path $repoRoot "src\$component\bin\$Configuration\net10.0-windows\$component.exe"

    if (-not (Test-Path $exe)) {
        $exe = Join-Path $repoRoot "src\$component\bin\$Platform\$Configuration\net10.0-windows\$component.exe"
    }

    if (Test-Path $exe) {
        # These install, start and change nothing; they prove the binaries run
        # and reject bad input.
        & $exe --self-test | Out-Host
        if ($LASTEXITCODE -ne 0) { Stop-Build "$component self-test failed." }
    } else {
        Write-Warn "$component was not found; its self-test did not run."
    }
}

# ------------------------------------------------------------- 7. package

Write-Step 'Packaging'

$stamp = $startedAt.ToString('yyyyMMdd-HHmmss')
$label = if ($willSign) { $productVersion } else { "$productVersion-UNSIGNED" }
$outputDir = Join-Path $artifactRoot "kidshell-$label-$Platform-$stamp"

New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

$appProject = Join-Path $repoRoot 'src\KidShell.App\KidShell.App.csproj'
# The trailing separator matters: MSBuild concatenates AppxPackageDir with the
# package folder name, so without it the output lands in "packageKidShell.App_..."
# instead of "package\KidShell.App_...".
$packageDir = (Join-Path $outputDir 'package') + [IO.Path]::DirectorySeparatorChar

& dotnet publish $appProject `
    -c $Configuration `
    -p:Platform=$Platform `
    -p:RuntimeIdentifier=$(if ($Platform -eq 'x64') { 'win-x64' } else { 'win-arm64' }) `
    -p:AppxPackageDir=$packageDir `
    -p:UapAppxPackageBuildMode=SideloadOnly `
    -p:AppxPackageSigningEnabled=false `
    -p:GenerateAppxPackageOnBuild=true `
    --no-restore | Out-Host

if ($LASTEXITCODE -ne 0) {
    Write-Warn 'MSIX packaging did not complete. Copying the unpackaged build instead.'

    $binDir = Join-Path $repoRoot "src\KidShell.App\bin\$Platform\$Configuration\net10.0-windows10.0.26100.0"

    if (Test-Path $binDir) {
        Copy-Item -Path $binDir -Destination (Join-Path $outputDir 'unpackaged') -Recurse -Force
    }
}

# Helper and watchdog ship alongside the package: the installer places them,
# and the security transaction registers the watchdog only on a target device.
$toolsOut = Join-Path $outputDir 'tools'
New-Item -ItemType Directory -Path $toolsOut -Force | Out-Null

foreach ($component in @('KidShell.SecurityHost', 'KidShell.Watchdog', 'KidShell.Recovery')) {
    $dir = Join-Path $repoRoot "src\$component\bin\$Configuration\net10.0-windows"

    if (Test-Path $dir) {
        Copy-Item -Path $dir -Destination (Join-Path $toolsOut $component) -Recurse -Force
    }
}

# --------------------------------------------------------------- 8. sign

$signedFiles = @()

if ($willSign) {
    Write-Step 'Signing'

    $signtool = Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1

    if (-not $signtool) { Stop-Build 'signtool.exe was not found. Install the Windows SDK.' }

    $toSign = Get-ChildItem -Path $outputDir -Include *.msix, *.msixbundle, *.exe -Recurse -File

    foreach ($file in $toSign) {
        # /fd SHA256 and an RFC 3161 timestamp, so the signature outlives the
        # certificate. The thumbprint selects the key from the store; no
        # password is passed, echoed or logged.
        & $signtool.FullName sign `
            /sha1 $CertificateThumbprint `
            /fd SHA256 `
            /tr $TimestampUrl `
            /td SHA256 `
            /q `
            $file.FullName

        if ($LASTEXITCODE -ne 0) { Stop-Build "Signing failed for $($file.Name)." }

        # Verified rather than assumed: a signing tool that returns success and
        # produces an unverifiable signature is exactly what this check is for.
        & $signtool.FullName verify /pa /q $file.FullName

        if ($LASTEXITCODE -ne 0) { Stop-Build "The signature on $($file.Name) does not verify." }

        $signedFiles += $file.Name
    }

    Write-Host "  Signed and verified $($signedFiles.Count) file(s)."
}

# ----------------------------------------------------------- 9. checksums

Write-Step 'Computing checksums'

$checksumPath = Join-Path $outputDir 'SHA256SUMS.txt'
$files = Get-ChildItem -Path $outputDir -Recurse -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' }

$lines = foreach ($file in $files) {
    $hash = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $relative = $file.FullName.Substring($outputDir.Length + 1)
    "$hash  $relative"
}

$lines | Set-Content -Path $checksumPath -Encoding utf8
Write-Host "  $($files.Count) file(s) hashed."

# -------------------------------------------------------- 10. release note

$installNote = @"
KidShell $productVersion
========================

Build:        $Configuration / $Platform
Commit:       $commit$(if ($isDirty) { ' (DIRTY WORKING TREE)' })
Built:        $($startedAt.ToString('yyyy-MM-dd HH:mm:ss'))
Tests:        $testCount
Signing:      $(if ($willSign) { "signed and verified ($($signedFiles.Count) file(s))" } else { 'UNSIGNED' })

$(if (-not $willSign) { @"
THIS BUILD IS NOT SIGNED AND IS NOT FIT FOR DISTRIBUTION
--------------------------------------------------------
An unsigned MSIX installs only on a machine with developer mode enabled or
with its certificate manually trusted. SmartScreen will warn, correctly.
Automatic updates stay disabled while signing cannot be proven, because an
updater that installs unsigned packages is remote code execution with a
friendly name.

Do not give this package to anybody as a release.
"@ })

PREREQUISITES
-------------
  Windows 10 version 2004 (build 19041) or later
  .NET 10 desktop runtime (included in the package)
  Windows App SDK 2.5.1 runtime

WHAT IS IN HERE
---------------
  package\        The MSIX, if packaging succeeded
  tools\          The elevated helper, the watchdog and the recovery tool
  SHA256SUMS.txt  Checksums for everything above

WINDOWS LOCKDOWN
----------------
Installing KidShell changes nothing about Windows. Security is applied only
when a parent completes secure setup on a device they have chosen, and every
change is written to a recovery manifest first.

See docs\DEDICATED-DEVICE-VALIDATION.md before running that on real hardware.
"@

$installNote | Set-Content -Path (Join-Path $outputDir 'README.txt') -Encoding utf8

# ------------------------------------------------------------- 11. report

$elapsed = (Get-Date) - $startedAt

Write-Host ''
Write-Host '========================================================' -ForegroundColor Green
Write-Host " KidShell $productVersion" -ForegroundColor Green
Write-Host '========================================================' -ForegroundColor Green
Write-Host "  Configuration : $Configuration / $Platform"
Write-Host "  Commit        : $commit$(if ($isDirty) { ' (dirty)' })"
Write-Host "  Tests         : $testCount"
Write-Host "  Signing       : $(if ($willSign) { 'signed and verified' } else { 'UNSIGNED - development only' })"
Write-Host "  Artifacts     : $outputDir"
Write-Host "  Files         : $($files.Count)"
Write-Host "  Elapsed       : $($elapsed.ToString('mm\:ss'))"

if ($warnings.Count -gt 0) {
    Write-Host ''
    Write-Host "  Warnings ($($warnings.Count)):" -ForegroundColor Yellow
    foreach ($warning in $warnings) { Write-Host "    - $warning" -ForegroundColor Yellow }
}

if (-not $willSign) {
    Write-Host ''
    Write-Host '  THIS BUILD IS UNSIGNED AND IS NOT FIT FOR DISTRIBUTION.' -ForegroundColor Yellow
}

Write-Host ''
exit 0
