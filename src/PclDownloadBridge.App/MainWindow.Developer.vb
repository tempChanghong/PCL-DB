Imports System.Windows
Imports System.Windows.Input
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private _developer As New DeveloperPreferences
    Private _experimentalThemesUnlocked As Boolean
    Private _restoringDeveloper As Boolean
    Private _developerInitialized As Boolean
    Private _developerDiagnosticText As String = "等待下载任务。"

    Private ReadOnly Property ExperimentalThemesAvailable As Boolean
        Get
            Return _developer.Enabled AndAlso _experimentalThemesUnlocked
        End Get
    End Property
    Private Sub InitializeDeveloperSettings()
        _developerInitialized = True
        RestoreDeveloperSettings(Nothing)
    End Sub
    Private Sub RestoreDeveloperSettings(token As JToken)
        _restoringDeveloper = True
        Try
            _developer = DeveloperPreferences.Read(token)
            _experimentalThemesUnlocked = False
            DeveloperCodeBox.Clear()
            DeveloperEnabledBox.IsChecked = _developer.Enabled
            DeveloperDiagnosticsBox.IsChecked = _developer.ShowDiagnostics
            RefreshDeveloperUi()
        Finally
            _restoringDeveloper = False
        End Try
    End Sub
    Private Sub RefreshDeveloperUi()
        DeveloperOptions.Visibility = If(_developer.Enabled, Visibility.Visible, Visibility.Collapsed)
        DeveloperUnlockRow.Visibility = If(ExperimentalThemesAvailable, Visibility.Collapsed, Visibility.Visible)
        DeveloperLockButton.Visibility = If(ExperimentalThemesAvailable, Visibility.Visible, Visibility.Collapsed)
        DeveloperUnlockStatus.Text = If(ExperimentalThemesAvailable, "已解锁，仅本次运行有效。", "尚未解锁")
        UpdateDeveloperDiagnostics()
        ApplyAppearance()
    End Sub
    Private Sub DeveloperEnabledChanged(sender As Object, e As RoutedEventArgs)
        If Not _developerInitialized OrElse _restoringDeveloper OrElse _restoringSettings Then Return
        _developer.Enabled = DeveloperEnabledBox.IsChecked.GetValueOrDefault()
        If Not _developer.Enabled Then _experimentalThemesUnlocked = False
        DeveloperCodeBox.Clear()
        RefreshDeveloperUi()
        SaveDeveloperPreferences()
    End Sub
    Private Sub DeveloperDiagnosticChanged(sender As Object, e As RoutedEventArgs)
        If Not _developerInitialized OrElse _restoringDeveloper OrElse _restoringSettings Then Return
        _developer.ShowDiagnostics = DeveloperDiagnosticsBox.IsChecked.GetValueOrDefault()
        UpdateDeveloperDiagnostics()
        SaveDeveloperPreferences()
    End Sub
    Private Function TryUnlockDeveloperThemes(code As String) As Boolean
        If Not _developer.Enabled OrElse Not DeveloperAccess.Verify(code) Then Return False
        _experimentalThemesUnlocked = True
        RefreshDeveloperUi()
        Return True
    End Function
    Private Sub UnlockDeveloperThemes(sender As Object, e As RoutedEventArgs)
        Dim accepted = TryUnlockDeveloperThemes(DeveloperCodeBox.Password)
        DeveloperCodeBox.Clear()
        If Not accepted Then
            DeveloperUnlockStatus.Text = "验证码不正确。"
            DeveloperCodeBox.Focus()
        Else
            DeveloperLockButton.Focus()
        End If
    End Sub
    Private Sub DeveloperCodeKeyDown(sender As Object, e As KeyEventArgs)
        If e.Key <> Key.Enter Then Return
        UnlockDeveloperThemes(sender, e)
        e.Handled = True
    End Sub
    Private Sub LockDeveloperThemes(sender As Object, e As RoutedEventArgs)
        _experimentalThemesUnlocked = False
        DeveloperCodeBox.Clear()
        RefreshDeveloperUi()
        If _developer.Enabled Then DeveloperCodeBox.Focus()
    End Sub
    Private Sub SaveDeveloperPreferences()
        If _preview OrElse _closing OrElse _settingsLoadFailed OrElse _restoringSettings OrElse _restoringDeveloper Then Return
        Try
            SaveDeveloperValues(SettingsFile, LegacySettingsFile)
        Catch ex As Exception
            StatusText.Text = "开发者设置已应用，但未能保存：" & ex.GetBaseException().Message
        End Try
    End Sub
    Private Sub SaveDeveloperValues(path As String, legacyPath As String)
        Dim values = _developer.ToJson(), changes = values
        Dim samePath = _preferenceBaseline IsNot Nothing AndAlso String.Equals(IO.Path.GetFullPath(path), _preferenceBaselinePath, StringComparison.OrdinalIgnoreCase)
        If samePath Then changes = DownloadSettingsFile.ChangedValues(TryCast(_preferenceBaseline("developer"), JObject), values)
        If changes.HasValues Then DownloadSettingsFile.SaveChanges(path, legacyPath, New JObject From {{"developer", changes}})
        If samePath Then _preferenceBaseline("developer") = values
    End Sub
    Private Sub UpdateDeveloperDiagnostics(Optional value As TransferProgress = Nothing)
        If value IsNot Nothing Then _developerDiagnosticText = "采样阶段：" & value.Phase & " · 采样连接：" & value.ActiveConnections & " · 重试：" & value.Retries & " · 动态分段：" & value.DynamicSplits
        DeveloperTaskInfo.Text = _developerDiagnosticText
        DeveloperTaskInfo.Visibility = If(_developer.Enabled AndAlso _developer.ShowDiagnostics, Visibility.Visible, Visibility.Collapsed)
    End Sub
End Class
