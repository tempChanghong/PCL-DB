Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyOnlinePackStartUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim focus = LocalModCheckGetNativeFocus(), observations As New JObject
        Dim host As New Border With {.Width = 900, .Height = 700}, layout = ControlLayout(host)
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                     File.WriteAllText(Path.Combine(directory, "online-pack-start.json"), observations.ToString())
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 700)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckOnlinePackStartAsync(directory, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 180000) Then Throw New TimeoutException("Online pack startup checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            Dim failed = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failed.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failed))
        Finally
            owner._closing = True : owner.CompleteOnlinePrompt(Nothing) : owner.CancelResourceRequests() : owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._managementMotion.Finish() : owner._categoryMotion.Finish() : owner.FinishPackContentMotion()
            host.Child = Nothing : owner.Content = original
            SynchronizationContext.SetSynchronizationContext(previous) : LocalModCheckSetNativeFocus(focus)
        End Try
    End Sub

    Private Async Function CheckOnlinePackStartAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _managementMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion() : layout()
                               End Sub
        Dim number = 0
        For launcher = 0 To 2
            For Each platform In {ModpackPlatform.CurseForge, ModpackPlatform.Modrinth}
                For reader = 0 To 2
                    For scenario = 0 To 3
                        number += 1 : _motionPreview = False : ShowDownload(Nothing, Nothing) : settle()
                        Dim folders = PrepareManagementFixture(Path.Combine(directory, "startup-" & number))
                        FolderList.SelectedItem = folders(launcher)
                        Dim target = CurrentInstallTarget(), name = "online-" & number, intruderName = "intruder-" & number
                        _managementProvider = Function(binding, token) Task.FromResult(InstalledInstanceCatalog.Scan(binding, token))
                        SelectResourceKind(CommunityResourceKind.Modpack) : ChangePackMode(PackBrowserMode.Search) : settle()
                        Dim files = DirectoryFilesForModUpdateChecks(Path.GetDirectoryName(target.Root))
                        Dim release As New ModpackRelease With {.Platform = platform, .Kind = CommunityResourceKind.Modpack, .Id = "primary", .ProjectId = "start-project",
                            .Name = "Primary pack", .FileName = If(platform = ModpackPlatform.CurseForge, "primary.zip", "primary.mrpack"), .Size = 100,
                            .Sha1 = New String("a"c, 40), .GameVersions = {"1.20.1"}, .Loaders = {"fabric"}}
                        Dim project As New ModpackProject With {.Platform = platform, .Kind = release.Kind, .Id = release.ProjectId, .Name = "Startup pack detail"}
                        _packProject = project : _packVerifiedProject = project : _packDetailCompletedProject = project
                        _packReleases = New List(Of ModpackRelease) From {release}
                        RenderPackProject(project) : ChangePackMode(PackBrowserMode.Detail) : InitializePackVersions() : settle()
                        Dim intruder = release.Copy() : intruder.Id = "intruder"
                        Dim gate As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
                        Dim extraGate As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
                        Dim calls As New List(Of String), nested As Task = Nothing, sawReservation = False, escaped = False
                        _onlineInstallRunner = Async Function(value, gameRoot, instanceName, progress, token)
                                                   calls.Add(value.Id)
                                                   Await If(value.Id = "primary", gate.Task, extraGate.Task)
                                                   token.ThrowIfCancellationRequested()
                                                   Dim published = Path.Combine(gameRoot, "versions", instanceName)
                                                   IO.Directory.CreateDirectory(published)
                                                   File.WriteAllText(Path.Combine(published, "fixture.txt"), value.Id)
                                                   Return New InstallResult With {.InstancePath = published, .FileCount = 1}
                                               End Function
                        RestoreAppearance(New JObject From {{"darkMode", (launcher + reader + scenario) Mod 2 = 0}})
                        _motionPreview = (launcher + reader + scenario \ 2) Mod 2 = 0 : settle()
                        Dim generation = _taskGeneration, capturedCase = scenario
                        Dim label = "/launcher=" & target.Kind.ToString() & "/platform=" & platform.ToString() & "/reader=" & reader & "/case=" & scenario
                        Using retired As New CancellationTokenSource()
                            Select Case reader
                                Case 0 : _packReadCancellation = retired
                                Case 1 : _packSearchRequest = retired
                                Case 2 : _packDetailRequest = retired
                            End Select
                            Using registration = retired.Token.Register(Sub()
                                                                            sawReservation = _busy AndAlso _cancellation IsNot Nothing AndAlso Not _taskFinished AndAlso
                                                                                _taskGeneration = generation + 1 AndAlso _taskName = name AndAlso _taskRoot = target.Root AndAlso
                                                                                _taskInstallTarget Is target AndAlso Not _taskResourceTransfer
                                                                            Select Case capturedCase
                                                                                Case 0 : nested = RunOnlinePackInstallationAsync(intruder, target.Root, intruderName, 0, target:=target)
                                                                                Case 1 : CancelInstall(Nothing, Nothing)
                                                                                Case 2 : Throw New IOException("Controlled online pack startup cancellation fault")
                                                                                Case 3 : ShowManagement(Nothing, Nothing)
                                                                            End Select
                                                                        End Sub)
                                Dim work = RunOnlinePackInstallationAsync(release, target.Root, name, 0, target:=target)
                                extraGate.TrySetResult(True)
                                If nested IsNot Nothing Then Await nested
                                Await Task.Yield() : settle()
                                Dim terminal = scenario = 1 OrElse scenario = 2
                                check(sawReservation AndAlso Not work.IsFaulted AndAlso calls.Count = If(terminal, 0, 1) AndAlso Not calls.Contains("intruder") AndAlso
                                    If(terminal, _taskFinished AndAlso Not _busy, _busy AndAlso Not _taskFinished AndAlso Not work.IsCompleted) AndAlso
                                    If(scenario = 3, ManagementPage.IsVisible, True),
                                    "online pack startup binds one immutable task before retiring readers and preserves newer navigation" & label)
                                gate.TrySetResult(True)
                                Try
                                    Await work
                                Catch
                                    escaped = True
                                End Try
                                settle()
                                Dim expected = If(scenario = 1, TaskOutcome.Cancelled, If(scenario = 2, TaskOutcome.Failed, TaskOutcome.Succeeded))
                                check(Not escaped AndAlso _taskFinished AndAlso _taskOutcome = expected AndAlso Not _busy AndAlso _cancellation Is Nothing AndAlso _taskGeneration = generation + 1 AndAlso
                                    _taskName = name AndAlso TaskInformation().Contains("游戏目录：" & target.Root) AndAlso Not IO.Directory.Exists(Path.Combine(target.Root, "versions", intruderName)) AndAlso
                                    If(scenario = 3, ManagementPage.IsVisible, DownloadProgress.IsVisible AndAlso DownloadSidebar.Visibility = Visibility.Collapsed) AndAlso
                                    If(terminal, _taskInstancePath Is Nothing AndAlso Not IO.Directory.Exists(Path.Combine(target.Root, "versions", name)),
                                        _taskInstancePath = Path.Combine(target.Root, "versions", name) AndAlso TaskFolderPath() = _taskInstancePath) AndAlso
                                    files.All(Function(item) File.Exists(item.Key) AndAlso File.ReadAllBytes(item.Key).SequenceEqual(item.Value)),
                                    "online pack startup fault or cancellation settles its own task and preserves existing native files" & label)
                            End Using
                            If _packReadCancellation Is retired Then _packReadCancellation = Nothing
                            If _packSearchRequest Is retired Then _packSearchRequest = Nothing
                            If _packDetailRequest Is retired Then _packDetailRequest = Nothing
                        End Using
                        calls.Clear() : gate = New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
                        Dim retryName = name & "-retry", retry = RunOnlinePackInstallationAsync(release, target.Root, retryName, 0, target:=target)
                        gate.TrySetResult(True) : Await retry : settle()
                        ResumeInstallation(Nothing, Nothing) : settle() : ShowSettings(Nothing, Nothing) : settle() : ShowDownload(Nothing, Nothing) : settle()
                        check(_taskFinished AndAlso _taskOutcome = TaskOutcome.Succeeded AndAlso _taskGeneration = generation + 2 AndAlso calls.SequenceEqual({"primary"}) AndAlso
                            _taskInstancePath = Path.Combine(target.Root, "versions", retryName) AndAlso SameManagementTarget(target, CurrentInstallTarget()) AndAlso
                            _downloadView = DownloadView.Modpacks AndAlso PackDetailSurface.IsVisible AndAlso _packProject Is project AndAlso PageSurface.IsHitTestVisible AndAlso DownloadViewHost.IsHitTestVisible AndAlso
                            files.All(Function(item) File.Exists(item.Key) AndAlso File.ReadAllBytes(item.Key).SequenceEqual(item.Value)),
                            "independent online pack retry and detail return preserve accepted target, native configuration and input" & label)
                    Next
                Next
            Next
        Next
    End Function
End Class
