Imports System.Windows
Imports System.Windows.Controls
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Sub VerifySourcePreferences(check As Action(Of Boolean, String), artifactDirectory As String)
        RestoreSourcePreferences(New JObject())
        check(SourceBox.Items.Count = 3 AndAlso VersionSourceBox.Items.Count = 3 AndAlso SourceBox.SelectedIndex = 1 AndAlso VersionSourceBox.SelectedIndex = 1, "two PCL source selectors default to automatic in mirror/auto/official display order")
        SourceBox.SelectedIndex = 0 : VersionSourceBox.SelectedIndex = 2
        Dim selected = Policy()
        check(selected.Preference = SourcePreference.MirrorFirst AndAlso selected.EffectiveVersionPreference = SourcePreference.OfficialFirst, "file and version list controls create independent source preferences")
        Dim settings = BuildSettings(0)
        check(CInt(settings("source")) = 2 AndAlso CInt(settings("versionSource")) = 1, "new source settings persist semantic values rather than reordered display indices")
        Dim directory = IO.Path.Combine(IO.Path.GetTempPath(), "PclDownloadBridgeSourceSettings-" & Guid.NewGuid().ToString("N"))
        Dim file = IO.Path.Combine(directory, "settings.json")
        DownloadSettingsFile.Save(file, settings)
        SourceBox.SelectedIndex = 1 : VersionSourceBox.SelectedIndex = 1
        RestoreSourcePreferences(JObject.Parse(IO.File.ReadAllText(file)))
        check(Policy().Preference = SourcePreference.MirrorFirst AndAlso Policy().EffectiveVersionPreference = SourcePreference.OfficialFirst, "independent source preferences survive an actual atomic settings-file round trip")
        For legacy = 0 To 2
            Dim oldSettings As New JObject From {{"source", legacy}}
            Dim before = oldSettings.ToString()
            RestoreSourcePreferences(oldSettings)
            check(CInt(Policy().Preference) = legacy AndAlso CInt(Policy().EffectiveVersionPreference) = legacy AndAlso before = oldSettings.ToString(), "legacy combined source value " & legacy & " retains both meanings without rewriting the loaded JSON")
        Next
        RestoreSourcePreferences(New JObject From {{"source", 999}, {"versionSource", New JObject()}})
        check(Policy().Preference = SourcePreference.Automatic AndAlso Policy().EffectiveVersionPreference = SourcePreference.Automatic, "invalid source preferences safely return to automatic")
        SetBusy(True)
        check(Not SourceBox.IsEnabled AndAlso Not VersionSourceBox.IsEnabled, "installation freezes both source selectors through their settings card")
        SetBusy(False)
        check(Grid.GetRow(VersionSourceBox) = 2 AndAlso Grid.GetRow(DirectCast(ConnectionsSlider.Parent, UIElement)) = 4 AndAlso Grid.GetRow(DirectCast(SpeedLimitBox.Parent, UIElement)) = 6, "version source, connection and speed controls occupy separate compact PCL rows")
        Dim selector As New PclComboBox With {.Margin = New Thickness(10), .SelectedIndex = 1}
        For Each item As PclComboBoxItem In VersionSourceBox.Items
            selector.Items.Add(New PclComboBoxItem With {.Content = item.Content})
        Next
        Dim host As New Window With {.Content = selector, .Width = 460, .Height = 100, .Left = -32000, .Top = -32000,
            .WindowStyle = WindowStyle.None, .AllowsTransparency = True, .Opacity = 0, .ShowInTaskbar = False, .ShowActivated = False}
        Try
            host.Show() : host.UpdateLayout()
            Dim menu = DirectCast(selector.Template.FindName("PART_Popup", selector), System.Windows.Controls.Primitives.Popup)
            DirectCast(menu.Child, Grid).Opacity = 0
            selector.IsDropDownOpen = True : FlushControlLayout()
            Dim width = DirectCast(menu.Child, Grid).ActualWidth
            check(Math.Abs(width - selector.ActualWidth) < 1 AndAlso width > 300, "long BMCLAPI source labels retain the real PCL popup width")
            If artifactDirectory IsNot Nothing Then SaveControlVisual(DirectCast(selector.Template.FindName("dropDownBorder", selector), Border), IO.Path.Combine(artifactDirectory, "source-menu-" & GetType(MainWindow).Assembly.GetName().Version.ToString(2) & ".png"))
        Finally
            selector.IsDropDownOpen = False
            host.Close()
        End Try
    End Sub
End Class
