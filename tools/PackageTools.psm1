Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
Import-Module (Join-Path $PSScriptRoot 'PackageCandidateTools.psm1')
Import-Module (Join-Path $PSScriptRoot 'VerificationTools.psm1')
$script:PayloadNames = @('PCL-DB.exe','LICENCE','README.md','SOURCE_REFERENCES.md','THIRD_PARTY_NOTICES.md','VALIDATION.md','PATCH_MECHANISMS.md','VS2026_DEBUG.md')

function Get-StreamHash([IO.Stream]$Stream) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($Stream)).Replace('-', '') }
    finally { $algorithm.Dispose() }
}

function Assert-OutputChild([string]$Root, [string]$Path) {
    $Root = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $full = [IO.Path]::GetFullPath($Path)
    if (!$full.StartsWith(($Root + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) { throw "Output is outside the selected root: $Path" }
    # Lexical containment alone would permit writes through a directory junction.
    $cursor = $full
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Output traverses a reparse point: $cursor" }
        }
        $cursor = Split-Path $cursor -Parent
    }
}

function Assert-BuildManifest([object]$Manifest) {
    if ($Manifest.Format -isnot [string] -or $Manifest.Format -cnotin @('PCL-DB-single-exe/v2','PCL-DB-single-exe/v3') -or !$Manifest.PSObject.Properties['ReleaseVersion'] -or
        !$Manifest.Verification.PSObject.Properties['MotionChecks'] -or
        $Manifest.Version -notmatch '^\d+\.\d+\.\d+$' -or
        $Manifest.ReleaseVersion -cnotmatch ('^' + [regex]::Escape($Manifest.Version) + '(?:-(?:alpha|beta|rc)\.[1-9]\d*)?$') -or
        $Manifest.Build.SourceCommit -notmatch '^[0-9a-f]{40}$' -or $Manifest.Build.InventorySHA256 -notmatch '^[0-9A-F]{64}$' -or
        $Manifest.Build.Configuration -cne 'Release' -or $Manifest.Build.Framework -cne 'net48' -or
        $Manifest.Build.Dirty -isnot [bool] -or $Manifest.Build.SingleExecutable -isnot [bool] -or !$Manifest.Build.SingleExecutable -or
        $Manifest.Files.Count -ne 8) { throw 'Invalid build manifest.' }
    # v2 carried only totals. Validate its declared structure, never today's suite
    # counts. This is archive integrity, not proof of current candidate acceptance.
    if ($Manifest.Format -ceq 'PCL-DB-single-exe/v2') {
        foreach ($field in @('StandaloneChecks','UiChecks','MotionChecks')) {
            if (!$Manifest.Verification.PSObject.Properties[$field]) { throw 'Invalid build manifest.' }
            $count = $Manifest.Verification.$field
            if (($count -isnot [int] -and $count -isnot [long]) -or $count -le 0 -or $count -gt 100000) { throw 'Invalid build manifest.' }
        }
    }
    $seen = @{}
    foreach ($file in $Manifest.Files) {
        if ($file.Name -cnotin $script:PayloadNames -or $seen.ContainsKey($file.Name) -or $file.Length -le 0 -or $file.SHA256 -notmatch '^[0-9A-F]{64}$') { throw 'Invalid build manifest file inventory.' }
        $seen[$file.Name] = $true
    }
    Assert-PclDbEmbeddedAssemblies $Manifest.EmbeddedAssemblies
    if ($Manifest.Format -ceq 'PCL-DB-single-exe/v3') {
        $exe = $Manifest.Files | Where-Object Name -CEQ 'PCL-DB.exe'
        try { Assert-PclDbVerification $Manifest.Verification $exe.SHA256 }
        catch { throw "Invalid build manifest. $($_.Exception.Message)" }
    }
}

function New-PclDbBuildManifest([object]$Candidate) {
    Assert-PclDbCandidateVerification $Candidate
    Assert-PclDbEmbeddedAssemblies $Candidate.EmbeddedAssemblies
    [pscustomobject][ordered]@{
        Format='PCL-DB-single-exe/v3';Version=$Candidate.Version;ReleaseVersion=$Candidate.ReleaseVersion
        Build=[ordered]@{SourceCommit=$Candidate.Source.Commit;SourceBranch=$Candidate.Source.Branch;Dirty=$Candidate.Source.Dirty;InventorySHA256=$Candidate.Source.InventorySHA256
            DotnetSDK=$Candidate.DotnetSDK;Configuration='Release';Framework='net48';SingleExecutable=$true}
        Verification=$Candidate.Verification
        EmbeddedAssemblies=$Candidate.EmbeddedAssemblies;Files=$Candidate.PayloadFiles
    }
}

