param([string]$SourceRoot, [switch]$CheckOnly, [switch]$ReplaceChanged)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path $PSScriptRoot -Parent
if (!$SourceRoot) { $SourceRoot = Join-Path (Split-Path $projectRoot -Parent) 'PCL' }
if ($CheckOnly -and $ReplaceChanged) { throw 'CheckOnly cannot be combined with ReplaceChanged.' }
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).ProviderPath
$baseline = '5ef81e490ae46b1100d61e33d0183094c7a80ca8'
$git = (Get-Command git -CommandType Application | Select-Object -First 1).Source
$utf8 = New-Object Text.UTF8Encoding($false, $true)

function Read-GitBlob([string]$Relative) {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $git
    $info.Arguments = '-C "' + $SourceRoot + '" cat-file blob "' + $baseline + ':' + $Relative + '"'
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $info
    $memory = New-Object IO.MemoryStream
    try {
        $null = $process.Start()
        $errorText = $process.StandardError.ReadToEndAsync()
        $process.StandardOutput.BaseStream.CopyTo($memory)
        $process.WaitForExit()
        $null = $errorText.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "Cannot read pinned PCL object: $Relative" }
        return ,$memory.ToArray()
    } finally { $memory.Dispose(); $process.Dispose() }
}

function Normalize-Source([byte[]]$Bytes) {
    $text = $utf8.GetString($Bytes).TrimStart([char]0xFEFF).Replace("`r`n", "`n").Replace("`r", "`n")
    if (!$text.EndsWith("`n")) { $text += "`n" }
    return $text
}

function Get-ByteHash([byte[]]$Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($Bytes)).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Assert-Target([string]$Path) {
    $prefix = [IO.Path]::GetFullPath($projectRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $full = [IO.Path]::GetFullPath($Path)
    if (!$full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Target is outside the project: $Path" }
    $current = $full
    while ($current -and $current -ne $projectRoot) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked import target is not supported: $current" }
        }
        $current = Split-Path $current -Parent
    }
}

