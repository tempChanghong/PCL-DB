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
