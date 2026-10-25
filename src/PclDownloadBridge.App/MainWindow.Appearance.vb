Imports System.Globalization
Imports System.IO
Imports System.Windows
Imports System.Windows.Controls
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private _appearance As New AppearancePreferences
    Private _restoringAppearance As Boolean

    Private Sub InitializeAppearance()
        _restoringAppearance = True
        InitializePclThemeFollow()
        For i = 0 To AppearancePreferences.ThemeIds.Length - 1
            Dim radio As New PclRadioBox With {.Content = AppearancePreferences.ThemeNames(i), .Tag = AppearancePreferences.ThemeIds(i), .GroupName = "AppearanceTheme", .Style = DirectCast(FindResource("PclAppearanceRadio"), Style)}
            AddHandler radio.Checked, AddressOf AppearanceChanged
            AppearanceThemes.Children.Add(radio)
        Next
        For Each slider In {OpacitySlider, HueSlider, SaturationSlider, LightnessSlider}
            AddHandler slider.ValueChanged, AddressOf AppearanceChanged
        Next
        For Each box In {ColorfulBackgroundBox, TitleTextureBox, DarkModeBox}
            AddHandler box.Checked, AddressOf AppearanceChanged
            AddHandler box.Unchecked, AddressOf AppearanceChanged
        Next
        RestoreAppearance(Nothing)
    End Sub

    Private Sub RestoreAppearance(token As JToken)
        _restoringAppearance = True
        Try
            _appearance = AppearancePreferences.Read(token)
            OpacitySlider.Value = _appearance.Transparency
            HueSlider.Value = _appearance.Hue
            SaturationSlider.Value = _appearance.Saturation
            LightnessSlider.Value = _appearance.Lightness
            ColorfulBackgroundBox.IsChecked = _appearance.ColorfulBackground
            TitleTextureBox.IsChecked = _appearance.TitleTexture
            DarkModeBox.IsChecked = _appearance.DarkMode
            FollowPclThemeBox.IsChecked = _appearance.FollowPclTheme
            ConfigurePclThemeFollow()
            ApplyAppearance()
        Finally
            _restoringAppearance = False
        End Try
    End Sub

    Private Sub AppearanceChanged(sender As Object, e As RoutedEventArgs)
        If _restoringAppearance OrElse _restoringSettings OrElse _closing Then Return
        Dim selected = TryCast(sender, RadioButton)
        If selected IsNot Nothing AndAlso selected.IsChecked.GetValueOrDefault() Then
            Dim choice = CStr(selected.Tag)
            If Not AppearancePreferences.PublicThemes.Contains(choice) AndAlso Not ExperimentalThemesAvailable Then
                ApplyAppearance()
                Return
            End If
            _appearance.Theme = choice
            If AppearancePreferences.PublicThemes.Contains(choice) Then _appearance.PublicTheme = choice
        End If
        _appearance.Transparency = CInt(OpacitySlider.Value)
        _appearance.Hue = HueSlider.Value : _appearance.Saturation = SaturationSlider.Value : _appearance.Lightness = LightnessSlider.Value
        _appearance.ColorfulBackground = ColorfulBackgroundBox.IsChecked.GetValueOrDefault()
        _appearance.TitleTexture = TitleTextureBox.IsChecked.GetValueOrDefault()
        _appearance.DarkMode = DarkModeBox.IsChecked.GetValueOrDefault()
        ApplyAppearance()
        SaveAppearance()
    End Sub

    Private Sub ApplyAppearance(Optional externalRefresh As Boolean = False)
        Dim local = _appearance.Effective(ExperimentalThemesAvailable)
        Dim effective = FollowedAppearance(local)
        Dim restoring = _restoringAppearance
        _restoringAppearance = True
        Try
            For Each radio In AppearanceThemes.Children.OfType(Of RadioButton)()
                Dim choice = CStr(radio.Tag)
                radio.Visibility = If(AppearancePreferences.PublicThemes.Contains(choice) OrElse ExperimentalThemesAvailable, Visibility.Visible, Visibility.Collapsed)
                radio.IsChecked = choice = local.Theme
            Next
        Finally
            _restoringAppearance = restoring
        End Try
        Dim key = PaletteKey(effective)
        Dim paletteChanged = Not externalRefresh OrElse key <> _appliedPaletteKey
        If paletteChanged Then PclTheme.Apply(TitleBar, Shell, effective)
        ConfigureThemeCycle(effective)
        _appliedPaletteKey = key
        Opacity = _appearance.Transparency / 1000.0 + 0.4
        OpacityValue.Text = (Opacity * 100).ToString("0", CultureInfo.InvariantCulture) & "%"
        HueValue.Text = _appearance.Hue.ToString("0", CultureInfo.InvariantCulture) & "°"
        SaturationValue.Text = _appearance.Saturation.ToString("0", CultureInfo.InvariantCulture) & "%"
        LightnessValue.Text = _appearance.Lightness.ToString("+0;-0;0", CultureInfo.InvariantCulture)
        CustomThemeGrid.Visibility = If(local.Theme = "custom", Visibility.Visible, Visibility.Collapsed)
        TitleTextureBox.Visibility = If(ExperimentalThemesAvailable, Visibility.Visible, Visibility.Collapsed)
        TitleTexture.Visibility = If(effective.Theme = "colorful" AndAlso _appearance.TitleTexture, Visibility.Visible, Visibility.Collapsed)
        DownloadTab.Checked = DownloadTab.Checked : SettingsTab.Checked = SettingsTab.Checked
        If paletteChanged AndAlso _selectedEntry IsNot Nothing Then RefreshComponents()
    End Sub

    Private Sub SaveAppearance(Optional resetAll As Boolean = False)
        If _preview OrElse _restoringSettings OrElse _restoringAppearance OrElse _settingsLoadFailed Then Return
        Try
            SaveAppearanceValues(SettingsFile, LegacySettingsFile, resetAll)
        Catch ex As Exception
            StatusText.Text = "外观已应用，但未能保存：" & ex.GetBaseException().Message
        End Try
    End Sub

    Private Sub SaveAppearanceValues(path As String, legacyPath As String, Optional resetAll As Boolean = False)
        Dim values = _appearance.ToJson(), changes = values
        Dim samePath = _preferenceBaseline IsNot Nothing AndAlso String.Equals(IO.Path.GetFullPath(path), _preferenceBaselinePath, StringComparison.OrdinalIgnoreCase)
        If samePath AndAlso Not resetAll Then changes = DownloadSettingsFile.ChangedValues(TryCast(_preferenceBaseline("appearance"), JObject), values)
        If changes.HasValues Then DownloadSettingsFile.SaveChanges(path, legacyPath, New JObject From {{"appearance", changes}})
        If samePath Then _preferenceBaseline("appearance") = values
    End Sub

    Private Sub ResetAppearance(sender As Object, e As RoutedEventArgs)
        If _restoringSettings OrElse _closing Then Return
        RestoreAppearance(Nothing)
        SaveAppearance(True)
    End Sub
End Class
