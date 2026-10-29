Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyLauncherSelectionRecoveryUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim host As New Border With {.Width = 850, .Height = 650}, layout = ControlLayout(host), observations As New JObject
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 850, 650)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                layout()
                Dim task = owner.CheckLauncherSelectionRecoveryAsync(directory, layout, check)
                If Not PumpMotionUntil(Function() task.IsCompleted, 30000) Then Throw New TimeoutException("Launcher selection recovery did not settle.")
                task.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "launcher-recovery.json"), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            owner._restoringSettings = False : owner._closing = True : owner._launcherReadGeneration += 1
            owner.CancelManagementRequest() : owner.CancelResourceRequests() : owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish()
            host.Child = Nothing : owner.Content = original : SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub
    Private Async Function CheckLauncherSelectionRecoveryAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim fixture = Path.Combine(directory, "launcher-recovery-" & Guid.NewGuid().ToString("N")), game = Path.Combine(fixture, "shared"), other = Path.Combine(fixture, "manual")
        IO.Directory.CreateDirectory(game) : IO.Directory.CreateDirectory(other)
        Dim state As New LauncherBindingsState, originals As New Dictionary(Of String, Byte())
        For Each kind In {ExternalLauncherKind.Pcl, ExternalLauncherKind.Hmcl, ExternalLauncherKind.PclCe}
            Dim work = Path.Combine(fixture, kind.ToString()) : IO.Directory.CreateDirectory(Path.Combine(work, "PCL"))
            Dim config = Path.Combine(work, If(kind = ExternalLauncherKind.Hmcl, "hmcl.json", If(kind = ExternalLauncherKind.PclCe, "PCL/config.v1.yml", "PCL/Setup.ini")))
            If kind = ExternalLauncherKind.Hmcl Then
                File.WriteAllText(config, New JObject From {{"last", "shared"}, {"configurations", New JObject From {{"shared", New JObject From {{"gameDir", game}}}}}}.ToString())
            Else
                File.WriteAllText(config, "LaunchFolderSelect: " & If(kind = ExternalLauncherKind.PclCe, "'" & game & "'", game))
            End If
            originals(config) = File.ReadAllBytes(config)
            Dim binding As New ExternalLauncherBinding With {.Id = kind.ToString(), .Kind = kind, .Workspace = work, .ConfigPath = config}
            state.Bindings.Add(binding) : state.SelectedRows(kind) = binding.Id
        Next
        state.TargetBindingId = ExternalLauncherKind.Hmcl.ToString() : state.TargetRoot = game
        Dim settings As New JObject From {{"pclBase", state.Bindings(0).Workspace}, {"gameRoot", game}, {"externalLaunchers", state.ToJson()}}
        Dim result As Func(Of ExternalLauncherBinding, LauncherDirectoryResult) =
            Function(binding)
                Dim value As New LauncherDirectoryResult With {.Succeeded = True}
                value.Folders.Add(New GameFolder With {.Location = game, .Name = binding.DisplayName, .LauncherKind = binding.Kind, .LauncherBindingId = binding.Id, .LauncherWorkspace = binding.Workspace, .LauncherConfigPath = binding.ConfigPath, .IsSelected = True})
                Return value
            End Function
        Dim gate As New TaskCompletionSource(Of LauncherDirectoryResult)(TaskCreationOptions.RunContinuationsAsynchronously), reads = 0
        _launcherDirectoryLoader = Function(binding)
                                       reads += 1
                                       Return If(reads = 1, gate.Task, Task.FromResult(result(binding)))
                                   End Function
        _selectedFolder = Nothing : _folders.Clear() : FolderList.ItemsSource = Nothing
        _restoringSettings = True
        Dim restore = RestoreLauncherBindingsAsync(settings)
        ShowSettings(Nothing, Nothing) : _surfaceMotion.Finish() : layout()
        SelectManualFolder(other)
        check(_selectedFolder Is Nothing AndAlso _folders.Count = 0, "startup restore rejects a manual directory edit before the saved target is available")
        ShowDownload(Nothing, Nothing) : _surfaceMotion.Finish() : layout()
        gate.SetResult(result(state.Bindings(0))) : Await restore
        _restoringSettings = False : layout()
        check(reads = 3 AndAlso _folders.Count = 3, "leaving settings during startup cannot abort the remaining launcher discovery")
        check(_selectedFolder IsNot Nothing AndAlso _selectedFolder.LauncherBindingId = state.TargetBindingId AndAlso SameGameRoot(_selectedFolder.Location, game),
            "startup navigation preserves the saved HMCL target instead of an empty or provisional directory")

        _selectedFolder = Nothing : _folders.Clear() : FolderList.ItemsSource = Nothing
        _launcherDirectoryLoader = Function(binding) Task.FromResult(result(binding))
        Await RestoreLauncherBindingsAsync(settings) : layout()
        Dim native = _selectedFolder, frozen = CurrentInstallTarget()
        SelectManualFolder(game & Path.DirectorySeparatorChar) : layout()
        check(_selectedFolder Is native AndAlso CurrentInstallTarget().Kind = ExternalLauncherKind.Hmcl AndAlso CurrentInstallTarget().BindingId = frozen.BindingId,
            "manually reselecting the active shared path retains its exact launcher identity")
        SelectManualFolder(other) : SelectManualFolder(game) : layout()
        Dim manual = _selectedFolder
        check(manual IsNot Nothing AndAlso String.IsNullOrEmpty(manual.LauncherBindingId) AndAlso Not manual.LauncherKind.HasValue AndAlso SameGameRoot(manual.Location, game),
            "a manual path chosen from another root never guesses among three launcher bindings")
        SelectManualFolder(game) : layout()
        check(_selectedFolder Is manual AndAlso _folders.Where(Function(item) String.IsNullOrEmpty(item.LauncherBindingId) AndAlso SameGameRoot(item.Location, game)).Count() = 1,
            "reselecting a manual path reuses its row without duplicate or native-binding conversion")
        Dim saved = BuildSettings(0), restored = LauncherBindingsState.Restore(saved, _pclBase)
        check(restored.Bindings.Count = 3 AndAlso String.IsNullOrEmpty(restored.TargetBindingId) AndAlso SameGameRoot(restored.TargetRoot, game),
            "manual target persistence retains all native bindings without assigning one to the manual path")

        Dim delayed As New TaskCompletionSource(Of LauncherDirectoryResult)(TaskCreationOptions.RunContinuationsAsynchronously)
        _launcherDirectoryLoader = Function(binding) delayed.Task
        Dim old = RestoreLauncherBindingsAsync(settings)
        _launcherDirectoryLoader = Function(binding) Task.FromResult(result(binding))
        Dim replacement = DirectCast(settings.DeepClone(), JObject)
        replacement("externalLaunchers")("targetBindingId") = ExternalLauncherKind.PclCe.ToString()
        Await RestoreLauncherBindingsAsync(replacement)
        Dim accepted = _selectedFolder
        delayed.SetResult(result(state.Bindings(0))) : Await old : layout()
        check(_selectedFolder Is accepted AndAlso accepted.LauncherKind.GetValueOrDefault() = ExternalLauncherKind.PclCe AndAlso _folders.Where(Function(item) Not String.IsNullOrEmpty(item.LauncherBindingId)).Count() = 3,
            "a superseded restore cannot replace a newer same-root launcher target")
        _busy = True : SelectManualFolder(other) : _busy = False
        check(_selectedFolder Is accepted, "manual directory selection cannot redirect an active installation target")
        ShowSettings(Nothing, Nothing) : _surfaceMotion.Finish() : layout()
        Dim selectedBinding = _launcherState.Bindings.Single(Function(item) item.Kind = ExternalLauncherKind.PclCe), prior = _launcherFolders(selectedBinding.Id), manualReads = 0
        delayed = New TaskCompletionSource(Of LauncherDirectoryResult)(TaskCreationOptions.RunContinuationsAsynchronously)
        _launcherDirectoryLoader = Function(item)
                                       manualReads += 1 : Return delayed.Task
                                   End Function
        Dim manualRead = ReadLauncherBindingAsync(selectedBinding)
        ShowDownload(Nothing, Nothing) : _surfaceMotion.Finish() : layout()
        Dim obsolete = result(selectedBinding) : obsolete.Folders(0).Location = other
        delayed.SetResult(obsolete) : Await manualRead : layout()
        check(manualReads = 1 AndAlso _launcherFolders(selectedBinding.Id) Is prior AndAlso _selectedFolder Is accepted,
            "manual launcher discovery still retires when the settings page is left")
        _launcherDirectoryLoader = Function(item) Task.FromResult(result(item))
        _selectedFolder = Nothing : _folders.Clear() : FolderList.ItemsSource = Nothing
        _restoringSettings = True
        Dim corruptTarget = DirectCast(settings.DeepClone(), JObject)
        corruptTarget("externalLaunchers")("targetRoot") = New JArray()
        Await RestoreLauncherBindingsAsync(corruptTarget) : layout()
        check(_launcherState.Bindings.Count = 3 AndAlso _folders.Count = 3 AndAlso _selectedFolder Is Nothing AndAlso FolderList.SelectedItem Is Nothing,
            "corrupt saved target metadata preserves launcher discovery without automatically choosing another launcher")
        Dim targetBlocked = False
        Try
            CurrentInstallTarget()
        Catch ex As ArgumentException
            targetBlocked = True
        End Try
        check(targetBlocked AndAlso SelectedPath.Text = "请选择 .minecraft 游戏目录",
            "a rejected saved target cannot become a provisional installation destination")
        Await RestoreLauncherBindingsAsync(settings) : layout()
        check(_selectedFolder IsNot Nothing AndAlso _selectedFolder.LauncherKind.GetValueOrDefault() = ExternalLauncherKind.Hmcl AndAlso
            _launcherState.Warnings.Count = 0 AndAlso SameGameRoot(CurrentInstallTarget().Root, game),
            "corrected target metadata restores the saved native identity and clears recovery warnings")
        delayed = New TaskCompletionSource(Of LauncherDirectoryResult)(TaskCreationOptions.RunContinuationsAsynchronously)
        _launcherDirectoryLoader = Function(item) delayed.Task
        Dim obsoleteRecovery = RestoreLauncherBindingsAsync(corruptTarget)
        _launcherDirectoryLoader = Function(item) Task.FromResult(result(item))
        Await RestoreLauncherBindingsAsync(replacement) : layout()
        accepted = _selectedFolder
        delayed.SetResult(result(state.Bindings(0))) : Await obsoleteRecovery : layout()
        check(_selectedFolder Is accepted AndAlso accepted.LauncherKind.GetValueOrDefault() = ExternalLauncherKind.PclCe AndAlso _launcherState.Warnings.Count = 0,
            "a late corrupt-target recovery cannot replace a newer corrected restore or replay its warning")
        _restoringSettings = False
        check(originals.All(Function(item) File.ReadAllBytes(item.Key).SequenceEqual(item.Value)), "startup and manual directory selection keep all launcher configurations byte-identical")
    End Function
End Class
