Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyForeignModWriteUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim host As New Border With {.Width = 900, .Height = 700}, layout = ControlLayout(host), observations As New JObject
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 700)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckForeignModWritesAsync(directory, host, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 60000) Then Throw New TimeoutException("Foreign Mod write checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "foreign-mod-write.json"), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            owner._closing = True : owner.CancelManagementRequest() : owner.CancelLocalModRequest() : owner._localModWriteRequest?.Cancel()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner._managementMotion.Finish()
            host.Child = Nothing : owner.Content = original : SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub

    Private Async Function CheckForeignModWritesAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : _managementMotion.Finish() : layout()
                               End Sub
        For launcher = 0 To 2
            For Each dark In {False, True}
                _motionPreview = False
                Dim folders = PrepareLocalModFixture(directory), game = folders(0).Location, binding = folders(launcher)
                LocalModJarForChecks(Path.Combine(game, "mods/shared.jar"), "foreign_shared")
                FolderList.SelectedItem = binding
                Dim target = CurrentInstallTarget(), nativeA = InstalledInstanceCatalog.Read(target, "Fabric 实例", CancellationToken.None)
                Dim nativeB = InstalledInstanceCatalog.Read(target, "我的原版", CancellationToken.None)
                IO.Directory.CreateDirectory(Path.Combine(nativeB.InstancePath, "PCL"))
                Dim configA = Path.GetFullPath(SetLocalModIsolationForChecks(nativeA, True))
                Dim configB = Path.GetFullPath(SetLocalModIsolationForChecks(nativeB, True))
                LocalModJarForChecks(Path.Combine(nativeB.InstancePath, "mods/other.jar"), "foreign_other")
                Dim protectedFiles = IO.Directory.GetFiles(Path.GetDirectoryName(game), "*", SearchOption.AllDirectories).ToDictionary(Function(path) path, Function(path) File.ReadAllBytes(path))
                Dim realReader As Func(Of InstalledInstance, CancellationToken, Task(Of InstalledInstance)) =
                    Function(instance, token) Task.FromResult(InstalledInstanceCatalog.Read(instance.Target, instance.Name, token))
                Dim realProvider As Func(Of InstalledInstance, CancellationToken, Task(Of List(Of LocalModEntry))) =
                    Function(instance, token) Task.FromResult(LocalModCatalog.Scan(instance, token))
                Dim selectDetail As Action(Of String) =
                    Sub(name)
                        If LocalModsPage.Visibility = Visibility.Visible Then BackFromLocalMods()
                        settle()
                        If ManagementDetailPage.Visibility = Visibility.Visible Then BackFromManagement(Nothing, Nothing)
                        settle() : SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = name)) : settle()
                    End Sub
                Dim openA As Func(Of Task) =
                    Async Function()
                        _localModInstanceReader = realReader : _localModProvider = realProvider
                        ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(True) : settle()
                        selectDetail(nativeA.Name)
                        ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                    End Function
                Await openA()
                _motionPreview = dark : RestoreAppearance(New JObject From {{"darkMode", dark}})
                Dim label = "/launcher=" & target.Kind.ToString() & "/animated-dark=" & dark, taskGeneration = _taskGeneration
                Dim writeGate As New TaskCompletionSource(Of LocalModActionResult)(TaskCreationOptions.RunContinuationsAsynchronously)
                _localModImporter = Function(instance, files, token) writeGate.Task
                Dim writing = ImportLocalModFilesAsync({Path.Combine(_localModInstance.RunDirectory, "mods/first.jar")})
                selectDetail(nativeB.Name)
                Dim nativeGate As New TaskCompletionSource(Of InstalledInstance)(TaskCreationOptions.RunContinuationsAsynchronously)
                Dim rowsGate As New TaskCompletionSource(Of List(Of LocalModEntry))(TaskCreationOptions.RunContinuationsAsynchronously)
                Dim readToken As CancellationToken, calls = 0
                If dark Then
                    _localModInstanceReader = Function(instance, token)
                                                  readToken = token : calls += 1 : Return nativeGate.Task
                                              End Function
                Else
                    _localModProvider = Function(instance, token)
                                            readToken = token : calls += 1 : Return rowsGate.Task
                                        End Function
                End If
                _managementMotion.PauseForChecks = dark
                ShowLocalMods(Nothing, Nothing) : layout()
                Dim generation = _localModGeneration, request = _localModRequest, entry = _managementMotion.Transition
                Dim readable = LocalModStatus.Text.Contains("正在读取")
                If dark Then
                    CancelLocalModOperation(Nothing, Nothing) : writeGate.SetCanceled()
                Else
                    writeGate.SetResult(New LocalModActionResult(Path.Combine(nativeA.InstancePath, "mods"), 0, 1))
                End If
                Await WaitPackUiAsync(Function() Not _localModWriting)
                Dim stable = calls = 1 AndAlso Not readToken.IsCancellationRequested AndAlso _localModGeneration = generation AndAlso _localModRequest Is request
                check(readable AndAlso stable AndAlso _localModScanning AndAlso LocalModStatus.Text.Contains("正在读取") AndAlso
                    (Not dark OrElse (_managementMotion.Transition Is entry AndAlso entry IsNot Nothing AndAlso Not ManagementViewHost.IsHitTestVisible)),
                    "an older instance write completing or cancelling cannot restart a different directory read or its entry motion" & label)
                If dark Then nativeGate.SetResult(InstalledInstanceCatalog.Read(target, nativeB.Name, CancellationToken.None)) Else rowsGate.SetResult(LocalModCatalog.Scan(InstalledInstanceCatalog.Read(target, nativeB.Name, CancellationToken.None), CancellationToken.None))
                Await writing : Await WaitPackUiAsync(Function() Not _localModScanning)
                _managementMotion.PauseForChecks = False : settle()
                check(stable AndAlso _localModReadSucceeded AndAlso _managedInstance.Name = nativeB.Name AndAlso _localMods.Single().ModIds.Contains("foreign_other") AndAlso LocalModImport.IsEnabled,
                    "the originally accepted foreign read completes normally and restores actions after the old writer exits" & label)

                Await openA() : writeGate = New TaskCompletionSource(Of LocalModActionResult)(TaskCreationOptions.RunContinuationsAsynchronously)
                writing = ImportLocalModFilesAsync({Path.Combine(_localModInstance.RunDirectory, "mods/first.jar")})
                selectDetail(nativeB.Name) : ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                Dim accepted = _localModInstance, entries = _localMods, status = LocalModStatus.Text
                generation = _localModGeneration : calls = 0
                _localModInstanceReader = Function(instance, token)
                                              calls += 1 : Return realReader(instance, token)
                                          End Function
                writeGate.SetResult(New LocalModActionResult(Path.Combine(nativeA.InstancePath, "mods"), 0, 1)) : Await writing : settle()
                check(calls = 0 AndAlso _localModGeneration = generation AndAlso _localModInstance Is accepted AndAlso _localMods Is entries AndAlso
                    LocalModStatus.Text = status AndAlso Not LocalModStatus.Text.Contains("正在处理") AndAlso _localModReadSucceeded,
                    "an already accepted list in another physical directory retains its rows selection and feedback without a redundant rescan" & label)

                Await openA() : writeGate = New TaskCompletionSource(Of LocalModActionResult)(TaskCreationOptions.RunContinuationsAsynchronously)
                writing = ImportLocalModFilesAsync({Path.Combine(_localModInstance.RunDirectory, "mods/first.jar")})
                selectDetail(nativeB.Name) : calls = 0
                _localModInstanceReader = Function(instance, token)
                                              calls += 1 : Return Task.FromException(Of InstalledInstance)(New IOException("current B native read failed"))
                                          End Function
                ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                generation = _localModGeneration : status = LocalModStatus.Text
                writeGate.SetResult(New LocalModActionResult(Path.Combine(nativeA.InstancePath, "mods"), 0, 1)) : Await writing : settle()
                Dim retainedFailure = calls = 1 AndAlso _localModGeneration = generation AndAlso LocalModStatus.Text = status AndAlso status.Contains("current B native read failed") AndAlso LocalModRefresh.IsEnabled
                _localModInstanceReader = realReader : RefreshLocalMods(Nothing, New RoutedEventArgs()) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                check(retainedFailure AndAlso _localModReadSucceeded AndAlso _localMods.Single().ModIds.Contains("foreign_other") AndAlso LocalModImport.IsEnabled,
                    "an unrelated write does not overwrite or silently retry the current read failure and explicit retry remains usable" & label)
                SaveControlVisual(host, Path.Combine(directory, "foreign-mod-write-" & target.Kind.ToString() & If(dark, "-dark.png", "-light.png")))

                SetLocalModIsolationForChecks(nativeA, False) : protectedFiles(configA) = File.ReadAllBytes(configA)
                For Each reroute In {False, True}
                    SetLocalModIsolationForChecks(nativeB, reroute) : protectedFiles(configB) = File.ReadAllBytes(configB)
                    Await openA() : writeGate = New TaskCompletionSource(Of LocalModActionResult)(TaskCreationOptions.RunContinuationsAsynchronously)
                    writing = ImportLocalModFilesAsync({Path.Combine(game, "mods/shared.jar")})
                    selectDetail(nativeB.Name)
                    If reroute Then SetLocalModIsolationForChecks(_managedInstance, False) : protectedFiles(configB) = File.ReadAllBytes(configB)
                    calls = 0
                    _localModProvider = Function(instance, token)
                                            calls += 1 : Return realProvider(instance, token)
                                        End Function
                    ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                    Dim waiting = calls = 0 AndAlso Not _localModReadSucceeded AndAlso LocalModStatus.Text.Contains(nativeA.Name)
                    writeGate.SetResult(New LocalModActionResult(Path.Combine(game, "mods"), 0, 1)) : Await writing : settle()
                    check(waiting AndAlso calls = 1 AndAlso _localModReadSucceeded AndAlso _managedInstance.Name = nativeB.Name AndAlso SameGameRoot(_localModInstance.RunDirectory, game) AndAlso
                        _localMods.Single().ModIds.Contains("foreign_shared") AndAlso LocalModImport.IsEnabled AndAlso LocalModSharedHint.IsVisible,
                        If(reroute, "a native reread redirected into the writer directory resumes on exit even when the cached directory differed", "a different instance waiting on the same shared Mods directory resumes exactly once after the writer exits") & label)
                Next

                SetLocalModIsolationForChecks(nativeA, True) : protectedFiles(configA) = File.ReadAllBytes(configA)
                SetLocalModIsolationForChecks(nativeB, True) : protectedFiles(configB) = File.ReadAllBytes(configB)
                Await openA() : writeGate = New TaskCompletionSource(Of LocalModActionResult)(TaskCreationOptions.RunContinuationsAsynchronously)
                writing = ImportLocalModFilesAsync({Path.Combine(nativeA.InstancePath, "mods/first.jar")})
                selectDetail(nativeB.Name)
                nativeGate = New TaskCompletionSource(Of InstalledInstance)(TaskCreationOptions.RunContinuationsAsynchronously)
                _localModInstanceReader = Function(instance, token)
                                              readToken = token : Return nativeGate.Task
                                          End Function
                ShowLocalMods(Nothing, Nothing)
                writeGate.SetResult(New LocalModActionResult(Path.Combine(nativeA.InstancePath, "mods"), 0, 1))
                Await WaitPackUiAsync(Function() Not _localModWriting)
                selectDetail(nativeA.Name) : _localModInstanceReader = realReader : _localModProvider = realProvider
                ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                accepted = _localModInstance : entries = _localMods : generation = _localModGeneration
                If dark Then nativeGate.SetException(New IOException("retired B failure")) Else nativeGate.SetResult(InstalledInstanceCatalog.Read(target, nativeB.Name, CancellationToken.None))
                Await writing : Await Task.Yield() : settle()
                check(readToken.IsCancellationRequested AndAlso _localModGeneration = generation AndAlso _localModInstance Is accepted AndAlso _localMods Is entries AndAlso
                    _managedInstance.Name = nativeA.Name AndAlso Not LocalModStatus.Text.Contains("retired B") AndAlso _localModReadSucceeded,
                    "returning again retires the foreign read and late success or failure cannot replace the newly selected instance" & label)
                check(_taskGeneration = taskGeneration AndAlso Not _localModWriting AndAlso protectedFiles.All(Function(pair) File.Exists(pair.Key) AndAlso File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)) AndAlso
                    IO.Directory.GetFiles(Path.GetDirectoryName(game), "*", SearchOption.AllDirectories).Length = protectedFiles.Count AndAlso SameManagementTarget(_managedInstance.Target, target),
                    "foreign write handoffs cancellation retry and shared rerouting preserve files configurations bindings and the global download task" & label)
                _motionPreview = False : ShowDownload(Nothing, Nothing) : settle()
            Next
        Next
    End Function
End Class
