Imports System.Threading.Tasks
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Sub VerifyInstallActionMotion(directory As String, check As Action(Of Boolean, String))
        Dim count = 0
        Dim verify As Action(Of Boolean, String) = Sub(ok, label)
                                                      check(ok, label)
                                                      count += 1
                                                  End Sub
        VerifyInstallActionCurves(verify)
        VerifyInstallActionLifetime(verify)
        VerifyInstallActionIntegration(directory, verify)
        IO.File.WriteAllText(IO.Path.Combine(directory, "install-action-check-count.json"),
            New JObject From {{"checks", count}, {"source", "PCL 5ef81e4 MyExtraTextButton.Show"}}.ToString())
    End Sub

    Private Shared Sub VerifyInstallActionCurves(check As Action(Of Boolean, String))
        Dim samples = {-30.0, 0, 25, 50, 90, 100, 130, 170, 210, 250, 350, 450, 700}
        For Each origin In {0.0, 0.47, 1.0375}
            For Each shown In {False, True}
                For Each alphaChannel As Boolean In {False, True}
                    ' Opacity origins must remain within the valid UIElement range.
                    Dim start = If(alphaChannel, Math.Min(1, origin), origin)
                    Dim animation As New PclInstallActionAnimation(start, shown, alphaChannel)
                    Dim label = start.ToString(Globalization.CultureInfo.InvariantCulture) & "/" & shown & "/" & alphaChannel
                    check(samples.All(Function(time) Math.Abs(animation.Sample(time) - ReferenceInstallAction(start, shown, alphaChannel, time)) < 0.0000001),
                        "install action follows independent PCL source samples, including delayed and interrupted values: " & label)
                    check(animation.Duration.TimeSpan.TotalMilliseconds = If(shown, If(alphaChannel, 130, 450), 100),
                        "install action retains the source scale and opacity track duration: " & label)
                    Dim clone = DirectCast(animation.Clone(), PclInstallActionAnimation)
                    clone.Freeze()
                    check(clone.IsFrozen AndAlso samples.All(Function(time) Math.Abs(clone.Sample(time) - ReferenceInstallAction(start, shown, alphaChannel, time)) < 0.0000001),
                        "install action origin direction and channel survive WPF cloning and freezing: " & label)
                Next
            Next
        Next
        check(Math.Abs(New PclInstallActionAnimation(0, True).Sample(250) - 1.0375) < 0.0000001 AndAlso
            New PclInstallActionAnimation(0, True).Sample(450) = 1 AndAlso
            New PclInstallActionAnimation(0, True, True).Sample(90) = 0.5,
            "install action retains PCL's 1.0375 rebound sample and separate half-opacity sample")
    End Sub

    Private Shared Function ReferenceInstallAction(origin As Double, shown As Boolean, opacity As Boolean, milliseconds As Double) As Double
        Dim clamp As Func(Of Double, Double) = Function(value) Math.Max(0, Math.Min(1, value))
        If opacity Then
            If shown Then Return origin + (1 - origin) * clamp((milliseconds - 50) / 80)
            Return origin * (1 - clamp((milliseconds - 50) / 50))
        End If
        If Not shown Then Return origin * (1 - Math.Pow(clamp(milliseconds / 100), 2))
        Dim back = clamp((milliseconds - 50) / 400)
        Dim fluent = clamp((milliseconds - 50) / 160)
        Return Math.Max(0, origin + (0.15 - origin) * (1 - Math.Pow(1 - back, 1.5) * Math.Cos(1.5 * Math.PI * back)) +
            0.85 * (1 - Math.Pow(1 - fluent, 3)))
    End Function

    Private Shared Sub VerifyInstallActionLifetime(check As Action(Of Boolean, String))
        Dim pane As New Grid(), action As New Grid With {.Width = 180, .Height = 52}
        Dim inner As New Button With {.Content = "开始下载", .RenderTransform = New ScaleTransform(0.9, 0.9)}
        action.Children.Add(inner) : pane.Children.Add(action)
        Dim host As New Border With {.Width = 500, .Height = 300, .Child = pane}
        Dim enabled = True
        Dim motion As New PclInstallActionMotion(action, Function() enabled) With {.PauseForChecks = True}
        Dim layout As Action = Sub()
                                   host.Measure(New Size(host.Width, host.Height)) : host.Arrange(New Rect(0, 0, host.Width, host.Height))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        Dim seek As Action(Of ClockGroup, Double) = Sub(clock, milliseconds)
                                                        PauseSelectionClock(clock)
                                                        clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(milliseconds), TimeSeekOrigin.BeginTime)
                                                        layout()
                                                    End Sub
        Using source = PreviewSurface.Attach(host, 500, 300)
            layout()
            check(action.Visibility = Visibility.Collapsed AndAlso action.Opacity = 0 AndAlso motion.Scale.ScaleX = 0 AndAlso
                motion.Scale.ScaleY = 0 AndAlso Not action.IsHitTestVisible AndAlso motion.Transition Is Nothing,
                "install action cold state is collapsed transparent unscaled and inert without an animation")
            motion.SelectShown(True)
            Dim opening = motion.Transition
            check(opening IsNot Nothing AndAlso action.Visibility = Visibility.Visible AndAlso Not action.IsHitTestVisible,
                "showing the loaded install action creates one clock and keeps its transparent early frames inert")
            seek(opening, 40)
            check(action.Opacity = 0 AndAlso motion.Scale.ScaleX = 0 AndAlso motion.Scale.ScaleY = 0,
                "install action does not flash before PCL's fifty-millisecond delay")
            seek(opening, 90)
            check(Math.Abs(action.Opacity - 0.5) < 0.00001 AndAlso Math.Abs(motion.Scale.ScaleX - ReferenceInstallAction(0, True, False, 90)) < 0.00001 AndAlso
                motion.Scale.ScaleX = motion.Scale.ScaleY AndAlso Not action.IsHitTestVisible,
                "loaded install action uses the source rebound on both axes while incomplete opacity stays gated")
            inner.IsEnabled = False
            check(motion.Transition Is opening AndAlso Not inner.IsEnabled,
                "disabling installation does not cancel its independent display animation or change the disabled state")
            seek(opening, 140)
            check(action.IsHitTestVisible AndAlso Not inner.IsEnabled AndAlso action.Opacity = 1,
                "install action opens its local input gate after full opacity without enabling a disabled download")
            inner.IsEnabled = True
            check(motion.Transition Is opening AndAlso DirectCast(inner.RenderTransform, ScaleTransform).ScaleX = 0.9,
                "install action display owns neither the inner button's enabled state nor its press transform")
            seek(opening, 210)
            Dim beforeScale = motion.Scale.ScaleX, beforeOpacity = action.Opacity
            motion.SelectShown(False)
            Dim hiding = motion.Transition
            check(hiding IsNot Nothing AndAlso hiding IsNot opening AndAlso Math.Abs(motion.Scale.ScaleX - beforeScale) < 0.00001 AndAlso action.Opacity = beforeOpacity,
                "reversing an install-action rebound captures current scale and opacity without a first-frame jump")
            check(action.Visibility = Visibility.Visible AndAlso Not action.IsHitTestVisible,
                "install action exit remains visible for its short animation but rejects input immediately")
            seek(hiding, 75)
            check(Math.Abs(motion.Scale.ScaleX - ReferenceInstallAction(beforeScale, False, False, 75)) < 0.00001 AndAlso
                Math.Abs(action.Opacity - ReferenceInstallAction(beforeOpacity, False, True, 75)) < 0.00001,
                "install action hide follows independent quadratic scale and delayed linear opacity tracks")
            Dim reverseScale = motion.Scale.ScaleX, reverseOpacity = action.Opacity
            motion.SelectShown(True)
            Dim returning = motion.Transition
            check(returning IsNot Nothing AndAlso returning IsNot hiding AndAlso Math.Abs(motion.Scale.ScaleX - reverseScale) < 0.00001 AndAlso
                Math.Abs(action.Opacity - reverseOpacity) < 0.00001,
                "reopening a partially hidden install action preserves both interrupted visible values")
            opening.Controller.SkipToFill() : hiding.Controller.SkipToFill() : layout()
            check(motion.Transition Is returning AndAlso action.Visibility = Visibility.Visible AndAlso Not action.IsHitTestVisible,
                "obsolete install-action show and hide completions cannot collapse or unlock the newer entry")
            seek(returning, 140)
            check(action.Opacity = 1 AndAlso action.IsHitTestVisible,
                "reversed install action exposes input once the replacement opacity track has finished")
            seek(returning, 451)
            check(motion.Transition Is Nothing AndAlso action.Opacity = 1 AndAlso motion.Scale.ScaleX = 1 AndAlso motion.Scale.ScaleY = 1 AndAlso
                Not action.HasAnimatedProperties AndAlso Not motion.Scale.HasAnimatedProperties AndAlso action.IsHitTestVisible,
                "completed install action releases opacity and scale clocks and settles its unit-scale interactive state")
            motion.SelectShown(False, False) : enabled = False : motion.SelectShown(True)
            check(motion.Transition Is Nothing AndAlso action.Visibility = Visibility.Visible AndAlso action.Opacity = 1 AndAlso motion.Scale.ScaleX = 1,
                "reduced motion shows the install action immediately without leaving it scaled or transparent")
            motion.SelectShown(False)
            check(motion.Transition Is Nothing AndAlso action.Visibility = Visibility.Collapsed AndAlso action.Opacity = 0 AndAlso
                motion.Scale.ScaleX = 0 AndAlso Not action.IsHitTestVisible,
                "reduced motion hides the install action immediately and removes its input surface")
            enabled = True : pane.Visibility = Visibility.Collapsed : motion.SelectShown(True)
            check(motion.Transition Is Nothing AndAlso motion.Scale.ScaleX = 1 AndAlso action.Opacity = 1,
                "an install action selected under a hidden parent settles without creating an invisible animation")
            pane.Visibility = Visibility.Visible : layout()
            check(motion.Transition Is Nothing AndAlso action.IsVisible AndAlso motion.Scale.ScaleX = 1,
                "revealing a settled install-action parent does not revive a hidden display clock")
            motion.SelectShown(False)
            hiding = motion.Transition : seek(hiding, 40)
            pane.Visibility = Visibility.Collapsed : layout()
            check(motion.Transition Is Nothing AndAlso action.Visibility = Visibility.Collapsed AndAlso action.Opacity = 0 AndAlso Not action.IsHitTestVisible,
                "hiding the parent during install-action exit releases every clock and settles its hidden state")
            pane.Visibility = Visibility.Visible : layout() : motion.SelectShown(True)
            opening = motion.Transition : seek(opening, 90)
            source.RootVisual = Nothing : FlushControlLayout()
            check(motion.Transition Is Nothing AndAlso action.Opacity = 1 AndAlso motion.Scale.ScaleX = 1 AndAlso Not motion.Scale.HasAnimatedProperties,
                "unloading an install-action entry removes its clocks and preserves the latest shown state")
            opening.Controller.SkipToFill() : layout()
            check(motion.Transition Is Nothing AndAlso action.Visibility = Visibility.Visible AndAlso motion.Scale.ScaleX = 1,
                "a detached install-action clock cannot revive motion or overwrite settled state")
            source.RootVisual = host : layout()
            check(motion.Transition Is Nothing AndAlso action.IsVisible AndAlso action.IsHitTestVisible,
                "reattaching an install action retains its settled state without an obsolete input gate")
            motion.SelectShown(False, False) : motion.PauseForChecks = False : motion.SelectShown(True)
            check(PumpMotionUntil(Function() motion.Transition Is Nothing, 1500) AndAlso action.Opacity = 1 AndAlso motion.Scale.ScaleX = 1,
                "unpaused install-action animation completes on the dispatcher without a manual seek")
            motion.SelectShown(False, False)
        End Using
    End Sub

    Private Sub VerifyInstallActionIntegration(directory As String, check As Action(Of Boolean, String))
        Dim owner As New MainWindow(True), content = DirectCast(owner.Content, FrameworkElement)
        Dim host As New Border With {.Width = 850, .Height = 600}
        owner.Content = Nothing : host.Child = content
        Dim context = System.Threading.SynchronizationContext.Current
        System.Threading.SynchronizationContext.SetSynchronizationContext(New System.Windows.Threading.DispatcherSynchronizationContext(owner.Dispatcher))
        Dim pending As New TaskCompletionSource(Of List(Of ComponentEntry))(TaskCreationOptions.RunContinuationsAsynchronously)
        Dim loading As Task = Nothing
        Dim layout As Action = Sub()
                                   host.Measure(New Size(host.Width, host.Height)) : host.Arrange(New Rect(0, 0, host.Width, host.Height))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        Dim seek As Action(Of ClockGroup, Double) = Sub(clock, milliseconds)
                                                        PauseSelectionClock(clock)
                                                        clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(milliseconds), TimeSeekOrigin.BeginTime)
                                                        layout()
                                                    End Sub
        Try
            Using source = PreviewSurface.Attach(host, 850, 600)
                layout() : owner._motionPreview = True : owner._installActionMotion.PauseForChecks = True
                owner._surfaceMotion.PauseForChecks = True : owner._categoryMotion.PauseForChecks = True
                check(owner.InstallActionHost.Visibility = Visibility.Collapsed AndAlso owner._installActionMotion.Transition Is Nothing,
                    "version browser has no visible install action or display clock before choosing a version")
                Dim entry = owner._versions.Single(Function(item) item.Id = "1.20.1")
                owner.SelectVersion(entry)
                Dim opening = owner._installActionMotion.Transition, page = owner._pageSwitch.Transition
                page.Controller.Pause() : layout()
                check(opening IsNot Nothing AndAlso page IsNot Nothing AndAlso owner.InstallActionHost.Parent Is owner.InstallPage.Parent AndAlso
                    Not owner.InstallActionHost.IsDescendantOf(owner.InstallPage),
                    "install action and install scroller have separate source-aligned parents and animation ownership")
                owner.InstallButton.ApplyTemplate() : layout()
                Dim buttonVisual = DirectCast(VisualTreeHelper.GetChild(owner.InstallButton, 0), FrameworkElement)
                check(TypeOf buttonVisual.RenderTransform Is ScaleTransform AndAlso buttonVisual.RenderTransform IsNot owner._installActionMotion.Scale AndAlso
                    owner.InstallActionHost.RenderTransform Is owner._installActionMotion.Scale,
                    "install-action show scaling remains separate from the button template's own press transform")
                seek(page, 90) : seek(opening, 90)
                check(owner.InstallPage.Opacity = 0 AndAlso Math.Abs(owner.InstallActionHost.Opacity - 0.5) < 0.00001 AndAlso
                    Math.Abs(owner._installActionMotion.Scale.ScaleX - ReferenceInstallAction(0, True, False, 90)) < 0.00001,
                    "install action independently appears while the incoming horizontal install page is still transparent")
                SaveControlVisual(host, IO.Path.Combine(directory, "install-action-090.png"))
                seek(opening, 140)
                check(CBool(owner.InstallActionHost.ReadLocalValue(IsHitTestVisibleProperty)) AndAlso Not owner.InstallActionHost.IsHitTestVisible AndAlso
                    Not owner.DownloadViewHost.IsHitTestVisible,
                    "full-opacity action cannot bypass the horizontal page's longer input gate")
                seek(page, 261)
                check(owner.DownloadViewHost.IsHitTestVisible AndAlso owner.InstallActionHost.IsHitTestVisible AndAlso
                    owner._installActionMotion.Transition Is opening AndAlso owner._pageSwitch.Transition Is Nothing,
                    "settled horizontal navigation releases input without truncating the independent button rebound")
                seek(opening, 250)
                check(Math.Abs(owner._installActionMotion.Scale.ScaleX - 1.0375) < 0.00001 AndAlso PageX(owner.InstallPage) = 0,
                    "real install action retains the small source overshoot without translating with its scroller")
                SaveControlVisual(host, IO.Path.Combine(directory, "install-action-250.png"))
                seek(opening, 451)
                owner.SelectComponent(owner._componentEntries(ComponentKind.Fabric)(0))
                owner.InstanceName.Text = "独立按钮动画保留名称"
                Dim selected = owner._componentSelection.Item(ComponentKind.Fabric), selectedCard = owner._componentViews(ComponentKind.Fabric).Card
                owner._componentProvider = Function(policy, kind, id, token) pending.Task
                loading = owner.RefreshComponentListsAsync(ComponentKind.NeoForge)
                Dim generation = owner._componentGeneration, request = owner._componentRequests(ComponentKind.NeoForge)
                owner.SetDownloadView(DownloadView.Versions)
                Dim hiding = owner._installActionMotion.Transition
                check(hiding IsNot Nothing AndAlso owner.InstallActionHost.Visibility = Visibility.Visible AndAlso Not owner.InstallActionHost.IsHitTestVisible,
                    "returning to versions runs the short install-action hide without removing its parent early")
                seek(hiding, 75)
                SaveControlVisual(host, IO.Path.Combine(directory, "install-action-hide-075.png"))
                owner.SetDownloadView(DownloadView.Install)
                Dim returning = owner._installActionMotion.Transition
                owner._pageSwitch.Transition.Controller.Pause()
                hiding.Controller.SkipToFill() : layout()
                check(returning IsNot Nothing AndAlso owner._installActionMotion.Transition Is returning AndAlso owner.InstallActionHost.Visibility = Visibility.Visible,
                    "rapid vanilla return and reopen keeps the newest action entry after the old exit completes")
                owner.RefreshTitleNavigation()
                check(owner._installActionMotion.Transition Is returning AndAlso owner._componentRequests(ComponentKind.NeoForge) Is request AndAlso
                    owner._componentGeneration = generation,
                    "same-view chrome refresh does not restart action motion or replace pending component work")
                owner.ShowSettings(Nothing, Nothing) : layout()
                check(owner._installActionMotion.Transition Is Nothing AndAlso owner.InstallActionHost.Visibility = Visibility.Collapsed AndAlso
                    owner._pageSwitch.Transition Is Nothing AndAlso owner.SettingsPage.IsVisible,
                    "opening settings immediately settles the vanilla action and horizontal animation before page handoff")
                owner._surfaceMotion.Finish() : owner.ShowDownload(Nothing, Nothing) : owner._surfaceMotion.Finish() : layout()
                check(owner._installActionMotion.Transition Is Nothing AndAlso owner.InstallActionHost.IsVisible AndAlso
                    owner._installActionMotion.Scale.ScaleX = 1 AndAlso owner.InstallActionHost.Opacity = 1,
                    "settings return restores the current install action without replaying its vanilla-selection popup")
                owner.ShowModpackDownloads(Nothing, Nothing) : layout()
                check(owner._installActionMotion.Transition Is Nothing AndAlso owner.InstallActionHost.Visibility = Visibility.Collapsed AndAlso
                    Not owner.InstallActionHost.IsHitTestVisible,
                    "switching to modpacks removes the vanilla action before their page can receive input")
                owner._categoryMotion.Finish() : owner.ShowVanillaDownloads(Nothing, Nothing) : owner._categoryMotion.Finish() : layout()
                check(owner._downloadView = DownloadView.Install AndAlso owner._installActionMotion.Transition Is Nothing AndAlso owner.InstallActionHost.IsVisible,
                    "returning from modpacks restores the retained vanilla form with an already-settled action")
                owner.ResetTaskView("按钮动画任务联动", True) : owner.SetDownloadView(DownloadView.Progress) : layout()
                check(owner._installActionMotion.Transition Is Nothing AndAlso owner.InstallActionHost.Visibility = Visibility.Collapsed AndAlso
                    owner.DownloadProgress.IsVisible,
                    "task progress takes ownership without leaving a floating vanilla download action")
                owner.ResumeInstallation(Nothing, Nothing) : layout()
                check(owner._downloadView = DownloadView.Install AndAlso Not owner.InstallActionHost.IsVisible AndAlso owner._installActionMotion.Transition Is Nothing,
                    "task return defers the retained vanilla action until page reveal without creating a second installation")
                owner._motionPreview = False : owner.SetDownloadView(DownloadView.Versions) : owner.SetDownloadView(DownloadView.Install) : layout()
                check(owner._installActionMotion.Transition Is Nothing AndAlso owner._installActionMotion.Scale.ScaleX = 1 AndAlso owner.InstallActionHost.Opacity = 1,
                    "disabled preview page motion preserves a fully rendered install action without a display clock")
                owner._pageSwitch.Finish() : owner._motionPreview = True
                owner.SetDownloadView(DownloadView.Versions) : owner._pageSwitch.Finish() : owner._installActionMotion.Finish()
                owner.SetDownloadView(DownloadView.Install) : owner._pageSwitch.Transition.Controller.Pause()
                returning = owner._installActionMotion.Transition
                owner.RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", True}}) : layout()
                check(owner._installActionMotion.Transition Is returning AndAlso owner.InstanceName.Text = "独立按钮动画保留名称" AndAlso
                    owner._selectedEntry Is entry AndAlso owner._componentSelection.Item(ComponentKind.Fabric) Is selected AndAlso
                    owner._componentViews(ComponentKind.Fabric).Card Is selectedCard AndAlso owner._componentRequests(ComponentKind.NeoForge) Is request AndAlso
                    owner._componentGeneration = generation AndAlso Not request.IsCancellationRequested,
                    "dark mode and action navigation preserve selected version custom name component card and active catalogue ownership")
                seek(returning, 250) : owner._pageSwitch.Finish()
                SaveControlVisual(host, IO.Path.Combine(directory, "install-action-dark-250.png"))
                owner.SetDownloadView(DownloadView.Versions) : owner._pageSwitch.Finish() : owner._installActionMotion.Finish()
                owner.SetDownloadView(DownloadView.Install) : owner._pageSwitch.Transition.Controller.Pause()
                RouteDownloadKey(owner.InstanceName, Input.Key.Escape) : layout()
                check(owner._downloadView = DownloadView.Versions AndAlso Not owner._closing AndAlso Not owner._busy AndAlso
                    Not owner.InstallActionHost.IsHitTestVisible,
                    "Escape reverses navigation during action entry while keeping the outgoing download button inert")
            End Using
        Finally
            pending.TrySetResult(New List(Of ComponentEntry)())
            If loading IsNot Nothing Then PumpMotionUntil(Function() loading.IsCompleted, 1500)
            owner._componentProvider = Nothing : owner.CancelComponentRequests()
            owner._motionPreview = False : owner._installActionMotion.Finish() : owner._pageSwitch.Finish()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner._titleNavigation.Finish()
            host.Child = Nothing : owner.Content = content
            System.Threading.SynchronizationContext.SetSynchronizationContext(context)
        End Try
    End Sub
End Class
