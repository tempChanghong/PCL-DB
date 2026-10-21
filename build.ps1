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
        & '.\src\PclDownloadBridge.App\bin\Release\net48\PclDownloadBridge.exe' --ui-checks artifacts/ui-checks-0.57.json | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'UI workflow checks failed.' }
        $uiChecks = Get-Content -LiteralPath artifacts/ui-checks-0.57.json -Raw | ConvertFrom-Json
        if (!$uiChecks.Succeeded -or $uiChecks.Scope -ne 'all' -or $uiChecks.Count -ne 658) { throw 'UI workflow checks failed.' }
        if ($Package) { & './tools/verify-packaging.ps1' }
    }
    if ($Package) {
        Import-Module (Join-Path $PSScriptRoot 'tools/PackageTools.psm1') -Force
        $result = New-PclDbPackage -ProjectRoot $PSScriptRoot
        Write-Output "PASS package $($result.Version): $($result.FileCount) files verified."
        Write-Output "Package: $($result.PackagePath)"
        Write-Output "Report: $($result.ResultPath)"
    }
} finally {
    Pop-Location
}
