Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private _nameEdited As Boolean
    Private _settingName As Boolean
    Private _installRunner As Func(Of VersionEntry, String, String, ComponentSelection, InstanceIsolationMode, Integer?, CancellationToken, Task(Of InstallResult))

    Private Sub SetAutomaticInstanceName(value As String)
        _settingName = True
        Try
            InstanceName.Text = value
        Finally
            _settingName = False
        End Try
    End Sub

    Private Sub InstanceNameChanged(sender As Object, e As TextChangedEventArgs)
        If _settingName Then Return
        _nameEdited = True
        RefreshInstallAvailability()
    End Sub

    Private Function NameProblem() As String
        If _selectedEntry Is Nothing Then Return Nothing
        Try
            InstallPreflight.ValidateName(InstanceName.Text)
            If _selectedFolder IsNot Nothing Then InstallPreflight.ValidateDestination(_selectedFolder.Location, InstanceName.Text, RepairBox.IsChecked.GetValueOrDefault())
        Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            Return ex.Message
        End Try
        Return Nothing
    End Function

    Private Function RefreshInstallAvailability(Optional busy As Boolean? = Nothing) As String
        Dim nameIssue = NameProblem()
        InstanceName.ValidationMessage = If(nameIssue, "")
        NameHint.Text = If(nameIssue, "")
        NameHint.Visibility = If(nameIssue Is Nothing, Visibility.Collapsed, Visibility.Visible)
        Dim issue = If(nameIssue, ComponentInstallProblem())
        If issue Is Nothing AndAlso RepairBox.IsChecked.GetValueOrDefault() AndAlso _componentSelection.Entries.Count > 0 Then issue = "请为组件安装使用新名称；原版校验仅支持未添加加载器的实例。"
        Dim active = If(busy, _busy)
        InstallButton.IsEnabled = Not active AndAlso issue Is Nothing
        InstallButton.ToolTip = If(active, "请等待当前安装任务结束。", issue)
        RefreshPackAvailability(busy)
        Return issue
    End Function

    Private Sub InstanceNameKeyDown(sender As Object, e As KeyEventArgs)
        If e.Key <> Key.Enter Then Return
        e.Handled = True
        If e.IsRepeat OrElse _busy OrElse _closing OrElse _downloadView <> DownloadView.Install OrElse DownloadPage.Visibility <> Visibility.Visible Then Return
        Dim issue = RefreshInstallAvailability()
        If issue IsNot Nothing Then
            StatusText.Text = issue
            Return
        End If
        StartInstall(sender, e)
    End Sub
End Class
