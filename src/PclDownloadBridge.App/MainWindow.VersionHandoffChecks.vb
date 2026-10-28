Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyVersionHandoffUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim host As New Border With {.Width = 900, .Height = 700}, observations As New JObject, faults As New List(Of Exception)
        Dim capture As DispatcherUnhandledExceptionEventHandler = Sub(sender, args)
                                                                     faults.Add(args.Exception) : args.Handled = True
                                                                 End Sub
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                     File.WriteAllText(Path.Combine(directory, "version-handoff.json"), observations.ToString())
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        AddHandler owner.Dispatcher.UnhandledException, capture
        Try
            Using source = PreviewSurface.Attach(host, 900, 700)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckVersionHandoffAsync(ControlLayout(host), check, faults)
                If Not PumpMotionUntil(Function() work.IsCompleted, 120000) Then Throw New TimeoutException("Version handoff checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            Dim failed = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failed.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failed))
        Finally
            RemoveHandler owner.Dispatcher.UnhandledException, capture
            owner._closing = True : CancelSubmittedSearchRequest(owner._versionCancellation) : owner._versionLoading.Reset()
            owner.CancelComponentRequests() : owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner._pageSwitch.Finish() : owner._titleNavigation.Finish()
            host.Child = Nothing : owner.Content = original
            SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub

    Private Async Function CheckVersionHandoffAsync(layout As Action, check As Action(Of Boolean, String), faults As List(Of Exception)) As Task
        Dim sample = _versions.ToList(), settle As Action = Sub()
                                                              _surfaceMotion.Finish() : _categoryMotion.Finish() : _pageSwitch.Finish() : _titleNavigation.Finish() : layout()
                                                          End Sub
        For Each dark In {False, True}
            For Each animated In {False, True}
                For Each changeSource In {False, True}
                    For scenario = 0 To 7
                        _motionPreview = False : _busy = False : _versionsDirty = False : _versionProvider = Nothing
                        _restoringSettings = True : VersionSourceBox.SelectedIndex = 1 : _restoringSettings = False
                        _versions = sample.ToList() : ShowDownload(Nothing, Nothing) : SetDownloadView(DownloadView.Versions) : settle()
                        Dim cached = scenario Mod 2 = 0, catalogue = If(cached, sample.ToList(), New List(Of VersionEntry)())
                        _versions = catalogue : UpdateVersions() : settle()
                        Dim oldGate = ListGate(Of VersionEntry)(), newGate = ListGate(Of VersionEntry)(), nested As Task = Task.CompletedTask, action As Task = Task.CompletedTask
                        Dim oldToken As CancellationToken, newToken As CancellationToken, newRequest As CancellationTokenSource = Nothing
                        Dim phase = 0, calls = 0, escaped = False, taskGeneration = _taskGeneration, chosen = scenario
                        Dim fresh = New List(Of VersionEntry) From {sample(1)}, expected = catalogue
                        Dim components = Enumerable.Range(0, 2).Select(Function(index) New CancellationTokenSource()).ToArray(), componentTokens = components.Select(Function(source) source.Token).ToArray()
                        For index = 0 To 1 : _componentRequests(CType(index, ComponentKind)) = components(index) : Next
                        _versionProvider = Function(policy, token)
                                               If phase = 0 Then oldToken = token : Return oldGate.Task
                                               calls += 1 : newToken = token
                                               Return If(chosen = 1 OrElse chosen = 3 OrElse chosen = 6, Task.FromResult(fresh), newGate.Task)
                                           End Function
                        _versionLoadingTimingForChecks = True : _versionLoading.NowForChecks = Function() 0
                        RestoreAppearance(New JObject From {{"darkMode", dark}}) : _motionPreview = animated
                        _surfaceMotion.PauseForChecks = animated : _categoryMotion.PauseForChecks = animated
                        Dim oldWork = LoadVersionsAsync(True), oldSource = _versionCancellation
                        If changeSource Then ShowSettings(Nothing, Nothing)
                        settle() : StatusText.Text = "current catalogue" : faults.Clear() : phase = 1
                        Dim expectedMain = ActiveSurface(), expectedClock = _surfaceMotion.Transition
                        Using registration = oldToken.Register(Sub()
                                                                   Select Case chosen
                                                                       Case 0, 1, 5
                                                                           nested = LoadVersionsAsync(True) : newRequest = _versionCancellation
                                                                           If chosen = 1 Then expected = fresh
                                                                       Case 2 : VersionSourceBox.SelectedIndex = If(VersionSourceBox.SelectedIndex = 0, 2, 0)
                                                                       Case 3
                                                                           If changeSource Then ShowAbout(Nothing, Nothing) Else ShowSettings(Nothing, Nothing)
                                                                           expectedMain = ActiveSurface() : expectedClock = _surfaceMotion.Transition
                                                                       Case 4 : Throw New IOException("Controlled catalogue cancellation failure")
                                                                       Case 7 : _busy = True : StatusText.Text = "new installation owns status"
                                                                   End Select
                                                                   If chosen = 5 Then Throw New IOException("Controlled obsolete catalogue failure")
                                                               End Sub)
                            If chosen = 6 Then oldSource.Dispose()
                            Try
                                If changeSource Then VersionSourceBox.SelectedIndex = 2 Else action = LoadVersionsAsync(True)
                                Await Task.Yield()
                                If action.IsFaulted Then escaped = True
                            Catch
                                escaped = True
                            End Try
                            Dim label = "/source=" & changeSource & "/dark=" & dark & "/motion=" & animated & "/case=" & chosen
                            Dim expectedCalls = If(chosen = 0 OrElse chosen = 1 OrElse chosen = 5 OrElse (Not changeSource AndAlso (chosen = 3 OrElse chosen = 6)), 1, 0)
                            check(Not escaped AndAlso faults.Count = 0 AndAlso calls = expectedCalls AndAlso
                                If(chosen = 0 OrElse chosen = 5, _versionCancellation Is newRequest AndAlso newRequest IsNot Nothing AndAlso Not newToken.IsCancellationRequested AndAlso _loading, True) AndAlso
                                If(chosen = 0 OrElse chosen = 5, _versionLoading.Phase = If(cached, PclLoadingPresentation.Phases.Idle,
                                    If(animated, If(changeSource, PclLoadingPresentation.Phases.Hidden, PclLoadingPresentation.Phases.Waiting), PclLoadingPresentation.Phases.Loading)), True) AndAlso
                                If(chosen = 3, ActiveSurface() Is expectedMain AndAlso _surfaceMotion.Transition Is expectedClock, True) AndAlso
                                If(chosen = 4, StatusText.Text.Contains("刷新失败") AndAlso _versionsDirty, True) AndAlso
                                If(chosen = 7, StatusText.Text = "new installation owns status", True),
                                "catalogue start or source change retains newer readers and contains cancellation observer failures" & label)
                            newGate.TrySetResult(fresh) : oldGate.TrySetResult(sample)
                            Try : Await Task.WhenAll(oldWork, nested, action) : Catch : escaped = True : End Try
                            Await Task.Yield() : settle()
                            If chosen = 0 OrElse chosen = 1 OrElse chosen = 5 OrElse (Not changeSource AndAlso (chosen = 3 OrElse chosen = 6)) Then expected = fresh
                            check(Not escaped AndAlso faults.Count = 0 AndAlso _versions Is expected AndAlso Not _loading AndAlso _versionCancellation Is Nothing AndAlso
                                (chosen = 6 OrElse oldToken.IsCancellationRequested) AndAlso ActiveSurface() Is expectedMain AndAlso PageSurface.IsHitTestVisible AndAlso
                                If(chosen = 7, StatusText.Text = "new installation owns status", True) AndAlso If(changeSource, componentTokens.All(Function(token) token.IsCancellationRequested), True) AndAlso _taskGeneration = taskGeneration,
                                "late manifests preserve the accepted catalogue, page and task while source changes retire captured components" & label)
                            _busy = False : _versionsDirty = False : _motionPreview = False
                            _versionProvider = Function(policy, token) Task.FromResult(sample.ToList())
                            ShowDownload(Nothing, Nothing) : Await LoadVersionsAsync(True) : settle()
                            check(_versions.Count = sample.Count AndAlso Not _versionsDirty AndAlso Not _loading AndAlso _versionCancellation Is Nothing AndAlso
                                _versionLoading.Phase = PclLoadingPresentation.Phases.Idle AndAlso Not _versionLoading.TimerArmed AndAlso VersionLoadingCard.Body Is Nothing AndAlso
                                PageSurface.IsHitTestVisible AndAlso _surfaceMotion.Transition Is Nothing AndAlso _categoryMotion.Transition Is Nothing AndAlso _taskGeneration = taskGeneration,
                                "catalogue retry restores usable rows and releases loading presentation without creating an install task" & label)
                        End Using
                        CancelComponentRequests()
                        For Each source In components : source.Dispose() : Next
                    Next
                Next
            Next
        Next
    End Function
End Class
