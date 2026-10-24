Imports System.Globalization
Imports System.Windows
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private _pclThemeMonitor As PclThemeMonitor
    Private _pclThemeSnapshot As PclThemeSnapshot
    Private _followPreview As Boolean
    Private _appliedPaletteKey As String

    Private Sub InitializePclThemeFollow()
        _pclThemeMonitor = New PclThemeMonitor(Dispatcher, AddressOf ReceivePclTheme)
        AddHandler Closed, Sub() _pclThemeMonitor.Dispose()
    End Sub
    Private Sub ConfigurePclThemeFollow()
        _pclThemeSnapshot = Nothing
        Dim enabled = _appearance.FollowPclTheme AndAlso (Not _preview OrElse _followPreview) AndAlso Not _closing
        _pclThemeMonitor.Bind(If(enabled, _appearance.PclThemeFile, Nothing))
    End Sub
    Private Sub ReceivePclTheme(snapshot As PclThemeSnapshot)
        If _closing OrElse Not _appearance.FollowPclTheme OrElse (_preview AndAlso Not _followPreview) Then Return
        _pclThemeSnapshot = snapshot
        ' This updates only presentation. External refresh must never save the
        ' sampled theme into local preferences or rebuild a catalogue.
        ApplyAppearance(externalRefresh:=True)
    End Sub
    Private Function FollowedAppearance(local As AppearancePreferences) As AppearancePreferences
        PclThemePathBox.Text = _appearance.PclThemeFile
        PclThemeSourcePanel.Visibility = If(_appearance.FollowPclTheme, Visibility.Visible, Visibility.Collapsed)
        AppearanceThemes.IsEnabled = Not _appearance.FollowPclTheme
        CustomThemeGrid.IsEnabled = Not _appearance.FollowPclTheme
        AppearanceThemeLabel.Text = If(_appearance.FollowPclTheme, "回退主题", "主题")
        If Not _appearance.FollowPclTheme Then
            PclThemeStatus.Text = ""
            Return local
        End If
        Dim reason As String
        If String.IsNullOrWhiteSpace(_appearance.PclThemeFile) Then
            reason = "请先选择要跟随的 PCL。"
        ElseIf _pclThemeSnapshot Is Nothing Then
            reason = "正在读取所选 PCL 的主题。"
        ElseIf Not _pclThemeSnapshot.Success Then
            reason = _pclThemeSnapshot.ErrorText
        ElseIf _pclThemeSnapshot.Preset Is Nothing Then
            reason = "PCL 当前为「" & _pclThemeSnapshot.Name & "」；暂不支持同步该配色。"
        ElseIf Not AppearancePreferences.PublicThemes.Contains(_pclThemeSnapshot.Preset) AndAlso Not ExperimentalThemesAvailable Then
            reason = "PCL 当前为「" & _pclThemeSnapshot.Name & "」；对应配色需要解锁本程序的实验性外观。"
        Else
            Dim result = AppearancePreferences.Read(local.ToJson())
            result.Theme = _pclThemeSnapshot.Preset
            PclThemeStatus.Text = "正在跟随：" & _pclThemeSnapshot.Name & If(_pclThemeSnapshot.IsDefault, "（配置未指定主题，采用 PCL 默认值）", "") &
                If(_pclThemeSnapshot.ThemeId = 0, "。", "（使用 PCL-DB 对应预设的近似配色）。")
            Return result
        End If
        PclThemeStatus.Text = reason & " 当前使用 PCL-DB 本机外观。"
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
