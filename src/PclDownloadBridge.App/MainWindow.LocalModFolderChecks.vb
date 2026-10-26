Imports System.IO
Imports System.Reflection
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyLocalModFolderUi(directory As String, passed As List(Of String))
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
                Dim work = owner.CheckLocalModFoldersAsync(directory, host, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 60000) Then Throw New TimeoutException("Local Mod folders did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "local-mod-folders.json"), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            owner._closing = True : owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner._managementMotion.Finish()
            host.Child = Nothing : owner.Content = original : SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub

    Private Async Function CheckLocalModFoldersAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        ' Keep the same cases compilable in the original synchronous Git baseline.
        Dim requestField = GetType(MainWindow).GetField("_localModFolderRequest", BindingFlags.Instance Or BindingFlags.NonPublic)
        Dim waitOpen As Func(Of Task) = Function() WaitPackUiAsync(Function() requestField Is Nothing OrElse requestField.GetValue(Me) Is Nothing)
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : _managementMotion.Finish() : layout()
                               End Sub
        For launcher = 0 To 2
            For Each dark In {False, True}
                _motionPreview = False
                Dim folders = PrepareLocalModFixture(directory), game = folders(0).Location
                LocalModJarForChecks(Path.Combine(game, "mods/shared.jar"), "folder_shared")
                FolderList.SelectedItem = folders(launcher)
                Dim target = CurrentInstallTarget(), native = InstalledInstanceCatalog.Read(target, "Fabric 实例", CancellationToken.None)
                SetLocalModIsolationForChecks(native, True)
                _managementProvider = Function(binding, token) Task.FromResult(InstalledInstanceCatalog.Scan(binding, token))
                _localModInstanceReader = Function(instance, token) Task.FromResult(InstalledInstanceCatalog.Read(instance.Target, instance.Name, token))
                _localModProvider = Function(instance, token) Task.FromResult(LocalModCatalog.Scan(instance, token))
                ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(True) : settle()
                SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = native.Name)) : settle()
                ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                _motionPreview = dark : RestoreAppearance(New JObject From {{"darkMode", dark}}) : layout()
                Dim label = "/launcher=" & target.Kind.ToString() & "/animated-dark=" & dark
                Dim actions As New List(Of String), workerRead = False, taskGeneration = _taskGeneration
                Dim realReader As Func(Of InstalledInstance, CancellationToken, Task(Of InstalledInstance)) =
                    Function(instance, token) RunManagementWorkerAsync(Function()
                                                                          workerRead = Not Dispatcher.CheckAccess()
                                                                          Return InstalledInstanceCatalog.Read(instance.Target, instance.Name, token)
                                                                      End Function, token)
                _managementFolderReader = realReader : _managementOpenFolder = Sub(folder) actions.Add(folder)
                SetLocalModIsolationForChecks(_managedInstance, False)
                Dim protectedFiles = IO.Directory.GetFiles(Path.GetDirectoryName(game), "*", SearchOption.AllDirectories).ToDictionary(Function(path) path, Function(path) File.ReadAllBytes(path))
                OpenLocalModFolder(LocalModOpen, Nothing) : Await waitOpen() : settle()
                check(actions.SequenceEqual({Path.Combine(game, "mods")}) AndAlso SameGameRoot(_localModInstance.RunDirectory, game) AndAlso
                    LocalModDirectory.Text = Path.Combine(game, "mods") AndAlso _localMods.Count = 1 AndAlso _localModReadSucceeded AndAlso SameManagementTarget(_localModInstance.Target, target),
                    "local Mod folder opens current native isolation and refreshes rows for its redirected directory" & label)
                check(workerRead, "local Mod folder native reads run outside the WPF dispatcher" & label)

                ' A baseline failure must not mask the independent shell-error cases.
                Await LoadLocalModsAsync(True, refreshInstance:=True) : settle()
                Dim retained = _localMods, generation = _localModGeneration, instanceOwner = _localModInstance
                _managementOpenFolder = Sub(folder)
                                            Throw New IOException("controlled local folder launch failure")
                                        End Sub
                OpenLocalModFolder(LocalModOpen, Nothing) : Await waitOpen() : layout()
                check(LocalModStatus.Text.Contains("controlled local folder launch failure") AndAlso LocalModStatus.IsVisible AndAlso
                    _localMods Is retained AndAlso generation = _localModGeneration AndAlso _localModReadSucceeded,
                    "local folder launch failure stays visible without replacing accepted Mod rows or read ownership" & label)
                SaveControlVisual(host, Path.Combine(directory, "local-mod-folder-" & target.Kind.ToString() & If(dark, "-dark.png", "-light.png")))
                actions.Clear() : _managementOpenFolder = Sub(folder) actions.Add(folder)
                OpenLocalModFolder(LocalModOpen, Nothing) : Await waitOpen() : layout()
                check(actions.SequenceEqual({Path.Combine(game, "mods")}) AndAlso LocalModStatus.Text = "" AndAlso _localMods Is retained AndAlso generation = _localModGeneration,
                    "local folder retry clears its error without rebinding unchanged Mod rows" & label)

                If requestField IsNot Nothing Then
                    Dim gate As New TaskCompletionSource(Of InstalledInstance)(TaskCreationOptions.RunContinuationsAsynchronously)
                    Dim retiredToken As CancellationToken, returned = False
                    Dim delayed As Func(Of InstalledInstance, CancellationToken, Task(Of InstalledInstance)) =
                        Async Function(instance, token)
                            retiredToken = token
                            Try
                                Return Await gate.Task
                            Finally
                                returned = True
                            End Try
                        End Function
                    actions.Clear() : _managementFolderReader = delayed
                    OpenLocalModFolder(LocalModOpen, Nothing)
                    _managementFolderReader = realReader : OpenLocalModFolder(LocalModOpen, Nothing) : Await waitOpen()
                    If dark Then gate.SetException(New IOException("retired local folder fault")) Else gate.SetResult(instanceOwner)
                    Await WaitPackUiAsync(Function() returned) : Await Task.Yield() : layout()
                    check(retiredToken.IsCancellationRequested AndAlso actions.SequenceEqual({Path.Combine(game, "mods")}) AndAlso LocalModStatus.Text = "" AndAlso _localMods Is retained,
                        "a newer local folder click retires old success and fault without disturbing the current list" & label)

                    gate = New TaskCompletionSource(Of InstalledInstance)(TaskCreationOptions.RunContinuationsAsynchronously) : returned = False
                    actions.Clear() : _managementFolderReader = delayed
                    OpenLocalModFolder(LocalModOpen, Nothing) : DirectCast(requestField.GetValue(Me), CancellationTokenSource).Cancel() : gate.SetResult(instanceOwner)
                    Await waitOpen()
                    Dim timedOut = actions.Count = 0 AndAlso LocalModStatus.Text.Contains("超时")
                    _managementFolderReader = realReader : OpenLocalModFolder(LocalModOpen, Nothing) : Await waitOpen()
                    check(timedOut AndAlso actions.SequenceEqual({Path.Combine(game, "mods")}) AndAlso LocalModStatus.Text = "",
                        "an owned local folder timeout reports feedback and permits a clean retry" & label)

                    gate = New TaskCompletionSource(Of InstalledInstance)(TaskCreationOptions.RunContinuationsAsynchronously) : returned = False
                    actions.Clear() : _managementFolderReader = delayed
                    OpenLocalModFolder(LocalModOpen, Nothing) : BackFromLocalMods() : settle() : BackFromManagement(Nothing, Nothing) : settle()
                    SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = "我的原版")) : settle()
                    If dark Then gate.SetException(New IOException("old instance folder fault")) Else gate.SetResult(instanceOwner)
                    Await WaitPackUiAsync(Function() returned) : Await Task.Yield() : layout()
                    check(retiredToken.IsCancellationRequested AndAlso actions.Count = 0 AndAlso _managedInstance.Name = "我的原版" AndAlso ManagementDetailPage.IsVisible,
                        "return and quick instance switching discard the previous local folder completion" & label)
                    BackFromManagement(Nothing, Nothing) : settle() : SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = native.Name)) : settle()
                    _managementFolderReader = realReader : ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()

                    instanceOwner = _localModInstance : actions.Clear()
                    Dim otherTarget As New LauncherInstallTarget(folders((launcher + 1) Mod 3), _pclBase)
                    _managementFolderReader = Function(instance, token) Task.FromResult(InstalledInstanceCatalog.Read(otherTarget, instance.Name, token))
                    OpenLocalModFolder(LocalModOpen, Nothing) : Await waitOpen()
                    check(actions.Count = 0 AndAlso LocalModStatus.Text.Contains("实例信息已改变") AndAlso _localModInstance Is instanceOwner,
                        "a different launcher binding over the same game root cannot supply a local folder result" & label)
                    _managementFolderReader = realReader
                Else
                    For Each scenario In {"a newer local folder click retires old success and fault without disturbing the current list", "an owned local folder timeout reports feedback and permits a clean retry", "return and quick instance switching discard the previous local folder completion", "a different launcher binding over the same game root cannot supply a local folder result"}
                        check(False, scenario & label)
                    Next
                End If

                actions.Clear()
                Dim scanGate As New TaskCompletionSource(Of List(Of LocalModEntry))(TaskCreationOptions.RunContinuationsAsynchronously)
                _localModProvider = Function(instance, token) scanGate.Task
                Dim refreshing = LoadLocalModsAsync(True), disabled = Not LocalModOpen.IsEnabled
                OpenLocalModFolder(LocalModOpen, Nothing)
                Dim ignored = actions.Count = 0 AndAlso (requestField Is Nothing OrElse requestField.GetValue(Me) Is Nothing)
                scanGate.SetResult(LocalModCatalog.Scan(_managedInstance, CancellationToken.None)) : Await refreshing : settle()
                check(disabled AndAlso ignored AndAlso LocalModOpen.IsEnabled AndAlso _localModReadSucceeded,
                    "local folder actions respect a running list scan and restore their input gate afterward" & label)
                check(protectedFiles.All(Function(pair) File.Exists(pair.Key) AndAlso File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)) AndAlso
                    IO.Directory.GetFiles(Path.GetDirectoryName(game), "*", SearchOption.AllDirectories).Length = protectedFiles.Count AndAlso _taskGeneration = taskGeneration,
                    "local folder reads errors returns and retries preserve launcher config bytes files and global download ownership" & label)
                _motionPreview = False : ShowDownload(Nothing, Nothing) : settle()
            Next
        Next
    End Function
End Class
