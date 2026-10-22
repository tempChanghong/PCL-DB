param([string]$SourceRoot, [string]$ReportPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
if (!$SourceRoot) { $SourceRoot = Join-Path (Split-Path $root -Parent) 'PCL' }
if (!$ReportPath) { $ReportPath = Join-Path $root 'artifacts/pcl-import-checks.json' }
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('PCL-DB-ImportChecks-' + [Guid]::NewGuid().ToString('N'))
$fixture = Join-Path $testRoot ('PCL-DB source ' + [char]0x6D4B + [char]0x8BD5)
$repository = Join-Path $testRoot ('upstream source ' + [char]0x6D4B + [char]0x8BD5 + '.git')
$shell = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
$passed = [Collections.Generic.List[string]]::new()
$runNumber = 0

function Assert-Case([bool]$Condition, [string]$Name) {
    if (!$Condition) { throw $Name }
    $passed.Add($Name)
    Write-Output "PASS $Name"
}

function Snapshot([switch]$Times) {
    $artifactPrefix = (Join-Path $fixture 'artifacts') + [IO.Path]::DirectorySeparatorChar
    $rows = @(Get-ChildItem -LiteralPath $fixture -Recurse -File -Force | Where-Object { !$_.FullName.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase) } | Sort-Object FullName | ForEach-Object {
        $_.FullName.Substring($fixture.Length) + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash + $(if ($Times) { ':' + $_.LastWriteTimeUtc.Ticks } else { '' })
    })
    return $rows -join '|'
}

function Invoke-Import([string[]]$Options = @(), [string]$RepositoryPath = $repository) {
    $script:runNumber++
    $records = Join-Path $fixture 'artifacts/pcl-import'
    $before = @(if (Test-Path -LiteralPath $records) { Get-ChildItem -LiteralPath $records -Recurse -Filter result.json | ForEach-Object FullName })
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $shell
    $info.Arguments = '-NoProfile -File "' + (Join-Path $fixture 'tools/import-pcl-ui.ps1') + '" -SourceRoot "' + $RepositoryPath + '" ' + ($Options -join ' ')
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $info
    try {
        $null = $process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $text = $stdout.GetAwaiter().GetResult() + "`n" + $stderr.GetAwaiter().GetResult()
        $exitCode = $process.ExitCode
    } finally { $process.Dispose() }
    $log = Join-Path $testRoot "run-$script:runNumber.log"
    [IO.File]::WriteAllText($log, $text)
    $newReports = @(if (Test-Path -LiteralPath $records) { Get-ChildItem -LiteralPath $records -Recurse -Filter result.json | Where-Object FullName -NotIn $before })
    $report = if ($newReports.Count -eq 1) { Get-Content -LiteralPath $newReports[0].FullName -Raw | ConvertFrom-Json } else { $null }
    [pscustomobject]@{ExitCode=$exitCode; Report=$report; Text=$text; Log=$log}
}