function Add-Candidate([string]$Relative, [byte[]]$Bytes, [bool]$Adapter = $false) {
    $target = Join-Path $projectRoot $Relative
    Assert-Target $target
    $candidate = Join-Path $operation "candidate/$Relative"
    New-Item -ItemType Directory -Path (Split-Path $candidate -Parent) -Force | Out-Null
    [IO.File]::WriteAllBytes($candidate, $Bytes)
    $exists = Test-Path -LiteralPath $target -PathType Leaf
    $originalHash = if ($exists) { (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash } else { '' }
    $status = if ($Adapter -and $exists) { 'PreservedAdapter' } elseif ($exists -and $originalHash -eq (Get-ByteHash $Bytes)) { 'Unchanged' } elseif (Test-Path -LiteralPath $target) { 'Conflict' } else { 'Missing' }
    $plan.Add([pscustomobject]@{File=$Relative; Target=$target; Candidate=$candidate; Hash=(Get-ByteHash $Bytes); OriginalHash=$originalHash; Status=$status})
}

function Add-Manifest([string]$Relative, $Expected, [bool]$Assets) {
    $bytes = $utf8.GetBytes(($Expected | ConvertTo-Json -Depth 6) + "`n")
    $target = Join-Path $projectRoot $Relative
    if (Test-Path -LiteralPath $target -PathType Leaf) {
        try {
            $existingBytes = [IO.File]::ReadAllBytes($target)
            $existing = [Text.Encoding]::UTF8.GetString($existingBytes).TrimStart([char]0xFEFF) | ConvertFrom-Json
            $same = $existing.Repository -eq $Expected.Repository -and $existing.Commit -eq $Expected.Commit -and @($existing.Files).Count -eq @($Expected.Files).Count
            if ($Assets) { $same = $same -and $existing.Algorithm -eq 'SHA256' }
            $seen = @{}
            foreach ($entry in $existing.Files) {
                $name = $entry.File.Replace('\', '/')
                $wanted = @($Expected.Files | Where-Object { $_.File.Replace('\', '/') -ceq $name })
                if ($seen.ContainsKey($name) -or $wanted.Count -ne 1 -or $wanted[0].Hash -ne $entry.Hash) { $same = $false; break }
                if ($Assets -and $entry.Source -cne $wanted[0].Source) { $same = $false; break }
                $seen[$name] = $true
            }
            # Keep valid manifests byte-for-byte, including their original ordering and encoding.
            if ($same) { $bytes = $existingBytes }
        } catch { # Invalid or unreadable metadata is treated as a conflict by Add-Candidate.
        }
    }
    Add-Candidate $Relative $bytes
}

$records = Join-Path $projectRoot 'artifacts/pcl-import'
Assert-Target $records
New-Item -ItemType Directory -Path $records -Force | Out-Null
try { $gate = [IO.File]::Open((Join-Path $records 'import.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
catch { throw 'Another PCL import is running or the import lock is unavailable.' }
$operation = Join-Path $records ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
$plan = [Collections.Generic.List[object]]::new()
$applied = [Collections.Generic.List[object]]::new()
$report = [ordered]@{Succeeded=$false; Mode=$(if ($CheckOnly) {'Check'} else {'Import'}); Commit=$baseline; SourceRoot=$SourceRoot; PlannedFiles=0; WrittenFiles=@(); PreservedAdapters=@(); MissingFiles=@(); Conflicts=@(); RollbackErrors=@(); OperationDirectory=$operation; ReportPath=(Join-Path $operation 'result.json')}
try {
    New-Item -ItemType Directory -Path $operation -Force | Out-Null
    $files = @('Controls/MyButton.xaml', 'Controls/MyButton.xaml.vb', 'Controls/MyRadioButton.xaml', 'Controls/MyRadioButton.xaml.vb', 'Controls/MyListItem.xaml', 'Controls/MyListItem.xaml.vb', 'Controls/MyCard.vb', 'Controls/MyExtraTextButton.xaml', 'FormMain.xaml', 'Application.xaml', 'Pages/PageDownload/PageDownloadLeft.xaml', 'Pages/PageDownload/PageDownloadInstall.xaml', 'Pages/PageDownload/PageDownloadInstall.xaml.vb', 'Pages/PageSelectLeft.xaml.vb', 'Modules/Base/ModNet.vb', 'Modules/Minecraft/ModDownload.vb', 'Pages/PageDownload/ModDownloadLib.vb', 'Pages/PageSpeedRight.xaml', 'Pages/PageSpeedLeft.xaml')
    $files += @('Controls/MyComboBox.vb', 'Controls/MyComboBoxItem.vb', 'Controls/MyCheckBox.xaml', 'Controls/MyCheckBox.xaml.vb', 'Controls/MyTextBox.vb', 'Controls/MySlider.xaml', 'Controls/MySlider.xaml.vb', 'Pages/PageSetup/PageSetupSystem.xaml', 'Modules/ModSecret.vb')
    $files += @('Controls/MyDropShadow.vb', 'Pages/PageSpeedLeft.xaml.vb', 'Controls/MyIconButton.xaml', 'Controls/MyIconButton.xaml.vb')
    $files += @('Modules/Base/ModValidate.vb', 'Controls/MyHint.xaml', 'Controls/MyHint.xaml.vb', 'Modules/Minecraft/ModMinecraft.vb')
    $files += @('Controls/MyVirtualizingElement.vb', 'Controls/Behaviors/LazyLoadBehavior.vb', 'Pages/PageSetup/PageSetupUI.xaml', 'Pages/PageSetup/Settings.vb', 'Controls/MyRadioBox.xaml', 'Controls/MyRadioBox.xaml.vb', 'Pages/PageOther/PageOtherAbout.xaml', 'Pages/PageOther/PageOtherAbout.xaml.vb')
    $texts = @{}; $sourceEntries = @()
    foreach ($relative in $files) {
        $text = Normalize-Source (Read-GitBlob "Plain Craft Launcher 2/$relative")
        $texts[$relative] = $text
        $bytes = $utf8.GetBytes($text)
        Add-Candidate "reference/PCL/$relative" $bytes
        $sourceEntries += [pscustomobject]@{File=$relative; Hash=(Get-ByteHash $bytes)}
    }
    $assets = @('Grass', 'CommandBlock', 'CobbleStone', 'GoldBlock', 'Anvil', 'NeoForge', 'Fabric', 'GrassPath', 'OptiFabric', 'Egg') | ForEach-Object { "Images/Blocks/$_.png" }
    $assets += @('Resources/Font.ttf', 'Images/Themes/12.png')
    $assets += @('Images/Heads/LTCat.jpg', 'Images/Heads/HerobrineXia.png', 'Images/Heads/bangbang93.png')
    $mappedAssets = @{'Images/Blocks/Release.png'='Images/ReleaseTypes/Release.png'; 'Images/Blocks/Beta.png'='Images/ReleaseTypes/Beta.png';
        'Images/Blocks/Alpha.png'='Images/ReleaseTypes/Alpha.png'; 'Images/Blocks/NoIcon.png'='Images/Icons/NoIcon.png'}
    $assets += @('Images/Blocks/Release.png','Images/Blocks/Beta.png','Images/Blocks/Alpha.png','Images/Blocks/NoIcon.png')
    $assets += @('Images/Icons/Disabled.png')
    $assetEntries = @()
    foreach ($relative in $assets) {
        $source = 'Plain Craft Launcher 2/' + $(if ($mappedAssets.ContainsKey($relative)) { $mappedAssets[$relative] } else { $relative })
        $bytes = Read-GitBlob $source
        Add-Candidate "src/PclDownloadBridge.App/$relative" $bytes
        $assetEntries += [pscustomobject]@{File=$relative; Source=$source; Hash=(Get-ByteHash $bytes)}
    }
    Add-Candidate 'LICENCE' (Read-GitBlob 'LICENCE')
    Add-Manifest 'reference/PCL/manifest.json' ([ordered]@{Repository='Meloong-Git/PCL'; Commit=$baseline; Files=$sourceEntries}) $false
    Add-Manifest 'reference/pcl-assets.json' ([ordered]@{Repository='Meloong-Git/PCL'; Commit=$baseline; Algorithm='SHA256'; Files=$assetEntries}) $true
    foreach ($name in @('Button', 'RadioButton', 'ListItem', 'IconButton')) {
        $text = $texts["Controls/My$name.xaml"].Replace("x:Class=`"My$name`"", "x:Class=`"Controls.Pcl$name`"")
        Add-Candidate "src/PclDownloadBridge.App/Controls/Pcl$name.xaml" ($utf8.GetBytes($text)) $true
    }
    $shadow = $texts['Controls/MyDropShadow.vb'].Replace('MyDropShadow', 'PclDropShadow').
        Replace('Return GetValue(ColorProperty)', 'Return CType(GetValue(ColorProperty), Color)').
        Replace('Return GetValue(ShadowRadiusProperty)', 'Return CDbl(GetValue(ShadowRadiusProperty))').
        Replace('Return GetValue(CornerRadiusProperty)', 'Return CType(GetValue(CornerRadiusProperty), CornerRadius)').
        Replace('Dim cr As CornerRadius = value', 'Dim cr As CornerRadius = CType(value, CornerRadius)')
    $shadow = "Imports System.Windows`nImports System.Windows.Controls`nImports System.Windows.Media`n`nNamespace Controls`n" + $shadow + "End Namespace`n"
    Add-Candidate 'src/PclDownloadBridge.App/Controls/PclDropShadow.vb' ($utf8.GetBytes($shadow)) $true
    [xml]$application = $texts['Application.xaml']
    $palette = @($application.Application.'Application.Resources'.ResourceDictionary.ChildNodes | Where-Object { $_.LocalName -in @('SolidColorBrush', 'Color') } | ForEach-Object { $_.OuterXml }) -join "`n"
    $dictionary = '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">' + "`n" + $palette + "`n</ResourceDictionary>"
    Add-Candidate 'src/PclDownloadBridge.App/Controls/PclPalette.xaml' ($utf8.GetBytes($dictionary)) $true
    [xml]$main = $texts['FormMain.xaml']
    $icons = '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:sys="clr-namespace:System;assembly=mscorlib">'
    foreach ($node in $main.SelectNodes('//*[local-name()="MyRadioButton"]')) {
        if ($node.GetAttribute('Text') -in @('下载', '设置')) { $icons += '<sys:String x:Key="IconTitle' + $node.GetAttribute('Text') + '">' + [Security.SecurityElement]::Escape($node.GetAttribute('Logo')) + '</sys:String>' }
    }
    Add-Candidate 'src/PclDownloadBridge.App/Controls/PclIcons.xaml' ($utf8.GetBytes($icons + '</ResourceDictionary>')) $true
    $report.PlannedFiles = $plan.Count
    $report.PreservedAdapters = @($plan | Where-Object Status -eq PreservedAdapter | ForEach-Object File)
    $report.MissingFiles = @($plan | Where-Object Status -eq Missing | ForEach-Object File)
    $report.Conflicts = @($plan | Where-Object Status -eq Conflict | ForEach-Object File)
    if ($CheckOnly -and ($report.MissingFiles.Count -or $report.Conflicts.Count)) { throw 'Pinned import check found missing or changed fixed files; no targets were written.' }
    if ($report.Conflicts.Count -and !$ReplaceChanged) { throw 'Fixed-file conflicts detected; no targets were written. Review the report before using ReplaceChanged.' }
    if (!$CheckOnly) {
        foreach ($entry in @($plan | Where-Object { $_.Status -in @('Missing', 'Conflict') })) {
            Assert-Target $entry.Target
            if ($entry.Status -eq 'Missing') {
                if (Test-Path -LiteralPath $entry.Target) { throw "Target appeared during import: $($entry.File)" }
            } elseif ((Get-FileHash -LiteralPath $entry.Target -Algorithm SHA256).Hash -ne $entry.OriginalHash) { throw "Target changed during import: $($entry.File)" }
            $backup = Join-Path $operation "previous/$($entry.File)"
            Assert-Target $backup
            New-Item -ItemType Directory -Path (Split-Path $entry.Target -Parent), (Split-Path $backup -Parent) -Force | Out-Null
            if ($entry.Status -eq 'Missing') { [IO.File]::Move($entry.Candidate, $entry.Target) }
            else { [IO.File]::Replace($entry.Candidate, $entry.Target, $backup, $true) }
            $applied.Add([pscustomobject]@{Entry=$entry; Backup=$backup})
        }
    }
    $report.WrittenFiles = @($applied | ForEach-Object { $_.Entry.File })
    $report.Succeeded = $true
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $report.ReportPath -Encoding UTF8
} catch {
    $failure = $_.Exception.Message
    $report.Succeeded = $false
    $report.WrittenFiles = @()
    $rollbackErrors = @()
    for ($index = $applied.Count - 1; $index -ge 0; $index--) {
        $entry = $applied[$index].Entry
        try {
            Assert-Target $entry.Target
            if ((Get-FileHash -LiteralPath $entry.Target -Algorithm SHA256).Hash -ne $entry.Hash) { throw "Rollback target changed: $($entry.File)" }
            $failed = Join-Path $operation "rolled-back/$($entry.File)"
            Assert-Target $failed
            New-Item -ItemType Directory -Path (Split-Path $failed -Parent) -Force | Out-Null
            if ($entry.Status -eq 'Missing') { [IO.File]::Move($entry.Target, $failed) }
            else { [IO.File]::Replace($applied[$index].Backup, $entry.Target, $failed, $true) }
        } catch { $rollbackErrors += $_.Exception.Message }
    }
    $report['AppliedBeforeRollback'] = @($applied | ForEach-Object { $_.Entry.File })
    $report.RollbackErrors = $rollbackErrors
    $report['Error'] = $failure
    try { $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $report.ReportPath -Encoding UTF8 }
    catch { $failure += " Could not save the report: $($_.Exception.Message)" }
    throw "PCL import failed: $failure Report: $($report.ReportPath)"
} finally {
    $gate.Dispose()
}
$report | ConvertTo-Json -Depth 6
