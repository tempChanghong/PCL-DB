Imports System.Diagnostics
Imports System.IO
Imports System.Windows

Partial Public Class MainWindow
    Private Enum TaskOutcome
        Running
        Succeeded
        Failed
        Cancelled
    End Enum
    Private _taskOutcome As TaskOutcome
    Private _taskRoot As String
    Private _taskInstancePath As String
    Private _taskMinecraft As String
    Private _taskComponents As String
    Private _taskDiagnostic As String
    Private _taskAction As Action(Of String, String)

    Private Sub ResetTaskResult(root As String)
        _taskRoot = root
        _taskInstancePath = Nothing
        _taskMinecraft = _selectedEntry?.Id
        _taskComponents = String.Join("、", _componentSelection.Entries.Values.Select(Function(c) ComponentLabel(c)))
        _taskDiagnostic = Nothing
        _taskOutcome = TaskOutcome.Running
        TaskResultActions.Visibility = Visibility.Collapsed
        TaskResultLocation.Text = ""
        TaskActionStatus.Text = ""
        CancelButton.Visibility = Visibility.Visible
    End Sub

    Private Shared Function ComponentLabel(entry As PclDownloadBridge.Core.ComponentEntry) As String
        Return PclDownloadBridge.Core.ComponentCatalog.Label(entry.Kind) & " " & entry.Version
    End Function

    Private Function TaskLogPath() As String
        If String.IsNullOrWhiteSpace(_taskRoot) Then Return Nothing
        Return PclDownloadBridge.Core.SafeFiles.UnderRoot(_taskRoot, ".vanilla-downloader/installer.log")
    End Function

    Private Sub RefreshTaskResultActions()
        TaskResultActions.Visibility = If(_taskFinished, Visibility.Visible, Visibility.Collapsed)
        OpenTaskFolderButton.Visibility = If(_taskOutcome = TaskOutcome.Succeeded, Visibility.Visible, Visibility.Collapsed)
        OpenTaskFolderButton.IsEnabled = _taskOutcome = TaskOutcome.Succeeded AndAlso Directory.Exists(_taskInstancePath)
        OpenTaskFolderButton.ToolTip = If(OpenTaskFolderButton.IsEnabled, _taskInstancePath, "此任务的版本文件夹不存在或无法访问。")
        OpenTaskLogButton.IsEnabled = File.Exists(TaskLogPath())
        OpenTaskLogButton.ToolTip = If(OpenTaskLogButton.IsEnabled, TaskLogPath() & Environment.NewLine & "此文件是该游戏目录的累计安装器日志。", "此任务尚无可用的安装器日志。")
        If _taskFinished Then
            TaskResultLocation.Text = If(_taskOutcome = TaskOutcome.Succeeded AndAlso Not String.IsNullOrEmpty(_taskInstancePath), "实例位置：" & _taskInstancePath,
                If(String.IsNullOrEmpty(_taskRoot), "", "游戏目录：" & _taskRoot))
        End If
    End Sub

    Private Function TaskInformation() As String
        Dim outcome = If(_taskOutcome = TaskOutcome.Succeeded, "成功", If(_taskOutcome = TaskOutcome.Cancelled, "已取消", "失败"))
        Return String.Join(Environment.NewLine, {
            ProductInfo.DisplayName & " " & ProductInfo.VersionText,
            "任务：" & _taskName, "Minecraft：" & If(_taskMinecraft, "未记录"),
            "组件：" & If(String.IsNullOrEmpty(_taskComponents), "原版", _taskComponents),
            "结果：" & outcome, "游戏目录：" & If(_taskRoot, "未记录"),
            "实例位置：" & If(_taskInstancePath, "未发布"), "详情：" & _taskDiagnostic})
    End Function

    Private Sub CopyTaskInformation(sender As Object, e As RoutedEventArgs)
        If Not _taskFinished Then Return
        DispatchTaskAction("copy", TaskInformation())
    End Sub

    Private Sub OpenTaskFolder(sender As Object, e As RoutedEventArgs)
        If Not _taskFinished OrElse _taskOutcome <> TaskOutcome.Succeeded Then Return
        If Not Directory.Exists(_taskInstancePath) Then
            TaskActionStatus.Text = "此任务的版本文件夹不存在或无法访问。"
            RefreshTaskResultActions()
            Return
        End If
        DispatchTaskAction("folder", _taskInstancePath)
    End Sub

    Private Sub OpenInstallerLog(sender As Object, e As RoutedEventArgs)
        Dim log = TaskLogPath()
        If Not File.Exists(log) Then
            TaskActionStatus.Text = "此任务尚无可用的安装器日志。"
            RefreshTaskResultActions()
            Return
        End If
        DispatchTaskAction("log", log)
    End Sub

    Private Sub DispatchTaskAction(kind As String, value As String)
        Try
            If _taskAction IsNot Nothing Then
                _taskAction(kind, value)
            ElseIf _preview Then
                Return
            ElseIf kind = "copy" Then
                Clipboard.SetText(value)
            Else
                Process.Start(New ProcessStartInfo(value) With {.UseShellExecute = True})
            End If
            TaskActionStatus.Text = If(kind = "copy", "任务信息已复制。", If(kind = "folder", "已打开此任务的版本文件夹。", "已打开此任务所在游戏目录的安装器日志。"))
        Catch ex As Exception
            TaskActionStatus.Text = "操作未完成：" & ex.Message
        End Try
    End Sub
End Class
