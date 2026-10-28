Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyManagementEntryErrorsUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim host As New Border With {.Width = 900, .Height = 700}, observations As New JObject, faults As New List(Of Exception)
        Dim capture As DispatcherUnhandledExceptionEventHandler = Sub(sender, args)
                                                                     faults.Add(args.Exception) : args.Handled = True
                                                                 End Sub
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                     File.WriteAllText(Path.Combine(directory, "management-entry-errors.json"), observations.ToString())
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        AddHandler owner.Dispatcher.UnhandledException, capture
        Try
            Using surface = PreviewSurface.Attach(host, 900, 700)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckManagementEntryErrorsAsync(directory, ControlLayout(host), check, faults)
                If Not PumpMotionUntil(Function() work.IsCompleted, 180000) Then Throw New TimeoutException("Management entry error checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            Dim failed = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failed.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failed))
        Finally
            RemoveHandler owner.Dispatcher.UnhandledException, capture
            owner._closing = True : owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._managementMotion.Finish() : owner._categoryMotion.Finish() : owner._pageSwitch.Finish()
            host.Child = Nothing : owner.Content = original : SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub

    Private Async Function CheckManagementEntryErrorsAsync(directory As String, layout As Action, check As Action(Of Boolean, String), faults As List(Of Exception)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _managementMotion.Finish() : _categoryMotion.Finish() : _pageSwitch.Finish() : _titleNavigation.Finish() : layout()
                               End Sub
        For launcher = 0 To 2
            For entry = 0 To 3
                For scenario = 0 To 3
                    _motionPreview = False
                    Dim folders = PrepareLocalModFixture(Path.Combine(directory, "native-" & launcher & "-" & entry & "-" & scenario))
                    FolderList.SelectedItem = folders(launcher)
                    Dim target = CurrentInstallTarget(), name = If(entry = 3, "Fabric 实例", "我的原版")
                    Dim realProvider As Func(Of LauncherInstallTarget, CancellationToken, Task(Of List(Of InstalledInstance))) = Function(binding, token) Task.FromResult(InstalledInstanceCatalog.Scan(binding, token))
                    Dim realReader As Func(Of InstalledInstance, CancellationToken, Task(Of InstalledInstance)) = Function(instance, token) Task.FromResult(InstalledInstanceCatalog.Read(instance.Target, instance.Name, token))
                    _managementProvider = realProvider : _managementFolderReader = realReader : _managementRepairReader = realReader
                    ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(True) : settle()
                    SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = name)) : settle()
                    IO.Directory.CreateDirectory(Path.Combine(_managedInstance.RunDirectory, "mods"))
                    Dim button = ManagedFolderActions.Children.OfType(Of PclButton)().Single(Function(item) CStr(item.Tag) = "mods")
                    If entry = 3 Then ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                    Dim instanceOwner = _managedInstance, protectedFiles = DirectoryFilesForModUpdateChecks(Path.GetDirectoryName(target.Root)), taskGeneration = _taskGeneration
                    Dim calls = 0, opened = 0, realMods = _localModProvider, realNative = _localModInstanceReader, selectedEntry = entry, chosen = scenario
                    _managementOpenFolder = Sub(path) opened += 1
                    _managementProvider = Function(binding, token)
                                              calls += 1 : Return realProvider(binding, token)
                                          End Function
                    _managementFolderReader = Function(instance, token)
                                                  calls += 1 : Return realReader(instance, token)
                                              End Function
                    _managementRepairReader = Function(instance, token)
                                                  calls += 1 : Return realReader(instance, token)
                                              End Function
                    _localModInstanceReader = Function(instance, token)
                                                  calls += 1 : Return realNative(instance, token)
                                              End Function
                    Dim scanSource As New CancellationTokenSource(), folderSource As New CancellationTokenSource(), oldScanToken = scanSource.Token, oldFolderToken = folderSource.Token
                    _managementRequest = scanSource : _managementFolderRequest = folderSource
                    Dim retired = If(entry = 1 OrElse scenario Mod 2 = 1, folderSource, scanSource)
                    Dim nested As Task = Task.CompletedTask, latest As CancellationTokenSource = Nothing, latestToken As CancellationToken
                    Dim folderGate As New TaskCompletionSource(Of InstalledInstance)(TaskCreationOptions.RunContinuationsAsynchronously), modGate = ListGate(Of LocalModEntry)()
                    Dim expectedMain = ActiveSurface(), expectedClock = _surfaceMotion.Transition, expectedFeedback = "", escaped = False
                    Dim feedback As Func(Of String) = Function() If(selectedEntry = 0, If(ManagementDetailPage.IsVisible, ManagedRepairStatus.Text, ManagementStatus.Text), If(selectedEntry = 1, ManagedFolderStatus.Text, If(selectedEntry = 2, ManagedRepairStatus.Text, LocalModStatus.Text)))
                    RestoreAppearance(New JObject From {{"darkMode", (launcher + entry + scenario) Mod 2 = 0}}) : _motionPreview = scenario Mod 2 = 1
                    _surfaceMotion.PauseForChecks = _motionPreview : _managementMotion.PauseForChecks = _motionPreview
                    faults.Clear()
                    Using registration = retired.Token.Register(Sub()
                                                                   If chosen = 1 Then
                                                                       ShowSettings(Nothing, Nothing)
                                                                   ElseIf chosen = 2 Then
                                                                       If selectedEntry = 3 Then
                                                                           _localModProvider = Function(instance, token) modGate.Task
                                                                           nested = LoadLocalModsAsync(True) : latest = _localModRequest
                                                                       Else
                                                                           _managementFolderReader = Function(instance, token) folderGate.Task
                                                                           OpenManagedFolder(button, Nothing) : latest = _managementFolderRequest
                                                                       End If
                                                                       If latest IsNot Nothing Then latestToken = latest.Token
                                                                   End If
                                                                   expectedMain = ActiveSurface() : expectedClock = _surfaceMotion.Transition : expectedFeedback = feedback()
                                                                   Throw New IOException("Controlled entry cancellation failure")
                                                               End Sub)
                        If chosen = 3 Then retired.Dispose()
                        Try
                            Select Case selectedEntry
                                Case 0 : Await RefreshManagementAsync()
                                Case 1 : OpenManagedFolder(button, Nothing)
                                Case 2 : Await PrepareManagedRepairAsync()
                                Case 3 : Await LoadLocalModsAsync(True, refreshInstance:=True)
                            End Select
                        Catch
                            escaped = True
                        End Try
                        Await Task.Yield() : layout()
                        Dim label = "/launcher=" & target.Kind.ToString() & "/entry=" & entry & "/case=" & scenario
                        check(Not escaped AndAlso faults.Count = 0 AndAlso If(chosen = 3, calls = 1, calls = 0) AndAlso
                            If(chosen = 0, feedback().Contains("Controlled entry"), True) AndAlso
                            If(chosen = 1, ActiveSurface() Is expectedMain AndAlso _surfaceMotion.Transition Is expectedClock AndAlso feedback() = expectedFeedback, True) AndAlso
                            If(chosen = 2, latest IsNot Nothing AndAlso Not latestToken.IsCancellationRequested AndAlso feedback() = expectedFeedback AndAlso If(entry = 3, _localModRequest Is latest, _managementFolderRequest Is latest), True),
                            "entry retirement contains observer failures, ignores disposed sources and preserves a newer page or reader" & label)
                        folderGate.TrySetResult(instanceOwner) : modGate.TrySetResult(_localMods.ToList()) : Await nested
                        Await WaitPackUiAsync(Function() _managementFolderRequest Is Nothing OrElse _managementFolderRequest Is folderSource)
                        Await Task.Yield() : settle()
                        check(faults.Count = 0 AndAlso Not _managementScanning AndAlso Not _managementPreparing AndAlso
                            (chosen = 3 OrElse oldFolderToken.IsCancellationRequested) AndAlso If(entry = 1, True, oldScanToken.IsCancellationRequested) AndAlso
                            If(chosen = 1, ActiveSurface() Is expectedMain AndAlso _managedInstance Is instanceOwner, True) AndAlso
                            opened = If((chosen = 2 AndAlso entry <> 3) OrElse (chosen = 3 AndAlso entry = 1), 1, 0) AndAlso _taskGeneration = taskGeneration,
                            "retired callbacks cannot reopen old folders or clear accepted reads, preparation state and task ownership" & label)
                    End Using
                    If _managementRequest Is scanSource Then _managementRequest = Nothing
                    If _managementFolderRequest Is folderSource Then _managementFolderRequest = Nothing
                    CancelSubmittedSearchRequest(scanSource) : CancelSubmittedSearchRequest(folderSource) : scanSource.Dispose() : folderSource.Dispose()
                    _localModProvider = realMods : _localModInstanceReader = realNative : _managementProvider = realProvider : _managementFolderReader = realReader : _managementRepairReader = realReader
                    _motionPreview = False : _surfaceMotion.PauseForChecks = False : _managementMotion.PauseForChecks = False
                    ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(True) : settle()
                    If LocalModsPage.IsVisible Then BackFromLocalMods() : settle()
                    SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = name)) : settle()
                    ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                    check(_localModReadSucceeded AndAlso SameManagementTarget(_localModInstance.Target, target) AndAlso PageSurface.IsHitTestVisible AndAlso ManagementViewHost.IsHitTestVisible AndAlso
                        _taskGeneration = taskGeneration AndAlso protectedFiles.All(Function(item) File.Exists(item.Key) AndAlso File.ReadAllBytes(item.Key).SequenceEqual(item.Value)),
                        "explicit retry restores native Mod access and preserves every existing configuration and game file" & "/launcher=" & target.Kind.ToString() & "/entry=" & entry & "/case=" & scenario)
                Next
            Next
        Next
    End Function
End Class
