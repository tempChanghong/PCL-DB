param([string]$ReportPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $PSScriptRoot 'PackageTools.psm1') -Force
if (!$ReportPath) { $ReportPath = Join-Path $root 'artifacts/package-checks.json' }
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('PCD-PackageChecks-' + [Guid]::NewGuid().ToString('N'))
$runtime = Join-Path $fixture 'runtime'
$passed = [Collections.Generic.List[string]]::new()

function Assert-Case([bool]$Condition, [string]$Name) {
    if (!$Condition) { throw $Name }
    $passed.Add($Name)
    Write-Output "PASS $Name"
}

function Assert-Rejected([scriptblock]$Action, [string]$Message, [string]$Name) {
    $reason = ''
    try { & $Action | Out-Null } catch { $reason = $_.Exception.Message }
    Assert-Case ($reason -match $Message) $Name
}

function Edit-Zip([string]$Path, [scriptblock]$Edit) {
    $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Update)
    try { & $Edit $zip } finally { $zip.Dispose() }
}

function Output-Snapshot([string]$Directory, [string]$Zip) {
    $files = @(Get-ChildItem -LiteralPath $Directory -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
        $_.FullName.Substring($Directory.Length) + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    })
    return ($files -join '|') + '|' + (Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash
}

try {
    New-Item -ItemType Directory -Path $runtime -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'PlainCraftDownloader.sln') -Destination $fixture
    foreach ($name in @('LICENCE','README.md','SOURCE_REFERENCES.md','THIRD_PARTY_NOTICES.md','VALIDATION.md','PATCH_MECHANISMS.md','VS2026_DEBUG.md')) {
        Copy-Item -LiteralPath (Join-Path $root $name) -Destination $fixture
    }
    foreach ($name in @('PlainCraftDownloader.exe','PlainCraftDownloader.exe.config','PlainCraftDownloader.Core.dll','Newtonsoft.Json.dll')) {
        Copy-Item -LiteralPath (Join-Path $root "src/PlainCraftDownloader.App/bin/Release/net48/$name") -Destination $runtime
    }
    $oldDirectory = Join-Path $fixture 'dist/PlainCraftDownloader'
    New-Item -ItemType Directory -Path $oldDirectory -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $oldDirectory 'legacy-note.txt'), 'Fixture: preserve this old output, but do not include it in the new package.')
    $version = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $runtime 'PlainCraftDownloader.exe')).Version
    $packageVersion = if ($version.Build -eq 0) { $version.ToString(2) } else { $version.ToString(3) }
    $packagePath = Join-Path $fixture "dist/PlainCraftDownloader-$packageVersion-preview.zip"
    [IO.File]::WriteAllText($packagePath, 'Fixture: previous ZIP bytes.')
    $first = New-PcdPackage -ProjectRoot $fixture -RuntimeDirectory $runtime
    Assert-Case ($first.Succeeded -and $first.FileCount -eq 11) 'new package contains exactly four runtime files and seven documents'
    Assert-Case ($first.PackagePath -eq $packagePath) 'package naming retains the established major/minor tag when the patch version is zero'
    Assert-Case (!(Test-Path -LiteralPath (Join-Path $oldDirectory 'legacy-note.txt'))) 'stale files are excluded from the new distribution'
    Assert-Case ((Test-Path -LiteralPath (Join-Path $first.BackupDirectory 'PlainCraftDownloader/legacy-note.txt')) -and
        [IO.File]::ReadAllText((Join-Path $first.BackupDirectory "PlainCraftDownloader-$packageVersion-preview.zip")) -eq 'Fixture: previous ZIP bytes.') 'previous directory and ZIP remain in the recorded backup'
    $report = Test-PcdPackage -ProjectRoot $fixture -RuntimeDirectory $runtime -PackagePath $packagePath -DistributionPath $oldDirectory
    Assert-Case ($report.Files.Count -eq 11 -and @($report.Files | Where-Object Name -eq LICENCE).Count -eq 1) 'ZIP and distribution match all current source bytes including LICENCE'

    $candidate = Join-Path $fixture 'candidate.zip'
    Copy-Item -LiteralPath $packagePath -Destination $candidate
    Edit-Zip $candidate { param($zip) $null = $zip.CreateEntry('PlainCraftDownloader/extra.txt') }
    Assert-Rejected { Test-PcdPackage -ProjectRoot $fixture -RuntimeDirectory $runtime -PackagePath $candidate } 'exactly 11 files' 'candidate verification rejects an extra ZIP entry'

    Copy-Item -LiteralPath $packagePath -Destination $candidate -Force
    Edit-Zip $candidate { param($zip) $zip.GetEntry('PlainCraftDownloader/LICENCE').Delete() }
    Assert-Rejected { Test-PcdPackage -ProjectRoot $fixture -RuntimeDirectory $runtime -PackagePath $candidate } 'exactly 11 files' 'candidate verification rejects a missing licence'

    Copy-Item -LiteralPath $packagePath -Destination $candidate -Force
    Edit-Zip $candidate {
        param($zip)
        $entry = $zip.GetEntry('PlainCraftDownloader/PlainCraftDownloader.Core.dll')
        $entry.Delete()
        $bytes = [IO.File]::ReadAllBytes((Join-Path $runtime 'PlainCraftDownloader.Core.dll'))
        $bytes[0] = $bytes[0] -bxor 1
        $stream = $zip.CreateEntry('PlainCraftDownloader/PlainCraftDownloader.Core.dll').Open()
        try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
    }
    Assert-Rejected { Test-PcdPackage -ProjectRoot $fixture -RuntimeDirectory $runtime -PackagePath $candidate } 'differs from its source' 'candidate verification rejects modified runtime bytes'

    Copy-Item -LiteralPath $packagePath -Destination $candidate -Force
    Edit-Zip $candidate {
        param($zip)
        $zip.GetEntry('PlainCraftDownloader/LICENCE').Delete()
        $null = $zip.CreateEntry('PlainCraftDownloader/README.md')
    }
    Assert-Rejected { Test-PcdPackage -ProjectRoot $fixture -RuntimeDirectory $runtime -PackagePath $candidate } 'duplicate package entry' 'candidate verification rejects duplicate names with an unchanged total count'

    $readme = Join-Path $fixture 'README.md'
    $readmeBytes = [IO.File]::ReadAllBytes($readme)
    try {
        [IO.File]::AppendAllText($readme, 'Changed after packaging.')
        Assert-Rejected { Test-PcdPackage -ProjectRoot $fixture -RuntimeDirectory $runtime -PackagePath $packagePath } 'differs from its source' 'candidate verification rejects outdated documentation'
    } finally { [IO.File]::WriteAllBytes($readme, $readmeBytes) }

    [IO.File]::WriteAllText((Join-Path $oldDirectory '.fixture-extra'), 'Unexpected distribution file.')
    [IO.File]::SetAttributes((Join-Path $oldDirectory '.fixture-extra'), [IO.FileAttributes]::Hidden)
    Assert-Rejected { Test-PcdPackage -ProjectRoot $fixture -RuntimeDirectory $runtime -PackagePath $packagePath -DistributionPath $oldDirectory } 'extra files' 'distribution verification includes hidden leftover files'
    $before = Output-Snapshot $oldDirectory $packagePath
    $handle = [IO.File]::Open($packagePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    try {
        Assert-Rejected { New-PcdPackage -ProjectRoot $fixture -RuntimeDirectory $runtime } 'Package replacement failed' 'a locked previous ZIP stops replacement after the old directory was backed up'
    } finally { $handle.Dispose() }
    Assert-Case ((Output-Snapshot $oldDirectory $packagePath) -eq $before) 'failed replacement restores every old directory file and the original ZIP bytes'
    $failure = @(Get-ChildItem -LiteralPath (Join-Path $fixture 'artifacts/packages') -Recurse -Filter failure.json | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json })
    Assert-Case ($failure.Count -eq 1 -and @($failure[0].RollbackErrors).Count -eq 0) 'failed replacement records a successful rollback'

    $gatePath = Join-Path $fixture 'artifacts/packages/package.lock'
    $gate = [IO.File]::Open($gatePath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        Assert-Rejected { New-PcdPackage -ProjectRoot $fixture -RuntimeDirectory $runtime } 'already running' 'concurrent packaging is rejected while the output lock is held'
    } finally { $gate.Dispose() }
    Assert-Case ((Output-Snapshot $oldDirectory $packagePath) -eq $before) 'a competing packager leaves both existing outputs untouched'

    $licence = Join-Path $fixture 'LICENCE'
    [IO.File]::Move($licence, ($licence + '.saved'))
    try {
        Assert-Rejected { New-PcdPackage -ProjectRoot $fixture -RuntimeDirectory $runtime } 'LICENCE|does not exist|Cannot find' 'missing source documentation fails before replacing existing outputs'
    } finally { [IO.File]::Move(($licence + '.saved'), $licence) }
    Assert-Case ((Output-Snapshot $oldDirectory $packagePath) -eq $before) 'a staging failure preserves the previous directory and ZIP'
    $retry = New-PcdPackage -ProjectRoot $fixture -RuntimeDirectory $runtime
    Assert-Case ($retry.Succeeded -and !(Test-Path -LiteralPath (Join-Path $oldDirectory '.fixture-extra'))) 'retry after failure releases the lock and publishes only the current file list'
    $result = [pscustomobject]@{Succeeded=$true; Count=$passed.Count; Passed=@($passed); Fixture=$fixture; PackageReport=$retry.ResultPath}
} catch {
    $result = [pscustomobject]@{Succeeded=$false; Count=$passed.Count; Passed=@($passed); Fixture=$fixture; Error=$_.Exception.ToString()}
    throw
} finally {
    New-Item -ItemType Directory -Path (Split-Path ([IO.Path]::GetFullPath($ReportPath)) -Parent) -Force | Out-Null
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
}
