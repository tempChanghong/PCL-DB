Imports System.Windows
Imports System.Windows.Input
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Async Function VerifyPagingEntryOwnershipAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim factory = _packCatalogFactory, motion = _motionPreview, paused = _categoryMotion.PauseForChecks, calls = 0
        Dim generation = _taskGeneration, observations As New JArray
        Dim resumeSearch As Action =
            Sub()
                _busy = False : _closing = False : _choosingOnline = False
                PageSurface.IsEnabled = True : PageSurface.IsHitTestVisible = True : DownloadViewHost.IsHitTestVisible = True : PackSearchResultsHost.IsHitTestVisible = True
                ShowDownload(Nothing, Nothing) : SetDownloadView(DownloadView.Modpacks) : ChangePackMode(PackBrowserMode.Search)
                _surfaceMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion() : layout()
            End Sub
        Dim countFetch As Func(Of Uri, CancellationToken, Task) = Function(uri, token)
                                                                    Interlocked.Increment(calls)
                                                                    Return Task.CompletedTask
                                                                End Function
        Try
            _motionPreview = False
            resumeSearch()
            _packCatalogFactory = Function() New ModpackCatalogClient(New PackSearchFixtureHandler With {.BeforeSendAsync = countFetch, .SearchPage = Function(uri)
                                                                                                                        Dim offset = PackFixtureOffset(uri)
                                                                                                                        Return New JObject From {{"hits", New JArray(Enumerable.Range(offset, 40).Select(Function(index) PackSearchFixtureHandler.ProjectJson("pagingentry" & index)))}, {"total_hits", 120}}
                                                                                                                    End Function}, "")
            PackSearchText.Text = "paging-entry" : PackSearchSource.SelectedIndex = 2
            RunPackSearch(Nothing, Nothing) : Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
            Await LoadPackSearchPageAsync(1) : layout()
            Dim session = _packSearchSession, rows = PackProjectRows.Children.Cast(Of Object)().ToArray()
            ShowSettings(Nothing, Nothing) : _surfaceMotion.Finish() : layout()
            ControlKey(PackNextPage, Key.Enter, True)
            Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
            IO.File.WriteAllText(IO.Path.Combine(directory, "paging-entry-state.json"), New JObject From {
                {"settingsVisible", SettingsPage.IsVisible}, {"page", _packSearchPage}, {"sameSession", _packSearchSession Is session}, {"sameRows", PackProjectRows.Children.Cast(Of Object)().SequenceEqual(rows)}}.ToString())
            check(SettingsPage.IsVisible AndAlso _packSearchPage = 1 AndAlso _packSearchSession Is session AndAlso PackProjectRows.Children.Cast(Of Object)().SequenceEqual(rows),
                "queued next-page Enter from Settings cannot replace the cached search page or its rows")
            resumeSearch()
            Dim reject As Action(Of String) =
                Sub(label)
                    Dim page = _packSearchPage, accepted = _packSearchSession, project = _packProject, before = calls, query = _packLastQuery
                    Dim current = PackProjectRows.Children.Cast(Of Object)().ToArray(), pageMotion = _categoryMotion.Transition, rootMotion = _surfaceMotion.Transition
                    For Each button As UIElement In New UIElement() {PackFirstPage, PackPreviousPage, PackNextPage}
                        NavigatePackPageFromUi(button, New RoutedEventArgs())
                    Next
                    ControlKey(PackNextPage, Key.Enter, True)
                    Dim unchanged = _packSearchPage = page AndAlso _packSearchSession Is accepted AndAlso _packLastQuery = query AndAlso _packProject Is project AndAlso
                        PackProjectRows.Children.Cast(Of Object)().SequenceEqual(current) AndAlso _categoryMotion.Transition Is pageMotion AndAlso _surfaceMotion.Transition Is rootMotion AndAlso
                        calls = before AndAlso _taskGeneration = generation
                    observations.Add(New JObject From {{"boundary", label}, {"unchanged", unchanged}, {"extraFetches", calls - before}})
                    IO.File.WriteAllText(IO.Path.Combine(directory, "paging-entry-ownership.json"), observations.ToString())
                    check(unchanged, "inactive paging entries preserve page, accepted rows, request and animation ownership: " & label)
                End Sub
            For Each boundary In {"detail", "local", "vanilla", "page-gate", "view-gate", "result-gate", "disabled", "busy", "closing", "prompt"}
                resumeSearch()
                Dim prompt As Task(Of String) = Nothing
                Select Case boundary
                    Case "detail" : ChangePackMode(PackBrowserMode.Detail)
                    Case "local" : ChangePackMode(PackBrowserMode.Local)
                    Case "vanilla" : SetDownloadView(DownloadView.Versions)
                    Case "page-gate" : PageSurface.IsHitTestVisible = False
                    Case "view-gate" : DownloadViewHost.IsHitTestVisible = False
                    Case "result-gate" : PackSearchResultsHost.IsHitTestVisible = False
                    Case "disabled" : PageSurface.IsEnabled = False
                    Case "busy" : _busy = True
                    Case "closing" : _closing = True
                    Case "prompt" : prompt = PromptOnlinePackAsync("输入版本名称", "分页入口交接", _selectedFolder.Location, "Paging-Entry", False, CancellationToken.None)
                End Select
                layout() : reject(boundary)
                If prompt IsNot Nothing Then
                    CancelOnlinePackName(Nothing, Nothing) : _onlinePromptMotion?.Controller.SkipToFill()
                    Await WaitPackUiAsync(Function() _onlinePromptMotion Is Nothing)
                    Await prompt
                End If
            Next
            resumeSearch() : _motionPreview = True : _categoryMotion.PauseForChecks = True
            ChangePackMode(PackBrowserMode.Detail) : ChangePackMode(PackBrowserMode.Search) : layout()
            Dim returning = _categoryMotion.Transition
            reject("rapid-return-entry")
            PauseSelectionClock(returning) : returning.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(175), System.Windows.Media.Animation.TimeSeekOrigin.BeginTime) : layout()
            PackNextPage.BringIntoView() : layout()
            Dim revealed As New JObject From {{"accepted", CanUsePackSearchInput(PackNextPage)}, {"enabled", PackNextPage.IsEnabled}, {"visible", PackNextPage.IsVisible}, {"hitTest", PackNextPage.IsHitTestVisible},
                {"pageGate", PageSurface.IsHitTestVisible}, {"viewGate", DownloadViewHost.IsHitTestVisible}, {"resultsGate", PackSearchResultsHost.IsHitTestVisible}, {"formGate", _searchInputPolicy.Interactive}, {"pageBefore", _packSearchPage}}
            PointerEvent(PackNextPage, Mouse.MouseDownEvent) : PointerEvent(PackNextPage, Mouse.MouseUpEvent)
            Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
            revealed("pageAfter") = _packSearchPage : revealed("sameSession") = _packSearchSession Is session
            revealed("firstRow") = If(PackProjectRows.Children.OfType(Of PclResourceItem)().Any(), DirectCast(PackProjectRows.Children.OfType(Of PclResourceItem)().First().Tag, ModpackProject).Id, "")
            IO.File.WriteAllText(IO.Path.Combine(directory, "paging-entry-revealed.json"), revealed.ToString())
            Dim expectedIds = Enumerable.Range(2 * ModpackCatalogClient.PageSize, ModpackCatalogClient.PageSize).Select(Function(index) "pagingentry" & index).OrderBy(Function(id) id, StringComparer.Ordinal)
            Dim actualIds = PackProjectRows.Children.OfType(Of PclResourceItem)().Select(Function(row) DirectCast(row.Tag, ModpackProject).Id).OrderBy(Function(id) id, StringComparer.Ordinal)
            check(_packSearchPage = 2 AndAlso _packSearchSession Is session AndAlso actualIds.SequenceEqual(expectedIds),
                "a real next-page click works after input reveal while retaining the accepted search identity")
            _motionPreview = False : resumeSearch()
            Dim fetched = calls
            PointerEvent(PackPreviousPage, Mouse.MouseDownEvent) : PointerEvent(PackPreviousPage, Mouse.MouseUpEvent)
            Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
            check(_packSearchPage = 1 AndAlso calls = fetched AndAlso _packSearchSession Is session, "the real previous-page button returns through the accepted cache without another fetch")
            PointerEvent(PackFirstPage, Mouse.MouseDownEvent) : PointerEvent(PackFirstPage, Mouse.MouseUpEvent)
            Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
            check(_packSearchPage = 0 AndAlso calls = fetched AndAlso _packSearchSession Is session, "the real first-page button retains normal cached navigation")
            _packCatalogFactory = Function() New ModpackCatalogClient(New PackSearchFixtureHandler With {.BeforeSendAsync = countFetch, .SearchPage = Function(uri)
                                                                                                                        Dim offset = PackFixtureOffset(uri), hits As New JArray
                                                                                                                        For index = offset To Math.Min(offset + 39, 160)
                                                                                                                            Dim item = PackSearchFixtureHandler.ProjectJson("continueentry" & index)
                                                                                                                            If index < 160 Then item("project_type") = "mod"
                                                                                                                            hits.Add(item)
                                                                                                                        Next
                                                                                                                        Return New JObject From {{"hits", hits}, {"total_hits", 161}}
                                                                                                                    End Function}, "")
            PackSearchText.Text = "paging-continuation-entry" : RunPackSearch(Nothing, Nothing)
            Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
            If PackNextPage.Text <> "继续加载" OrElse Not PackNextPage.IsEnabled Then Throw New InvalidOperationException("Paging continuation fixture was not accepted.")
            PointerEvent(PackNextPage, Mouse.MouseDownEvent) : PointerEvent(PackNextPage, Mouse.MouseUpEvent)
            Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
            check(_packSearchPage = 0 AndAlso _packSearchSession.Count = 1 AndAlso PackProjectRows.Children.OfType(Of PclResourceItem)().Count() = 1 AndAlso _taskGeneration = generation,
                "the real continue-loading button fills the current presentation page instead of skipping an empty batch")
        Finally
            _packSearchRequest?.Cancel()
            CompleteOnlinePrompt(Nothing) : StopOnlinePromptMotion() : HideOnlinePrompt()
            resumeSearch()
            _packCatalogFactory = factory : _motionPreview = motion : _categoryMotion.PauseForChecks = paused
        End Try
    End Function
End Class
