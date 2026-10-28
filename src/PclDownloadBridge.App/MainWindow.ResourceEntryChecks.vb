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
    Public Sub VerifyResourceEntryUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim focus = LocalModCheckGetNativeFocus(), observations As New JObject, failures As New List(Of Exception)
        Dim host As New Border With {.Width = 900, .Height = 700}, layout = ControlLayout(host)
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                     File.WriteAllText(Path.Combine(directory, "resource-entry.json"), observations.ToString())
                                                 End Sub
        Dim capture As DispatcherUnhandledExceptionEventHandler = Sub(sender, args)
                                                                     failures.Add(args.Exception) : args.Handled = True
                                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        AddHandler owner.Dispatcher.UnhandledException, capture
        Try
            Using surface = PreviewSurface.Attach(host, 900, 700)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckResourceEntryAsync(directory, layout, check, failures)
                If Not PumpMotionUntil(Function() work.IsCompleted, 180000) Then Throw New TimeoutException("Resource entry checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            Dim failed = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failed.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failed))
        Finally
            RemoveHandler owner.Dispatcher.UnhandledException, capture
            owner._closing = True : owner.CancelResourceRequests()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner.FinishPackContentMotion()
            host.Child = Nothing : owner.Content = original
            SynchronizationContext.SetSynchronizationContext(previous) : LocalModCheckSetNativeFocus(focus)
        End Try
    End Sub

    Private Async Function CheckResourceEntryAsync(directory As String, layout As Action, check As Action(Of Boolean, String), failures As List(Of Exception)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion() : layout()
                               End Sub
        For Each kind In {CommunityResourceKind.Mod, CommunityResourceKind.Shader}
            For route = 0 To 1
                For origin = 0 To 2
                    For scenario = 0 To 5
                        _motionPreview = False : _surfaceMotion.PauseForChecks = False : _categoryMotion.PauseForChecks = False : _packDetailMotion.PauseForChecks = False : _packResultMotion.PauseForChecks = False
                        CancelResourceRequests() : _resourceBrowsers.Clear() : ShowDownload(Nothing, Nothing) : SelectResourceKind(kind) : ChangePackMode(PackBrowserMode.Search) : settle()
                        PackSearchText.Text = "original entry query" : PackSearchSource.SelectedIndex = 2 : PackSearchGame.Text = "全部 (也可自行输入)"
                        PackSearchCategory.SelectedIndex = 0 : ResourceLoader.SelectedIndex = 0
                        _packSearchSession = New ModpackSearchSession(CurrentPackQuery()) : _packLastQuery = PackQueryKey(CurrentPackQuery()) : _packLastSearch = DateTime.UtcNow
                        Dim initial As New SearchStartState(kind, "Original", 40)
                        initial.Gate.SetResult(True) : _packCatalogFactory = Function() New ModpackCatalogClient(New SearchStartHandler(initial), "", 0)
                        Await LoadPackSearchPageAsync(0) : settle()
                        Dim parent As New ModpackProject With {.Kind = kind, .Platform = ModpackPlatform.Modrinth, .Id = "parent", .Name = "Parent entry"}
                        Dim grand As New ModpackProject With {.Kind = kind, .Platform = parent.Platform, .Id = "grand", .Name = "Grandparent"}
                        Dim incoming As New ModpackProject With {.Kind = kind, .Platform = parent.Platform, .Id = "incoming", .Name = "Incoming entry"}
                        Dim grandFrame = PrepareResourceBackDetail(grand)
                        Dim prepare As Action = Sub()
                                                    PrepareResourceBackDetail(parent) : _resourceBack.Clear()
                                                    _packReleases(0).Dependencies = {New ResourceDependency With {.ProjectId = "dep1"}}
                                                    _resourceDependencyNames("Modrinth:dep1") = "Dependency entry"
                                                    _packTargetGame = ""
                                                    InitializePackVersions() : _packVersionCache("1.20.1").Collapsed = False : settle()
                                                End Sub
                        prepare()
                        Dim parentReleases = _packReleases, chosenCase = scenario
                        If route = 0 Then ChangePackMode(PackBrowserMode.Search) Else _resourceBack.Push(grandFrame)
                        settle()
                        Dim sources = Enumerable.Range(0, 3).Select(Function(index) New CancellationTokenSource()).ToArray(), tokens = sources.Select(Function(source) source.Token).ToArray()
                        _resourceNamesRequest?.Dispose() : _resourceNamesRequest = sources(1)
                        If route = 0 Then _dependencyRequest = sources(0) Else _packDetailRequest = sources(0)
                        _packDetailArtworkRequest = sources(2) : _packDetailArtworkRow = ArtworkHeader()
                        Dim animated = (origin + scenario + route) Mod 2 = 0, taskGeneration = _taskGeneration
                        RestoreAppearance(New JObject From {{"darkMode", (origin + scenario) Mod 2 = 0}}) : _motionPreview = animated
                        _surfaceMotion.PauseForChecks = animated : _categoryMotion.PauseForChecks = animated : _packDetailMotion.PauseForChecks = animated : _packResultMotion.PauseForChecks = animated
                        Dim obsolete As New DetailStartState(kind, "Obsolete entry", "obsolete1") : obsolete.Gate.SetResult(True)
                        Dim latest As DetailStartState = Nothing, search As SearchStartState = Nothing, target As ModpackProject = Nothing
                        Dim live As CancellationTokenSource = Nothing, liveToken As CancellationToken
                        Dim expectedProject = _packProject, expectedSession = _packSearchSession, expectedKind = _resourceKind, expectedMode = _packMode
                        Dim expectedMain = ActiveSurface(), expectedFrames = _resourceBack.ToArray()
                        Dim categoryClock As ClockGroup = Nothing, detailClock As ClockGroup = Nothing, mainClock As ClockGroup = Nothing
                        Dim label = "/kind=" & kind.ToString() & "/route=" & route & "/origin=" & origin & "/case=" & scenario
                        failures.Clear()
                        Using registration = tokens(origin).Register(Sub()
                                                                         If chosenCase = 5 Then Throw New IOException("Controlled project entry cancellation failure")
                                                                         settle()
                                                                         Select Case chosenCase
                                                                             Case 0
                                                                                 search = New SearchStartState(_resourceKind, "LatestEntry", 40)
                                                                                 _packCatalogFactory = Function() New ModpackCatalogClient(New SearchStartHandler(search), "", 0)
                                                                                 PackSearchText.Text = "latest entry query" : RunPackSearch(Nothing, Nothing) : live = _packSearchRequest
                                                                             Case 1
                                                                                 latest = New DetailStartState(_resourceKind, "Latest entry project", "latest01")
                                                                                 target = New ModpackProject With {.Kind = _resourceKind, .Platform = ModpackPlatform.Modrinth, .Id = "latest", .Name = latest.Name}
                                                                                 _packCatalogFactory = Function() New ModpackCatalogClient(New DetailStartHandler(latest), "", 0)
                                                                                 OpenPackDetails(target) : live = _packDetailRequest
                                                                             Case 2 : ShowSettings(Nothing, Nothing)
                                                                             Case 3 : SelectResourceKind(CommunityResourceKind.DataPack)
                                                                             Case 4 : BackToPackSearch(Nothing, Nothing)
                                                                         End Select
                                                                         If live IsNot Nothing Then liveToken = live.Token
                                                                         expectedProject = _packProject : expectedSession = _packSearchSession : expectedKind = _resourceKind : expectedMode = _packMode
                                                                         expectedMain = ActiveSurface() : expectedFrames = _resourceBack.ToArray()
                                                                         categoryClock = _categoryMotion.Transition : detailClock = _packDetailMotion.Transition : mainClock = _surfaceMotion.Transition
                                                                         _packCatalogFactory = Function() New ModpackCatalogClient(New DetailStartHandler(obsolete), "", 0)
                                                                     End Sub)
                            If route = 0 Then
                                _packCatalogFactory = Function() New ModpackCatalogClient(New DetailStartHandler(obsolete), "", 0)
                                OpenPackDetails(incoming)
                            Else
                                Dim lookup As New DependencyHandoffState(kind)
                                For Each gate In lookup.Gates.Values : gate.SetResult(True) : Next
                                _packCatalogFactory = Function() New ModpackCatalogClient(New DependencyHandoffHandler(lookup), "", 0)
                                Dim link = DirectCast(_packVersionCache("1.20.1").Body, StackPanel).Children.OfType(Of WrapPanel)().SelectMany(Function(panel) panel.Children.OfType(Of PclButton)()).Single()
                                OpenResourceDependency(parent, DirectCast(link.Tag, ResourceDependency), link)
                            End If
                            Await Task.Yield() : layout()
                            Dim feedback = If(_packMode = PackBrowserMode.Detail, PackProjectActionStatus.Text, PackSearchInputHint.Text)
                            check(failures.Count = 0 AndAlso _packProject Is expectedProject AndAlso _packSearchSession Is expectedSession AndAlso _resourceKind = expectedKind AndAlso _packMode = expectedMode AndAlso
                                ActiveSurface() Is expectedMain AndAlso _resourceBack.SequenceEqual(expectedFrames) AndAlso
                                If(scenario = 4 AndAlso route = 1, _packProject Is grand AndAlso _resourceBack.Count = 0, True) AndAlso
                                If(scenario = 5, feedback.Contains("重试"), _categoryMotion.Transition Is categoryClock AndAlso _packDetailMotion.Transition Is detailClock AndAlso _surfaceMotion.Transition Is mainClock) AndAlso
                                If(scenario < 2, live IsNot Nothing AndAlso Not liveToken.IsCancellationRequested, True) AndAlso obsolete.Calls = 0,
                                "resource entry cannot overwrite a newer query, project, return, category or main page and contains cancellation faults" & label)
                            latest?.Gate.TrySetResult(True) : search?.Gate.TrySetResult(True)
                            Await WaitPackUiAsync(Function() (_packDetailRequest Is Nothing OrElse _packDetailRequest Is sources(0)) AndAlso _packSearchRequest Is Nothing)
                            Await Task.Yield() : settle()
                            Dim published = If(scenario = 0, PackProjectRows.Children.OfType(Of PclResourceItem)().Count() = 40 AndAlso
                                PackProjectRows.Children.OfType(Of PclResourceItem)().All(Function(row) DirectCast(row.Tag, ModpackProject).Name.StartsWith("Latest", StringComparison.Ordinal)),
                                scenario <> 1 OrElse (_packDetailCompletedProject Is target AndAlso _packReleases.Count = 1 AndAlso _packReleases(0).Id = latest.VersionId))
                            File.WriteAllText(Path.Combine(directory, "entry-result-" & kind.ToString() & "-" & route & "-" & origin & "-" & scenario & ".json"), New JObject From {
                                {"Published", published}, {"Rows", PackProjectRows.Children.OfType(Of PclResourceItem)().Count()}, {"FirstRow", PackProjectRows.Children.OfType(Of PclResourceItem)().Select(Function(row) DirectCast(row.Tag, ModpackProject).Name).FirstOrDefault()},
                                {"Cancelled", New JArray(tokens.Select(Function(token) token.IsCancellationRequested))}, {"Failures", failures.Count}, {"Project", _packProject Is expectedProject},
                                {"Mode", _packMode = expectedMode}, {"Session", _packSearchSession Is expectedSession}, {"Kind", _resourceKind = expectedKind}, {"Main", ActiveSurface() Is expectedMain},
                                {"Frames", _resourceBack.SequenceEqual(expectedFrames)}, {"Task", _taskGeneration = taskGeneration}}.ToString())
                            check(tokens.All(Function(token) token.IsCancellationRequested) AndAlso failures.Count = 0 AndAlso _packProject Is expectedProject AndAlso _packMode = expectedMode AndAlso _packSearchSession Is expectedSession AndAlso
                                _resourceKind = expectedKind AndAlso ActiveSurface() Is expectedMain AndAlso _resourceBack.SequenceEqual(expectedFrames) AndAlso published AndAlso _taskGeneration = taskGeneration,
                                "all captured readers retire after a fault while accepted results and exact return frames survive late work" & label)
                        End Using
                        If _dependencyRequest Is sources(0) Then _dependencyRequest = Nothing
                        If _packDetailRequest Is sources(0) Then _packDetailRequest = Nothing
                        If _resourceNamesRequest Is sources(1) Then _resourceNamesRequest = Nothing
                        If _packDetailArtworkRequest Is sources(2) Then _packDetailArtworkRequest = Nothing : _packDetailArtworkRow = Nothing
                        For Each source In sources : CancelSubmittedSearchRequest(source) : source.Dispose() : Next
                        _surfaceMotion.PauseForChecks = False : _categoryMotion.PauseForChecks = False : _packDetailMotion.PauseForChecks = False : _packResultMotion.PauseForChecks = False
                        ShowDownload(Nothing, Nothing) : SelectResourceKind(kind) : settle()
                        If scenario <> 5 OrElse _packProject IsNot parent OrElse Not _packVersionCache.ContainsKey("1.20.1") Then prepare() : parentReleases = _packReleases
                        Dim recovered As New DetailStartState(kind, "Recovered entry", "retry001") : recovered.Gate.SetResult(True)
                        _packCatalogFactory = Function() New ModpackCatalogClient(New DetailStartHandler(recovered), "", 0)
                        Dim history = _resourceBack.ToArray(), accepted As Boolean
                        If route = 0 Then
                            OpenPackDetails(incoming)
                            Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing) : settle()
                            accepted = _packProject Is incoming AndAlso _packDetailCompletedProject Is incoming AndAlso _packReleases.Count = 1 AndAlso _packReleases(0).Id = recovered.VersionId
                            NavigateTitleBack() : settle()
                            accepted = accepted AndAlso _packMode = PackBrowserMode.Search AndAlso _resourceBack.Count = 0
                        Else
                            ' Reuse the still-visible parent after cancellation failure;
                            ' successful retry must add and consume exactly one frame.
                            Dim link = DirectCast(_packVersionCache("1.20.1").Body, StackPanel).Children.OfType(Of WrapPanel)().SelectMany(Function(panel) panel.Children.OfType(Of PclButton)()).Single()
                            OpenResourceDependency(parent, DirectCast(link.Tag, ResourceDependency), link)
                            Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing AndAlso _dependencyRequest Is Nothing) : settle()
                            accepted = _packProject?.Id = "dep1" AndAlso _packDetailCompletedProject Is _packProject AndAlso _packReleases.Count = 1 AndAlso _packReleases(0).Id = recovered.VersionId
                            NavigateTitleBack() : settle()
                            accepted = accepted AndAlso _packProject Is parent AndAlso _packReleases Is parentReleases AndAlso _packMode = PackBrowserMode.Detail AndAlso _resourceBack.SequenceEqual(history)
                            If scenario = 5 AndAlso origin = 1 Then
                                While _resourceBack.Count < 32 : _resourceBack.Push(grandFrame) : End While
                                Dim limitFrames = _resourceBack.ToArray(), limitNames = _resourceNamesRequest, limitToken = limitNames.Token
                                Using pending As New CancellationTokenSource()
                                    _packDetailRequest = pending
                                    link = DirectCast(_packVersionCache("1.20.1").Body, StackPanel).Children.OfType(Of WrapPanel)().SelectMany(Function(panel) panel.Children.OfType(Of PclButton)()).Single()
                                    OpenResourceDependency(parent, DirectCast(link.Tag, ResourceDependency), link)
                                    Await WaitPackUiAsync(Function() _dependencyRequest Is Nothing) : settle()
                                    accepted = accepted AndAlso Not pending.IsCancellationRequested AndAlso _packDetailRequest Is pending AndAlso _resourceNamesRequest Is limitNames AndAlso Not limitToken.IsCancellationRequested AndAlso
                                        _packProject Is parent AndAlso _packReleases Is parentReleases AndAlso _resourceBack.SequenceEqual(limitFrames) AndAlso CStr(PackProjectActionStatus.ToolTip).Contains("层级")
                                    _packDetailRequest = Nothing
                                End Using
                                _resourceBack.Clear() : For Each frame In history.Reverse() : _resourceBack.Push(frame) : Next
                            End If
                        End If
                        check(accepted AndAlso PackSearchResultsHost.IsHitTestVisible AndAlso PageSurface.IsHitTestVisible AndAlso
                            _taskGeneration = taskGeneration AndAlso Not _busy AndAlso parentReleases.Count = 2,
                            "entry retry and title return retain cache data, input and task ownership without a stale history frame" & label)
                    Next
                Next
            Next
        Next
    End Function
End Class
