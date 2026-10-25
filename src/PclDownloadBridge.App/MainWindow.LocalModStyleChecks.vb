Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Documents
Imports System.Windows.Input
Imports System.Windows.Media.Animation
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Sub PrepareLocalModStylePage(directory As String, layout As Action)
        PrepareLocalModFixture(directory)
        ShowManagement(Nothing, Nothing) : ScanManagementAsync(False).GetAwaiter().GetResult() : layout()
        SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = "Fabric 实例")) : layout()
        ShowLocalMods(Nothing, Nothing) : LoadLocalModsAsync(False).GetAwaiter().GetResult() : layout()
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
                check(owner.LocalModOpen.Highlight AndAlso {owner.LocalModOpen, owner.LocalModImport, owner.LocalModRefresh, owner.LocalModCancel}.All(Function(button) button.Height = 35) AndAlso
                    owner.LocalModOpen.Margin.Right = 15 AndAlso owner.LocalModImport.Margin.Right = 15 AndAlso owner.LocalModRefresh.Margin.Right = 15,
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
                    check(owner.LocalModSearchCard.ActualHeight = 40 AndAlso owner.LocalModSearch.ActualWidth > 600 AndAlso actions.ActualWidth > 400 AndAlso
                        owner.LocalModOpen.TranslatePoint(New Point(), actions).Y = owner.LocalModRefresh.TranslatePoint(New Point(), actions).Y AndAlso
                        owner.LocalModRefresh.TranslatePoint(New Point(), actions).X + owner.LocalModRefresh.ActualWidth <= actions.ActualWidth + 1,
                        "compact local search and wrapping actions fit the minimum window in dark mode " & dark)
                    SaveControlVisual(host, Path.Combine(directory, "local-mod-style-" & If(dark, "dark", "light") & ".png"))
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
