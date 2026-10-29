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
    Public Sub VerifySearchRestoreEntryUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim host As New Border With {.Width = 900, .Height = 550}, layout = ControlLayout(host), observations As New JObject
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                     File.WriteAllText(Path.Combine(directory, "search-restore-entry.json"), observations.ToString())
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 550)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckSearchRestoreEntryAsync(directory, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 60000) Then Throw New TimeoutException("Search restore entry did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            Dim failed = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failed.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failed))
        Finally
            owner._closing = True : owner.CancelResourceRequests() : owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner._managementMotion.Finish() : owner.FinishPackContentMotion()
            host.Child = Nothing : owner.Content = original : SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub

    Private Async Function CheckSearchRestoreEntryAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : _managementMotion.Finish() : FinishPackContentMotion() : layout()
                               End Sub
        Dim seed As Func(Of ModpackSearchSession) =
            Function()
                _motionPreview = False : CancelResourceRequests() : _resourceBrowsers.Clear() : _resourceBack.Clear()
                ShowDownload(Nothing, Nothing) : SelectResourceKind(CommunityResourceKind.Mod) : ChangePackMode(PackBrowserMode.Search) : settle()
                PackSearchSource.SelectedIndex = 2 : PackSearchText.Text = "retired cached query" : PackSearchGame.Text = "1.18.2"
                _packSearchSession = New ModpackSearchSession(CurrentPackQuery()) : _packSearchScroll = 240
                _packLastQuery = PackQueryKey(CurrentPackQuery()) : _packLastSearch = DateTime.UtcNow
                Dim cached = _packSearchSession
                SetRestoreSearchRows(layout) : ModpackPage.ScrollToVerticalOffset(240) : layout()
                SelectResourceKind(CommunityResourceKind.Shader) : settle()
                Return cached
            End Function
        Dim measurements As New JArray
        For Each dark In {False, True}
            For Each animated In {False, True}
                Dim cached = seed(), obsolete As New SearchRestoreEntryState("obsolete")
                PrepareLocalModStylePage(Path.Combine(directory, "target-" & dark & "-" & animated), layout) : settle()
                Dim instance = _localModInstance, task = _taskGeneration
                _packCatalogFactory = Function() New ModpackCatalogClient(New SearchRestoreEntryHandler(obsolete), "", 0)
                RestoreAppearance(New JObject From {{"darkMode", dark}}) : _motionPreview = animated
                DownloadNewLocalMod(Nothing, Nothing) : settle()
                Dim label = "/search-restore-target/dark=" & dark & "/motion=" & animated
                check(obsolete.Calls = 0 AndAlso cached.Count = 0,
                    "download-new-Mod binds the instance before submitting a retired cached search" & label)
                check(DownloadPage.IsVisible AndAlso _resourceKind = CommunityResourceKind.Mod AndAlso _packMode = PackBrowserMode.Search AndAlso
                    _packSearchSession Is Nothing AndAlso PackSearchText.Text = "" AndAlso PackSearchGame.Text = instance.MinecraftVersion AndAlso
                    CStr(DirectCast(ResourceLoader.SelectedItem, FrameworkElement).Tag).Equals("Fabric", StringComparison.OrdinalIgnoreCase) AndAlso _localModInstance Is instance,
                    "cached search entry retains the actual local Mod instance game and loader filters" & label)
                obsolete.Gate.TrySetResult(True)
                If obsolete.Calls > 0 Then Await WaitPackUiAsync(Function() obsolete.Disposed)
                FlushControlLayout() : settle()
                Dim accepted As New SearchRestoreEntryState("accepted")
                _packCatalogFactory = Function() New ModpackCatalogClient(New SearchRestoreEntryHandler(accepted), "", 0)
                RunPackSearch(Nothing, Nothing)
                Await WaitPackUiAsync(Function() accepted.Calls > 0)
                check(accepted.Calls = 1 AndAlso _packSearchSession.Query.Minecraft = instance.MinecraftVersion AndAlso
                    _packSearchSession.Query.Loader.Equals("Fabric", StringComparison.OrdinalIgnoreCase),
                    "the next submitted search queries the bound instance once" & label)
                accepted.Gate.TrySetResult(True)
                Await WaitPackUiAsync(Function() accepted.Disposed AndAlso _packSearchRequest Is Nothing) : settle()
                check(PackProjectRows.Children.OfType(Of PclResourceItem)().Count() = 40 AndAlso Not PackSearchHint.IsVisible AndAlso
                    _taskGeneration = task AndAlso Not _busy AndAlso Not _choosingOnline AndAlso PageSurface.IsHitTestVisible AndAlso DownloadViewHost.IsHitTestVisible AndAlso
                    PackSearchResultsHost.IsHitTestVisible AndAlso _surfaceMotion.Transition Is Nothing AndAlso _categoryMotion.Transition Is Nothing AndAlso _packResultMotion.Transition Is Nothing AndAlso
                    Math.Abs(PackSearchResultsSurface.Opacity - 1) < 0.0001,
                    "the accepted instance search finishes without stale hints installation changes or hidden content" & label)
                measurements.Add(New JObject From {{"case", label}, {"obsoleteReads", obsolete.Calls}, {"acceptedReads", accepted.Calls}, {"game", instance.MinecraftVersion}, {"rows", PackProjectRows.Children.Count}})

                cached = seed() : obsolete = New SearchRestoreEntryState("layoutobsolete")
                _packCatalogFactory = Function() New ModpackCatalogClient(New SearchRestoreEntryHandler(obsolete), "", 0)
                Dim entered = 0, configured = 0
                Dim navigate As EventHandler =
                    Sub()
                        If entered <> 0 OrElse _resourceKind <> CommunityResourceKind.Mod OrElse Not _categoryMotion.ApplyingContent Then Return
                        entered += 1 : ShowSettings(Nothing, Nothing) : PackSearchText.Text = "newer layout navigation"
                    End Sub
                RestoreAppearance(New JObject From {{"darkMode", dark}}) : _motionPreview = animated : settle()
                AddHandler ModpackPage.LayoutUpdated, navigate
                Try
                    SelectResourceKind(CommunityResourceKind.Mod, Sub()
                                                                     configured += 1 : PackSearchText.Text = "retired category configuration"
                                                                 End Sub)
                    settle()
                    label = "/search-restore-layout/dark=" & dark & "/motion=" & animated
                    check(entered = 1 AndAlso SettingsPage.IsVisible AndAlso PackSearchText.Text = "newer layout navigation",
                        "layout navigation supersedes the cached-search configuration without rewriting its input" & label)
                    check(configured = 0 AndAlso obsolete.Calls = 0 AndAlso _packSearchRequest Is Nothing AndAlso _taskGeneration = task AndAlso
                        PageSurface.IsHitTestVisible AndAlso _surfaceMotion.Transition Is Nothing AndAlso _categoryMotion.Transition Is Nothing,
                        "an abandoned search-category entry submits no hidden reader and leaves no animation gate" & label)
                    measurements.Add(New JObject From {{"case", label}, {"layoutEntries", entered}, {"configured", configured}, {"obsoleteReads", obsolete.Calls}, {"settingsVisible", SettingsPage.IsVisible}, {"input", PackSearchText.Text}})
                Finally
                    RemoveHandler ModpackPage.LayoutUpdated, navigate
                    obsolete.Gate.TrySetResult(True)
                End Try
                If obsolete.Calls > 0 Then Await WaitPackUiAsync(Function() obsolete.Disposed)
                FlushControlLayout() : settle()
            Next
        Next
        File.WriteAllText(Path.Combine(directory, "search-restore-entry-measurements.json"), measurements.ToString())

        Dim retained = seed(), ready As New SearchRestoreEntryState("retained")
        _packCatalogFactory = Function() New ModpackCatalogClient(New SearchRestoreEntryHandler(ready), "", 0)
        SelectResourceKind(CommunityResourceKind.Mod) : settle()
        check(ready.Calls = 1 AndAlso _packSearchSession Is retained AndAlso _packSearchRequest IsNot Nothing,
            "an ordinary incomplete search cache still resumes its accepted reader once")
        ready.Gate.TrySetResult(True)
        Await WaitPackUiAsync(Function() ready.Disposed AndAlso _packSearchRequest Is Nothing) : settle()
        check(_packSearchSession Is retained AndAlso retained.Count = 40 AndAlso PackProjectRows.Children.OfType(Of PclResourceItem)().Count() = 40 AndAlso
            Math.Abs(ModpackPage.VerticalOffset - 240) < 0.5 AndAlso _resourceBack.Count = 0,
            "ordinary cached search restores its populated rows saved viewport and navigation history")
        retained = seed()
        Dim failed As New SearchRestoreEntryState("failed") With {.Fail = True}
        failed.Gate.TrySetResult(True) : _packCatalogFactory = Function() New ModpackCatalogClient(New SearchRestoreEntryHandler(failed), "", 0)
        SelectResourceKind(CommunityResourceKind.Mod) : Await WaitPackUiAsync(Function() failed.Disposed AndAlso _packSearchRequest Is Nothing) : settle()
        check(_packSearchSession Is retained AndAlso PackSearchHint.IsVisible AndAlso PackSearchHint.Theme = PclHintTheme.Red AndAlso retained.ErrorMessage <> "",
            "a resumed search source failure retains explicit retry feedback on its accepted cache")
        Dim retry As New SearchRestoreEntryState("retried")
        retry.Gate.TrySetResult(True) : _packCatalogFactory = Function() New ModpackCatalogClient(New SearchRestoreEntryHandler(retry), "", 0)
        _packLastSearch = DateTime.MinValue : RunPackSearch(Nothing, Nothing)
        Await WaitPackUiAsync(Function() retry.Disposed AndAlso _packSearchRequest Is Nothing) : settle()
        check(retry.Calls = 1 AndAlso _packSearchSession IsNot retained AndAlso PackProjectRows.Children.OfType(Of PclResourceItem)().Count() = 40 AndAlso
            Not PackSearchHint.IsVisible AndAlso PageSurface.IsHitTestVisible AndAlso PackSearchResultsHost.IsHitTestVisible,
            "explicit retry replaces a failed cache once and settles accepted results and input")
    End Function

    Private Class SearchRestoreEntryState
        Friend ReadOnly Gate As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Friend ReadOnly Prefix As String
        Friend Calls As Integer, Disposed As Boolean, Fail As Boolean
        Friend Sub New(prefix As String)
            Me.Prefix = prefix
        End Sub
    End Class
    Private Class SearchRestoreEntryHandler
        Inherits HttpMessageHandler
        Private ReadOnly _state As SearchRestoreEntryState
        Friend Sub New(state As SearchRestoreEntryState)
            _state = state
        End Sub
        Protected Overrides Async Function SendAsync(request As HttpRequestMessage, token As CancellationToken) As Task(Of HttpResponseMessage)
            Interlocked.Increment(_state.Calls) : Await _state.Gate.Task.ConfigureAwait(False)
            If _state.Fail Then Return New HttpResponseMessage(HttpStatusCode.BadRequest) With {.Content = New StringContent("Controlled search restore failure")}
            Dim hits As New JArray
            For index = 0 To 39
                Dim project = CommunityProjectFixture(CommunityResourceKind.Mod, _state.Prefix & index)
                project("title") = _state.Prefix & " result " & index : hits.Add(project)
            Next
            Return New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New StringContent(New JObject From {{"hits", hits}, {"total_hits", 40}}.ToString(), Text.Encoding.UTF8, "application/json")}
        End Function
        Protected Overrides Sub Dispose(disposing As Boolean)
            _state.Disposed = True : MyBase.Dispose(disposing)
        End Sub
    End Class
End Class
