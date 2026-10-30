Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media.Animation
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Public Sub VerifyLateMotionPolicyUi(directory As String, passed As List(Of String))
        Dim observations As New JObject, saved = _appearance.ToJson()
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        Try
            VerifyLoadedMotionPolicy(check)
            For Each dark In {False, True}
                VerifyLoadedModSelectionBar(directory, dark, check)
            Next
            File.WriteAllText(Path.Combine(directory, "late-motion-policy.json"), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            RestoreAppearance(saved)
        End Try
    End Sub

    Private Sub VerifyLoadedMotionPolicy(check As Action(Of Boolean, String))
        Dim target As New Border With {.Width = 160, .Height = 40, .Opacity = 0}
        Dim host As New Border With {.Width = 240, .Height = 100, .Child = target}, layout = ControlLayout(host)
        Dim slot As New PclMotionSlot(target, OpacityProperty), policy As PclControlMotionPolicy = Nothing, notifications = 0, premature = False
        Dim settle As Action = Sub()
                                   notifications += 1
                                   If policy Is Nothing Then premature = True
                                   slot.SetValue(0.0)
                               End Sub
        Using surface = PreviewSurface.Attach(host, 240, 100)
            layout()
            check(target.IsLoaded AndAlso target.IsVisible, "late motion policy fixture finishes Loaded before constructing its policy")
            policy = New PclControlMotionPolicy(target, settle)
            check(Not premature AndAlso notifications = 0, "a late motion policy never calls its owner before the caller assigns its field")
            policy.EnabledForChecks = True : policy.PauseForChecks = True
            check(policy.CanAnimate, "a policy constructed after Loaded can animate without reattaching its owner")
            If policy.CanAnimate Then slot.Animate(New DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(100)), 1.0, True)
            If slot.Clock IsNot Nothing Then SeekControl(slot.Clock, 50)
            check(slot.Clock IsNot Nothing AndAlso Math.Abs(target.Opacity - 0.5) < 0.01, "a late policy drives an actual intermediate property frame")
            target.IsHitTestVisible = False
            check(Not policy.CanAnimate AndAlso slot.Clock Is Nothing AndAlso target.Opacity = 0, "input gating retires a late policy's active motion")
            target.IsHitTestVisible = True : target.IsEnabled = False
            check(Not policy.CanAnimate AndAlso slot.Clock Is Nothing, "disabling a loaded owner keeps its late policy noninteractive")
            target.IsEnabled = True : policy.EnabledForChecks = False
            check(Not policy.CanAnimate AndAlso slot.Clock Is Nothing, "reduced motion settles the late policy without a clock")
            policy.EnabledForChecks = True
            host.Child = Nothing : layout()
            check(Not target.IsLoaded AndAlso Not policy.CanAnimate AndAlso slot.Clock Is Nothing, "detaching a late policy owner clears motion and animation eligibility")
            host.Child = target : layout()
            check(target.IsLoaded AndAlso policy.CanAnimate AndAlso Not premature, "reattaching a late policy owner restores motion without a constructor callback")
            If policy.CanAnimate Then slot.Animate(New DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(100)), 1.0, True)
            host.Child = Nothing : layout()
            check(Not policy.CanAnimate AndAlso slot.Clock Is Nothing AndAlso target.Opacity = 0, "a reloaded late policy still retires its next clock on unload")
        End Using
    End Sub

    Private Sub VerifyLoadedModSelectionBar(directory As String, dark As Boolean, check As Action(Of Boolean, String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim focus = LocalModCheckGetNativeFocus(), host As New Border With {.Width = 900, .Height = 700}, layout = ControlLayout(host)
        Dim label As Func(Of String, String) = Function(value) value & "/dark=" & dark
        Dim caseRoot = Path.Combine(directory, "loaded-mod-bar-" & dark)
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 700)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                ' Reproduce the desktop order: the initial page has already loaded
                ' before navigation first creates the Mod selection-bar policy.
                layout()
                check(owner.LocalModsPage.IsLoaded AndAlso owner._localModBarPolicy Is Nothing,
                    label("first Mod visit begins with a loaded page and no selection-bar policy"))
                owner.PrepareLocalModStylePage(caseRoot, layout)
                owner.RestoreAppearance(New JObject From {{"darkMode", dark}}) : layout()
                Dim protectedFiles = DirectoryFilesForModUpdateChecks(caseRoot)
                owner._localModBarPolicy.EnabledForChecks = True : owner._localModBarPolicy.PauseForChecks = True
                Dim row = owner.LocalModRows.Children.OfType(Of PclLocalModItem)().First(Function(item) DirectCast(item.Tag, Core.LocalModEntry).FileName = "first.jar")
                Dim second = owner.LocalModRows.Children.OfType(Of PclLocalModItem)().First(Function(item) DirectCast(item.Tag, Core.LocalModEntry).FileName = "second.jar.disabled")
                check(owner.LocalModsPage.IsLoaded AndAlso owner._localModBarPolicy.CanAnimate,
                    label("the first loaded Mod visit enables its late-created selection bar"))
                row.Checked = True
                Dim entered = owner._localModBarOpacity.Clock, offset = owner._localModBarOffset.Clock
                check(entered IsNot Nothing AndAlso offset IsNot Nothing AndAlso owner.LocalModSelectionBar.Opacity = 0 AndAlso owner.LocalModSelectionOffset.Y = -10,
                    label("the first real Mod selection starts the floating bar at its hidden PCL origin"))
                If entered IsNot Nothing Then SeekControl(entered, 30)
                If offset IsNot Nothing Then SeekControl(offset, 60)
                check(Math.Abs(owner.LocalModSelectionBar.Opacity - 0.5) < 0.01 AndAlso Math.Abs(owner.LocalModSelectionOffset.Y + 22.75) < 0.01,
                    label("the floating bar exposes PCL's sixty-ms fade and one-hundred-twenty-ms jump midpoint"))
                SaveControlVisual(host, Path.Combine(directory, "loaded-mod-bar-enter-" & dark & ".png"))
                entered?.Controller.SkipToFill() : offset?.Controller.SkipToFill() : layout()
                check(owner._localModBarOpacity.Clock Is Nothing AndAlso owner._localModBarOffset.Clock Is Nothing AndAlso owner.LocalModSelectionBar.Opacity = 1 AndAlso owner.LocalModSelectionOffset.Y = -25,
                    label("the floating bar's first entry releases its clocks at the PCL resting position"))
                second.Checked = True
                Dim increased = owner._localModBarOffset.Clock
                check(increased IsNot Nothing AndAlso increased IsNot offset AndAlso owner._localModSelection.Count = 2,
                    label("adding another selected Mod starts a fresh PCL bar bounce"))
                owner._localModBarOpacity.Clock?.Controller.SkipToFill() : increased?.Controller.SkipToFill() : layout()
                owner.ClearLocalModSelection(Nothing, Nothing)
                Dim exitOpacity = owner._localModBarOpacity.Clock, exitOffset = owner._localModBarOffset.Clock
                check(exitOpacity IsNot Nothing AndAlso exitOffset IsNot Nothing AndAlso Not owner.LocalModSelectionBar.IsHitTestVisible,
                    label("clearing selection starts an owned exit and immediately gates floating actions"))
                If exitOpacity IsNot Nothing Then SeekControl(exitOpacity, 45)
                If exitOffset IsNot Nothing Then SeekControl(exitOffset, 45)
                check(Math.Abs(owner.LocalModSelectionBar.Opacity - 0.5) < 0.01 AndAlso Math.Abs(owner.LocalModSelectionOffset.Y + 21.25) < 0.01,
                    label("the bar's exit retains the PCL ninety-ms fade and movement midpoint"))
                row.Checked = True
                Dim replacement = owner._localModBarOpacity.Clock
                exitOpacity?.Controller.SkipToFill() : exitOffset?.Controller.SkipToFill() : layout()
                check(replacement IsNot Nothing AndAlso owner._localModBarOpacity.Clock Is replacement AndAlso owner.LocalModSelectionBar.IsVisible AndAlso owner._localModSelection.Count = 1,
                    label("an obsolete exit cannot hide a rapidly reselected Mod bar"))
                owner.PageSurface.IsHitTestVisible = False : layout()
                check(owner._localModBarOpacity.Clock Is Nothing AndAlso owner._localModBarOffset.Clock Is Nothing AndAlso Not owner.LocalModSelectionBar.IsHitTestVisible,
                    label("page input gating settles the active bar and retires its floating actions"))
                owner.PageSurface.IsHitTestVisible = True : owner._localModBarPolicy.EnabledForChecks = False
                second.Checked = True : layout()
                check(owner._localModBarOpacity.Clock Is Nothing AndAlso owner._localModBarOffset.Clock Is Nothing AndAlso owner.LocalModSelectionBar.Opacity = 1 AndAlso owner.LocalModSelectionOffset.Y = -25,
                    label("reduced motion retains the selected bar's final geometry without clocks"))
                owner._localModBarPolicy.EnabledForChecks = True : owner.BackFromLocalMods() : layout()
                check(owner._localModSelection.Count = 0 AndAlso owner._localModBarOpacity.Clock Is Nothing AndAlso owner._localModBarOffset.Clock Is Nothing AndAlso
                    protectedFiles.All(Function(item) File.Exists(item.Key) AndAlso File.ReadAllBytes(item.Key).SequenceEqual(item.Value)),
                    label("returning retires the bar and preserves every Mod and launcher-setting byte"))
            End Using
        Finally
            owner._closing = True : owner.CancelLocalModRequest() : owner.CancelManagementRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._managementMotion.Finish() : owner._categoryMotion.Finish()
            host.Child = Nothing : owner.Content = original
            SynchronizationContext.SetSynchronizationContext(previous) : LocalModCheckSetNativeFocus(focus)
        End Try
    End Sub
End Class
