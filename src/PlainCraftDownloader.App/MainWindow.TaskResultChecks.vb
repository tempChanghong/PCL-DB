Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.IO
Imports Newtonsoft.Json.Linq
Imports PlainCraftDownloader.Core

Partial Public Class MainWindow
    Private Sub VerifyTaskResults(check As Action(Of Boolean, String), artifactDirectory As String)
        Using cancellation As New CancellationTokenSource()
            Try
                _cancellation = cancellation : _busy = True
                ResetTaskView("主动取消样本", False)
                RenderTaskProgress(QueuedTaskValue("正在下载"))
                CancelInstall(Nothing, Nothing)
                FinishTaskView(False, "已取消，已校验文件保留")
                check(_taskRows("files").Icon.Data.ToString() <> System.Windows.Media.Geometry.Parse(FailedIcon).ToString(), "a user-cancelled task does not display the failed-task cross icon")
            Finally
                _busy = False : _cancellation = Nothing : SetBusy(False)
            End Try
        End Using
        Dim originalFolders = _folders.ToList(), originalFolder = _selectedFolder
        Dim savedAppearance = _appearance.ToJson()
        Dim fixture = Path.Combine(Path.GetTempPath(), "PlainCraftDownloader-TaskResult-" & Guid.NewGuid().ToString("N"))
        Dim rootA = Path.Combine(fixture, "原来的游戏目录"), rootB = Path.Combine(fixture, "后来选择的目录")
        Dim logA = Path.Combine(rootA, ".vanilla-downloader", "installer.log")
        Dim logB = Path.Combine(rootB, ".vanilla-downloader", "installer.log")
        Directory.CreateDirectory(Path.GetDirectoryName(logA)) : Directory.CreateDirectory(Path.GetDirectoryName(logB))
        File.WriteAllText(logA, "task A processor fixture") : File.WriteAllText(logB, "task B processor fixture")
        Dim content = DirectCast(Me.Content, FrameworkElement)
        Dim host As New Border With {.Width = 850, .Height = 700}
        Dim source As Interop.HwndSource = Nothing
        Dim layout As Action = Sub()
                                   host.Measure(New Size(host.Width, host.Height))
                                   host.Arrange(New Rect(0, 0, host.Width, host.Height))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        Dim click As Action(Of UIElement) = Sub(control)
                                               PointerEvent(control, Mouse.MouseDownEvent)
                                               PointerEvent(control, Mouse.MouseUpEvent)
                                           End Sub
        Dim capture As Action(Of String) = Sub(name)
                                               DownloadPage.BeginAnimation(OpacityProperty, Nothing) : DownloadPage.Opacity = 1
                                               ApplyAppearance() : layout()
                                               SaveControlVisual(host, Path.Combine(artifactDirectory, name))
                                           End Sub
        Dim actions As New List(Of KeyValuePair(Of String, String))
        Try
            Me.Content = Nothing : host.Child = content
            source = PreviewSurface.Attach(host, 850, 700)
            RestoreAppearance(New JObject From {{"theme", "colorful"}})
            SelectVersion(_versions(0)) : SelectManualFolder(rootA)
            RepairBox.IsChecked = False : SpeedLimitBox.Text = "0"
            InstanceName.Text = "任务 A 的原版实例"
            Dim instancePath = Path.Combine(rootA, "versions", InstanceName.Text)
            _installRunner = Function(entry, root, name, selection, token)
                                 Directory.CreateDirectory(instancePath)
                                 RenderTaskProgress(New TransferProgress With {.Phase = "files", .Stage = "下载原版游戏与资源文件", .StageFraction = 1, .Total = 3, .Completed = 3, .NetworkBytes = 1024, .Reused = 2})
                                 Return Task.FromResult(New InstallResult With {.InstancePath = instancePath, .FileCount = 3, .Reused = 2, .NetworkBytes = 1024})
                             End Function
            StartInstall(Nothing, Nothing) : layout()
            check(Not _busy AndAlso _cancellation Is Nothing AndAlso _taskOutcome = TaskOutcome.Succeeded AndAlso OpenTaskFolderButton.IsEnabled AndAlso TaskResultLocation.Text.Contains(instancePath) AndAlso CancelButton.Visibility = Visibility.Collapsed, "a successful install exposes its actual instance directory and replaces cancellation with result actions")
            _taskAction = Sub(kind, value) actions.Add(New KeyValuePair(Of String, String)(kind, value))
            SelectManualFolder(rootB) : SelectVersion(_versions(1))
            SelectComponent(_componentEntries(ComponentKind.Fabric)(0))
            InstanceName.Text = "后来改选的实例"
            ShowProgress(Nothing, Nothing) : layout()
            click(OpenTaskFolderButton) : click(OpenTaskLogButton) : click(CopyTaskInfoButton)
            check(actions.Count = 3 AndAlso actions(0).Key = "folder" AndAlso actions(0).Value = instancePath AndAlso actions(1).Key = "log" AndAlso actions(1).Value = logA, "result folder and installer log stay attached to task A after selecting another game root and install plan")
            Dim report = actions(2).Value
            check(actions(2).Key = "copy" AndAlso report.Contains("任务：任务 A 的原版实例") AndAlso report.Contains("Minecraft：" & _versions(0).Id) AndAlso report.Contains("组件：原版") AndAlso report.Contains("结果：成功") AndAlso Not report.Contains("后来改选的实例") AndAlso Not report.Contains(rootB), "copied task information retains the original Minecraft components name root and outcome")
            Dim title = TaskTitle.Text
            _taskAction = Sub(kind, value)
                              Throw New IOException("controlled shell failure")
                          End Sub
            click(OpenTaskFolderButton)
            check(TaskActionStatus.Text.Contains("controlled shell failure") AndAlso TaskTitle.Text = title AndAlso TaskProgress.Value = 1, "a shell failure reports inline without replacing the completed task result")
            _taskAction = Nothing : TaskActionStatus.Text = "preview sentinel"
            click(CopyTaskInfoButton) : click(OpenTaskFolderButton) : click(OpenTaskLogButton)
            check(TaskActionStatus.Text = "preview sentinel", "result preview suppresses real clipboard and shell launches")
            TaskActionStatus.Text = ""
            capture("align-task-success-after-0.29.png")
            host.Width = 810 : host.Height = 470 : layout()
            DownloadProgress.ScrollToEnd() : layout()
            Dim top = CopyTaskInfoButton.TranslatePoint(New Point(), DownloadProgress).Y
            check(top >= 0 AndAlso top + CopyTaskInfoButton.ActualHeight <= DownloadProgress.ViewportHeight AndAlso TaskResultActions.ActualWidth >= 456, "all terminal task actions remain reachable and fit the minimum window")
            capture("align-task-result-minimum-after-0.29.png")
            _taskAction = Sub(kind, value) actions.Add(New KeyValuePair(Of String, String)(kind, value))
            Dim count = actions.Count
            Directory.Delete(instancePath) ' This fixture created an empty directory; never recursive.
            click(OpenTaskFolderButton)
            check(actions.Count = count AndAlso Not OpenTaskFolderButton.IsEnabled AndAlso TaskActionStatus.Text.Contains("不存在"), "a removed instance directory produces inline feedback instead of opening another location")
            File.Delete(logA)
            click(OpenTaskLogButton)
            check(actions.Count = count AndAlso Not OpenTaskLogButton.IsEnabled AndAlso TaskActionStatus.Text.Contains("日志"), "a disappeared task log disables its action without falling back to the newly selected root")
            host.Width = 850 : host.Height = 700
            SelectVersion(_versions(0)) : InstanceName.Text = "失败的实例"
            Dim cleanReset = False
            _installRunner = Function(entry, root, name, selection, token)
                                 cleanReset = TaskResultActions.Visibility = Visibility.Collapsed AndAlso CancelButton.Visibility = Visibility.Visible AndAlso TaskResultLocation.Text = "" AndAlso TaskActionStatus.Text = "" AndAlso _taskInstancePath Is Nothing
                                 RenderTaskProgress(QueuedTaskValue("失败的下载样本"))
                                 Return Task.FromException(Of InstallResult)(New IOException("受控的下载失败原因"))
                             End Function
            StartInstall(Nothing, Nothing) : layout()
            check(cleanReset, "starting a replacement task clears old result paths feedback and actions before its runner starts")
            check(_taskOutcome = TaskOutcome.Failed AndAlso TaskResultActions.Visibility = Visibility.Visible AndAlso OpenTaskFolderButton.Visibility = Visibility.Collapsed AndAlso _taskRows("files").Icon.Data.ToString() = System.Windows.Media.Geometry.Parse(FailedIcon).ToString(), "a failed install retains the PCL failure icon and does not offer an unpublished instance directory")
            click(CopyTaskInfoButton)
            report = actions.Last().Value
            check(report.Contains("结果：失败") AndAlso report.Contains("受控的下载失败原因") AndAlso report.Contains(rootB) AndAlso Not report.Contains(rootA) AndAlso report.Contains("实例位置：未发布"), "failure information belongs to the new task and includes its actual reason without stale result paths")
            DownloadProgress.ScrollToTop() : layout()
            capture("align-task-failed-after-0.29.png")
            ResumeInstallation(Nothing, Nothing) : InstanceName.Text = "取消的实例"
            _installRunner = Function(entry, root, name, selection, token)
                                 RenderTaskProgress(QueuedTaskValue("取消前的下载样本"))
                                 click(CancelButton)
                                 Return Task.FromCanceled(Of InstallResult)(token)
                             End Function
            StartInstall(Nothing, Nothing) : layout()
            check(Not _busy AndAlso _taskOutcome = TaskOutcome.Cancelled AndAlso TaskTitle.Text = "已取消，可重新开始" AndAlso _taskRows("files").State.Text = "已取消" AndAlso OpenTaskFolderButton.Visibility = Visibility.Collapsed, "real user cancellation finishes with cancelled stages and no published-instance action")
            click(CopyTaskInfoButton)
            check(actions.Last().Value.Contains("结果：已取消") AndAlso Not actions.Last().Value.Contains("结果：失败"), "copied cancellation information distinguishes user cancellation from download failure")
            capture("align-task-cancelled-after-0.29.png")
            ResumeInstallation(Nothing, Nothing) : InstanceName.Text = "取消后退出异常的实例"
            _installRunner = Function(entry, root, name, selection, token)
                                 click(CancelButton)
                                 Return Task.FromException(Of InstallResult)(New IOException("受控的取消清理异常"))
                             End Function
            StartInstall(Nothing, Nothing)
            check(Not _busy AndAlso _taskOutcome = TaskOutcome.Cancelled AndAlso TaskTitle.Text = "已取消，可重新开始" AndAlso TaskInformation().Contains("受控的取消清理异常"), "a non-cancellation exception during requested shutdown stays cancelled while retaining its diagnostic reason")
            File.WriteAllText(Path.Combine(artifactDirectory, "task-result-context-0.29.json"), Newtonsoft.Json.JsonConvert.SerializeObject(New With {.OriginalRoot = rootA, .SelectedRoot = rootB, .CapturedActions = actions.Select(Function(a) New With {.Kind = a.Key, .Value = a.Value})}, Newtonsoft.Json.Formatting.Indented))
        Finally
            _taskAction = Nothing : _installRunner = Nothing
            _busy = False : SetBusy(False)
            If source IsNot Nothing Then
                source.RootVisual = Nothing : source.Dispose()
            End If
            host.Child = Nothing
            If Me.Content Is Nothing Then Me.Content = content
            _folders = originalFolders
            FolderList.ItemsSource = Nothing : FolderList.ItemsSource = _folders
            FolderList.SelectedItem = originalFolder
            RestoreAppearance(savedAppearance)
        End Try
    End Sub
End Class
