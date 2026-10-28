Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media.Animation
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifySurfaceNavigationUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim focus = LocalModCheckGetNativeFocus(), observations As New JObject, failures As New List(Of Exception)
        Dim host As New Border With {.Width = 900, .Height = 700}, layout = ControlLayout(host)
        FocusManager.SetIsFocusScope(host, True)
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                     File.WriteAllText(Path.Combine(directory, "surface-navigation.json"), observations.ToString())
                                                 End Sub
        Dim capture As DispatcherUnhandledExceptionEventHandler = Sub(sender, args)
                                                                     failures.Add(args.Exception) : args.Handled = True
                                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        AddHandler owner.Dispatcher.UnhandledException, capture
        Try
            Using surface = PreviewSurface.Attach(host, 900, 700)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckSurfaceNavigationAsync(directory, host, layout, check, failures)
                If Not PumpMotionUntil(Function() work.IsCompleted, 180000) Then Throw New TimeoutException("Main surface checks did not settle.")
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

    Private Async Function CheckSurfaceNavigationAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String), failures As List(Of Exception)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _managementMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion() : _titleNavigation.Finish() : layout()
                               End Sub
        For launcher = 0 To 2
            For destination = 0 To 1
                For origin = 0 To 2
                    For scenario = 0 To 4
                        _motionPreview = False : _surfaceMotion.PauseForChecks = False : _managementMotion.PauseForChecks = False
                        _versionsDirty = False : _versionProvider = Nothing
                        _managementProvider = Function(binding, token) Task.FromResult(InstalledInstanceCatalog.Scan(binding, token))
                        ShowDownload(Nothing, Nothing) : settle()
                        Dim folders = PrepareManagementFixture(Path.Combine(directory, "native-" & launcher & "-" & destination & "-" & origin & "-" & scenario))
                        FolderList.SelectedItem = folders(launcher)
                        ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(True) : settle()
                        Dim owner = _managementInstances.Single(Function(item) item.Name = "Fabric 实例"), target = CurrentInstallTarget()
                        SelectManagedInstance(owner) : settle()
                        FocusLocalModElementForChecks(ManagementDetailPage)
                        Dim files = DirectoryFilesForModUpdateChecks(Path.GetDirectoryName(target.Root)), taskGeneration = _taskGeneration
                        Dim sources = Enumerable.Range(0, 5).Select(Function(index) New CancellationTokenSource()).ToArray(), tokens = sources.Select(Function(source) source.Token).ToArray()
                        _managementRequest = sources(0) : _managementFolderRequest = sources(1) : _localModUpdatesRequest = sources(2) : _localModFolderRequest = sources(3) : _localModRequest = sources(4)
                        Dim selectedOrigin = If(origin = 0, destination, If(origin = 1, 2, 4)), chosenCase = scenario
                        Dim requested = (launcher + destination) Mod 3, animated = (launcher + origin + scenario) Mod 2 = 0
                        Dim incoming As FrameworkElement = If(requested = 0, DirectCast(SettingsPage, FrameworkElement), If(requested = 1, DirectCast(DownloadPage, FrameworkElement), AboutPage))
                        RestoreAppearance(New JObject From {{"darkMode", (launcher + scenario) Mod 2 = 0}}) : _motionPreview = animated : _surfaceMotion.PauseForChecks = animated
                        SettingsSaveFeedback.Text = "" : StatusText.Text = "before switch" : _managementReturnName = owner.Name
                        Dim expectedMain = incoming, expectedInstance = _managedInstance, expectedReturn = CStr(Nothing)
                        Dim expectedClock As ClockGroup = Nothing, expectedFocus As Action = Nothing, live As CancellationTokenSource = Nothing, liveToken As CancellationToken
                        Dim scanGate As New TaskCompletionSource(Of List(Of InstalledInstance))(TaskCreationOptions.RunContinuationsAsynchronously), scans = 0
                        Dim refreshed = _managementInstances.ToList(), immediate = launcher = 1, escaped = False, obsoleteDownloadReads = 0
                        If immediate Then scanGate.SetResult(refreshed)
                        Dim label = "/launcher=" & target.Kind.ToString() & "/target=" & requested & "/origin=" & selectedOrigin & "/case=" & scenario
                        failures.Clear()
                        Using registration = tokens(selectedOrigin).Register(Sub()
                                                                                If chosenCase = 3 Then Throw New IOException("Controlled main surface cancellation failure")
                                                                                Select Case chosenCase
                                                                                    Case 0
                                                                                        _managementDirty = True
                                                                                        _managementProvider = Function(binding, token)
                                                                                                                  scans += 1 : Return scanGate.Task
                                                                                                              End Function
                                                                                        ShowManagement(Nothing, Nothing) : live = _managementRequest
                                                                                        If live IsNot Nothing Then liveToken = live.Token
                                                                                    Case 1, 4
                                                                                        If requested = 0 Then
                                                                                            ShowAbout(Nothing, Nothing)
                                                                                        ElseIf requested = 1 Then
                                                                                            ShowSettings(Nothing, Nothing)
                                                                                        Else
                                                                                            ShowDownload(Nothing, Nothing)
                                                                                        End If
                                                                                    Case 2 : NavigateToSetting(SpeedLimitBox, True)
                                                                                End Select
                                                                                If requested = 1 Then
                                                                                    _versionsDirty = True
                                                                                    _versionProvider = Function(policy, token)
                                                                                                           obsoleteDownloadReads += 1 : Return Task.FromResult(_versions.ToList())
                                                                                                       End Function
                                                                                End If
                                                                                _managementReturnName = "newer return owner"
                                                                                expectedMain = ActiveSurface() : expectedInstance = _managedInstance : expectedReturn = _managementReturnName
                                                                                expectedClock = _surfaceMotion.Transition : expectedFocus = _pendingSurfaceFocus
                                                                                If chosenCase = 4 Then Throw New IOException("Controlled obsolete surface failure")
                                                                            End Sub)
                            Try
                                If requested = 0 Then
                                    NavigateToSetting(SourceBox, True)
                                ElseIf requested = 1 Then
                                    ShowDownload(Nothing, Nothing)
                                Else
                                    ShowAbout(Nothing, Nothing)
                                End If
                            Catch
                                escaped = True
                            End Try
                            Await Task.Yield() : layout()
                            check(Not escaped AndAlso failures.Count = 0 AndAlso obsoleteDownloadReads = 0 AndAlso ActiveSurface() Is expectedMain AndAlso _managedInstance Is expectedInstance AndAlso _managementReturnName = expectedReturn AndAlso
                                If(scenario = 3, StatusText.Text.Contains("重试") OrElse SettingsSaveFeedback.Text.Contains("重试"), _surfaceMotion.Transition Is expectedClock AndAlso _pendingSurfaceFocus Is expectedFocus) AndAlso
                                If(scenario = 0, scans = 1 AndAlso If(immediate, Not _managementDirty AndAlso _managementRequest Is Nothing, live IsNot Nothing AndAlso _managementRequest Is live AndAlso Not liveToken.IsCancellationRequested), True),
                                "main surface handoff preserves a newer page, scan, return owner, pending focus and animation without leaking cancellation faults" & label)
                            scanGate.TrySetResult(refreshed)
                            Await WaitPackUiAsync(Function() _managementRequest Is Nothing OrElse _managementRequest Is sources(0))
                            Await WaitPackUiAsync(Function() Not _loading)
                            Await Task.Yield() : settle()
                            Dim focused = TryCast(FocusManager.GetFocusedElement(host), FrameworkElement)
                            check(failures.Count = 0 AndAlso obsoleteDownloadReads = 0 AndAlso tokens.All(Function(token) token.IsCancellationRequested) AndAlso ActiveSurface() Is expectedMain AndAlso PageSurface.IsHitTestVisible AndAlso
                                If(scenario = 0, _managementInstances Is refreshed AndAlso Not _managementDirty, _managedInstance Is expectedInstance) AndAlso
                                If(scenario = 2, focused Is SpeedLimitBox, True) AndAlso _taskGeneration = taskGeneration,
                                "all captured readers retire and late work cannot replace accepted page state or setting focus after settling" & label)
                        End Using
                        If _managementRequest Is sources(0) Then _managementRequest = Nothing
                        If _managementFolderRequest Is sources(1) Then _managementFolderRequest = Nothing
                        If _localModUpdatesRequest Is sources(2) Then _localModUpdatesRequest = Nothing
                        If _localModFolderRequest Is sources(3) Then _localModFolderRequest = Nothing
                        If _localModRequest Is sources(4) Then _localModRequest = Nothing
                        For Each source In sources : CancelSubmittedSearchRequest(source) : source.Dispose() : Next
                        _versionsDirty = False : _versionProvider = Nothing
                        _managementProvider = Function(binding, token) Task.FromResult(InstalledInstanceCatalog.Scan(binding, token))
                        _surfaceMotion.PauseForChecks = False : _motionPreview = False
                        ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(True) : settle()
                        SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = owner.Name)) : settle()
                        ShowSettings(Nothing, Nothing) : settle() : ShowDownload(Nothing, Nothing) : settle()
                        check(DownloadPage.IsVisible AndAlso DownloadTab.Checked AndAlso Not ManagementTab.Checked AndAlso Not SettingsTab.Checked AndAlso PageSurface.IsHitTestVisible AndAlso
                            _surfaceMotion.Transition Is Nothing AndAlso _pendingSurfaceFocus Is Nothing AndAlso _taskGeneration = taskGeneration AndAlso
                            files.All(Function(item) File.Exists(item.Key) AndAlso File.ReadAllBytes(item.Key).SequenceEqual(item.Value)),
                            "main surface retry restores tab state and input while retaining native launcher configuration and existing instance files" & label)
                    Next
                Next
            Next
        Next
    End Function
End Class
