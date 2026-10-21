param(
    [Parameter(Mandatory)][string]$PackagePath,
    [string]$DistributionPath,
    [string]$RuntimeDirectory
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'PackageTools.psm1') -Force
$report = Test-PcdPackage -ProjectRoot (Split-Path $PSScriptRoot -Parent) -PackagePath $PackagePath -DistributionPath $DistributionPath -RuntimeDirectory $RuntimeDirectory
$report | ConvertTo-Json -Depth 6
