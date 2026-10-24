Imports System.Windows
Imports System.Windows.Input
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Async Function VerifySearchEntryOwnershipAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim factory = _packCatalogFactory, motion = _motionPreview, paused = _categoryMotion.PauseForChecks, calls = 0
        Dim generation = _taskGeneration
        Dim observations As New JArray
        Dim resumeSearch As Action =
            Sub()
                _busy = False : _closing = False : _choosingOnline = False
                PageSurface.IsEnabled = True : PageSurface.IsHitTestVisible = True : DownloadViewHost.IsHitTestVisible = True
                ShowDownload(Nothing, Nothing) : SetDownloadView(DownloadView.Modpacks) : ChangePackMode(PackBrowserMode.Search)
                _surfaceMotion.Finish() : _categoryMotion.Finish() : _pageSwitch.Finish() : layout()
            End Sub
        Try
            _motionPreview = False
            resumeSearch()
            _packCatalogFactory = Function() New ModpackCatalogClient(New PackSearchFixtureHandler With {.BeforeSendAsync = Function(uri, token)
                                                                                                                            Interlocked.Increment(calls)
                                                                                                                            Return Task.CompletedTask
                                                                                                                        End Function}, "")
            PackSearchText.Text = "entry-baseline" : PackSearchSource.SelectedIndex = 2
            RunPackSearch(Nothing, Nothing) : Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
            Dim accepted = _packSearchSession
            ShowSettings(Nothing, Nothing) : _surfaceMotion.Finish() : layout()
            PackSearchText.Text = "retired-search-input"
            ControlKey(PackSearchText, Key.Enter, True)
            Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
            IO.File.WriteAllText(IO.Path.Combine(directory, "search-entry-state.json"), New JObject From {
                {"settingsVisible", SettingsPage.IsVisible}, {"sessionChanged", _packSearchSession IsNot accepted}, {"acceptedText", _packSearchSession.Query.Text}}.ToString())
            check(SettingsPage.IsVisible AndAlso _packSearchSession Is accepted,
                "queued Enter from a hidden search editor cannot replace the accepted search session on Settings")
            resumeSearch()
            Dim reject As Action(Of String) =
                Sub(label)
                    Dim session = _packSearchSession, acceptedKey = _packLastQuery, page = _packSearchPage, project = _packProject, before = calls
                    Dim text = PackSearchText.Text, game = PackSearchGame.Text, sourceIndex = PackSearchSource.SelectedIndex, category = PackSearchCategory.SelectedIndex
                    Dim rows = PackProjectRows.Children.Cast(Of Object)().ToArray()
                    Dim pageMotion = _categoryMotion.Transition, rootMotion = _surfaceMotion.Transition
                    SubmitPackSearch(PackSearchButton, New RoutedEventArgs())
                    ResetPackSearchFromUi(PackSearchReset, New RoutedEventArgs())
                    ControlKey(PackSearchText, Key.Enter, True) : ControlKey(PackSearchGame, Key.Enter, True)
                    Dim unchanged = _packSearchSession Is session AndAlso _packLastQuery = acceptedKey AndAlso _packSearchPage = page AndAlso _packProject Is project AndAlso calls = before AndAlso
                        PackSearchText.Text = text AndAlso PackSearchGame.Text = game AndAlso PackSearchSource.SelectedIndex = sourceIndex AndAlso PackSearchCategory.SelectedIndex = category AndAlso
                        PackProjectRows.Children.Cast(Of Object)().SequenceEqual(rows) AndAlso _taskGeneration = generation AndAlso
                        _categoryMotion.Transition Is pageMotion AndAlso _surfaceMotion.Transition Is rootMotion
                    observations.Add(New JObject From {{"boundary", label}, {"unchanged", unchanged}, {"extraFetches", calls - before}})
                    IO.File.WriteAllText(IO.Path.Combine(directory, "search-entry-ownership.json"), observations.ToString())
                    check(unchanged, "inactive search entry preserves draft filters, accepted results and request ownership: " & label)
                End Sub
            For Each boundary In {"detail", "local", "vanilla", "page-gate", "view-gate", "disabled", "busy", "closing", "prompt"}
                resumeSearch()
                PackSearchText.Text = "pending-" & boundary : PackSearchGame.Text = "1.20.1"
                PackSearchSource.SelectedIndex = 2 : PackSearchCategory.SelectedIndex = 1
                Dim prompt As Task(Of String) = Nothing
                Select Case boundary
                    Case "detail" : ChangePackMode(PackBrowserMode.Detail)
                    Case "local" : ChangePackMode(PackBrowserMode.Local)
                    Case "vanilla" : SetDownloadView(DownloadView.Versions)
                    Case "page-gate" : PageSurface.IsHitTestVisible = False
                    Case "view-gate" : DownloadViewHost.IsHitTestVisible = False
                    Case "disabled" : PageSurface.IsEnabled = False
                    Case "busy" : _busy = True
                    Case "closing" : _closing = True
                    Case "prompt" : prompt = PromptOnlinePackAsync("输入版本名称", "搜索入口交接", _selectedFolder.Location, "Search-Entry", False, CancellationToken.None)
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
            PackSearchText.Text = "visible-button-query"
            PointerEvent(PackSearchButton, Mouse.MouseDownEvent) : PointerEvent(PackSearchButton, Mouse.MouseUpEvent)
            Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
            check(_packSearchSession.Query.Text = "visible-button-query" AndAlso _packMode = PackBrowserMode.Search AndAlso _taskGeneration = generation,
                "the real search button submits accepted criteria once input is revealed during page entry")
            _motionPreview = False : _categoryMotion.Finish() : _surfaceMotion.Finish() : FinishPackContentMotion() : layout()
            Dim submitted = _packSearchSession
            PointerEvent(PackSearchReset, Mouse.MouseDownEvent) : PointerEvent(PackSearchReset, Mouse.MouseUpEvent) : layout()
            check(PackSearchText.Text = "" AndAlso PackSearchGame.Text.StartsWith("全部", StringComparison.Ordinal) AndAlso PackSearchSource.SelectedIndex = 0 AndAlso PackSearchCategory.SelectedIndex = 0 AndAlso
                _packSearchSession Is submitted, "the real reset icon clears only draft criteria while preserving accepted search results")
            For Each editor As UIElement In New UIElement() {PackSearchText, PackSearchGame}
                PackSearchSource.SelectedIndex = 2 : PackSearchText.Text = If(editor Is PackSearchText, "visible-text-enter", "visible-game-enter")
                ControlKey(editor, Key.Enter, True) : Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
                check(_packSearchSession.Query.Text = PackSearchText.Text AndAlso _taskGeneration = generation,
                    "visible search editor Enter retains its normal accepted-query flow: " & If(editor Is PackSearchText, "name", "game"))
            Next
        Finally
            _packSearchRequest?.Cancel()
            CompleteOnlinePrompt(Nothing) : StopOnlinePromptMotion() : HideOnlinePrompt()
            resumeSearch() : FinishPackContentMotion()
            _packCatalogFactory = factory : _motionPreview = motion : _categoryMotion.PauseForChecks = paused
        End Try
    End Function
End Class
