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
    Private Function ProjectCompletionVisualsSettledForChecks(originalTransform As Transform) As Boolean
        Return Math.Abs(PackDetailResultsSurface.Opacity - 1) < 0.0001 AndAlso PackDetailResultsSurface.RenderTransform Is originalTransform AndAlso
            Not PackDetailResultsSurface.HasAnimatedProperties AndAlso PclSurfaceMotion.Units(PackDetailResultsSurface).All(Function(item) Not TypeOf item.RenderTransform Is TransformGroup AndAlso Math.Abs(item.Opacity - 1) < 0.0001) AndAlso
            _packDetailMotion.Transition Is Nothing AndAlso _packResultMotion.Transition Is Nothing AndAlso _surfaceMotion.Transition Is Nothing AndAlso _categoryMotion.Transition Is Nothing AndAlso
            PackDetailGhost.Children.Count = 0 AndAlso PackSearchGhost.Children.Count = 0 AndAlso PackDetailResultsHost.IsHitTestVisible AndAlso DownloadViewHost.IsHitTestVisible AndAlso PageSurface.IsHitTestVisible
    End Function
End Class
