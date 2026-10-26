Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyTaskManagementRefreshUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim host As New Border With {.Width = 900, .Height = 650}, layout = ControlLayout(host), observations As New JObject
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 650)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckTaskManagementRefreshAsync(directory, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 60000) Then Throw New TimeoutException("Task management refresh did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "task-management-refresh.json"), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            owner._closing = True : owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner._managementMotion.Finish()
            host.Child = Nothing : owner.Content = original : SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub

    Private Async Function CheckTaskManagementRefreshAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : _managementMotion.Finish() : layout()
                               End Sub
        For launcher = 0 To 2
            For Each dark In {False, True}
                _motionPreview = False
                Dim folders = PrepareLocalModFixture(directory), game = folders(0).Location
                LocalModJarForChecks(Path.Combine(game, "mods/shared.jar"), "task_shared")
                FolderList.SelectedItem = folders(launcher)
                Dim target = CurrentInstallTarget(), native = InstalledInstanceCatalog.Read(target, "Fabric 实例", CancellationToken.None)
                SetLocalModIsolationForChecks(native, Not dark)
                Dim protectedFiles = IO.Directory.GetFiles(Path.GetDirectoryName(game), "*", SearchOption.AllDirectories).ToDictionary(Function(path) path, Function(path) File.ReadAllBytes(path))
                Dim realProvider As Func(Of LauncherInstallTarget, CancellationToken, Task(Of List(Of InstalledInstance))) =
                    Function(binding, token) Task.FromResult(InstalledInstanceCatalog.Scan(binding, token))
                _managementProvider = realProvider
                ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(True) : settle()
                SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = native.Name)) : settle()
                ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                Dim instance = _managedInstance, beforeCount = _localMods.Count
                Dim source = LocalModJarForChecks(Path.Combine(directory, "task-resource-" & Guid.NewGuid().ToString("N"), "completion.jar"), "task_completion")
                Dim bytes = File.ReadAllBytes(source), digest As String
                Using sha = Security.Cryptography.SHA1.Create()
                    digest = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
                End Using
                Dim release As New ModpackRelease With {.Kind = CommunityResourceKind.Mod, .Platform = ModpackPlatform.Modrinth,
                    .Name = "Task completion", .FileName = "completion.jar", .GameVersions = {instance.MinecraftVersion}, .Loaders = {"fabric"}, .Size = bytes.Length, .Sha1 = digest}
                Dim plan = CommunityResourceInstallation.Create(release, instance, Nothing, CancellationToken.None)
                Dim gate As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously), taskToken As CancellationToken, reused = False
                Dim execute As Func(Of SourcePolicy, IProgress(Of TransferProgress), CancellationToken, Task) =
                    Async Function(policy, progress, token)
                        taskToken = token : Await gate.Task : token.ThrowIfCancellationRequested()
                        reused = Not Await plan.InstallAsync(Path.Combine(directory, "task-resource-cache"), policy, progress, token)
                    End Function
                _resourceInstallRunner = Function(frozen, cache, policy, progress, token) execute(policy, progress, token)
                _resourceSaveRunner = Function(item, destination, progress, token) execute(Policy(), progress, token)
                Dim begin As Func(Of Task) = Function() RunResourceSaveAsync(release, plan.Destination, If(dark, Nothing, plan))
                _motionPreview = True : RestoreAppearance(New JObject From {{"darkMode", dark}})
                _surfaceMotion.PauseForChecks = True : _managementMotion.PauseForChecks = True
                Dim saving = begin()
                ShowManagement(Nothing, Nothing) : layout()
                Dim gated = _surfaceMotion.Transition IsNot Nothing AndAlso Not PageSurface.IsHitTestVisible
                File.Copy(source, plan.Destination)
                Dim scans = 0
                _managementProvider = Function(binding, token)
                                          scans += 1 : Return realProvider(binding, token)
                                      End Function
                Dim beforeManual = _managementGeneration
                RefreshManagement(Nothing, New RoutedEventArgs())
                Dim manualBlocked = scans = 0 AndAlso beforeManual = _managementGeneration
                gate.SetResult(True) : Await saving
                Await WaitPackUiAsync(Function() Not _managementScanning AndAlso Not _localModScanning) : layout()
                Dim label = "/launcher=" & target.Kind.ToString() & "/save-as-shared-dark=" & dark
                check(gated AndAlso manualBlocked AndAlso scans = 1 AndAlso Not _managementDirty AndAlso _localModReadSucceeded AndAlso _taskOutcome = TaskOutcome.Succeeded AndAlso reused,
                    "task completion refreshes management during a real paused root entry instead of losing the request at its input gate" & label)
                settle()
                check(PageSurface.IsHitTestVisible AndAlso ManagementViewHost.IsHitTestVisible AndAlso LocalModsPage.IsVisible AndAlso
                    _localMods.Count = beforeCount + 1 AndAlso _localMods.Any(Function(item) item.FileName = "completion.jar") AndAlso LocalModImport.IsEnabled AndAlso
                    SameManagementTarget(_managedInstance.Target, target) AndAlso File.ReadAllBytes(plan.Destination).SequenceEqual(bytes),
                    "revealing the completed management entry shows the newly checked file and usable actions without changing its native binding" & label)

                ' Recover the unmodified baseline before each later observation.
                _managementProvider = realProvider : Await ScanManagementAsync(True) : Await LoadLocalModsAsync(True) : settle()
                gate = New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
                saving = begin() : ShowManagement(Nothing, Nothing) : layout()
                scans = 0 : _managementProvider = Function(binding, token)
                                                    scans += 1 : Return realProvider(binding, token)
                                                End Function
                CancelInstall(Nothing, Nothing) : gate.SetCanceled() : Await saving
                Await WaitPackUiAsync(Function() Not _managementScanning AndAlso Not _localModScanning) : settle()
                check(taskToken.IsCancellationRequested AndAlso scans = 1 AndAlso _taskOutcome = TaskOutcome.Cancelled AndAlso Not _busy AndAlso
                    _localModReadSucceeded AndAlso LocalModImport.IsEnabled AndAlso Not _managementDirty,
                    "cancellation during management entry settles its frozen task and restores the current readable Mods page" & label)

                _managementProvider = realProvider : Await ScanManagementAsync(True) : Await LoadLocalModsAsync(True) : settle()
                Dim oldGeneration = _taskGeneration
                gate = New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
                saving = begin() : ShowManagement(Nothing, Nothing) : layout()
                scans = 0 : _managementProvider = Function(binding, token)
                                                    scans += 1 : Return realProvider(binding, token)
                                                End Function
                gate.SetResult(True) : Await saving
                Await WaitPackUiAsync(Function() Not _managementScanning AndAlso Not _localModScanning) : settle()
                check(_taskGeneration > oldGeneration AndAlso _taskOutcome = TaskOutcome.Succeeded AndAlso scans = 1 AndAlso reused AndAlso
                    _localModReadSucceeded AndAlso LocalModImport.IsEnabled AndAlso Not _managementDirty,
                    "an immediate retry owns a new task and refreshes the same current instance after checked existing-file reuse" & label)

                _managementProvider = realProvider : Await ScanManagementAsync(True) : Await LoadLocalModsAsync(True) : settle()
                gate = New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
                saving = begin() : ShowManagement(Nothing, Nothing) : layout()
                _managementProvider = Function(binding, token) Task.FromException(Of List(Of InstalledInstance))(New IOException("controlled task-completion scan failure"))
                gate.SetResult(True) : Await saving
                Await WaitPackUiAsync(Function() Not _managementScanning) : settle()
                Dim failedRefresh = _managementProblem IsNot Nothing AndAlso _managementProblem.Contains("controlled task-completion scan failure") AndAlso _managementDirty
                _managementProvider = realProvider : RefreshManagement(Nothing, New RoutedEventArgs())
                Await WaitPackUiAsync(Function() Not _managementScanning AndAlso Not _localModScanning) : settle()
                check(failedRefresh AndAlso Not _managementDirty AndAlso _taskOutcome = TaskOutcome.Succeeded AndAlso _localModReadSucceeded AndAlso LocalModImport.IsEnabled,
                    "a failed completion refresh preserves task success and remains repairable through the ordinary refresh button" & label)

                gate = New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
                saving = begin() : ShowManagement(Nothing, Nothing) : layout()
                Dim oldItems = InstalledInstanceCatalog.Scan(target, CancellationToken.None)
                Dim delayed As New TaskCompletionSource(Of List(Of InstalledInstance))(TaskCreationOptions.RunContinuationsAsynchronously), called = False, returned = False, oldToken As CancellationToken
                _managementProvider = Async Function(binding, token)
                                          called = True : oldToken = token
                                          Dim result = Await delayed.Task
                                          returned = True : Return result
                                      End Function
                gate.SetResult(True) : Await saving : settle()
                Dim started = called AndAlso _managementScanning
                NavigateTitleBack() : settle() : BackFromManagement(Nothing, Nothing) : settle()
                ShowSettings(Nothing, Nothing) : settle()
                _managementProvider = realProvider
                FolderList.SelectedItem = folders((launcher + 1) Mod 3)
                ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(False) : settle()
                SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = "我的原版")) : settle()
                ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                Dim accepted = _managementInstances, acceptedMod = _localMods, selected = _managedInstance, currentTarget = CurrentInstallTarget()
                delayed.SetResult(oldItems)
                If called Then Await WaitPackUiAsync(Function() returned)
                Await Task.Yield() : settle()
                check(started AndAlso oldToken.IsCancellationRequested AndAlso _managementInstances Is accepted AndAlso _localMods Is acceptedMod AndAlso
                    _managedInstance Is selected AndAlso _managedInstance.Name = "我的原版" AndAlso SameManagementTarget(_managementTarget, currentTarget) AndAlso
                    LocalModsPage.IsVisible AndAlso _localModReadSucceeded AndAlso LocalModImport.IsEnabled,
                    "return, settings and another launcher binding retire a delayed completion refresh even when its provider ignores cancellation" & label)
                check(protectedFiles.All(Function(pair) File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)) AndAlso
                    File.ReadAllBytes(plan.Destination).SequenceEqual(bytes) AndAlso File.ReadAllBytes(source).SequenceEqual(bytes),
                    "completion, cancellation, reuse and late refreshes preserve original archives and every external configuration byte" & label)
            Next
        Next
    End Function
End Class
