Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyResourceRestoreUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim host As New Border With {.Width = 900, .Height = 550}, layout = ControlLayout(host), observations As New JObject
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 550)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckResourceRestoreAsync(directory, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 30000) Then Throw New TimeoutException("Resource restore did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "resource-restore.json"), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            owner._closing = True : owner.CancelResourceRequests() : owner.CancelManagementRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish()
            host.Child = Nothing : owner.Content = original : SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub

    Private Async Function CheckResourceRestoreAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        ShowDownload(Nothing, Nothing) : SetDownloadView(DownloadView.Modpacks) : _surfaceMotion.Finish() : _categoryMotion.Finish()
        Dim project As New ModpackProject With {.Platform = ModpackPlatform.Modrinth, .Kind = CommunityResourceKind.Modpack, .Id = "restore", .Name = "Restore fixture"}
        Dim releases = New List(Of ModpackRelease) From {
            New ModpackRelease With {.Platform = project.Platform, .Kind = project.Kind, .ProjectId = project.Id, .Id = "one", .Name = "one", .GameVersions = {"1.20.1"}},
            New ModpackRelease With {.Platform = project.Platform, .Kind = project.Kind, .ProjectId = project.Id, .Id = "two", .Name = "two", .GameVersions = {"1.21.1"}}}
        For Each dark In {False, True}
            RestoreAppearance(New JObject From {{"darkMode", dark}})
            _packProject = project : _packVerifiedProject = project : _packDetailCompletedProject = project : _packReleases = releases
            _packTargetGame = "1.20.1" : ChangePackMode(PackBrowserMode.Detail) : InitializePackVersions() : layout()
            check(_packGameFilter = "1.20.1", "first detail entry still selects the accepted search game/dark=" & dark)
            Dim all = PackVersionFilters.Children.OfType(Of PclRadioButton)().Single(Function(button) CStr(button.Tag) = "")
            SelectPackVersionFilter(all) : layout()
            Dim frame = CaptureResourceDetail()
            check(_packGameFilter = "" AndAlso PackVersionCards.Children.Count = 2, "explicit All includes both game groups before navigation/dark=" & dark)
            RestoreResourceDetail(frame) : layout()
            check(_packGameFilter = "" AndAlso PackVersionCards.Children.Count = 2 AndAlso PackVersionFilters.Children.OfType(Of PclRadioButton)().Single(Function(button) CStr(button.Tag) = "").Checked,
                "restoring a completed detail preserves explicit All rather than reapplying its search game/dark=" & dark)
            ' The actual dependency-back path uses the same saved detail frame.
            _resourceBack.Push(frame)
            _packProject = New ModpackProject With {.Platform = project.Platform, .Kind = CommunityResourceKind.Mod, .Id = "dependency", .Name = "Dependency"}
            BackToPackSearch(Nothing, Nothing) : layout()
            check(_packProject Is project AndAlso _packGameFilter = "" AndAlso PackVersionCards.Children.Count = 2,
                "returning from a dependency preserves the parent's explicit All selection/dark=" & dark)
            SelectPackVersionFilter(PackVersionFilters.Children.OfType(Of PclRadioButton)().Single(Function(button) CStr(button.Tag) = "1.21.1"))
            frame = CaptureResourceDetail() : RestoreResourceDetail(frame) : layout()
            check(_packGameFilter = "1.21.1" AndAlso PackVersionCards.Children.Count = 1, "restoring a nonempty version filter remains unchanged/dark=" & dark)
        Next
        _packGameFilter = "" : RenderPackVersions()
        SelectResourceKind(CommunityResourceKind.Shader) : _categoryMotion.Finish() : layout()
        SelectResourceKind(CommunityResourceKind.Modpack) : _categoryMotion.Finish() : layout()
        check(_packProject Is project AndAlso _packGameFilter = "" AndAlso PackVersionCards.Children.Count = 2,
            "an actual resource-category round trip retains All and the exact completed project")

        ChangePackMode(PackBrowserMode.Search) : _categoryMotion.Finish() : layout()
        For Each animated In {False, True}
            _motionPreview = animated : _packLoadingTimingForChecks = animated
            Dim handler As New RestoreSearchHandler
            _packSearchSession = New ModpackSearchSession(New ModpackQuery With {.Platforms = ModpackPlatform.CurseForge})
            _packCatalogFactory = Function() New ModpackCatalogClient(handler, "", 0)
            _packSearchScroll = 360
            RestoreResourceSearchAsync(0)
            Await WaitPackUiAsync(Function() handler.Entered)
            CancelResourceRequests()
            SetRestoreSearchRows(layout) : ModpackPage.ScrollToVerticalOffset(80) : layout()
            Dim offset = ModpackPage.VerticalOffset, session = _packSearchSession
            handler.Gate.SetResult(True)
            Await WaitPackUiAsync(Function() handler.Disposed)
            ' Dispatcher idle drains the queued async-Sub return and scroll command.
            FlushControlLayout() : layout()
            check(offset > 0 AndAlso Math.Abs(ModpackPage.VerticalOffset - offset) < 0.5,
                "a cancelled cache restore cannot overwrite the current reading position/animated=" & animated)
            check(_packSearchSession Is session AndAlso PackProjectRows.Children.Count = 40 AndAlso _packSearchRequest Is Nothing,
                "retired cache restores preserve replacement rows and request ownership/animated=" & animated)
        Next
        _motionPreview = False : _packLoadingTimingForChecks = False
        Dim ready As New RestoreSearchHandler With {.Immediate = True}
        _packSearchSession = New ModpackSearchSession(New ModpackQuery With {.Platforms = ModpackPlatform.CurseForge})
        _packCatalogFactory = Function() New ModpackCatalogClient(ready, "", 0)
        _packSearchScroll = 240 : RestoreResourceSearchAsync(0)
        Await WaitPackUiAsync(Function() ready.Disposed AndAlso _packSearchRequest Is Nothing) : FlushControlLayout() : layout()
        File.WriteAllText(Path.Combine(directory, "resource-restore-viewport.json"), New JObject From {
            {"Rows", PackProjectRows.Children.Count}, {"Offset", ModpackPage.VerticalOffset}, {"Extent", ModpackPage.ExtentHeight}, {"Scrollable", ModpackPage.ScrollableHeight},
            {"SessionCount", _packSearchSession.Count}, {"Error", _packSearchSession.ErrorMessage}, {"Mode", _packMode.ToString()}, {"Saved", _packSearchScroll}}.ToString())
        check(PackProjectRows.Children.Count = 40 AndAlso Math.Abs(ModpackPage.VerticalOffset - 240) < 0.5,
            "the accepted cache restore still restores its saved viewport after laying out returned rows")
        check(ActivePackProject() Is project AndAlso _packReleases.Count = 2,
            "search restoration does not mutate the retained detail or its installable release identities")
        Await CheckCategoryRestoreContinuationAsync(directory, layout, check)
    End Function
    Private Async Function CheckCategoryRestoreContinuationAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion() : layout()
                               End Sub
        Dim observations As New JArray
        For Each dark In {False, True}
            For Each animated In {False, True}
                _motionPreview = False : CancelResourceRequests() : _resourceBrowsers.Clear() : _resourceBack.Clear()
                _packCatalogFactory = Function() FailingPackCatalog(New IOException("Offline category restore fixture"))
                ShowDownload(Nothing, Nothing) : SelectResourceKind(CommunityResourceKind.Mod) : settle()
                Dim parent As New ModpackProject With {.Platform = ModpackPlatform.Modrinth, .Kind = CommunityResourceKind.Mod, .Id = "categoryparent", .Name = "Cached parent"}
                PrepareResourceBackDetail(parent) : PackSearchText.Text = "cached target"
                _packTargetGame = "" : _packGameFilter = ""
                _packReleases = Enumerable.Range(0, 40).Select(Function(index) New ModpackRelease With {.Platform = parent.Platform, .Kind = parent.Kind,
                    .ProjectId = parent.Id, .Id = "parent-" & index, .Name = "Parent " & index, .GameVersions = {"1.20.1"}}).ToList()
                InitializePackVersions() : settle() : ModpackPage.ScrollToVerticalOffset(240) : layout()
                SelectResourceKind(CommunityResourceKind.Shader) : settle() : PackSearchText.Text = "away context"
                Dim winner As New ModpackProject With {.Platform = parent.Platform, .Kind = parent.Kind, .Id = "categorywinner", .Name = "Newer accepted project"}
                Dim armed = 0, callbacks = 0, configured = 0, reads = 0, versions = 0, savedScroll As Double
                Dim acceptedRows As UIElement() = {}, acceptedFilters As UIElement() = {}, acceptedReleases As List(Of ModpackRelease) = Nothing, acceptedText As String = Nothing
                Dim generation = _taskGeneration, registration As CancellationTokenRegistration
                Dim arm As TextChangedEventHandler =
                    Sub()
                        If armed <> 0 OrElse _resourceKind <> CommunityResourceKind.Mod OrElse PackSearchText.Text <> "cached target" Then Return
                        armed += 1
                        registration = _resourceNamesRequest.Token.Register(
                            Sub()
                                callbacks += 1
                                _packCatalogFactory = Function() New ModpackCatalogClient(New PackSearchFixtureHandler With {
                                    .ProjectPage = Function(uri)
                                                       reads += 1
                                                       Return CommunityProjectFixture(winner.Kind, winner.Id)
                                                   End Function,
                                    .VersionPage = Function(uri)
                                                       versions += 1
                                                       Return New JArray(Enumerable.Range(0, 40).Select(Function(index) CommunityReleaseFixture(winner.Kind, winner.Id, "winner" & index.ToString("D2"))))
                                                   End Function}, "", 0)
                                OpenPackDetails(winner)
                                If Not PumpMotionUntil(Function() _packDetailCompletedProject Is winner AndAlso _packDetailRequest Is Nothing) Then Throw New TimeoutException("Newer category project did not complete.")
                                layout() : ModpackPage.ScrollToVerticalOffset(150) : layout()
                                savedScroll = ModpackPage.VerticalOffset : acceptedText = PackSearchText.Text : acceptedReleases = _packReleases
                                acceptedRows = PackVersionCards.Children.Cast(Of UIElement)().ToArray() : acceptedFilters = PackVersionFilters.Children.Cast(Of UIElement)().ToArray()
                            End Sub)
                    End Sub
                RestoreAppearance(New JObject From {{"darkMode", dark}}) : _motionPreview = animated : settle()
                AddHandler PackSearchText.TextChanged, arm
                Try
                    SelectResourceKind(CommunityResourceKind.Mod, Sub()
                                                                     configured += 1
                                                                     PackSearchText.Text = "obsolete instance configuration"
                                                                 End Sub)
                    settle()
                    Dim label = "/category-restore/dark=" & dark & "/motion=" & animated
                    check(armed = 1 AndAlso callbacks = 1 AndAlso reads = 1 AndAlso versions = 1 AndAlso savedScroll > 100,
                        "the actual category restore publishes its new names source before a newer completed project is opened" & label)
                    check(_packProject Is winner AndAlso _packDetailCompletedProject Is winner AndAlso _packReleases Is acceptedReleases AndAlso
                        PackVersionCards.Children.Cast(Of UIElement)().SequenceEqual(acceptedRows) AndAlso PackVersionFilters.Children.Cast(Of UIElement)().SequenceEqual(acceptedFilters),
                        "the category caller keeps the newer project's exact completed release cards and filters" & label)
                    check(configured = 0 AndAlso PackSearchText.Text = acceptedText AndAlso Math.Abs(ModpackPage.VerticalOffset - savedScroll) < 0.5,
                        "an abandoned cached-detail restore cannot run its old configuration delegate or rewind the newer viewport" & label)
                    check(_taskGeneration = generation AndAlso Not _busy AndAlso Not _choosingOnline AndAlso _resourceBack.Count = 0 AndAlso
                        PageSurface.IsHitTestVisible AndAlso DownloadViewHost.IsHitTestVisible AndAlso PackDetailResultsHost.IsHitTestVisible AndAlso
                        _surfaceMotion.Transition Is Nothing AndAlso _categoryMotion.Transition Is Nothing AndAlso _packDetailMotion.Transition Is Nothing AndAlso
                        Math.Abs(PackDetailResultsSurface.Opacity - 1) < 0.0001,
                        "the accepted category project settles without task ownership changes or stranded content gates" & label)
                    observations.Add(New JObject From {{"case", label}, {"configured", configured}, {"beforeScroll", savedScroll}, {"afterScroll", ModpackPage.VerticalOffset},
                        {"inputBefore", acceptedText}, {"inputAfter", PackSearchText.Text}, {"metadataReads", reads}, {"versionReads", versions}, {"armed", armed}, {"callbacks", callbacks}})
                Finally
                    RemoveHandler PackSearchText.TextChanged, arm : registration.Dispose()
                End Try
            Next
        Next
        File.WriteAllText(Path.Combine(directory, "category-restore-continuation.json"), observations.ToString())

        ' A valid incomplete cached frame must still configure once, load, fail,
        ' and retry through the same public detail controls.
        _motionPreview = False : CancelResourceRequests() : _resourceBrowsers.Clear() : _resourceBack.Clear()
        _packCatalogFactory = Function() FailingPackCatalog(New IOException("Offline positive restore fixture"))
        SelectResourceKind(CommunityResourceKind.Mod) : settle()
        Dim pending As New ModpackProject With {.Platform = ModpackPlatform.Modrinth, .Kind = CommunityResourceKind.Mod, .Id = "categorypending", .Name = "Pending cached parent"}
        PrepareResourceBackDetail(pending) : _packDetailCompletedProject = Nothing : settle()
        SelectResourceKind(CommunityResourceKind.Shader) : settle()
        Dim metadata As New DetailStartState(pending.Kind, "Restored cached project", "restored1") With {.Fail = True}, calls = 0
        _packCatalogFactory = Function() New ModpackCatalogClient(New DetailStartHandler(metadata), "", 0)
        SelectResourceKind(CommunityResourceKind.Mod, Sub() calls += 1) : settle()
        check(calls = 1 AndAlso metadata.Calls = 1 AndAlso _packProject Is pending AndAlso _packDetailRequest IsNot Nothing,
            "an accepted incomplete category frame configures once and starts exactly one metadata reader")
        metadata.Gate.TrySetResult(True)
        Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing) : settle()
        File.WriteAllText(Path.Combine(directory, "category-restore-failure.json"), New JObject From {{"metadataAttempts", metadata.Calls},
            {"retryVisible", PackDetailRetry.IsVisible}, {"hintVisible", PackDetailHint.IsVisible}, {"sameProject", _packProject Is pending}, {"hint", PackDetailHint.Text}}.ToString())
        ' One submitted reader may try more than one metadata endpoint on failure.
        check(PackDetailRetry.IsVisible AndAlso PackDetailHint.IsVisible AndAlso _packProject Is pending AndAlso _packDetailCompletedProject Is Nothing,
            "a failed incomplete category restore remains on its own retryable detail")
        Dim retry As New DetailStartState(pending.Kind, "Retried cached project", "retried1")
        retry.Gate.TrySetResult(True) : _packCatalogFactory = Function() New ModpackCatalogClient(New DetailStartHandler(retry), "", 0)
        RetryPackDetails(Nothing, Nothing)
        Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing) : settle()
        check(calls = 1 AndAlso retry.Calls = 1 AndAlso _packDetailCompletedProject Is pending AndAlso _packReleases.Single().Id = retry.VersionId AndAlso Not PackDetailRetry.IsVisible,
            "an explicit retry completes the accepted category frame without rerunning configuration")
        Dim completed = _packReleases
        SelectResourceKind(CommunityResourceKind.Shader) : settle()
        SelectResourceKind(CommunityResourceKind.Mod, Sub() calls += 1) : settle()
        check(calls = 2 AndAlso retry.Calls = 1 AndAlso _packReleases Is completed AndAlso _packDetailCompletedProject Is pending,
            "a completed category frame reuses its exact cached releases while configuring once per accepted entry")
        _packDetailCompletedProject = Nothing
        SelectResourceKind(CommunityResourceKind.Shader) : settle()
        Dim blocked As New DetailStartState(pending.Kind, "Blocked obsolete read", "blocked1")
        _packCatalogFactory = Function() New ModpackCatalogClient(New DetailStartHandler(blocked), "", 0)
        SelectResourceKind(CommunityResourceKind.Mod, Sub() Throw New IOException("Controlled restore configuration failure")) : settle()
        check(PackProjectActionStatus.IsVisible AndAlso PackProjectActionStatus.ToolTip.ToString().Contains("Controlled restore configuration failure") AndAlso _packProject Is pending,
            "a cached-detail configuration exception stays in the category error path without escaping its async restore")
        check(blocked.Calls = 0 AndAlso _packDetailRequest Is Nothing,
            "a failed category continuation does not submit an unnecessary cached-detail metadata reader")
        blocked.Gate.TrySetResult(True)
        Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing) : settle()
        _packDetailCompletedProject = Nothing
        SelectResourceKind(CommunityResourceKind.Shader) : settle()
        Dim redirected As New DetailStartState(pending.Kind, "Unneeded detail after search configuration", "redirect1"), redirects = 0, task = _taskGeneration
        _packCatalogFactory = Function() New ModpackCatalogClient(New DetailStartHandler(redirected), "", 0)
        SelectResourceKind(CommunityResourceKind.Mod,
            Sub()
                redirects += 1 : CancelResourceRequests() : _packSearchSession = Nothing
                ChangePackMode(PackBrowserMode.Search)
            End Sub) : settle()
        check(redirects = 1 AndAlso _packMode = PackBrowserMode.Search AndAlso redirected.Calls = 0 AndAlso _packDetailRequest Is Nothing,
            "an accepted search configuration supersedes the cached detail without fetching metadata that it immediately cancels")
        check(_taskGeneration = task AndAlso PageSurface.IsHitTestVisible AndAlso DownloadViewHost.IsHitTestVisible AndAlso PackSearchResultsHost.IsHitTestVisible AndAlso
            _categoryMotion.Transition Is Nothing AndAlso _packDetailMotion.Transition Is Nothing,
            "a configuration that changes view retains settled search input and the existing installation owner")
        redirected.Gate.TrySetResult(True) : FlushControlLayout() : settle()
    End Function
    Private Sub SetRestoreSearchRows(layout As Action)
        PackResultsCard.Visibility = Visibility.Visible : PackResultsCard.HorizontalAlignment = HorizontalAlignment.Stretch
        PackProjectRows.Children.Clear()
        For index = 1 To 40 : PackProjectRows.Children.Add(New TextBlock With {.Text = "Current row " & index, .Height = 64}) : Next
        layout()
    End Sub
    Private Class RestoreSearchHandler
        Inherits HttpMessageHandler
        Friend Gate As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Friend Entered As Boolean, Disposed As Boolean, Immediate As Boolean
        Protected Overrides Async Function SendAsync(request As HttpRequestMessage, token As CancellationToken) As Task(Of HttpResponseMessage)
            Entered = True
            If Not Immediate Then Await Gate.Task.ConfigureAwait(False) ' Intentionally ignores cancellation.
            Dim data As New JArray
            For index = 1 To 40
                data.Add(New JObject From {{"id", index}, {"classId", 4471}, {"name", "Restore " & index}, {"slug", "restore-" & index}, {"summary", "Fixture"},
                    {"downloadCount", 100}, {"dateModified", "2026-10-01T00:00:00Z"}})
            Next
            Return New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New StringContent(New JObject From {{"data", data}, {"pagination", New JObject From {{"totalCount", 40}}}}.ToString(), Text.Encoding.UTF8, "application/json")}
        End Function
        Protected Overrides Sub Dispose(disposing As Boolean)
            Disposed = True : MyBase.Dispose(disposing)
        End Sub
    End Class
End Class
