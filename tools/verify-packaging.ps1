param([string]$ReportPath, [string]$CandidateResultPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $PSScriptRoot 'PackageTools.psm1')
Import-Module (Join-Path $PSScriptRoot 'PackageCandidateTools.psm1')
if (!$ReportPath) { $ReportPath = Join-Path $root 'artifacts/package-checks.json' }
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('PCL-DB PackageChecks ' + [char]0x6D4B + [char]0x8BD5 + ' ' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$passed = New-Object 'Collections.Generic.List[string]'

function Assert-Case([bool]$Condition, [string]$Name) {
    if (!$Condition) { throw $Name }
    $passed.Add($Name)
    Write-Output "PASS $Name"
}
function Assert-Rejected([scriptblock]$Action, [string]$Message, [string]$Name) {
    $reason = ''
    try { & $Action | Out-Null } catch { $reason = $_.Exception.Message }
    if ($reason -notmatch $Message) { throw "$Name -- expected rejection /$Message/, got: $reason" }
    Assert-Case $true $Name
}
function Edit-Zip([string]$Path, [scriptblock]$Edit) {
    $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Update)
    try { & $Edit $zip } finally { $zip.Dispose() }
}
function Set-ZipBytes([object]$Zip, [string]$Name, [byte[]]$Bytes) {
    $Zip.GetEntry($Name).Delete()
    $stream = $Zip.CreateEntry($Name).Open()
    try { $stream.Write($Bytes, 0, $Bytes.Length) } finally { $stream.Dispose() }
}
function Edit-Manifest([object]$Zip, [scriptblock]$Edit) {
    $name = 'PclDownloadBridge/build-manifest.json'
    $reader = New-Object IO.StreamReader($Zip.GetEntry($name).Open(), [Text.Encoding]::UTF8)
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    & $Edit $manifest
    Set-ZipBytes $Zip $name ([Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Depth 8)))
}
function Output-Snapshot {
    return (@(Get-ChildItem -LiteralPath (Join-Path $fixture 'dist') -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
        $_.FullName.Substring($fixture.Length) + ':' + $_.LastWriteTimeUtc.Ticks + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }) -join '|')
}

