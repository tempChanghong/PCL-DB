param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Push-Location -LiteralPath $PSScriptRoot
try {
    dotnet build PlainCraftDownloader.sln -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (!$SkipTests) {
        & './tools/verify-pcl-ui.ps1'
        & '.\tests\PlainCraftDownloader.Tests\bin\Release\net48\PlainCraftDownloader.Tests.exe'
        if ($LASTEXITCODE -ne 0) { throw 'Fixture checks failed.' }
        New-Item -ItemType Directory -Path artifacts -Force | Out-Null
        & '.\src\PlainCraftDownloader.App\bin\Release\net48\PlainCraftDownloader.exe' --ui-checks artifacts/ui-checks-0.47.json | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'UI workflow checks failed.' }
        $uiChecks = Get-Content -LiteralPath artifacts/ui-checks-0.47.json -Raw | ConvertFrom-Json
        if (!$uiChecks.Succeeded -or $uiChecks.Scope -ne 'all' -or $uiChecks.Count -ne 561) { throw 'UI workflow checks failed.' }
        & './tools/verify-packaging.ps1'
    }
    Import-Module (Join-Path $PSScriptRoot 'tools/PackageTools.psm1') -Force
    $package = New-PcdPackage -ProjectRoot $PSScriptRoot
    Write-Output "PASS package $($package.Version): $($package.FileCount) files verified."
    Write-Output "Package: $($package.PackagePath)"
    Write-Output "Report: $($package.ResultPath)"
} finally {
    Pop-Location
}