function Assert-ManifestCandidate([object]$Manifest, [object]$Candidate) {
    if ($Manifest.Format -cne 'PCL-DB-single-exe/v3') { throw 'A historical manifest cannot satisfy current candidate verification.' }
    Assert-PclDbVerification $Manifest.Verification $Candidate.CandidateSHA256 -Current
    $manifestEvidence = New-PclDbVerification $Manifest.Verification.Reports $Candidate.CandidateSHA256
    $candidateEvidence = New-PclDbVerification $Candidate.Verification.Reports $Candidate.CandidateSHA256
    if (($manifestEvidence | ConvertTo-Json -Depth 8 -Compress) -cne ($candidateEvidence | ConvertTo-Json -Depth 8 -Compress)) { throw 'Manifest differs from the selected candidate verification.' }
    if ($Manifest.Version -ne $Candidate.Version -or $Manifest.ReleaseVersion -cne $Candidate.ReleaseVersion -or $Manifest.Build.SourceCommit -cne $Candidate.Source.Commit -or
        $Manifest.Build.SourceBranch -cne $Candidate.Source.Branch -or $Manifest.Build.Dirty -ne $Candidate.Source.Dirty -or
        $Manifest.Build.InventorySHA256 -cne $Candidate.Source.InventorySHA256 -or $Manifest.Build.DotnetSDK -cne $Candidate.DotnetSDK) { throw 'Manifest differs from the selected candidate source.' }
    foreach ($file in $Candidate.PayloadFiles) {
        $actual = @($Manifest.Files | Where-Object Name -CEQ $file.Name)
        if ($actual.Count -ne 1 -or $actual[0].Length -ne $file.Length -or $actual[0].SHA256 -cne $file.SHA256) { throw 'Manifest differs from the selected candidate bytes.' }
    }
    if (($Manifest.EmbeddedAssemblies | ConvertTo-Json -Depth 4 -Compress) -cne ($Candidate.EmbeddedAssemblies | ConvertTo-Json -Depth 4 -Compress)) { throw 'Manifest differs from the selected embedded dependencies.' }
}

