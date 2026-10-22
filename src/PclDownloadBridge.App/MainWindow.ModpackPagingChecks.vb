Imports System.IO
Imports System.Windows
Imports System.Windows.Controls
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

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
End Class
