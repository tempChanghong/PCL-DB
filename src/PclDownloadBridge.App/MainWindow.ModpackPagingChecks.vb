Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Text
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Async Function VerifyPackPagingAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        ChangePackMode(PackBrowserMode.Search) : layout()
        ResetPackSearch(Nothing, Nothing) : PackSearchSource.SelectedIndex = 2 : PackSearchText.Text = "filtered-pages"
        Dim fetched As New List(Of Integer)
        _packCatalogFactory = Function() New ModpackCatalogClient(New PackSearchFixtureHandler With {.SearchPage = Function(uri)
                                                                                                                   Dim offset = PackFixtureOffset(uri), hits As New JArray
                                                                                                                   fetched.Add(offset)
                                                                                                                   For index = offset To Math.Min(offset + 39, 160)
                                                                                                                       Dim item = PackSearchFixtureHandler.ProjectJson("filtered" & index)
                                                                                                                       If index < 160 Then item("project_type") = "mod"
                                                                                                                       hits.Add(item)
                                                                                                                   Next
                                                                                                                   Return New JObject From {{"hits", hits}, {"total_hits", 161}}
                                                                                                               End Function}, "")
        RunPackSearch(Nothing, Nothing) : Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
        check(_packSearchSession.Count = 0 AndAlso _packSearchSession.HasMore AndAlso fetched.SequenceEqual({0, 40, 80, 120}), "filtered raw pages stop after a bounded search batch without declaring the source exhausted")
        check(PackPagingCard.IsVisible AndAlso PackNextPage.IsEnabled AndAlso PackNextPage.Text = "继续加载", "an initially empty usable result still exposes continuation when later source pages remain")
        check(PackProjectRows.Children.OfType(Of TextBlock)().Single().Text.Contains("继续加载") AndAlso Not PackSearchHint.IsVisible, "an incomplete empty batch explains continuation instead of claiming there are no matches")
        SaveControlVisual(host, Path.Combine(directory, "pack-search-empty-continuation.png"))
        NextPackPage(Nothing, Nothing) : Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
        check(_packSearchPage = 0 AndAlso PackProjectRows.Children.OfType(Of PclResourceItem)().Count() = 1 AndAlso fetched.Last() = 160, "continuation fills the same presentation page from the next raw source cursor")
        check(Not PackPagingCard.IsVisible AndAlso Not PackNextPage.IsEnabled, "one terminal presentation page does not leave redundant disabled paging controls")

        ResetPackSearch(Nothing, Nothing) : PackSearchText.Text = "partial-filtered-pages"
        RunPackSearch(Nothing, Nothing) : Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
        check(_packSearchSession.Count = 0 AndAlso PackNextPage.IsEnabled AndAlso PackSearchHint.IsVisible AndAlso PackSearchHint.Theme = PclHintTheme.Yellow AndAlso PackSearchHint.Text.Contains("继续加载"), "one unavailable source does not turn another source's continuable empty batch into a terminal error")
        SaveControlVisual(host, Path.Combine(directory, "pack-search-partial-continuation.png"))

        ResetPackSearch(Nothing, Nothing) : PackSearchSource.SelectedIndex = 2 : PackSearchText.Text = "unknown-end"
        _packCatalogFactory = Function() New ModpackCatalogClient(New PackSearchFixtureHandler With {.SearchPage = Function(uri)
                                                                                                                   Dim offset = PackFixtureOffset(uri), hits As New JArray
                                                                                                                   For index = offset To Math.Min(offset + 39, 39)
                                                                                                                       hits.Add(PackSearchFixtureHandler.ProjectJson("tail" & index))
                                                                                                                   Next
                                                                                                                   Return New JObject From {{"hits", hits}, {"total_hits", 80}}
                                                                                                               End Function}, "")
        RunPackSearch(Nothing, Nothing) : Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
        Dim firstIds = PackProjectRows.Children.OfType(Of PclResourceItem)().Select(Function(row) DirectCast(row.Tag, ModpackProject).Key).ToArray()
        NextPackPage(Nothing, Nothing) : Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
        check(_packSearchPage = 0 AndAlso PackPageLabel.Text = "1" AndAlso PackProjectRows.Children.OfType(Of PclResourceItem)().Select(Function(row) DirectCast(row.Tag, ModpackProject).Key).SequenceEqual(firstIds), "an unexpectedly empty terminal page keeps the last populated page and its original ordering")
        check(Not PackPagingCard.IsVisible AndAlso Not PackSearchHint.IsVisible, "a source ending before its advertised total settles without a blank page or a false request error")

        ResetPackSearch(Nothing, Nothing) : PackSearchSource.SelectedIndex = 2 : PackSearchText.Text = "later-failure"
        _packCatalogFactory = Function() New ModpackCatalogClient(New PackSearchFixtureHandler With {.SearchPage = Function(uri)
                                                                                                                   If PackFixtureOffset(uri) > 0 Then Throw New IOException("fixture later page failed")
                                                                                                                   Dim hits As New JArray
                                                                                                                   For index = 0 To 39 : hits.Add(PackSearchFixtureHandler.ProjectJson("retained" & index)) : Next
                                                                                                                   Return New JObject From {{"hits", hits}, {"total_hits", 80}}
                                                                                                               End Function}, "")
        RunPackSearch(Nothing, Nothing) : Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
        NextPackPage(Nothing, Nothing) : Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
        check(_packSearchPage = 0 AndAlso PackProjectRows.Children.OfType(Of PclResourceItem)().Count() = 40 AndAlso PackSearchHint.IsVisible AndAlso PackSearchHint.Theme = PclHintTheme.Yellow AndAlso PackSearchHint.Text.Contains("保留") AndAlso PackSearchHint.ToolTip.ToString().Contains("fixture later page failed"), "failure of a later source page retains cached results with a partial-result warning rather than an empty error page")

        Dim session = _packSearchSession
        PackSearchSource.SelectedIndex = 1
        PackSearchCategory.SelectedItem = PackSearchCategory.Items.OfType(Of PclComboBoxItem)().Single(Function(item) CStr(item.Tag) = "/optimization")
        RunPackSearch(Nothing, Nothing) : Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
        check(_packSearchSession Is session AndAlso PackSearchInputHint.IsVisible AndAlso PackSearchInputHint.Text.Contains("CurseForge") AndAlso PackProjectRows.Children.OfType(Of PclResourceItem)().Count() = 40, "an incompatible source/category pair is rejected without replacing the accepted search or its results")
        SaveControlVisual(host, Path.Combine(directory, "pack-search-source-category.png"))
        _packCatalogFactory = Function() New ModpackCatalogClient(New PackSearchFixtureHandler(), "")
    End Function

    Private Shared Function PackFixtureOffset(uri As Uri) As Integer
        Return Integer.Parse(uri.Query.TrimStart("?"c).Split("&"c).Single(Function(pair) pair.StartsWith("offset=", StringComparison.Ordinal)).Substring(7), Globalization.CultureInfo.InvariantCulture)
    End Function

    ' Standalone public entry also lets the session regression run against an
    ' older Core binary. It only uses catalogue/session public APIs and HTTP data.
    Public Shared Async Function VerifyPackPagingSessionsAsync(passed As List(Of String)) As Task(Of ModpackSearchSession)
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     If Not ok Then Throw New InvalidOperationException(label)
                                                     passed.Add(label)
                                                 End Sub
        Dim handler As New PackPagingFixtureHandler()
        Dim session As New ModpackSearchSession(New ModpackQuery With {.Platforms = ModpackPlatform.Modrinth})
        Using client As New ModpackCatalogClient(handler, "")
            For page = 0 To ModpackSearchSession.MaxPage - 1
                Await session.GetPageAsync(page, client, CancellationToken.None).ConfigureAwait(False)
            Next
            check(session.Count = 20000 AndAlso session.HasNext(499), "paging page 499 retains the final legal next-page action")
            handler.Accepted = Function(platform, offset) If(offset = 20000, 1, 0)
            Dim last = Await session.GetPageAsync(500, client, CancellationToken.None).ConfigureAwait(False)
            check(last.Count = 1 AndAlso session.HasMore AndAlso session.HasNext(500), "an incomplete final legal page can continue after four filtered source batches")
            handler.Accepted = Function(platform, offset) ModpackCatalogClient.PageSize
            last = Await session.GetPageAsync(500, client, CancellationToken.None).ConfigureAwait(False)
            check(last.Count = 40 AndAlso session.HasMore AndAlso Not session.HasNext(500), "a full page 500 stops navigation even when its source has more results")
            Dim requests = handler.Requests, count = session.Count, key = last(0).Key
            last = Await session.GetPageAsync(500, client, CancellationToken.None).ConfigureAwait(False)
            check(handler.Requests = requests AndAlso last(0).Key = key, "the final legal cached page preserves order without another catalogue request")
            Dim rejected = 0
            For Each invalid In {-1, 501, Integer.MaxValue}
                Try
                    Await session.GetPageAsync(invalid, client, CancellationToken.None).ConfigureAwait(False)
                Catch ex As ArgumentOutOfRangeException
                    rejected += 1
                End Try
            Next
            check(rejected = 3 AndAlso session.Count = count AndAlso handler.Requests = requests, "invalid core page calls fail before changing accepted results or source cursors")
            check(Not session.HasNext(-1) AndAlso Not session.HasNext(501) AndAlso Not session.HasNext(Integer.MaxValue), "out-of-range navigation queries are false without integer overflow")
        End Using
        Dim both As New ModpackSearchSession(New ModpackQuery With {.Platforms = ModpackPlatform.All})
        Dim dual As New PackPagingFixtureHandler With {.Total = 40}
        Using client As New ModpackCatalogClient(dual, "")
            Dim first = Await both.GetPageAsync(0, client, CancellationToken.None).ConfigureAwait(False)
            Dim requests = dual.Requests
            Dim second = Await both.GetPageAsync(1, client, CancellationToken.None).ConfigureAwait(False)
            check(first.Count = 40 AndAlso second.Count = 40 AndAlso both.Count = 80 AndAlso Not both.HasMore AndAlso both.HasNext(0) AndAlso Not both.HasNext(1), "two ended sources retain cached next-page results without inventing another page")
            check(dual.Requests = requests AndAlso first.Concat(second).Select(Function(item) item.Key).Distinct().Count() = 80, "dual-source cached paging preserves platform-qualified identities and avoids refetch")
        End Using
        Dim empty As New ModpackSearchSession(New ModpackQuery With {.Platforms = ModpackPlatform.Modrinth})
        Dim filtered As New PackPagingFixtureHandler With {.Accepted = Function(platform, offset) 0}
        Using client As New ModpackCatalogClient(filtered, "")
            Dim first = Await empty.GetPageAsync(0, client, CancellationToken.None).ConfigureAwait(False)
            check(first.Count = 0 AndAlso empty.Count = 0 AndAlso empty.HasMore AndAlso empty.HasNext(0) AndAlso filtered.Requests = 4, "an empty filtered presentation page still permits bounded continuation")
            filtered.Accepted = Function(platform, offset) 40
            first = Await empty.GetPageAsync(0, client, CancellationToken.None).ConfigureAwait(False)
            check(first.Count = 40 AndAlso first(0).Id.StartsWith("pack160", StringComparison.Ordinal), "continuation advances the raw cursor rather than repeating filtered pages")
        End Using
        Dim retry As New ModpackSearchSession(New ModpackQuery With {.Platforms = ModpackPlatform.Modrinth})
        Dim cancelHandler As New PackPagingFixtureHandler()
        Using client As New ModpackCatalogClient(cancelHandler, ""), cancellation As New CancellationTokenSource()
            Dim entered As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
            cancelHandler.BeforeSend = Async Function(token)
                                           entered.TrySetResult(True)
                                           Await Task.Delay(Timeout.Infinite, token).ConfigureAwait(False)
                                       End Function
            Dim work = retry.GetPageAsync(0, client, cancellation.Token)
            Await entered.Task.ConfigureAwait(False) : cancellation.Cancel()
            Dim canceled = False
            Try
                Await work.ConfigureAwait(False)
            Catch ex As OperationCanceledException
                canceled = True
            End Try
            check(canceled AndAlso retry.Count = 0 AndAlso retry.HasMore AndAlso retry.ErrorMessage = "", "canceled catalogue batches do not publish results, end sources, or retain an error")
            cancelHandler.BeforeSend = Nothing
            Dim first = Await retry.GetPageAsync(0, client, CancellationToken.None).ConfigureAwait(False)
            check(first.Count = 40 AndAlso first(0).Id = "pack0" AndAlso cancelHandler.Requests = 2, "retry after cancellation reuses the unchanged raw cursor with a fresh token")
        End Using
        Return session
    End Function

    Public Sub VerifyPackPagingUi(directory As String, passed As List(Of String))
        If Not _preview Then Throw New InvalidOperationException("Paging checks require isolated preview mode.")
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     If Not ok Then Throw New InvalidOperationException(label)
                                                     passed.Add(label)
                                                 End Sub
        Dim sessions = VerifyPackPagingSessionsAsync(passed)
        If Not sessions.IsCompleted AndAlso Not PumpMotionUntil(Function() sessions.IsCompleted, timeoutMilliseconds:=45000) Then Throw New TimeoutException("Paging session checks exceeded their deadline.")
        Dim accepted = sessions.GetAwaiter().GetResult()
        Dim owner As New MainWindow(True), previous = SynchronizationContext.Current
        Dim content = DirectCast(owner.Content, FrameworkElement), host As New Border With {.Width = 850, .Height = 600}
        owner.Content = Nothing : host.Child = content
        Try
            Using surface = PreviewSurface.Attach(host, 850, 600)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.VerifyPackPagingViewAsync(accepted, host, check)
                If Not work.IsCompleted AndAlso Not PumpMotionUntil(Function() work.IsCompleted, timeoutMilliseconds:=15000) Then Throw New TimeoutException("Paging UI checks exceeded their deadline.")
                work.GetAwaiter().GetResult()
            End Using
        Finally
            owner._packSearchRequest?.Cancel() : owner._packArtworkRequest?.Cancel() : owner._packArtworkRequest?.Dispose()
            owner._packArtworkRequest = Nothing
            owner.FinishPackContentMotion() : owner._categoryMotion.Finish() : owner._surfaceMotion.Finish()
            host.Child = Nothing : owner.Content = content
            SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub
    Private Async Function VerifyPackPagingViewAsync(session As ModpackSearchSession, host As Border, check As Action(Of Boolean, String)) As Task
        Dim layout As Action = Sub()
                                   host.Measure(New Size(host.Width, host.Height)) : host.Arrange(New Rect(0, 0, host.Width, host.Height))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        SetDownloadView(DownloadView.Modpacks) : layout()
        _packCatalogFactory = Function() New ModpackCatalogClient(New PackPagingFixtureHandler(), "")
        _packSearchSession = session
        Await LoadPackSearchPageAsync(500) : layout()
        check(_packSearchPage = 500 AndAlso PackPageLabel.Text = "501" AndAlso PackProjectRows.Children.Count = 40 AndAlso Not PackNextPage.IsEnabled, "the final legal UI page keeps its results and disables the next-page action")
        check(PackSearchHint.IsVisible AndAlso PackSearchHint.Text.Contains("缩小搜索范围") AndAlso PackPagingCard.IsVisible AndAlso PackPreviousPage.IsEnabled, "the search limit explains how to narrow a query while keeping backward navigation")
        Dim rows = PackProjectRows.Children.Cast(Of UIElement)().ToArray(), artwork = _packArtworkRequest
        Await LoadPackSearchPageAsync(501) : layout()
        check(_packSearchPage = 500 AndAlso PackProjectRows.Children.Cast(Of UIElement)().SequenceEqual(rows) AndAlso PackResultsCard.Title = "整合包", "an illegal UI page cannot clear the last accepted rows or replace them with an error page")
        check(_packArtworkRequest Is artwork AndAlso Not artwork.IsCancellationRequested AndAlso _packSearchRequest Is Nothing, "invalid UI navigation does not cancel artwork or create another search request")
        Await LoadPackSearchPageAsync(-1) : layout()
        NextPackPage(Nothing, Nothing) : layout()
        check(PackProjectRows.Children.Cast(Of UIElement)().SequenceEqual(rows) AndAlso _packSearchPage = 500, "negative or stale next-page handlers preserve the accepted final page")
        Await LoadPackSearchPageAsync(499) : layout()
        check(_packSearchPage = 499 AndAlso PackPageLabel.Text = "500" AndAlso PackNextPage.IsEnabled AndAlso Not PackSearchHint.IsVisible, "returning to page 499 restores cached forward navigation and clears the limit notice")

        Dim handler As New PackPagingFixtureHandler With {.Accepted = Function(platform, offset) 0}
        _packSearchSession = New ModpackSearchSession(New ModpackQuery With {.Platforms = ModpackPlatform.Modrinth})
        _packCatalogFactory = Function() New ModpackCatalogClient(handler, "")
        Await LoadPackSearchPageAsync(0) : layout()
        check(PackNextPage.IsEnabled AndAlso PackNextPage.Text = "继续加载" AndAlso PackPagingCard.IsVisible, "filtered empty pages retain the visible continue-loading action")

        Dim entered As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Dim release As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        handler = New PackPagingFixtureHandler With {.BeforeSend = Async Function(token)
                                                                    entered.TrySetResult(True)
                                                                    Using registration = token.Register(Sub() release.TrySetCanceled())
                                                                        Await release.Task
                                                                    End Using
                                                                End Function}
        _packSearchSession = New ModpackSearchSession(New ModpackQuery With {.Platforms = ModpackPlatform.Modrinth})
        _packCatalogFactory = Function() New ModpackCatalogClient(handler, "")
        Dim loading = LoadPackSearchPageAsync(0)
        Await entered.Task : layout()
        Dim request = _packSearchRequest, pendingRows = PackProjectRows.Children.Cast(Of UIElement)().ToArray()
        Await LoadPackSearchPageAsync(501)
        check(_packSearchRequest Is request AndAlso Not request.IsCancellationRequested AndAlso PackProjectRows.Children.Cast(Of UIElement)().SequenceEqual(pendingRows), "invalid navigation cannot revoke the current valid request or its loading presentation")
        release.TrySetResult(True) : Await loading : layout()
        check(_packSearchRequest Is Nothing AndAlso PackProjectRows.Children.Count = 40 AndAlso PackResultsCard.Title = "整合包", "the retained valid request can still publish its accepted results after invalid navigation")

        ' CF fails in the completed first round, while the next MR-only round is
        ' explicitly held. Invalid navigation must only show its local message,
        ' without asking a still-running session to enumerate source diagnostics.
        Dim partialEntered As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Dim partialRelease As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        handler = New PackPagingFixtureHandler With {
            .FailCF = True,
            .Accepted = Function(platform, offset) If(offset = 0, 0, ModpackCatalogClient.PageSize),
            .BeforeRequest = Async Function(uri, token)
                                 If uri.Host <> "api.modrinth.com" OrElse Not uri.Query.Split("&"c).Any(Function(part) part = "offset=40") Then Return
                                 partialEntered.TrySetResult(True)
                                 Using registration = token.Register(Sub() partialRelease.TrySetCanceled())
                                     Await partialRelease.Task
                                 End Using
                             End Function}
        session = New ModpackSearchSession(New ModpackQuery With {.Platforms = ModpackPlatform.All})
        _packSearchSession = session
        _packCatalogFactory = Function() New ModpackCatalogClient(handler, "")
        loading = LoadPackSearchPageAsync(0)
        Await partialEntered.Task : layout()
        request = _packSearchRequest : artwork = _packArtworkRequest
        pendingRows = PackProjectRows.Children.Cast(Of UIElement)().ToArray()
        PackSearchHint.ToolTip = "fixture obsolete diagnostic"
        Await LoadPackSearchPageAsync(501) : layout()
        check(PackSearchHint.ToolTip Is Nothing AndAlso _packSearchSession Is session AndAlso _packSearchRequest Is request AndAlso Not request.IsCancellationRequested,
            "invalid navigation clears stale diagnostics without reading a session held between partial-source rounds")
        check(_packArtworkRequest Is artwork AndAlso Not artwork.IsCancellationRequested AndAlso PackProjectRows.Children.Cast(Of UIElement)().SequenceEqual(pendingRows),
            "partial-source invalid navigation preserves the valid loading rows and artwork ownership")
        partialRelease.TrySetResult(True) : Await loading : layout()
        check(_packSearchRequest Is Nothing AndAlso PackProjectRows.Children.Count = 40 AndAlso PackSearchHint.IsVisible AndAlso PackSearchHint.Text.Contains("部分来源") AndAlso PackSearchHint.ToolTip.ToString().Contains("fixture CF source unavailable"),
            "the valid partial-source request later publishes results with its original source failure warning")
    End Function

    Private Class PackPagingFixtureHandler
        Inherits HttpMessageHandler
        Private _requests As Integer
        Public Property Total As Integer = 50000
        Public Property Accepted As Func(Of ModpackPlatform, Integer, Integer) = Function(platform, offset) ModpackCatalogClient.PageSize
        Public Property BeforeSend As Func(Of CancellationToken, Task)
        Public Property BeforeRequest As Func(Of Uri, CancellationToken, Task)
        Public Property FailCF As Boolean
        Public ReadOnly Property Requests As Integer
            Get
                Return Volatile.Read(_requests)
            End Get
        End Property
        Protected Overrides Async Function SendAsync(request As HttpRequestMessage, token As CancellationToken) As Task(Of HttpResponseMessage)
            Interlocked.Increment(_requests)
            If BeforeRequest IsNot Nothing Then Await BeforeRequest(request.RequestUri, token).ConfigureAwait(False)
            If BeforeSend IsNot Nothing Then Await BeforeSend(token).ConfigureAwait(False)
            token.ThrowIfCancellationRequested()
            Dim address = request.RequestUri, platform = If(address.Host = "api.modrinth.com", ModpackPlatform.Modrinth, ModpackPlatform.CurseForge)
            If FailCF AndAlso platform = ModpackPlatform.CurseForge Then Return New HttpResponseMessage(HttpStatusCode.ServiceUnavailable) With {.ReasonPhrase = "fixture CF source unavailable", .Content = New StringContent("{}")}
            Dim parameter = If(platform = ModpackPlatform.Modrinth, "offset", "index") & "="
            Dim offset = Integer.Parse(address.Query.TrimStart("?"c).Split("&"c).Single(Function(part) part.StartsWith(parameter, StringComparison.Ordinal)).Substring(parameter.Length), Globalization.CultureInfo.InvariantCulture)
            Dim projects As New JArray(), acceptedCount = Accepted(platform, offset)
            For index = 0 To Math.Min(ModpackCatalogClient.PageSize, Total - offset) - 1
                Dim id = (offset + index + 1).ToString(Globalization.CultureInfo.InvariantCulture)
                If platform = ModpackPlatform.Modrinth Then
                    projects.Add(New JObject From {{"project_id", "pack" & (offset + index).ToString(Globalization.CultureInfo.InvariantCulture)}, {"project_type", If(index < acceptedCount, "modpack", "mod")}, {"title", "Paging " & id}, {"slug", "pack" & id}, {"downloads", 1}, {"date_modified", "2026-10-01T00:00:00Z"}, {"versions", New JArray("1.20.1")}})
                Else
                    projects.Add(New JObject From {{"id", id}, {"classId", If(index < acceptedCount, 4471, 6)}, {"name", "Paging " & id}, {"slug", "pack" & id}, {"downloadCount", 1}, {"dateModified", "2026-10-01T00:00:00Z"}})
                End If
            Next
            Dim result = If(platform = ModpackPlatform.Modrinth,
                New JObject From {{"hits", projects}, {"total_hits", Total}},
                New JObject From {{"data", projects}, {"pagination", New JObject From {{"totalCount", Total}}}})
            Return New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New StringContent(result.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json")}
        End Function
    End Class
End Class
