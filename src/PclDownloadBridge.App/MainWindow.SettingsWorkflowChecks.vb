Imports System.IO
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Sub VerifySettingsWorkflow(check As Action(Of Boolean, String), artifactDirectory As String)
        Dim savedAppearance = _appearance.ToJson()
        Dim content = DirectCast(Me.Content, FrameworkElement)
        Dim host As New Border With {.Width = 810, .Height = 470}
        Dim source As Interop.HwndSource = Nothing
        Dim layout As Action = Sub()
                                   host.Measure(New Size(host.Width, host.Height))
                                   host.Arrange(New Rect(0, 0, host.Width, host.Height))
                                   host.UpdateLayout()
                                   FlushControlLayout()
                                   host.UpdateLayout()
                               End Sub
        Try
            Me.Content = Nothing : host.Child = content
            source = PreviewSurface.Attach(host, 810, 470)
            PreviewPage("settings")
            RestoreAppearance(New JObject From {{"theme", "custom"}, {"hue", 285}, {"saturation", 65}})
            SelectVersion(_versions(0))
            layout()
            PointerEvent(ChangeFolderButton, Mouse.MouseDownEvent)
            PointerEvent(ChangeFolderButton, Mouse.MouseUpEvent)
            SettingsPage.BeginAnimation(OpacityProperty, Nothing) : SettingsPage.Opacity = 1
            layout()
            Dim top = FolderList.TranslatePoint(New Point(), SettingsPage).Y
            Dim bottom = top + FolderList.ActualHeight
            File.WriteAllText(Path.Combine(artifactDirectory, "settings-folder-navigation-0.28.json"), Newtonsoft.Json.JsonConvert.SerializeObject(New With {.Top = top, .Bottom = bottom, .Viewport = SettingsPage.ViewportHeight, .Offset = SettingsPage.VerticalOffset}, Newtonsoft.Json.Formatting.Indented))
            ApplyAppearance() : layout()
            SaveControlVisual(host, Path.Combine(artifactDirectory, "align-folder-navigation-0.28.png"))
            check(SettingsPage.Visibility = Visibility.Visible AndAlso AboutPage.Visibility = Visibility.Collapsed AndAlso top >= 0 AndAlso bottom <= SettingsPage.ViewportHeight, "changing the install folder brings its selector fully into view below expanded personalization")
            InstanceName.Text = "目录导航保留的名称"
            Dim selected = _selectedEntry
            ChooseInstallFolder(Nothing, Nothing) : layout()
            check(_selectedEntry Is selected AndAlso InstanceName.Text = "目录导航保留的名称" AndAlso _downloadView = DownloadView.Install, "targeted directory navigation preserves the selected version and custom instance name")
            Dim folder = _selectedFolder
            _selectedFolder = Nothing
            SwitchMainPage(False)
            StartInstall(Nothing, Nothing) : layout()
            top = FolderList.TranslatePoint(New Point(), SettingsPage).Y
            check(SettingsPage.Visibility = Visibility.Visible AndAlso top >= 0 AndAlso top + FolderList.ActualHeight <= SettingsPage.ViewportHeight AndAlso StatusText.Text = "请先选择游戏目录。", "installing without a game folder reveals the actual folder selector instead of personalization")
            _selectedFolder = folder
            SpeedLimitBox.Text = "不是数字"
            SettingsPage.ScrollToEnd() : layout()
            PointerEvent(SaveSettingsButton, Mouse.MouseDownEvent)
            PointerEvent(SaveSettingsButton, Mouse.MouseUpEvent)
            layout()
            top = SpeedLimitBox.TranslatePoint(New Point(), SettingsPage).Y
            check(top >= 0 AndAlso top + SpeedLimitBox.ActualHeight <= SettingsPage.ViewportHeight AndAlso SpeedLimitBox.ValidationMessage.Length > 0 AndAlso Not SettingsSaveFeedback.Text.Contains("已保存"), "saving invalid speed brings the input and its validation message fully into view")
            SettingsPage.BeginAnimation(OpacityProperty, Nothing) : SettingsPage.Opacity = 1
            ApplyAppearance() : layout()
            SaveControlVisual(host, Path.Combine(artifactDirectory, "align-settings-invalid-after-0.28.png"))
            SpeedLimitBox.Text = "12.5" : layout()
            check(SettingsSaveFeedback.Text = "" AndAlso StatusText.Text = "" AndAlso SpeedLimitBox.ValidationMessage = "", "correcting invalid speed clears the old save failure and inline validation")
            SaveSettingsButton.BringIntoView() : layout()
            PointerEvent(SaveSettingsButton, Mouse.MouseDownEvent)
            PointerEvent(SaveSettingsButton, Mouse.MouseUpEvent)
            layout()
            top = SettingsSaveFeedback.TranslatePoint(New Point(), SettingsPage).Y
            check(SettingsSaveFeedback.Text = "下载设置已保存。" AndAlso top >= 0 AndAlso top + SettingsSaveFeedback.ActualHeight <= SettingsPage.ViewportHeight AndAlso _preview, "successful save feedback appears beside the button without writing preview preferences")
            ApplyAppearance() : layout()
            SaveControlVisual(host, Path.Combine(artifactDirectory, "align-settings-saved-after-0.28.png"))
            Dim index = SourceBox.SelectedIndex
            SourceBox.SelectedIndex = (index + 1) Mod SourceChoices.Length
            check(SettingsSaveFeedback.Text = "" AndAlso StatusText.Text = "", "changing the source clears stale successful-save feedback")
            SourceBox.SelectedIndex = index
            SaveSettings(Nothing, Nothing)
            JavaPathBox.Text &= " "
            check(SettingsSaveFeedback.Text = "" AndAlso StatusText.Text = "", "editing the installer Java path clears stale successful-save feedback")
            JavaPathBox.Text = JavaPathBox.Text.TrimEnd()
            SaveSettings(Nothing, Nothing)
            ReuseBox.IsChecked = Not ReuseBox.IsChecked.GetValueOrDefault()
            check(SettingsSaveFeedback.Text = "" AndAlso StatusText.Text = "", "toggling file reuse clears stale successful-save feedback")
            ReuseBox.IsChecked = Not ReuseBox.IsChecked.GetValueOrDefault()
            SpeedLimitBox.Text = "0"
        Finally
            If source IsNot Nothing Then
                source.RootVisual = Nothing : source.Dispose()
            End If
            host.Child = Nothing
            If Me.Content Is Nothing Then Me.Content = content
            RestoreAppearance(savedAppearance)
        End Try
    End Sub
End Class
