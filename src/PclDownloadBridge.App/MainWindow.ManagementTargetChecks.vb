Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyManagementTargetUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim host As New Border With {.Width = 900, .Height = 700}, observations As New JObject, faults As New List(Of Exception)
        Dim capture As DispatcherUnhandledExceptionEventHandler = Sub(sender, args)
                                                                     faults.Add(args.Exception) : args.Handled = True
                                                                 End Sub
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                     File.WriteAllText(Path.Combine(directory, "management-target.json"), observations.ToString())
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        AddHandler owner.Dispatcher.UnhandledException, capture
        Try
            Using surface = PreviewSurface.Attach(host, 900, 700)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckManagementTargetAsync(directory, ControlLayout(host), check, faults, host)
                If Not PumpMotionUntil(Function() work.IsCompleted, 180000) Then Throw New TimeoutException("Management target checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            Dim failed = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failed.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failed))
        Finally
            RemoveHandler owner.Dispatcher.UnhandledException, capture
            owner._closing = True : owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._surfaceMotion.Finish() : owner._managementMotion.Finish() : owner._categoryMotion.Finish()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            host.Child = Nothing : owner.Content = original : SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub

    Private Async Function CheckManagementTargetAsync(directory As String, layout As Action, check As Action(Of Boolean, String), faults As List(Of Exception), host As Border) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _managementMotion.Finish() : _categoryMotion.Finish() : _titleNavigation.Finish() : layout()
                               End Sub
        For launcher = 0 To 2
            For reader = 0 To 1
                For scenario = 0 To 3
                    _motionPreview = False
                    Dim folders = PrepareLocalModFixture(Path.Combine(directory, "target-" & launcher & "-" & reader & "-" & scenario))
                    FolderList.SelectedItem = folders(launcher)
                    ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(False) : settle()
                    SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = "Fabric 实例")) : settle()
                    ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                    Dim initialTarget = CurrentInstallTarget(), files = DirectoryFilesForModUpdateChecks(Path.GetDirectoryName(initialTarget.Root)), taskGeneration = _taskGeneration
                    Dim realScan = _managementProvider, realMods = _localModProvider
                    Dim scanGate = ListGate(Of InstalledInstance)(), modGate = ListGate(Of LocalModEntry)(), oldWork As Task
                    If reader = 0 Then
                        _managementProvider = Function(target, token) scanGate.Task
                        oldWork = ScanManagementAsync(True)
                    Else
                        _localModProvider = Function(instance, token) modGate.Task
                        oldWork = LoadLocalModsAsync(True)
                    End If
                    Dim oldRequest = If(reader = 0, _managementRequest, _localModRequest), tokenBefore = oldRequest.Token
                    Dim chosen = scenario, nextFolder = folders((launcher + 1) Mod 3), lastFolder = folders((launcher + 2) Mod 3)
                    Dim callbackCount = 0, escaped = False, accepted As InstalledInstance = Nothing, acceptedRows As List(Of LocalModEntry) = Nothing
                    Dim acceptedClock As System.Windows.Media.Animation.ClockGroup = Nothing
                    Dim acceptedMain As FrameworkElement = Nothing, expectedFolder As GameFolder = nextFolder
                    RestoreAppearance(New JObject From {{"darkMode", (launcher + reader + scenario) Mod 2 = 0}})
                    _motionPreview = scenario Mod 2 = 0 : _surfaceMotion.PauseForChecks = _motionPreview : _managementMotion.PauseForChecks = _motionPreview
                    Dim beforeFaults = faults.Count
                    Dim label = "/launcher=" & initialTarget.Kind.ToString() & "/reader=" & reader & "/case=" & scenario
                    Using registration = oldRequest.Token.Register(Sub()
                                                                      callbackCount += 1
                                                                      _managementProvider = realScan : _localModProvider = realMods
                                                                      If chosen = 1 Then
                                                                          ShowSettings(Nothing, Nothing)
                                                                          acceptedMain = ActiveSurface() : acceptedClock = _surfaceMotion.Transition
                                                                      ElseIf chosen = 3 Then
                                                                          Throw New IOException("Controlled target retirement failure")
                                                                      Else
                                                                          If chosen = 2 Then FolderList.SelectedItem = lastFolder : expectedFolder = lastFolder
                                                                          ShowManagement(Nothing, Nothing) : settle()
                                                                          SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = "Fabric 实例")) : settle()
                                                                          ShowLocalMods(Nothing, Nothing)
                                                                          accepted = _managedInstance : acceptedRows = _localMods : acceptedClock = _managementMotion.Transition
                                                                          acceptedMain = ActiveSurface()
                                                                      End If
                                                                  End Sub)
                        Try
                            FolderList.SelectedItem = nextFolder
                        Catch
                            escaped = True
                        End Try
                        layout()
                        check(Not escaped AndAlso faults.Count = beforeFaults AndAlso callbackCount = 1 AndAlso tokenBefore.IsCancellationRequested AndAlso
                            _selectedFolder Is expectedFolder AndAlso ManagementFolders.SelectedItem Is expectedFolder,
                            "target change contains retirement failures and retains the newest same-root launcher binding" & label)
                        check(If(chosen = 1, ActiveSurface() Is acceptedMain AndAlso _surfaceMotion.Transition Is acceptedClock,
                            If(chosen = 3, ManagementListPage.IsVisible AndAlso _managementDirty AndAlso _managementRequest Is Nothing AndAlso _localModRequest Is Nothing,
                                ManagementPage.IsVisible AndAlso LocalModsPage.IsVisible AndAlso _managedInstance Is accepted AndAlso _localModInstance Is accepted AndAlso
                                _localMods Is acceptedRows AndAlso _localModReadSucceeded AndAlso _managementMotion.Transition Is acceptedClock)),
                            "old target cleanup cannot clear a replacement Mod page or finish a newer main-page animation" & label)
                    End Using
                    _managementProvider = realScan : _localModProvider = realMods
                    scanGate.TrySetResult(New List(Of InstalledInstance)) : modGate.TrySetResult(New List(Of LocalModEntry)) : Await oldWork
                    Dim latePreserved = If(chosen = 1, ActiveSurface() Is acceptedMain AndAlso _surfaceMotion.Transition Is acceptedClock,
                        If(chosen = 3, True, LocalModsPage.IsVisible AndAlso _managedInstance Is accepted AndAlso _localMods Is acceptedRows AndAlso _managementMotion.Transition Is acceptedClock))
                    _motionPreview = False : _surfaceMotion.PauseForChecks = False : _managementMotion.PauseForChecks = False : settle()
                    If launcher = 0 AndAlso reader = 0 AndAlso scenario = 2 Then SaveControlVisual(host, Path.Combine(directory, "target-handoff.png"))
                    ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(True) : settle()
                    If LocalModsPage.IsVisible Then BackFromLocalMods() : settle()
                    SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = "Fabric 实例")) : settle()
                    ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                    Dim expectedMods = LocalModCatalog.Scan(_managedInstance, CancellationToken.None)
                    check(latePreserved AndAlso _localModReadSucceeded AndAlso SameManagementTarget(_localModInstance.Target, CurrentInstallTarget()) AndAlso
                        _localMods.Select(Function(item) item.FilePath).SequenceEqual(expectedMods.Select(Function(item) item.FilePath)) AndAlso
                        PageSurface.IsHitTestVisible AndAlso ManagementViewHost.IsHitTestVisible AndAlso _taskGeneration = taskGeneration AndAlso
                        files.All(Function(item) File.Exists(item.Key) AndAlso File.ReadAllBytes(item.Key).SequenceEqual(item.Value)),
                        "late old results and explicit retry preserve native isolation files and usable Mod input" & label)
                Next
            Next
        Next
    End Function
End Class
