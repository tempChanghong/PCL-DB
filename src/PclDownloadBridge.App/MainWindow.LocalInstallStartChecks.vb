Imports System.IO
Imports System.IO.Compression
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyLocalInstallStartUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim focus = LocalModCheckGetNativeFocus(), observations As New JObject, faults As New List(Of Exception)
        Dim capture As DispatcherUnhandledExceptionEventHandler = Sub(sender, args)
                                                                     faults.Add(args.Exception) : args.Handled = True
                                                                 End Sub
        Dim host As New Border With {.Width = 900, .Height = 700}, layout = ControlLayout(host)
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                     File.WriteAllText(Path.Combine(directory, "local-install-start.json"), observations.ToString())
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        AddHandler owner.Dispatcher.UnhandledException, capture
        Try
            Using surface = PreviewSurface.Attach(host, 900, 700)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckLocalInstallStartAsync(directory, layout, check, faults, host)
                If Not PumpMotionUntil(Function() work.IsCompleted, 180000) Then Throw New TimeoutException("Local installation startup checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "local-install-start-faults.json"),
                New JObject From {{"count", faults.Count}, {"errors", New JArray(faults.Select(Function(item) item.GetBaseException().Message))}}.ToString())
            Dim failed = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failed.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failed))
        Finally
            RemoveHandler owner.Dispatcher.UnhandledException, capture
            owner._closing = True : owner.CancelResourceRequests() : owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._managementMotion.Finish() : owner._categoryMotion.Finish() : owner._pageSwitch.Finish() : owner.FinishPackContentMotion()
            host.Child = Nothing : owner.Content = original
            SynchronizationContext.SetSynchronizationContext(previous) : LocalModCheckSetNativeFocus(focus)
        End Try
    End Sub

    Private Async Function CheckLocalInstallStartAsync(directory As String, layout As Action, check As Action(Of Boolean, String), faults As List(Of Exception), host As Border) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _managementMotion.Finish() : _categoryMotion.Finish()
                                   If _downloadView = DownloadView.Versions OrElse _downloadView = DownloadView.Install Then _pageSwitch.Finish()
                                   FinishPackContentMotion() : _titleNavigation.Finish() : layout()
                               End Sub
        Dim number = 0
        For launcher = 0 To 2
            For packFormat = 0 To 2
                For source = 0 To 1
                    For scenario = 0 To 3
                        number += 1 : _motionPreview = False : ShowDownload(Nothing, Nothing) : settle()
                        Dim fixture = Path.Combine(directory, "startup-" & number), folders = PrepareManagementFixture(fixture)
                        FolderList.SelectedItem = folders(launcher)
                        Dim target = CurrentInstallTarget(), name = "local-" & number
                        SelectVersion(_versions.Single(Function(item) item.Id = "1.20.1")) : InstanceName.Text = name : RepairBox.IsChecked = False : settle()
                        If packFormat > 0 Then
                            Dim manifest As JObject, entryName As String, fileName As String
                            If packFormat = 1 Then
                                manifest = New JObject From {{"manifestType", "minecraftModpack"}, {"manifestVersion", 1}, {"name", "Local CF"}, {"version", "1"},
                                    {"minecraft", New JObject From {{"version", "1.20.1"}, {"modLoaders", New JArray()}}}, {"files", New JArray()}}
                                entryName = "manifest.json" : fileName = "pack.zip"
                            Else
                                manifest = New JObject From {{"game", "minecraft"}, {"formatVersion", 1}, {"name", "Local MR"}, {"versionId", "1"},
                                    {"dependencies", New JObject From {{"minecraft", "1.20.1"}}}, {"files", New JArray()}}
                                entryName = "modrinth.index.json" : fileName = "pack.mrpack"
                            End If
                            Dim archivePath = Path.Combine(fixture, fileName)
                            Using archive = ZipFile.Open(archivePath, ZipArchiveMode.Create), writer As New StreamWriter(archive.CreateEntry(entryName).Open())
                                writer.Write(manifest.ToString())
                            End Using
                            If packFormat = 1 Then
                                SelectCurseForgePackage(CurseForgePackage.Read(archivePath)) : PackInstanceName.Text = name
                            Else
                                SelectModrinthPackage(ModrinthPackage.Read(archivePath)) : ModrinthInstanceName.Text = name
                            End If
                            ChangePackMode(PackBrowserMode.Local)
                        End If
                        settle()
                        Dim files = DirectoryFilesForModUpdateChecks(Path.GetDirectoryName(target.Root)), gate As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
                        Dim calls = 0, sawReservation = False, callbackCalls = 0, capturedCase = scenario
                        Dim tokenUsed As CancellationToken, isolated As InstanceIsolationMode, expectedSurface As FrameworkElement = Nothing, expectedClock As Media.Animation.ClockGroup = Nothing
                        _installRunner = Async Function(entry, root, instanceName, selection, isolation, defaultIsolation, token)
                                             calls += 1 : tokenUsed = token : isolated = isolation
                                             Await gate.Task : token.ThrowIfCancellationRequested()
                                             Dim published = Path.Combine(root, "versions", instanceName)
                                             IO.Directory.CreateDirectory(published) : File.WriteAllText(Path.Combine(published, "fixture.txt"), "controlled local install")
                                             Return New InstallResult With {.InstancePath = published, .FileCount = 1}
                                         End Function
                        RestoreAppearance(New JObject From {{"darkMode", (launcher + packFormat + scenario) Mod 2 = 0}})
                        _motionPreview = (launcher + packFormat + scenario \ 2) Mod 2 = 0 : settle()
                        Dim generation = _taskGeneration, beforeFaults = faults.Count
                        Dim label = "/launcher=" & target.Kind.ToString() & "/packFormat=" & packFormat & "/source=" & source & "/case=" & scenario
                        Dim notify As Action = Sub()
                                                  callbackCalls += 1
                                                  sawReservation = _busy AndAlso _cancellation IsNot Nothing AndAlso Not _taskFinished AndAlso _taskGeneration = generation + 1 AndAlso
                                                      _taskName = name AndAlso _taskRoot = target.Root AndAlso SameManagementTarget(_taskInstallTarget, target)
                                                  Select Case capturedCase
                                                      Case 0 : StartInstall(Nothing, Nothing)
                                                      Case 1 : CancelInstall(Nothing, Nothing)
                                                      Case 2 : Throw New IOException("Controlled local install startup fault")
                                                      Case 3
                                                          ShowManagement(Nothing, Nothing)
                                                          expectedSurface = ActiveSurface() : expectedClock = _surfaceMotion.Transition
                                                  End Select
                                              End Sub
                        Dim visibility As DependencyPropertyChangedEventHandler = Sub(sender, args)
                                                                                     If CBool(args.NewValue) AndAlso callbackCalls = 0 Then notify()
                                                                                 End Sub
                        Using retired As New CancellationTokenSource()
                            Dim registration As CancellationTokenRegistration
                            If source = 0 Then
                                _packReadCancellation = retired : registration = retired.Token.Register(notify)
                            Else
                                AddHandler TaskPanel.IsVisibleChanged, visibility
                            End If
                            StartInstall(Nothing, Nothing)
                            Await Task.Yield() : layout()
                            Await WaitPackUiAsync(Function() calls > 0 OrElse _taskFinished OrElse faults.Count > beforeFaults)
                            Dim terminal = scenario = 1 OrElse scenario = 2
                            check(callbackCalls = 1 AndAlso sawReservation AndAlso faults.Count = beforeFaults AndAlso calls = If(terminal, 0, 1) AndAlso
                                If(terminal, _taskFinished AndAlso Not _busy, _busy AndAlso Not _taskFinished) AndAlso
                                If(scenario = 3, ActiveSurface() Is expectedSurface AndAlso _surfaceMotion.Transition Is expectedClock, True),
                                "local installation binds its task before UI and reader notifications and preserves accepted navigation" & label)
                            gate.TrySetResult(True)
                            Await WaitPackUiAsync(Function() Not _busy OrElse faults.Count > beforeFaults) : settle()
                            Dim expected = If(scenario = 1, TaskOutcome.Cancelled, If(scenario = 2, TaskOutcome.Failed, TaskOutcome.Succeeded))
                            check(_taskFinished AndAlso _taskOutcome = expected AndAlso Not _busy AndAlso _cancellation Is Nothing AndAlso faults.Count = beforeFaults AndAlso _taskGeneration = generation + 1 AndAlso
                                _taskName = name AndAlso If(terminal, _taskInstancePath Is Nothing, _taskInstancePath = Path.Combine(target.Root, "versions", name) AndAlso Not tokenUsed.IsCancellationRequested) AndAlso
                                If(scenario = 3, ManagementPage.IsVisible, DownloadProgress.IsVisible AndAlso ModpackPage.Visibility = System.Windows.Visibility.Collapsed AndAlso
                                    VersionPage.Visibility = System.Windows.Visibility.Collapsed AndAlso InstallPage.Visibility = System.Windows.Visibility.Collapsed AndAlso
                                    DownloadSidebar.Visibility = System.Windows.Visibility.Collapsed AndAlso DownloadToolbar.Visibility = System.Windows.Visibility.Collapsed AndAlso
                                    InnerTitle.Text = "任务管理" AndAlso TaskResultActions.IsVisible AndAlso PageSurface.IsHitTestVisible AndAlso DownloadViewHost.IsHitTestVisible) AndAlso
                                files.All(Function(item) File.ReadAllBytes(item.Key).SequenceEqual(item.Value)),
                                "startup cancellation or UI failure settles locally without Dispatcher faults or changing existing native files" & label)
                            If launcher = 0 AndAlso source = 1 AndAlso scenario = 2 Then SaveControlVisual(host, Path.Combine(directory, "task-fault-layout-" & packFormat & ".png"))
                            RemoveHandler TaskPanel.IsVisibleChanged, visibility : registration.Dispose()
                            If _packReadCancellation Is retired Then _packReadCancellation = Nothing
                        End Using
                        ' Baseline-only recovery after observing an escaped Async Sub
                        ' fault; it is recorded as a failure before any test cleanup.
                        If _busy Then
                            Dim abandoned = _cancellation : _cancellation = Nothing : _busy = False : abandoned?.Dispose() : SetBusy(False)
                        End If
                        calls = 0 : gate = New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
                        ShowDownload(Nothing, Nothing) : ResumeInstallation(Nothing, Nothing) : settle()
                        If packFormat = 0 Then SetDownloadView(DownloadView.Install) : InstanceName.Text = name & "-retry"
                        If packFormat = 1 Then ChangePackMode(PackBrowserMode.Local) : PackInstanceName.Text = name & "-retry"
                        If packFormat = 2 Then ChangePackMode(PackBrowserMode.Local) : ModrinthInstanceName.Text = name & "-retry"
                        settle() : StartInstall(Nothing, Nothing) : gate.TrySetResult(True)
                        Await WaitPackUiAsync(Function() Not _busy) : settle()
                        ResumeInstallation(Nothing, Nothing) : settle() : ShowSettings(Nothing, Nothing) : settle() : ShowDownload(Nothing, Nothing) : settle()
                        check(calls = 1 AndAlso _taskOutcome = TaskOutcome.Succeeded AndAlso _taskGeneration = generation + 2 AndAlso
                            _taskInstancePath = Path.Combine(target.Root, "versions", name & "-retry") AndAlso Not tokenUsed.IsCancellationRequested AndAlso
                            isolated = If(packFormat = 0, InstanceIsolationMode.FollowPcl, InstanceIsolationMode.Enabled) AndAlso SameManagementTarget(target, CurrentInstallTarget()) AndAlso
                            PageSurface.IsHitTestVisible AndAlso DownloadViewHost.IsHitTestVisible AndAlso files.All(Function(item) File.ReadAllBytes(item.Key).SequenceEqual(item.Value)),
                            "independent retry preserves original launcher isolation and target and returns usable input" & label)
                    Next
                Next
            Next
        Next
    End Function
End Class
