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
    Require production signing. Fails if no signing material is supplied.

.PARAMETER LabSign
    Sign for a dedicated validation device with a local lab certificate.
    The resulting bundle is channel DedicatedLabSigned and is never production.

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

.EXAMPLE
    .\tools\build-release.ps1 -LabSign -CertificateThumbprint ABC123...
#>

[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',

    [ValidateSet('x64', 'ARM64')]
    [string] $Platform = 'x64',

    [switch] $Sign,

    [switch] $LabSign,

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
$commitFull = ''
$branch = 'unknown'
$isDirty = $false

if ($gitAvailable) {
    Push-Location $repoRoot
    try {
        $commit = (& git rev-parse --short HEAD 2>$null)

        # The FULL hash as well as the short one. The release manifest carries
        # the full hash and the installer refuses a bundle without one: "which
        # code is on that device" has to have an answer, and a short hash is an
        # abbreviation rather than an identity.
        $commitFull = (& git rev-parse HEAD 2>$null)
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

if ($Sign -and $LabSign) {
    Stop-Build '-Sign and -LabSign are mutually exclusive. Production signing and lab signing are different trust boundaries.'
}

if (($Sign -or $LabSign) -and -not $signingCertificate) {
    # The boundary. "Sign if you can" is how an unsigned build reaches users.
    Stop-Build 'Signing was requested but no signing material was supplied. Pass -CertificateThumbprint.'
}

if ($CertificateThumbprint -and -not ($Sign -or $LabSign)) {
    Stop-Build 'A certificate was supplied without -Sign or -LabSign. Refusing to guess which trust boundary you intended.'
}

if (($Sign -or $LabSign) -and $SkipTests) {
    Stop-Build 'Refusing to sign a build whose tests were skipped.'
}

$willSign = [bool]($Sign -or $LabSign)

if ($willSign) {
    $manifestPublisher = $identity.GetAttribute('Publisher')
    if (-not [string]::Equals($signingCertificate.Subject, $manifestPublisher, [StringComparison]::OrdinalIgnoreCase)) {
        Stop-Build "The certificate subject does not match Package.appxmanifest Publisher. Certificate: '$($signingCertificate.Subject)'. Manifest: '$manifestPublisher'."
    }

    if ($LabSign) {
        Write-Host '  LAB SIGNING selected. This certificate grants no production release authority.' -ForegroundColor Yellow
    }
}
else {
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

    $testOutput = & dotnet test (Join-Path $repoRoot 'KidShell.sln') -c $Configuration -p:Platform=$Platform --no-build 2>&1
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
$label = if ($LabSign) { "$productVersion-LABSIGNED" } elseif ($Sign) { $productVersion } else { "$productVersion-UNSIGNED" }
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

# ------------------------------------------------------ 7b. the components
#
# THE DEFECT THIS BLOCK REPLACES
#
# It used to copy from src\<component>\bin\<Configuration>\net10.0-windows,
# while the build above runs with -p:Platform=x64 and MSBuild writes to
# bin\x64\<Configuration>. On a developer's machine the unplatformed folder
# existed from some older build, so the copy SUCCEEDED and shipped binaries a
# week older than the commit being released - including a SecurityHost from
# before the broker was rewritten. On a clean checkout the folder was absent,
# Test-Path was false, and the bundle simply had no helper in it.
#
# Neither case produced an error, and the bundle's own README claimed all three
# components were present either way.
#
# So: the path includes the platform, the component list comes from the
# layout, and a missing output is a BUILD FAILURE rather than a skipped copy.
# KidShell.DeviceValidation is in the list now too, which the old one predated.

$componentsOut = Join-Path $outputDir 'components'
New-Item -ItemType Directory -Path $componentsOut -Force | Out-Null

# The component list and every path come from InstallationLayout, through the
# tool the solution build just produced. Not duplicated here: two definitions of
# where a component's output lives is exactly the arrangement that shipped
# week-old binaries, and one of them would eventually have been updated alone.
$layoutTool = Join-Path $repoRoot `
    "src\KidShell.DeviceValidation\bin\$Platform\$Configuration\net10.0-windows\KidShell.DeviceValidation.exe"

if (-not (Test-Path $layoutTool)) {
    Stop-Build "KidShell.DeviceValidation.exe was not built at '$layoutTool'. The bundle's layout cannot be resolved."
}

$componentList = @(& $layoutTool layout --what components)

if ($LASTEXITCODE -ne 0 -or $componentList.Count -eq 0) {
    Stop-Build 'The installation layout could not be read from KidShell.DeviceValidation.exe.'
}

# EVERY path resolved BEFORE the copy loop starts.
#
# The tool lives in one of the directories this loop copies, and an executable
# that has just exited can still be briefly locked - by the antimalware scanner,
# usually. Invoking it from inside the loop made the copy of its own folder fail
# intermittently: once in three runs here, which is exactly the frequency that
# gets written off as a fluke.
$componentPaths = [ordered]@{}

foreach ($component in $componentList) {
    $relative = (& $layoutTool layout --what build-output `
        --component $component --configuration $Configuration --platform $Platform)

    if ($LASTEXITCODE -ne 0) {
        Stop-Build "The build output path for $component could not be resolved."
    }

    $componentPaths[$component] = Join-Path $repoRoot $relative
}

$bundledComponents = @()

foreach ($component in $componentList) {
    $dir = $componentPaths[$component]

    if (-not (Test-Path $dir)) {
        Stop-Build "The build output for $component is not at '$dir'. The bundle would have shipped without it."
    }

    $exe = Join-Path $dir "$component.exe"

    if (-not (Test-Path $exe)) {
        Stop-Build "$component built no executable at '$exe'."
    }

    # Freshness, not just presence. A folder that exists is not evidence that
    # the build just ran: that is exactly how a week-old binary shipped.
    $age = (Get-Date) - (Get-Item $exe).LastWriteTime

    if ($age.TotalHours -gt 6) {
        Stop-Build "$component.exe is $([int]$age.TotalHours) hour(s) old. The build did not produce it; refusing to bundle a stale binary."
    }

    Copy-Item -Path $dir -Destination (Join-Path $componentsOut $component) -Recurse -Force

    $bundledComponents += $component
    Write-Host "  $component ($([int]((Get-ChildItem $dir -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)) KB)"
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

    $toSign = if ($LabSign) {
        # Lab signing exists to make the real MSIX deployable on the dedicated
        # child account. It does not pretend the standalone service binaries
        # have production Authenticode signatures.
        @(
            Get-ChildItem -Path (Join-Path $outputDir 'package') -Include *.msix, *.msixbundle -Recurse -File |
                Where-Object { $_.Name -like 'KidShell.App_*' }
        )
    }
    else {
        @(Get-ChildItem -Path $outputDir -Include *.msix, *.msixbundle, *.exe -Recurse -File)
    }

    if ($toSign.Count -eq 0) {
        Stop-Build 'Signing was requested but there were no KidShell files to sign.'
    }

    foreach ($file in $toSign) {
        # /fd SHA256 and an RFC 3161 timestamp, so the signature outlives the
        # certificate. The thumbprint selects the key from the store; no
        # password is passed, echoed or logged.
        if ($LabSign) {
            # A local lab certificate deliberately has no production timestamp
            # dependency. Its short validity is part of the lab boundary.
            & $signtool.FullName sign `
                /sha1 $CertificateThumbprint `
                /fd SHA256 `
                /q `
                $file.FullName
        }
        else {
            & $signtool.FullName sign `
                /sha1 $CertificateThumbprint `
                /fd SHA256 `
                /tr $TimestampUrl `
                /td SHA256 `
                /q `
                $file.FullName
        }

        if ($LASTEXITCODE -ne 0) { Stop-Build "Signing failed for $($file.Name)." }

        # Verified rather than assumed: a signing tool that returns success and
        # produces an unverifiable signature is exactly what this check is for.
        & $signtool.FullName verify /pa /q $file.FullName

        if ($LASTEXITCODE -ne 0) { Stop-Build "The signature on $($file.Name) does not verify." }

        $signedFiles += $file.Name
    }

    Write-Host "  Signed and verified $($signedFiles.Count) file(s)."
}

# ------------------------------------------------------- 8b. install scripts
#
# The bundle carries its own installer. A bundle that has to be matched up with
# the right version of a script from a checkout is a bundle somebody will pair
# with the wrong one.

Write-Step 'Adding the install scripts'

$installOut = Join-Path $outputDir 'install'
New-Item -ItemType Directory -Path $installOut -Force | Out-Null

foreach ($script in Get-ChildItem (Join-Path $repoRoot 'tools\install') -Filter '*.ps1' -File) {
    Copy-Item $script.FullName (Join-Path $installOut $script.Name)
}

# The validation toolset travels with it, since the dedicated-device procedure
# needs both and they have to be the same version.
Copy-Item -Path (Join-Path $repoRoot 'tools\device-validation') `
    -Destination (Join-Path $outputDir 'device-validation') -Recurse -Force

Copy-Item -Path (Join-Path $repoRoot 'tools\audit-windows-state.ps1') `
    -Destination (Join-Path $outputDir 'device-validation\audit-windows-state.ps1') -Force

# The documentation travels with it. The operator on the dedicated machine needs
# the runbook more than anybody, and telling them to go and find a checkout for
# it is how a procedure gets done from memory.
Copy-Item -Path (Join-Path $repoRoot 'docs') -Destination (Join-Path $outputDir 'docs') -Recurse -Force

# --------------------------------------------------- 9. the release manifest
#
# Every install step reads this rather than looking around a directory and
# copying what it finds. A file in the bundle the manifest does not name is not
# installed - it is reported, because something put it there.

Write-Step 'Writing the release manifest'

$manifestsOut = Join-Path $outputDir 'manifests'
New-Item -ItemType Directory -Path $manifestsOut -Force | Out-Null

if ($LabSign) {
    # Only the public half travels with the bundle. The private key remains
    # non-exported in the developer's CurrentUser certificate store.
    $labCerPath = Join-Path $manifestsOut 'KidShell-DedicatedLab-Public.cer'
    Export-Certificate -Cert $signingCertificate -FilePath $labCerPath -Force | Out-Null
    Write-Host "  Lab public certificate: $labCerPath"
}

$channel = if ($LabSign) { 'DedicatedLabSigned' } elseif ($Sign) { 'Production' } else { 'DedicatedLabUnsigned' }

$componentEntries = @()

foreach ($component in $bundledComponents) {
    $componentDir = Join-Path $componentsOut $component

    $fileEntries = @(
        Get-ChildItem $componentDir -Recurse -File | ForEach-Object {
            [pscustomobject]@{
                path   = $_.FullName.Substring($componentDir.Length + 1)
                sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                bytes  = $_.Length
            }
        }
    )

    $componentEntries += [pscustomobject]@{
        component  = ($component -replace '^KidShell\.', '')
        bundlePath = "components\$component"
        files      = $fileEntries
    }
}

# The packaged app, when MSIX packaging produced one. Named in the manifest and
# deliberately NOT file-copied by the installer: an MSIX is registered.
$packageRoot = Join-Path $outputDir 'package'

if (Test-Path $packageRoot) {
    $packageFiles = @(
        Get-ChildItem $packageRoot -Recurse -File |
            Where-Object { $_.Extension -in @('.msix', '.msixbundle', '.appx', '.appxbundle', '.cer') } |
            ForEach-Object {
                [pscustomobject]@{
                    path   = $_.FullName.Substring($packageRoot.Length + 1)
                    sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                    bytes  = $_.Length
                }
            }
    )

    if ($packageFiles.Count -gt 0) {
        $componentEntries += [pscustomobject]@{
            component  = 'App'
            bundlePath = 'package'
            files      = $packageFiles
        }
    }
    else {
        Write-Warn 'No MSIX was produced, so the bundle has no App component and the installer will refuse it.'
    }
}

$expectedPaths = [ordered]@{}

foreach ($entry in $componentEntries) {
    $folder = 'KidShell.' + $entry.component
    if ($entry.component -eq 'App') { $folder = 'KidShell.App' }
    $expectedPaths[$entry.component] = "C:\Program Files\KidShell\$folder"
}

$releaseManifest = [ordered]@{
    schemaVersion      = 1
    product            = 'KidShell'
    version            = $version
    packageVersion     = $packageVersion
    prerelease         = $prerelease
    gitSha             = ([string]$commitFull).Trim()
    dirty              = [bool]$isDirty
    architecture       = $Platform
    buildConfiguration = $Configuration
    buildUtc           = $startedAt.ToUniversalTime().ToString('u')
    signed             = [bool]$willSign
    channel            = $channel
    packageIdentity    = $identity.GetAttribute('Name')
    components         = $componentEntries
    expectedInstallPaths = $expectedPaths
}

$releaseManifestPath = Join-Path $outputDir 'release-manifest.json'
$releaseManifest | ConvertTo-Json -Depth 8 | Set-Content -Path $releaseManifestPath -Encoding utf8

Write-Host "  $($componentEntries.Count) component(s), $(($componentEntries | ForEach-Object { $_.files.Count } | Measure-Object -Sum).Sum) file(s)"

# -------------------------------------------------- 9b. the signing manifest
#
# What MUST be signed before anything here is a production release. Written by
# the build rather than kept in prose, so it cannot drift from what the bundle
# actually contains.

$signingManifest = [ordered]@{
    schemaVersion = 1
    signedNow     = [bool]$willSign
    note          = 'Everything listed here must carry a valid Authenticode or MSIX signature before this bundle may be called a production release.'
    mustBeSigned  = @(
        [ordered]@{ what = 'MSIX / MSIXBundle'; how = 'SignTool with the package certificate'; signed = [bool]$willSign }
        [ordered]@{ what = 'KidShell.SecurityHost.exe'; how = 'Authenticode'; signed = [bool]$Sign }
        [ordered]@{ what = 'KidShell.Watchdog.exe'; how = 'Authenticode'; signed = [bool]$Sign }
        [ordered]@{ what = 'KidShell.Recovery.exe'; how = 'Authenticode'; signed = [bool]$Sign }
        [ordered]@{ what = 'KidShell.DeviceValidation.exe'; how = 'Authenticode'; signed = [bool]$Sign }
        [ordered]@{ what = 'every managed DLL shipped beside those executables'; how = 'Authenticode'; signed = $false }
    )
    notSignedAndWhy = @(
        [ordered]@{
            what = 'the PowerShell scripts under install\ and device-validation\'
            why  = 'They are NOT Authenticode-signed by this build, and this manifest does not claim they are. Signing them would need the same certificate and an execution policy that honours it; until then they are verified by the hashes in hashes.sha256, which is a weaker guarantee and is stated as one.'
        }
        [ordered]@{
            what = 'release-manifest.json and this file'
            why  = 'Covered by hashes.sha256 rather than signed. The hash file is what an operator checks before trusting either.'
        }
    )
}

$signingManifest | ConvertTo-Json -Depth 6 |
    Set-Content -Path (Join-Path $manifestsOut 'signing-manifest.json') -Encoding utf8

# ----------------------------------------------------------- 9c. checksums
#
# Covers release-manifest.json too. The document that decides what gets
# installed must not be the one thing nobody checked.

Write-Step 'Computing checksums'

$checksumPath = Join-Path $outputDir 'hashes.sha256'
$files = Get-ChildItem -Path $outputDir -Recurse -File | Where-Object { $_.Name -ne 'hashes.sha256' }

$lines = foreach ($file in $files) {
    $hash = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $relative = $file.FullName.Substring($outputDir.Length + 1)
    "$hash  $relative"
}

$lines | Set-Content -Path $checksumPath -Encoding utf8
Write-Host "  $($files.Count) file(s) hashed, including release-manifest.json."

# -------------------------------------------------------- 10. release note

$installNote = @"
KidShell $productVersion
========================

Build:        $Configuration / $Platform
Commit:       $commit$(if ($isDirty) { ' (DIRTY WORKING TREE)' })
Built:        $($startedAt.ToString('yyyy-MM-dd HH:mm:ss'))
Tests:        $testCount
Signing:      $(if ($LabSign) { "LAB-SIGNED and verified ($($signedFiles.Count) file(s))" } elseif ($Sign) { "PRODUCTION-SIGNED and verified ($($signedFiles.Count) file(s))" } else { 'UNSIGNED' })

$(if ($LabSign) { @"
THIS BUILD IS LAB-SIGNED AND IS NOT FIT FOR DISTRIBUTION
--------------------------------------------------------
The signature exists only so Windows can deploy the real KidShell package on
a dedicated validation device. The certificate is a local test trust anchor,
not production release authority.

Do not give this package or its lab certificate to anybody as a release.
"@ } elseif (-not $willSign) { @"

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
  package\               The MSIX, if packaging succeeded
  components\            SecurityHost, Watchdog, Recovery, DeviceValidation
  install\               Install-KidShellLab.ps1 and friends
  device-validation\     The dedicated-device validation toolset
  docs\                  The documentation, including the runbook below
  manifests\             signing metadata$(if ($LabSign) { ' + the PUBLIC lab certificate' } else { '' })
  release-manifest.json  What is in this bundle, with a digest for every file
  hashes.sha256          Covers everything above, including release-manifest.json

HOW TO INSTALL THIS ON A DEDICATED TEST DEVICE
----------------------------------------------
Read docs\DEDICATED-DEVICE-VALIDATION.md in THIS folder first - the whole
procedure is in there, Part 4 onwards. Then, on the test machine, in an elevated
PowerShell window:

  cd <this folder>\install
  powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-KidShellLab.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-KidShellLab.ps1 -Apply

The first command is a dry run and changes nothing. The second needs the
dedicated-device marker, the confirmation phrase and an elevated window, and
even then it installs FILES ONLY - no service, no account, no access list, no
AppLocker, no shell change. Enabling the security is a separate, reviewable
stage.

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
Write-Host "  Signing       : $(if ($LabSign) { 'LAB-SIGNED - dedicated device only' } elseif ($Sign) { 'PRODUCTION-SIGNED' } else { 'UNSIGNED - development only' })"
Write-Host "  Artifacts     : $outputDir"
Write-Host "  Files         : $($files.Count)"
Write-Host "  Elapsed       : $($elapsed.ToString('mm\:ss'))"

if ($warnings.Count -gt 0) {
    Write-Host ''
    Write-Host "  Warnings ($($warnings.Count)):" -ForegroundColor Yellow
    foreach ($warning in $warnings) { Write-Host "    - $warning" -ForegroundColor Yellow }
}

if ($LabSign) {
    Write-Host ''
    Write-Host '  THIS BUILD IS LAB-SIGNED AND IS NOT FIT FOR DISTRIBUTION.' -ForegroundColor Yellow
}
elseif (-not $willSign) {
    Write-Host ''
    Write-Host '  THIS BUILD IS UNSIGNED AND IS NOT FIT FOR DISTRIBUTION.' -ForegroundColor Yellow
}

Write-Host ''
exit 0
