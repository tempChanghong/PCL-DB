Imports System.Globalization
Imports System.IO
Imports System.Windows
Imports System.Windows.Controls
Imports Newtonsoft.Json.Linq
Imports PlainCraftDownloader.Controls

Partial Public Class MainWindow
    Private _appearance As New AppearancePreferences
    Private _restoringAppearance As Boolean

    Private Sub InitializeAppearance()
        _restoringAppearance = True
        For i = 0 To AppearancePreferences.ThemeIds.Length - 1
            Dim radio As New RadioButton With {.Content = AppearancePreferences.ThemeNames(i), .Tag = AppearancePreferences.ThemeIds(i), .GroupName = "AppearanceTheme", .Style = DirectCast(FindResource("PclAppearanceRadio"), Style)}
            AddHandler radio.Checked, AddressOf AppearanceChanged
            AppearanceThemes.Children.Add(radio)
        Next
        For Each slider In {OpacitySlider, HueSlider, SaturationSlider, LightnessSlider}
            AddHandler slider.ValueChanged, AddressOf AppearanceChanged
        Next
        For Each box In {ColorfulBackgroundBox, TitleTextureBox}
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
            For Each radio In AppearanceThemes.Children.OfType(Of RadioButton)()
                radio.IsChecked = CStr(radio.Tag) = _appearance.Theme
            Next
            ApplyAppearance()
        Finally
            _restoringAppearance = False
        End Try
    End Sub

    Private Sub AppearanceChanged(sender As Object, e As RoutedEventArgs)
        If _restoringAppearance OrElse _restoringSettings OrElse _closing Then Return
        _appearance.Theme = CStr(AppearanceThemes.Children.OfType(Of RadioButton)().First(Function(r) r.IsChecked.GetValueOrDefault()).Tag)
        _appearance.Transparency = CInt(OpacitySlider.Value)
        _appearance.Hue = HueSlider.Value : _appearance.Saturation = SaturationSlider.Value : _appearance.Lightness = LightnessSlider.Value
        _appearance.ColorfulBackground = ColorfulBackgroundBox.IsChecked.GetValueOrDefault()
        _appearance.TitleTexture = TitleTextureBox.IsChecked.GetValueOrDefault()
        ApplyAppearance()
        SaveAppearance()
    End Sub

    Private Sub ApplyAppearance()
        PclTheme.Apply(TitleBar, Shell, _appearance)
        Opacity = _appearance.Transparency / 1000.0 + 0.4
        OpacityValue.Text = (Opacity * 100).ToString("0", CultureInfo.InvariantCulture) & "%"
        HueValue.Text = _appearance.Hue.ToString("0", CultureInfo.InvariantCulture) & "°"
        SaturationValue.Text = _appearance.Saturation.ToString("0", CultureInfo.InvariantCulture) & "%"
        LightnessValue.Text = _appearance.Lightness.ToString("+0;-0;0", CultureInfo.InvariantCulture)
        CustomThemeGrid.Visibility = If(_appearance.Theme = "custom", Visibility.Visible, Visibility.Collapsed)
        TitleTexture.Visibility = If(_appearance.Theme = "colorful" AndAlso _appearance.TitleTexture, Visibility.Visible, Visibility.Collapsed)
        DownloadTab.Checked = DownloadTab.Checked : SettingsTab.Checked = SettingsTab.Checked
        If _selectedEntry IsNot Nothing Then RefreshComponents()
    End Sub

    Private Sub SaveAppearance()
        If _preview OrElse _restoringSettings OrElse _restoringAppearance OrElse _settingsLoadFailed Then Return
        Try
            SaveAppearanceValues(SettingsFile, LegacySettingsFile)
        Catch ex As Exception
            StatusText.Text = "外观已应用，但未能保存：" & ex.GetBaseException().Message
        End Try
    End Sub

    Private Sub SaveAppearanceValues(path As String, legacyPath As String)
        DownloadSettingsFile.SaveChanges(path, legacyPath, _appearance.Merge(New JObject))
        If _preferenceBaseline IsNot Nothing AndAlso String.Equals(IO.Path.GetFullPath(path), _preferenceBaselinePath, StringComparison.OrdinalIgnoreCase) Then
            _preferenceBaseline("appearance") = _appearance.ToJson()
        End If
    End Sub

    Private Sub ResetAppearance(sender As Object, e As RoutedEventArgs)
        RestoreAppearance(Nothing)
        SaveAppearance()
    End Sub
End Class
