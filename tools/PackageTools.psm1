Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-PackageLayout([string]$ProjectRoot, [string]$RuntimeDirectory) {
    foreach ($name in @('PclDownloadBridge.exe', 'PclDownloadBridge.exe.config', 'PclDownloadBridge.Core.dll', 'Newtonsoft.Json.dll')) {
        [pscustomobject]@{Name=$name; Source=(Join-Path $RuntimeDirectory $name)}
    }
    foreach ($name in @('LICENCE', 'README.md', 'SOURCE_REFERENCES.md', 'THIRD_PARTY_NOTICES.md', 'VALIDATION.md', 'PATCH_MECHANISMS.md', 'VS2026_DEBUG.md')) {
        [pscustomobject]@{Name=$name; Source=(Join-Path $ProjectRoot $name)}
    }
}

function Get-StreamHash([IO.Stream]$Stream) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($Stream)).Replace('-', '') }
    finally { $algorithm.Dispose() }
}

function Assert-ProjectChild([string]$ProjectRoot, [string]$Path) {
    $prefix = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (![IO.Path]::GetFullPath($Path).StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Output is outside the project: $Path" }
}

function Test-PclDbPackage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ProjectRoot,
        [Parameter(Mandatory)][string]$PackagePath,
        [string]$RuntimeDirectory,
        [string]$DistributionPath
    )
    $ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).ProviderPath
    if (!$RuntimeDirectory) { $RuntimeDirectory = Join-Path $ProjectRoot 'src/PclDownloadBridge.App/bin/Release/net48' }
    $RuntimeDirectory = (Resolve-Path -LiteralPath $RuntimeDirectory).ProviderPath
    $PackagePath = (Resolve-Path -LiteralPath $PackagePath).ProviderPath
    if ($DistributionPath) { $DistributionPath = (Resolve-Path -LiteralPath $DistributionPath).ProviderPath }
    $layout = @(Get-PackageLayout $ProjectRoot $RuntimeDirectory)
    $expected = @{}
    foreach ($file in $layout) {
        $item = Get-Item -LiteralPath $file.Source
        if ($item.PSIsContainer) { throw "Expected a source file: $($file.Source)" }
        $expected[('PclDownloadBridge/' + $file.Name)] = [pscustomobject]@{
            Name=$file.Name; Length=$item.Length; SHA256=(Get-FileHash -LiteralPath $file.Source -Algorithm SHA256).Hash
        }
    }
    $zip = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        if ($zip.Entries.Count -ne $layout.Count) { throw "Package must contain exactly $($layout.Count) files." }
        $seen = @{}
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName
            if (!$expected.ContainsKey($name) -or $seen.ContainsKey($name) -or $name -cne ('PclDownloadBridge/' + $expected[$name].Name)) { throw "Unexpected or duplicate package entry: $name" }
            $seen[$name] = $true
            $stream = $entry.Open()
            try { $hash = Get-StreamHash $stream } finally { $stream.Dispose() }
            if ($entry.Length -ne $expected[$name].Length -or $hash -ne $expected[$name].SHA256) { throw "Package differs from its source: $name" }
        }
    } finally { $zip.Dispose() }
    if ($DistributionPath) {
        $actual = @(Get-ChildItem -LiteralPath $DistributionPath -File -Recurse -Force)
        if ($actual.Count -ne $layout.Count) { throw 'Distribution contains missing or extra files.' }
        foreach ($file in $layout) {
            $path = Join-Path $DistributionPath $file.Name
            if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $expected[('PclDownloadBridge/' + $file.Name)].SHA256) { throw "Distribution differs from its source: $($file.Name)" }
        }
    }
    $version = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $RuntimeDirectory 'PclDownloadBridge.exe')).Version.ToString(3)
    [pscustomobject]@{
        Succeeded=$true; Version=$version; PackagePath=$PackagePath
        PackageSHA256=(Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash
        DistributionPath=$DistributionPath; FileCount=$layout.Count
        Files=@($layout | ForEach-Object { $expected[('PclDownloadBridge/' + $_.Name)] })
    }
}

