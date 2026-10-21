param(
    [Parameter(Mandatory)][string]$PackagePath,
    [string]$DistributionPath,
    [string]$StandalonePath,
    [string]$ManifestPath,
    [string]$CandidateResultPath
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'PackageTools.psm1') -Force
$report = Test-PclDbPackage -ProjectRoot (Split-Path $PSScriptRoot -Parent) -PackagePath $PackagePath -DistributionPath $DistributionPath -StandalonePath $StandalonePath -ManifestPath $ManifestPath -CandidateResultPath $CandidateResultPath
$report | ConvertTo-Json -Depth 8
