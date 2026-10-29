Imports System.IO
Imports System.Net.Http
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Media.Imaging
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyProjectRenderUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim focus = LocalModCheckGetNativeFocus(), observations As New JObject
        Dim host As New Border With {.Width = 900, .Height = 680}, layout = ControlLayout(host)
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 680)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckProjectRenderAsync(layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 90000) Then Throw New TimeoutException("Project rendering checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "project-render.json"), observations.ToString())
            Dim failed = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failed.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failed))
        Finally
            owner._closing = True : owner.CancelResourceRequests()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner.FinishPackContentMotion()
            host.Child = Nothing : owner.Content = original
            SynchronizationContext.SetSynchronizationContext(previous) : LocalModCheckSetNativeFocus(focus)
        End Try
    End Sub

    Private Async Function CheckProjectRenderAsync(layout As Action, check As Action(Of Boolean, String)) As Task
        Dim png As Byte(), latePng As Byte()
        Using input = Application.GetResourceStream(New Uri("pack://application:,,,/Images/Blocks/Grass.png")).Stream, bytes As New MemoryStream()
            input.CopyTo(bytes) : png = bytes.ToArray()
        End Using
        Using input = Application.GetResourceStream(New Uri("pack://application:,,,/Images/Blocks/GoldBlock.png")).Stream, bytes As New MemoryStream()
            input.CopyTo(bytes) : latePng = bytes.ToArray()
        End Using
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion() : layout()
                               End Sub
        For Each dark In {False, True}
            For Each animated In {False, True}
                For scenario = 0 To 1
                    _motionPreview = False
                    Dim state As New ArtworkFixtureState, project = ArtworkProject("original")
                    PrepareArtworkProject(project, state, settle)
                    Dim first = state.Calls.ToArray().Single(), retired = _packDetailArtworkRequest
                    Dim verified = ArtworkProject(project.Id) : verified.Name = "Verified original"
                    Dim other = ArtworkProject("newer") : other.Name = "Newer project"
                    _packVerifiedProject = verified
                    RestoreAppearance(New JObject From {{"darkMode", dark}}) : _motionPreview = animated : settle()
                    Dim latestRow As PclResourceItem = Nothing, latest As ArtworkFixtureCall = Nothing, newest As CancellationTokenSource = Nothing
                    Dim expected = If(scenario = 0, verified, other), owner = If(scenario = 0, project, other), changeProject = scenario = 1
                    Dim generation = _taskGeneration, label = "/dark=" & dark & "/motion=" & animated & "/case=" & scenario
                    _packCatalogFactory = Function() FailingPackCatalog(New IOException("Controlled newer project metadata failure"))
                    Using registration = retired.Token.Register(Sub()
                                                                   If changeProject Then OpenPackDetails(other) Else RenderPackProject(verified)
                                                                   latestRow = ArtworkHeader() : latest = state.Calls.ToArray().Last() : newest = _packDetailArtworkRequest
                                                               End Sub)
                        RenderPackProject(verified) : settle()
                        check(ArtworkHeader() Is latestRow AndAlso latestRow.Tag Is expected AndAlso _packDetailArtworkRow Is latestRow AndAlso _packDetailArtworkRequest Is newest AndAlso
                            newest IsNot Nothing AndAlso Not latest.Token.IsCancellationRequested AndAlso state.Calls.Count = 2,
                            "a later same-project render or project change retains its header and one live image request" & label)
                        latest.Reply(png) : first.Reply(latePng)
                        Await WaitPackUiAsync(Function() latest.Returned AndAlso first.Returned)
                        Await WaitPackUiAsync(Function() latest.Token.IsCancellationRequested OrElse _packIcons.ContainsKey(expected.IconUrl))
                        If changeProject Then Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing)
                        FlushControlLayout() : settle()
                        Dim accepted As BitmapSource = Nothing : _packIcons.TryGetValue(expected.IconUrl, accepted)
                        check(ArtworkHeader() Is latestRow AndAlso ArtworkHeader().Tag Is expected AndAlso accepted IsNot Nothing AndAlso accepted.IsFrozen AndAlso
                            ArtworkHeader().Icon.Source Is accepted AndAlso PackProjectTitle.Text = expected.Name,
                            "only the current project header displays its accepted image after retired image and metadata responses" & label)
                        state.CompleteAll(png)
                        Await WaitPackUiAsync(Function() _packIconSlots.CurrentCount = 4) : FlushControlLayout() : settle()
                        check(_packProject Is owner AndAlso ActivePackProject() Is expected AndAlso state.Calls.Count = 2 AndAlso _resourceBack.Count = 0 AndAlso
                            _taskGeneration = generation AndAlso Not _busy AndAlso PackDetailResultsHost.IsHitTestVisible AndAlso PageSurface.IsHitTestVisible AndAlso
                            If(changeProject, PackDetailRetry.IsVisible AndAlso _packReleases.Count = 0, _packDetailCompletedProject Is project AndAlso _packReleases.Count = 1),
                            "header rendering preserves the accepted detail or retry state and retires page gates without duplicate work" & label)
                    End Using
                Next
            Next
        Next
        Await CheckProjectCompletionRenderAsync(png, layout, check)
        Await CheckProjectBackRestoreAsync(png, layout, check)
    End Function
    Private Async Function CheckProjectCompletionRenderAsync(png As Byte(), layout As Action, check As Action(Of Boolean, String)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion() : layout()
                               End Sub
        For Each dark In {False, True}
            For Each animated In {False, True}
                For Each failed In {False, True}
                    _motionPreview = False
                    Dim state As New ArtworkFixtureState, project = ArtworkProject("original")
                    PrepareArtworkProject(project, state, settle) : _resourceTargetLoader = ""
                    Dim retired = _packDetailArtworkRequest, other = ArtworkProject("newer001")
                    other.Name = "Newer completion owner"
                    Dim newer As New DetailStartState(CommunityResourceKind.Mod, "Verified newer completion owner", "nextvers")
                    Dim callbacks = 0, projects = 0, versions = 0, oldRequest As CancellationTokenSource = Nothing, acceptedRequest As CancellationTokenSource = Nothing
                    Dim cards As UIElement() = {}, filters As UIElement() = {}, hintText As String = Nothing, hintTip As Object = Nothing
                    Dim hintTheme As PclHintTheme, hintVisibility As Visibility, retryVisibility As Visibility, filterVisibility As Visibility
                    Dim header As PclResourceItem = Nothing, generation = _taskGeneration, originalTransform = PackDetailResultsSurface.RenderTransform, firstVisualSettled = False
                    Dim label = "/completion/dark=" & dark & "/motion=" & animated & "/newer-failed=" & failed
                    Dim visual As New JObject From {{"case", label}}
                    _packCatalogFactory = Function() New ModpackCatalogClient(New PackSearchFixtureHandler With {
                        .ProjectPage = Function(uri)
                                           projects += 1
                                           Dim data = CommunityProjectFixture(project.Kind, project.Id)
                                           data("title") = "Verified original completion" : data("icon_url") = project.IconUrl
                                           Return data
                                       End Function,
                        .VersionPage = Function(uri)
                                           versions += 1 : oldRequest = _packDetailRequest
                                           Dim data = CommunityReleaseFixture(project.Kind, project.Id, "oldvers")
                                           data("game_versions") = New JArray("1.20.1", "1.21.1")
                                           Return New JArray(data)
                                       End Function}, "")
                    RestoreAppearance(New JObject From {{"darkMode", dark}}) : _motionPreview = animated : settle()
                    Using registration = retired.Token.Register(Sub()
                                                                   callbacks += 1
                                                                   visual("beforeOpenOpacity") = PackDetailResultsSurface.Opacity
                                                                   visual("beforeOpenClock") = If(_packDetailMotion.Transition Is Nothing, 0, _packDetailMotion.Transition.GetHashCode())
                                                                   visual("beforeOpenApplyingContent") = _packDetailMotion.ApplyingContent
                                                                   If failed Then
                                                                       _packCatalogFactory = Function() FailingPackCatalog(New IOException("Controlled newer completion failure"))
                                                                   Else
                                                                       _packCatalogFactory = Function() New ModpackCatalogClient(New DetailStartHandler(newer), "", 0)
                                                                   End If
                                                                   OpenPackDetails(other)
                                                                   If failed AndAlso Not PumpMotionUntil(Function() _packDetailRequest Is Nothing) Then Throw New TimeoutException("Newer detail failure did not settle inside the artwork retirement callback.")
                                                                   visual("afterOpenOpacity") = PackDetailResultsSurface.Opacity
                                                                   visual("afterOpenClock") = If(_packDetailMotion.Transition Is Nothing, 0, _packDetailMotion.Transition.GetHashCode())
                                                                   settle()
                                                                   visual("callbackSettledOpacity") = PackDetailResultsSurface.Opacity
                                                                   acceptedRequest = _packDetailRequest : header = ArtworkHeader()
                                                                   cards = PackVersionCards.Children.Cast(Of UIElement)().ToArray() : filters = PackVersionFilters.Children.Cast(Of UIElement)().ToArray()
                                                                   hintText = PackDetailHint.Text : hintTip = PackDetailHint.ToolTip : hintTheme = PackDetailHint.Theme
                                                                   hintVisibility = PackDetailHint.Visibility : retryVisibility = PackDetailRetry.Visibility : filterVisibility = PackVersionFilterCard.Visibility
                                                               End Sub)
                        Await LoadPackDetailsAsync(project) : settle()
                        check(projects = 1 AndAlso versions = 1 AndAlso callbacks = 1 AndAlso oldRequest IsNot Nothing AndAlso oldRequest.IsCancellationRequested,
                            "real successful project and version responses trigger one reentrant artwork retirement before the obsolete completion returns" & label)
                        check(_packProject Is other AndAlso ActivePackProject() Is other AndAlso _packDetailCompletedProject Is Nothing AndAlso _packDetailRequest Is acceptedRequest AndAlso
                            ArtworkHeader() Is header AndAlso header IsNot Nothing AndAlso header.Tag Is other AndAlso _packReleases.Count = 0 AndAlso
                            If(failed, acceptedRequest Is Nothing, acceptedRequest IsNot Nothing AndAlso Not acceptedRequest.IsCancellationRequested AndAlso newer.Calls = 1),
                            "the obsolete detail completion retains the newer project's current request header and incomplete metadata state" & label)
                        check(PackVersionCards.Children.Cast(Of UIElement)().SequenceEqual(cards) AndAlso PackVersionFilters.Children.Cast(Of UIElement)().SequenceEqual(filters) AndAlso
                            PackVersionFilterCard.Visibility = filterVisibility AndAlso PackDetailHint.Text = hintText AndAlso PackDetailHint.ToolTip Is hintTip AndAlso PackDetailHint.Theme = hintTheme AndAlso
                            PackDetailHint.Visibility = hintVisibility AndAlso PackDetailRetry.Visibility = retryVisibility AndAlso
                            If(failed, PackDetailHint.IsVisible AndAlso PackDetailHint.Theme = PclHintTheme.Red AndAlso PackDetailRetry.IsVisible AndAlso cards.Length = 0,
                                cards.Length = 1 AndAlso DirectCast(cards(0), PclCard).Body IsNot Nothing AndAlso Not PackDetailHint.IsVisible AndAlso Not PackDetailRetry.IsVisible),
                            "the obsolete completion cannot replace newer error or loading cards version filters hint and retry controls" & label)
                        firstVisualSettled = ProjectCompletionVisualsSettledForChecks(originalTransform)
                        visual("obsoleteCompletionSettledOpacity") = PackDetailResultsSurface.Opacity
                        visual("obsoleteCompletionVisualsSettled") = firstVisualSettled
                    End Using
                    _packCatalogFactory = Function() New ModpackCatalogClient(New DetailStartHandler(newer), "", 0)
                    newer.Gate.TrySetResult(True)
                    If failed Then RetryPackDetails(Nothing, Nothing)
                    Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing) : settle()
                    state.CompleteAll(png)
                    Await WaitPackUiAsync(Function() _packIconSlots.CurrentCount = 4) : FlushControlLayout() : settle()
                    check(_packProject Is other AndAlso _packDetailCompletedProject Is other AndAlso ActivePackProject().Id = other.Id AndAlso
                        _packReleases.Count = 1 AndAlso _packReleases(0).Id = "nextvers" AndAlso _packReleases(0).ProjectId = other.Id AndAlso
                        PackVersionCards.Children.Count = 1 AndAlso PackVersionFilters.Children.Count = 2 AndAlso Not PackDetailRetry.IsVisible AndAlso
                        _taskGeneration = generation AndAlso Not _busy AndAlso _resourceBack.Count = 0 AndAlso PackDetailResultsHost.IsHitTestVisible AndAlso PageSurface.IsHitTestVisible,
                        "the newer pending load or explicit retry completes with only its own release cards and unchanged task ownership" & label)
                    visual("newerCompletionSettledOpacity") = PackDetailResultsSurface.Opacity
                    visual("newerCompletionVisualsSettled") = ProjectCompletionVisualsSettledForChecks(originalTransform)
                    visual("remainingTransformGroups") = PclSurfaceMotion.Units(PackDetailResultsSurface).Count(Function(item) TypeOf item.RenderTransform Is TransformGroup)
                    Console.WriteLine("DETAIL_COMPLETION_OBSERVATION " & visual.ToString(Newtonsoft.Json.Formatting.None))
                    check(firstVisualSettled AndAlso CBool(visual("newerCompletionVisualsSettled")),
                        "reentrant detail completion restores surface opacity original transforms clocks ghosts and input gates before and after recovery" & label)
                Next
            Next
        Next
    End Function
    Private Async Function CheckProjectBackRestoreAsync(png As Byte(), layout As Action, check As Action(Of Boolean, String)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion() : layout()
                               End Sub
        For Each dark In {False, True}
            For Each animated In {False, True}
                For Each names In {False, True}
                    _motionPreview = False : CancelResourceRequests()
                    ShowDownload(Nothing, Nothing) : SelectResourceKind(CommunityResourceKind.Mod) : ChangePackMode(PackBrowserMode.Search) : settle()
                    Dim grand As New ModpackProject With {.Platform = ModpackPlatform.Modrinth, .Kind = CommunityResourceKind.Mod, .Id = "restoregrand", .Name = "Grandparent"}
                    Dim parent As New ModpackProject With {.Platform = grand.Platform, .Kind = grand.Kind, .Id = "restoreparent", .Name = "Completed parent"}
                    Dim grandFrame = PrepareResourceBackDetail(grand)
                    PrepareResourceBackDetail(parent)
                    _packTargetGame = ""
                    _packReleases = Enumerable.Range(0, 40).Select(Function(index) New ModpackRelease With {.Platform = parent.Platform, .Kind = parent.Kind, .ProjectId = parent.Id,
                        .Id = "parent-" & index, .Name = "Parent release " & index, .GameVersions = {If(index Mod 2 = 0, "1.20.1", "1.21.1")}}).ToList()
                    InitializePackVersions() : settle()
                    SelectPackVersionFilter(PackVersionFilters.Children.OfType(Of PclRadioButton)().Single(Function(button) CStr(button.Tag) = "1.20.1")) : settle()
                    ModpackPage.ScrollToVerticalOffset(240) : layout()
                    Dim parentFrame = CaptureResourceDetail()
                    Dim artwork As New ArtworkFixtureState, child = ArtworkProject("restorechild")
                    PrepareArtworkProject(child, artwork, settle)
                    Dim retiredArtwork = _packDetailArtworkRequest, firstImage = artwork.Calls.ToArray().Single()
                    _resourceBack.Push(grandFrame) : _resourceBack.Push(parentFrame)
                    Dim session = _packSearchSession, generation = _taskGeneration, originalTransform = PackDetailResultsSurface.RenderTransform
                    Dim other = ArtworkProject("restorenewer"), newer As New DetailStartState(other.Kind, "Verified newer restore owner", "restored1")
                    Dim callbacks = 0, armed = 0, framesBeforeOpen As ResourceDetailState() = {}, acceptedFrames As ResourceDetailState() = {}
                    Dim acceptedRequest As CancellationTokenSource = Nothing, acceptedNames As CancellationTokenSource = Nothing, acceptedToken As CancellationToken
                    Dim header As PclResourceItem = Nothing, cards As UIElement() = {}, filters As UIElement() = {}, expanded As String() = {}
                    Dim filter As String = Nothing, scroll As Double, hintText As String = Nothing, hintTip As Object = Nothing, hintTheme As PclHintTheme
                    Dim hintVisibility As Visibility, retryVisibility As Visibility, filterVisibility As Visibility
                    Dim label = "/restore-back/dark=" & dark & "/motion=" & animated & "/origin=" & If(names, "second-names-reset", "artwork")
                    Dim visual As New JObject From {{"case", label}, {"savedParentScroll", parentFrame.Scroll}, {"savedParentFilter", parentFrame.Filter}}
                    Dim openNewer As Action = Sub()
                                                 callbacks += 1 : framesBeforeOpen = _resourceBack.ToArray()
                                                 _packCatalogFactory = Function() New ModpackCatalogClient(New DetailStartHandler(newer), "", 0)
                                                 OpenPackDetails(other)
                                                 acceptedRequest = _packDetailRequest : acceptedNames = _resourceNamesRequest : acceptedToken = acceptedNames.Token
                                                 header = ArtworkHeader() : acceptedFrames = _resourceBack.ToArray()
                                                 cards = PackVersionCards.Children.Cast(Of UIElement)().ToArray() : filters = PackVersionFilters.Children.Cast(Of UIElement)().ToArray()
                                                 expanded = CapturePackVersionExpansions() : filter = _packGameFilter : scroll = ModpackPage.VerticalOffset
                                                 hintText = PackDetailHint.Text : hintTip = PackDetailHint.ToolTip : hintTheme = PackDetailHint.Theme
                                                 hintVisibility = PackDetailHint.Visibility : retryVisibility = PackDetailRetry.Visibility : filterVisibility = PackVersionFilterCard.Visibility
                                                 visual("callbackOpacity") = PackDetailResultsSurface.Opacity : visual("callbackScroll") = scroll
                                                 visual("callbackCards") = cards.Length : visual("callbackFilter") = filter
                                             End Sub
                    RestoreAppearance(New JObject From {{"darkMode", dark}}) : _motionPreview = animated : settle()
                    Dim second As CancellationTokenRegistration
                    Dim token = If(names, _resourceNamesRequest.Token, retiredArtwork.Token)
                    Using registration = token.Register(Sub()
                                                            If names Then
                                                                ' Back's first reset must accept its own transition. The
                                                                ' next source is retired only inside RestoreResourceDetail.
                                                                armed += 1 : second = _resourceNamesRequest.Token.Register(openNewer)
                                                            Else
                                                                openNewer()
                                                            End If
                                                        End Sub)
                        Try
                            Dim navigated = NavigateTitleBack() : settle()
                            check(navigated AndAlso callbacks = 1 AndAlso armed = If(names, 1, 0) AndAlso token.IsCancellationRequested AndAlso
                                framesBeforeOpen.Length = 1 AndAlso framesBeforeOpen(0) Is grandFrame AndAlso acceptedFrames.Length = 0 AndAlso parentFrame.Completed Is parent AndAlso
                                parentFrame.Filter = "1.20.1" AndAlso parentFrame.Scroll > 100 AndAlso parentFrame.Expanded.Length > 0,
                                "actual title Back consumes only its accepted parent before a newer direct project opening deliberately clears history" & label)
                            check(_packProject Is other AndAlso ActivePackProject() Is other AndAlso _packDetailCompletedProject Is Nothing AndAlso _packDetailRequest Is acceptedRequest AndAlso
                                acceptedRequest IsNot Nothing AndAlso Not acceptedRequest.IsCancellationRequested AndAlso _resourceNamesRequest Is acceptedNames AndAlso Not acceptedToken.IsCancellationRequested AndAlso
                                ArtworkHeader() Is header AndAlso header IsNot Nothing AndAlso header.Tag Is other AndAlso _packReleases.Count = 0 AndAlso newer.Calls = 1 AndAlso _resourceBack.SequenceEqual(acceptedFrames),
                                "obsolete parent restoration retains the newer pending project's reader header names and explicit navigation ownership" & label)
                            check(PackVersionCards.Children.Cast(Of UIElement)().SequenceEqual(cards) AndAlso PackVersionFilters.Children.Cast(Of UIElement)().SequenceEqual(filters) AndAlso
                                CapturePackVersionExpansions().OrderBy(Function(value) value).SequenceEqual(expanded.OrderBy(Function(value) value)) AndAlso _packGameFilter = filter AndAlso
                                Math.Abs(ModpackPage.VerticalOffset - scroll) < 0.1 AndAlso PackVersionFilterCard.Visibility = filterVisibility AndAlso PackDetailHint.Text = hintText AndAlso
                                PackDetailHint.ToolTip Is hintTip AndAlso PackDetailHint.Theme = hintTheme AndAlso PackDetailHint.Visibility = hintVisibility AndAlso PackDetailRetry.Visibility = retryVisibility AndAlso
                                cards.Length = 1 AndAlso TypeOf DirectCast(cards(0), PclCard).Body Is PclLoadingView,
                                "retired parent expansion filter viewport and neutral rendering cannot replace the newer loading cards filters hint or retry state" & label)
                            visual("afterRestoreOpacity") = PackDetailResultsSurface.Opacity : visual("afterRestoreScroll") = ModpackPage.VerticalOffset
                            visual("afterRestoreCards") = PackVersionCards.Children.Count : visual("afterRestoreFilter") = _packGameFilter
                        Finally
                            second.Dispose()
                        End Try
                    End Using
                    newer.Gate.TrySetResult(True)
                    Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing) : settle()
                    artwork.CompleteAll(png)
                    Await WaitPackUiAsync(Function() _packIconSlots.CurrentCount = 4) : FlushControlLayout() : settle()
                    check(_packProject Is other AndAlso _packDetailCompletedProject Is other AndAlso ActivePackProject().Name = newer.Name AndAlso _packReleases.Count = 1 AndAlso
                        _packReleases(0).Id = newer.VersionId AndAlso _packReleases(0).ProjectId = other.Id AndAlso newer.Calls = 1 AndAlso firstImage.Returned AndAlso firstImage.Token.IsCancellationRequested AndAlso
                        _resourceBack.Count = 0 AndAlso Not PackDetailRetry.IsVisible AndAlso _taskGeneration = generation AndAlso _packSearchSession Is session,
                        "the newer metadata completes once while the late retired child image drains and completed parent restoration causes no metadata reload" & label)
                    Dim detailSettled = ProjectCompletionVisualsSettledForChecks(originalTransform)
                    Dim returned = NavigateTitleBack() : settle()
                    visual("finalSearchOpacity") = PackSearchSurface.Opacity : visual("detailVisualsSettled") = detailSettled
                    Console.WriteLine("DETAIL_RESTORE_OBSERVATION " & visual.ToString(Newtonsoft.Json.Formatting.None))
                    check(returned AndAlso detailSettled AndAlso _packMode = PackBrowserMode.Search AndAlso _resourceBack.Count = 0 AndAlso _packSearchSession Is session AndAlso newer.Calls = 1 AndAlso
                        _taskGeneration = generation AndAlso Not _busy AndAlso Not _choosingOnline AndAlso PageSurface.IsHitTestVisible AndAlso DownloadViewHost.IsHitTestVisible AndAlso PackSearchResultsHost.IsHitTestVisible AndAlso
                        Math.Abs(PackSearchSurface.Opacity - 1) < 0.0001 AndAlso _surfaceMotion.Transition Is Nothing AndAlso _categoryMotion.Transition Is Nothing AndAlso
                        _packDetailMotion.Transition Is Nothing AndAlso _packResultMotion.Transition Is Nothing AndAlso PackDetailGhost.Children.Count = 0 AndAlso PackSearchGhost.Children.Count = 0,
                        "the next explicit Back reaches cached search with no duplicate load ghost clock transform or input gate after reentrant parent restoration" & label)
                Next
            Next
        Next
    End Function

    Private Function ProjectCompletionVisualsSettledForChecks(originalTransform As Transform) As Boolean
        Return Math.Abs(PackDetailResultsSurface.Opacity - 1) < 0.0001 AndAlso PackDetailResultsSurface.RenderTransform Is originalTransform AndAlso
            Not PackDetailResultsSurface.HasAnimatedProperties AndAlso PclSurfaceMotion.Units(PackDetailResultsSurface).All(Function(item) Not TypeOf item.RenderTransform Is TransformGroup AndAlso Math.Abs(item.Opacity - 1) < 0.0001) AndAlso
            _packDetailMotion.Transition Is Nothing AndAlso _packResultMotion.Transition Is Nothing AndAlso _surfaceMotion.Transition Is Nothing AndAlso _categoryMotion.Transition Is Nothing AndAlso
            PackDetailGhost.Children.Count = 0 AndAlso PackSearchGhost.Children.Count = 0 AndAlso PackDetailResultsHost.IsHitTestVisible AndAlso DownloadViewHost.IsHitTestVisible AndAlso PageSurface.IsHitTestVisible
    End Function
End Class
