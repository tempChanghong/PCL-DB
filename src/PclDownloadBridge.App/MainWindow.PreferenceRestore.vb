Imports Newtonsoft.Json.Linq

Partial Public Class MainWindow
    Private Shared Function ReadScalarPreference(Of T)(settings As JObject, key As String, label As String, fallback As T,
                                                       convert As Func(Of JToken, T), invalid As List(Of String)) As T
        Dim token = settings(key)
        If token Is Nothing Then Return fallback
        Try
            Return convert(token)
        Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is InvalidCastException OrElse
            TypeOf ex Is FormatException OrElse TypeOf ex Is OverflowException
            ' A malformed scalar must not stop unrelated preferences or directory discovery.
            invalid.Add(label)
            Return fallback
        End Try
    End Function

    Private Async Function RestorePreferenceValuesAsync(settings As JObject) As Task(Of List(Of String))
        If _closing Then Return Nothing
        Dim invalid As New List(Of String)
        If settings IsNot Nothing Then
            RestoreDeveloperSettings(settings("developer"))
            RestoreAppearance(settings("appearance"))
            _pclBase = ReadScalarPreference(settings, "pclBase", "PCL 位置", _pclBase, Function(value) If(CStr(value), _pclBase), invalid)
            RestoreSourcePreferences(settings)
            RestoreIsolationPreference(settings)
            ConnectionsSlider.Value = ReadScalarPreference(settings, "connectionsIndex", "下载线程数", ConnectionLevels(1),
                Function(value) ConnectionLevels(Math.Max(0, Math.Min(3, CInt(value)))), invalid)
            Dim storedSpeed = ReadScalarPreference(settings, "speed", "下载速度", "0", Function(value) If(CStr(value), "0"), invalid)
            Dim restoredSpeed As Double
            SpeedLimitBox.Text = If(Double.TryParse(storedSpeed, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, restoredSpeed), restoredSpeed.ToString(Globalization.CultureInfo.CurrentCulture), storedSpeed)
            ReuseBox.IsChecked = ReadScalarPreference(settings, "reuse", "复用文件", True, Function(value) CBool(value), invalid)
            JavaPathBox.Text = ReadScalarPreference(settings, "java", "Java 路径", "", Function(value) If(CStr(value), ""), invalid)
            If Not Await RestoreLauncherBindingsAsync(settings) Then Return Nothing
        Else
            If Not Await RestoreLauncherBindingsAsync(Nothing) Then Return Nothing
        End If
        If _closing Then Return Nothing
        CapturePreferenceBaseline()
        Return invalid
    End Function
End Class
