Imports System.ComponentModel
Imports System.Threading
Imports System.Windows
Imports System.Windows.Threading
Imports PlainCraftDownloader.Core

Partial Public Class MainWindow
    Private Sub DrainTaskCallbacks()
        Dim frame As New DispatcherFrame()
        Dim stopFrame = Dispatcher.BeginInvoke(DispatcherPriority.Background, New Action(Sub() frame.Continue = False))
        Dispatcher.PushFrame(frame)
    End Sub

    Private Shared Function QueuedTaskValue(label As String, Optional fraction As Double = 0.1, Optional bytes As Long = 1024) As TransferProgress
        Return New TransferProgress With {.Phase = "files", .Stage = label, .StageFraction = fraction, .Total = 10, .Completed = 1,
            .NetworkBytes = bytes, .BytesPerSecond = 512, .ActiveConnections = 2, .DynamicSplits = 3, .Retries = 1,
            .CurrentFile = label & ".jar", .Source = "example.org", .Detail = label}
    End Function

    Private Sub VerifyTaskLifecycle(check As Action(Of Boolean, String))
        Dim previousContext = SynchronizationContext.Current
        Dim previousCancellation = _cancellation
        Dim previousClosing = _closing
        SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(Dispatcher))
        Using cancellation As New CancellationTokenSource(), retryCancellation As New CancellationTokenSource()
            Try
                _busy = True : _cancellation = cancellation
                ResetTaskView("失败回调样本", False)
                Dim oldProgress = CreateTaskProgress()
                oldProgress.Report(QueuedTaskValue("初始下载"))
                DrainTaskCallbacks()
                check(TaskSpeed.Text = "512 B/s" AndAlso TransferFile.Text = "初始下载.jar", "active task progress is delivered through the real WPF dispatcher")
                oldProgress.Report(QueuedTaskValue("失败前已经排队", 0.9, 9000000))
                TaskTitle.Text = "安装未完成，可重试"
                FinishTaskView(False, "保留这条失败原因")
                Dim failureDetail = TaskDetail.Text
                DrainTaskCallbacks() ' Like the nested dispatcher of an open error dialog.
                check(TaskTitle.Text = "安装未完成，可重试" AndAlso TaskDetail.Text = failureDetail AndAlso TaskSpeed.Text = "0 B/s" AndAlso TaskConnections.Text = "0", "queued progress cannot reopen a failed task while its error dialog still owns the UI")
                check(_taskRows("files").Icon.Visibility = Visibility.Visible AndAlso _taskRows("files").Icon.Height = 15 AndAlso _taskRows("files").State.Visibility = Visibility.Collapsed, "late failure progress preserves the terminal error icon and phase state")

                ResetTaskView("新的重试任务", False)
                Dim currentProgress = CreateTaskProgress()
                currentProgress.Report(QueuedTaskValue("新任务自己的文件", bytes:=2048))
                DrainTaskCallbacks()
                Dim retryDetail = TaskDetail.Text, retryPercent = TaskPercent.Text, retryStats = TransferStats.Text
                oldProgress.Report(QueuedTaskValue("上次任务的晚到回调", 0.99, 99999999))
                DrainTaskCallbacks()
                check(TaskTitle.Text = "新的重试任务 安装" AndAlso TaskDetail.Text = retryDetail AndAlso TaskPercent.Text = retryPercent AndAlso TransferStats.Text = retryStats AndAlso TransferFile.Text = "新任务自己的文件.jar", "previous-task callbacks cannot contaminate a retry's progress bytes counters or file")
                currentProgress.Report(QueuedTaskValue("当前任务继续下载", 0.2, 4096))
                DrainTaskCallbacks()
                check(TransferFile.Text = "当前任务继续下载.jar" AndAlso TaskDetail.Text.Contains("4 KiB"), "the current task continues accepting progress after stale callbacks are discarded")
                currentProgress.Report(QueuedTaskValue("成功之前已经排队", 0.3, 5000))
                TaskTitle.Text = "安装完成 · 新的重试任务"
                FinishTaskView(True, "成功结果")
                DrainTaskCallbacks()
                check(TaskTitle.Text.StartsWith("安装完成", StringComparison.Ordinal) AndAlso TaskPercent.Text = "100.00 %" AndAlso TaskProgress.Value = 1 AndAlso TaskSpeed.Text = "0 B/s", "queued progress cannot lower a successful task from its terminal hundred percent state")

                ResetTaskView("取消样本", True)
                SetBusy(True)
                Dim cancelledProgress = CreateTaskProgress()
                cancelledProgress.Report(QueuedTaskValue("取消前的文件"))
                DrainTaskCallbacks()
                CancelInstall(Nothing, Nothing)
                check(cancellation.IsCancellationRequested AndAlso Not CancelButton.IsEnabled AndAlso TaskTitle.Text = "正在取消…" AndAlso TaskSpeed.Text = "—" AndAlso TaskConnections.Text = "—", "cancel requests only the owned operation and disables repeated cancellation without displaying stale live statistics")
                Dim cancelDetail = TaskDetail.Text, cancelStatus = StatusText.Text
                cancelledProgress.Report(QueuedTaskValue("取消后才到达", 0.8, 8888888))
                DrainTaskCallbacks()
                check(TaskTitle.Text = "正在取消…" AndAlso TaskDetail.Text = cancelDetail AndAlso StatusText.Text = cancelStatus AndAlso TransferFile.Text = "取消前的文件.jar", "queued progress cannot replace cancellation status or its explanation")
                CancelInstall(Nothing, Nothing)
                check(TaskTitle.Text = "正在取消…" AndAlso TaskDetail.Text = cancelDetail AndAlso Not CancelButton.IsEnabled, "a repeated cancel action keeps the stopping state stable")
                FinishTaskView(False, "已取消，已完成的文件会保留")
                TaskTitle.Text = "已取消，可重新开始"
                _busy = False : SetBusy(False)
                cancelledProgress.Report(QueuedTaskValue("取消完成后晚到", 0.9))
                DrainTaskCallbacks()
                check(TaskTitle.Text = "已取消，可重新开始" AndAlso GameFolderCard.IsEnabled AndAlso DownloadSettingsCard.IsEnabled AndAlso Not CancelButton.IsEnabled, "a completed cancellation unlocks settings without accepting more progress")
                CancelInstall(Nothing, Nothing)
                check(TaskTitle.Text = "已取消，可重新开始", "an idle cancel event cannot reopen a terminal task")

                _cancellation = retryCancellation : _busy = True
                ResetTaskView("取消后的新任务", False)
                SetBusy(True)
                Dim retryProgress = CreateTaskProgress()
                check(TaskPercent.Text = "0.00 %" AndAlso TransferStats.Text = "暂无传输数据" AndAlso TransferFile.Text = "—" AndAlso TransferSource.Text = "—" AndAlso CancelButton.IsEnabled AndAlso Not retryCancellation.IsCancellationRequested, "retry starts with fresh counters source and an independent cancellation token")
                cancelledProgress.Report(QueuedTaskValue("取消任务的迟到文件", 0.99, 99999999))
                retryProgress.Report(QueuedTaskValue("重试自己的文件", bytes:=512))
                DrainTaskCallbacks()
                check(TaskDetail.Text.Contains("512 B") AndAlso TransferFile.Text = "重试自己的文件.jar", "a cancelled task cannot leak cumulative bytes into its replacement")
                Dim closing As New CancelEventArgs()
                WindowClosing(Me, closing)
                retryProgress.Report(QueuedTaskValue("关闭请求后晚到", 0.9))
                DrainTaskCallbacks()
                check(closing.Cancel AndAlso retryCancellation.IsCancellationRequested AndAlso Not CancelButton.IsEnabled AndAlso TaskTitle.Text = "正在取消…" AndAlso StatusText.Text.Contains("稍后关闭"), "closing an active task follows the same stable cancel path and waits for shutdown")
                FinishTaskView(False, "关闭前停止完成")
                _busy = False
                _closing = True
                Dim beforeClose = TaskDetail.Text
                retryProgress.Report(QueuedTaskValue("关闭后晚到", 0.99))
                DrainTaskCallbacks()
                check(TaskDetail.Text = beforeClose, "progress after window shutdown cannot overwrite its final task result")
            Finally
                _busy = False : _closing = previousClosing : _cancellation = previousCancellation
                SetBusy(False)
                SynchronizationContext.SetSynchronizationContext(previousContext)
            End Try
        End Using
    End Sub
End Class
