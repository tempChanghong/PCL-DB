Imports System.IO
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Automation.Peers
Imports System.Windows.Automation.Provider
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Sub VerifyInstallPressMotion(directory As String, passed As List(Of String))
        Dim initial = passed.Count
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     If Not ok Then Throw New InvalidOperationException(label)
                                                     passed.Add(label)
                                                 End Sub
        Dim owner As New MainWindow(True), content = DirectCast(owner.Content, FrameworkElement)
        Dim host As New Border With {.Width = 850, .Height = 600}
        owner.Content = Nothing : host.Child = content
        Dim layout = ControlLayout(host)
        VerifyInstallPressCurves(check)
        Try
            Using surface = PreviewSurface.Attach(host, 850, 600)
                owner.SelectVersion(owner._versions.Single(Function(value) value.Id = "1.20.1"))
                owner._pageSwitch.Finish() : owner._installActionMotion.Finish() : layout()
                owner.InstallButton.ApplyTemplate() : layout()
                owner.InstallButton.MotionPolicy.EnabledForChecks = True
                owner.InstallButton.MotionPolicy.PauseForChecks = True
                Dim inner = DirectCast(VisualTreeHelper.GetChild(owner.InstallButton, 0), FrameworkElement)
                Dim scale = DirectCast(inner.RenderTransform, ScaleTransform)
                check(owner.InstallButton.IsEnabled AndAlso owner.InstallButton.IsHitTestVisible AndAlso owner.InstallActionHost.Opacity = 1,
                    "floating install press fixture begins with a fully shown interactive action")
                PointerEvent(owner.InstallButton, Mouse.MouseDownEvent)
                File.WriteAllText(Path.Combine(directory, "install-press-initial.json"), New JObject From {
                    {"animated", scale.HasAnimatedProperties}, {"innerScale", scale.ScaleX}, {"outerScale", owner._installActionMotion.Scale.ScaleX}}.ToString())
                check(scale.HasAnimatedProperties,
                    "floating install action has a real source press clock on its inner template transform")
                Dim clock = owner.InstallButton.PressTransition
                clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(800), TimeSeekOrigin.BeginTime) : layout()
                check(Math.Abs(scale.ScaleX - 0.8) < 0.000001 AndAlso owner._installActionMotion.Scale.ScaleX = 1 AndAlso
                    owner.InstallButton.HasPressGesture AndAlso owner._componentSelection.Entries.Count = 0,
                    "holding the real download action reaches PCL's inner 0.8 scale without modifying its outer show scale or install plan")
                SaveControlVisual(host, Path.Combine(directory, "install-button-held-800.png"))
                owner.DownloadViewHost.IsHitTestVisible = False : layout()
                check(Not owner.InstallButton.HasPressGesture AndAlso owner.InstallButton.PressTransition Is Nothing AndAlso scale.ScaleX = 1,
                    "the real download page gate cancels its held floating action before page ownership changes")
                owner.DownloadViewHost.IsHitTestVisible = True
                PointerEvent(owner.InstallButton, Mouse.MouseUpEvent) : clock.Controller.SkipToFill() : layout()
                check(Not owner._hasTask AndAlso owner._downloadView = DownloadView.Install AndAlso owner.InstallButton.PressTransition Is Nothing,
                    "a download-button release after gate recovery cannot start a task or revive the retired press clock")
                VerifyInstallPressLifetime(owner, owner.InstallButton.Template, directory, check)
            End Using
        Finally
            owner._motionPreview = False : owner._pageSwitch.Finish() : owner._categoryMotion.Finish() : owner._surfaceMotion.Finish()
            owner._installActionMotion.Finish() : owner.CancelComponentRequests() : owner._pclThemeMonitor.Dispose()
            host.Child = Nothing : owner.Content = content : ApplyAppearance()
        End Try
        File.WriteAllText(Path.Combine(directory, "install-press-count.json"), New JObject From {{"checks", passed.Count - initial}}.ToString())
    End Sub

    Private Shared Sub VerifyInstallPressCurves(check As Action(Of Boolean, String))
        For Each origin In {1.0, 0.8, 0.93}
            For Each kind As PclInstallPressKind In [Enum].GetValues(GetType(PclInstallPressKind))
                Dim animation As New PclInstallPressAnimation(origin, kind)
                Dim samples = {-10.0, 0, 30, 60, 150, 300, 500, 800, 1000}
                Dim expected As Func(Of Double, Double) =
                    Function(ms)
                        Dim t = Math.Max(0, Math.Min(1, ms / If(kind = PclInstallPressKind.Down, 800, If(kind = PclInstallPressKind.Up, 300, 500))))
                        Select Case kind
                            Case PclInstallPressKind.Down
                                Return origin + (0.85 - origin) * (1 - Math.Pow(1 - t, 4)) - 0.05 * (1 - Math.Pow(1 - Math.Max(0, Math.Min(1, ms / 60)), 3))
                            Case PclInstallPressKind.Up
                                Return origin + (1 - origin) * (1 - Math.Pow(1 - t, 1.5) * Math.Cos(1.5 * Math.PI * t))
                            Case Else
                                Return origin + (1 - origin) * (1 - Math.Pow(1 - t, 3))
                        End Select
                    End Function
                check(samples.All(Function(ms) Math.Abs(animation.Sample(ms) - expected(ms)) < 0.0000001),
                    "floating press retains MyExtraTextButton's parallel increments and ModAnimation powers: " & origin & "/" & kind.ToString())
                check(animation.Duration.TimeSpan.TotalMilliseconds = If(kind = PclInstallPressKind.Down, 800, If(kind = PclInstallPressKind.Up, 300, 500)),
                    "floating press retains source down up and leave durations: " & origin & "/" & kind.ToString())
                Dim clone = DirectCast(animation.Clone(), PclInstallPressAnimation) : clone.Freeze()
                check(clone.IsFrozen AndAlso samples.All(Function(ms) Math.Abs(clone.Sample(ms) - expected(ms)) < 0.0000001),
                    "floating press origin and direction survive WPF cloning and freezing: " & origin & "/" & kind.ToString())
            Next
        Next
        check(Math.Abs(New PclInstallPressAnimation(1, PclInstallPressKind.Down).Sample(800) - 0.8) < 0.0000001 AndAlso
            Math.Abs(New PclInstallPressAnimation(0.8, PclInstallPressKind.Up).Sample(150) - 1.05) < 0.0000001,
            "floating press reaches source held shrink and release overshoot samples")
        check(Math.Abs(New PclInstallPressAnimation(1, PclInstallPressKind.Down).Sample(30) - 0.9349842810058594) < 0.0000001,
            "the sixty-millisecond parallel micro-shrink uses default cubic Fluent rather than a linear increment")
    End Sub

    Private Sub VerifyInstallPressLifetime(owner As MainWindow, template As ControlTemplate, directory As String, check As Action(Of Boolean, String))
        Dim button As New PclInstallButton With {.Template = template, .Width = 160, .Height = 52}
        Dim panel As New Grid With {.Width = 300, .Height = 160}
        panel.Children.Add(button)
        Dim host As New Border With {.Width = 300, .Height = 160, .Child = panel}
        Dim layout = ControlLayout(host), actions = 0
        AddHandler button.Click, Sub() actions += 1
        Dim sendKey As Action(Of Key, Boolean) = Sub(value, down)
                                                Dim args As New KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(button), Environment.TickCount, value) With {
                                                    .RoutedEvent = If(down, Keyboard.KeyDownEvent, Keyboard.KeyUpEvent)}
                                                button.RaiseEvent(args)
                                            End Sub
        Dim seek As Action(Of AnimationClock, Double) = Sub(clock, ms)
                                                            clock.Controller.Pause() : layout()
                                                            clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(ms), TimeSeekOrigin.BeginTime) : layout()
                                                        End Sub
        Using surface = PreviewSurface.Attach(host, 300, 160)
            layout() : button.MotionPolicy.EnabledForChecks = True : button.MotionPolicy.PauseForChecks = True
            button.ApplyTemplate() : layout()
            PointerEvent(button, Mouse.MouseDownEvent)
            Dim held = button.PressTransition
            seek(held, 800)
            PointerEvent(button, Mouse.MouseUpEvent)
            Dim released = button.PressTransition
            check(released IsNot Nothing AndAlso released IsNot held AndAlso Not button.HasPressGesture,
                "floating pointer release replaces only its own held clock with a source rebound")
            seek(released, 150)
            check(Math.Abs(button.PressScale.ScaleX - 1.05) < 0.000001 AndAlso button.PressScale.ScaleY = button.PressScale.ScaleX,
                "floating release shares one XY clock and reaches the source Back overshoot")
            SaveControlVisual(host, Path.Combine(directory, "install-button-release-150.png"))
            held.Controller.SkipToFill() : layout()
            check(button.PressTransition Is released, "an old held clock cannot reset a newer floating rebound")
            seek(released, 301)
            check(button.PressTransition Is Nothing AndAlso button.PressScale.ScaleX = 1 AndAlso Not button.PressScale.HasAnimatedProperties,
                "floating release removes its clocks after reaching unit scale")
            PointerEvent(button, Mouse.MouseDownEvent) : seek(button.PressTransition, 800)
            Dim beforeLeave = actions
            LeavePointer(button)
            Dim leaving = button.PressTransition
            check(leaving IsNot Nothing AndAlso Not button.HasPressGesture, "moving out of a held floating button retires its gesture and starts the source leave recovery")
            seek(leaving, 250)
            check(Math.Abs(button.PressScale.ScaleX - 0.975) < 0.000001, "floating leave uses the source five-hundred-millisecond default Fluent curve")
            PointerEvent(button, Mouse.MouseUpEvent) : layout()
            check(actions = beforeLeave AndAlso button.PressTransition Is leaving, "a release after pointer leave cannot click or replace its recovery clock")
            seek(leaving, 501)
            For Each boundary In {"gate", "disabled", "hidden", "unload", "theme", "template", "policy"}
                PointerEvent(button, Mouse.MouseDownEvent)
                Dim retired = button.PressTransition, scale = button.PressScale
                seek(retired, 60)
                Dim before = actions
                Select Case boundary
                    Case "gate" : panel.IsHitTestVisible = False
                    Case "disabled" : button.IsEnabled = False
                    Case "hidden" : panel.Visibility = Visibility.Collapsed
                    Case "unload" : panel.Children.Remove(button)
                    Case "theme" : owner.RestoreAppearance(New JObject From {{"darkMode", True}})
                    Case "template" : button.Template = Nothing : button.ApplyTemplate()
                    Case "policy" : button.MotionPolicy.EnabledForChecks = False
                End Select
                layout()
                check(Not button.HasPressGesture AndAlso button.PressTransition Is Nothing AndAlso Not scale.HasAnimatedProperties AndAlso scale.ScaleX = 1,
                    "floating press retirement clears gesture and visual state: " & boundary)
                Select Case boundary
                    Case "gate" : panel.IsHitTestVisible = True
                    Case "disabled" : button.IsEnabled = True
                    Case "hidden" : panel.Visibility = Visibility.Visible
                    Case "unload" : panel.Children.Add(button)
                    Case "template" : button.Template = template : button.ApplyTemplate()
                    Case "policy" : button.MotionPolicy.EnabledForChecks = True
                End Select
                layout() : PointerEvent(button, Mouse.MouseUpEvent) : retired.Controller.SkipToFill() : layout()
                check(actions = before AndAlso button.PressTransition Is Nothing AndAlso button.PressScale.ScaleX = 1,
                    "retired pointer release cannot activate or reanimate the recovered floating action: " & boundary)
            Next
            sendKey(Key.Space, True)
            check(button.HasPressGesture AndAlso button.PressTransition IsNot Nothing, "Space starts the same floating press feedback without requiring desktop pointer capture")
            panel.IsHitTestVisible = False : layout() : panel.IsHitTestVisible = True : layout()
            Dim keyActions = actions
            sendKey(Key.Space, False) : layout()
            check(actions = keyActions AndAlso Not button.HasPressGesture AndAlso button.PressTransition Is Nothing,
                "Space released after page-gate recovery cannot activate a retired floating gesture")
            sendKey(Key.Space, True)
            Dim lost As New KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, button, Nothing) With {.RoutedEvent = Keyboard.LostKeyboardFocusEvent}
            button.RaiseEvent(lost) : sendKey(Key.Space, False) : layout()
            check(actions = keyActions AndAlso Not button.HasPressGesture AndAlso button.PressTransition Is Nothing,
                "losing keyboard focus retires the floating Space gesture before its late release")
            button.MotionPolicy.EnabledForChecks = False
            PointerEvent(button, Mouse.MouseDownEvent)
            check(button.PressTransition Is Nothing AndAlso button.PressScale.ScaleX = 1, "disabled client animation uses static press geometry")
            PointerEvent(button, Mouse.MouseUpEvent)
            Dim provider = DirectCast(UIElementAutomationPeer.CreatePeerForElement(button).GetPattern(PatternInterface.Invoke), IInvokeProvider)
            Dim beforeInvoke = actions
            provider.Invoke()
            check(PumpMotionUntil(Function() actions = beforeInvoke + 1), "native Button Invoke automation remains functional after retired gestures")
            panel.IsHitTestVisible = False
            provider.Invoke() : FlushControlLayout()
            check(actions = beforeInvoke + 1, "native Button Invoke respects the same hidden-page gate as pointer and keyboard activation")
            panel.IsHitTestVisible = True : button.MotionPolicy.EnabledForChecks = True
            panel.IsHitTestVisible = False
            provider.Invoke() : panel.IsHitTestVisible = True : FlushControlLayout()
            check(actions = beforeInvoke + 1, "Invoke requested while gated cannot activate later after the page gate has reopened")
        End Using
        check(button.PressTransition Is Nothing AndAlso Not button.HasPressGesture,
            "detaching the floating press fixture leaves no running clock or held gesture")
    End Sub
End Class
