$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$sourceRoot = Join-Path (Split-Path $projectRoot -Parent) 'PCL'
$baseline = '5ef81e490ae46b1100d61e33d0183094c7a80ca8'
$appRoot = Join-Path $projectRoot 'src/PlainCraftDownloader.App'
$referenceRoot = Join-Path $projectRoot 'reference/PCL'
New-Item -ItemType Directory -Path (Join-Path $appRoot 'Controls'), (Join-Path $appRoot 'Images/Blocks'), $referenceRoot -Force | Out-Null
$files = @('Controls/MyButton.xaml', 'Controls/MyButton.xaml.vb', 'Controls/MyRadioButton.xaml', 'Controls/MyRadioButton.xaml.vb', 'Controls/MyListItem.xaml', 'Controls/MyListItem.xaml.vb', 'Controls/MyCard.vb', 'Controls/MyExtraTextButton.xaml', 'FormMain.xaml', 'Application.xaml', 'Pages/PageDownload/PageDownloadLeft.xaml', 'Pages/PageDownload/PageDownloadInstall.xaml', 'Pages/PageDownload/PageDownloadInstall.xaml.vb', 'Pages/PageSelectLeft.xaml.vb', 'Modules/Base/ModNet.vb', 'Modules/Minecraft/ModDownload.vb', 'Pages/PageDownload/ModDownloadLib.vb', 'Pages/PageSpeedRight.xaml', 'Pages/PageSpeedLeft.xaml')
$files += @('Controls/MyComboBox.vb', 'Controls/MyComboBoxItem.vb', 'Controls/MyCheckBox.xaml', 'Controls/MyCheckBox.xaml.vb', 'Controls/MyTextBox.vb', 'Controls/MySlider.xaml', 'Controls/MySlider.xaml.vb', 'Pages/PageSetup/PageSetupSystem.xaml', 'Modules/ModSecret.vb')
$files += @('Controls/MyDropShadow.vb', 'Pages/PageSpeedLeft.xaml.vb', 'Controls/MyIconButton.xaml', 'Controls/MyIconButton.xaml.vb')
$files += @('Modules/Base/ModValidate.vb', 'Controls/MyHint.xaml', 'Controls/MyHint.xaml.vb')
$files += @('Modules/Minecraft/ModMinecraft.vb')
$files += @('Controls/MyVirtualizingElement.vb', 'Controls/Behaviors/LazyLoadBehavior.vb')
$files += @('Pages/PageSetup/PageSetupUI.xaml', 'Pages/PageSetup/Settings.vb', 'Controls/MyRadioBox.xaml', 'Controls/MyRadioBox.xaml.vb')
$files += @('Pages/PageOther/PageOtherAbout.xaml', 'Pages/PageOther/PageOtherAbout.xaml.vb')
foreach ($relative in $files) {
    $lines = git -C $sourceRoot show "${baseline}:Plain Craft Launcher 2/$relative"
    if ($LASTEXITCODE -ne 0) { throw "Cannot read PCL source: $relative" }
    $text = ($lines -join "`n") + "`n"
    $destination = Join-Path $referenceRoot $relative
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    [IO.File]::WriteAllText($destination, $text, (New-Object Text.UTF8Encoding($false)))
    if ($relative -eq 'Controls/MyDropShadow.vb') {
        $adapted = $text.Replace('MyDropShadow', 'PclDropShadow').
            Replace('Return GetValue(ColorProperty)', 'Return CType(GetValue(ColorProperty), Color)').
            Replace('Return GetValue(ShadowRadiusProperty)', 'Return CDbl(GetValue(ShadowRadiusProperty))').
            Replace('Return GetValue(CornerRadiusProperty)', 'Return CType(GetValue(CornerRadiusProperty), CornerRadius)').
            Replace('Dim cr As CornerRadius = value', 'Dim cr As CornerRadius = CType(value, CornerRadius)')
        $adapted = "Imports System.Windows`nImports System.Windows.Controls`nImports System.Windows.Media`n`nNamespace Controls`n" + $adapted + "End Namespace`n"
        [IO.File]::WriteAllText((Join-Path $appRoot 'Controls/PclDropShadow.vb'), $adapted, (New-Object Text.UTF8Encoding($false)))
    }
    foreach ($control in @('MyButton', 'MyRadioButton', 'MyListItem', 'MyIconButton')) {
        if ($relative -eq "Controls/$control.xaml") {
            $class = $control.Replace('My', 'Pcl')
            $output = $text.Replace("x:Class=`"$control`"", "x:Class=`"Controls.$class`"")
            [IO.File]::WriteAllText((Join-Path $appRoot "Controls/$class.xaml"), $output, (New-Object Text.UTF8Encoding($false)))
        }
    }
}
[xml]$application = Get-Content -LiteralPath (Join-Path $referenceRoot 'Application.xaml') -Raw
$palette = @($application.Application.'Application.Resources'.ResourceDictionary.ChildNodes | Where-Object { $_.LocalName -in @('SolidColorBrush', 'Color') } | ForEach-Object { $_.OuterXml }) -join "`n"
$dictionary = '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">' + "`n" + $palette + "`n</ResourceDictionary>"
[IO.File]::WriteAllText((Join-Path $appRoot 'Controls/PclPalette.xaml'), $dictionary, (New-Object Text.UTF8Encoding($false)))
[xml]$main = Get-Content -LiteralPath (Join-Path $referenceRoot 'FormMain.xaml') -Raw
$icons = '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:sys="clr-namespace:System;assembly=mscorlib">'
foreach ($node in $main.SelectNodes('//*[local-name()="MyRadioButton"]')) {
    if ($node.GetAttribute('Text') -notin @('下载', '设置')) { continue }
    $icons += '<sys:String x:Key="IconTitle' + $node.GetAttribute('Text') + '">' + [Security.SecurityElement]::Escape($node.GetAttribute('Logo')) + '</sys:String>'
}
$icons += '</ResourceDictionary>'
[IO.File]::WriteAllText((Join-Path $appRoot 'Controls/PclIcons.xaml'), $icons, (New-Object Text.UTF8Encoding($false)))
foreach ($image in @('Grass', 'CommandBlock', 'CobbleStone', 'GoldBlock', 'Anvil', 'NeoForge', 'Fabric', 'GrassPath', 'OptiFabric', 'Egg')) {
    Copy-Item -LiteralPath (Join-Path $sourceRoot "Plain Craft Launcher 2/Images/Blocks/$image.png") -Destination (Join-Path $appRoot "Images/Blocks/$image.png") -Force
}
New-Item -ItemType Directory -Path (Join-Path $appRoot 'Resources') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $appRoot 'Images/Themes') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $sourceRoot 'Plain Craft Launcher 2/Images/Themes/12.png') -Destination (Join-Path $appRoot 'Images/Themes/12.png') -Force
Copy-Item -LiteralPath (Join-Path $sourceRoot 'Plain Craft Launcher 2/Resources/Font.ttf') -Destination (Join-Path $appRoot 'Resources/Font.ttf') -Force
$manifest = Get-ChildItem -LiteralPath $referenceRoot -File -Recurse | Where-Object { $_.Name -ne 'manifest.json' } | Get-FileHash -Algorithm SHA256 | Select-Object @{Name='File';Expression={$_.Path.Substring($referenceRoot.Length + 1)}}, Hash
@{ Repository = 'Meloong-Git/PCL'; Commit = $baseline; Files = $manifest } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $referenceRoot 'manifest.json') -Encoding UTF8
