Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Public Sub VerifyLocalModMotionFollowupUi(directory As String, passed As List(Of String))
        Dim failures As New List(Of String), observed = 0
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observed += 1
                                                     If ok Then passed.Add(label) Else failures.Add(label)
                                                 End Sub
        For Each dark In {False, True}
            VerifyLocalModMotionFollowupCase(directory, dark, check)
        Next
        If failures.Count > 0 Then Throw New InvalidOperationException("Mod motion followup failures: " & failures.Count & "/" & observed & vbCrLf & String.Join(vbCrLf, failures))
    End Sub
    Private Sub VerifyLocalModMotionFollowupCase(directory As String, dark As Boolean, check As Action(Of Boolean, String))
        Dim saved = _appearance.ToJson(), previous = SynchronizationContext.Current, nativeFocus = LocalModCheckGetNativeFocus()
        Dim owner As New MainWindow(True), original = owner.Content
        Dim host As New Border With {.Width = 900, .Height = 700}, layout = ControlLayout(host)
        Dim label As Func(Of String, String) = Function(value) value & " in dark mode " & dark
        Dim caseRoot = Path.Combine(directory, "motion-followup-" & dark)
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 700)
                owner.PrepareLocalModStylePage(caseRoot, layout)
                For index = 0 To 63
                    LocalModJarForChecks(Path.Combine(owner._localModInstance.RunDirectory, "mods/motion-" & index & ".jar"), "motion_" & index)
                Next
                owner.LoadLocalModsAsync(False).GetAwaiter().GetResult() : layout()
                owner.RestoreAppearance(New JObject From {{"darkMode", dark}}) : layout()
                Dim protectedFiles = IO.Directory.GetFiles(caseRoot, "*", SearchOption.AllDirectories).ToDictionary(Function(full) full, Function(full) File.ReadAllBytes(full))
                Dim rows = owner.LocalModRows.Children.OfType(Of PclLocalModItem)().ToArray()
                For Each row In rows : row.MotionPolicy.EnabledForChecks = True : row.MotionPolicy.PauseForChecks = True : Next
                owner._localModBarPolicy.EnabledForChecks = True : owner._localModBarPolicy.PauseForChecks = True
                Dim onScreen = rows.Where(Function(row) Not PclExitSnapshot.VisibleBounds(row, host).IsEmpty).ToArray()
                Dim outside = rows.Except(onScreen).ToArray()
                check(rows.Length = 68 AndAlso onScreen.Length > 0 AndAlso outside.Length > 50, label("a real sixty-eight-file catalogue separates visible rows from clipped attached rows"))
                owner.SelectAllLocalMods(Nothing, Nothing) : layout()
                check(onScreen.All(Function(row) row.Checked AndAlso LocalModSelectionClock(row) IsNot Nothing AndAlso row.HoverTransition IsNot Nothing),
                    label("visible bulk-selected Mods retain their marker and backing-plate entry"))
                check(outside.All(Function(row) row.Checked AndAlso LocalModSelectionClock(row) Is Nothing),
                    label("bulk selection settles offscreen markers without allocating selection clocks"))
                check(outside.All(Function(row) row.HoverTransition Is Nothing AndAlso row.ActionOpacityTransition Is Nothing AndAlso row.ActionInsetTransition Is Nothing),
                    label("bulk selection allocates no invisible backing-plate or action clocks"))
                check(outside.All(Function(row) LocalModRowBackForChecks(row).Opacity = 1 AndAlso DirectCast(LocalModRowBackForChecks(row).RenderTransform, ScaleTransform).ScaleX = 1),
                    label("offscreen selected backing plates already have their stable selected appearance"))
                owner.ClearLocalModSelection(Nothing, Nothing) : layout()
                check(outside.All(Function(row) Not row.Checked AndAlso LocalModSelectionClock(row) Is Nothing AndAlso row.HoverTransition Is Nothing),
                    label("clearing bulk selection retires offscreen plate clocks instead of starting an invisible exit"))
                check(outside.All(Function(row) LocalModRowBackForChecks(row).Opacity = 0 AndAlso DirectCast(LocalModRowBackForChecks(row).RenderTransform, ScaleTransform).ScaleX = 0.8),
                    label("offscreen cleared rows keep the final hidden backing-plate geometry"))
                owner.LocalModsPage.ScrollToVerticalOffset(owner.LocalModsPage.ScrollableHeight) : layout()
                Dim newlyVisible = outside.Last(Function(row) Not PclExitSnapshot.VisibleBounds(row, host).IsEmpty)
                newlyVisible.Checked = True : layout()
                check(LocalModSelectionClock(newlyVisible) IsNot Nothing AndAlso newlyVisible.HoverTransition IsNot Nothing,
                    label("a clipped row plays fresh marker and plate motion after scrolling into view"))
                owner._localModBarOpacity.Clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(60), TimeSeekOrigin.BeginTime)
                owner._localModBarOffset.Clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(360), TimeSeekOrigin.BeginTime) : layout()
                owner.ClearLocalModSelection(Nothing, Nothing)
                Dim exitFade = owner._localModBarOpacity.Clock, exitOffset = owner._localModBarOffset.Clock
                exitFade.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(45), TimeSeekOrigin.BeginTime)
                exitOffset.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(45), TimeSeekOrigin.BeginTime) : layout()
                Dim alpha = owner.LocalModSelectionBar.Opacity, y = owner.LocalModSelectionOffset.Y
                owner.RefreshLocalModSelection(True) : layout()
                check(owner._localModBarOpacity.Clock Is exitFade AndAlso owner._localModBarOffset.Clock Is exitOffset AndAlso owner.LocalModSelectionBar.IsVisible AndAlso
                    Math.Abs(owner.LocalModSelectionBar.Opacity - alpha) < 0.001 AndAlso Math.Abs(owner.LocalModSelectionOffset.Y - y) < 0.001,
                    label("repeated empty refresh preserves the same partly completed ninety-ms toolbar exit"))
                owner.ClearLocalModSelection(Nothing, Nothing) : layout()
                check(owner._localModBarOpacity.Clock Is exitFade AndAlso owner.LocalModSelectionBar.IsVisible AndAlso owner.LocalModSelectionCount.Text = "已选择 1 个文件" AndAlso
                    Not owner.LocalModSelectionBar.IsHitTestVisible,
                    label("repeated clear keeps the exit count and input gate without snapping the floating toolbar"))
                newlyVisible.Checked = True : layout()
                exitFade.Controller.SkipToFill() : exitOffset.Controller.SkipToFill() : layout()
                check(owner.LocalModSelectionBar.IsVisible AndAlso owner._localModSelection.Count = 1 AndAlso owner.LocalModSelectionCount.Text = "已选择 1 个文件" AndAlso
                    owner._localModBarOpacity.Clock IsNot Nothing AndAlso owner._localModBarOpacity.Clock IsNot exitFade,
                    label("a new selection supersedes a repeatedly refreshed exit without being hidden by its stale completion"))
                owner.ClearLocalModSelection(Nothing, Nothing)
                Dim naturalExit = owner._localModBarOpacity.Clock, naturalOffset = owner._localModBarOffset.Clock
                naturalExit.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(45), TimeSeekOrigin.BeginTime)
                owner.RefreshLocalModSelection(True)
                naturalExit.Controller.Resume() : naturalOffset.Controller.Resume()
                naturalExit.Controller.SkipToFill() : naturalOffset.Controller.SkipToFill()
                check(PumpMotionUntil(Function() Not owner.LocalModSelectionBar.IsVisible AndAlso owner._localModBarOpacity.Clock Is Nothing AndAlso
                    owner._localModBarOffset.Clock Is Nothing) AndAlso owner._localModSelection.Count = 0,
                    label("a repeatedly refreshed exit still completes naturally and removes its original clocks"))
                newlyVisible.Checked = True : layout()
                owner.ShowSettings(Nothing, Nothing) : layout()
                check(owner._localModBarOpacity.Clock Is Nothing AndAlso owner._localModBarOffset.Clock Is Nothing AndAlso owner._localModSelection.Count = 0 AndAlso
                    rows.All(Function(row) row.HoverTransition Is Nothing AndAlso LocalModSelectionClock(row) Is Nothing AndAlso row.ActionOpacityTransition Is Nothing AndAlso row.ActionInsetTransition Is Nothing),
                    label("leaving the catalogue releases both visible and offscreen row clocks and the floating toolbar"))
                check(IO.Directory.GetFiles(caseRoot, "*", SearchOption.AllDirectories).Length = protectedFiles.Count AndAlso
                    protectedFiles.All(Function(pair) File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),
                    label("bulk selection scrolling repeated clearing and leaving preserve every fixture file and launcher setting"))
            End Using
        Finally
            owner._closing = True : owner.CancelLocalModRequest() : owner.CancelManagementRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._managementMotion.Finish() : owner._categoryMotion.Finish()
            host.Child = Nothing : owner.Content = original
            RestoreAppearance(saved) : SynchronizationContext.SetSynchronizationContext(previous) : LocalModCheckSetNativeFocus(nativeFocus)
        End Try
    End Sub
End Class
