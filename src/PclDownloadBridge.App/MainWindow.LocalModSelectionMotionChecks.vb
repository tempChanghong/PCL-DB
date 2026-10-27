Imports System.IO
Imports System.Reflection
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Shared Function LocalModSelectionClock(row As PclLocalModItem) As ClockGroup
        ' Keep the video-reproduction fixture runnable against the old control.
        Dim field = row.GetType().GetField("_selectionMotion", BindingFlags.Instance Or BindingFlags.NonPublic)
        Dim motion = field?.GetValue(row)
        If motion Is Nothing Then Return Nothing
        Return DirectCast(motion.GetType().GetProperty("Transition", BindingFlags.Instance Or BindingFlags.NonPublic).GetValue(motion), ClockGroup)
    End Function
    Public Sub VerifyLocalModSelectionMotionUi(directory As String, passed As List(Of String))
        Dim savedAppearance = _appearance.ToJson()
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim nativeFocus = LocalModCheckGetNativeFocus()
        Dim host As New Border With {.Width = 900, .Height = 700}, layout = ControlLayout(host)
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     If Not ok Then Throw New InvalidOperationException(label)
                                                     passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 700)
                owner.PrepareLocalModStylePage(Path.Combine(directory, "selection-motion"), layout)
                Dim rows = owner.LocalModRows.Children.OfType(Of PclLocalModItem)().ToArray()
                Dim row = rows.First(Function(item) DirectCast(item.Tag, Core.LocalModEntry).Enabled)
                Dim marker = row.Children.OfType(Of Border)().Single(Function(item) item.Width = 5)
                row.MotionPolicy.EnabledForChecks = True : row.MotionPolicy.PauseForChecks = True
                Dim idle = DirectCast(row.TitleLabel.Foreground, SolidColorBrush).Color
                row.Checked = True
                Dim entering = LocalModSelectionClock(row)
                check(entering IsNot Nothing, "video regression: a Mod selection owns a live marker and title transition instead of direct visibility")
                PauseSelectionClock(entering)
                check(marker.Visibility = Visibility.Visible AndAlso marker.Margin.Left = -3 AndAlso marker.VerticalAlignment = VerticalAlignment.Center AndAlso marker.Width = 5,
                    "Mod selection marker retains PCL's five-DIP width and minus-three inset")
                check(marker.Height = 0 AndAlso DirectCast(row.TitleLabel.Foreground, SolidColorBrush).Color = idle,
                    "selection starts at the displayed marker height and title ink without a final-state flash")
                entering.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(15), TimeSeekOrigin.BeginTime) : layout()
                check(Math.Abs(marker.Opacity - 0.5) < 0.01, "Mod marker uses the PCL thirty-ms linear opacity entry")
                entering.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(100), TimeSeekOrigin.BeginTime) : layout()
                Dim expectedHeight = New PclSelectedHeightAnimation(0, 32).Sample(100)
                check(Math.Abs(marker.Height - expectedHeight) < 0.001 AndAlso marker.Height > 0 AndAlso marker.Height < 32,
                    "Mod marker samples the parallel 200-ms fluent and 300-ms weak-back height deltas")
                Dim expectedInk = New PclRadioColorAnimation(idle, PclAnimation.Brush("ColorBrush2").Color, 200).Sample(0.5)
                check(DirectCast(row.TitleLabel.Foreground, SolidColorBrush).Color = expectedInk, "Mod title interpolates ARGB ink over the PCL two-hundred-ms selection")
                Dim height = marker.Height, alpha = marker.Opacity
                row.RaiseEvent(New MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) With {.RoutedEvent = Mouse.MouseLeaveEvent})
                check(LocalModSelectionClock(row) Is entering AndAlso marker.Height = height, "hover refresh cannot restart or snap the independent selection clock")
                row.HasUpdate = True
                check(LocalModSelectionClock(row) Is entering AndAlso marker.Height = height, "late update metadata does not interrupt a selected Mod's marker or title transition")
                row.Checked = False
                Dim exiting = LocalModSelectionClock(row)
                PauseSelectionClock(exiting)
                check(exiting IsNot entering AndAlso Math.Abs(marker.Height - height) < 0.001 AndAlso marker.Opacity = alpha,
                    "rapid deselection samples the partly entered marker before retiring the old clock")
                exiting.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(20), TimeSeekOrigin.BeginTime) : layout()
                check(marker.Opacity = alpha, "deselect holds sampled alpha throughout the original forty-ms delay")
                exiting.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(75), TimeSeekOrigin.BeginTime) : layout()
                check(Math.Abs(marker.Opacity - alpha / 2) < 0.01 AndAlso marker.Height < height, "deselect shrinks with ease-in while fading over seventy ms after the delay")
                height = marker.Height : row.Checked = True
                Dim newest = LocalModSelectionClock(row)
                PauseSelectionClock(newest)
                check(Math.Abs(marker.Height - height) < 0.001, "reselecting an exiting marker preserves the composited height")
                exiting.Controller.SkipToFill() : entering.Controller.SkipToFill() : layout()
                check(LocalModSelectionClock(row) Is newest AndAlso row.Checked, "obsolete selection completions cannot overwrite the latest selection")
                newest.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(300), TimeSeekOrigin.BeginTime) : layout()
                check(Math.Abs(marker.Height - 32) < 0.001 AndAlso marker.Opacity = 1, "selected Mod geometry settles at PCL's thirty-two-DIP marker")
                PointerEvent(row, Mouse.PreviewMouseDownEvent)
                Dim press = row.HoverTransition
                PauseSelectionClock(press) : press.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(145), TimeSeekOrigin.BeginTime) : layout()
                check(Math.Abs(DirectCast(LocalModRowBackForChecks(row).RenderTransform, ScaleTransform).ScaleX - 0.996) < 0.0001,
                    "pressing the selectable row itself uses PCL's backing-plate press scale")
                row.RaiseEvent(New MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) With {.RoutedEvent = Mouse.MouseLeaveEvent})
                Dim release = row.HoverTransition
                PauseSelectionClock(release) : release.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(145), TimeSeekOrigin.BeginTime) : layout()
                check(Math.Abs(DirectCast(LocalModRowBackForChecks(row).RenderTransform, ScaleTransform).ScaleX - 1) < 0.0001 AndAlso row.Checked,
                    "leaving a pressed selected row releases its plate without changing file selection")
                row.MotionPolicy.EnabledForChecks = False
                check(LocalModSelectionClock(row) Is Nothing AndAlso marker.Height = 32 AndAlso marker.Opacity = 1,
                    "reduced motion removes selection clocks while retaining selected geometry")
                row.Checked = False
                check(LocalModSelectionClock(row) Is Nothing AndAlso marker.Height = 0 AndAlso marker.Opacity = 0,
                    "reduced-motion deselection immediately clears geometry without hiding a future animation target")
                Dim disabled = rows.First(Function(item) Not DirectCast(item.Tag, Core.LocalModEntry).Enabled)
                disabled.MotionPolicy.EnabledForChecks = False : disabled.Checked = True
                check(DirectCast(disabled.TitleLabel.Foreground, SolidColorBrush).Color = PclAnimation.Brush("ColorBrush5").Color,
                    "disabled selected Mods use the original lighter accent and preserve their struck title")
                disabled.Checked = False
                For Each dark In {False, True}
                    owner.RestoreAppearance(New JObject From {{"darkMode", dark}})
                    row.MotionPolicy.EnabledForChecks = True : row.MotionPolicy.PauseForChecks = True : row.Checked = True
                    Dim paletteClock = LocalModSelectionClock(row)
                    PauseSelectionClock(paletteClock) : paletteClock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(80), TimeSeekOrigin.BeginTime) : layout()
                    height = marker.Height
                    PclTheme.ApplyCycleFrame(owner.TitleBar, owner.Shell, New AppearancePreferences With {.Hue = 200, .DarkMode = dark})
                    check(LocalModSelectionClock(row) Is paletteClock AndAlso marker.Height = height AndAlso row.Checked,
                        "moving theme frames preserve Mod selection geometry in dark mode " & dark)
                    SaveControlVisual(host, Path.Combine(directory, "selection-marker-" & dark & ".png"))
                    row.Visibility = Visibility.Collapsed : layout()
                    check(LocalModSelectionClock(row) Is Nothing AndAlso marker.Height = 32,
                        "hidden selected rows retire clocks without losing identity in dark mode " & dark)
                    row.Visibility = Visibility.Visible : row.MotionPolicy.EnabledForChecks = False : row.Checked = False : layout()
                Next
                owner.ClearLocalModSelection(Nothing, Nothing)
                For Each item In rows : item.MotionPolicy.EnabledForChecks = True : item.MotionPolicy.PauseForChecks = True : Next
                owner._localModBarPolicy.EnabledForChecks = True : owner._localModBarPolicy.PauseForChecks = True
                Dim unloaded = 0
                For Each item In rows
                    AddHandler item.Unloaded, Sub() unloaded += 1
                Next
                owner.SelectAllLocalMods(Nothing, Nothing) : layout()
                check(unloaded = 0 AndAlso rows.SequenceEqual(owner.LocalModRows.Children.OfType(Of PclLocalModItem)()),
                    "select-all retains attached rows instead of unloading and snapping their animations")
                check(rows.All(Function(item) item.Checked AndAlso LocalModSelectionClock(item) IsNot Nothing) AndAlso owner._localModSelection.Count = rows.Length,
                    "select-all starts selection motion for each visible Mod and updates the model once")
                For Each item In rows
                    Dim itemClock = LocalModSelectionClock(item)
                    PauseSelectionClock(itemClock) : itemClock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(300), TimeSeekOrigin.BeginTime)
                Next
                owner._localModBarOpacity.Clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(60), TimeSeekOrigin.BeginTime)
                owner._localModBarOffset.Clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(360), TimeSeekOrigin.BeginTime) : layout()
                SaveControlVisual(host, Path.Combine(directory, "selection-all.png"))
                Dim countText = owner.LocalModSelectionCount.Text
                owner.ClearLocalModSelection(Nothing, Nothing) : layout()
                check(unloaded = 0 AndAlso rows.All(Function(item) Not item.Checked AndAlso LocalModSelectionClock(item) IsNot Nothing),
                    "clear-selection retains all outgoing row animations without rebuilding the list")
                owner._localModBarOpacity.Clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(45), TimeSeekOrigin.BeginTime) : layout()
                SaveControlVisual(host, Path.Combine(directory, "selection-exit.png"))
                check(owner.LocalModSelectionCount.Text = countText AndAlso owner._localModSelection.Count = 0 AndAlso Not owner.LocalModSelectionBar.IsHitTestVisible,
                    "the floating toolbar keeps its last count throughout exit while retiring actions immediately")
                Dim barExit = owner._localModBarOpacity.Clock
                row.Checked = True : layout()
                barExit.Controller.SkipToFill() : layout()
                check(owner.LocalModSelectionBar.IsVisible AndAlso owner.LocalModSelectionCount.Text = "已选择 1 个文件" AndAlso owner._localModSelection.Count = 1,
                    "a rapid select during toolbar exit cannot be hidden by the obsolete fade completion")
                Dim loadedClock = LocalModSelectionClock(row)
                owner.LocalModRows.Children.Remove(row) : layout()
                check(LocalModSelectionClock(row) Is Nothing AndAlso row.Checked, "detaching a selected row releases all owned marker and color clocks")
                loadedClock?.Controller.SkipToFill()
                owner.LocalModRows.Children.Insert(0, row) : layout()
                check(LocalModSelectionClock(row) Is Nothing AndAlso marker.Height = 32 AndAlso row.Checked,
                    "reattaching a selected row restores stable geometry without replaying an obsolete clock")
                owner.PageSurface.IsHitTestVisible = False : row.Checked = False : layout()
                check(row.Checked AndAlso owner._localModSelection.Count = 1 AndAlso LocalModSelectionClock(row) Is Nothing,
                    "outgoing page gates reject a deselection and settle the retained row state")
                owner.PageSurface.IsHitTestVisible = True : layout()
                Dim spacer As New Border With {.Height = 1000}
                Dim offscreen As New PclLocalModItem(DirectCast(row.Tag, Core.LocalModEntry))
                owner.LocalModRows.Children.Add(spacer) : owner.LocalModRows.Children.Add(offscreen) : layout()
                offscreen.MotionPolicy.EnabledForChecks = True : offscreen.MotionPolicy.PauseForChecks = True
                check(offscreen.IsVisible AndAlso PclExitSnapshot.VisibleBounds(offscreen, host).IsEmpty,
                    "large-list fixture keeps an attached but scrolled-out Mod row outside the actual viewport")
                offscreen.Checked = True
                Dim offscreenMarker = offscreen.Children.OfType(Of Border)().Single(Function(item) item.Width = 5)
                check(LocalModSelectionClock(offscreen) Is Nothing AndAlso offscreenMarker.Height = 32,
                    "selecting an offscreen Mod settles directly instead of allocating invisible selection clocks")
                owner.LocalModsPage.ScrollToVerticalOffset(owner.LocalModsPage.ScrollableHeight) : layout()
                check(Not PclExitSnapshot.VisibleBounds(offscreen, host).IsEmpty AndAlso LocalModSelectionClock(offscreen) Is Nothing AndAlso offscreenMarker.Height = 32,
                    "scrolling a selected row into view exposes its stable selection without replaying an old entry")
                offscreen.Checked = False
                check(LocalModSelectionClock(offscreen) IsNot Nothing,
                    "the same Mod row animates again when a new selection change occurs inside the viewport")
                owner.LocalModRows.Children.Remove(offscreen) : owner.LocalModRows.Children.Remove(spacer)
                owner.LocalModsPage.ScrollToTop() : layout()
                row.Checked = False : row.Checked = True : layout()
                owner.BackFromLocalMods() : layout()
                check(LocalModSelectionClock(row) Is Nothing AndAlso owner._localModBarOpacity.Clock Is Nothing AndAlso owner._localModSelection.Count = 0,
                    "return navigation retires row and floating-bar clocks together with local selection")
                SaveControlVisual(host, Path.Combine(directory, "selection-motion-return.png"))
            End Using
        Finally
            owner._closing = True : owner.CancelLocalModRequest() : owner.CancelManagementRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._managementMotion.Finish() : owner._categoryMotion.Finish()
            host.Child = Nothing : owner.Content = original
            RestoreAppearance(savedAppearance)
            SynchronizationContext.SetSynchronizationContext(previous) : LocalModCheckSetNativeFocus(nativeFocus)
        End Try
    End Sub
End Class
