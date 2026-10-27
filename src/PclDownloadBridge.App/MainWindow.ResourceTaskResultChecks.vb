Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyInstanceTaskResultsUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), previous = SynchronizationContext.Current, focus = LocalModCheckGetNativeFocus()
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     If Not ok Then Throw New InvalidOperationException(label)
                                                     passed.Add(label)
                                                 End Sub
        Try
            SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
            owner.VerifyTaskResults(check, directory)
        Finally
            owner._closing = True : owner.CancelResourceRequests() : owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._managementMotion.Finish() : owner._categoryMotion.Finish()
            SynchronizationContext.SetSynchronizationContext(previous) : LocalModCheckSetNativeFocus(focus)
        End Try
    End Sub

    Public Sub VerifyResourceTaskResultUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim focus = LocalModCheckGetNativeFocus(), observations As New JObject
        Dim host As New Border With {.Width = 900, .Height = 700}, layout = ControlLayout(host)
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 700)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckResourceTaskResultAsync(directory, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 45000) Then Throw New TimeoutException("Resource task result checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "resource-task-result.json"), observations.ToString())
            Dim failed = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failed.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failed))
        Finally
            owner._closing = True : owner.CancelResourceRequests() : owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._managementMotion.Finish() : owner._categoryMotion.Finish() : owner.FinishPackContentMotion()
            host.Child = Nothing : owner.Content = original
            SynchronizationContext.SetSynchronizationContext(previous) : LocalModCheckSetNativeFocus(focus)
        End Try
    End Sub

    Private Async Function CheckResourceTaskResultAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _managementMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion() : layout()
                               End Sub
        For launcher = 0 To 2
            For Each install In {False, True}
                For outcome = 0 To 2
                    Dim folders = PrepareManagementFixture(directory)
                    FolderList.SelectedItem = folders(launcher)
                    ShowDownload(Nothing, Nothing)
                    Dim target = CurrentInstallTarget(), instance = InstalledInstanceCatalog.Read(target, "我的原版", CancellationToken.None)
                    Dim files = DirectoryFilesForModUpdateChecks(Path.GetDirectoryName(target.Root))
                    Dim release As New ModpackRelease With {.Platform = ModpackPlatform.Modrinth, .Kind = CommunityResourceKind.Shader, .Id = "result-file", .ProjectId = "result-project",
                        .Name = "Resource result", .FileName = "result.zip", .Size = 100, .Sha1 = New String("a"c, 40), .GameVersions = {"1.20.1"}, .Loaders = {"iris"}}
                    Dim plan = If(install, CommunityResourceInstallation.Create(release, instance, Nothing, CancellationToken.None), Nothing)
                    Dim destination = If(plan?.Destination, Path.Combine(directory, "saved-" & Guid.NewGuid().ToString("N"), release.FileName))
                    IO.Directory.CreateDirectory(Path.GetDirectoryName(destination))
                    Dim gate As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
                    _resourceInstallRunner = Function(selected, cache, policy, progress, token) gate.Task
                    _resourceSaveRunner = Function(selected, filePath, progress, token) gate.Task
                    _motionPreview = (launcher + outcome) Mod 2 = 0
                    RestoreAppearance(New JObject From {{"darkMode", (launcher + outcome) Mod 2 <> 0}}) : settle()
                    Dim generation = _taskGeneration, work = RunResourceSaveAsync(release, destination, plan)
                    Dim label = "/launcher=" & target.Kind.ToString() & "/install=" & install & "/outcome=" & outcome
                    check(_busy AndAlso Not work.IsCompleted AndAlso _taskResourceFile Is Nothing AndAlso TaskInformation().Contains("目标文件：" & destination),
                        "a running resource task records its target without claiming that a file was published" & label)
                    If outcome = 0 Then
                        File.WriteAllText(destination, "controlled completed resource") : gate.TrySetResult(True)
                    ElseIf outcome = 1 Then
                        gate.TrySetException(New IOException("Controlled resource failure"))
                    Else
                        CancelInstall(Nothing, Nothing) : gate.TrySetCanceled()
                    End If
                    Await work : settle()
                    Dim expected = If(outcome = 0, TaskOutcome.Succeeded, If(outcome = 1, TaskOutcome.Failed, TaskOutcome.Cancelled))
                    Dim later = Path.Combine(directory, "later-root-" & Guid.NewGuid().ToString("N"))
                    IO.Directory.CreateDirectory(later) : SelectManualFolder(later) : settle()
                    Dim copied As String = Nothing
                    _taskAction = Sub(kind, value)
                                      If kind = "copy" Then copied = value
                                  End Sub
                    CopyTaskInformation(Nothing, Nothing)
                    check(_taskFinished AndAlso _taskOutcome = expected AndAlso Not _busy AndAlso _taskGeneration = generation + 1 AndAlso
                        TaskResultLocation.Text = "目标文件：" & destination AndAlso copied IsNot Nothing AndAlso copied.Contains("目标文件：" & destination) AndAlso
                        Not copied.Contains("游戏目录：") AndAlso Not copied.Contains("实例位置：") AndAlso Not copied.Contains(later) AndAlso
                        If(outcome = 0, _taskResourceFile = destination AndAlso TaskFolderPath() = Path.GetDirectoryName(destination) AndAlso OpenTaskFolderButton.IsEnabled,
                            _taskResourceFile Is Nothing AndAlso TaskFolderPath() Is Nothing AndAlso Not OpenTaskFolderButton.IsEnabled) AndAlso
                        files.All(Function(item) File.Exists(item.Key) AndAlso File.ReadAllBytes(item.Key).SequenceEqual(item.Value)),
                        "terminal resource results retain their own target after navigation and expose folders only on success" & label)
                Next
            Next
        Next
        Dim fakeRoot = Path.Combine(directory, "resource-result-log-" & Guid.NewGuid().ToString("N")), log = Path.Combine(fakeRoot, ".vanilla-downloader/installer.log")
        IO.Directory.CreateDirectory(Path.GetDirectoryName(log)) : File.WriteAllText(log, "recorded resource download log")
        ResetTaskView("resource log", False, fakeRoot, hasArchive:=True)
        _taskResourceTransfer = True : FinishTaskView(False, "resource failure")
        Dim resourceLogCorrect = TaskLogPath() = Path.GetFullPath(log) AndAlso OpenTaskLogButton.IsEnabled AndAlso OpenTaskLogButton.Text = "打开下载日志" AndAlso
            OpenTaskLogButton.ToolTip.ToString().Contains("资源下载缓存的累计日志") AndAlso Not OpenTaskLogButton.ToolTip.ToString().Contains("游戏目录")
        ResetTaskView("instance log", False, fakeRoot) : FinishTaskView(False, "instance failure")
        check(resourceLogCorrect AndAlso TaskLogPath() = Path.GetFullPath(log) AndAlso OpenTaskLogButton.IsEnabled AndAlso OpenTaskLogButton.Text = "打开安装器日志" AndAlso
            Not _taskResourceTransfer AndAlso _taskResourceFile Is Nothing,
            "resource results retain their download cache log with accurate labels while a new instance task restores its own log presentation")
    End Function
End Class
