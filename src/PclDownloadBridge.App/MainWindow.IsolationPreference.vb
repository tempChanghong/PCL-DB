Imports System.Globalization
Imports System.Windows
Imports Newtonsoft.Json.Linq

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
            Case 0 : description = "不同版本共用存档、Mods 与资源包，请留意兼容性；同名不同内容的文件不会被覆盖。"
            Case 1 : description = "隔离可安装 Mod 的版本；其他原版版本共用存档与资源包。"
            Case 2 : description = "隔离快照、预发布版、远古版本与愚人节版本。"
            Case 3 : description = "隔离可安装 Mod 的版本，以及快照等非正式版。"
            Case 4 : description = "所有新版本分别使用独立的存档、Mods 与资源包。"
            Case Else : description = "跟随所选 PCL 的默认隔离策略；未找到设置时使用 PCL 默认值。"
        End Select
        IsolationHint.Text = description & " 仅对之后新建的版本生效；已有实例及校验补全保留原设置，整合包始终使用独立实例。"
    End Sub
End Class
