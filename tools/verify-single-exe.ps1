param([string]$ReportDirectory, [switch]$PassThru)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $PSScriptRoot 'PackageCandidateTools.psm1')
Import-Module (Join-Path $PSScriptRoot 'VerificationTools.psm1')
Import-Module (Join-Path $PSScriptRoot 'VerificationProcessTools.psm1')
if (!$ReportDirectory) { $ReportDirectory = Join-Path $root 'artifacts/single-exe' }
$operation = Join-Path ([IO.Path]::GetFullPath($ReportDirectory)) ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
$build = Join-Path $operation 'build'
New-Item -ItemType Directory -Path $operation -Force | Out-Null

function Invoke-IsolatedCheck([string]$Scope, [string]$Name) {
    $policy = Get-PclDbCheckPolicy $Scope
    $path = Join-Path $operation ($Name + '.json')
    $expected = @($path)
    if ($Scope -eq 'standalone') { $expected += Join-Path $operation 'embedded-content.json' }
    $null = Invoke-PclDbVerificationProcess -Name $Name -FilePath $candidate -Arguments ($policy.Mode + ' "' + $path + '"') -WorkingDirectory $cold -StdoutPath (Join-Path $operation ($Name + '.stdout.log')) -StderrPath (Join-Path $operation ($Name + '.stderr.log')) -ExpectedOutputPaths $expected -TimeoutSeconds $policy.TimeoutSeconds
    $report = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-PclDbCheckReport $report $Scope -ExecutableSHA256 $candidateHash -RequireStandalone
    return $report
}

Push-Location -LiteralPath $root
try {
    $source = Get-PclDbSourceState $root
    $sdk = (& dotnet --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the build SDK.' }
    $buildLog = Join-Path $operation 'build.log'
    & dotnet build src/PclDownloadBridge.App/PclDownloadBridge.App.vbproj -c Release -p:PclDbSingleExecutable=true "-p:SourceRevisionId=$($source.Commit)" --artifacts-path $build --nologo *> $buildLog
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
    $candidateHash = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash
    $version = [Reflection.AssemblyName]::GetAssemblyName($candidate).Version.ToString(3)
    $releaseVersion = Get-PclDbReleaseVersion $version ([Diagnostics.FileVersionInfo]::GetVersionInfo($candidate).ProductVersion) $source.Commit
    $standalone = Invoke-IsolatedCheck 'standalone' 'standalone'
    $embedded = Get-Content -LiteralPath (Join-Path $operation 'embedded-content.json') -Raw -Encoding UTF8 | ConvertFrom-Json
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
    $ui = Invoke-IsolatedCheck 'all' 'ui'
    $motion = Invoke-IsolatedCheck 'motion' 'motion'
    $packSearch = Invoke-IsolatedCheck 'pack-search' 'pack-search'
    $verification = New-PclDbVerification @($standalone,$ui,$motion,$packSearch) $candidateHash
    if (@(Get-ChildItem -LiteralPath $cold -Force).Count -ne 1) { throw 'The isolated run left extra files beside the executable.' }
    if ((Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -cne $candidateHash) { throw 'Candidate changed during isolated verification.' }
    $payload = Join-Path $operation 'payload'
    New-Item -ItemType Directory -Path $payload | Out-Null
    Copy-Item -LiteralPath $candidate -Destination (Join-Path $payload 'PCL-DB.exe')
    foreach ($document in $embedded.Documents) { Copy-Item -LiteralPath (Join-Path $root $document.Name) -Destination (Join-Path $payload $document.Name) }
    $files = @(Get-ChildItem -LiteralPath $payload -File | Sort-Object Name | ForEach-Object {
        [pscustomobject]@{Name=$_.Name;Length=$_.Length;SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    })
    Assert-PclDbSourceState $source (Get-PclDbSourceState $root)
    $result = [ordered]@{Format='PCL-DB-candidate/v3'; Succeeded=$true; PowerShellVersion=$PSVersionTable.PSVersion.ToString(); Version=$version; ReleaseVersion=$releaseVersion;
        StandaloneChecks=$standalone.Count; UiChecks=$ui.Count; MotionChecks=$motion.Count; PackSearchChecks=$packSearch.Count; Verification=$verification;
        EmbeddedAssemblies=$embedded.Assemblies; EmbeddedDocuments=$embedded.Documents;
        CandidatePath=$candidate; CandidateSHA256=$candidateHash;
        ColdDirectoryContainsOnlyExe=$true; Source=$source; DotnetSDK=$sdk; PayloadDirectory=$payload; PayloadFiles=$files;
        BuildDirectory=$build; ResultPath=(Join-Path $operation 'result.json'); Published=$false; DistributionUpdated=$false}
    $result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $result.ResultPath -Encoding UTF8
    if ($PassThru) { [pscustomobject]$result } else {
        Write-Output "PASS single EXE: $($standalone.Count) standalone, $($ui.Count) UI, $($motion.Count) motion and $($packSearch.Count) pack search checks; embedded bytes match the fresh build and seven source documents."
        Write-Output "Report: $($result.ResultPath)"
    }
} catch {
    [pscustomobject]@{Succeeded=$false;Error=$_.Exception.ToString();Operation=$operation} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $operation 'failure.json') -Encoding UTF8
    throw
} finally { Pop-Location }
