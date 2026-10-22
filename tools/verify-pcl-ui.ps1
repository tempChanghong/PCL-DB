$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$sourceManifest = Get-Content -LiteralPath (Join-Path $root 'reference/PCL/manifest.json') -Raw | ConvertFrom-Json
if ($sourceManifest.Repository -ne 'Meloong-Git/PCL' -or $sourceManifest.Commit -ne '5ef81e490ae46b1100d61e33d0183094c7a80ca8' -or @($sourceManifest.Files).Count -ne 44) { throw 'Unexpected PCL source baseline.' }
$sourceFiles = @{}
foreach ($entry in $sourceManifest.Files) {
    if ($sourceFiles.ContainsKey($entry.File)) { throw "Duplicate PCL source snapshot: $($entry.File)" }
    $sourceFiles[$entry.File] = $true
    $path = Join-Path $root "reference/PCL/$($entry.File)"
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.Hash) { throw "PCL source snapshot bytes mismatch: $($entry.File)" }
}
Write-Output 'PASS all 44 fixed PCL source snapshot hashes'
$assetManifest = Get-Content -LiteralPath (Join-Path $root 'reference/pcl-assets.json') -Raw | ConvertFrom-Json
if ($assetManifest.Repository -ne 'Meloong-Git/PCL' -or $assetManifest.Commit -ne '5ef81e490ae46b1100d61e33d0183094c7a80ca8' -or @($assetManifest.Files).Count -ne 19) { throw 'Unexpected PCL asset baseline.' }
function Assert-PclAsset([string]$relative) {
    $entry = @($assetManifest.Files | Where-Object File -eq $relative)
    if ($entry.Count -ne 1) { throw "Missing or duplicate PCL asset hash: $relative" }
    $actual = Join-Path $root "src/PclDownloadBridge.App/$relative"
    if ((Get-FileHash -LiteralPath $actual -Algorithm SHA256).Hash -ne $entry[0].Hash) { throw "PCL asset bytes mismatch: $relative" }
}
foreach ($name in @('Button', 'RadioButton', 'ListItem', 'IconButton')) {
    $original = [IO.File]::ReadAllText((Join-Path $root "reference/PCL/Controls/My$name.xaml")).Replace("x:Class=`"My$name`"", "x:Class=`"Controls.Pcl$name`"")
    $actual = [IO.File]::ReadAllText((Join-Path $root "src/PclDownloadBridge.App/Controls/Pcl$name.xaml"))
    if ($original.Replace("`r`n", "`n") -ne $actual.Replace("`r`n", "`n")) { throw "PCL original XAML mismatch: $name" }
    Write-Output "PASS original PCL $name XAML (only class name adapted)"
}
foreach ($image in @('Grass', 'CommandBlock', 'CobbleStone', 'GoldBlock', 'Anvil', 'NeoForge', 'Fabric', 'GrassPath', 'OptiFabric', 'Egg')) {
    Assert-PclAsset "Images/Blocks/$image.png"
}
Write-Output 'PASS original PCL block-image bytes'
foreach ($name in @('Release','Beta','Alpha','NoIcon')) { Assert-PclAsset "Images/Blocks/$name.png" }
Write-Output 'PASS original PCL release-type and fallback artwork bytes'
foreach ($name in @('LTCat.jpg', 'HerobrineXia.png', 'bangbang93.png')) { Assert-PclAsset "Images/Heads/$name" }
Write-Output 'PASS three original PCL about-page portraits'
[xml]$sourceApplication = Get-Content -LiteralPath (Join-Path $root 'reference/PCL/Application.xaml') -Raw
[xml]$sourceCheck = Get-Content -LiteralPath (Join-Path $root 'reference/PCL/Controls/MyCheckBox.xaml') -Raw
[xml]$selection = Get-Content -LiteralPath (Join-Path $root 'src/PclDownloadBridge.App/Controls/PclSelectionStyles.xaml') -Raw
$sourceArrow = $sourceApplication.SelectSingleNode('//*[local-name()="Style" and @TargetType="ToggleButton"]//*[local-name()="Path"]')
$actualArrow = $selection.SelectSingleNode('//*[local-name()="Style" and @*[local-name()="Key"]="PclComboToggle"]//*[local-name()="Path"]')
foreach ($attribute in @('Data','Width','StrokeThickness','RenderTransformOrigin')) {
    if ($sourceArrow.GetAttribute($attribute) -ne $actualArrow.GetAttribute($attribute)) { throw "PCL selector arrow mismatch: $attribute" }
}
$sourceOutline = $sourceCheck.SelectSingleNode('//*[local-name()="Border"]')
$actualOutline = $selection.SelectSingleNode('//*[local-name()="Border" and @*[local-name()="Name"]="ShapeBorder"]')
foreach ($attribute in @('Width','Height','CornerRadius','BorderThickness','Margin')) {
    if ($sourceOutline.GetAttribute($attribute) -ne $actualOutline.GetAttribute($attribute)) { throw "PCL checkbox outline mismatch: $attribute" }
}
$sourceMark = $sourceCheck.SelectSingleNode('//*[local-name()="Path"]')
$actualMark = $selection.SelectSingleNode('//*[local-name()="Path" and @*[local-name()="Name"]="ShapeCheck"]')
foreach ($attribute in @('Data','Width','Height','Margin')) {
    if ($sourceMark.GetAttribute($attribute) -ne $actualMark.GetAttribute($attribute)) { throw "PCL checkbox mark mismatch: $attribute" }
}
Write-Output 'PASS PCL selector-arrow and checkbox geometry match the fixed source'
Assert-PclAsset 'Resources/Font.ttf'
Write-Output 'PASS original PCL embedded font bytes'
$shadowSource = [IO.File]::ReadAllText((Join-Path $root 'reference/PCL/Controls/MyDropShadow.vb'))
$shadowExpected = $shadowSource.Replace('MyDropShadow', 'PclDropShadow').
    Replace('Return GetValue(ColorProperty)', 'Return CType(GetValue(ColorProperty), Color)').
    Replace('Return GetValue(ShadowRadiusProperty)', 'Return CDbl(GetValue(ShadowRadiusProperty))').
    Replace('Return GetValue(CornerRadiusProperty)', 'Return CType(GetValue(CornerRadiusProperty), CornerRadius)').
    Replace('Dim cr As CornerRadius = value', 'Dim cr As CornerRadius = CType(value, CornerRadius)')
$shadowExpected = "Imports System.Windows`nImports System.Windows.Controls`nImports System.Windows.Media`n`nNamespace Controls`n" + $shadowExpected + "End Namespace`n"
$shadowActual = [IO.File]::ReadAllText((Join-Path $root 'src/PclDownloadBridge.App/Controls/PclDropShadow.vb'))
if ($shadowExpected.Replace("`r`n", "`n") -ne $shadowActual.Replace("`r`n", "`n")) { throw 'PCL gradient shadow source differs beyond namespace, class name and explicit casts.' }
Write-Output 'PASS original PCL gradient shadow drawing (only namespace, class name and strict casts adapted)'
$runtimeTasks = [regex]::Replace([IO.File]::ReadAllText((Join-Path $root 'reference/PCL/Pages/PageSpeedLeft.xaml.vb')), '\s+', '')
$taskAdapter = [IO.File]::ReadAllText((Join-Path $root 'src/PclDownloadBridge.App/MainWindow.Tasks.vb'))
foreach ($icon in @('CompleteIcon','WaitingIcon','FailedIcon')) {
    $match = [regex]::Match($taskAdapter, ('Private Const ' + $icon + ' As String = "([^"]+)"'))
    if (!$match.Success -or !$runtimeTasks.Contains([regex]::Replace($match.Groups[1].Value, '\s+', ''))) { throw "PCL runtime task vector mismatch: $icon" }
}
Write-Output 'PASS PCL runtime waiting, completed and failed task vector geometry'
[xml]$sourceSlider = Get-Content -LiteralPath (Join-Path $root 'reference/PCL/Controls/MySlider.xaml') -Raw
$sourceDot = $sourceSlider.SelectSingleNode('//*[local-name()="Ellipse"]')
$actualThumb = $selection.SelectSingleNode('//*[local-name()="Style" and @TargetType="p:PclSlider"]//*[local-name()="Thumb"]')
$actualDot = $selection.SelectSingleNode('//*[local-name()="Ellipse" and @*[local-name()="Name"]="ShapeDot"]')
foreach ($attribute in @('Width','Height')) {
    if ($sourceDot.GetAttribute($attribute) -ne $actualThumb.GetAttribute($attribute)) { throw "PCL slider thumb mismatch: $attribute" }
}
if ($sourceDot.GetAttribute('StrokeThickness') -ne $actualDot.GetAttribute('StrokeThickness')) { throw 'PCL slider outline mismatch.' }
[xml]$sourceSettings = Get-Content -LiteralPath (Join-Path $root 'reference/PCL/Pages/PageSetup/PageSetupSystem.xaml') -Raw
[xml]$actualWindow = Get-Content -LiteralPath (Join-Path $root 'src/PclDownloadBridge.App/MainWindow.xaml') -Raw
[xml]$sourceInstall = Get-Content -LiteralPath (Join-Path $root 'reference/PCL/Pages/PageDownload/PageDownloadInstall.xaml') -Raw
$sourceBack = $sourceInstall.SelectSingleNode('//*[@*[local-name()="Name"]="BtnBack"]')
$actualBack = $actualWindow.SelectSingleNode('//*[@*[local-name()="Name"]="InstallBack"]')
foreach ($attribute in @('Logo','LogoScale','Width','Height','Opacity','Margin')) {
    if ($sourceBack.GetAttribute($attribute) -ne $actualBack.GetAttribute($attribute)) { throw "PCL install-back icon mismatch: $attribute" }
}
Write-Output 'PASS PCL install-back icon geometry, size, inset, scale and opacity'
$sourceDownload = $sourceSettings.SelectSingleNode('//*[local-name()="MyCard" and @Title="下载"]')
$sourceRows = $sourceDownload.SelectNodes('.//*[local-name()="RowDefinition"]')
$actualSettings = $actualWindow.SelectSingleNode('//*[local-name()="Grid" and @*[local-name()="Name"]="DownloadSettingsGrid"]')
$actualRows = $actualSettings.SelectNodes('./*[local-name()="Grid.RowDefinitions"]/*')
foreach ($index in @(0,1,2,3)) {
    if ($sourceRows[$index].GetAttribute('Height') -ne $actualRows[$index].GetAttribute('Height')) { throw "PCL settings row mismatch: $index" }
}
$sourceContent = $sourceDownload.SelectSingleNode('./*[local-name()="StackPanel"]')
$actualCard = $actualWindow.SelectSingleNode('//*[local-name()="PclCard" and @*[local-name()="Name"]="DownloadSettingsCard"]')
if ($sourceContent.GetAttribute('Margin') -ne $actualCard.GetAttribute('BodyMargin')) { throw 'PCL settings card body margins mismatch.' }
Write-Output 'PASS PCL slider geometry and compact settings row/margin values match fixed source'
foreach ($pair in @(@('ComboDownloadSource','SourceBox'), @('ComboDownloadVersion','VersionSourceBox'))) {
    $originalSource = $sourceDownload.SelectSingleNode('.//*[@*[local-name()="Name"]="' + $pair[0] + '"]')
    $actualSource = $actualSettings.SelectSingleNode('.//*[@*[local-name()="Name"]="' + $pair[1] + '"]')
    if ($originalSource.GetAttribute('Grid.Row') -ne $actualSource.GetAttribute('Grid.Row')) { throw "PCL source-selector row mismatch: $($pair[1])" }
    $originalItems = @($originalSource.ChildNodes | Where-Object LocalName -eq 'MyComboBoxItem')
    $actualItems = @($actualSource.ChildNodes | Where-Object LocalName -eq 'PclComboBoxItem')
    if ($originalItems.Count -ne 3 -or $actualItems.Count -ne 3 -or $actualSource.GetAttribute('SelectedIndex') -ne '1') { throw 'PCL source-selector defaults or item count mismatch.' }
    foreach ($index in @(0,1,2)) {
        if ($originalItems[$index].GetAttribute('Content').Replace('镜像源',' BMCLAPI') -ne $actualItems[$index].GetAttribute('Content')) { throw "PCL source-selector choice mismatch: $($pair[1])/$index" }
    }
}
Write-Output 'PASS two PCL source selectors, original choice order and automatic defaults (mirror explicitly named BMCLAPI)'

[xml]$hintSource = Get-Content -LiteralPath (Join-Path $root 'reference/PCL/Controls/MyHint.xaml') -Raw
$hintCode = Get-Content -LiteralPath (Join-Path $root 'src/PclDownloadBridge.App/Controls/PclHint.vb') -Raw
$hintText = $hintSource.SelectSingleNode('//*[local-name()="TextBlock"]')
if ($hintSource.DocumentElement.GetAttribute('BorderThickness') -ne '3,0,0,0' -or
    $hintSource.DocumentElement.GetAttribute('CornerRadius') -ne '2' -or
    $hintText.GetAttribute('Padding') -ne '12,9' -or $hintText.GetAttribute('LineHeight') -ne '16' -or
    $hintText.GetAttribute('Margin') -ne '0,0,0,-1' -or
    !$hintCode.Contains('New Thickness(3, 0, 0, 0)') -or !$hintCode.Contains('New CornerRadius(2)') -or
    !$hintCode.Contains('New Thickness(12, 9, 12, 9)') -or !$hintCode.Contains('.LineHeight = 16')) { throw 'PCL name-hint geometry mismatch.' }
$hintTheme = Get-Content -LiteralPath (Join-Path $root 'reference/PCL/Controls/MyHint.xaml.vb') -Raw
foreach ($pair in @(@('#ffdddf','255, 221, 223'), @('#d82929','216, 41, 41'), @('#bf0b0b','191, 11, 11'))) {
    if (!$hintTheme.Contains($pair[0]) -or !$hintCode.Contains('Color.FromRgb(' + $pair[1] + ')')) { throw 'PCL name-hint red theme mismatch.' }
}
Write-Output 'PASS PCL red name-hint geometry and theme from fixed MyHint source'

$versionSource = Get-Content -LiteralPath (Join-Path $root 'reference/PCL/Modules/Minecraft/ModMinecraft.vb') -Raw
$versionAdapter = Get-Content -LiteralPath (Join-Path $root 'src/PclDownloadBridge.App/VersionPresentation.vb') -Raw
$descriptions = [regex]::Matches($versionAdapter, '(?m)^\s*Lore = "([^"]+)"[ \t]*\r?$')
if ($descriptions.Count -ne 10) { throw 'PCL April Fools description count mismatch.' }
foreach ($description in $descriptions) {
    if (!$versionSource.Contains('Return "' + $description.Groups[1].Value + '"')) { throw 'PCL April Fools description mismatch.' }
}
Write-Output 'PASS all ten April Fools descriptions match fixed PCL GetMcFoolName source'

[xml]$radioSource = Get-Content -LiteralPath (Join-Path $root 'reference/PCL/Controls/MyRadioBox.xaml') -Raw
$radioStyle = $selection.SelectSingleNode('//*[local-name()="Style" and @*[local-name()="Key"]="PclAppearanceRadio"]')
$radioGlyph = $radioStyle.SelectSingleNode('.//*[local-name()="PclRadioGlyph" and @*[local-name()="Name"]="Glyph"]')
$glyphCode = Get-Content -LiteralPath (Join-Path $root 'src/PclDownloadBridge.App/Controls/PclRadioGlyph.vb') -Raw
$sourceRing = $radioSource.SelectSingleNode('//*[local-name()="Ellipse" and @*[local-name()="Name"]="ShapeBorder"]')
if ($radioGlyph.GetAttribute('Width') -ne '20' -or $radioGlyph.GetAttribute('Height') -ne '20' -or
    !$glyphCode.Contains('RingSize As Double = ' + $sourceRing.GetAttribute('Width')) -or
    !$glyphCode.Contains('OutlineThickness As Double = ' + $sourceRing.GetAttribute('StrokeThickness')) -or
    $sourceRing.GetAttribute('Margin') -ne '1,0,0,0' -or !$glyphCode.Contains('New Point(10, 10)')) {
    throw 'PCL appearance radio ring geometry mismatch.'
}
$radioCode = Get-Content -LiteralPath (Join-Path $root 'reference/PCL/Controls/MyRadioBox.xaml.vb') -Raw
if (!$radioCode.Contains('ShapeDot.Width = 9') -or !$glyphCode.Contains('DotSize As Double = 9')) { throw 'PCL appearance radio dot mismatch.' }
Assert-PclAsset 'Images/Themes/12.png'
Write-Output 'PASS PCL appearance radio geometry and original title texture bytes'

[xml]$sourceWindow = Get-Content -LiteralPath (Join-Path $root 'reference/PCL/FormMain.xaml') -Raw
foreach ($pair in @(@('BtnTitleMin','TitleMinimize'), @('BtnTitleClose','TitleClose'))) {
    $original = $sourceWindow.SelectSingleNode('//*[@*[local-name()="Name"]="' + $pair[0] + '"]')
    $actual = $actualWindow.SelectSingleNode('//*[@*[local-name()="Name"]="' + $pair[1] + '"]')
    foreach ($attribute in @('Width','Height','Theme','LogoScale','Logo')) {
        if ($original.GetAttribute($attribute) -ne $actual.GetAttribute($attribute)) { throw "PCL title icon mismatch: $($pair[1])/$attribute" }
    }
}
Write-Output 'PASS both PCL white title icon geometries, 28 DIP hit areas and 0.72 logo scale'
[xml]$barStyle = Get-Content -LiteralPath (Join-Path $root 'src/PclDownloadBridge.App/Controls/PclScrollBarStyle.xaml') -Raw
$sourceBar = $sourceApplication.SelectSingleNode('//*[local-name()="Style" and @TargetType="local:MyScrollBar"]')
$sourceThumb = $sourceApplication.SelectSingleNode('//*[local-name()="Style" and @*[local-name()="Key"]="MyThumb"]//*[local-name()="Border" and @Margin]')
$actualThumb = $barStyle.SelectSingleNode('//*[local-name()="ControlTemplate" and @TargetType="Thumb"]//*[local-name()="Border" and @Margin]')
if ($barStyle.SelectSingleNode('//*[local-name()="Style" and @TargetType="ScrollBar"]/*[local-name()="Setter" and @Property="Width"]').GetAttribute('Value') -ne $sourceBar.SelectSingleNode('./*[local-name()="Setter" and @Property="Width"]').GetAttribute('Value')) { throw 'PCL scrollbar hit width mismatch.' }
foreach ($attribute in @('CornerRadius','Margin')) {
    if ($sourceThumb.GetAttribute($attribute) -ne $actualThumb.GetAttribute($attribute)) { throw "PCL scrollbar thumb mismatch: $attribute" }
}
Write-Output 'PASS PCL scrollbar 8 DIP hit area, 2 DIP thumb inset and corner radius'
