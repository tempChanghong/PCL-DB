Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Sub VerifyCapsuleRadioMotion(directory As String, check As Action(Of Boolean, String))
        Dim tab As New PclRadioButton With {.Text = "全部"}, peer As New PclRadioButton With {.Text = "1.20.1", .ColorType = PclRadioButton.ColorState.Highlight}
        Dim items As New ControlLayoutProbe With {.Orientation = Orientation.Horizontal, .VerticalAlignment = VerticalAlignment.Center}
        items.Children.Add(tab) : items.Children.Add(peer)
        Dim host As New Border With {.Width = 300, .Height = 90, .Child = items}
        Dim layout = ControlLayout(host)
        Dim finish As Action = Sub()
                                   For Each clock In {tab.ColorTransition, tab.InkTransition, tab.LogoTransition}
                                       If clock IsNot Nothing Then SeekControl(clock, 151)
                                   Next
                                   layout()
                               End Sub
        Dim stable As Func(Of Boolean) = Function() tab.ColorTransition Is Nothing AndAlso tab.InkTransition Is Nothing AndAlso tab.LogoTransition Is Nothing AndAlso
                                                       Not DirectCast(tab.Background, SolidColorBrush).HasAnimatedProperties AndAlso
                                                       Not DirectCast(tab.ShapeLogo.Fill, SolidColorBrush).HasAnimatedProperties AndAlso Not DirectCast(tab.LabText.Foreground, SolidColorBrush).HasAnimatedProperties
        Dim clicks = 0
        AddHandler tab.Click, Sub()
                                  clicks += 1 : tab.Checked = True
                              End Sub
        Using source = PreviewSurface.Attach(host, 300, 90)
            layout() : SetSelectionHover(tab, True)
            Dim timer = System.Diagnostics.Stopwatch.StartNew()
            If Not PumpMotionUntil(Function() timer.ElapsedMilliseconds >= 210) Then Throw New InvalidOperationException("Capsule color did not settle.")
            check(Not DirectCast(tab.Background, SolidColorBrush).HasAnimatedProperties AndAlso
                Not DirectCast(tab.ShapeLogo.Fill, SolidColorBrush).HasAnimatedProperties AndAlso Not DirectCast(tab.LabText.Foreground, SolidColorBrush).HasAnimatedProperties,
                "a settled capsule radio releases its color clocks instead of retaining HoldEnd animations")
            Dim blend As New PclRadioColorAnimation(Color.FromArgb(10, 20, 40, 250), Color.FromArgb(210, 220, 140, 50), 120)
            Dim clone = DirectCast(blend.Clone(), PclRadioColorAnimation) : clone.Freeze()
            check(clone.IsFrozen AndAlso clone.Sample(0.25) = Color.FromArgb(60, 70, 65, 200) AndAlso clone.Sample(0.5) = Color.FromArgb(110, 120, 90, 150) AndAlso
                clone.Sample(1) = Color.FromArgb(210, 220, 140, 50), "capsule color interpolation retains PCL's byte-ARGB linear rounding after WPF cloning and freezing")
            tab.ColorType = PclRadioButton.ColorState.Highlight
            tab.LabText.Margin = New Thickness(-2, 0, 10, 0) : peer.LabText.Margin = tab.LabText.Margin
            tab.MotionPolicy.PauseForChecks = True : peer.MotionPolicy.EnabledForChecks = False
            For Each dark In {False, True}
                RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", dark}})
                tab.MotionPolicy.EnabledForChecks = False : tab.Checked = False : SetSelectionHover(tab, False)
                tab.MotionPolicy.EnabledForChecks = True : layout()
                check(stable() AndAlso DirectCast(tab.Background, SolidColorBrush).Color = PclAnimation.Brush("ColorBrushSemiTransparent").Color AndAlso
                    DirectCast(tab.LabText.Foreground, SolidColorBrush).Color = PclAnimation.Brush("ColorBrush3").Color,
                    "unselected highlight capsule uses PCL's transparent fill and accent text in dark mode " & dark)
                Dim measures = items.Measures, arrangements = items.Arrangements
                SetSelectionHover(tab, True)
                Dim hovering = tab.ColorTransition, origin = DirectCast(tab.Background, SolidColorBrush).Color
                SeekControl(hovering, 45) : layout()
                check(hovering.Timeline.Duration.TimeSpan.TotalMilliseconds = 90 AndAlso DirectCast(tab.Background, SolidColorBrush).Color <> origin,
                    "highlight capsule hover advances its source 90 ms background transition in dark mode " & dark)
                check(items.Measures = measures AndAlso items.Arrangements = arrangements,
                    "capsule color frames do not remeasure or rearrange the parent in dark mode " & dark)
                origin = DirectCast(tab.Background, SolidColorBrush).Color
                PointerEvent(tab, Mouse.MouseDownEvent)
                Dim pressed = tab.ColorTransition
                check(pressed IsNot hovering AndAlso pressed.Timeline.Duration.TimeSpan.TotalMilliseconds = 90 AndAlso DirectCast(tab.Background, SolidColorBrush).Color = origin,
                    "highlight press retargets from the displayed color over PCL 90 ms in dark mode " & dark)
                hovering.Controller.SkipToFill() : layout()
                check(tab.ColorTransition Is pressed, "a retired capsule hover cannot settle a newer press in dark mode " & dark)
                PointerEvent(tab, Mouse.MouseUpEvent)
                check(tab.Checked AndAlso tab.ColorTransition.Timeline.Duration.TimeSpan.TotalMilliseconds = 120 AndAlso tab.InkTransition.Timeline.Duration.TimeSpan.TotalMilliseconds = 120,
                    "highlight selection fades its fill and white ink over PCL 120 ms in dark mode " & dark)
                finish()
                check(stable() AndAlso DirectCast(tab.Background, SolidColorBrush).Color = PclAnimation.Brush("ColorBrush3").Color AndAlso DirectCast(tab.LabText.Foreground, SolidColorBrush).Color = Colors.White,
                    "selected capsule settles to accent fill and white ink without held clocks in dark mode " & dark)
                tab.Checked = False
                Dim unchanged = tab.ColorTransition, retained = tab.Background
                For index = 1 To 500
                    tab.Checked = False : tab.ColorType = PclRadioButton.ColorState.Highlight
                Next
                check(tab.ColorTransition Is unchanged AndAlso tab.Background Is retained,
                    "five hundred unchanged capsule states retain the same brush and transition in dark mode " & dark)
                tab.Checked = True : finish()
                Dim before = clicks
                ControlKey(tab, Key.Space, True) : PointerEvent(tab, Mouse.MouseDownEvent) : PointerEvent(tab, Mouse.MouseUpEvent)
                check(clicks = before, "an already selected capsule absorbs keyboard and pointer activation in dark mode " & dark)
                SaveControlVisual(host, IO.Path.Combine(directory, "capsule-highlight-" & If(dark, "dark", "blue") & ".png"))
            Next
            For Each interruption In {"input-gate", "hidden", "detached", "disabled"}
                tab.MotionPolicy.EnabledForChecks = False : tab.Checked = False : SetSelectionHover(tab, True)
                tab.MotionPolicy.EnabledForChecks = True : PointerEvent(tab, Mouse.MouseDownEvent)
                Dim old = tab.ColorTransition, before = clicks
                Select Case interruption
                    Case "input-gate" : items.IsHitTestVisible = False
                    Case "hidden" : host.Visibility = Visibility.Collapsed
                    Case "disabled" : tab.IsEnabled = False
                    Case Else : items.Children.Clear()
                End Select
                layout()
                tab.RaiseEvent(New KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Enter) With {.RoutedEvent = Keyboard.KeyDownEvent})
                check(stable() AndAlso clicks = before, "inactive capsule rejects keyboard input and releases colors across " & interruption)
                Select Case interruption
                    Case "input-gate" : items.IsHitTestVisible = True
                    Case "hidden" : host.Visibility = Visibility.Visible
                    Case "disabled" : tab.IsEnabled = True
                    Case Else : items.Children.Add(tab) : items.Children.Add(peer)
                End Select
                layout() : old?.Controller.SkipToFill() : PointerEvent(tab, Mouse.MouseUpEvent)
                check(clicks = before AndAlso Not tab.Checked, "restoring a capsule cannot revive its old press across " & interruption)
            Next
            tab.MotionPolicy.EnabledForChecks = False : tab.ColorType = PclRadioButton.ColorState.White : tab.Checked = True
            check(DirectCast(tab.Background, SolidColorBrush).Color = Colors.White AndAlso DirectCast(tab.LabText.Foreground, SolidColorBrush).Color = PclAnimation.Brush("ColorBrush3").Color,
                "the default white top-tab palette remains distinct from highlight filters")
            tab.MotionPolicy.EnabledForChecks = True : tab.Checked = False : PointerEvent(tab, Mouse.MouseDownEvent)
            check(tab.ColorTransition.Timeline.Duration.TimeSpan.TotalMilliseconds = 60 AndAlso DirectCast(tab.ColorTransition.Timeline, PclRadioColorAnimation).Destination.A = 120,
                "white top-tab press retains PCL's 60 ms transition to alpha 120")
            tab.MotionPolicy.EnabledForChecks = False : tab.ColorType = PclRadioButton.ColorState.Highlight : tab.Checked = True
            check(stable() AndAlso DirectCast(tab.Background, SolidColorBrush).Color = PclAnimation.Brush("ColorBrush3").Color,
                "disabled client-area motion selects capsule colors immediately without clocks")
            tab.MotionPolicy.EnabledForChecks = True : tab.Checked = False
            Dim obsolete = tab.ColorTransition
            SeekControl(obsolete, 45)
            RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", False}})
            obsolete.Controller.SkipToFill() : layout()
            check(stable() AndAlso DirectCast(tab.LabText.Foreground, SolidColorBrush).Color = PclAnimation.Brush("ColorBrush3").Color,
                "theme replacement settles all capsule colors and an old completion cannot restore the old palette")
        End Using
    End Sub
End Class
