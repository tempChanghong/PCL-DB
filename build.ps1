param([switch]$SkipTests, [switch]$Package)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'tools/VerificationTools.psm1')
Import-Module (Join-Path $PSScriptRoot 'tools/VerificationProcessTools.psm1')
Push-Location -LiteralPath $PSScriptRoot
try {
    dotnet build PclDownloadBridge.sln -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (!$SkipTests) {
        & './tools/verify-pcl-ui.ps1'
        & './tools/verify-validation-contracts.ps1'
        & './tools/verify-verification-timeouts.ps1'
        & './tools/verify-verification-process.ps1'
        New-Item -ItemType Directory -Path artifacts -Force | Out-Null
        $core = Invoke-PclDbVerificationProcess -Name 'core fixtures' -FilePath (Join-Path $PSScriptRoot 'tests/PclDownloadBridge.Tests/bin/Release/net48/PclDownloadBridge.Tests.exe') -WorkingDirectory $PSScriptRoot -StdoutPath (Join-Path $PSScriptRoot 'artifacts/core-checks.stdout.log') -StderrPath (Join-Path $PSScriptRoot 'artifacts/core-checks.stderr.log') -TimeoutSeconds 900
        Get-Content -LiteralPath $core.Stdout -Tail 2
        $app = Join-Path $PSScriptRoot 'src/PclDownloadBridge.App/bin/Release/net48/PclDownloadBridge.exe'
        $appHash = (Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash
        foreach ($scope in @('all','motion','pack-search')) {
            $policy = Get-PclDbCheckPolicy $scope
            $stem = if ($scope -eq 'all') { 'ui' } else { $scope }
            $path = Join-Path $PSScriptRoot ('artifacts/' + $stem + '-checks.json')
            $run = Invoke-PclDbVerificationProcess -Name ($scope + ' checks') -FilePath $app -Arguments ($policy.Mode + ' "' + $path + '"') -WorkingDirectory $PSScriptRoot -StdoutPath ($path + '.stdout.log') -StderrPath ($path + '.stderr.log') -ExpectedOutputPaths @($path) -TimeoutSeconds $policy.TimeoutSeconds
            if ($run.ExecutableSHA256 -cne $appHash) { throw 'Application changed between verification scopes.' }
            $report = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
            Assert-PclDbCheckReport $report $scope -ExecutableSHA256 $appHash
            Write-Output "PASS $scope checks: $($report.Count)."
        }
    }
    if ($Package) {
        Import-Module (Join-Path $PSScriptRoot 'tools/PackageTools.psm1') -Force
        $candidate = & './tools/verify-single-exe.ps1' -PassThru
        if (!$SkipTests) { & './tools/verify-packaging.ps1' -CandidateResultPath $candidate.ResultPath }
        $result = New-PclDbPackage -ProjectRoot $PSScriptRoot -CandidateResultPath $candidate.ResultPath
        Write-Output "PASS package $($result.Version): $($result.FileCount) files verified."
        Write-Output "Package: $($result.PackagePath)"
        Write-Output "Single EXE: $($result.StandalonePath)"
        Write-Output "Build manifest: $($result.ManifestPath)"
        Write-Output "Report: $($result.ResultPath)"
    }
} finally {
    Pop-Location
}
