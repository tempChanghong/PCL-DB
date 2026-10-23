param(
    [string[]]$VersionIds = @('13w10b','13w10a','13w09c','13w09b','11w47a'),
    [switch]$Install,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $PSScriptRoot 'VerificationProcessTools.psm1')
Import-Module (Join-Path $PSScriptRoot 'PackageCandidateTools.psm1')
if (!$VersionIds.Count -or $VersionIds.Count -gt 32 -or @($VersionIds | Select-Object -Unique).Count -ne $VersionIds.Count) { throw 'Specify 1-32 distinct canonical version IDs.' }
foreach ($id in $VersionIds) { if ($id -notmatch '\A[A-Za-z0-9_.~+-]+\z') { throw "Invalid version ID: $id" } }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root ('artifacts/supplemental-verification/' + [Guid]::NewGuid().ToString('N')) }
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if ((Test-Path -LiteralPath $output) -and (!(Test-Path -LiteralPath $output -PathType Container) -or @(Get-ChildItem -LiteralPath $output -Force).Count)) { throw 'Output directory must be new or empty.' }
$source = Get-PclDbSourceState $root
dotnet build (Join-Path $root 'tests/PclDownloadBridge.Tests/PclDownloadBridge.Tests.vbproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Verification build failed.' }
$results = Join-Path $output 'results'
$arguments = '--verify-supplemental "' + $results + '" "' + [string]::Join(',', $VersionIds) + '"'
if ($Install) { $arguments += ' --install' }
$reportPath = Join-Path $results 'report.json'
$run = Invoke-PclDbVerificationProcess -Name 'supplemental candidate verification' -FilePath (Join-Path $root 'tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe') -Arguments $arguments -WorkingDirectory $root -StdoutPath (Join-Path $output 'stdout.log') -StderrPath (Join-Path $output 'stderr.log') -ExpectedOutputPaths @($reportPath) -TimeoutSeconds 840
Assert-PclDbSourceState $source (Get-PclDbSourceState $root)
$report = Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json
if (!$report.succeeded -or $report.installationRequested -ne [bool]$Install -or @($report.records).Count -ne $VersionIds.Count -or @($report.records | Where-Object { !$_.succeeded }).Count) { throw 'Incomplete supplemental verification report.' }
[ordered]@{Source=$source; Process=$run; CoreSHA256=(Get-FileHash -LiteralPath (Join-Path $root 'tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Core.dll') -Algorithm SHA256).Hash; ReportSHA256=(Get-FileHash -LiteralPath $reportPath -Algorithm SHA256).Hash; InstallationRequested=[bool]$Install; GameLaunched=$false; PclGuiVerified=$false} | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $output 'receipt.json') -Encoding UTF8
Get-Content -LiteralPath $run.Stdout -Tail ($VersionIds.Count + 1)
Write-Output ('Verification report: ' + $reportPath)
