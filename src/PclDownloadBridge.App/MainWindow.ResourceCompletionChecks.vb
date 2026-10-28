Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyResourceCompletionUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim host As New Border With {.Width = 900, .Height = 700}, observations As New JObject, faults As New List(Of Exception)
        Dim capture As DispatcherUnhandledExceptionEventHandler = Sub(sender, args)
                                                                     faults.Add(args.Exception) : args.Handled = True
                                                                 End Sub
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                     File.WriteAllText(Path.Combine(directory, "resource-completion.json"), observations.ToString())
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        AddHandler owner.Dispatcher.UnhandledException, capture
        Try
            Using surface = PreviewSurface.Attach(host, 900, 700)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckResourceCompletionAsync(directory, ControlLayout(host), check, faults, host)
                If Not PumpMotionUntil(Function() work.IsCompleted, 180000) Then Throw New TimeoutException("Resource completion checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            Dim failed = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failed.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failed))
        Finally
            RemoveHandler owner.Dispatcher.UnhandledException, capture
            owner._closing = True : owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._managementMotion.Finish() : owner._categoryMotion.Finish()
            host.Child = Nothing : owner.Content = original : SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub

    Private Async Function CheckResourceCompletionAsync(directory As String, layout As Action, check As Action(Of Boolean, String), faults As List(Of Exception), host As Border) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _managementMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion() : _titleNavigation.Finish() : layout()
                               End Sub
        For launcher = 0 To 2
            For outcome = 0 To 2
                For scenario = 0 To 3
                    _motionPreview = False
                    Dim folders = PrepareLocalModFixture(Path.Combine(directory, "completion-" & launcher & "-" & outcome & "-" & scenario))
                    FolderList.SelectedItem = folders(launcher)
                    Dim target = CurrentInstallTarget(), native = InstalledInstanceCatalog.Read(target, "Fabric 实例", CancellationToken.None)
                    SetLocalModIsolationForChecks(native, scenario Mod 2 = 0)
                    ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(True) : settle()
                    SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = native.Name)) : settle()
                    ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                    Dim instance = _managedInstance, files = DirectoryFilesForModUpdateChecks(Path.GetDirectoryName(target.Root)), realMods = _localModProvider
                    Dim source = LocalModJarForChecks(Path.Combine(directory, "completion-source-" & Guid.NewGuid().ToString("N"), "completed.jar"), "completion_fixture")
                    Dim bytes = File.ReadAllBytes(source), release As New ModpackRelease With {.Kind = CommunityResourceKind.Mod, .Platform = ModpackPlatform.Modrinth,
                        .Name = "Completion fixture", .FileName = "completed.jar", .GameVersions = {instance.MinecraftVersion}, .Loaders = {"fabric"}, .Size = bytes.Length}
                    Using digest = Security.Cryptography.SHA1.Create()
                        release.Sha1 = BitConverter.ToString(digest.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
                    End Using
                    Dim plan = CommunityResourceInstallation.Create(release, instance, Nothing, CancellationToken.None)
                    Dim gate As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
                    _resourceInstallRunner = Async Function(frozen, cache, policy, progress, token)
                                                 Await gate.Task : token.ThrowIfCancellationRequested()
                                                 File.Copy(source, frozen.Destination)
                                             End Function
                    Dim taskGeneration = _taskGeneration, work = RunResourceSaveAsync(release, plan.Destination, plan)
                    ShowManagement(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning AndAlso Not _managementScanning) : settle()
                    Dim oldRows = _localMods, oldGate = ListGate(Of LocalModEntry)(), newGate = ListGate(Of LocalModEntry)()
                    _localModProvider = Function(current, token) oldGate.Task
                    Dim oldRead = LoadLocalModsAsync(True), retired = _localModRequest, retiredToken = retired.Token
                    Dim replacement As Task = Task.CompletedTask, replacementRegistration As CancellationTokenRegistration, notifications = 0, escaped = False, chosen = scenario
                    RestoreAppearance(New JObject From {{"darkMode", (launcher + outcome + scenario) Mod 2 = 0}})
                    _motionPreview = scenario Mod 2 = 0 : _surfaceMotion.PauseForChecks = _motionPreview : _managementMotion.PauseForChecks = _motionPreview
                    Dim beforeFaults = faults.Count, expected = If(outcome = 0, TaskOutcome.Succeeded, If(outcome = 1, TaskOutcome.Failed, TaskOutcome.Cancelled))
                    Dim label = "/launcher=" & target.Kind.ToString() & "/outcome=" & outcome & "/case=" & scenario
                    Using registration = retired.Token.Register(Sub()
                                                                   notifications += 1
                                                                   _localModProvider = realMods
                                                                   If chosen = 3 Then Return
                                                                   If chosen = 1 Then
                                                                       _localModProvider = Function(current, token) newGate.Task
                                                                       replacement = LoadLocalModsAsync(True)
                                                                       replacementRegistration = _localModRequest.Token.Register(Sub()
                                                                                                                                    Throw New IOException("Controlled replacement observer failure")
                                                                                                                                End Sub)
                                                                       _localModProvider = realMods
                                                                   ElseIf chosen = 2 Then
                                                                       ShowSettings(Nothing, Nothing)
                                                                   End If
                                                                   Throw New IOException("Controlled completion observer failure")
                                                               End Sub)
                        If outcome = 0 Then
                            gate.TrySetResult(True)
                        ElseIf outcome = 1 Then
                            gate.TrySetException(New IOException("Controlled resource writer failure"))
                        Else
                            CancelInstall(Nothing, Nothing) : gate.TrySetCanceled()
                        End If
                        Try
                            Await work
                        Catch
                            escaped = True
                        End Try
                        check(Not escaped AndAlso faults.Count = beforeFaults AndAlso _taskFinished AndAlso _taskOutcome = expected AndAlso
                            Not _busy AndAlso _cancellation Is Nothing AndAlso _taskGeneration = taskGeneration + 1 AndAlso notifications = 1,
                            "resource completion retains the writer outcome and releases task ownership despite observer failures" & label)
                    End Using
                    replacementRegistration.Dispose()
                    _localModProvider = realMods : oldGate.TrySetResult(oldRows) : newGate.TrySetResult(oldRows)
                    Await oldRead : Await replacement : Await Task.Yield() : settle()
                    check(retiredToken.IsCancellationRequested AndAlso faults.Count = beforeFaults AndAlso _taskOutcome = expected AndAlso
                        If(scenario = 2, SettingsPage.IsVisible, True) AndAlso
                        If(outcome = 0, File.ReadAllBytes(plan.Destination).SequenceEqual(bytes) AndAlso _taskResourceFile = plan.Destination AndAlso OpenTaskFolderButton.IsEnabled,
                            Not File.Exists(plan.Destination) AndAlso _taskResourceFile Is Nothing AndAlso Not OpenTaskFolderButton.IsEnabled) AndAlso
                        files.All(Function(item) File.Exists(item.Key) AndAlso File.ReadAllBytes(item.Key).SequenceEqual(item.Value)),
                        "late retired reads cannot reclassify a published file or steal accepted navigation" & label)
                    If launcher = 0 AndAlso outcome = 0 AndAlso scenario = 0 Then
                        ShowProgress(Nothing, Nothing) : settle() : SaveControlVisual(host, Path.Combine(directory, "completion-result.png"))
                    End If
                    ' Clear a broken baseline's leaked gate only after recording it,
                    ' so every matrix case still reaches the explicit retry check.
                    Dim leaked = _cancellation : _cancellation = Nothing : _busy = False : leaked?.Dispose()
                    _motionPreview = False : _surfaceMotion.PauseForChecks = False : _managementMotion.PauseForChecks = False : SetBusy(False) : settle()
                    _resourceInstallRunner = Function(frozen, cache, policy, progress, token)
                                                 If Not File.Exists(frozen.Destination) Then File.Copy(source, frozen.Destination)
                                                 Return Task.CompletedTask
                                             End Function
                    Await RunResourceSaveAsync(release, plan.Destination, plan) : settle()
                    ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(True) : settle()
                    If Not LocalModsPage.IsVisible Then
                        SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = native.Name)) : settle()
                        ShowLocalMods(Nothing, Nothing)
                    Else
                        Await LoadLocalModsAsync(True)
                    End If
                    Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                    check(_taskOutcome = TaskOutcome.Succeeded AndAlso Not _busy AndAlso _cancellation Is Nothing AndAlso _taskGeneration = taskGeneration + 2 AndAlso
                        _localModReadSucceeded AndAlso LocalModImport.IsEnabled AndAlso PageSurface.IsHitTestVisible AndAlso ManagementViewHost.IsHitTestVisible AndAlso
                        File.ReadAllBytes(plan.Destination).SequenceEqual(bytes) AndAlso files.All(Function(item) File.ReadAllBytes(item.Key).SequenceEqual(item.Value)),
                        "explicit resource retry restores native Mod input without modifying existing files or launcher isolation" & label)
                Next
            Next
        Next
    End Function
End Class