function Test-PclDbPackage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ProjectRoot,
        [Parameter(Mandatory)][string]$PackagePath,
        [string]$DistributionPath,
        [string]$StandalonePath,
        [string]$ManifestPath,
        [string]$CandidateResultPath
    )
    $PackagePath = (Resolve-Path -LiteralPath $PackagePath).ProviderPath
    $zip = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        if ($zip.Entries.Count -ne 9) { throw 'Package must contain exactly 9 files.' }
        $entries = @{}
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName
            if ($name -cnotin @($script:PayloadNames + 'build-manifest.json' | ForEach-Object { 'PclDownloadBridge/' + $_ }) -or $entries.ContainsKey($name)) { throw "Unexpected or duplicate package entry: $name" }
            $entries[$name] = $entry
        }
        $manifestEntry = $entries['PclDownloadBridge/build-manifest.json']
        if ($manifestEntry.Length -gt 1048576) { throw 'Oversized build manifest.' }
        $stream = $manifestEntry.Open()
        $reader = New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8)
        try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        Assert-BuildManifest $manifest
        $expected = @{}
        foreach ($file in $manifest.Files) { $expected[$file.Name] = $file }
        $stream = $manifestEntry.Open()
        try { $manifestHash = Get-StreamHash $stream } finally { $stream.Dispose() }
        $expected['build-manifest.json'] = [pscustomobject]@{Name='build-manifest.json';Length=$manifestEntry.Length;SHA256=$manifestHash}
        foreach ($file in $expected.Values) {
            $entry = $entries[('PclDownloadBridge/' + $file.Name)]
            $stream = $entry.Open()
            try { $hash = Get-StreamHash $stream } finally { $stream.Dispose() }
            if ($entry.Length -ne $file.Length -or $hash -cne $file.SHA256) { throw "Package differs from its manifest: $($file.Name)" }
        }
    } finally { $zip.Dispose() }
    if ($CandidateResultPath) {
        $candidate = Test-PclDbCandidate -ProjectRoot $ProjectRoot -CandidateResultPath $CandidateResultPath
        Assert-ManifestCandidate $manifest $candidate
    }
    if ($DistributionPath) {
        $actual = @(Get-ChildItem -LiteralPath $DistributionPath -Recurse -Force)
        if ($actual.Count -ne 9 -or @($actual | Where-Object PSIsContainer).Count -ne 0) { throw 'Distribution contains missing or extra files.' }
        foreach ($file in $expected.Values) {
            $path = Join-Path $DistributionPath $file.Name
            if ((Get-Item -LiteralPath $path).Length -ne $file.Length -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $file.SHA256) { throw "Distribution differs from its manifest: $($file.Name)" }
        }
    }
    foreach ($asset in @(@{Path=$StandalonePath;Name='PCL-DB.exe'}, @{Path=$ManifestPath;Name='build-manifest.json'})) {
        if ($asset.Path) {
            if ((Get-Item -LiteralPath $asset.Path).Length -ne $expected[$asset.Name].Length -or
                (Get-FileHash -LiteralPath $asset.Path -Algorithm SHA256).Hash -cne $expected[$asset.Name].SHA256) { throw "Separate asset differs from the ZIP: $($asset.Name)" }
        }
    }
    [pscustomobject]@{
        Succeeded=$true;Version=$manifest.Version;ReleaseVersion=$manifest.ReleaseVersion;PackagePath=$PackagePath;PackageSHA256=(Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash
        StandalonePath=$StandalonePath;StandaloneSHA256=$expected['PCL-DB.exe'].SHA256;ManifestPath=$ManifestPath;ManifestSHA256=$manifestHash
        DistributionPath=$DistributionPath;FileCount=9;Files=@($expected.Values | Sort-Object Name);Build=$manifest.Build
        CandidateVerified=[bool]$CandidateResultPath;ManifestFormat=$manifest.Format
        VerificationLevel=$(if ($CandidateResultPath) { 'CurrentCandidate' } else { 'ArchiveIntegrity' })
    }
}

function Move-PackageOutput([string]$Source, [string]$Destination, [bool]$IsDirectory) {
    if ($IsDirectory) { [IO.Directory]::Move($Source, $Destination) } else { [IO.File]::Move($Source, $Destination) }
}