function Move-Aside([string]$Relative) {
    $source = Join-Path $fixture $Relative
    $saved = Join-Path $testRoot "saved/$Relative"
    foreach ($path in @($source, $saved)) {
        if (![IO.Path]::GetFullPath($path).StartsWith(([IO.Path]::GetFullPath($testRoot) + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture path escaped the temporary test root.' }
    }
    New-Item -ItemType Directory -Path (Split-Path $saved -Parent) -Force | Out-Null
    [IO.File]::Move($source, $saved)
}

try {
    New-Item -ItemType Directory -Path (Join-Path $fixture 'tools'), (Join-Path $fixture 'src/PclDownloadBridge.App') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'reference') -Destination $fixture -Recurse
    foreach ($folder in @('Controls', 'Images', 'Resources')) { Copy-Item -LiteralPath (Join-Path $root "src/PclDownloadBridge.App/$folder") -Destination (Join-Path $fixture 'src/PclDownloadBridge.App') -Recurse }
    Copy-Item -LiteralPath (Join-Path $root 'LICENCE') -Destination $fixture
    Copy-Item -LiteralPath (Join-Path $root 'tools/import-pcl-ui.ps1') -Destination (Join-Path $fixture 'tools')
    & git clone --shared --bare --quiet -- $SourceRoot $repository
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the isolated upstream repository.' }
    $loose = Join-Path $repository 'Plain Craft Launcher 2/Images/Blocks/Grass.png'
    New-Item -ItemType Directory -Path (Split-Path $loose -Parent) -Force | Out-Null
    [IO.File]::WriteAllText($loose, 'Fixture-only loose resource: never copy these bytes.')
    $adapter = Join-Path $fixture 'src/PclDownloadBridge.App/Controls/PclButton.xaml'
    [IO.File]::AppendAllText($adapter, "`n<!-- Fixture-only local adaptation -->`n")
    $adapterBytes = [IO.File]::ReadAllBytes($adapter)
    $notes = Join-Path $fixture 'reference/PCL/local-notes.txt'
    [IO.File]::WriteAllText($notes, 'Unrelated local reference notes must be retained.')
    $before = Snapshot -Times
    $first = Invoke-Import
    Assert-Case ($first.ExitCode -eq 0 -and $first.Report.Succeeded -and $first.Report.PlannedFiles -eq 73) 'all 73 planned files can be checked using a bare upstream repository with spaces and Unicode paths'
    Assert-Case (@($first.Report.WrittenFiles).Count -eq 0 -and (Snapshot -Times) -eq $before) 'an unchanged import preserves every target byte and modification timestamp'
    Assert-Case (@($first.Report.PreservedAdapters).Count -eq 7 -and [Convert]::ToBase64String([IO.File]::ReadAllBytes($adapter)) -eq [Convert]::ToBase64String($adapterBytes)) 'existing local control adaptations are preserved'
    $assets = Get-Content -LiteralPath (Join-Path $fixture 'reference/pcl-assets.json') -Raw | ConvertFrom-Json
    $incorrect = @($assets.Files | Where-Object { (Get-FileHash -LiteralPath (Join-Path $fixture "src/PclDownloadBridge.App/$($_.File)") -Algorithm SHA256).Hash -ne $_.Hash })
    Assert-Case ($incorrect.Count -eq 0 -and $assets.Files.Count -eq 19) 'all 19 binary resources match pinned hashes despite loose upstream resource contamination'
    $manifest = Get-Content -LiteralPath (Join-Path $fixture 'reference/PCL/manifest.json') -Raw | ConvertFrom-Json
    Assert-Case (@($manifest.Files).Count -eq 44 -and (Test-Path -LiteralPath $notes)) 'unrelated reference files are retained and excluded from the fixed source manifest'
    $again = Invoke-Import -Options @('-CheckOnly')
    Assert-Case ($again.ExitCode -eq 0 -and $again.Report.Mode -eq 'Check' -and (Snapshot -Times) -eq $before) 'repeated CheckOnly uses the fixed baseline without rewriting manifests or targets'

    $missing = @('reference/PCL/Controls/MyButton.xaml', 'src/PclDownloadBridge.App/Images/Themes/12.png', 'src/PclDownloadBridge.App/Images/Heads/LTCat.jpg', 'src/PclDownloadBridge.App/Controls/PclButton.xaml', 'reference/PCL/manifest.json', 'reference/pcl-assets.json', 'LICENCE')
    foreach ($relative in $missing) { Move-Aside $relative }
    $beforeMissing = Snapshot -Times
    $check = Invoke-Import -Options @('-CheckOnly')
    Assert-Case ($check.ExitCode -ne 0 -and @($check.Report.MissingFiles).Count -eq 7 -and (Snapshot -Times) -eq $beforeMissing) 'CheckOnly reports all missing files and does not fill them'
    $fill = Invoke-Import
    Assert-Case ($fill.ExitCode -eq 0 -and @($fill.Report.WrittenFiles).Count -eq 7) 'normal import fills missing snapshots, portraits, bootstrap controls, manifests and LICENCE'
    $manifest = Get-Content -LiteralPath (Join-Path $fixture 'reference/PCL/manifest.json') -Raw | ConvertFrom-Json
    $wrong = @($manifest.Files | Where-Object { (Get-FileHash -LiteralPath (Join-Path $fixture "reference/PCL/$($_.File)") -Algorithm SHA256).Hash -ne $_.Hash })
    Assert-Case ($wrong.Count -eq 0 -and @($manifest.Files).Count -eq 44) 'regenerated source manifest contains exactly the 44 verified snapshots'
    Assert-Case ((Get-FileHash -LiteralPath (Join-Path $fixture 'LICENCE')).Hash -eq (Get-FileHash -LiteralPath (Join-Path $root 'LICENCE')).Hash) 'regenerated LICENCE matches the fixed upstream bytes'

    $damagedReference = Join-Path $fixture 'reference/PCL/Controls/MyButton.xaml'
    [IO.File]::AppendAllText($damagedReference, 'Fixture-only reference conflict.')
    $lockedResource = Join-Path $fixture 'src/PclDownloadBridge.App/Images/Themes/12.png'
    [IO.File]::AppendAllText($lockedResource, 'Fixture-only resource conflict.')
    $assetPath = Join-Path $fixture 'reference/pcl-assets.json'
    $assetText = [IO.File]::ReadAllText($assetPath).Replace('5ef81e490ae46b1100d61e33d0183094c7a80ca8', '0000000000000000000000000000000000000000')
    [IO.File]::WriteAllText($assetPath, $assetText)
    [IO.File]::Move((Join-Path $fixture 'LICENCE'), (Join-Path $testRoot 'licence-before-conflict'))
    $beforeConflict = Snapshot -Times
    $refuse = Invoke-Import
    Assert-Case ($refuse.ExitCode -ne 0 -and @($refuse.Report.Conflicts).Count -eq 3) 'default import reports changed reference bytes, binary resources and manifest metadata as conflicts'
    Assert-Case ((Snapshot -Times) -eq $beforeConflict -and !(Test-Path -LiteralPath (Join-Path $fixture 'LICENCE'))) 'conflict preflight leaves every target and missing file untouched'
    $beforeRollback = Snapshot
    $handle = [IO.File]::Open($lockedResource, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { $failure = Invoke-Import -Options @('-ReplaceChanged') } finally { $handle.Dispose() }
    Assert-Case ($failure.ExitCode -ne 0 -and @($failure.Report.AppliedBeforeRollback).Count -eq 1) 'a locked later resource triggers failure after an earlier replacement'
    Assert-Case (@($failure.Report.RollbackErrors).Count -eq 0 -and (Snapshot) -eq $beforeRollback) 'failure rolls the earlier replacement back to the exact pre-import bytes'
    $retry = Invoke-Import -Options @('-ReplaceChanged')
    Assert-Case ($retry.ExitCode -eq 0 -and @($retry.Report.WrittenFiles).Count -eq 4) 'explicit replacement repairs only changed fixed files and fills the missing licence'
    Assert-Case ((Test-Path -LiteralPath (Join-Path $retry.Report.OperationDirectory 'previous/reference/PCL/Controls/MyButton.xaml')) -and (Test-Path -LiteralPath (Join-Path $retry.Report.OperationDirectory 'previous/src/PclDownloadBridge.App/Images/Themes/12.png'))) 'successful explicit replacements retain original conflicting files in backups'
    $healthy = Invoke-Import -Options @('-CheckOnly')
    Assert-Case ($healthy.ExitCode -eq 0 -and @($healthy.Report.WrittenFiles).Count -eq 0) 'repaired files pass a subsequent pinned baseline check'

    $emptyRepository = Join-Path $testRoot 'empty.git'
    & git init --bare --quiet -- $emptyRepository
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the missing-object fixture.' }
    $beforeMissingObject = Snapshot -Times
    $noObject = Invoke-Import -RepositoryPath $emptyRepository
    Assert-Case ($noObject.ExitCode -ne 0 -and $noObject.Report.Error -match 'Cannot read pinned PCL object' -and (Snapshot -Times) -eq $beforeMissingObject) 'unavailable pinned Git objects fail before changing any targets'
    $gatePath = Join-Path $fixture 'artifacts/pcl-import/import.lock'
    $gate = [IO.File]::Open($gatePath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try { $busy = Invoke-Import } finally { $gate.Dispose() }
    Assert-Case ($busy.ExitCode -ne 0 -and $busy.Text -match 'Another PCL import' -and (Snapshot -Times) -eq $beforeMissingObject) 'a second process cannot write targets while the import gate is held'
    $result = [pscustomobject]@{Succeeded=$true; Count=$passed.Count; Passed=@($passed); Fixture=$testRoot}
} catch {
    $result = [pscustomobject]@{Succeeded=$false; Count=$passed.Count; Passed=@($passed); Fixture=$testRoot; Error=$_.Exception.ToString()}
    throw
} finally {
    New-Item -ItemType Directory -Path (Split-Path ([IO.Path]::GetFullPath($ReportPath)) -Parent) -Force | Out-Null
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
}
