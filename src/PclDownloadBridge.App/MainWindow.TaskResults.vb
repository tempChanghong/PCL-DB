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
    Private _taskResourceFile As String
    Private _taskResourceDestination As String
    Private _taskResourceTransfer As Boolean
    Private _taskAction As Action(Of String, String)

    Private Sub ResetTaskResult(root As String)
        _taskRoot = root
        _taskInstancePath = Nothing
        _taskMinecraft = _selectedEntry?.Id
        _taskComponents = String.Join("、", _componentSelection.Entries.Values.Select(Function(c) ComponentLabel(c)))
        _taskDiagnostic = Nothing
        _taskResourceFile = Nothing
        _taskResourceDestination = Nothing
        _taskResourceTransfer = False
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
        If Not _taskFinished OrElse String.IsNullOrWhiteSpace(_taskRoot) Then Return Nothing
        Try
            Dim path = PclDownloadBridge.Core.SafeFiles.UnderRoot(_taskRoot, ".vanilla-downloader/installer.log")
            Return If(File.Exists(path), path, Nothing)
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is ArgumentException OrElse TypeOf ex Is NotSupportedException OrElse TypeOf ex Is Security.SecurityException
            Return Nothing
        End Try
    End Function

    Private Function TaskFolderPath() As String
        If _taskFinished AndAlso _taskOutcome = TaskOutcome.Succeeded AndAlso Not String.IsNullOrEmpty(_taskResourceFile) Then
            Try
                If File.Exists(_taskResourceFile) AndAlso (File.GetAttributes(_taskResourceFile) And FileAttributes.ReparsePoint) = 0 Then Return Path.GetDirectoryName(Path.GetFullPath(_taskResourceFile))
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is ArgumentException
                Return Nothing
            End Try
            Return Nothing
        End If
        If Not _taskFinished OrElse _taskOutcome <> TaskOutcome.Succeeded OrElse String.IsNullOrWhiteSpace(_taskRoot) OrElse String.IsNullOrWhiteSpace(_taskInstancePath) Then Return Nothing
        Try
            PclDownloadBridge.Core.SafeFiles.ValidateInstanceName(_taskName)
            Dim expected = PclDownloadBridge.Core.SafeFiles.UnderRoot(_taskRoot, "versions/" & _taskName)
            If Not Directory.Exists(expected) OrElse (File.GetAttributes(expected) And FileAttributes.ReparsePoint) <> 0 Then Return Nothing
            Dim recorded = Path.GetFullPath(_taskInstancePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            Return If(String.Equals(recorded, expected, StringComparison.OrdinalIgnoreCase), expected, Nothing)
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is ArgumentException OrElse TypeOf ex Is NotSupportedException OrElse TypeOf ex Is Security.SecurityException
            Return Nothing
        End Try
    End Function

    Private Sub RefreshTaskResultActions()
        TaskResultActions.Visibility = If(_taskFinished, Visibility.Visible, Visibility.Collapsed)
        OpenTaskFolderButton.Visibility = If(_taskOutcome = TaskOutcome.Succeeded, Visibility.Visible, Visibility.Collapsed)
        Dim folder = TaskFolderPath(), log = TaskLogPath()
        OpenTaskFolderButton.IsEnabled = folder IsNot Nothing
        OpenTaskFolderButton.ToolTip = If(folder, TaskFolderUnavailableMessage())
        OpenTaskLogButton.IsEnabled = log IsNot Nothing
        OpenTaskLogButton.Text = If(_taskResourceTransfer, "打开下载日志", "打开安装器日志")
        OpenTaskLogButton.ToolTip = If(log IsNot Nothing, log & Environment.NewLine & If(_taskResourceTransfer, "此文件是资源下载缓存的累计日志。", "此文件是该游戏目录的累计安装器日志。"), TaskLogUnavailableMessage())
        If _taskFinished Then
            If _taskResourceTransfer Then
                TaskResultLocation.Text = If(String.IsNullOrEmpty(_taskResourceDestination), "", "目标文件：" & _taskResourceDestination)
            Else
                TaskResultLocation.Text = If(_taskOutcome = TaskOutcome.Succeeded AndAlso Not String.IsNullOrEmpty(_taskInstancePath), "实例位置：" & _taskInstancePath,
                    If(String.IsNullOrEmpty(_taskRoot), "", "游戏目录：" & _taskRoot))
            End If
        End If
    End Sub

    Private Function TaskLogUnavailableMessage() As String
        Return If(_taskResourceTransfer, "此任务尚无可用的下载日志。", "此任务尚无可用的安装器日志。")
    End Function

    Private Function TaskFolderUnavailableMessage() As String
        Return If(_taskResourceTransfer, "下载文件不存在或无法访问。", "此任务的版本文件夹不存在或无法访问。")
    End Function

    Private Function TaskInformation() As String
        Dim outcome = If(_taskOutcome = TaskOutcome.Succeeded, "成功", If(_taskOutcome = TaskOutcome.Cancelled, "已取消", "失败"))
        Dim lines As New List(Of String) From {
            ProductInfo.DisplayName & " " & ProductInfo.VersionText,
            "任务：" & _taskName, "Minecraft：" & If(_taskMinecraft, "未记录"),
            "组件：" & If(String.IsNullOrEmpty(_taskComponents), "原版", _taskComponents),
            "结果：" & outcome}
        If _taskResourceTransfer Then
            lines.Add("目标文件：" & If(_taskResourceDestination, "未记录"))
        Else
            lines.Add("游戏目录：" & If(_taskRoot, "未记录"))
            lines.Add("实例位置：" & If(_taskInstancePath, "未发布"))
        End If
        lines.Add("详情：" & _taskDiagnostic)
        Return String.Join(Environment.NewLine, lines)
    End Function

    Private Sub CopyTaskInformation(sender As Object, e As RoutedEventArgs)
        If Not _taskFinished Then Return
        DispatchTaskAction("copy", TaskInformation())
    End Sub

    Private Sub OpenTaskFolder(sender As Object, e As RoutedEventArgs)
        If Not _taskFinished OrElse _taskOutcome <> TaskOutcome.Succeeded Then Return
        Dim folder = TaskFolderPath()
        If folder Is Nothing Then
            TaskActionStatus.Text = TaskFolderUnavailableMessage()
            RefreshTaskResultActions()
            Return
        End If
        DispatchTaskAction("folder", folder)
    End Sub

    Private Sub OpenInstallerLog(sender As Object, e As RoutedEventArgs)
        If Not _taskFinished Then Return
        Dim log = TaskLogPath()
        If log Is Nothing Then
            TaskActionStatus.Text = TaskLogUnavailableMessage()
            RefreshTaskResultActions()
            Return
        End If
        DispatchTaskAction("log", log)
    End Sub

    Private Sub DispatchTaskAction(kind As String, value As String)
        Dim generation = _taskGeneration
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
            ' Shell/clipboard dispatch can reenter the UI. Only the task which
            ' accepted this action may publish its success or failure feedback.
            If generation <> _taskGeneration OrElse _closing OrElse Not _taskFinished Then Return
            TaskActionStatus.Text = If(kind = "copy", "任务信息已复制。", If(kind = "folder",
                If(_taskResourceTransfer, "已打开文件所在文件夹。", "已打开此任务的版本文件夹。"),
                If(_taskResourceTransfer, "已打开此任务的下载日志。", "已打开此任务所在游戏目录的安装器日志。")))
        Catch ex As Exception
            If generation = _taskGeneration AndAlso Not _closing AndAlso _taskFinished Then TaskActionStatus.Text = "操作未完成：" & ex.Message
        End Try
    End Sub
End Class
