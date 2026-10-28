Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifySearchSubmissionUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim focus = LocalModCheckGetNativeFocus(), observations As New JObject, failures As New List(Of Exception)
        Dim host As New Border With {.Width = 900, .Height = 680}, layout = ControlLayout(host)
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                     File.WriteAllText(Path.Combine(directory, "search-submission.json"), observations.ToString())
                                                 End Sub
        Dim capture As DispatcherUnhandledExceptionEventHandler = Sub(sender, args)
                                                                     failures.Add(args.Exception) : args.Handled = True
                                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        AddHandler owner.Dispatcher.UnhandledException, capture
        Try
            Using surface = PreviewSurface.Attach(host, 900, 680)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckSearchSubmissionAsync(directory, layout, check, failures)
                If Not PumpMotionUntil(Function() work.IsCompleted, 90000) Then Throw New TimeoutException("Search submission checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "search-submission.json"), observations.ToString())
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

    Private Async Function CheckSearchSubmissionAsync(directory As String, layout As Action, check As Action(Of Boolean, String), failures As List(Of Exception)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion() : layout()
                               End Sub
        For Each kind In {CommunityResourceKind.Mod, CommunityResourceKind.Shader}
            For Each dark In {False, True}
                For Each animated In {False, True}
                    For scenario = 0 To 5
                        Dim progressPath = Path.Combine(directory, "submission-progress.log")
                        File.AppendAllText(progressPath, "start " & kind.ToString() & "/" & dark & "/" & animated & "/" & scenario & Environment.NewLine)
                        _motionPreview = False : CancelResourceRequests()
                        ShowDownload(Nothing, Nothing) : SelectResourceKind(kind) : ChangePackMode(PackBrowserMode.Search) : settle()
                        PackSearchText.Text = "original query" : PackSearchSource.SelectedIndex = 2 : PackSearchCategory.SelectedIndex = 0
                        PackSearchGame.Text = "全部 (也可自行输入)" : ResourceLoader.SelectedIndex = 0
                        _packSearchSession = New ModpackSearchSession(CurrentPackQuery()) : _packLastQuery = PackQueryKey(CurrentPackQuery())
                        _packLastSearch = DateTime.UtcNow : _packSearchPage = 0
                        RestoreAppearance(New JObject From {{"darkMode", dark}}) : _motionPreview = animated : settle()
                        Dim old As New SearchStartState(kind, "Old", 40), latest As New SearchStartState(kind, "Latest", 40)
                        Dim stale As New SearchStartState(kind, "Obsolete", 40)
                        stale.Gate.SetResult(True)
                        _packCatalogFactory = Function() New ModpackCatalogClient(New SearchStartHandler(old), "", 0)
                        Dim oldRead = LoadPackSearchPageAsync(0), retired = _packSearchRequest
                        Await WaitPackUiAsync(Function() old.Calls > 0) : settle()
                        File.AppendAllText(progressPath, "old read ready" & Environment.NewLine)
                        Dim expected As ModpackSearchSession = Nothing, newest As CancellationTokenSource = Nothing, artwork As CancellationToken
                        Dim taskGeneration = _taskGeneration, fault = scenario = 3, completedContext = scenario >= 4, disposeDetail = scenario = 4
                        Dim label = "/kind=" & kind.ToString() & "/dark=" & dark & "/motion=" & animated & "/case=" & scenario
                        failures.Clear()
                        Using detail As New CancellationTokenSource(), dependency As New CancellationTokenSource()
                            _packDetailRequest = detail : _dependencyRequest = dependency
                            Dim token = If(scenario = 0 OrElse scenario >= 4, retired.Token, If(scenario = 1, detail.Token, dependency.Token))
                            Using registration = token.Register(Sub()
                                                                    If fault Then Throw New IOException("Controlled submitted-query cancellation failure")
                                                                    _packCatalogFactory = Function() New ModpackCatalogClient(New SearchStartHandler(latest), "", 0)
                                                                    If completedContext Then
                                                                        ' Another old completion may dispose a captured source
                                                                        ' before this submission gets to its cancellation.
                                                                        If disposeDetail Then detail.Dispose() Else dependency.Dispose()
                                                                    Else
                                                                        PackSearchText.Text = "latest query" : RunPackSearch(Nothing, Nothing)
                                                                    End If
                                                                    expected = _packSearchSession : newest = _packSearchRequest : artwork = _packArtworkRequest.Token
                                                                    If Not completedContext Then _packCatalogFactory = Function() New ModpackCatalogClient(New SearchStartHandler(stale), "", 0)
                                                                End Sub)
                                _packCatalogFactory = Function() New ModpackCatalogClient(New SearchStartHandler(stale), "", 0)
                                PackSearchText.Text = "superseded query" : RunPackSearch(Nothing, Nothing)
                                File.AppendAllText(progressPath, "submission returned" & Environment.NewLine)
                                old.Gate.TrySetResult(True) : Await oldRead : Await Task.Yield() : settle()
                                File.AppendAllText(progressPath, "old read retired" & Environment.NewLine)
                                If fault Then
                                    check(failures.Count = 0 AndAlso _packSearchRequest Is Nothing AndAlso PackSearchHint.IsVisible AndAlso PackSearchHint.Theme = PclHintTheme.Red AndAlso
                                        detail.IsCancellationRequested AndAlso dependency.IsCancellationRequested,
                                        "submitted-query cancellation faults stay in retry feedback without an escaping dispatcher exception" & label)
                                    _packCatalogFactory = Function() New ModpackCatalogClient(New SearchStartHandler(latest), "", 0)
                                    PackSearchText.Text = "retry query" : RunPackSearch(Nothing, Nothing) : expected = _packSearchSession
                                Else
                                    check(_packSearchSession Is expected AndAlso _packSearchRequest Is newest AndAlso newest IsNot Nothing AndAlso Not newest.IsCancellationRequested AndAlso
                                        Not artwork.IsCancellationRequested AndAlso (scenario = 4 OrElse detail.IsCancellationRequested) AndAlso (scenario = 5 OrElse dependency.IsCancellationRequested) AndAlso failures.Count = 0,
                                        "latest submitted query retains search and artwork ownership through every retired request notification" & label)
                                End If
                                latest.Gate.TrySetResult(True)
                                Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : settle()
                                Dim rows = PackProjectRows.Children.OfType(Of PclResourceItem)().ToArray()
                                check(_packSearchSession Is expected AndAlso rows.Length = 40 AndAlso rows.All(Function(row) DirectCast(row.Tag, ModpackProject).Name.StartsWith("Latest", StringComparison.Ordinal)) AndAlso
                                    stale.Calls = 0 AndAlso _packSearchPage = 0 AndAlso _resourceBack.Count = 0 AndAlso _taskGeneration = taskGeneration AndAlso Not PackSearchHint.IsVisible,
                                    "only the accepted submission or retry publishes results without obsolete queries or download side effects" & label)
                                ShowSettings(Nothing, Nothing) : settle() : ShowDownload(Nothing, Nothing) : settle()
                                check(_packSearchSession Is expected AndAlso PackSearchSurface.IsVisible AndAlso PackProjectRows.Children.OfType(Of PclResourceItem)().Count() = 40 AndAlso
                                    PageSurface.IsHitTestVisible AndAlso DownloadViewHost.IsHitTestVisible AndAlso PackSearchResultsHost.IsHitTestVisible AndAlso
                                    _packResultMotion.Transition Is Nothing AndAlso latest.Calls = 1,
                                    "return after search cancellation or retry retains cached rows and settled input without replaying old content motion" & label)
                            End Using
                            If _packDetailRequest Is detail Then _packDetailRequest = Nothing
                            If _dependencyRequest Is dependency Then _dependencyRequest = Nothing
                        End Using
                    Next
                Next
            Next
        Next
    End Function
End Class
