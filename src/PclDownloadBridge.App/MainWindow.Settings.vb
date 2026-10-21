Imports System.Globalization
Imports System.Windows
Imports System.Windows.Controls
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core
Imports Newtonsoft.Json.Linq

Partial Public Class MainWindow
    Private _restoringSettings As Boolean
    Private _preferenceBaseline As JObject
    Private _preferenceBaselinePath As String
    Private Sub InitializeSettingsFeedback()
        AddHandler SourceBox.SelectionChanged, AddressOf ClearSettingsFeedback
        AddHandler VersionSourceBox.SelectionChanged, AddressOf ClearSettingsFeedback
        AddHandler FolderList.SelectionChanged, AddressOf ClearSettingsFeedback
        AddHandler ConnectionsSlider.ValueChanged, AddressOf ClearSettingsFeedback
        AddHandler SpeedLimitBox.TextChanged, AddressOf ClearSettingsFeedback
        AddHandler JavaPathBox.TextChanged, AddressOf ClearSettingsFeedback
        AddHandler ReuseBox.Checked, AddressOf ClearSettingsFeedback
        AddHandler ReuseBox.Unchecked, AddressOf ClearSettingsFeedback
    End Sub

    Private Sub ClearSettingsFeedback(sender As Object, e As RoutedEventArgs)
        If SettingsSaveFeedback.Text.Length > 0 AndAlso StatusText.Text = SettingsSaveFeedback.Text Then StatusText.Text = ""
        SettingsSaveFeedback.Text = ""
    End Sub

    Private Sub ReportSettingsSave(message As String, failed As Boolean)
        StatusText.Text = message
        SettingsSaveFeedback.Text = message
        SettingsSaveFeedback.SetResourceReference(TextBlock.ForegroundProperty, If(failed, "ColorBrushRedDark", "ColorBrush3"))
    End Sub

    Private Sub NavigateToSetting(target As FrameworkElement, Optional focus As Boolean = False)
        SwitchMainPage(True, Sub()
                                 SettingsPage.UpdateLayout()
                                 target.BringIntoView()
                                 SettingsPage.UpdateLayout()
                             End Sub)
        If focus Then
            If _surfaceMotion.Transition IsNot Nothing Then
                _pendingSurfaceFocus = Sub() target.Focus()
            Else
                target.Focus()
            End If
        End If
    End Sub

    Private Shared ReadOnly ConnectionLevels As Integer() = {8, 16, 32, 64}
    ' PCL display order differs from our persisted 0.1-0.16 enum values.
    Private Shared ReadOnly SourceChoices As SourcePreference() = {SourcePreference.MirrorFirst, SourcePreference.Automatic, SourcePreference.OfficialFirst}
    Private Shared Function SelectedSource(box As PclComboBox) As SourcePreference
        Return If(box.SelectedIndex < 0, SourcePreference.Automatic, SourceChoices(box.SelectedIndex))
    End Function
    Private Shared Function StoredSource(settings As JObject, key As String, fallback As SourcePreference) As SourcePreference
        Dim stored = settings(key)
        If stored Is Nothing OrElse (stored.Type <> JTokenType.Integer AndAlso stored.Type <> JTokenType.String) Then Return fallback
        Dim value As Integer
        If Not Integer.TryParse(CStr(stored), NumberStyles.Integer, CultureInfo.InvariantCulture, value) OrElse Not [Enum].IsDefined(GetType(SourcePreference), value) Then Return fallback
        Return CType(value, SourcePreference)
    End Function

    Private Sub RestoreSourcePreferences(settings As JObject)
        Dim fileSource = StoredSource(settings, "source", SourcePreference.Automatic)
        SourceBox.SelectedIndex = Array.IndexOf(SourceChoices, fileSource)
        ' The old single option controlled both: preserve its meaning on upgrade.
        VersionSourceBox.SelectedIndex = Array.IndexOf(SourceChoices, StoredSource(settings, "versionSource", fileSource))
    End Sub
    Private Function BuildSettings(limit As Double) As JObject
        Return New JObject From {{"pclBase", _pclBase}, {"gameRoot", If(_selectedFolder?.Location, "")},
            {"source", CInt(SelectedSource(SourceBox))}, {"versionSource", CInt(SelectedSource(VersionSourceBox))},
            {"connectionsIndex", Array.IndexOf(ConnectionLevels, ConnectionCount())},
            {"speed", limit.ToString(CultureInfo.InvariantCulture)}, {"reuse", ReuseBox.IsChecked = True}, {"java", JavaPathBox.Text.Trim()}, {"appearance", _appearance.ToJson()}, {"developer", _developer.ToJson()}}
    End Function

    Private Sub CapturePreferenceBaseline()
        Dim limit As Double
        Dim valid = TryGetSpeedLimit(limit)
        _preferenceBaseline = BuildSettings(If(valid, limit, 0))
        If Not valid Then _preferenceBaseline("speed") = SpeedLimitBox.Text
        _preferenceBaselinePath = IO.Path.GetFullPath(SettingsFile)
    End Sub

    Private Sub SavePreferenceValues(path As String, legacyPath As String, limit As Double, Optional explicitSave As Boolean = False)
        Dim target = IO.Path.GetFullPath(path)
        Dim values = BuildSettings(limit)
        Dim changes = values
        If Not explicitSave AndAlso _preferenceBaseline IsNot Nothing AndAlso String.Equals(target, _preferenceBaselinePath, StringComparison.OrdinalIgnoreCase) Then
            changes = DownloadSettingsFile.ChangedValues(_preferenceBaseline, values)
        End If
        If changes.HasValues Then DownloadSettingsFile.SaveChanges(target, legacyPath, changes, explicitSave)
        _preferenceBaseline = values
        _preferenceBaselinePath = target
    End Sub

    Private Function ConnectionCount() As Integer
        Return ConnectionLevels.OrderBy(Function(value) Math.Abs(value - ConnectionsSlider.Value)).First()
    End Function

    Private Sub ConnectionsChanged(sender As Object, e As RoutedPropertyChangedEventArgs(Of Double))
        Dim slider = TryCast(sender, PclSlider)
        If slider Is Nothing Then Return
        Dim nearest = ConnectionLevels.OrderBy(Function(value) Math.Abs(value - slider.Value)).First()
        If slider.Value <> nearest Then
            slider.Value = nearest
            Return
        End If
        If ConnectionValue IsNot Nothing Then ConnectionValue.Text = nearest.ToString(CultureInfo.InvariantCulture)
    End Sub

    Private Function TryGetSpeedLimit(ByRef limit As Double) As Boolean
        If SpeedLimitBox Is Nothing Then Return False
        Dim valid = Double.TryParse(SpeedLimitBox.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, limit)
        valid = valid AndAlso Not Double.IsNaN(limit) AndAlso Not Double.IsInfinity(limit) AndAlso limit >= 0 AndAlso limit <= 100000
        If valid AndAlso limit > 0 AndAlso limit < 1.0 / (1024 * 1024) Then
            SpeedLimitBox.ValidationMessage = "速度上限不能小于 1 字节/秒；0 表示不限速"
            Return False
        End If
        SpeedLimitBox.ValidationMessage = If(valid, "", "请输入 0～100000 的数字")
        Return valid
    End Function

    Private Sub SpeedLimitChanged(sender As Object, e As TextChangedEventArgs)
        Dim limit As Double
        TryGetSpeedLimit(limit)
    End Sub
End Class
