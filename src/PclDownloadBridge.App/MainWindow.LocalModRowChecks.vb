Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Shared Function LocalModRowBackForChecks(row As PclLocalModItem) As Border
        Return row.Children.OfType(Of Border)().Single(Function(border) Grid.GetColumnSpan(border) = 6 AndAlso TypeOf border.RenderTransform Is ScaleTransform)
    End Function
    Private Shared Sub SeekLocalModActionForChecks(clock As AnimationClock, milliseconds As Double, layout As Action)
        clock.Controller.Pause()
        If Not PumpMotionUntil(Function() clock.IsPaused) Then Throw New TimeoutException("Local Mod action clock did not pause.")
        Dim activeTime = Math.Max(0, milliseconds - clock.Timeline.BeginTime.GetValueOrDefault().TotalMilliseconds)
        clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(activeTime), TimeSeekOrigin.BeginTime) : layout()
    End Sub
    Private Sub VerifyLocalModRowUi(directory As String, check As Action(Of Boolean, String))
        Dim nativeFocus = LocalModCheckGetNativeFocus(), owner As New MainWindow(True), original = owner.Content
        Dim host As New Border With {.Width = 850, .Height = 700}, layout = ControlLayout(host)
        FocusManager.SetIsFocusScope(host, True)
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using source = PreviewSurface.Attach(host, 850, 700)
                layout() : owner.PrepareLocalModStylePage(directory, layout)
                Dim entry = owner._localMods.Single(Function(item) item.FileName = "first.jar")
                Dim row = DirectCast(owner._localModRowViews(entry), PclLocalModItem)
                Dim iconFrame = DirectCast(row.Icon.Parent, Border), clip = DirectCast(iconFrame.Clip, RectangleGeometry)
                check(row.ActualHeight = 44 AndAlso row.ColumnDefinitions.Take(3).Select(Function(column) column.ActualWidth).SequenceEqual({6.0, 34.0, 7.0}) AndAlso
                    row.Icon.ActualWidth = 34 AndAlso row.Icon.ActualHeight = 34 AndAlso clip.RadiusX = 6 AndAlso clip.RadiusY = 6 AndAlso
                    row.TitleLabel.TranslatePoint(New Point(), row).X = 47 AndAlso row.InfoLabel.TranslatePoint(New Point(), row).X = 47,
                    "local Mod rows retain PCL's forty-four-DIP height six-thirty-four-seven columns square clipped icon and aligned text origin")
                check(row.TitleLabel.FontSize = 14 AndAlso row.VersionLabel.FontSize = 12 AndAlso row.VersionLabel.Opacity = 0.4 AndAlso
                    row.VersionLabel.Text.Contains("1.2.3") AndAlso row.InfoLabel.FontSize = 12 AndAlso row.InfoLabel.Text.StartsWith("first:", StringComparison.Ordinal) AndAlso
                    row.InfoLabel.Text.Contains("本地 Mod 夹具") AndAlso row.InfoLabel.Opacity = 1 AndAlso row.Opacity = 1,
                    "local names versions and filename descriptions keep the original distinct fourteen and twelve point hierarchy")
                Dim disabledEntry = owner._localMods.Single(Function(item) item.FileName = "second.jar.disabled")
                Dim disabled = DirectCast(owner._localModRowViews(disabledEntry), PclLocalModItem)
                check(disabled.TitleLabel.TextDecorations IsNot Nothing AndAlso disabled.TitleLabel.TextDecorations.Count > 0 AndAlso disabled.Opacity = 1 AndAlso
                    DirectCast(disabled.TitleLabel.Foreground, SolidColorBrush).Color = PclAnimation.Brush("ColorBrushGray4").Color AndAlso disabled.DisabledBadge.IsVisible AndAlso
                    disabled.DisabledBadge.Width = 20 AndAlso disabled.DisabledBadge.Height = 20 AndAlso disabled.DisabledBadge.Margin = New Thickness(0, 0, -5, -3) AndAlso disabled.InfoLabel.Opacity = 1,
                    "disabled local files use a gray struck title and original twenty-DIP corner badge without dimming every information layer")
                check(row.Focusable AndAlso row.Cursor IsNot Cursors.Hand AndAlso row.Children.OfType(Of PclIconButton)().Single() Is row.ToggleButton AndAlso
                    row.Tag Is entry AndAlso row.ToggleButton.Tag Is entry AndAlso row.ToggleButton.Width = 25 AndAlso row.ToggleButton.Height = 25 AndAlso
                    row.ToggleButton.IsHitTestVisible AndAlso row.ToggleButton.Focusable AndAlso row.ToggleButton.Opacity = 0,
                    "the selectable local row is keyboard reachable and keeps one independently tagged twenty-five-DIP enable action")
                row.MotionPolicy.EnabledForChecks = False
                owner.FocusLocalModButtonForChecks(entry) : layout()
                check(Keyboard.FocusedElement Is row.ToggleButton AndAlso row.ToggleButton.Opacity = 1 AndAlso row.ActionInset = 30 AndAlso
                    LocalModRowBackForChecks(row).ActualWidth = row.ActualWidth AndAlso row.IsAncestorOf(row.ToggleButton),
                    "keyboard focus reveals the local file action inside a backing plate that covers the complete row including its button")
                Dim bytes = File.ReadAllBytes(entry.FilePath)
                RouteDownloadKey(row.ToggleButton, Key.Space) : layout()
                Dim replacement = owner._localMods.Single(Function(item) item.EnabledName = entry.EnabledName)
                Dim accepted = DirectCast(owner._localModRowViews(replacement), PclLocalModItem)
                check(Not replacement.Enabled AndAlso File.Exists(entry.FilePath & ".disabled") AndAlso Not File.Exists(entry.FilePath) AndAlso
                    File.ReadAllBytes(replacement.FilePath).SequenceEqual(bytes) AndAlso accepted.DisabledBadge.IsVisible AndAlso accepted.TitleLabel.TextDecorations.Count > 0 AndAlso
                    Keyboard.FocusedElement Is accepted.ToggleButton,
                    "keyboard activation still uses the production rename rescan and current-row focus handoff without changing archive bytes")
                Dim cached = owner._localModRowViews.ToDictionary(Function(pair) pair.Key, Function(pair) pair.Value), generation = owner._localModGeneration
                owner.FilterLocalMods(owner.LocalModDisabled, Nothing) : layout() : owner.FilterLocalMods(owner.LocalModAll, Nothing) : layout()
                check(owner._localModGeneration = generation AndAlso owner.LocalModRows.Children.Cast(Of Grid)().All(Function(view) cached(DirectCast(view.Tag, LocalModEntry)) Is view),
                    "filtering retains existing specialized rows without changing catalogue ownership or rescanning local files")
                Dim unknown = owner._localMods.Single(Function(item) item.FileName = "unknown.jar"), broken = owner._localMods.Single(Function(item) item.FileName = "broken.jar")
                Dim unknownRow = DirectCast(owner._localModRowViews(unknown), PclLocalModItem), brokenRow = DirectCast(owner._localModRowViews(broken), PclLocalModItem)
                check(unknownRow.VersionLabel.Visibility = Visibility.Collapsed AndAlso brokenRow.InfoLabel.Text = broken.Problem AndAlso
                    unknownRow.ToggleButton.IsEnabled AndAlso brokenRow.ToggleButton.IsEnabled AndAlso unknownRow.ToolTip.ToString().Contains("声明加载器") AndAlso brokenRow.ToolTip.ToString().Contains("物理状态"),
                    "unknown and broken archives retain their actual diagnostics and disable action without invented versions or compatibility claims")
                Dim longData = LocalModCatalog.Read(replacement.FilePath, CancellationToken.None)
                longData.DisplayName = "短标题" : longData.Version = New String("9"c, 180) : longData.Description = "第一行" & vbCrLf & "第二行"
                owner.LocalModRows.Children.Clear()
                Dim wideVersion As New PclLocalModItem(longData) With {.Width = 400}
                owner.LocalModRows.Children.Add(wideVersion) : layout()
                check(wideVersion.TitleCompression = 1 AndAlso wideVersion.VersionLabel.ActualWidth > 0 AndAlso wideVersion.TitleLabel.ActualWidth > 20 AndAlso
                    DirectCast(wideVersion.TitleLabel.Parent, Grid).ActualWidth <= wideVersion.ActualWidth - 47 - wideVersion.ActionInset + 0.5 AndAlso
                    Not wideVersion.InfoLabel.Text.Contains(vbCr) AndAlso Not wideVersion.InfoLabel.Text.Contains(vbLf),
                    "a long declared version compresses its secondary label before the readable local title and flattens multiline descriptions")
                longData.DisplayName = New String("W"c, 200)
                owner.LocalModRows.Children.Clear()
                Dim wideTitle As New PclLocalModItem(longData) With {.Width = 400}
                owner.LocalModRows.Children.Add(wideTitle) : layout()
                check(wideTitle.TitleCompression = 2 AndAlso DirectCast(wideTitle.VersionLabel.Parent, Grid).ColumnDefinitions(1).ActualWidth = 0 AndAlso
                    PclExitSnapshot.VisibleBounds(wideTitle.VersionLabel, wideTitle).IsEmpty AndAlso wideTitle.TitleLabel.ActualWidth > 0 AndAlso
                    wideTitle.TitleLabel.ActualWidth <= wideTitle.ActualWidth - 47 - wideTitle.ActionInset + 0.5 AndAlso
                    wideTitle.TitleLabel.TextTrimming = TextTrimming.CharacterEllipsis,
                    "a title wider than the row takes the remaining width only after its secondary version has been compressed away")
                owner.RenderLocalMods() : layout()
                Dim configBytes = IO.Directory.GetFiles(Path.GetDirectoryName(owner._localModInstance.Target.Root), "*", SearchOption.AllDirectories).
                    Where(Function(full) Not full.Contains(Path.DirectorySeparatorChar & "mods" & Path.DirectorySeparatorChar)).
                    ToDictionary(Function(full) full, Function(full) File.ReadAllBytes(full))
                For Each dark In {False, True}
                    owner.RestoreAppearance(New Newtonsoft.Json.Linq.JObject From {{"theme", "blue"}, {"darkMode", dark}})
                    host.Width = 810 : host.Height = 470 : layout()
                    accepted = DirectCast(owner._localModRowViews(replacement), PclLocalModItem)
                    check(accepted.ActualWidth > 600 AndAlso accepted.ActualHeight = 44 AndAlso accepted.DisabledBadge.IsVisible AndAlso
                        DirectCast(accepted.TitleLabel.Foreground, SolidColorBrush).Color = PclAnimation.Brush("ColorBrushGray4").Color AndAlso accepted.InfoLabel.Opacity = 1,
                        "specialized local rows preserve disabled-state contrast and geometry at the minimum window in dark mode " & dark)
                    SaveControlVisual(host, Path.Combine(directory, "local-mod-row-" & If(dark, "dark", "light") & ".png"))
                Next
                check(configBytes.All(Function(pair) File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),
                    "local row formatting focus filtering and theme changes preserve every external launcher and instance configuration byte")
            End Using
        Finally
            owner._closing = True : owner.CancelLocalModRequest() : owner.CancelManagementRequest()
            owner._managementMotion.Finish() : owner._surfaceMotion.Finish()
            host.Child = Nothing : owner.Content = original
            LocalModCheckSetNativeFocus(nativeFocus)
        End Try
    End Sub

    Private Sub VerifyLocalModRowMotion(directory As String, check As Action(Of Boolean, String))
        Dim nativeFocus = LocalModCheckGetNativeFocus(), owner As New MainWindow(True), original = owner.Content
        Dim host As New Border With {.Width = 850, .Height = 700}, layout = ControlLayout(host)
        FocusManager.SetIsFocusScope(host, True)
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using source = PreviewSurface.Attach(host, 850, 700)
                layout() : owner.PrepareLocalModStylePage(directory, layout)
                Dim entry = owner._localMods.Single(Function(item) item.FileName = "first.jar"), row = DirectCast(owner._localModRowViews(entry), PclLocalModItem)
                row.MotionPolicy.EnabledForChecks = False
                owner.FocusLocalModButtonForChecks(entry) : layout()
                For Each keyEvent As RoutedEvent In {Keyboard.PreviewKeyDownEvent, Keyboard.KeyDownEvent}
                    row.ToggleButton.RaiseEvent(New KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Space) With {.RoutedEvent = keyEvent})
                Next
                Dim pressedScale = DirectCast(LocalModRowBackForChecks(row).RenderTransform, ScaleTransform).ScaleX
                owner.FocusLocalModElementForChecks(owner.LocalModSearch) : owner.FocusLocalModButtonForChecks(entry) : layout()
                check(Math.Abs(pressedScale - 0.996) < 0.0001 AndAlso DirectCast(LocalModRowBackForChecks(row).RenderTransform, ScaleTransform).ScaleX = 1 AndAlso
                    Keyboard.FocusedElement Is row.ToggleButton AndAlso owner._localMods.Contains(entry) AndAlso File.Exists(entry.FilePath) AndAlso Not owner._localModWriting,
                    "losing keyboard focus during a held Space clears the row press so refocusing restores normal hover without writing the file")
                owner.FocusLocalModElementForChecks(owner.LocalModSearch) : layout()
                row.MotionPolicy.EnabledForChecks = True : row.MotionPolicy.PauseForChecks = True
                owner.FocusLocalModButtonForChecks(entry) : layout()
                Dim enterOpacity = row.ActionOpacityTransition, enterInset = row.ActionInsetTransition, back = row.HoverTransition
                check(enterOpacity IsNot Nothing AndAlso enterOpacity.Timeline.Duration.TimeSpan.TotalMilliseconds = 84 AndAlso enterOpacity.Timeline.BeginTime.Value.TotalMilliseconds = 36 AndAlso
                    enterInset.Timeline.Duration.TimeSpan.TotalMilliseconds = 36 AndAlso enterInset.Timeline.BeginTime.Value.TotalMilliseconds = 84 AndAlso
                    DirectCast(back.Timeline, ParallelTimeline).Children(0).Duration.TimeSpan.TotalMilliseconds = 120,
                    "local row focus uses PCL's full backing-plate entry and the original delayed action and right-inset schedules")
                SeekLocalModActionForChecks(enterOpacity, 78, layout) : SeekLocalModActionForChecks(enterInset, 78, layout)
                PauseSelectionClock(back) : back.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(78), TimeSeekOrigin.BeginTime) : layout()
                check(Math.Abs(row.ToggleButton.Opacity - 0.5) < 0.05 AndAlso Math.Abs(row.ActionInset - 4) < 0.1 AndAlso
                    LocalModRowBackForChecks(row).Opacity > 0.5 AndAlso LocalModRowBackForChecks(row).ActualWidth = row.ActualWidth,
                    "the complete row remains highlighted while its action fades halfway in before text starts reserving the action space")
                SeekLocalModActionForChecks(enterInset, 106, layout)
                check(row.ActionInset > 4 AndAlso row.ActionInset < 30 AndAlso row.TitleLabel.TranslatePoint(New Point(), row).X = 47,
                    "the delayed local action inset expands independently while the PCL text and icon origin stay fixed")
                owner.FocusLocalModElementForChecks(owner.LocalModSearch) : layout()
                Dim leaving = row.ActionOpacityTransition
                owner.FocusLocalModButtonForChecks(entry) : layout()
                Dim current = row.ActionOpacityTransition
                enterOpacity.Controller.SkipToFill() : leaving.Controller.SkipToFill() : layout()
                check(row.ActionOpacityTransition Is current AndAlso current IsNot enterOpacity AndAlso row.IsKeyboardFocusWithin,
                    "rapid action focus reversal replaces only its owned fade and old completions cannot retire the current local row action")
                Dim calls = 0
                AddHandler row.ToggleButton.Click, Sub() calls += 1
                row.IsEnabled = False : layout()
                RouteDownloadKey(row.ToggleButton, Key.Space)
                check(row.HoverTransition Is Nothing AndAlso row.ActionOpacityTransition Is Nothing AndAlso row.ActionInsetTransition Is Nothing AndAlso
                    row.ToggleButton.Opacity = 0 AndAlso row.ActionInset = 4 AndAlso Not row.ToggleButton.IsEnabled AndAlso calls = 0,
                    "disabling a local row settles every owned hover action and padding clock before rejecting keyboard activation")
                row.IsEnabled = True : owner.FocusLocalModElementForChecks(owner.LocalModSearch) : layout()
                Dim touch As New TouchEventArgs(New MotionTouchDevice(source), Environment.TickCount) With {.RoutedEvent = UIElement.PreviewTouchDownEvent}
                row.RaiseEvent(touch) : layout()
                check(touch.Handled AndAlso row.ToggleButton.Opacity = 1 AndAlso row.ActionInset = 30 AndAlso row.ToggleButton.IsHitTestVisible AndAlso row.ToggleButton.Focusable AndAlso calls = 0,
                    "first touch reveals the local action without promoting a previously invisible button into a file mutation")
                owner.ManagementViewHost.IsHitTestVisible = False : layout()
                RouteDownloadKey(row.ToggleButton, Key.Space)
                check(Not row.ToggleButton.IsHitTestVisible AndAlso row.ToggleButton.Opacity = 0 AndAlso row.ActionInset = 4 AndAlso
                    row.HoverTransition Is Nothing AndAlso row.ActionOpacityTransition Is Nothing AndAlso calls = 0 AndAlso Not owner._localModWriting,
                    "page handoff gates also retire touch-revealed actions and prevent stale local row input from starting a write")
                owner.ManagementViewHost.IsHitTestVisible = True : owner.FocusLocalModButtonForChecks(entry) : layout()
                Dim oldFade = row.ActionOpacityTransition, oldInset = row.ActionInsetTransition, oldBack = row.HoverTransition
                owner.ShowSettings(Nothing, Nothing) : owner._surfaceMotion.Finish() : layout()
                owner.FocusLocalModElementForChecks(owner.SpeedLimitBox)
                oldFade?.Controller.SkipToFill() : oldInset?.Controller.SkipToFill() : oldBack?.Controller.SkipToFill() : layout()
                check(owner.SettingsPage.IsVisible AndAlso Keyboard.FocusedElement Is owner.SpeedLimitBox AndAlso row.HoverTransition Is Nothing AndAlso
                    row.ActionOpacityTransition Is Nothing AndAlso row.ActionInsetTransition Is Nothing AndAlso row.ToggleButton.Opacity = 0 AndAlso row.ActionInset = 4,
                    "leaving the local Mods page releases all row clocks and obsolete completions cannot reveal its action or steal another page's focus")
            End Using
        Finally
            owner._closing = True : owner.CancelLocalModRequest() : owner.CancelManagementRequest()
            owner._managementMotion.Finish() : owner._surfaceMotion.Finish()
            host.Child = Nothing : owner.Content = original
            LocalModCheckSetNativeFocus(nativeFocus)
        End Try
    End Sub
End Class