try {
    if (!$CandidateResultPath) {
        $fresh = & (Join-Path $PSScriptRoot 'verify-single-exe.ps1') -PassThru
        $CandidateResultPath = $fresh.ResultPath
    }
    $candidate = Test-PclDbCandidate -ProjectRoot $root -CandidateResultPath $CandidateResultPath
    [string[]]$ordered = @($candidate.Source.Files | ForEach-Object Name)
    [Array]::Sort($ordered, [StringComparer]::Ordinal)
    Assert-Case (($ordered -join '|') -ceq (($candidate.Source.Files | ForEach-Object Name) -join '|')) 'source inventory uses ordinal ordering across PowerShell hosts'
    $options = @{ProjectRoot=$root;CandidateResultPath=$CandidateResultPath;OutputRoot=$fixture}
    $verify = @{ProjectRoot=$root;CandidateResultPath=$CandidateResultPath}
    $stem = 'PCL-DB-' + $candidate.ReleaseVersion
    $directory = Join-Path $fixture 'dist/PclDownloadBridge'
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $directory 'legacy-note.txt'), 'previous directory')
    foreach ($suffix in @('.zip','.exe','.build-manifest.json')) { [IO.File]::WriteAllText((Join-Path $fixture ('dist/' + $stem + $suffix)), ('previous ' + $suffix)) }
    $first = New-PclDbPackage @options
    Assert-Case ($first.Succeeded -and $first.FileCount -eq 9) 'package contains one EXE, seven documents and one manifest'
    Assert-Case ($first.PackagePath -eq (Join-Path $fixture ('dist/' + $stem + '.zip'))) 'archive naming preserves the complete release version and channel'
    Assert-Case (!(Test-Path -LiteralPath (Join-Path $directory 'legacy-note.txt'))) 'old directory leftovers are excluded'
    Assert-Case ((Get-ChildItem -LiteralPath $first.BackupDirectory -Force).Count -eq 4 -and [IO.File]::ReadAllText((Join-Path $first.BackupDirectory ($stem + '.exe'))) -eq 'previous .exe') 'all four previous outputs are backed up'
    $report = Test-PclDbPackage @verify -PackagePath $first.PackagePath -DistributionPath $directory -StandalonePath $first.StandalonePath -ManifestPath $first.ManifestPath
    Assert-Case ($report.CandidateVerified -and $report.StandaloneSHA256 -eq $candidate.CandidateSHA256) 'ZIP, direct EXE, manifest and directory match the explicit verified candidate'
    Assert-Case ($report.Build.SourceCommit -eq $candidate.Source.Commit -and $report.Build.Dirty -eq $candidate.Source.Dirty -and $report.Build.InventorySHA256 -eq $candidate.Source.InventorySHA256) 'manifest retains commit, workspace status and source inventory digest'
    Assert-Case ($report.Build.DotnetSDK -eq $candidate.DotnetSDK -and $report.Build.Framework -eq 'net48') 'manifest identifies SDK and runtime'
    $independent = Test-PclDbPackage -ProjectRoot $root -PackagePath $first.PackagePath
    Assert-Case (!$independent.CandidateVerified -and $independent.Succeeded) 'archive-only verification is explicitly distinguished from candidate provenance validation'
    Assert-Case ((Get-PclDbReleaseVersion '1.0.0' ('1.0.0-beta.1+' + $candidate.Source.Commit) $candidate.Source.Commit) -ceq '1.0.0-beta.1') 'beta informational identity retains the numeric assembly version'
    Assert-Rejected { Get-PclDbReleaseVersion '1.0.0' ('1.0.0-beta.1+wrong') $candidate.Source.Commit } 'product version' 'wrong source identity cannot become a release version'
    $zip = [IO.Compression.ZipFile]::OpenRead($first.PackagePath)
    try { Assert-Case (@($zip.Entries | Where-Object { $_.FullName -match '\.(dll|config|pdb)$' }).Count -eq 0) 'archive excludes DLL, config and PDB sidecars' } finally { $zip.Dispose() }

    $bad = Join-Path $fixture 'candidate.zip'
    Copy-Item -LiteralPath $first.PackagePath -Destination $bad
    Edit-Zip $bad { param($zip) Edit-Manifest $zip { param($manifest) $manifest.Verification.MotionChecks = 0 } }
    Assert-Rejected { Test-PclDbPackage @verify -PackagePath $bad } 'Invalid build manifest' 'a package with failed motion verification is rejected'
    Copy-Item -LiteralPath $first.PackagePath -Destination $bad -Force
    Edit-Zip $bad { param($zip) Edit-Manifest $zip { param($manifest) $manifest.Verification.PSObject.Properties.Remove('MotionChecks') } }
    Assert-Rejected { Test-PclDbPackage @verify -PackagePath $bad } 'Invalid build manifest' 'a package missing motion verification is rejected'
    Copy-Item -LiteralPath $first.PackagePath -Destination $bad -Force
    Edit-Zip $bad { param($zip) Edit-Manifest $zip { param($manifest) $manifest.ReleaseVersion = $manifest.Version + '-beta.999' } }
    Assert-Rejected { Test-PclDbPackage @verify -PackagePath $bad } 'selected candidate source' 'a package cannot silently change the selected beta release identity'
    Copy-Item -LiteralPath $first.PackagePath -Destination $bad
    Edit-Zip $bad { param($zip) $null = $zip.CreateEntry('PclDownloadBridge/extra.txt') }
    Assert-Rejected { Test-PclDbPackage @verify -PackagePath $bad } 'exactly 9' 'extra ZIP entries are rejected'
    Copy-Item -LiteralPath $first.PackagePath -Destination $bad -Force
    Edit-Zip $bad { param($zip) $zip.GetEntry('PclDownloadBridge/LICENCE').Delete() }
    Assert-Rejected { Test-PclDbPackage @verify -PackagePath $bad } 'exactly 9' 'missing licence is rejected'
    Copy-Item -LiteralPath $first.PackagePath -Destination $bad -Force
    Edit-Zip $bad { param($zip) $zip.GetEntry('PclDownloadBridge/LICENCE').Delete(); $null = $zip.CreateEntry('PclDownloadBridge/README.md') }
    Assert-Rejected { Test-PclDbPackage @verify -PackagePath $bad } 'duplicate package entry' 'duplicate ZIP names with the same entry count are rejected'
    Copy-Item -LiteralPath $first.PackagePath -Destination $bad -Force
    Edit-Zip $bad { param($zip) $zip.GetEntry('PclDownloadBridge/LICENCE').Delete(); $null = $zip.CreateEntry('PclDownloadBridge/../LICENCE') }
    Assert-Rejected { Test-PclDbPackage @verify -PackagePath $bad } 'Unexpected' 'noncanonical and traversal entry names are rejected'
    Copy-Item -LiteralPath $first.PackagePath -Destination $bad -Force
    Edit-Zip $bad {
        param($zip)
        $bytes = [IO.File]::ReadAllBytes((Join-Path $candidate.PayloadDirectory 'PCL-DB.exe'))
        $bytes[0] = $bytes[0] -bxor 1
        Set-ZipBytes $zip 'PclDownloadBridge/PCL-DB.exe' $bytes
    }
    Assert-Rejected { Test-PclDbPackage @verify -PackagePath $bad } 'differs from its manifest' 'same-length EXE corruption is rejected'
    Copy-Item -LiteralPath $first.PackagePath -Destination $bad -Force
    Edit-Zip $bad { param($zip) Edit-Manifest $zip { param($manifest) $manifest.Files[0] = $manifest.Files[1] } }
    Assert-Rejected { Test-PclDbPackage @verify -PackagePath $bad } 'file inventory' 'duplicate manifest file declarations are rejected'
    Copy-Item -LiteralPath $first.PackagePath -Destination $bad -Force
    Edit-Zip $bad { param($zip) Edit-Manifest $zip { param($manifest) $manifest.Version = '99.0.0'; $manifest.ReleaseVersion = '99.0.0-beta.1' } }
    Assert-Rejected { Test-PclDbPackage @verify -PackagePath $bad } 'selected candidate source' 'valid-looking but incorrect version provenance is rejected'
    Copy-Item -LiteralPath $first.PackagePath -Destination $bad -Force
    Edit-Zip $bad { param($zip) Edit-Manifest $zip { param($manifest) $manifest.Build.SourceCommit = ('0' * 40) } }
    Assert-Rejected { Test-PclDbPackage @verify -PackagePath $bad } 'selected candidate source' 'wrong source commit is rejected against the explicit candidate'
    Copy-Item -LiteralPath $first.PackagePath -Destination $bad -Force
    Edit-Zip $bad { param($zip) Edit-Manifest $zip { param($manifest) $manifest.EmbeddedAssemblies[0].SHA256 = ('0' * 64) } }
    Assert-Rejected { Test-PclDbPackage @verify -PackagePath $bad } 'selected embedded dependencies' 'wrong embedded dependency provenance is rejected'
    $badExe = Join-Path $fixture 'bad.exe'
    [IO.File]::WriteAllText($badExe, 'wrong direct asset')
    Assert-Rejected { Test-PclDbPackage @verify -PackagePath $first.PackagePath -StandalonePath $badExe } 'Separate asset differs' 'direct EXE must match the ZIP byte for byte'
    Assert-Rejected { Test-PclDbPackage @verify -PackagePath $first.PackagePath -ManifestPath $badExe } 'Separate asset differs' 'separate manifest must match the ZIP byte for byte'

    $copy = Join-Path $fixture 'payload-copy'
    $variantPath = Join-Path $fixture 'invalid-verification.json'
    foreach ($mode in @('zero','missing','release')) {
        $invalid = Get-Content -LiteralPath $CandidateResultPath -Raw -Encoding UTF8 | ConvertFrom-Json
        switch ($mode) {
            'zero' { $invalid.MotionChecks = 0 }
            'missing' { $invalid.PSObject.Properties.Remove('MotionChecks') }
            'release' { $invalid.ReleaseVersion = '99.0.0-beta.1' }
        }
        $invalid | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $variantPath -Encoding UTF8
        $expected = if ($mode -eq 'release') { 'release version mismatch' } else { 'Invalid single EXE' }
        Assert-Rejected { Test-PclDbCandidate -ProjectRoot $root -CandidateResultPath $variantPath } $expected ('candidate rejects invalid verification identity: ' + $mode)
    }
    New-Item -ItemType Directory -Path $copy | Out-Null
    foreach ($file in $candidate.PayloadFiles) { Copy-Item -LiteralPath (Join-Path $candidate.PayloadDirectory $file.Name) -Destination $copy }
    $variant = Get-Content -LiteralPath $CandidateResultPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $variant.PayloadDirectory = $copy
    $variantPath = Join-Path $fixture 'variant-result.json'
    $variant | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $variantPath -Encoding UTF8
    [IO.File]::AppendAllText((Join-Path $copy 'README.md'), 'changed')
    Assert-Rejected { Test-PclDbCandidate -ProjectRoot $root -CandidateResultPath $variantPath } 'Candidate bytes changed' 'candidate payload changes after validation are rejected'
    Copy-Item -LiteralPath (Join-Path $candidate.PayloadDirectory 'README.md') -Destination $copy -Force
    [IO.File]::WriteAllText((Join-Path $copy 'unexpected.dll'), 'unexpected')
    Assert-Rejected { Test-PclDbCandidate -ProjectRoot $root -CandidateResultPath $variantPath } 'extra files' 'candidate payload cannot acquire an extra DLL'
    $variant.Source.InventorySHA256 = ('0' * 64)
    $variant | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $variantPath -Encoding UTF8
    Assert-Rejected { Test-PclDbCandidate -ProjectRoot $root -CandidateResultPath $variantPath } 'Source changed' 'stale source inventory is rejected before packaging'

    [IO.File]::WriteAllText((Join-Path $directory '.hidden-extra'), 'old output')
    [IO.File]::SetAttributes((Join-Path $directory '.hidden-extra'), [IO.FileAttributes]::Hidden)
    Assert-Rejected { Test-PclDbPackage @verify -PackagePath $first.PackagePath -DistributionPath $directory } 'extra files' 'distribution verification includes hidden leftovers'
    $before = Output-Snapshot
    foreach ($locked in @($first.PackagePath,$first.StandalonePath,$first.ManifestPath)) {
        $handle = [IO.File]::Open($locked, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        try { Assert-Rejected { New-PclDbPackage @options } 'Package replacement failed' ('locked output stops replacement: ' + [IO.Path]::GetExtension($locked)) }
        finally { $handle.Dispose() }
        Assert-Case ((Output-Snapshot) -ceq $before) 'rollback restores every old output byte and file timestamp'
    }
    $module = Get-Module PackageTools
    & $module {
        $script:SavedMove = (Get-Command Move-PackageOutput).ScriptBlock
        $script:MoveCount = 0
        Set-Item Function:script:Move-PackageOutput {
            param($Source,$Destination,$IsDirectory)
            $script:MoveCount++
            if ($script:MoveCount -eq 6) { throw 'Fixture: I/O failure after directory publication.' }
            & $script:SavedMove $Source $Destination $IsDirectory
        }
    }
    try { Assert-Rejected { New-PclDbPackage @options } 'Package replacement failed' 'I/O failure after publishing the new directory enters rollback' }
    finally { & $module { Set-Item Function:script:Move-PackageOutput $script:SavedMove } }
    Assert-Case ((Output-Snapshot) -ceq $before) 'partially published outputs are retained aside and previous outputs are restored'
    $failures = @(Get-ChildItem -LiteralPath (Join-Path $fixture 'artifacts/packages') -Recurse -Filter failure.json | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json })
    Assert-Case ($failures.Count -eq 4 -and @($failures | Where-Object { @($_.RollbackErrors).Count -ne 0 }).Count -eq 0) 'all four replacement failures record successful rollback'
    $gate = [IO.File]::Open((Join-Path $fixture 'artifacts/packages/package.lock'), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try { Assert-Rejected { New-PclDbPackage @options } 'already running' 'output lock prevents a concurrent packager' } finally { $gate.Dispose() }
    Assert-Case ((Output-Snapshot) -ceq $before) 'rejected concurrency preserves all previous outputs'
    $retry = New-PclDbPackage @options
    Assert-Case ($retry.Succeeded -and !(Test-Path -LiteralPath (Join-Path $directory '.hidden-extra'))) 'retry releases the gate and removes old leftovers from the new distribution'

    $collision = Join-Path $fixture 'collision'
    New-Item -ItemType Directory -Path (Join-Path $collision ('dist/' + $stem + '.exe')) -Force | Out-Null
    Assert-Rejected { New-PclDbPackage -ProjectRoot $root -CandidateResultPath $CandidateResultPath -OutputRoot $collision } 'wrong file type' 'occupied EXE destination fails before replacement'
    $outside = Join-Path $fixture 'junction-target'
    $junction = Join-Path $fixture 'junction-output'
    New-Item -ItemType Directory -Path $outside | Out-Null
    New-Item -ItemType Junction -Path $junction -Value $outside | Out-Null
    Assert-Rejected { New-PclDbPackage -ProjectRoot $root -CandidateResultPath $CandidateResultPath -OutputRoot $junction } 'reparse point' 'output junctions are rejected before creating package records'
    Assert-Case (@(Get-ChildItem -LiteralPath $outside -Force).Count -eq 0) 'junction target remains untouched'

    $sourceFixture = Join-Path $fixture 'source-fixture'
    New-Item -ItemType Directory -Path $sourceFixture | Out-Null
    & git -C $sourceFixture init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Cannot initialize the source fixture.' }
    [IO.File]::WriteAllText((Join-Path $sourceFixture '.gitignore'), "artifacts/`n")
    [IO.File]::WriteAllText((Join-Path $sourceFixture 'input.vb'), 'original source')
    & git -C $sourceFixture add .
    & git -C $sourceFixture -c user.name=PackageFixture -c user.email=fixture@example.invalid -c commit.gpgsign=false commit --quiet -m fixture
    if ($LASTEXITCODE -ne 0) { throw 'Cannot commit the local source fixture.' }
    $source = Get-PclDbSourceState $sourceFixture
    Assert-Case (!$source.Dirty -and $source.Files.Count -eq 2) 'clean source state includes all tracked inputs'
    [IO.File]::AppendAllText((Join-Path $sourceFixture 'input.vb'), ' changed')
    Assert-Rejected { Assert-PclDbSourceState $source (Get-PclDbSourceState $sourceFixture) } 'Source changed' 'tracked input changes invalidate a candidate'
    $changed = Get-PclDbSourceState $sourceFixture
    Assert-Case ($changed.Dirty -and $changed.Commit -eq $source.Commit -and $changed.InventorySHA256 -ne $source.InventorySHA256) 'dirty builds distinguish content changes under the same commit'
    [IO.File]::WriteAllText((Join-Path $sourceFixture 'new.vb'), 'new untracked source')
    Assert-Rejected { Assert-PclDbSourceState $changed (Get-PclDbSourceState $sourceFixture) } 'Source changed' 'new untracked source inputs invalidate a candidate'
    $withNew = Get-PclDbSourceState $sourceFixture
    New-Item -ItemType Directory -Path (Join-Path $sourceFixture 'artifacts') | Out-Null
    [IO.File]::WriteAllText((Join-Path $sourceFixture 'artifacts/ignored.txt'), 'generated output')
    Assert-PclDbSourceState $withNew (Get-PclDbSourceState $sourceFixture)
    Assert-Case $true 'ignored generated artifacts do not invalidate source identity'
    [IO.File]::Move((Join-Path $sourceFixture 'input.vb'), (Join-Path $sourceFixture 'artifacts/deleted-input.saved'))
    Assert-Rejected { Assert-PclDbSourceState $withNew (Get-PclDbSourceState $sourceFixture) } 'Source changed' 'deleted tracked source inputs invalidate a candidate'
    $result = [pscustomobject]@{Succeeded=$true;Count=$passed.Count;Passed=@($passed);Fixture=$fixture;CandidateResultPath=$CandidateResultPath;PackageReport=$retry.ResultPath;ProductionDistributionUpdated=$false}
} catch {
    $result = [pscustomobject]@{Succeeded=$false;Count=$passed.Count;Passed=@($passed);Fixture=$fixture;Error=$_.Exception.ToString()}
    throw
} finally {
    New-Item -ItemType Directory -Path (Split-Path ([IO.Path]::GetFullPath($ReportPath)) -Parent) -Force | Out-Null
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
}
