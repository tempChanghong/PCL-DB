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
    Public Sub VerifySearchStartUi(directory As String, passed As List(Of String))
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
                Dim work = owner.CheckSearchStartAsync(layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 90000) Then Throw New TimeoutException("Search startup checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "search-start.json"), observations.ToString())
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

    Private Async Function CheckSearchStartAsync(layout As Action, check As Action(Of Boolean, String)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion() : layout()
                               End Sub
        For Each kind In {CommunityResourceKind.Mod, CommunityResourceKind.Shader}
            For Each dark In {False, True}
                For Each animated In {False, True}
                    For scenario = 0 To 2
                        _motionPreview = False : CancelResourceRequests()
                        ShowDownload(Nothing, Nothing) : SelectResourceKind(kind) : ChangePackMode(PackBrowserMode.Search) : settle()
                        PackSearchText.Text = "original query" : PackSearchSource.SelectedIndex = 2 : PackSearchCategory.SelectedIndex = 0
                        PackSearchGame.Text = "全部 (也可自行输入)" : ResourceLoader.SelectedIndex = 0
                        _packSearchSession = New ModpackSearchSession(CurrentPackQuery()) : _packSearchPage = 0 : _packLastQuery = PackQueryKey(CurrentPackQuery()) : _packLastSearch = DateTime.UtcNow
                        Dim session = _packSearchSession, expected = session, taskGeneration = _taskGeneration
                        Dim old As New SearchStartState(kind, "Old", 80), latest As New SearchStartState(kind, "Latest", If(scenario = 0, 80, 40))
                        Dim stale As New SearchStartState(kind, "Obsolete", 80) With {.Fail = True}
                        stale.Gate.SetResult(True)
                        _packCatalogFactory = Function() New ModpackCatalogClient(New SearchStartHandler(old), "", 0)
                        RestoreAppearance(New JObject From {{"darkMode", dark}}) : _motionPreview = animated : settle()
                        Dim oldRead = LoadPackSearchPageAsync(0), retired = _packSearchRequest, retiredArtwork = _packArtworkRequest
                        Await WaitPackUiAsync(Function() old.Calls > 0) : settle()
                        Dim newest As CancellationTokenSource = Nothing, artwork As CancellationToken, nested As Task = Nothing, escaped = False
                        Dim changeQuery = scenario = 1, failCancel = scenario = 2
                        Dim label = "/kind=" & kind.ToString() & "/dark=" & dark & "/motion=" & animated & "/case=" & scenario
                        Dim notify = If(changeQuery, retiredArtwork.Token, retired.Token)
                        Using registration = notify.Register(Sub()
                                                                 If failCancel Then Throw New IOException("Controlled search cancellation failure")
                                                                 _packCatalogFactory = Function() New ModpackCatalogClient(New SearchStartHandler(latest), "", 0)
                                                                 If changeQuery Then
                                                                     PackSearchText.Text = "newer query" : RunPackSearch(Nothing, Nothing)
                                                                     expected = _packSearchSession
                                                                 Else
                                                                     nested = LoadPackSearchPageAsync(0)
                                                                 End If
                                                                 newest = _packSearchRequest : artwork = _packArtworkRequest.Token
                                                                 _packCatalogFactory = Function() New ModpackCatalogClient(New SearchStartHandler(stale), "", 0)
                                                             End Sub)
                            Dim outgoing = LoadPackSearchPageAsync(If(scenario = 0, 1, 0))
                            old.Gate.SetResult(True) : Await oldRead
                            If failCancel Then
                                Try
                                    Await outgoing
                                Catch
                                    escaped = True
                                End Try
                                settle()
                                check(Not escaped AndAlso _packSearchRequest Is Nothing AndAlso PackSearchHint.IsVisible AndAlso PackSearchHint.Theme = PclHintTheme.Red,
                                    "search cancellation notification faults stay inside retry feedback and request cleanup" & label)
                                latest.Gate.SetResult(True)
                                _packCatalogFactory = Function() New ModpackCatalogClient(New SearchStartHandler(latest), "", 0)
                                Await LoadPackSearchPageAsync(0)
                            Else
                                Await WaitPackUiAsync(Function() latest.Calls > 0)
                                check(_packSearchRequest Is newest AndAlso newest IsNot Nothing AndAlso Not newest.IsCancellationRequested AndAlso Not artwork.IsCancellationRequested AndAlso
                                    _packSearchSession Is expected AndAlso If(changeQuery, expected IsNot session, expected Is session),
                                    "the newer page or submitted query keeps its own search and artwork ownership" & label)
                                latest.Gate.SetResult(True)
                                If nested IsNot Nothing Then Await nested
                                Await outgoing
                                Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing AndAlso expected.Count = 40)
                            End If
                            settle()
                            Dim rows = PackProjectRows.Children.OfType(Of PclResourceItem)().ToArray()
                            check(_packSearchSession Is expected AndAlso _packSearchPage = 0 AndAlso rows.Length = 40 AndAlso rows.All(Function(row) DirectCast(row.Tag, ModpackProject).Name.StartsWith("Latest", StringComparison.Ordinal)) AndAlso
                                stale.Calls = 0 AndAlso expected.ErrorMessage = "" AndAlso Not PackSearchHint.IsVisible AndAlso _taskGeneration = taskGeneration AndAlso
                                _packMode = PackBrowserMode.Search AndAlso PageSurface.IsHitTestVisible AndAlso PackSearchResultsHost.IsHitTestVisible,
                                "only the latest search renders page one without obsolete requests source errors or download side effects" & label)
                        End Using
                    Next
                Next
            Next
        Next
    End Function

    Private Class SearchStartState
        Friend ReadOnly Gate As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Friend ReadOnly Kind As CommunityResourceKind, Prefix As String, Total As Integer
        Friend Calls As Integer, Fail As Boolean
        Friend Sub New(kind As CommunityResourceKind, prefix As String, total As Integer)
            Me.Kind = kind : Me.Prefix = prefix : Me.Total = total
        End Sub
    End Class
    Private Class SearchStartHandler
        Inherits HttpMessageHandler
        Private ReadOnly _state As SearchStartState
        Friend Sub New(state As SearchStartState)
            _state = state
        End Sub
        Protected Overrides Async Function SendAsync(request As HttpRequestMessage, token As CancellationToken) As Task(Of HttpResponseMessage)
            Interlocked.Increment(_state.Calls) : Await _state.Gate.Task.ConfigureAwait(False)
            If _state.Fail Then Return New HttpResponseMessage(HttpStatusCode.BadRequest) With {.Content = New StringContent("Controlled obsolete page failure")}
            Dim offset = PackFixtureOffset(request.RequestUri), hits As New JArray
            For index = offset To Math.Min(offset + 39, _state.Total - 1)
                Dim project = CommunityProjectFixture(_state.Kind, _state.Prefix & index)
                project("title") = _state.Prefix & " result " & index : hits.Add(project)
            Next
            Dim data As New JObject From {{"hits", hits}, {"total_hits", _state.Total}}
            Return New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New StringContent(data.ToString(), Text.Encoding.UTF8, "application/json")}
        End Function
    End Class
End Class
