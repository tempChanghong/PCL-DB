Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyTaskFeedbackUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current, focus = LocalModCheckGetNativeFocus()
        Dim host As New Border With {.Width = 900, .Height = 700}, observations As New JObject
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                     File.WriteAllText(Path.Combine(directory, "task-feedback.json"), observations.ToString())
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 700)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim layout = ControlLayout(host), work = owner.CheckResourceFeedbackAsync(directory, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 90000) Then Throw New TimeoutException("Task feedback checks did not settle.")
                work.GetAwaiter().GetResult()
                owner.CheckResultActionFeedback(directory, layout, check)
            End Using
            Dim failed = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failed.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failed))
        Finally
            owner._closing = True : owner.CancelResourceRequests() : owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._managementMotion.Finish() : owner._categoryMotion.Finish() : owner.FinishPackContentMotion()
            host.Child = Nothing : owner.Content = original : SynchronizationContext.SetSynchronizationContext(previous) : LocalModCheckSetNativeFocus(focus)
        End Try
    End Sub

    Private Async Function CheckResourceFeedbackAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _managementMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion() : layout()
                               End Sub
        For launcher = 0 To 2
            For Each install In {False, True}
                For outcome = 0 To 2
                    _motionPreview = False : ShowDownload(Nothing, Nothing) : settle()
                    Dim fixture = Path.Combine(directory, "feedback-" & launcher & "-" & install & "-" & outcome), folders = PrepareManagementFixture(fixture)
                    FolderList.SelectedItem = folders(launcher)
                    Dim target = CurrentInstallTarget(), instance = InstalledInstanceCatalog.Read(target, "我的原版", CancellationToken.None)
                    SelectResourceKind(CommunityResourceKind.Shader)
                    Dim release As New ModpackRelease With {.Platform = ModpackPlatform.Modrinth, .Kind = CommunityResourceKind.Shader, .Id = "feedback", .ProjectId = "feedback-project",
                        .Name = "Feedback resource", .FileName = "feedback.zip", .Size = 100, .Sha1 = New String("a"c, 40), .GameVersions = {"1.20.1"}, .Loaders = {"iris"}}
                    Dim project As New ModpackProject With {.Platform = release.Platform, .Kind = release.Kind, .Id = release.ProjectId, .Name = "Feedback detail"}
                    _packProject = project : _packVerifiedProject = project : _packDetailCompletedProject = project
                    _packReleases = New List(Of ModpackRelease) From {release}
                    RenderPackProject(project) : ChangePackMode(PackBrowserMode.Detail) : InitializePackVersions() : settle()
                    Dim plan = If(install, CommunityResourceInstallation.Create(release, instance, Nothing, CancellationToken.None), Nothing)
                    Dim destination = If(plan?.Destination, Path.Combine(fixture, "saved", release.FileName)), logRoot = Path.Combine(fixture, "logs")
                    Dim logPath = Path.Combine(logRoot, ".vanilla-downloader/installer.log")
                    IO.Directory.CreateDirectory(Path.GetDirectoryName(logPath)) : File.WriteAllText(logPath, "controlled resource log")
                    Dim files = DirectoryFilesForModUpdateChecks(Path.GetDirectoryName(target.Root)), gate As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
                    Dim progress As IProgress(Of TransferProgress) = Nothing
                    Dim run As Func(Of String, IProgress(Of TransferProgress), CancellationToken, Task) =
                        Async Function(filePath, reporter, token)
                            progress = reporter : Await gate.Task : token.ThrowIfCancellationRequested()
                            IO.Directory.CreateDirectory(Path.GetDirectoryName(filePath)) : File.WriteAllText(filePath, "controlled resource")
                        End Function
                    _resourceSaveRunner = Function(value, filePath, reporter, token) run(filePath, reporter, token)
                    _resourceInstallRunner = Function(value, cache, policy, reporter, token) run(value.Destination, reporter, token)
                    _motionPreview = (launcher + outcome) Mod 2 = 0 : RestoreAppearance(New JObject From {{"darkMode", (launcher + outcome) Mod 2 <> 0}})
                    Dim generation = _taskGeneration, work = RunResourceSaveAsync(release, destination, plan), runningTitle = If(install, "安装 ", "下载 ") & release.FileName
                    ' Route log actions to a temporary cache; never create a log in
                    ' the user's real shared ResourceCache during preview checks.
                    _taskRoot = logRoot
                    ShowManagement(Nothing, Nothing)
                    Dim motion = _surfaceMotion.Transition
                    If motion IsNot Nothing Then PauseSelectionClock(motion)
                    progress.Report(New TransferProgress With {.Phase = "archive", .Stage = "下载文件", .StageFraction = 0.25, .NetworkBytes = 1024, .Detail = New String("x"c, 300)})
                    DrainTaskCallbacks() : layout()
                    Dim label = "/launcher=" & target.Kind.ToString() & "/install=" & install & "/outcome=" & outcome
                    check(TaskTitle.Text = runningTitle AndAlso StatusText.Text.Contains("下载日志") AndAlso Not StatusText.Text.Contains("安装器日志") AndAlso
                        TransferSource.Text = "正在处理本地文件" AndAlso _taskGeneration = generation + 1 AndAlso _busy AndAlso ManagementPage.IsVisible AndAlso _surfaceMotion.Transition Is motion,
                        "resource progress preserves its accepted download or install title, diagnostics kind and current page clock" & label)
                    settle() : ShowSettings(Nothing, Nothing) : settle() : ShowProgress(Nothing, Nothing) : settle()
                    If outcome = 0 Then
                        gate.TrySetResult(True)
                    ElseIf outcome = 1 Then
                        gate.TrySetException(New IOException("controlled resource failure"))
                    Else
                        CancelInstall(Nothing, Nothing) : gate.TrySetResult(True)
                    End If
                    Await work : settle()
                    Dim oldProgress = progress, title = TaskTitle.Text, detail = TaskDetail.Text
                    Dim expectedTitle = If(outcome = 0, If(install, "安装完成 · ", "下载完成 · ") & release.FileName,
                        If(outcome = 1, If(install, "安装", "下载") & "未完成，可重试", "已取消，可重新" & If(install, "安装", "下载")))
                    oldProgress.Report(QueuedTaskValue("late terminal resource", bytes:=99999999)) : DrainTaskCallbacks()
                    check(_taskFinished AndAlso Not _busy AndAlso _taskOutcome = If(outcome = 0, TaskOutcome.Succeeded, If(outcome = 1, TaskOutcome.Failed, TaskOutcome.Cancelled)) AndAlso
                        TaskTitle.Text = expectedTitle AndAlso title = expectedTitle AndAlso TaskDetail.Text = detail AndAlso _taskResourceDestination = destination AndAlso
                        files.All(Function(item) File.ReadAllBytes(item.Key).SequenceEqual(item.Value)),
                        "resource terminal state and target survive late progress after success failure or cancellation" & label)
                    Dim actions As New List(Of String)
                    _taskAction = Sub(kind, value) actions.Add(kind & ":" & value)
                    OpenInstallerLog(Nothing, Nothing)
                    Dim correct = actions.SequenceEqual({"log:" & Path.GetFullPath(logPath)}) AndAlso TaskActionStatus.Text.Contains("下载日志") AndAlso Not TaskActionStatus.Text.Contains("安装器")
                    If outcome = 0 Then
                        OpenTaskFolder(Nothing, Nothing)
                        correct = correct AndAlso actions.Last() = "folder:" & Path.GetDirectoryName(destination) AndAlso TaskActionStatus.Text.Contains("文件所在文件夹")
                        File.Delete(destination) : OpenTaskFolder(Nothing, Nothing)
                        correct = correct AndAlso Not OpenTaskFolderButton.IsEnabled AndAlso TaskActionStatus.Text.Contains("文件") AndAlso Not TaskActionStatus.Text.Contains("版本文件夹")
                    End If
                    check(correct AndAlso Not OpenTaskFolderButton.ToolTip.ToString().Contains("版本文件夹"),
                        "resource result actions describe download logs and file folders and handle a missing publication accurately" & label)
                    _taskAction = Nothing : gate = New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
                    Dim retry = RunResourceSaveAsync(release, destination, plan)
                    oldProgress.Report(QueuedTaskValue("old resource retry", bytes:=99999999))
                    progress.Report(New TransferProgress With {.Phase = "archive", .Stage = "重试", .StageFraction = 0.5, .NetworkBytes = 2048})
                    DrainTaskCallbacks()
                    Dim retryOwned = TaskTitle.Text = runningTitle AndAlso _lastNetworkBytes = 2048 AndAlso _taskGeneration = generation + 2
                    gate.TrySetResult(True) : Await retry : settle()
                    ResumeInstallation(Nothing, Nothing) : settle() : ShowSettings(Nothing, Nothing) : settle() : ShowDownload(Nothing, Nothing) : settle()
                    check(retryOwned AndAlso _taskOutcome = TaskOutcome.Succeeded AndAlso _packProject Is project AndAlso PackDetailSurface.IsVisible AndAlso
                        PageSurface.IsHitTestVisible AndAlso DownloadViewHost.IsHitTestVisible AndAlso SameManagementTarget(target, CurrentInstallTarget()) AndAlso
                        files.All(Function(item) File.ReadAllBytes(item.Key).SequenceEqual(item.Value)),
                        "retry retains its own resource title and progress while return restores the accepted detail and launcher target" & label)
                Next
            Next
        Next
    End Function

    Private Sub CheckResultActionFeedback(directory As String, layout As Action, check As Action(Of Boolean, String))
        Dim number = 0
        For Each resource In {False, True}
            For Each action In {"copy", "folder", "log"}
                For Each fail In {False, True}
                    number += 1
                    Dim root = Path.Combine(directory, "action-" & number), oldName = "old-task", newName = "new-task"
                    Dim instancePath = Path.Combine(root, "versions", oldName), resourcePath = Path.Combine(root, "old.zip"), log = Path.Combine(root, ".vanilla-downloader/installer.log")
                    IO.Directory.CreateDirectory(instancePath) : IO.Directory.CreateDirectory(Path.GetDirectoryName(log))
                    File.WriteAllText(resourcePath, "old resource") : File.WriteAllText(log, "old log")
                    ResetTaskView(oldName, False, root)
                    _taskResourceTransfer = resource : _taskResourceFile = If(resource, resourcePath, Nothing) : _taskInstancePath = instancePath
                    FinishTaskView(True, "old success") : ShowProgress(Nothing, Nothing)
                    Dim generation = _taskGeneration, calls = 0, captured As String = Nothing
                    Using request As New CancellationTokenSource()
                        _taskAction = Sub(kind, value)
                                          calls += 1 : captured = value : _busy = True : _cancellation = request
                                          ResetTaskView(newName, False, root) : _taskResourceTransfer = Not resource
                                          TaskActionStatus.Text = "newer task owns feedback"
                                          If fail Then Throw New IOException("retired result action failure")
                                      End Sub
                        Select Case action
                            Case "copy" : CopyTaskInformation(Nothing, Nothing)
                            Case "folder" : OpenTaskFolder(Nothing, Nothing)
                            Case "log" : OpenInstallerLog(Nothing, Nothing)
                        End Select
                        layout()
                        check(calls = 1 AndAlso captured IsNot Nothing AndAlso Not captured.Contains(newName) AndAlso _taskGeneration = generation + 1 AndAlso _busy AndAlso
                            Not _taskFinished AndAlso _cancellation Is request AndAlso Not request.IsCancellationRequested AndAlso TaskTitle.Text = newName & " 安装" AndAlso
                            TaskActionStatus.Text = "newer task owns feedback",
                            "a retired result action cannot overwrite a newer task's feedback after success or error/resource=" & resource & "/action=" & action & "/fault=" & fail)
                        _busy = False : _cancellation = Nothing : _taskAction = Nothing : FinishTaskView(True, "new success") : SetBusy(False)
                    End Using
                Next
            Next
        Next
    End Sub
End Class
