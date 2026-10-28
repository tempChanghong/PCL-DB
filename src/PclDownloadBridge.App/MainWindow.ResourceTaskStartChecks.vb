Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyResourceTaskStartUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim focus = LocalModCheckGetNativeFocus(), observations As New JObject
        Dim host As New Border With {.Width = 900, .Height = 700}, layout = ControlLayout(host)
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                     File.WriteAllText(Path.Combine(directory, "resource-task-start.json"), observations.ToString())
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 700)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckResourceTaskStartAsync(directory, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 90000) Then Throw New TimeoutException("Resource task startup checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
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

    Private Async Function CheckResourceTaskStartAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _managementMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion() : layout()
                               End Sub
        For launcher = 0 To 2
            For Each install In {False, True}
                For scenario = 0 To 3
                    _motionPreview = False : ShowDownload(Nothing, Nothing) : settle()
                    Dim folders = PrepareManagementFixture(Path.Combine(directory, "startup-" & launcher & "-" & install & "-" & scenario))
                    FolderList.SelectedItem = folders(launcher)
                    Dim target = CurrentInstallTarget(), instance = InstalledInstanceCatalog.Read(target, "我的原版", CancellationToken.None)
                    _managementProvider = Function(binding, token) Task.FromResult(New List(Of InstalledInstance) From {instance})
                    SelectResourceKind(CommunityResourceKind.Shader) : ChangePackMode(PackBrowserMode.Search) : settle()
                    Dim files = DirectoryFilesForModUpdateChecks(Path.GetDirectoryName(target.Root))
                    Dim release As New ModpackRelease With {.Platform = ModpackPlatform.Modrinth, .Kind = CommunityResourceKind.Shader, .Id = "primary", .ProjectId = "start-project",
                        .Name = "Primary resource", .FileName = "primary.zip", .Size = 100, .Sha1 = New String("a"c, 40), .GameVersions = {"1.20.1"}, .Loaders = {"iris"}}
                    Dim project As New ModpackProject With {.Platform = release.Platform, .Kind = release.Kind, .Id = release.ProjectId, .Name = "Startup resource detail"}
                    _packProject = project : _packVerifiedProject = project : _packDetailCompletedProject = project
                    _packReleases = New List(Of ModpackRelease) From {release}
                    RenderPackProject(project) : ChangePackMode(PackBrowserMode.Detail) : settle()
                    Dim intruder = release.Copy() : intruder.Id = "intruder" : intruder.FileName = "intruder.zip"
                    Dim plan = If(install, CommunityResourceInstallation.Create(release, instance, Nothing, CancellationToken.None), Nothing)
                    Dim destination = If(plan?.Destination, Path.Combine(Path.GetDirectoryName(target.Root), "saved", release.FileName))
                    Dim intruderPath = Path.Combine(Path.GetDirectoryName(target.Root), "intruder.zip")
                    Dim gate As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
                    Dim extraGate As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
                    Dim calls As New List(Of String), nested As Task = Nothing, sawReservation = False, escaped = False
                    Dim run As Func(Of ModpackRelease, String, CancellationToken, Task) =
                        Async Function(value, filePath, token)
                            calls.Add(value.Id)
                            Dim wait = If(value.Id = "primary", gate, extraGate)
                            Await wait.Task
                            token.ThrowIfCancellationRequested()
                            IO.Directory.CreateDirectory(Path.GetDirectoryName(filePath))
                            File.WriteAllText(filePath, "controlled " & value.Id)
                        End Function
                    _resourceSaveRunner = Function(value, filePath, progress, token) run(value, filePath, token)
                    _resourceInstallRunner = Function(value, cache, policy, progress, token) run(value.Release, value.Destination, token)
                    RestoreAppearance(New JObject From {{"darkMode", (launcher + scenario) Mod 2 = 0}})
                    _motionPreview = (launcher + scenario \ 2) Mod 2 = 0 : settle()
                    Dim generation = _taskGeneration, capturedCase = scenario
                    Dim label = "/launcher=" & target.Kind.ToString() & "/install=" & install & "/case=" & scenario
                    Using retired As New CancellationTokenSource()
                        _dependencyRequest = retired
                        Using registration = retired.Token.Register(Sub()
                                                                        sawReservation = _busy AndAlso _cancellation IsNot Nothing AndAlso Not _taskFinished AndAlso
                                                                            _taskGeneration = generation + 1 AndAlso _taskResourceDestination = destination
                                                                        Select Case capturedCase
                                                                            Case 0 : nested = RunResourceSaveAsync(intruder, intruderPath)
                                                                            Case 1 : CancelInstall(Nothing, Nothing)
                                                                            Case 2 : Throw New IOException("Controlled resource startup cancellation fault")
                                                                            Case 3 : ShowManagement(Nothing, Nothing)
                                                                        End Select
                                                                    End Sub)
                            Dim work = RunResourceSaveAsync(release, destination, plan)
                            extraGate.TrySetResult(True)
                            If nested IsNot Nothing Then Await nested
                            Await Task.Yield() : settle()
                            Dim terminal = scenario = 1 OrElse scenario = 2
                            check(sawReservation AndAlso Not work.IsFaulted AndAlso calls.Count = If(terminal, 0, 1) AndAlso Not calls.Contains("intruder") AndAlso
                                If(terminal, _taskFinished AndAlso Not _busy, _busy AndAlso Not _taskFinished AndAlso Not work.IsCompleted) AndAlso
                                If(scenario = 3, ManagementPage.IsVisible, True),
                                "resource startup reserves one task before old cancellation, rejects duplicates and preserves later navigation" & label)
                            If scenario = 1 Then gate.TrySetCanceled() Else gate.TrySetResult(True)
                            Try
                                Await work
                            Catch
                                escaped = True
                            End Try
                            settle()
                            Dim expected = If(scenario = 1, TaskOutcome.Cancelled, If(scenario = 2, TaskOutcome.Failed, TaskOutcome.Succeeded))
                            check(Not escaped AndAlso _taskFinished AndAlso _taskOutcome = expected AndAlso Not _busy AndAlso _cancellation Is Nothing AndAlso _taskGeneration = generation + 1 AndAlso
                                TaskInformation().Contains("目标文件：" & destination) AndAlso Not File.Exists(intruderPath) AndAlso
                                If(scenario = 3, ManagementPage.IsVisible, DownloadProgress.IsVisible AndAlso DownloadSidebar.Visibility = Visibility.Collapsed) AndAlso
                                If(terminal, _taskResourceFile Is Nothing AndAlso Not File.Exists(destination), _taskResourceFile = destination AndAlso File.Exists(destination)) AndAlso
                                files.All(Function(item) File.Exists(item.Key) AndAlso File.ReadAllBytes(item.Key).SequenceEqual(item.Value)),
                                "startup completion, cancellation or fault leaves its own terminal result and preserves native instance bytes" & label)
                        End Using
                        If _dependencyRequest Is retired Then _dependencyRequest = Nothing
                    End Using
                    calls.Clear() : gate = New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
                    Dim retry = RunResourceSaveAsync(release, destination, plan)
                    gate.TrySetResult(True) : Await retry : settle()
                    ResumeInstallation(Nothing, Nothing) : settle() : ShowSettings(Nothing, Nothing) : settle() : ShowDownload(Nothing, Nothing) : settle()
                    check(_taskFinished AndAlso _taskOutcome = TaskOutcome.Succeeded AndAlso _taskGeneration = generation + 2 AndAlso calls.SequenceEqual({"primary"}) AndAlso
                        _taskResourceFile = destination AndAlso _taskResourceDestination = destination AndAlso SameManagementTarget(target, CurrentInstallTarget()) AndAlso
                        _downloadView = DownloadView.Modpacks AndAlso PackDetailSurface.IsVisible AndAlso PageSurface.IsHitTestVisible AndAlso DownloadViewHost.IsHitTestVisible AndAlso
                        files.All(Function(item) File.Exists(item.Key) AndAlso File.ReadAllBytes(item.Key).SequenceEqual(item.Value)),
                        "resource retry and title/settings return retain the original target and restore input without changing launcher configuration" & label)
                Next
            Next
        Next
    End Function
End Class
