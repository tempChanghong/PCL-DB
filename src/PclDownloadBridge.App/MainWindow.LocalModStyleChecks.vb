Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Documents
Imports System.Windows.Input
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Sub PrepareLocalModStylePage(directory As String, layout As Action)
        PrepareLocalModFixture(directory)
        ShowManagement(Nothing, Nothing) : ScanManagementAsync(False).GetAwaiter().GetResult() : layout()
        SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = "Fabric 实例")) : layout()
        ShowLocalMods(Nothing, Nothing) : LoadLocalModsAsync(False).GetAwaiter().GetResult() : layout()
    End Sub
    Private Shared Function LocalModCardMetricsForChecks(owner As MainWindow) As JObject
        Dim filters = DirectCast(owner.LocalModAll.Parent, FrameworkElement), list = DirectCast(owner.LocalModRows.Parent, FrameworkElement)
        Dim filterOrigin = filters.TranslatePoint(New Point(), owner.LocalModListCard), listOrigin = list.TranslatePoint(New Point(), owner.LocalModListCard)
        Dim rowsOrigin = owner.LocalModRows.TranslatePoint(New Point(), owner.LocalModListCard)
        Return New JObject From {{"actionsHeight", owner.LocalModActionsCard.ActualHeight},
            {"firstActionY", owner.LocalModOpen.TranslatePoint(New Point(), owner.LocalModActionsCard).Y},
            {"filterX", filterOrigin.X}, {"filterY", filterOrigin.Y}, {"filterHeight", filters.ActualHeight},
            {"rowsX", rowsOrigin.X}, {"rowsY", rowsOrigin.Y},
            {"listRight", owner.LocalModListCard.ActualWidth - listOrigin.X - list.ActualWidth},
            {"listBottom", owner.LocalModListCard.ActualHeight - listOrigin.Y - list.ActualHeight}}
    End Function
    Private Shared Sub VerifyLocalModCardMetricsForChecks(owner As MainWindow, stage As String, check As Action(Of Boolean, String))
        Dim metrics = LocalModCardMetricsForChecks(owner), close As Func(Of String, Double, Boolean) = Function(key, target) Math.Abs(CDbl(metrics(key)) - target) < 0.1
        check(close("actionsHeight", 65) AndAlso close("firstActionY", 15),
            "local Mod actions retain PCL's sixty-five-DIP card and fifteen-DIP top inset: " & stage)
        check(close("filterX", 15) AndAlso close("filterY", 13) AndAlso close("filterHeight", 28),
            "local Mod filters retain PCL's actual fifteen-thirteen origin and twenty-eight-DIP height: " & stage)
        check(close("rowsX", 20) AndAlso close("rowsY", 48) AndAlso close("listRight", 18) AndAlso close("listBottom", 22),
            "local Mod list retains PCL's actual twenty-forty-eight-eighteen-twenty-two insets: " & stage)
        Dim filters = DirectCast(owner.LocalModAll.Parent, Panel), expected = {owner.LocalModAll, owner.LocalModEnabled, owner.LocalModDisabled, owner.LocalModUpdatable}
        check(filters.Children.OfType(Of PclRadioButton)().SequenceEqual(expected),
            "local Mod filters retain PCL's all enabled disabled update order: " & stage)
        Dim first = owner.LocalModRows.Children.OfType(Of PclLocalModItem)().FirstOrDefault()
        If first IsNot Nothing Then
            check(Math.Abs(first.TitleLabel.TranslatePoint(New Point(), owner.LocalModListCard).X - 67) < 0.1 AndAlso first.ActualHeight = 44,
                "local Mod text combines the original card inset with the forty-seven-DIP row origin: " & stage)
        End If
    End Sub
    Private Sub VerifyLocalModStyleUi(directory As String, check As Action(Of Boolean, String))
        Dim nativeFocus = LocalModCheckGetNativeFocus(), owner As New MainWindow(True), original = owner.Content
        Dim host As New Border With {.Width = 850, .Height = 700}, layout = ControlLayout(host)
        FocusManager.SetIsFocusScope(host, True)
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using source = PreviewSurface.Attach(host, 850, 700)
                layout() : owner.PrepareLocalModStylePage(directory, layout)
                owner._localModSearchClearPolicy.EnabledForChecks = False
                ' Save both old and new layouts before a geometry assertion can fail.
                For Each dark In {False, True}
                    owner.RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", dark}}) : layout()
                    Dim name = "local-mod-card-geometry-" & If(dark, "dark", "light")
                    SaveControlVisual(host, Path.Combine(directory, name & ".png"))
                    File.WriteAllText(Path.Combine(directory, name & ".json"), LocalModCardMetricsForChecks(owner).ToString())
                Next
                owner.RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", False}}) : layout()
                VerifyLocalModCardMetricsForChecks(owner, "initial", check)
                Dim contentHost = DirectCast(owner.LocalModSearch.Template.FindName("PART_ContentHost", owner.LocalModSearch), ScrollViewer)
                Dim placeholder = DirectCast(owner.LocalModSearch.Template.FindName("Placeholder", owner.LocalModSearch), TextBlock)
                check(owner.LocalModSearchCard.ActualHeight = 40 AndAlso owner.LocalModSearch.ActualHeight = 40 AndAlso
                    owner.LocalModSearch.BorderThickness = New Thickness(0) AndAlso Not owner.LocalModSearch.HasBackground AndAlso
                    owner.LocalModSearch.Padding = New Thickness(32, 0, 40, 0) AndAlso owner.LocalModSearchIcon.Width = 14 AndAlso owner.LocalModSearchIcon.Margin.Left = 14 AndAlso
                    contentHost.TranslatePoint(New Point(), owner.LocalModSearch).X >= 32 AndAlso placeholder.TranslatePoint(New Point(), owner.LocalModSearch).X >= 32 AndAlso
                    contentHost.Margin.Right = 40 AndAlso placeholder.Margin.Right = 40 AndAlso placeholder.IsVisible,
                    "local Mod search uses the fixed PCL forty-DIP card transparent editor and original magnifier inset")
                check(owner.LocalModSearchClear.Width = 24 AndAlso owner.LocalModSearchClear.Height = 24 AndAlso owner.LocalModSearchClear.LogoScale = 0.75 AndAlso
                    owner.LocalModSearchClear.Margin.Right = 10 AndAlso owner.LocalModSearchClear.Opacity = 0 AndAlso Not owner.LocalModSearchClear.IsHitTestVisible AndAlso Not owner.LocalModSearchClear.Focusable,
                    "an empty local search keeps the original twenty-four-DIP clear icon invisible and outside keyboard or pointer input")
                check(owner.LocalModOpen.Highlight AndAlso {owner.LocalModOpen, owner.LocalModImport, owner.LocalModDownload, owner.LocalModSelectAll, owner.LocalModCancel}.All(Function(button) button.Height = 35) AndAlso
                    owner.LocalModOpen.Margin.Right = 15 AndAlso owner.LocalModImport.Margin.Right = 15 AndAlso owner.LocalModDownload.Margin.Right = 15 AndAlso owner.LocalModRefresh.Width = 28,
                    "local Mod actions preserve PCL's highlighted folder action thirty-five-DIP height and fifteen-DIP horizontal gaps")
                Dim emptyStatusHeight = owner.LocalModActionsCard.ActualHeight, emptyCollapsed = owner.LocalModStatus.Visibility = Visibility.Collapsed
                owner.LocalModStatus.Text = "正在读取 Mod 列表…" : layout()
                Dim readingVisible = owner.LocalModStatus.IsVisible AndAlso owner.LocalModStatus.ActualHeight > 0 AndAlso
                    owner.LocalModActionsCard.ActualHeight - emptyStatusHeight >= owner.LocalModStatus.ActualHeight + owner.LocalModStatus.Margin.Top - 1
                owner.LocalModStatus.Text = "操作已取消，可重新尝试。" : layout()
                Dim cancelVisible = owner.LocalModStatus.IsVisible AndAlso owner.LocalModStatus.Text.Contains("取消")
                owner.LocalModStatus.Text = "" : layout()
                check(emptyCollapsed AndAlso readingVisible AndAlso cancelVisible AndAlso owner.LocalModStatus.Visibility = Visibility.Collapsed AndAlso
                    Math.Abs(owner.LocalModActionsCard.ActualHeight - emptyStatusHeight) < 0.5,
                    "empty action feedback consumes no row or margin while reading and cancellation messages restore their measured status row")
                check(owner.LocalModAll.Text = "全部 (4)" AndAlso owner.LocalModEnabled.Text = "启用 (3)" AndAlso owner.LocalModDisabled.Text = "禁用 (1)" AndAlso
                    owner.LocalModEnabled.IsVisible AndAlso owner.LocalModDisabled.IsVisible,
                    "mixed local files expose both state filters with counts taken from the current candidate set")
                Dim entries = owner._localMods, first = entries.Single(Function(entry) entry.FileName = "first.jar"), row = owner._localModRowViews(first)
                owner.LocalModSearch.Text = "enabled_fixture" : layout()
                check(owner.LocalModAll.Text = "搜索结果 (1)" AndAlso owner.LocalModEnabled.Text = "启用 (1)" AndAlso owner.LocalModDisabled.Text = "禁用 (0)" AndAlso
                    Not owner.LocalModEnabled.IsVisible AndAlso Not owner.LocalModDisabled.IsVisible AndAlso owner.LocalModRows.Children.Count = 1 AndAlso
                    owner.LocalModRows.Children(0) Is row AndAlso owner._localMods Is entries,
                    "one enabled search match updates candidate counts and hides redundant state filters without rescanning or replacing its row")
                owner.LocalModSearch.Text = "" : owner.FilterLocalMods(owner.LocalModDisabled, Nothing)
                owner.LocalModSearch.Text = "enabled_fixture" : layout()
                check(owner._localModFilter = "disabled" AndAlso owner.LocalModDisabled.Checked AndAlso owner.LocalModDisabled.IsVisible AndAlso
                    owner.LocalModDisabled.Text = "禁用 (0)" AndAlso owner.LocalModRows.Children.Count = 0 AndAlso owner.LocalModEmpty.IsVisible AndAlso Not owner.LocalModEnabled.IsVisible,
                    "an already selected zero-match disabled filter stays visible so the user can return through search results")
                owner.LocalModSearch.Text = "" : owner.FilterLocalMods(owner.LocalModEnabled, Nothing)
                owner.LocalModSearch.Text = "disabled_fixture" : layout()
                check(owner.LocalModAll.Text = "搜索结果 (1)" AndAlso owner.LocalModEnabled.Checked AndAlso owner.LocalModEnabled.IsVisible AndAlso
                    owner.LocalModEnabled.Text = "启用 (0)" AndAlso owner.LocalModDisabled.Text = "禁用 (1)" AndAlso owner.LocalModDisabled.IsVisible AndAlso owner.LocalModRows.Children.Count = 0,
                    "an enabled filter with zero matches retains its own selector while exposing the disabled search candidate count")
                owner.LocalModSearch.Text = "no-such-mod" : layout()
                check(owner.LocalModAll.Text = "搜索结果 (0)" AndAlso owner.LocalModEnabled.IsVisible AndAlso owner.LocalModEnabled.Text = "启用 (0)" AndAlso
                    Not owner.LocalModDisabled.IsVisible AndAlso owner.LocalModRows.Children.Count = 0,
                    "an empty query result retains only its selected zero state and the zero-result return selector")
                owner.FocusLocalModElementForChecks(owner.LocalModSearchClear)
                owner.ClearLocalModSearch(owner.LocalModSearchClear, Nothing) : layout()
                check(owner.LocalModSearch.Text = "" AndAlso Keyboard.FocusedElement Is owner.LocalModSearch AndAlso owner._localModFilter = "enabled" AndAlso
                    owner.LocalModRows.Children.Count = 3 AndAlso owner.LocalModAll.Text = "全部 (4)" AndAlso Not owner.LocalModSearchClear.IsHitTestVisible,
                    "clearing search synchronously restores candidates and editor focus while retaining the chosen state filter")
                owner.LocalModSearch.Text = "   " : layout()
                check(owner.LocalModAll.Text = "全部 (4)" AndAlso owner.LocalModRows.Children.Count = 3 AndAlso owner.LocalModSearchClear.IsHitTestVisible AndAlso owner.LocalModSearchClear.Focusable,
                    "whitespace keeps an actionable clear button while candidate filtering follows PCL's non-searching semantics")
                Dim newer As TextChangedEventHandler = Sub()
                                                           If owner.LocalModSearch.Text.Length > 0 Then Return
                                                           PointerEvent(owner.LocalModRefresh, Mouse.PreviewMouseDownEvent)
                                                           owner.FocusLocalModElementForChecks(owner.LocalModRefresh)
                                                       End Sub
                AddHandler owner.LocalModSearch.TextChanged, newer
                Try
                    owner.FocusLocalModElementForChecks(owner.LocalModSearchClear)
                    owner.ClearLocalModSearch(owner.LocalModSearchClear, Nothing) : layout()
                Finally
                    RemoveHandler owner.LocalModSearch.TextChanged, newer
                End Try
                check(Keyboard.FocusedElement Is owner.LocalModRefresh AndAlso owner.LocalModSearch.Text = "",
                    "a newer deliberate control focus during search clearing supersedes the clear action's editor focus request")
                owner.LocalModSearch.Text = "keep-hidden" : owner.ShowSettings(Nothing, Nothing) : layout()
                owner.FocusLocalModElementForChecks(owner.SpeedLimitBox)
                owner.ClearLocalModSearch(owner.LocalModSearchClear, Nothing) : layout()
                check(owner.SettingsPage.IsVisible AndAlso owner.LocalModSearch.Text = "keep-hidden" AndAlso Keyboard.FocusedElement Is owner.SpeedLimitBox AndAlso owner._localModSearchClearMotion.Clock Is Nothing,
                    "a hidden clear action cannot mutate retained search text steal settings focus or retain its fade clock")
                owner.ShowManagement(Nothing, Nothing) : owner.ScanManagementAsync(False).GetAwaiter().GetResult() : layout()
                owner.ManagementViewHost.IsHitTestVisible = False
                owner.ClearLocalModSearch(owner.LocalModSearchClear, Nothing)
                owner.ManagementViewHost.IsHitTestVisible = True : layout()
                check(owner.LocalModSearch.Text = "keep-hidden" AndAlso ShortLocalModText("abcdef", 4) = "abc…" AndAlso
                    ShortLocalModText("ab" & Char.ConvertFromUtf32(&H1F600) & "cdef", 4) = "ab…" AndAlso ShortLocalModText("文本", 3) = "文本" AndAlso ShortLocalModText("a", 0) = "",
                    "page input gates reject clearing and bounded local text includes its ellipsis without splitting a surrogate pair")
                owner.LocalModSearch.Text = "" : owner.FilterLocalMods(owner.LocalModAll, Nothing)
                For Each dark In {False, True}
                    owner.RestoreAppearance(New Newtonsoft.Json.Linq.JObject From {{"theme", "blue"}, {"darkMode", dark}})
                    host.Width = 810 : host.Height = 470 : layout()
                    Dim actions = DirectCast(owner.LocalModOpen.Parent, WrapPanel)
                    Dim mainActions = {owner.LocalModOpen, owner.LocalModImport, owner.LocalModDownload, owner.LocalModSelectAll}
                    check(owner.LocalModSearchCard.ActualHeight = 40 AndAlso owner.LocalModSearch.ActualWidth > 600 AndAlso actions.ActualWidth > 500 AndAlso
                        mainActions.All(Function(button) Math.Abs(button.TranslatePoint(New Point(), owner.LocalModActionsCard).Y - 15) < 0.1 AndAlso button.TranslatePoint(New Point(), actions).X + button.ActualWidth <= actions.ActualWidth + 1) AndAlso
                        owner.LocalModRefresh.TranslatePoint(New Point(), owner.LocalModActionsCard).X + owner.LocalModRefresh.ActualWidth <= owner.LocalModActionsCard.ActualWidth - 14,
                        "compact local search and wrapping actions fit the minimum window in dark mode " & dark)
                    VerifyLocalModCardMetricsForChecks(owner, "minimum width dark=" & dark, check)
                    owner.FilterLocalMods(owner.LocalModDisabled, Nothing) : layout()
                    Dim disabledRow = owner.LocalModRows.Children.OfType(Of PclLocalModItem)().Single()
                    owner.FocusLocalModElementForChecks(owner.LocalModSearch)
                    check(Not DirectCast(disabledRow.Tag, LocalModEntry).Enabled AndAlso owner.LocalModDisabled.Checked AndAlso Keyboard.FocusedElement Is owner.LocalModSearch,
                        "filtering the inset local list retains its physical disabled file and search focus in dark mode " & dark)
                    VerifyLocalModCardMetricsForChecks(owner, "disabled filter dark=" & dark, check)
                    owner.FilterLocalMods(owner.LocalModAll, Nothing) : layout()
                    Dim updateEntry = owner._localMods.Single(Function(item) item.FileName = "first.jar")
                    Dim project As New ModpackProject With {.Kind = CommunityResourceKind.Mod, .Platform = ModpackPlatform.Modrinth, .Id = "geometry"}
                    Dim current As New ModpackRelease With {.Kind = project.Kind, .Platform = project.Platform, .ProjectId = project.Id, .Id = "before01", .Name = "before", .FileName = updateEntry.FileName}
                    Dim release = current.Copy() : release.Id = "after001" : release.Name = "geometry update"
                    owner._localModUpdates(updateEntry.FilePath) = New LocalModUpdate(updateEntry, project, current, release)
                    owner.RenderLocalMods() : layout()
                    Dim updateRow = owner._localModRowViews(updateEntry)
                    Dim visibleFilters = DirectCast(owner.LocalModAll.Parent, Panel).Children.OfType(Of PclRadioButton)().Where(Function(button) button.IsVisible).ToArray()
                    check(visibleFilters.SequenceEqual({owner.LocalModAll, owner.LocalModEnabled, owner.LocalModDisabled, owner.LocalModUpdatable}) AndAlso
                        visibleFilters.Zip(visibleFilters.Skip(1), Function(left, right) left.TranslatePoint(New Point(), owner.LocalModListCard).X + left.ActualWidth <= right.TranslatePoint(New Point(), owner.LocalModListCard).X + 0.1).All(Function(value) value),
                        "all four visible local filters follow PCL order without overlap in dark mode " & dark)
                    owner.FilterLocalMods(owner.LocalModUpdatable, Nothing) : layout()
                    check(owner.LocalModRows.Children.Count = 1 AndAlso owner.LocalModRows.Children(0) Is updateRow AndAlso owner.LocalModUpdatable.Checked AndAlso Keyboard.FocusedElement Is owner.LocalModSearch,
                        "the reordered update selector retains the attached eligible row and editor focus in dark mode " & dark)
                    VerifyLocalModCardMetricsForChecks(owner, "update filter dark=" & dark, check)
                    owner._localModUpdates.Clear() : owner.FilterLocalMods(owner.LocalModAll, Nothing) : layout()
                    SaveControlVisual(host, Path.Combine(directory, "local-mod-style-" & If(dark, "dark", "light") & ".png"))
                    owner.LocalModActionsCard.Width = 520 : owner.LocalModCancel.Visibility = Visibility.Visible : layout()
                    Dim selectOrigin = owner.LocalModSelectAll.TranslatePoint(New Point(), actions), downloadOrigin = owner.LocalModDownload.TranslatePoint(New Point(), actions)
                    Dim bottom = mainActions.Concat({owner.LocalModCancel}).Max(Function(button) button.TranslatePoint(New Point(), owner.LocalModActionsCard).Y + button.ActualHeight)
                    check(selectOrigin.Y > downloadOrigin.Y AndAlso Math.Abs(selectOrigin.Y - downloadOrigin.Y - owner.LocalModDownload.ActualHeight - 7) < 0.1 AndAlso
                        Math.Abs(owner.LocalModActionsCard.ActualHeight - bottom - 15) < 0.1 AndAlso mainActions.Concat({owner.LocalModCancel}).All(Function(button) button.TranslatePoint(New Point(), actions).X + button.ActualWidth <= actions.ActualWidth + 0.1),
                        "wrapped local actions preserve seven-DIP row gaps and fifteen-DIP card clearance in dark mode " & dark)
                    Dim wrappedHeight = owner.LocalModActionsCard.ActualHeight
                    owner.LocalModStatus.Text = "操作已取消，可重新尝试。" : layout()
                    check(owner.LocalModStatus.IsVisible AndAlso owner.LocalModStatus.TranslatePoint(New Point(), owner.LocalModActionsCard).Y >= bottom + 4.9 AndAlso
                        owner.LocalModActionsCard.ActualHeight >= wrappedHeight + owner.LocalModStatus.ActualHeight + 4.9,
                        "wrapped local actions leave a measured feedback row after cancellation in dark mode " & dark)
                    owner.LocalModStatus.Text = "" : owner.LocalModCancel.Visibility = Visibility.Collapsed : owner.LocalModActionsCard.Width = Double.NaN
                    host.Width = 1050 : layout()
                    VerifyLocalModCardMetricsForChecks(owner, "wide width dark=" & dark, check)
                Next
            End Using
        Finally
            owner._closing = True : owner.CancelLocalModRequest() : owner.CancelManagementRequest()
            owner._managementMotion.Finish() : owner._surfaceMotion.Finish()
            host.Child = Nothing : owner.Content = original
            LocalModCheckSetNativeFocus(nativeFocus)
        End Try
    End Sub

    Private Sub VerifyLocalModStyleMotion(directory As String, check As Action(Of Boolean, String))
        Dim nativeFocus = LocalModCheckGetNativeFocus(), owner As New MainWindow(True), original = owner.Content
        Dim host As New Border With {.Width = 850, .Height = 700}, layout = ControlLayout(host)
        FocusManager.SetIsFocusScope(host, True)
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using source = PreviewSurface.Attach(host, 850, 700)
                layout() : owner.PrepareLocalModStylePage(directory, layout)
                owner.FocusLocalModElementForChecks(owner.LocalModSearch)
                owner._localModSearchClearPolicy.EnabledForChecks = True : owner._localModSearchClearPolicy.PauseForChecks = True
                owner.LocalModSearch.Text = "enabled_fixture" : layout()
                Dim appearing = owner._localModSearchClearMotion.Clock
                check(appearing IsNot Nothing AndAlso appearing.Timeline.Duration.TimeSpan.TotalMilliseconds = 90 AndAlso owner.LocalModSearchClear.IsHitTestVisible AndAlso
                    owner.LocalModRows.Children.Count = 1 AndAlso owner.LocalModAll.Text = "搜索结果 (1)",
                    "local search filters immediately while its original PCL clear icon starts a ninety-millisecond fade")
                appearing.Controller.Pause()
                If Not PumpMotionUntil(Function() appearing.IsPaused) Then Throw New TimeoutException("Local clear fade did not pause.")
                appearing.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(45), TimeSeekOrigin.BeginTime) : layout()
                check(Math.Abs(owner.LocalModSearchClear.Opacity - 0.5) < 0.05 AndAlso Keyboard.FocusedElement Is owner.LocalModSearch,
                    "the live clear icon samples PCL's linear half-opacity without replacing the search editor focus")
                owner.LocalModSearch.Text = "" : layout()
                Dim disappearing = owner._localModSearchClearMotion.Clock
                owner.LocalModSearch.Text = "disabled_fixture" : layout()
                Dim replacement = owner._localModSearchClearMotion.Clock
                appearing.Controller.SkipToFill() : disappearing.Controller.SkipToFill() : layout()
                check(owner._localModSearchClearMotion.Clock Is replacement AndAlso owner.LocalModSearchClear.IsHitTestVisible AndAlso owner.LocalModRows.Children.Count = 1 AndAlso owner.LocalModDisabled.Text = "禁用 (1)",
                    "rapid query reversal replaces only its own clear fade and obsolete clocks cannot hide the latest search action")
                replacement.Controller.Pause()
                If Not PumpMotionUntil(Function() replacement.IsPaused) Then Throw New TimeoutException("Replacement clear fade did not pause.")
                replacement.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(91), TimeSeekOrigin.BeginTime) : layout()
                owner.FocusLocalModElementForChecks(owner.LocalModSearchClear)
                owner.ClearLocalModSearch(owner.LocalModSearchClear, Nothing) : layout()
                Dim cleared = owner._localModSearchClearMotion.Clock
                check(cleared IsNot Nothing AndAlso cleared.Timeline.Duration.TimeSpan.TotalMilliseconds = 90 AndAlso Not owner.LocalModSearchClear.IsHitTestVisible AndAlso
                    Not owner.LocalModSearchClear.Focusable AndAlso owner.LocalModRows.Children.Count = 4 AndAlso Keyboard.FocusedElement Is owner.LocalModSearch,
                    "clearing search starts the original ninety-millisecond exit while immediately retiring clear input and restoring editor focus")
                owner.LocalModSearch.Text = "enabled_fixture" : layout()
                owner._motionPreview = True : owner._managementMotion.PauseForChecks = True : owner._surfaceMotion.PauseForChecks = True
                owner.LoadLocalModsAsync(True).GetAwaiter().GetResult() : layout()
                Dim page = owner._managementMotion.Transition
                owner.ClearLocalModSearch(owner.LocalModSearchClear, Nothing)
                Dim rejected = owner.LocalModSearch.Text = "enabled_fixture"
                owner.LocalModSearch.SelectAll() : EditingCommands.Delete.Execute(Nothing, owner.LocalModSearch) : layout()
                check(rejected AndAlso owner.LocalModSearch.Text = "" AndAlso owner._localModFilterPending AndAlso owner.LocalModRows.Children.Count = 1 AndAlso Not owner.LocalModSearchClear.IsHitTestVisible,
                    "clear activation respects the card gate while native editor changes retain a pending search and immediately retire its clear input")
                PauseSelectionClock(page) : page.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(190), TimeSeekOrigin.BeginTime) : layout()
                check(owner.LocalModRows.Children.Count = 4 AndAlso owner.LocalModAll.Text = "全部 (4)" AndAlso Not owner._localModFilterPending AndAlso
                    owner._localModSearchClearMotion.Clock Is Nothing AndAlso owner.LocalModSearchClear.Opacity = 0 AndAlso Keyboard.FocusedElement Is owner.LocalModSearch,
                    "PCL card handoff applies the latest pending query and settles clear opacity without competing with page focus")
                owner.LocalModSearch.Text = "disabled_fixture" : layout()
                Dim obsolete = owner._localModSearchClearMotion.Clock
                owner.ShowSettings(Nothing, Nothing) : owner._surfaceMotion.Finish() : layout()
                owner.FocusLocalModElementForChecks(owner.SpeedLimitBox)
                owner.ClearLocalModSearch(owner.LocalModSearchClear, Nothing) : obsolete.Controller.SkipToFill() : layout()
                check(owner.SettingsPage.IsVisible AndAlso owner.LocalModSearch.Text = "disabled_fixture" AndAlso owner._localModSearchClearMotion.Clock Is Nothing AndAlso
                    Keyboard.FocusedElement Is owner.SpeedLimitBox,
                    "leaving local Mods retires its clear opacity clock and rejects hidden clearing or obsolete focus completion")
            End Using
        Finally
            owner._closing = True : owner.CancelLocalModRequest() : owner.CancelManagementRequest()
            owner._managementMotion.Finish() : owner._surfaceMotion.Finish()
            host.Child = Nothing : owner.Content = original
            LocalModCheckSetNativeFocus(nativeFocus)
        End Try
    End Sub
End Class
