Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media.Animation
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyLocalModNavigationUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim focus = LocalModCheckGetNativeFocus(), observations As New JObject, failures As New List(Of Exception)
        Dim host As New Border With {.Width = 900, .Height = 700}, layout = ControlLayout(host)
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                     File.WriteAllText(Path.Combine(directory, "local-mod-navigation.json"), observations.ToString())
                                                 End Sub
        Dim capture As DispatcherUnhandledExceptionEventHandler = Sub(sender, args)
                                                                     failures.Add(args.Exception) : args.Handled = True
                                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        AddHandler owner.Dispatcher.UnhandledException, capture
        Try
            Using surface = PreviewSurface.Attach(host, 900, 700)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckLocalModNavigationAsync(directory, layout, check, failures)
                If Not PumpMotionUntil(Function() work.IsCompleted, 90000) Then Throw New TimeoutException("Mod navigation checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            Dim failed = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failed.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failed))
        Finally
            RemoveHandler owner.Dispatcher.UnhandledException, capture
            owner._closing = True : owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._managementMotion.Finish() : owner._categoryMotion.Finish()
            host.Child = Nothing : owner.Content = original
            SynchronizationContext.SetSynchronizationContext(previous) : LocalModCheckSetNativeFocus(focus)
        End Try
    End Sub

    Private Async Function CheckLocalModNavigationAsync(directory As String, layout As Action, check As Action(Of Boolean, String), failures As List(Of Exception)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _managementMotion.Finish() : _categoryMotion.Finish() : layout()
                               End Sub
        Dim realProvider = _localModProvider, realReader = _localModInstanceReader
        _managementProvider = Function(target, token) Task.FromResult(InstalledInstanceCatalog.Scan(target, token))
        _localModUpdateProvider = Function(instance, entries, token) Task.FromResult(New LocalModUpdateCheckResult({}))
        For Each returning In {False, True}
            For launcher = 0 To 2
                For origin = 0 To 2
                    For scenario = 0 To 4
                        _motionPreview = False : _managementMotion.PauseForChecks = False : _surfaceMotion.PauseForChecks = False
                        _localModProvider = realProvider : _localModInstanceReader = realReader
                        ShowDownload(Nothing, Nothing) : settle()
                        Dim folders = PrepareLocalModFixture(Path.Combine(directory, "native-" & returning & "-" & launcher & "-" & origin & "-" & scenario))
                        FolderList.SelectedItem = folders(launcher)
                        Dim target = CurrentInstallTarget(), native = InstalledInstanceCatalog.Read(target, "Fabric 实例", CancellationToken.None)
                        SetLocalModIsolationForChecks(native, launcher Mod 2 = 0)
                        LocalModJarForChecks(Path.Combine(target.Root, "mods/shared-navigation.jar"), "navigation_shared")
                        ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(True) : settle()
                        SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = native.Name)) : settle()
                        If returning Then ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                        Dim owner = _managedInstance, other = _managementInstances.Single(Function(item) item.Name = "我的原版")
                        Dim files = DirectoryFilesForModUpdateChecks(Path.GetDirectoryName(target.Root)), taskGeneration = _taskGeneration
                        Dim entries = LocalModCatalog.Scan(owner, CancellationToken.None), obsoleteCalls = 0, latestCalls = 0
                        Dim sources = Enumerable.Range(0, 5).Select(Function(index) New CancellationTokenSource()).ToArray(), tokens = sources.Select(Function(source) source.Token).ToArray()
                        If Not returning Then _managementRequest = sources(0) : _managementFolderRequest = sources(1)
                        _localModUpdatesRequest = sources(2) : _localModFolderRequest = sources(3) : _localModRequest = sources(4)
                        Dim selectedOrigin = If(origin = 0, If(returning, 3, launcher Mod 2), If(origin = 1, 2, 4)), chosenCase = scenario
                        Dim animated = (launcher + origin + scenario) Mod 2 = 0, immediate = launcher = 1
                        RestoreAppearance(New JObject From {{"darkMode", (launcher + scenario) Mod 2 = 0}}) : _motionPreview = animated
                        _managementMotion.PauseForChecks = animated : _surfaceMotion.PauseForChecks = animated
                        Dim latest As New TaskCompletionSource(Of List(Of LocalModEntry))(TaskCreationOptions.RunContinuationsAsynchronously)
                        If immediate Then latest.SetResult(entries)
                        Dim newest As CancellationTokenSource = Nothing, newestToken As CancellationToken
                        Dim expectedInstance = _managedInstance, expectedContent = ManagementContent(), expectedMain = ActiveSurface()
                        Dim contentClock As ClockGroup = Nothing, surfaceClock As ClockGroup = Nothing
                        Dim label = "/back=" & returning & "/launcher=" & target.Kind.ToString() & "/origin=" & selectedOrigin & "/case=" & scenario
                        failures.Clear()
                        _localModProvider = Function(instance, token)
                                                obsoleteCalls += 1 : Return Task.FromResult(entries)
                                            End Function
                        Using registration = tokens(selectedOrigin).Register(Sub()
                                                                                If chosenCase = 3 Then Throw New IOException("Controlled Mod navigation cancellation failure")
                                                                                settle()
                                                                                Select Case chosenCase
                                                                                    Case 0 : SelectManagedInstance(other)
                                                                                    Case 1
                                                                                        _localModInstanceReader = Function(instance, token) Task.FromResult(InstalledInstanceCatalog.Read(instance.Target, instance.Name, token))
                                                                                        _localModProvider = Function(instance, token)
                                                                                                                latestCalls += 1 : Return latest.Task
                                                                                                            End Function
                                                                                        If returning Then
                                                                                            Dim work = LoadLocalModsAsync(True)
                                                                                        Else
                                                                                            ShowLocalMods(Nothing, Nothing)
                                                                                        End If
                                                                                        newest = _localModRequest
                                                                                        If newest IsNot Nothing Then newestToken = newest.Token
                                                                                    Case 2 : ShowSettings(Nothing, Nothing)
                                                                                    Case 4
                                                                                        If returning Then BackFromLocalMods() Else BackFromManagement(Nothing, Nothing)
                                                                                End Select
                                                                                expectedInstance = _managedInstance : expectedContent = ManagementContent() : expectedMain = ActiveSurface()
                                                                                contentClock = _managementMotion.Transition : surfaceClock = _surfaceMotion.Transition
                                                                                _localModProvider = Function(instance, token)
                                                                                                        obsoleteCalls += 1 : Return Task.FromResult(entries)
                                                                                                    End Function
                                                                            End Sub)
                            Dim escaped = False
                            Try
                                If returning Then BackFromLocalMods() Else ShowLocalMods(Nothing, Nothing)
                            Catch
                                escaped = True
                            End Try
                            Await Task.Yield() : layout()
                            Dim feedback = If(LocalModsPage.IsVisible, LocalModStatus.Text, If(ManagementDetailPage.IsVisible, ManagedRepairStatus.Text, ManagementStatus.Text))
                            check(Not escaped AndAlso failures.Count = 0 AndAlso _managedInstance Is expectedInstance AndAlso ManagementContent() Is expectedContent AndAlso ActiveSurface() Is expectedMain AndAlso obsoleteCalls = 0 AndAlso
                                If(scenario = 3, feedback.Contains("重试"), _managementMotion.Transition Is contentClock AndAlso _surfaceMotion.Transition Is surfaceClock) AndAlso
                                If(scenario = 1, latestCalls = 1 AndAlso If(immediate, _localMods Is entries AndAlso _localModReadSucceeded, newest IsNot Nothing AndAlso _localModRequest Is newest AndAlso Not newestToken.IsCancellationRequested), True),
                                "Mod enter and back preserve newer instances, reads, surfaces and clocks; owned cancellation faults remain retryable" & label)
                            latest.TrySetResult(entries)
                            Await WaitPackUiAsync(Function() _localModRequest Is Nothing OrElse _localModRequest Is sources(4))
                            Await Task.Yield() : settle()
                            Dim required = If(returning, tokens.Skip(2), tokens.AsEnumerable())
                            check(failures.Count = 0 AndAlso required.All(Function(token) token.IsCancellationRequested) AndAlso ManagementContent() Is expectedContent AndAlso ActiveSurface() Is expectedMain AndAlso obsoleteCalls = 0 AndAlso
                                If(scenario = 1, _localMods Is entries AndAlso _localModReadSucceeded AndAlso SameLocalModContext(_localModInstance), _managedInstance Is expectedInstance) AndAlso _taskGeneration = taskGeneration,
                                "all captured old Mod reads retire even after a fault and cannot publish over the accepted context" & label)
                        End Using
                        If _managementRequest Is sources(0) Then _managementRequest = Nothing
                        If _managementFolderRequest Is sources(1) Then _managementFolderRequest = Nothing
                        If _localModUpdatesRequest Is sources(2) Then _localModUpdatesRequest = Nothing
                        If _localModFolderRequest Is sources(3) Then _localModFolderRequest = Nothing
                        If _localModRequest Is sources(4) Then _localModRequest = Nothing
                        For Each source In sources : CancelSubmittedSearchRequest(source) : source.Dispose() : Next
                        _localModProvider = realProvider : _localModInstanceReader = realReader
                        _managementMotion.PauseForChecks = False : _surfaceMotion.PauseForChecks = False
                        ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(True) : settle()
                        SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = owner.Name)) : settle()
                        ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                        Dim accepted = _localModReadSucceeded AndAlso SameLocalModContext(_localModInstance) AndAlso _localModInstance.Name = owner.Name AndAlso _localModInstance.IsolationSummary = owner.IsolationSummary AndAlso SameGameRoot(_localModInstance.RunDirectory, owner.RunDirectory)
                        BackFromLocalMods() : settle()
                        check(accepted AndAlso ManagementDetailPage.IsVisible AndAlso _managedInstance.Name = owner.Name AndAlso ManagedIntro.Title = owner.Name AndAlso
                            ManagementViewHost.IsHitTestVisible AndAlso PageSurface.IsHitTestVisible AndAlso Not _busy AndAlso _taskGeneration = taskGeneration AndAlso
                            files.All(Function(item) File.Exists(item.Key) AndAlso File.ReadAllBytes(item.Key).SequenceEqual(item.Value)),
                            "Mod retry and return restore page input and preserve native isolation, shared directories, configuration and existing files" & label)
                    Next
                Next
            Next
        Next
    End Function
End Class
