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
    Public Sub VerifyResourceBackUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim focus = LocalModCheckGetNativeFocus(), observations As New JObject, failures As New List(Of Exception)
        Dim host As New Border With {.Width = 900, .Height = 680}, layout = ControlLayout(host)
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                     File.WriteAllText(Path.Combine(directory, "resource-back.json"), observations.ToString())
                                                 End Sub
        Dim capture As DispatcherUnhandledExceptionEventHandler = Sub(sender, args)
                                                                     failures.Add(args.Exception) : args.Handled = True
                                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        AddHandler owner.Dispatcher.UnhandledException, capture
        Try
            Using surface = PreviewSurface.Attach(host, 900, 680)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckResourceBackAsync(layout, check, failures)
                If Not PumpMotionUntil(Function() work.IsCompleted, 90000) Then Throw New TimeoutException("Resource return checks did not settle.")
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

    Private Function PrepareResourceBackDetail(project As ModpackProject) As ResourceDetailState
        _packProject = project : _packVerifiedProject = project : _packDetailCompletedProject = project
        _packTargetGame = "1.20.1" : _resourceTargetLoader = ""
        _packReleases = New List(Of ModpackRelease) From {
            New ModpackRelease With {.Platform = project.Platform, .Kind = project.Kind, .ProjectId = project.Id, .Id = project.Id & "-a", .Name = "First", .GameVersions = {"1.20.1"}},
            New ModpackRelease With {.Platform = project.Platform, .Kind = project.Kind, .ProjectId = project.Id, .Id = project.Id & "-b", .Name = "Second", .GameVersions = {"1.21.1"}}}
        RenderPackProject(project) : ChangePackMode(PackBrowserMode.Detail) : InitializePackVersions()
        _packGameFilter = "" : RenderPackVersions()
        Return CaptureResourceDetail()
    End Function

    Private Async Function CheckResourceBackAsync(layout As Action, check As Action(Of Boolean, String), failures As List(Of Exception)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion() : layout()
                               End Sub
        For Each kind In {CommunityResourceKind.Mod, CommunityResourceKind.Shader}
            For Each history In {False, True}
                For origin = 0 To If(history, 2, 1)
                    For scenario = 0 To 5
                        _motionPreview = False : _surfaceMotion.PauseForChecks = False : _categoryMotion.PauseForChecks = False : _packDetailMotion.PauseForChecks = False
                        CancelResourceRequests() : ShowDownload(Nothing, Nothing) : SelectResourceKind(kind) : ChangePackMode(PackBrowserMode.Search) : settle()
                        PackSearchText.Text = "original query" : PackSearchSource.SelectedIndex = 2
                        PackSearchGame.Text = "全部 (也可自行输入)" : PackSearchCategory.SelectedIndex = 0 : ResourceLoader.SelectedIndex = 0
                        _packSearchSession = New ModpackSearchSession(CurrentPackQuery()) : _packLastQuery = PackQueryKey(CurrentPackQuery()) : _packLastSearch = DateTime.UtcNow
                        Dim originalSearch As New SearchStartState(kind, "Original", 40)
                        originalSearch.Gate.SetResult(True)
                        _packCatalogFactory = Function() New ModpackCatalogClient(New SearchStartHandler(originalSearch), "", 0)
                        Await LoadPackSearchPageAsync(0) : settle()
                        Dim session = _packSearchSession, taskGeneration = _taskGeneration
                        Dim grand As New ModpackProject With {.Kind = kind, .Platform = ModpackPlatform.Modrinth, .Id = "grand", .Name = "Grandparent"}
                        Dim parent As New ModpackProject With {.Kind = kind, .Platform = grand.Platform, .Id = "parent", .Name = "Parent"}
                        Dim project As New ModpackProject With {.Kind = kind, .Platform = grand.Platform, .Id = "current", .Name = "Current"}
                        Dim grandFrame = PrepareResourceBackDetail(grand), parentFrame = PrepareResourceBackDetail(parent)
                        PrepareResourceBackDetail(project) : _resourceBack.Clear()
                        If history Then _resourceBack.Push(grandFrame) : _resourceBack.Push(parentFrame)
                        settle()
                        Dim old As New DetailStartState(kind, "Retired detail", "retired1"), oldRead As Task = Task.CompletedTask
                        If origin = 2 Then
                            _packCatalogFactory = Function() New ModpackCatalogClient(New DetailStartHandler(old), "", 0)
                            oldRead = LoadPackDetailsAsync(project) : Await WaitPackUiAsync(Function() old.Calls > 0) : settle()
                        End If
                        Dim retiredLoading = PackVersionCards.Children.OfType(Of PclCard)().Select(Function(card) TryCast(card.Body, PclLoadingView)).FirstOrDefault(Function(view) view IsNot Nothing)
                        RestoreAppearance(New JObject From {{"darkMode", scenario Mod 2 = 0}})
                        Dim animated = scenario \ 2 Mod 2 = 0, chosenCase = scenario
                        _motionPreview = animated : _surfaceMotion.PauseForChecks = animated : _categoryMotion.PauseForChecks = animated : _packDetailMotion.PauseForChecks = animated
                        Dim latest As New DetailStartState(kind, "Latest detail", "latest01"), latestSearch As New SearchStartState(kind, "Latest", 40)
                        Dim other As New ModpackProject With {.Kind = kind, .Platform = project.Platform, .Id = "latest", .Name = "Newer detail"}
                        Dim expectedProject = _packProject, expectedSession = _packSearchSession, expectedFrames = _resourceBack.ToArray()
                        Dim expectedMode = _packMode, expectedSurface = ActiveSurface(), expectedNames As CancellationTokenSource = Nothing
                        Dim expectedToken As CancellationToken, expectedCanceled As Boolean
                        Dim detailClock As ClockGroup = Nothing, categoryClock As ClockGroup = Nothing, surfaceClock As ClockGroup = Nothing
                        Dim escaped = False, failedLookupCompleted = False
                        Dim label = "/kind=" & kind.ToString() & "/history=" & history & "/origin=" & origin & "/case=" & scenario
                        failures.Clear()
                        Using dependency As New CancellationTokenSource()
                            If origin = 0 Then _dependencyRequest = dependency
                            Dim token = If(origin = 0, dependency.Token, If(origin = 1, _resourceNamesRequest.Token, _packDetailRequest.Token))
                            Using registration = token.Register(Sub()
                                                                    Select Case chosenCase
                                                                        Case 0
                                                                            _packCatalogFactory = Function() New ModpackCatalogClient(New DetailStartHandler(latest), "", 0)
                                                                            OpenPackDetails(other)
                                                                        Case 1
                                                                            _packCatalogFactory = Function() New ModpackCatalogClient(New SearchStartHandler(latestSearch), "", 0)
                                                                            PackSearchText.Text = "latest query" : RunPackSearch(Nothing, Nothing)
                                                                        Case 2 : ShowSettings(Nothing, Nothing)
                                                                        Case 3 : Throw New IOException("Controlled resource return cancellation failure")
                                                                        Case 4 : BackToPackSearch(Nothing, Nothing)
                                                                        Case 5
                                                                            OpenResourceDependency(ActivePackProject(), New ResourceDependency With {.ProjectId = "invalid id"}, PackProjectIntro)
                                                                            failedLookupCompleted = _dependencyRequest Is Nothing AndAlso PackProjectActionStatus.IsVisible AndAlso PackProjectActionStatus.Text.Contains("无法获取前置")
                                                                    End Select
                                                                    expectedProject = _packProject : expectedSession = _packSearchSession : expectedFrames = _resourceBack.ToArray()
                                                                    expectedMode = _packMode : expectedSurface = ActiveSurface() : expectedNames = _resourceNamesRequest
                                                                    expectedToken = expectedNames.Token : expectedCanceled = expectedToken.IsCancellationRequested
                                                                    detailClock = _packDetailMotion.Transition : categoryClock = _categoryMotion.Transition : surfaceClock = _surfaceMotion.Transition
                                                                End Sub)
                                Try
                                    BackToPackSearch(Nothing, Nothing)
                                Catch
                                    escaped = True
                                End Try
                                layout()
                                check(Not escaped AndAlso failures.Count = 0 AndAlso (chosenCase <> 5 OrElse failedLookupCompleted) AndAlso _packProject Is expectedProject AndAlso _packSearchSession Is expectedSession AndAlso
                                    _packMode = expectedMode AndAlso ActiveSurface() Is expectedSurface AndAlso _resourceBack.SequenceEqual(expectedFrames) AndAlso
                                    If(chosenCase = 3, PackProjectActionStatus.IsVisible AndAlso PackProjectActionStatus.Text.Contains("重试"),
                                       _resourceNamesRequest Is expectedNames AndAlso expectedToken.IsCancellationRequested = expectedCanceled AndAlso
                                       _packDetailMotion.Transition Is detailClock AndAlso _categoryMotion.Transition Is categoryClock AndAlso _surfaceMotion.Transition Is surfaceClock),
                                    "return cancellation preserves newer detail, search, main page, history and entry clocks; owned faults keep retry feedback" & label)
                                old.Gate.TrySetResult(True) : latest.Gate.TrySetResult(True) : latestSearch.Gate.TrySetResult(True)
                                Await oldRead : Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing AndAlso _packSearchRequest Is Nothing)
                                Await Task.Yield() : settle()
                                Dim published = If(chosenCase = 0, _packDetailCompletedProject Is other AndAlso ActivePackProject().Name = latest.Name AndAlso _packReleases.Single().Id = latest.VersionId,
                                    If(chosenCase = 1, PackProjectRows.Children.OfType(Of PclResourceItem)().Count() = 40 AndAlso
                                       PackProjectRows.Children.OfType(Of PclResourceItem)().All(Function(row) DirectCast(row.Tag, ModpackProject).Name.StartsWith("Latest", StringComparison.Ordinal)),
                                       chosenCase <> 4 OrElse Not history OrElse (_packProject Is parent AndAlso _packGameFilter = "" AndAlso PackVersionCards.Children.Count = 2)))
                                check(failures.Count = 0 AndAlso _packProject Is expectedProject AndAlso _packSearchSession Is expectedSession AndAlso _packMode = expectedMode AndAlso
                                    ActiveSurface() Is expectedSurface AndAlso _resourceBack.SequenceEqual(expectedFrames) AndAlso published,
                                    "late retired details cannot replace accepted results, consume another return frame or reopen hidden pages" & label)
                            End Using
                            If _dependencyRequest Is dependency Then _dependencyRequest = Nothing
                        End Using
                        _surfaceMotion.PauseForChecks = False : _categoryMotion.PauseForChecks = False : _packDetailMotion.PauseForChecks = False
                        ShowDownload(Nothing, Nothing) : settle()
                        Dim resumed = origin <> 2 OrElse (chosenCase <> 2 AndAlso chosenCase <> 3 AndAlso chosenCase <> 5) OrElse
                            (PackDetailRetry.IsVisible AndAlso PackDetailHint.IsVisible AndAlso
                             Not PackVersionCards.Children.OfType(Of PclCard)().Any(Function(card) TypeOf card.Body Is PclLoadingView) AndAlso retiredLoading IsNot Nothing AndAlso retiredLoading.Transition Is Nothing)
                        If origin = 2 AndAlso (chosenCase = 2 OrElse chosenCase = 3 OrElse chosenCase = 5) AndAlso resumed Then
                            Dim recovered As New DetailStartState(kind, "Recovered detail", "recover01")
                            recovered.Gate.SetResult(True)
                            _packCatalogFactory = Function() New ModpackCatalogClient(New DetailStartHandler(recovered), "", 0)
                            RetryPackDetails(Nothing, Nothing) : Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing) : settle()
                            resumed = recovered.Calls = 1 AndAlso _packDetailCompletedProject Is project AndAlso _packReleases.Count = 1 AndAlso
                                _packReleases(0).Id = recovered.VersionId AndAlso Not PackDetailRetry.IsVisible AndAlso Not PackDetailHint.IsVisible
                        End If
                        Dim retried = True
                        If chosenCase = 3 Then
                            BackToPackSearch(Nothing, Nothing) : settle()
                            retried = If(history, _packProject Is parent AndAlso _resourceBack.Count = 1 AndAlso _packGameFilter = "" AndAlso Not PackDetailRetry.IsVisible, _packMode = PackBrowserMode.Search)
                        End If
                        For count = 0 To 2
                            If _packMode <> PackBrowserMode.Detail Then Exit For
                            BackToPackSearch(Nothing, Nothing) : settle()
                        Next
                        check(resumed AndAlso retried AndAlso _packMode = PackBrowserMode.Search AndAlso _resourceBack.Count = 0 AndAlso _packSearchSession Is If(chosenCase = 1, expectedSession, session) AndAlso
                            PackProjectRows.Children.OfType(Of PclResourceItem)().Count() = 40 AndAlso PageSurface.IsHitTestVisible AndAlso DownloadViewHost.IsHitTestVisible AndAlso
                            PackSearchResultsHost.IsHitTestVisible AndAlso _categoryMotion.Transition Is Nothing AndAlso _packDetailMotion.Transition Is Nothing AndAlso _taskGeneration = taskGeneration,
                            "explicit return retry restores cached search and settled input without another query or download" & label)
                    Next
                Next
            Next
        Next
    End Function
End Class
