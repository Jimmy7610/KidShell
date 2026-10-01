<#
.SYNOPSIS
    Assembles the final report from the stage evidence.

.DESCRIPTION
    STRICTLY READ-ONLY, and deliberately not clever: it reads the *.stage.json
    files the stage scripts wrote and hands them to the decision tool, which
    derives each stage's status and the overall outcome.

    The report is assembled from evidence rather than written by hand, because a
    handwritten summary of a security validation is a summary somebody will be
    optimistic in at 23:00 on the second hour. A stage with no evidence is NOT
    RUN; a stage with one failure fails; a run with anything unlooked-at is
    INCOMPLETE and not a pass.

    The exit code is non-zero unless the run actually passed, so a build log
    cannot show a finished-looking incomplete run.
#>
[CmdletBinding()]
param(
    [string] $RunPath,
    [string] $OutFile
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'KidShellValidation.psm1') -Force

if (-not $RunPath) {
    $latest = Get-LatestValidationRun

    if (-not $latest) {
        throw 'No validation run was found. Run 01-capture-baseline.ps1 first.'
    }

    $RunPath = $latest.FullName
}

if (-not (Test-Path $RunPath)) {
    throw "No validation run directory at $RunPath."
}

$arguments = @('report', '--run', $RunPath)
if ($OutFile) { $arguments += @('--out', $OutFile) }

$result = Invoke-ValidationTool -Arguments $arguments
$result.Output | ForEach-Object { Write-Host $_ }

Write-Host ''

if ($result.Accepted) {
    Write-Host 'The run PASSED every required stage.' -ForegroundColor Green
}
else {
    Write-Host 'The run did not pass. Stages that failed or did not run are listed above.' -ForegroundColor Yellow
}

exit $result.ExitCode
