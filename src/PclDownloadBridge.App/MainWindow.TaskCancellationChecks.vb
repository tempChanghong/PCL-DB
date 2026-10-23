Imports System.ComponentModel
Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Shared Function ObserveCancellationReentrancy(directory As String) As JObject
        Dim previousContext = SynchronizationContext.Current
        Dim result As New JObject From {{"success", True}, {"cancelled", True}, {"cleanup", True}, {"close", True}}
        Dim cases As New JArray()
        Try
            For Each mode In {"inline-cancelled", "inline-success", "inline-cleanup", "deferred-cancelled", "close-cancelled", "close-success", "close-deferred"}
                Dim owner As New MainWindow(True)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim root = Path.Combine(Path.GetTempPath(), "PclDownloadBridge-CancelReentry-" & Guid.NewGuid().ToString("N"))
                IO.Directory.CreateDirectory(root)
                owner.SelectVersion(owner._versions(0)) : owner.SelectManualFolder(root)
                owner.RepairBox.IsChecked = False : owner.InstanceName.Text = mode
                Dim published = Path.Combine(root, "versions", mode)
                ' Deliberately permit inline continuations: this models an installer
                ' finishing on the same dispatcher during CancellationToken.Cancel.
                Dim gate As New TaskCompletionSource(Of InstallResult)()
                Dim registration As CancellationTokenRegistration
                Dim requests = 0, callbacks = 0, callbackSawPending = False, completedInsideCallback = False
                Dim tokenOwned As CancellationToken
                Dim deferred = mode.Contains("deferred"), closing = mode.StartsWith("close-", StringComparison.Ordinal)
                Dim successful = mode.EndsWith("success", StringComparison.Ordinal), cleanup = mode.EndsWith("cleanup", StringComparison.Ordinal)
                owner._installRunner = Function(entry, gameRoot, name, selection, isolation, defaultIsolation, token)
                                           requests += 1 : tokenOwned = token
                                           Dim progress = owner.CreateTaskProgress()
                                           registration = token.Register(Sub()
                                                                             callbacks += 1
                                                                             callbackSawPending = owner._taskCancelling AndAlso owner.TaskTitle.Text = "正在取消…" AndAlso
                                                                                 Not owner.CancelButton.IsEnabled AndAlso owner.TaskProgress.IsIndeterminate AndAlso
                                                                                 owner.TaskSpeed.Text = "—" AndAlso owner.TaskConnections.Text = "—"
                                                                             progress.Report(QueuedTaskValue("取消回调内排队的进度", 0.99, 99999999))
                                                                             If deferred Then Return
                                                                             If successful Then
                                                                                 IO.Directory.CreateDirectory(published)
                                                                                 File.WriteAllText(Path.Combine(published, "keep.txt"), "committed fixture")
                                                                                 gate.TrySetResult(New InstallResult With {.InstancePath = published, .FileCount = 1})
                                                                             ElseIf cleanup Then
                                                                                 gate.TrySetException(New IOException("受控的同步取消清理失败"))
                                                                             Else
                                                                                 gate.TrySetCanceled()
                                                                             End If
                                                                             completedInsideCallback = Not owner._busy AndAlso owner._taskFinished AndAlso owner._cancellation Is Nothing
                                                                         End Sub)
                                           Return gate.Task
                                       End Function
                Try
                    owner.StartInstall(Nothing, Nothing)
                    Dim began = owner._busy AndAlso Not owner._taskFinished AndAlso requests = 1
                    owner.StartInstall(Nothing, Nothing)
                    Dim firstClose As New CancelEventArgs()
                    If closing Then owner.WindowClosing(owner, firstClose) Else owner.CancelInstall(Nothing, Nothing)
                    Dim pendingPreserved = True
                    If deferred Then
                        pendingPreserved = owner._busy AndAlso Not owner._taskFinished AndAlso owner._taskCancelling AndAlso
                            owner.TaskTitle.Text = "正在取消…" AndAlso owner.TaskProgress.IsIndeterminate AndAlso Not owner.CancelButton.IsEnabled
                        If closing Then pendingPreserved = pendingPreserved AndAlso firstClose.Cancel AndAlso Not owner._closing
                        Dim secondClose As New CancelEventArgs()
                        If closing Then
                            owner.WindowClosing(owner, secondClose)
                            pendingPreserved = pendingPreserved AndAlso secondClose.Cancel AndAlso owner.StatusText.Text.Contains("稍后关闭")
                        Else
                            owner.CancelInstall(Nothing, Nothing)
                        End If
                        pendingPreserved = pendingPreserved AndAlso callbacks = 1
                        gate.TrySetCanceled()
                    End If
                    owner.DrainTaskCallbacks()
                    Dim title = owner.TaskTitle.Text, status = owner.StatusText.Text, detail = owner.TaskDetail.Text
                    Dim expectedTitle = If(successful, "安装完成 · " & mode, "已取消，可重新开始")
                    Dim terminal = Not owner._busy AndAlso owner._cancellation Is Nothing AndAlso owner._taskFinished AndAlso
                        owner._taskOutcome = If(successful, TaskOutcome.Succeeded, TaskOutcome.Cancelled) AndAlso
                        title = expectedTitle AndAlso Not owner.TaskProgress.IsIndeterminate AndAlso owner.TaskSpeed.Text = "0 B/s" AndAlso
                        owner.TaskConnections.Text = "0" AndAlso owner.TaskResultActions.Visibility = Visibility.Visible
                    If successful Then terminal = terminal AndAlso owner.TaskProgress.Value = 1 AndAlso File.ReadAllText(Path.Combine(published, "keep.txt")) = "committed fixture"
                    If cleanup Then terminal = terminal AndAlso owner.TaskInformation().Contains("受控的同步取消清理失败")
                    Dim closeSettled = True
                    If closing Then
                        If deferred Then
                            Dim finalClose As New CancelEventArgs()
                            owner.WindowClosing(owner, finalClose)
                            closeSettled = Not finalClose.Cancel AndAlso owner._closing
                        Else
                            closeSettled = Not firstClose.Cancel AndAlso owner._closing
                        End If
                    End If
                    owner.CancelInstall(Nothing, Nothing) : owner.DrainTaskCallbacks()
                    Dim stable = owner.TaskTitle.Text = title AndAlso owner.StatusText.Text = status AndAlso owner.TaskDetail.Text = detail
                    Dim passed = began AndAlso requests = 1 AndAlso callbacks = 1 AndAlso callbackSawPending AndAlso tokenOwned.IsCancellationRequested AndAlso
                        (deferred OrElse completedInsideCallback) AndAlso pendingPreserved AndAlso terminal AndAlso closeSettled AndAlso stable
                    cases.Add(New JObject From {{"case", mode}, {"began", began}, {"requests", requests}, {"callbacks", callbacks},
                        {"callbackSawPendingUi", callbackSawPending}, {"completedInsideCallback", completedInsideCallback}, {"pendingPreserved", pendingPreserved},
                        {"terminalPreserved", terminal}, {"title", title}, {"status", status}, {"indeterminate", owner.TaskProgress.IsIndeterminate},
                        {"closeSettled", closeSettled}, {"lateProgressAndCancelIgnored", stable}, {"passed", passed}})
                    Dim group = If(closing, "close", If(successful, "success", If(cleanup, "cleanup", "cancelled")))
                    result(group) = CBool(result(group)) AndAlso passed
                Finally
                    If Not gate.Task.IsCompleted Then gate.TrySetCanceled()
                    owner.DrainTaskCallbacks()
                    registration.Dispose() : owner._installRunner = Nothing
                End Try
            Next
        Finally
            SynchronizationContext.SetSynchronizationContext(previousContext)
            result("cases") = cases
            File.WriteAllText(Path.Combine(directory, "task-cancel-reentrancy.json"), result.ToString())
        End Try
        Return result
    End Function
End Class
