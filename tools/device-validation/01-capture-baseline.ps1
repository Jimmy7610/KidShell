<#
.SYNOPSIS
    Captures the machine state before anything is validated, and starts a run.

.DESCRIPTION
    STRICTLY READ-ONLY. It calls tools\audit-windows-state.ps1, which contains
    no Set-, New-, Remove-, Enable- or Disable- cmdlet anywhere in it - the tool
    used to prove a machine is unchanged must not be capable of changing it.

    Creates the run directory everything else writes into, and records the
    baseline hash. Step 15 captures the same thing again; the two hashes are
    what let an operator account for every difference.
#>
[CmdletBinding()]
param(
    [string] $ConfigPath,
    [string] $EvidenceRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'KidShellValidation.psm1') -Force

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$config = Import-ValidationConfig -Path $ConfigPath

if (-not $EvidenceRoot -and $config -and $config.EvidenceDirectory) {
    $EvidenceRoot = $config.EvidenceDirectory
}

$run = New-ValidationRun -EvidenceRoot $EvidenceRoot
$audit = Join-Path $repoRoot 'tools\audit-windows-state.ps1'

if (-not (Test-Path $audit)) {
    throw "The machine-state audit was not found at $audit."
}

$before = Join-Path $run.Path 'before-machine-state.txt'

& powershell -NoProfile -ExecutionPolicy Bypass -File $audit -Out $before | Out-Null

$hash = (Get-FileHash $before -Algorithm SHA256).Hash

$gitSha = ''
try { $gitSha = (& git -C $repoRoot rev-parse HEAD 2>$null) } catch { }

$cv = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'

function Reg([string] $name) {
    try { return [string](Get-ItemProperty -Path $cv -Name $name -ErrorAction Stop).$name }
    catch { return '' }
}

# run.json is what the report generator reads for its header.
@{
    runId          = $run.RunId
    machineName    = $env:COMPUTERNAME
    windowsEdition = (Reg 'ProductName')
    windowsBuild   = (Reg 'CurrentBuild')
    gitSha         = ([string]$gitSha).Trim()
    startedUtc     = (Get-Date).ToUniversalTime().ToString('u')
    baselineHash   = $hash
} | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $run.Path 'run.json') -Encoding utf8

Write-Host ''
Write-Host '===== Baseline captured =====' -ForegroundColor Cyan
Write-Host ("Run      : {0}" -f $run.RunId)
Write-Host ("Evidence : {0}" -f $run.Path)
Write-Host ("BEFORE   : {0}" -f $hash)
Write-Host ''
Write-Host 'Pass this run path to the other scripts with -RunPath.' -ForegroundColor Yellow
Write-Host 'Nothing was changed.' -ForegroundColor Green

return $run
