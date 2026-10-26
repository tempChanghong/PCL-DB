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
    Public Sub VerifyDependencyNavigationUi(directory As String, passed As List(Of String))
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
                Dim work = owner.CheckDependencyNavigationAsync(directory, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 30000) Then Throw New TimeoutException("Dependency navigation did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "dependency-navigation.json"), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            owner._closing = True : owner.CancelResourceRequests() : owner.CancelManagementRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish()
            host.Child = Nothing : owner.Content = original : SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub

    Private Async Function CheckDependencyNavigationAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        ShowDownload(Nothing, Nothing) : SelectResourceKind(CommunityResourceKind.Mod)
        _surfaceMotion.Finish() : _categoryMotion.Finish()
        Dim project As New ModpackProject With {.Platform = ModpackPlatform.Modrinth, .Kind = CommunityResourceKind.Mod, .Id = "parent", .Name = "Parent fixture"}
        Dim releases As New List(Of ModpackRelease)
        For Each game In {"1.20.1", "1.21.1"}
            For index = 1 To 16
                releases.Add(New ModpackRelease With {.Platform = project.Platform, .Kind = project.Kind, .ProjectId = project.Id, .Id = game & "-" & index,
                    .Name = "Release " & index, .FileName = "resource.jar", .GameVersions = {game}, .Loaders = {"fabric"}})
            Next
        Next
        releases(0).Dependencies = {New ResourceDependency With {.ProjectId = "dep1"}, New ResourceDependency With {.ProjectId = "dep2"}}
        _resourceDependencyNames("Modrinth:dep1") = "Dependency one" : _resourceDependencyNames("Modrinth:dep2") = "Dependency two"
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion() : layout()
                               End Sub
        Dim prepare As Action = Sub()
                                    _resourceBack.Clear()
                                    _packProject = project : _packVerifiedProject = project : _packDetailCompletedProject = project : _packReleases = releases
                                    _packTargetGame = "" : _resourceTargetLoader = "fabric" : ChangePackMode(PackBrowserMode.Detail)
                                    InitializePackVersions() : settle()
                                    For Each card In PackVersionCards.Children.OfType(Of PclCard)() : card.Collapsed = False : Next
                                    settle()
                                    ModpackPage.ScrollToVerticalOffset(40) : layout()
                                End Sub
        Dim link As Func(Of String, PclButton) = Function(id)
                                                   Return DirectCast(_packVersionCache("1.20.1").Body, StackPanel).Children.OfType(Of WrapPanel)().
                                                       SelectMany(Function(panel) panel.Children.OfType(Of PclButton)()).Single(Function(button) DirectCast(button.Tag, ResourceDependency).ProjectId = id)
                                               End Function
        For Each animated In {False, True}
            For Each dark In {False, True}
                _motionPreview = animated : RestoreAppearance(New JObject From {{"darkMode", dark}})
                prepare()
                Dim state As New DependencyNavigationState
                _packCatalogFactory = Function() New ModpackCatalogClient(New DependencyNavigationHandler(state), "", 0)
                Dim button = link("dep1"), dependency = DirectCast(button.Tag, ResourceDependency), label = "/motion=" & animated & "/dark=" & dark
                File.WriteAllText(Path.Combine(directory, "dependency-entry.json"), New JObject From {{"Loaded", button.IsLoaded}, {"Visible", button.IsVisible}, {"Enabled", button.IsEnabled}, {"Hit", button.IsHitTestVisible},
                    {"PageHit", PageSurface.IsHitTestVisible}, {"ViewHit", DownloadViewHost.IsHitTestVisible}, {"ResultHit", PackDetailResultsHost.IsHitTestVisible}, {"PageVisible", DownloadPage.IsVisible},
                    {"Busy", _busy}, {"Choosing", _choosingOnline}, {"Current", ActivePackProject() Is project}, {"Mode", _packMode.ToString()}, {"View", _downloadView.ToString()}}.ToString())
                OpenResourceDependency(project, dependency, button)
                Await WaitPackUiAsync(Function() state.Entered)
                SelectPackVersionFilter(PackVersionFilters.Children.OfType(Of PclRadioButton)().Single(Function(value) CStr(value.Tag) = "1.20.1"))
                ModpackPage.ScrollToVerticalOffset(240) : layout()
                Dim expectedOffset = ModpackPage.VerticalOffset
                check(_resourceBack.Count = 0 AndAlso _packProject Is project AndAlso expectedOffset > 150 AndAlso button.IsVisible,
                    "pending dependency resolution leaves the current filter and viewport interactive" & label)
                state.Gate.SetResult(True)
                Await WaitPackUiAsync(Function() _packProject?.Id = "dep1" AndAlso _packDetailCompletedProject Is _packProject)
                settle()
                Dim frame = _resourceBack.Peek()
                check(frame.Project Is project AndAlso frame.Filter = "1.20.1" AndAlso Math.Abs(frame.Scroll - expectedOffset) < 0.5,
                    "dependency handoff captures the latest accepted parent filter and reading position" & label)
                NavigateTitleBack() : settle()
                check(_packProject Is project AndAlso _packGameFilter = "1.20.1" AndAlso Math.Abs(ModpackPage.VerticalOffset - expectedOffset) < 0.5,
                    "title back restores edits made while resolving a dependency" & label)
                check(_resourceBack.Count = 0 AndAlso _packReleases Is releases AndAlso _packReleases.Count = 32 AndAlso PageSurface.IsHitTestVisible AndAlso PackDetailResultsHost.IsHitTestVisible,
                    "dependency return retains exact release identities and retires all entry input gates" & label)

                prepare()
                state = New DependencyNavigationState With {.DelayFirst = False}
                _packCatalogFactory = Function() New ModpackCatalogClient(New DependencyNavigationHandler(state), "", 0)
                button = link("dep1") : OpenResourceDependency(project, DirectCast(button.Tag, ResourceDependency), button)
                Await WaitPackUiAsync(Function() _packProject?.Id = "dep1" AndAlso _packDetailCompletedProject Is _packProject)
                settle() : NavigateTitleBack() : settle()
                check(PackVersionCards.Children.OfType(Of PclCard)().Count() = 2 AndAlso PackVersionCards.Children.OfType(Of PclCard)().All(Function(card) Not card.Collapsed),
                    "returning from a dependency retains manually expanded version cards" & label)
            Next
        Next

        _motionPreview = True : prepare()
        Dim late As New DependencyNavigationState
        _packCatalogFactory = Function() New ModpackCatalogClient(New DependencyNavigationHandler(late), "", 0)
        Dim oldLink = link("dep1")
        OpenResourceDependency(project, DirectCast(oldLink.Tag, ResourceDependency), oldLink)
        Await WaitPackUiAsync(Function() late.Entered)
        NavigateTitleBack() : settle()
        late.Gate.SetResult(True) : Await WaitPackUiAsync(Function() late.Exited) : FlushControlLayout() : settle()
        check(_packMode = PackBrowserMode.Search AndAlso _resourceBack.Count = 0 AndAlso _packProject Is project, "a late dependency cancelled by title back cannot navigate or append a return frame")
        OpenPackDetails(project) : settle()
        Dim replacement As New DependencyNavigationState
        _packCatalogFactory = Function() New ModpackCatalogClient(New DependencyNavigationHandler(replacement), "", 0)
        oldLink = link("dep1") : OpenResourceDependency(project, DirectCast(oldLink.Tag, ResourceDependency), oldLink)
        Await WaitPackUiAsync(Function() replacement.Entered)
        Dim newerLink = link("dep2")
        OpenResourceDependency(project, DirectCast(newerLink.Tag, ResourceDependency), newerLink)
        Await WaitPackUiAsync(Function() _packProject?.Id = "dep2" AndAlso _packDetailCompletedProject Is _packProject)
        settle() : replacement.Gate.SetResult(True) : Await WaitPackUiAsync(Function() replacement.Exited) : FlushControlLayout() : settle()
        check(_packProject.Id = "dep2" AndAlso _resourceBack.Count = 1 AndAlso _resourceBack.Peek().Project Is project, "a replaced dependency cannot supersede the newer project or duplicate its return frame")
        NavigateTitleBack() : settle()
        check(_packProject Is project AndAlso _resourceBack.Count = 0, "one title return after rapid dependency replacement reaches the original parent")

        prepare()
        Dim failed As New DependencyNavigationState With {.DelayFirst = False, .FailFirst = True}
        _packCatalogFactory = Function() New ModpackCatalogClient(New DependencyNavigationHandler(failed), "", 0)
        oldLink = link("dep1") : OpenResourceDependency(project, DirectCast(oldLink.Tag, ResourceDependency), oldLink)
        Await WaitPackUiAsync(Function() _dependencyRequest Is Nothing) : settle()
        check(_packProject Is project AndAlso _resourceBack.Count = 0 AndAlso PackProjectActionStatus.IsVisible, "failed dependency resolution leaves the accepted parent and return stack intact")
        failed.FailFirst = False
        OpenResourceDependency(project, DirectCast(oldLink.Tag, ResourceDependency), oldLink)
        Await WaitPackUiAsync(Function() _packProject?.Id = "dep1" AndAlso _packDetailCompletedProject Is _packProject)
        settle()
        check(_resourceBack.Count = 1 AndAlso PackDetailHint.Visibility <> Visibility.Visible, "retry after dependency failure accepts one current detail without retaining the previous error")
        NavigateTitleBack() : settle()
        SelectResourceKind(CommunityResourceKind.Shader) : settle()
        SelectResourceKind(CommunityResourceKind.Mod) : settle()
        check(_packProject Is project AndAlso PackVersionCards.Children.OfType(Of PclCard)().All(Function(card) Not card.Collapsed), "category round trips retain restored parent card expansions")

        prepare() : _packTargetGame = "1.20.1" : InitializePackVersions() : settle()
        check(_packGameFilter = "1.20.1" AndAlso PackVersionCards.Children.OfType(Of PclCard)().Single().Collapsed = False,
            "fresh detail still defaults to its supported target version and expands the sole card")
        SelectPackVersionFilter(PackVersionFilters.Children.OfType(Of PclRadioButton)().Single(Function(value) CStr(value.Tag) = "")) : settle()
        check(_packVersionCache("1.21.1").Collapsed AndAlso _packVersionCache("1.21.1").Body Is Nothing,
            "fresh nonselected version groups retain PCL lazy construction")
        _packVersionCache("1.21.1").Collapsed = False : settle()
        SelectPackVersionFilter(PackVersionFilters.Children.OfType(Of PclRadioButton)().Single(Function(value) CStr(value.Tag) = "1.20.1")) : settle()
        For index = 1 To 2
            SelectResourceKind(CommunityResourceKind.Shader) : settle()
            SelectResourceKind(CommunityResourceKind.Mod) : settle()
        Next
        check(Not _packVersionCache.ContainsKey("1.21.1"), "restoring a filtered detail does not eagerly build its hidden version cards")
        SelectPackVersionFilter(PackVersionFilters.Children.OfType(Of PclRadioButton)().Single(Function(value) CStr(value.Tag) = "")) : settle()
        check(Not _packVersionCache("1.21.1").Collapsed AndAlso _packVersionCache("1.21.1").Body IsNot Nothing,
            "an expanded hidden group survives repeated filtered category round trips")
        _packVersionCache("1.21.1").Collapsed = True : settle()
        SelectResourceKind(CommunityResourceKind.Shader) : settle()
        SelectResourceKind(CommunityResourceKind.Mod) : settle()
        check(_packVersionCache("1.21.1").Collapsed AndAlso _packVersionCache("1.21.1").Body Is Nothing,
            "explicitly collapsing a restored group discards its remembered expansion and keeps its body lazy")
    End Function

    Private Class DependencyNavigationState
        Friend Gate As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Friend Entered As Boolean, Exited As Boolean, DelayFirst As Boolean = True, FailFirst As Boolean
    End Class
    Private Class DependencyNavigationHandler
        Inherits HttpMessageHandler
        Private ReadOnly _state As DependencyNavigationState
        Friend Sub New(state As DependencyNavigationState)
            _state = state
        End Sub
        Protected Overrides Async Function SendAsync(request As HttpRequestMessage, token As CancellationToken) As Task(Of HttpResponseMessage)
            Dim path = request.RequestUri.AbsolutePath.Replace("/modrinth/", "/")
            If path = "/v2/project/dep1" Then
                If _state.DelayFirst Then
                    _state.DelayFirst = False : _state.Entered = True
                    Await _state.Gate.Task.ConfigureAwait(False) ' Intentionally late after cancellation.
                    _state.Exited = True
                End If
                If _state.FailFirst Then Return New HttpResponseMessage(HttpStatusCode.BadRequest) With {.Content = New StringContent("Controlled dependency failure")}
            End If
            Dim id = path.Split("/"c)(3)
            Dim data As JToken = If(path.EndsWith("/version", StringComparison.Ordinal),
                DirectCast(New JArray(CommunityReleaseFixture(CommunityResourceKind.Mod, id)), JToken), CommunityProjectFixture(CommunityResourceKind.Mod, id))
            Return New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New StringContent(data.ToString(), Text.Encoding.UTF8, "application/json")}
        End Function
    End Class
End Class
