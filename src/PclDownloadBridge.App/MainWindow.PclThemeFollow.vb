Imports System.Globalization
Imports System.Windows
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private _pclThemeMonitor As PclThemeMonitor
    Private _pclThemeSnapshot As PclThemeSnapshot
    Private _followPreview As Boolean
    Private _appliedPaletteKey As String
    Private _pclThemeBoundPath As String

    Private Sub InitializePclThemeFollow()
        _pclThemeMonitor = New PclThemeMonitor(Dispatcher, AddressOf ReceivePclTheme)
        AddHandler Closed, Sub() _pclThemeMonitor.Dispose()
    End Sub
    Private Sub ConfigurePclThemeFollow()
        Dim enabled = _appearance.FollowPclTheme AndAlso (Not _preview OrElse _followPreview) AndAlso Not _closing
        Dim path = If(enabled, CurrentPclThemeFile(), Nothing)
        If String.Equals(path, _pclThemeBoundPath, StringComparison.OrdinalIgnoreCase) Then Return
        _pclThemeBoundPath = path : _pclThemeSnapshot = Nothing
        _pclThemeMonitor.Bind(path)
    End Sub
    Private Function CurrentPclThemeFile() As String
        Dim id As String = Nothing
        If _launcherState IsNot Nothing AndAlso _launcherState.SelectedRows.TryGetValue(Core.ExternalLauncherKind.Pcl, id) Then
            Dim binding = _launcherState.Bindings.FirstOrDefault(Function(item) item.Kind = Core.ExternalLauncherKind.Pcl AndAlso item.Id = id)
            If binding IsNot Nothing Then
                Try
                    binding.ValidatePclConfiguration()
                    Return PclThemeSnapshot.NormalizePath(If(binding.ConfigPath, IO.Path.Combine(binding.Workspace, "PCL", "Setup.ini")))
                Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is IO.IOException OrElse TypeOf ex Is IO.InvalidDataException OrElse TypeOf ex Is NotSupportedException
                    Return ""
                End Try
            End If
            Return ""
        End If
        If _launcherState IsNot Nothing AndAlso _launcherState.Bindings.Any(Function(item) item.Kind = Core.ExternalLauncherKind.Pcl) Then Return ""
        ' Preserve explicit sources saved before the launcher-card integration.
        Return _appearance.PclThemeFile
    End Function
    Private Sub RefreshFollowedLauncherTheme()
        If _pclThemeMonitor Is Nothing OrElse Not _appearance.FollowPclTheme OrElse _closing Then Return
        ConfigurePclThemeFollow()
        ApplyAppearance(externalRefresh:=True)
    End Sub
    Private Sub ReceivePclTheme(snapshot As PclThemeSnapshot)
        If _closing OrElse Not _appearance.FollowPclTheme OrElse (_preview AndAlso Not _followPreview) Then Return
        _pclThemeSnapshot = snapshot
        ' This updates only presentation. External refresh must never save the
        ' sampled theme into local preferences or rebuild a catalogue.
        ApplyAppearance(externalRefresh:=True)
    End Sub
    Private Function FollowedAppearance(local As AppearancePreferences) As AppearancePreferences
        Dim source = CurrentPclThemeFile()
        PclThemePathBox.Text = source
        PclThemeStatus.ToolTip = source
        PclThemeSourcePanel.Visibility = If(_appearance.FollowPclTheme, Visibility.Visible, Visibility.Collapsed)
        AppearanceThemes.IsEnabled = Not _appearance.FollowPclTheme
        CustomThemeGrid.IsEnabled = Not _appearance.FollowPclTheme
        AppearanceThemeLabel.Text = If(_appearance.FollowPclTheme, "回退主题", "主题")
        If Not _appearance.FollowPclTheme Then
            PclThemeStatus.Text = ""
            Return local
        End If
        Dim reason As String
        If String.IsNullOrWhiteSpace(source) Then
            reason = "请先选择外部启动器中的 PCL 位置。"
        ElseIf _pclThemeSnapshot Is Nothing Then
            reason = "正在读取所选 PCL 的主题。"
        ElseIf Not _pclThemeSnapshot.Success Then
            reason = _pclThemeSnapshot.ErrorText
        ElseIf _pclThemeSnapshot.Preset Is Nothing Then
            reason = "PCL 当前为「" & _pclThemeSnapshot.Name & "」；暂不支持同步该配色。"
        ElseIf Not AppearancePreferences.PublicThemes.Contains(_pclThemeSnapshot.Preset) AndAlso Not ExperimentalThemesAvailable Then
            reason = "「" & _pclThemeSnapshot.Name & "」需先解锁实验性外观。"
        Else
            Dim result = AppearancePreferences.Read(local.ToJson())
            result.Theme = _pclThemeSnapshot.Preset
            PclThemeStatus.Text = "正在跟随：" & _pclThemeSnapshot.Name & If(_pclThemeSnapshot.IsDefault, "（默认）", If(_pclThemeSnapshot.ThemeId = 0, "", "（近似配色）"))
            Return result
        End If
        PclThemeStatus.Text = reason & " 使用本机外观。"
        Return local
    End Function
    Private Shared Function PaletteKey(value As AppearancePreferences) As String
        Return String.Join("|", {value.Theme, value.Hue.ToString("R", CultureInfo.InvariantCulture),
            value.Saturation.ToString("R", CultureInfo.InvariantCulture), value.Lightness.ToString("R", CultureInfo.InvariantCulture),
            value.DarkMode.ToString(), value.ColorfulBackground.ToString()})
    End Function
    Private Sub FollowPclThemeChanged(sender As Object, e As RoutedEventArgs)
        If _pclThemeMonitor Is Nothing OrElse _restoringAppearance OrElse _restoringSettings OrElse _closing Then Return
        _appearance.FollowPclTheme = FollowPclThemeBox.IsChecked.GetValueOrDefault()
        ConfigurePclThemeFollow()
        ApplyAppearance()
        SaveAppearance()
    End Sub
    Private Sub BindPclThemeSource(path As String)
        _appearance.PclThemeFile = PclThemeSnapshot.FromSelection(path)
        ConfigurePclThemeFollow()
        ApplyAppearance()
        SaveAppearance()
    End Sub
    Private Sub ChoosePclThemeSource(sender As Object, e As RoutedEventArgs)
        Using dialog As New Forms.OpenFileDialog With {.Title = "选择要跟随的 PCL 程序或 Setup.ini", .Filter = "PCL 程序或配置|*.exe;Setup.ini", .CheckFileExists = True}
            If dialog.ShowDialog() <> Forms.DialogResult.OK Then Return
            Try
                BindPclThemeSource(dialog.FileName)
            Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is IO.IOException OrElse TypeOf ex Is NotSupportedException
                PclThemeStatus.Text = "所选路径无法使用，请重新选择 PCL。"
            End Try
        End Using
    End Sub
    Private Sub RefreshPclThemeSource(sender As Object, e As RoutedEventArgs)
        If _closing OrElse Not _appearance.FollowPclTheme Then Return
        _pclThemeMonitor.Refresh()
    End Sub
End Class
