param([string]$ReportDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
if (!$ReportDirectory) { $ReportDirectory = Join-Path $root 'artifacts/single-exe' }
$operation = Join-Path ([IO.Path]::GetFullPath($ReportDirectory)) ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
$build = Join-Path $operation 'build'
New-Item -ItemType Directory -Path $operation -Force | Out-Null

function Invoke-IsolatedCheck([string]$Mode, [string]$Name, [int]$ExpectedCount) {
    $path = Join-Path $operation ($Name + '.json')
    $options = @{FilePath=$candidate; WorkingDirectory=$cold; WindowStyle='Hidden'; PassThru=$true;
        ArgumentList=@($Mode, ('"' + $path + '"')); RedirectStandardOutput=(Join-Path $operation ($Name + '.stdout.log')); RedirectStandardError=(Join-Path $operation ($Name + '.stderr.log'))}
    $process = Start-Process @options
    try {
        # Retain the handle before exit; Windows PowerShell can otherwise lose ExitCode.
        $null = $process.Handle
        $clock = [Diagnostics.Stopwatch]::StartNew()
        while (!$process.WaitForExit(250)) {
            if ($clock.ElapsedMilliseconds -gt 180000) { $process.Kill(); $null = $process.WaitForExit(2000); throw "Isolated check timed out: $Name" }
        }
        $process.Refresh()
        $exitCode = $process.ExitCode
        if ($null -eq $exitCode -or $exitCode -ne 0) { throw "Isolated check failed: $Name (exit $exitCode). See $path" }
    } finally { $process.Dispose() }
    $report = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $scope = if ($Name -eq 'standalone') { 'standalone' } else { 'all' }
    if (!$report.Succeeded -or $report.Scope -ne $scope -or $report.Count -ne $ExpectedCount -or $report.Passed.Count -ne $ExpectedCount) { throw "Incomplete check report: $path" }
    return $report
}

Push-Location -LiteralPath $root
try {
    $buildLog = Join-Path $operation 'build.log'
    & dotnet build src/PclDownloadBridge.App/PclDownloadBridge.App.vbproj -c Release -p:PclDbSingleExecutable=true --artifacts-path $build --nologo *> $buildLog
    if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $buildLog -Tail 25; throw 'Single executable build failed.' }
    $executables = @(Get-ChildItem -LiteralPath (Join-Path $build 'bin/PclDownloadBridge.App') -Filter PclDownloadBridge.exe -File -Recurse)
    if ($executables.Count -ne 1) { throw 'Expected one application build output.' }
    $runtime = $executables[0].DirectoryName
    # The cold-start directory is outside the checkout and contains only the renamed EXE.
    # ASCII script source also works in Windows PowerShell 5.1; the directory is Unicode.
    $cold = Join-Path ([IO.Path]::GetTempPath()) ('PCL-DB ' + [char]0x5355 + [char]0x6587 + [char]0x4EF6 + ' ' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $cold | Out-Null
    $candidate = Join-Path $cold 'PCL-DB.exe'
    Copy-Item -LiteralPath $executables[0].FullName -Destination $candidate
    $standalone = Invoke-IsolatedCheck '--standalone-checks' 'standalone' 25
    $embedded = Get-Content -LiteralPath (Join-Path $operation 'embedded-content.json') -Raw | ConvertFrom-Json
    if ($embedded.Assemblies.Count -ne 2 -or $embedded.Documents.Count -ne 7) { throw 'Incomplete embedded content inventory.' }
    foreach ($assembly in $embedded.Assemblies) {
        if ($assembly.Name -notin @('PclDownloadBridge.Core','Newtonsoft.Json')) { throw 'Unexpected embedded dependency.' }
        $expected = (Get-FileHash -LiteralPath (Join-Path $runtime ($assembly.Name + '.dll')) -Algorithm SHA256).Hash
        if ($expected -ne $assembly.SHA256) { throw "Embedded DLL differs from this build: $($assembly.Name)" }
    }
    foreach ($document in $embedded.Documents) {
        if ($document.Name -notin @('LICENCE','README.md','SOURCE_REFERENCES.md','THIRD_PARTY_NOTICES.md','VALIDATION.md','PATCH_MECHANISMS.md','VS2026_DEBUG.md')) { throw 'Unexpected embedded document.' }
        if ((Get-FileHash -LiteralPath (Join-Path $root $document.Name) -Algorithm SHA256).Hash -ne $document.SHA256) { throw "Embedded documentation differs from source: $($document.Name)" }
    }
    $ui = Invoke-IsolatedCheck '--ui-checks' 'ui' 658
    if (@(Get-ChildItem -LiteralPath $cold -Force).Count -ne 1) { throw 'The isolated run left extra files beside the executable.' }
    $result = [ordered]@{Succeeded=$true; PowerShellVersion=$PSVersionTable.PSVersion.ToString(); Version=[Reflection.AssemblyName]::GetAssemblyName($candidate).Version.ToString(3);
        StandaloneChecks=$standalone.Count; UiChecks=$ui.Count; EmbeddedAssemblies=$embedded.Assemblies; EmbeddedDocuments=$embedded.Documents;
        CandidatePath=$candidate; CandidateSHA256=(Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash;
        ColdDirectoryContainsOnlyExe=$true; BuildDirectory=$build; ResultPath=(Join-Path $operation 'result.json'); Published=$false; DistributionUpdated=$false}
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $result.ResultPath -Encoding UTF8
    Write-Output "PASS single EXE: $($standalone.Count) standalone checks, $($ui.Count) UI checks; embedded bytes match the fresh build and seven source documents."
    Write-Output "Report: $($result.ResultPath)"
} catch {
    [pscustomobject]@{Succeeded=$false;Error=$_.Exception.ToString();Operation=$operation} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $operation 'failure.json') -Encoding UTF8
    throw
} finally { Pop-Location }
