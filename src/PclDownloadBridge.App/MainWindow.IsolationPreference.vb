Imports System.Globalization
Imports System.Windows
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    ' -1 follows the selected PCL; 0..4 are PCL LaunchArgumentIndieV2 values.
    ' These are default strategies, not the separate three-state install API.
    Private Function SelectedIsolationStrategy() As Integer?
        Return If(IsolationBox.SelectedIndex >= 1 AndAlso IsolationBox.SelectedIndex <= 5,
                  New Integer?(IsolationBox.SelectedIndex - 1), Nothing)
    End Function

    Private Sub RestoreIsolationPreference(settings As JObject)
        Dim value = If(settings Is Nothing, Nothing, settings("defaultIsolation")), strategy As Integer
        If value IsNot Nothing AndAlso (value.Type = JTokenType.Integer OrElse value.Type = JTokenType.String) AndAlso
            Integer.TryParse(CStr(value), NumberStyles.Integer, CultureInfo.InvariantCulture, strategy) AndAlso strategy >= -1 AndAlso strategy <= 4 Then
            IsolationBox.SelectedIndex = strategy + 1
        Else
            IsolationBox.SelectedIndex = 0
        End If
    End Sub

    Private Sub IsolationChanged(sender As Object, e As RoutedEventArgs)
        RefreshIsolation(_busy)
    End Sub

    Private Sub RefreshIsolation(busy As Boolean)
        If IsolationBox Is Nothing OrElse IsolationHint Is Nothing Then Return
        IsolationBox.IsEnabled = Not busy
        Dim strategy = SelectedIsolationStrategy(), description As String
        Select Case strategy.GetValueOrDefault(-1)
            Case 0 : description = "共用存档、Mod 和资源包；同名冲突文件不会被覆盖。"
            Case 1 : description = "仅隔离可安装 Mod 的版本。"
            Case 2 : description = "隔离快照、预发布版、远古版本与愚人节版本。"
            Case 3 : description = "隔离可安装 Mod 的版本与非正式版。"
            Case 4 : description = "每个版本使用独立的存档、Mod 和资源包。"
            Case Else : description = "使用所选启动器的原生隔离设置。"
        End Select
        IsolationHint.Text = description & " 已有实例及校验补全保留原设置；整合包始终隔离。"
    End Sub
End Class
