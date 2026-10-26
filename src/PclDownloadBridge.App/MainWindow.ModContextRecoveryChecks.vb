Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyModContextRecoveryUi(directory As String, passed As List(Of String))
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
                Dim work = owner.CheckModContextRecoveryAsync(directory, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 45000) Then Throw New TimeoutException("Mod context recovery did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "mod-context-recovery.json"), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            owner._closing = True : owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner._managementMotion.Finish()
            host.Child = Nothing : owner.Content = original : SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub

    Private Async Function CheckModContextRecoveryAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion() : _managementMotion.Finish() : layout()
                               End Sub
        For launcher = 0 To 2
            For Each animated In {False, True}
                For Each dark In {False, True}
                    _motionPreview = False
                    Dim folders = PrepareLocalModFixture(directory), game = folders(0).Location
                    LocalModJarForChecks(Path.Combine(game, "mods/shared.jar"), "shared_context_fixture")
                    FolderList.SelectedItem = folders(launcher)
                    Dim target = CurrentInstallTarget(), name = "Fabric 实例", initial = InstalledInstanceCatalog.Read(target, name, CancellationToken.None)
                    Dim config = SetLocalModIsolationForChecks(initial, True)
                    Dim protectedFiles = IO.Directory.GetFiles(Path.GetDirectoryName(game), "*", SearchOption.AllDirectories).ToDictionary(Function(path) path, Function(path) File.ReadAllBytes(path))
                    Dim realProvider As Func(Of InstalledInstance, CancellationToken, Task(Of List(Of LocalModEntry))) = Function(instance, token) Task.FromResult(LocalModCatalog.Scan(instance, token))
                    _localModProvider = realProvider
                    _localModInstanceReader = Function(instance, token) Task.FromResult(InstalledInstanceCatalog.Read(instance.Target, instance.Name, token))
                    Dim openHealthy As Func(Of Task) = Async Function()
                                                           If LocalModsPage.Visibility = Visibility.Visible Then BackFromLocalMods()
                                                           settle()
                                                           If ManagementDetailPage.Visibility = Visibility.Visible Then BackFromManagement(Nothing, Nothing)
                                                           ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(True) : settle()
                                                           SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = name)) : settle()
                                                           ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                                                       End Function
                    Await openHealthy()
                    _motionPreview = animated : RestoreAppearance(New JObject From {{"darkMode", dark}})
                    Dim originalEntries = _localMods, retiredEntry = _localMods.Single(Function(item) item.FileName = "first.jar"), writeCalls = 0
                    _localModToggle = Function(instance, entry, enabled, token)
                                          writeCalls += 1 : Return Task.FromResult(New LocalModActionResult("", 0, 0))
                                      End Function
                    Dim label = "/launcher=" & target.Kind.ToString() & "/motion=" & animated & "/dark=" & dark
                    CorruptModContextForChecks(config, target.Kind) : protectedFiles(config) = File.ReadAllBytes(config)
                    RefreshManagement(Nothing, New RoutedEventArgs())
                    Await WaitPackUiAsync(Function() Not _managementScanning) : settle()
                    check(_managedInstance.Problem IsNot Nothing AndAlso LocalModsPage.IsVisible AndAlso Not _localModReadSucceeded AndAlso
                        Not LocalModRows.IsEnabled AndAlso Not LocalModImport.IsEnabled AndAlso Not LocalModOpen.IsEnabled AndAlso LocalModRefresh.IsEnabled AndAlso
                        Not LocalModSearch.IsEnabled AndAlso Not LocalModAll.IsEnabled AndAlso Not LocalModEnabled.IsEnabled AndAlso Not LocalModDisabled.IsEnabled AndAlso
                        LocalModStatus.Text.Contains(_managedInstance.Problem) AndAlso _localMods Is originalEntries,
                        "an accepted broken native instance invalidates cached Mod writes and exposes a same-page refresh instead of leaving stale enabled rows" & label)
                    Await ToggleLocalModAsync(retiredEntry)
                    SetLocalModIsolationForChecks(_managedInstance, False) : protectedFiles(config) = File.ReadAllBytes(config)
                    RefreshLocalMods(Nothing, New RoutedEventArgs())
                    Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                    check(_managedInstance.Problem Is Nothing AndAlso _localModReadSucceeded AndAlso _localModInstance Is _managedInstance AndAlso
                        SameGameRoot(_managedInstance.RunDirectory, game) AndAlso _localMods.Count = 1 AndAlso LocalModSharedHint.IsVisible AndAlso
                        LocalModDirectory.Text = Path.Combine(game, "mods") AndAlso LocalModImport.IsEnabled AndAlso LocalModOpen.IsEnabled AndAlso LocalModSearch.IsEnabled AndAlso LocalModAll.IsEnabled,
                        "repairing native isolation allows explicit refresh to replace the broken snapshot and read the shared Mods directory on the same page" & label)

                    ' Reset through actual navigation so the race is independent
                    ' of the first recovery result on an unmodified baseline.
                    _localModProvider = realProvider : Await openHealthy()
                    Dim sharedEntries = _localMods
                    Dim oldGate As New TaskCompletionSource(Of List(Of LocalModEntry))(TaskCreationOptions.RunContinuationsAsynchronously), oldToken As CancellationToken
                    _localModProvider = Function(instance, token)
                                            oldToken = token : Return oldGate.Task
                                        End Function
                    Dim oldRead = LoadLocalModsAsync(True)
                    CorruptModContextForChecks(config, target.Kind) : protectedFiles(config) = File.ReadAllBytes(config)
                    RefreshManagement(Nothing, New RoutedEventArgs())
                    Await WaitPackUiAsync(Function() Not _managementScanning) : settle()
                    check(oldToken.IsCancellationRequested AndAlso Not _localModScanning AndAlso Not _localModReadSucceeded AndAlso
                        _managedInstance.Problem IsNot Nothing AndAlso LocalModRefresh.IsEnabled,
                        "root refresh retires an in-flight Mod read as soon as its native instance becomes unusable" & label)
                    SetLocalModIsolationForChecks(_managedInstance, True) : protectedFiles(config) = File.ReadAllBytes(config)
                    Dim newGate As New TaskCompletionSource(Of List(Of LocalModEntry))(TaskCreationOptions.RunContinuationsAsynchronously), newToken As CancellationToken, newCalls = 0
                    _localModProvider = Function(instance, token)
                                            newCalls += 1 : newToken = token : Return newGate.Task
                                        End Function
                    RefreshLocalMods(Nothing, New RoutedEventArgs())
                    Dim currentRequest = _localModRequest
                    check(newCalls = 1 AndAlso _localModScanning AndAlso Not _localModReadSucceeded AndAlso newToken <> oldToken AndAlso Not newToken.IsCancellationRequested,
                        "same-page retry rereads the repaired instance and owns a fresh read while the old provider still ignores cancellation" & label)
                    oldGate.SetResult(sharedEntries) : Await oldRead : settle()
                    check(_localModRequest Is currentRequest AndAlso _localModScanning AndAlso Not _localModReadSucceeded AndAlso _localMods Is sharedEntries AndAlso
                        Not LocalModImport.IsEnabled AndAlso Not LocalModRows.IsEnabled AndAlso newCalls = 1,
                        "a late old Mod result cannot clear the retry token, enable stale rows or replace the current loading state" & label)
                    Dim repaired = InstalledInstanceCatalog.Read(target, name, CancellationToken.None)
                    newGate.SetResult(LocalModCatalog.Scan(repaired, CancellationToken.None))
                    Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                    check(_localModReadSucceeded AndAlso _localModInstance Is _managedInstance AndAlso _managedInstance.Problem Is Nothing AndAlso
                        SameGameRoot(_managedInstance.RunDirectory, repaired.InstancePath) AndAlso _localMods.Count = 4 AndAlso
                        LocalModDirectory.Text = Path.Combine(repaired.InstancePath, "mods") AndAlso LocalModImport.IsEnabled AndAlso Not LocalModSharedHint.IsVisible,
                        "the accepted retry restores only the isolated directory and its current native context" & label)

                    _localModProvider = realProvider : Await openHealthy()
                    Dim leaveGate As New TaskCompletionSource(Of List(Of LocalModEntry))(TaskCreationOptions.RunContinuationsAsynchronously), leaveToken As CancellationToken
                    _localModProvider = Function(instance, token)
                                            leaveToken = token : Return leaveGate.Task
                                        End Function
                    Dim leaving = LoadLocalModsAsync(True)
                    Dim returned = NavigateTitleBack() : settle()
                    ShowSettings(Nothing, Nothing) : settle()
                    _localModProvider = realProvider
                    ShowManagement(Nothing, Nothing) : settle()
                    ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                    Dim acceptedEntries = _localMods, acceptedInstance = _localModInstance
                    leaveGate.SetResult(sharedEntries) : Await leaving : settle()
                    check(returned AndAlso leaveToken.IsCancellationRequested AndAlso _localModReadSucceeded AndAlso _localMods Is acceptedEntries AndAlso
                        _localModInstance Is acceptedInstance AndAlso LocalModsPage.IsVisible AndAlso LocalModImport.IsEnabled AndAlso Not _localModScanning,
                        "return, settings switch and reopen retire the previous read without replacing the reopened page or its controls" & label)
                    Await ToggleLocalModAsync(retiredEntry)
                    check(writeCalls = 0 AndAlso protectedFiles.All(Function(pair) File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),
                        "retired Mod actions never reach a writer and every archive and expected native configuration byte remains unchanged" & label)
                Next
            Next
        Next
    End Function

    Private Shared Sub CorruptModContextForChecks(path As String, kind As ExternalLauncherKind)
        Select Case kind
            Case ExternalLauncherKind.Pcl
                File.WriteAllText(path, "VersionArgumentIndieV2:invalid" & vbCrLf & "WindowWidth:900")
            Case ExternalLauncherKind.PclCe
                File.WriteAllText(path, "VersionArgumentIndieV2: invalid" & vbLf & "Unrelated: keep")
            Case ExternalLauncherKind.Hmcl
                File.WriteAllText(path, New JObject From {{"usesGlobal", False}, {"gameDirType", "invalid"}, {"unrelated", "keep"}}.ToString())
        End Select
    End Sub
End Class
