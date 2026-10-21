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
        & '.\src\PlainCraftDownloader.App\bin\Release\net48\PlainCraftDownloader.exe' --ui-checks artifacts/ui-checks-0.45.json | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'UI workflow checks failed.' }
        $uiChecks = Get-Content -LiteralPath artifacts/ui-checks-0.45.json -Raw | ConvertFrom-Json
        if (!$uiChecks.Succeeded -or $uiChecks.Scope -ne 'all' -or $uiChecks.Count -ne 561) { throw 'UI workflow checks failed.' }
    }
    $distribution = Join-Path $PSScriptRoot 'dist\PlainCraftDownloader'
    New-Item -ItemType Directory -Path $distribution -Force | Out-Null
    foreach ($name in @('PlainCraftDownloader.exe', 'PlainCraftDownloader.exe.config', 'PlainCraftDownloader.Core.dll', 'Newtonsoft.Json.dll')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "src\PlainCraftDownloader.App\bin\Release\net48\$name") -Destination (Join-Path $distribution $name) -Force
    }
    foreach ($name in @('LICENCE', 'README.md', 'SOURCE_REFERENCES.md', 'THIRD_PARTY_NOTICES.md', 'VALIDATION.md', 'PATCH_MECHANISMS.md', 'VS2026_DEBUG.md')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $distribution $name) -Force
    }
    Compress-Archive -LiteralPath $distribution -DestinationPath (Join-Path $PSScriptRoot 'dist\PlainCraftDownloader-0.45-preview.zip') -Force
    Get-Item -LiteralPath (Join-Path $distribution 'PlainCraftDownloader.exe') | Select-Object FullName, Length
} finally {
    Pop-Location
}
