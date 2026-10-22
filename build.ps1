param([switch]$SkipTests, [switch]$Package)
$ErrorActionPreference = 'Stop'
Push-Location -LiteralPath $PSScriptRoot
try {
    dotnet build PclDownloadBridge.sln -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (!$SkipTests) {
        & './tools/verify-pcl-ui.ps1'
        & '.\tests\PclDownloadBridge.Tests\bin\Release\net48\PclDownloadBridge.Tests.exe'
        if ($LASTEXITCODE -ne 0) { throw 'Fixture checks failed.' }
        New-Item -ItemType Directory -Path artifacts -Force | Out-Null
        & '.\src\PclDownloadBridge.App\bin\Release\net48\PclDownloadBridge.exe' --ui-checks artifacts/ui-checks.json | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'UI workflow checks failed.' }
        $uiChecks = Get-Content -LiteralPath artifacts/ui-checks.json -Raw | ConvertFrom-Json
        if (!$uiChecks.Succeeded -or $uiChecks.Scope -ne 'all' -or $uiChecks.Count -ne 754) { throw 'UI workflow checks failed.' }
        & '.\src\PclDownloadBridge.App\bin\Release\net48\PclDownloadBridge.exe' --motion-checks artifacts/motion-checks.json | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Motion checks failed.' }
        $motion = Get-Content -LiteralPath artifacts/motion-checks.json -Raw | ConvertFrom-Json
        if (!$motion.Succeeded -or $motion.Scope -ne 'motion' -or $motion.Count -ne 358) { throw 'Motion checks failed.' }
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