function New-PclDbPackage {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ProjectRoot, [string]$RuntimeDirectory)
    $ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).ProviderPath
    if (!(Test-Path -LiteralPath (Join-Path $ProjectRoot 'PclDownloadBridge.sln') -PathType Leaf)) { throw 'The PCL-DB solution was not found.' }
    if (!$RuntimeDirectory) { $RuntimeDirectory = Join-Path $ProjectRoot 'src/PclDownloadBridge.App/bin/Release/net48' }
    $records = Join-Path $ProjectRoot 'artifacts/packages'
    Assert-ProjectChild $ProjectRoot $records
    New-Item -ItemType Directory -Path $records -Force | Out-Null
    # An OS file handle also coordinates separate PowerShell processes. The empty lock file can remain after a crash.
    try { $gate = [IO.File]::Open((Join-Path $records 'package.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
    catch { throw 'Another package operation is already running or the output lock is unavailable.' }
    try {
        $operation = Join-Path $records ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
        $stage = Join-Path $operation 'PclDownloadBridge'
        New-Item -ItemType Directory -Path $stage -Force | Out-Null
        foreach ($file in @(Get-PackageLayout $ProjectRoot $RuntimeDirectory)) {
            Copy-Item -LiteralPath $file.Source -Destination (Join-Path $stage $file.Name)
        }
        $version = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $stage 'PclDownloadBridge.exe')).Version.ToString(3)
        # Retain the established 0.45-preview.zip naming when the patch component is zero.
        $packageVersion = $version -replace '\.0$', ''
        $zipName = "PCL-DB-$packageVersion-preview.zip"
        $stageZip = Join-Path $operation $zipName
        [IO.Compression.ZipFile]::CreateFromDirectory($stage, $stageZip, [IO.Compression.CompressionLevel]::Optimal, $true)
        $null = Test-PclDbPackage -ProjectRoot $ProjectRoot -PackagePath $stageZip -RuntimeDirectory $RuntimeDirectory -DistributionPath $stage
        $distribution = Join-Path $ProjectRoot 'dist/PclDownloadBridge'
        $package = Join-Path $ProjectRoot "dist/$zipName"
        $previous = Join-Path $operation 'previous'
        $oldDirectory = Join-Path $previous 'PclDownloadBridge'
        $oldZip = Join-Path $previous $zipName
        $failedDirectory = Join-Path $operation 'failed-distribution'
        $failedZip = Join-Path $operation 'failed.zip'
        foreach ($path in @($stage, $stageZip, $distribution, $package, $oldDirectory, $oldZip, $failedDirectory, $failedZip)) { Assert-ProjectChild $ProjectRoot $path }
        if ((Test-Path -LiteralPath $distribution) -and !(Test-Path -LiteralPath $distribution -PathType Container)) { throw 'The distribution path is occupied by a file.' }
        if ((Test-Path -LiteralPath $package) -and !(Test-Path -LiteralPath $package -PathType Leaf)) { throw 'The ZIP path is occupied by a directory.' }
        New-Item -ItemType Directory -Path (Split-Path $distribution -Parent), $previous -Force | Out-Null
        $directoryBackedUp = $false; $zipBackedUp = $false; $directoryPublished = $false; $zipPublished = $false
        try {
            if (Test-Path -LiteralPath $distribution) { [IO.Directory]::Move($distribution, $oldDirectory); $directoryBackedUp = $true }
            if (Test-Path -LiteralPath $package) { [IO.File]::Move($package, $oldZip); $zipBackedUp = $true }
            [IO.Directory]::Move($stage, $distribution); $directoryPublished = $true
            [IO.File]::Move($stageZip, $package); $zipPublished = $true
            $report = Test-PclDbPackage -ProjectRoot $ProjectRoot -PackagePath $package -RuntimeDirectory $RuntimeDirectory -DistributionPath $distribution
            $report | Add-Member NoteProperty BackupDirectory $previous
            $report | Add-Member NoteProperty ResultPath (Join-Path $operation 'result.json')
            $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $report.ResultPath -Encoding UTF8
            return $report
        } catch {
            $failure = $_.Exception.Message
            $rollbackErrors = @()
            try { if ($zipPublished) { [IO.File]::Move($package, $failedZip) } } catch { $rollbackErrors += $_.Exception.Message }
            try { if ($directoryPublished) { [IO.Directory]::Move($distribution, $failedDirectory) } } catch { $rollbackErrors += $_.Exception.Message }
            try { if ($zipBackedUp) { [IO.File]::Move($oldZip, $package) } } catch { $rollbackErrors += $_.Exception.Message }
            try { if ($directoryBackedUp) { [IO.Directory]::Move($oldDirectory, $distribution) } } catch { $rollbackErrors += $_.Exception.Message }
            [pscustomobject]@{Succeeded=$false; Error=$failure; RollbackErrors=$rollbackErrors; BackupDirectory=$previous} |
                ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $operation 'failure.json') -Encoding UTF8
            throw "Package replacement failed: $failure See $operation"
        }
    } finally { $gate.Dispose() }
}

Export-ModuleMember -Function New-PclDbPackage, Test-PclDbPackage