function New-PclDbPackage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ProjectRoot,
        [string]$CandidateResultPath,
        [string]$OutputRoot
    )
    $ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).ProviderPath
    if (!(Test-Path -LiteralPath (Join-Path $ProjectRoot 'PclDownloadBridge.sln') -PathType Leaf)) { throw 'The PCL-DB solution was not found.' }
    if (!$OutputRoot) { $OutputRoot = $ProjectRoot }
    $OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
    $records = Join-Path $OutputRoot 'artifacts/packages'
    Assert-OutputChild $OutputRoot $records
    New-Item -ItemType Directory -Path $records -Force | Out-Null
    try { $gate = [IO.File]::Open((Join-Path $records 'package.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
    catch { throw 'Another package operation is already running or the output lock is unavailable.' }
    try {
        if (!$CandidateResultPath) {
            $fresh = & (Join-Path $PSScriptRoot 'verify-single-exe.ps1') -PassThru
            $CandidateResultPath = $fresh.ResultPath
        }
        $candidate = Test-PclDbCandidate -ProjectRoot $ProjectRoot -CandidateResultPath $CandidateResultPath
        $operation = Join-Path $records ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
        $stage = Join-Path $operation 'PclDownloadBridge'
        Assert-OutputChild $OutputRoot $stage
        New-Item -ItemType Directory -Path $stage -Force | Out-Null
        foreach ($file in $candidate.PayloadFiles) { Copy-Item -LiteralPath (Join-Path $candidate.PayloadDirectory $file.Name) -Destination (Join-Path $stage $file.Name) }
        $manifest = New-PclDbBuildManifest $candidate
        $manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $stage 'build-manifest.json') -Encoding UTF8
        $stem = 'PCL-DB-' + $candidate.ReleaseVersion
        $stageZip = Join-Path $operation ($stem + '.zip')
        $stageExe = Join-Path $operation ($stem + '.exe')
        $stageManifest = Join-Path $operation ($stem + '.build-manifest.json')
        Copy-Item -LiteralPath (Join-Path $stage 'PCL-DB.exe') -Destination $stageExe
        Copy-Item -LiteralPath (Join-Path $stage 'build-manifest.json') -Destination $stageManifest
        # .NET Framework's CreateFromDirectory can emit backslashes. Write canonical ZIP names explicitly.
        $archive = [IO.Compression.ZipFile]::Open($stageZip, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($file in @(Get-ChildItem -LiteralPath $stage -File | Sort-Object Name)) {
                $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, ('PclDownloadBridge/' + $file.Name), [IO.Compression.CompressionLevel]::Optimal)
            }
        } finally { $archive.Dispose() }
        $dist = Join-Path $OutputRoot 'dist'
        $previous = Join-Path $operation 'previous'
        $outputs = @(
            [pscustomobject]@{Name='PclDownloadBridge';Stage=$stage;Directory=$true;BackedUp=$false;Published=$false}
            [pscustomobject]@{Name=($stem + '.zip');Stage=$stageZip;Directory=$false;BackedUp=$false;Published=$false}
            [pscustomobject]@{Name=($stem + '.exe');Stage=$stageExe;Directory=$false;BackedUp=$false;Published=$false}
            [pscustomobject]@{Name=($stem + '.build-manifest.json');Stage=$stageManifest;Directory=$false;BackedUp=$false;Published=$false}
        )
        foreach ($output in $outputs) {
            $output | Add-Member NoteProperty Final (Join-Path $dist $output.Name)
            $output | Add-Member NoteProperty Backup (Join-Path $previous $output.Name)
            $output | Add-Member NoteProperty Failed (Join-Path $operation ('failed-' + $output.Name))
            foreach ($path in @($output.Stage,$output.Final,$output.Backup,$output.Failed)) { Assert-OutputChild $OutputRoot $path }
            if ((Test-Path -LiteralPath $output.Final) -and (Get-Item -LiteralPath $output.Final -Force).PSIsContainer -ne $output.Directory) { throw "Output path has the wrong file type: $($output.Final)" }
        }
        $check = @{ProjectRoot=$ProjectRoot;CandidateResultPath=$CandidateResultPath}
        $null = Test-PclDbPackage @check -PackagePath $stageZip -DistributionPath $stage -StandalonePath $stageExe -ManifestPath $stageManifest
        New-Item -ItemType Directory -Path $dist,$previous -Force | Out-Null
        try {
            foreach ($output in $outputs) {
                if (Test-Path -LiteralPath $output.Final) { Move-PackageOutput $output.Final $output.Backup $output.Directory; $output.BackedUp = $true }
            }
            foreach ($output in $outputs) { Move-PackageOutput $output.Stage $output.Final $output.Directory; $output.Published = $true }
            $report = Test-PclDbPackage @check -PackagePath $outputs[1].Final -DistributionPath $outputs[0].Final -StandalonePath $outputs[2].Final -ManifestPath $outputs[3].Final
            $report | Add-Member NoteProperty CandidateResultPath ([IO.Path]::GetFullPath($CandidateResultPath))
            $report | Add-Member NoteProperty BackupDirectory $previous
            $report | Add-Member NoteProperty ResultPath (Join-Path $operation 'result.json')
            $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $report.ResultPath -Encoding UTF8
            return $report
        } catch {
            $failure = $_.Exception.Message
            $rollbackErrors = @()
            for ($index = $outputs.Count - 1; $index -ge 0; $index--) {
                $output = $outputs[$index]
                try { if ($output.Published) { Move-PackageOutput $output.Final $output.Failed $output.Directory } } catch { $rollbackErrors += $_.Exception.Message }
                try { if ($output.BackedUp) { Move-PackageOutput $output.Backup $output.Final $output.Directory } } catch { $rollbackErrors += $_.Exception.Message }
            }
            [pscustomobject]@{Succeeded=$false;Error=$failure;RollbackErrors=$rollbackErrors;BackupDirectory=$previous} |
                ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $operation 'failure.json') -Encoding UTF8
            throw "Package replacement failed: $failure See $operation"
        }
    } finally { $gate.Dispose() }
}

Export-ModuleMember -Function New-PclDbPackage, Test-PclDbPackage
