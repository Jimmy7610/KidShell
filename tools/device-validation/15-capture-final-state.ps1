<#
.SYNOPSIS
    Captures the machine state after the run, and compares it with the baseline.

.DESCRIPTION
    STRICTLY READ-ONLY, through the same audit script as the baseline.

    On a DEDICATED DEVICE the two hashes are expected to DIFFER - validation
    applied real security, so the machine changed. Every difference has to
    correspond to a line in a recovery manifest, and the differences are printed
    here so an operator can account for them one by one.

    On a development machine the two must be IDENTICAL, because nothing was
    applied. Both expectations are reported rather than assumed, because which
    one holds depends on what the operator was doing.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $RunPath,
    [string] $ConfigPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'KidShellValidation.psm1') -Force

# From the context, for the same reason as 01-capture-baseline.ps1: the bundle
# keeps the audit script beside these ones.
$audit = Get-ValidationAuditScript

$before = Join-Path $RunPath 'before-machine-state.txt'
$after = Join-Path $RunPath 'after-machine-state.txt'

& powershell -NoProfile -ExecutionPolicy Bypass -File $audit -Out $after | Out-Null

$afterHash = (Get-FileHash $after -Algorithm SHA256).Hash

Write-Host ''
Write-Host '===== Final machine state =====' -ForegroundColor Cyan
Write-Host ("AFTER  : {0}" -f $afterHash)

if (-not (Test-Path $before)) {
    Write-Host 'There is no baseline in this run, so nothing can be compared.' -ForegroundColor Yellow
    return
}

$beforeHash = (Get-FileHash $before -Algorithm SHA256).Hash
$identical = ($beforeHash -eq $afterHash)

Write-Host ("BEFORE : {0}" -f $beforeHash)
Write-Host ("Identical: {0}" -f $identical)

if (-not $identical) {
    Write-Host ''
    Write-Host 'Differences. Each one must correspond to a line in a recovery manifest:' -ForegroundColor Yellow

    $differences = @(Compare-Object (Get-Content $before) (Get-Content $after))

    foreach ($difference in $differences) {
        $marker = '  BEFORE only:'
        if ($difference.SideIndicator -eq '=>') { $marker = '  AFTER only :' }

        Write-Host ("{0} {1}" -f $marker, $difference.InputObject)
    }

    Write-Host ''
    Write-Host ("{0} difference(s)." -f $differences.Count)
}
else {
    Write-Host ''
    Write-Host 'The machine is byte-for-byte what it was. Nothing was applied.' -ForegroundColor Green
}

Write-Evidence -RunPath $RunPath -Name 'machine-state.json' -Data @{
    beforeHash  = $beforeHash
    afterHash   = $afterHash
    identical   = $identical
    capturedUtc = (Get-Date).ToUniversalTime().ToString('u')
} | Out-Null

Write-Host ''
Write-Host "Evidence: $after"
