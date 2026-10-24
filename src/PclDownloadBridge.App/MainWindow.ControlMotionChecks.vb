Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Controls.Primitives
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports System.Windows.Automation.Peers
Imports System.Windows.Automation.Provider
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Public Function VerifyControlMotionUi(directory As String, passed As List(Of String)) As List(Of String)
        If Not _preview Then Throw New InvalidOperationException("Control motion checks require isolated preview mode.")
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     If Not ok Then Throw New InvalidOperationException(label)
                                                     passed.Add(label)
                                                 End Sub
        Dim saved = _appearance.ToJson()
        Try
            VerifyImmediateScrollBursts(directory, check)
            VerifyFilterWheelBursts(directory, check)
            VerifyCapsuleGestureOwnership(directory, check)
            VerifyHintAndActionPresentation(directory, check)
            VerifyPclActionColorOwnership(directory, check)
            VerifyResourceRowMotion(directory, check)
            VerifyCapsuleRadioMotion(directory, check)
            VerifyRadioCurves(check)
            VerifyCaptionMotion(directory, check)
            VerifyScrollChromeMotion(directory, check)
            VerifyRadioBoxMotion(directory, check)
            VerifyAppearanceRadioIntegration(directory, check)
            VerifyLoadingMotionUi(directory, check)
        Finally
            RestoreAppearance(saved)
        End Try
        Return passed
    End Function

    Private Shared Sub ControlPointer(control As UIElement, over As Boolean)
        ' Same net48 fixture mechanism as SetButtonPointer. No desktop pointer moves.
        SetControlFlag(control, "IsMouseOverCache", over)
        control.RaiseEvent(New MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) With {.RoutedEvent = If(over, Mouse.MouseEnterEvent, Mouse.MouseLeaveEvent)})
    End Sub

    Private Shared Sub SetControlFlag(control As UIElement, name As String, value As Boolean)
        Dim setter = GetType(UIElement).GetMethod("WriteFlag", Reflection.BindingFlags.Instance Or Reflection.BindingFlags.NonPublic)
        If setter Is Nothing Then Throw New InvalidOperationException("The net48 WPF state fixture is unavailable.")
        setter.Invoke(control, New Object() {[Enum].Parse(setter.GetParameters()(0).ParameterType, name), value})
    End Sub

    Private Shared Sub ControlKey(control As UIElement, key As Key, down As Boolean)
        control.RaiseEvent(New KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(control), Environment.TickCount, key) With {.RoutedEvent = If(down, Keyboard.KeyDownEvent, Keyboard.KeyUpEvent)})
    End Sub

    Private Shared Sub SeekControl(clock As AnimationClock, milliseconds As Double)
        If clock Is Nothing Then Throw New InvalidOperationException("Expected an owned control animation.")
        clock.Controller.Pause()
        If Not PumpMotionUntil(Function() clock.IsPaused) Then Throw New InvalidOperationException("The control clock did not acknowledge Pause.")
        clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(milliseconds), TimeSeekOrigin.BeginTime)
        FlushControlLayout()
    End Sub

    Private Shared Function ControlLayout(host As Border) As Action
        Return Sub()
                   host.Measure(New Size(host.Width, host.Height))
                   host.Arrange(New Rect(0, 0, host.Width, host.Height))
                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
               End Sub
    End Function

    Private Sub VerifyCaptionMotion(directory As String, check As Action(Of Boolean, String))
        Dim icon As New PclIconButton With {.Width = 28, .Height = 28, .Theme = PclIconButton.Themes.White, .LogoScale = 0.72, .Logo = TitleClose.Logo}
        Dim row As New StackPanel With {.Orientation = Orientation.Horizontal}
        row.Children.Add(icon)
        Dim host As New Border With {.Width = 260, .Height = 64, .Padding = New Thickness(18), .Child = row, .Background = Brushes.SteelBlue}
        Dim layout = ControlLayout(host), actions = 0, bubbled = 0
        icon.MotionPolicy.EnabledForChecks = True
        icon.MotionPolicy.PauseForChecks = True
        AddHandler icon.Click, Sub() actions += 1
        AddHandler row.MouseLeftButtonDown, Sub() bubbled += 1
        Using source = PreviewSurface.Attach(host, 260, 64)
            layout()
            check(TitleClose.Theme = PclIconButton.Themes.White AndAlso TitleMinimize.Theme = PclIconButton.Themes.White AndAlso TitleClose.Width = 28 AndAlso TitleClose.LogoScale = 0.72, "actual caption controls use the fixed PCL white icon role and hit geometry")
            For Each dark In {False, True}
                RestoreAppearance(New JObject From {{"darkMode", dark}})
                check(DirectCast(icon.Path.Fill, SolidColorBrush).Color = Colors.White, "caption ink stays light in dark mode " & dark)
                Dim beforeHover = DirectCast(icon.PanBack.Background, SolidColorBrush).Color.A
                ControlPointer(icon, True)
                Dim colorClock = icon.BackgroundTransition
                check(colorClock.Timeline.Duration.TimeSpan.TotalMilliseconds = 120, "caption hover uses PCL 120 ms background transition in dark mode " & dark)
                SeekControl(colorClock, 60)
                IO.File.WriteAllText(IO.Path.Combine(directory, "caption-hover-observation.json"), New JObject From {{"before", beforeHover}, {"after", DirectCast(icon.PanBack.Background, SolidColorBrush).Color.A}, {"time", colorClock.CurrentTime?.TotalMilliseconds}, {"focused", icon.IsKeyboardFocused}, {"owned", icon.BackgroundTransition Is colorClock}}.ToString())
                check(Math.Abs(DirectCast(icon.PanBack.Background, SolidColorBrush).Color.A - (beforeHover + 50) / 2.0) <= 1, "caption hover advances halfway from the displayed ink toward source alpha 50 in dark mode " & dark)
                PointerEvent(icon, Mouse.MouseDownEvent)
                Dim down = icon.ScaleTransition
                SeekControl(down, 400)
                check(Math.Abs(DirectCast(icon.PanBack.RenderTransform, ScaleTransform).ScaleX - 0.8) < 0.00001 AndAlso bubbled = 0, "caption press shrinks to 0.8 and stays out of title dragging in dark mode " & dark)
                PointerEvent(icon, Mouse.MouseUpEvent)
                Dim up = icon.ScaleTransition
                SeekControl(up, 175)
                check(Math.Abs(DirectCast(icon.PanBack.RenderTransform, ScaleTransform).ScaleX - ReferencePressScale(PclPressKind.IconUp, 0.8, 175)) < 0.00001, "caption release retains the PCL parallel rebound in dark mode " & dark)
                ControlPointer(icon, False)
                Dim leave = icon.ScaleTransition
                up.Controller.SkipToFill() : colorClock.Controller.SkipToFill() : layout()
                check(icon.ScaleTransition Is leave AndAlso icon.BackgroundTransition IsNot colorClock, "retired caption clocks cannot finish a newer leave in dark mode " & dark)
                SeekControl(leave, 251) : SeekControl(icon.BackgroundTransition, 151)
                check(icon.ScaleTransition Is Nothing AndAlso icon.BackgroundTransition Is Nothing AndAlso DirectCast(icon.PanBack.Background, SolidColorBrush).Color.A = 0, "caption leave clears scale and background clocks in dark mode " & dark)
                SaveControlVisual(host, IO.Path.Combine(directory, "caption-" & If(dark, "dark", "blue") & ".png"))
            Next
            check(actions = 2, "two complete caption gestures invoke exactly two actions")
            PointerEvent(icon, Mouse.MouseDownEvent)
            icon.RaiseEvent(New KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, icon, Nothing) With {.RoutedEvent = Keyboard.LostKeyboardFocusEvent})
            PointerEvent(icon, Mouse.MouseUpEvent)
            check(actions = 2 AndAlso icon.ScaleTransition Is Nothing, "a caption pointer release after focus loss cannot activate the abandoned gesture")
            For Each boundary In {"gate", "hide", "disable", "detach"}
                PointerEvent(icon, Mouse.MouseDownEvent)
                Dim old = icon.ScaleTransition
                ControlKey(icon, Key.Space, True)
                Select Case boundary
                    Case "gate" : row.IsHitTestVisible = False
                    Case "hide" : host.Visibility = Visibility.Collapsed
                    Case "disable" : row.IsEnabled = False
                    Case Else : row.Children.Clear()
                End Select
                layout()
                check(icon.ScaleTransition Is Nothing AndAlso icon.InkTransition Is Nothing AndAlso icon.BackgroundTransition Is Nothing, "caption " & boundary & " releases every owned clock")
                row.IsHitTestVisible = True : row.IsEnabled = True : host.Visibility = Visibility.Visible
                If icon.Parent Is Nothing Then row.Children.Add(icon)
                layout()
                old.Controller.SkipToFill()
                PointerEvent(icon, Mouse.MouseUpEvent) : ControlKey(icon, Key.Space, False) : layout()
                check(actions = 2 AndAlso icon.ScaleTransition Is Nothing, "caption " & boundary & " does not replay a stale pointer or space release")
            Next
            icon.MotionPolicy.EnabledForChecks = False
            PointerEvent(icon, Mouse.MouseDownEvent) : PointerEvent(icon, Mouse.MouseUpEvent)
            PressControlKey(icon, Key.Space)
            check(actions = 4 AndAlso icon.ScaleTransition Is Nothing AndAlso DirectCast(icon.PanBack.RenderTransform, ScaleTransform).ScaleX = 1, "reduced motion caption remains clickable by pointer and Space without a clock")
            icon.MotionPolicy.EnabledForChecks = True
            PointerEvent(icon, Mouse.MouseDownEvent)
            Dim active = icon.ScaleTransition
            icon.MotionPolicy.EnabledForChecks = False
            active.Controller.SkipToFill() : layout()
            check(icon.ScaleTransition Is Nothing AndAlso DirectCast(icon.PanBack.RenderTransform, ScaleTransform).ScaleX = 1, "turning reduced motion on during a caption press immediately removes the old clock")
            PointerEvent(row, Mouse.MouseDownEvent)
            check(bubbled = 1, "blank title surface still receives its drag event")
            System.Windows.Automation.AutomationProperties.SetName(icon, "关闭")
            Dim peer = UIElementAutomationPeer.CreatePeerForElement(icon)
            check(peer.GetAutomationControlType() = AutomationControlType.Button AndAlso peer.GetName() = "关闭", "caption icon retains the named Button accessibility role")
            Dim invoke = DirectCast(peer.GetPattern(PatternInterface.Invoke), IInvokeProvider)
            invoke.Invoke() : layout()
            check(actions = 5, "caption accessibility Invoke activates once")
            row.IsHitTestVisible = False
            invoke.Invoke() : layout()
            check(actions = 5, "caption accessibility Invoke obeys the inherited input gate")
            row.IsHitTestVisible = True : icon.IsEnabled = False
            Dim rejected = False
            Try
                invoke.Invoke()
            Catch ex As System.Windows.Automation.ElementNotEnabledException
                rejected = True
            End Try
            check(rejected AndAlso actions = 5, "disabled caption accessibility Invoke retains native rejection semantics")
            source.RootVisual = Nothing : FlushControlLayout()
        End Using
    End Sub

    Private Sub VerifyScrollChromeMotion(directory As String, check As Action(Of Boolean, String))
        Dim viewer As New PclScrollViewer With {.VerticalScrollBarVisibility = ScrollBarVisibility.Auto, .HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            .Content = New Border With {.Height = 1400, .Background = Brushes.Transparent}}
        Dim host As New Border With {.Width = 260, .Height = 240, .Child = viewer}
        host.SetResourceReference(BackgroundProperty, "CardSurfaceBrush")
        Dim layout = ControlLayout(host)
        Using source = PreviewSurface.Attach(host, 260, 240)
            layout()
            Dim bar = viewer.VerticalBar, chrome = DirectCast(bar.Template.FindName("Chrome", bar), PclScrollBarChrome)
            chrome.MotionPolicy.EnabledForChecks = True
            chrome.MotionPolicy.PauseForChecks = True
            Dim track = DirectCast(bar.Template.FindName("PART_Track", bar), Track)
            check(bar.ActualWidth = 8 AndAlso track.Thumb IsNot Nothing AndAlso track.IncreaseRepeatButton IsNot Nothing AndAlso track.DecreaseRepeatButton IsNot Nothing, "scroll chrome keeps the 8 DIP hit area and native thumb/page commands")
            check(ReferenceEquals(track.Thumb.Foreground, chrome.Ink), "rendered thumb binds the live chrome ink across the Track template boundary")
            viewer.ScrollToVerticalOffset(300) : layout()
            For Each dark In {False, True}
                RestoreAppearance(New JObject From {{"darkMode", dark}}) : layout()
                Dim offset = viewer.VerticalOffset
                check(chrome.Opacity = 0.5 AndAlso chrome.Ink.Color = PclAnimation.Brush("ColorBrush4").Color, "idle scrollbar uses source half opacity and current ColorBrush4 in dark mode " & dark)
                ControlPointer(bar, True)
                Dim hover = chrome.OpacityTransition
                check(hover.Timeline.Duration.TimeSpan.TotalMilliseconds = 130, "scrollbar hover duration is 130 ms in dark mode " & dark)
                SeekControl(hover, 65)
                IO.File.WriteAllText(IO.Path.Combine(directory, "scroll-hover-observation.json"), New JObject From {{"inner", chrome.Opacity}, {"outer", bar.Opacity}, {"offset", viewer.VerticalOffset}, {"before", offset}, {"clock", hover.CurrentTime?.TotalMilliseconds}, {"owned", chrome.OpacityTransition Is hover}}.ToString())
                check(Math.Abs(chrome.Opacity - 0.7) < 0.00001 AndAlso viewer.VerticalOffset = offset AndAlso bar.Opacity = 1, "scrollbar half hover changes only inner opacity without scrolling in dark mode " & dark)
                SeekControl(hover, 131) : SeekControl(chrome.ColorTransition, 131)
                check(chrome.Opacity = 0.9 AndAlso chrome.Ink.Color = PclAnimation.Brush("ColorBrush3").Color, "hover ends at source 0.9 opacity and current ColorBrush3 in dark mode " & dark)
                ' Controlled WPF state flag: models capture without stealing the user's mouse.
                SetControlFlag(bar, "IsMouseCaptureWithinCache", True)
                bar.RaiseEvent(New MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) With {.RoutedEvent = Mouse.GotMouseCaptureEvent})
                Dim captured = chrome.OpacityTransition
                check(captured.Timeline.Duration.TimeSpan.TotalMilliseconds = 50, "captured scrollbar uses PCL 50 ms duration in dark mode " & dark)
                ControlPointer(bar, False)
                SeekControl(chrome.OpacityTransition, 51) : SeekControl(chrome.ColorTransition, 51)
                check(chrome.Opacity = 1 AndAlso chrome.Ink.Color = PclAnimation.Brush("ColorBrush4").Color, "drag capture has priority over mouse leave in dark mode " & dark)
                SetControlFlag(bar, "IsMouseCaptureWithinCache", False)
                bar.RaiseEvent(New MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) With {.RoutedEvent = Mouse.LostMouseCaptureEvent})
                Dim idle = chrome.OpacityTransition
                check(idle.Timeline.Duration.TimeSpan.TotalMilliseconds = 180, "scrollbar returns over 180 ms in dark mode " & dark)
                captured.Controller.SkipToFill() : layout()
                check(chrome.OpacityTransition Is idle, "retired scrollbar capture cannot finish its successor in dark mode " & dark)
                SeekControl(idle, 181) : SeekControl(chrome.ColorTransition, 181)
                check(chrome.Opacity = 0.5 AndAlso chrome.OpacityTransition Is Nothing AndAlso chrome.ColorTransition Is Nothing, "scrollbar idle has no retained clocks in dark mode " & dark)
                SaveControlVisual(host, IO.Path.Combine(directory, "scrollbar-" & If(dark, "dark", "blue") & ".png"))
            Next
            Dim before = viewer.VerticalOffset
            ScrollBar.PageDownCommand.Execute(Nothing, bar) : layout()
            check(viewer.VerticalOffset > before, "native scrollbar page-down still scrolls the viewer")
            before = viewer.VerticalOffset
            track.Thumb.RaiseEvent(New DragDeltaEventArgs(0, 10) With {.RoutedEvent = Thumb.DragDeltaEvent}) : layout()
            check(viewer.VerticalOffset > before, "native thumb drag delta still reaches the viewer")
            ' Page owns the outer opacity independently of pointer/capture transitions.
            Dim pageClock = DirectCast(New DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(350)).CreateClock(True), AnimationClock)
            bar.ApplyAnimationClock(OpacityProperty, pageClock)
            SeekControl(pageClock, 175)
            ControlPointer(bar, True) : SeekControl(chrome.OpacityTransition, 65)
            check(Math.Abs(bar.Opacity - 0.5) < 0.00001 AndAlso Math.Abs(chrome.Opacity - 0.7) < 0.00001, "page fade and scrollbar hover retain separate property ownership")
            bar.ApplyAnimationClock(OpacityProperty, Nothing) : pageClock.Controller.Remove()
            Dim obsolete = chrome.OpacityTransition
            host.IsHitTestVisible = False : layout()
            check(chrome.Opacity = 0.5 AndAlso chrome.OpacityTransition Is Nothing AndAlso chrome.ColorTransition Is Nothing, "page input gate settles scrollbar chrome without hiding its content")
            host.IsHitTestVisible = True : layout()
            ControlPointer(bar, False)
            chrome.MotionPolicy.EnabledForChecks = False
            obsolete.Controller.SkipToFill() : layout()
            check(chrome.Opacity = 0.5 AndAlso chrome.OpacityTransition Is Nothing, "reduced motion ignores retired scrollbar completions")
            ControlPointer(bar, True)
            check(chrome.Opacity = 0.9 AndAlso chrome.ColorTransition Is Nothing, "reduced motion scrollbar applies hover immediately")
            host.Visibility = Visibility.Collapsed : layout()
            check(chrome.Opacity = 0 AndAlso chrome.OpacityTransition Is Nothing, "hidden scrollbar immediately clears clocks and uses zero inner opacity")
            host.Visibility = Visibility.Visible : layout()
            chrome.MotionPolicy.EnabledForChecks = True
            ControlPointer(bar, False)
            Dim oldChrome = chrome, retired = chrome.OpacityTransition, template = bar.Template
            bar.Template = Nothing : layout()
            bar.Template = template : layout()
            chrome = DirectCast(bar.Template.FindName("Chrome", bar), PclScrollBarChrome)
            retired?.Controller.SkipToFill() : layout()
            check(chrome IsNot oldChrome AndAlso oldChrome.OpacityTransition Is Nothing AndAlso oldChrome.ColorTransition Is Nothing AndAlso chrome.Opacity = 0.5, "replacing the native scroll template releases old clocks and attaches one fresh chrome")
            Dim horizontal As New ScrollViewer With {.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, .VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, .Content = New Border With {.Width = 1400, .Height = 20}}
            host.Child = horizontal : layout()
            Dim horizontalBar = DirectCast(horizontal.Template.FindName("PART_HorizontalScrollBar", horizontal), ScrollBar)
            Dim horizontalTrack = DirectCast(horizontalBar.Template.FindName("PART_Track", horizontalBar), Track)
            check(horizontalBar.ActualHeight = 8 AndAlso horizontalBar.ActualWidth > 200 AndAlso Not horizontalTrack.IsDirectionReversed, "horizontal scrollbar keeps an 8 DIP height and native forward direction")
            ScrollBar.PageRightCommand.Execute(Nothing, horizontalBar) : layout()
            check(horizontal.HorizontalOffset > 0, "native horizontal page-right command survives the shared style")
            before = horizontal.HorizontalOffset
            horizontalTrack.Thumb.RaiseEvent(New DragDeltaEventArgs(10, 0) With {.RoutedEvent = Thumb.DragDeltaEvent}) : layout()
            check(horizontal.HorizontalOffset > before, "native horizontal thumb delta still reaches the viewer")
            source.RootVisual = Nothing : FlushControlLayout()
            check(chrome.OpacityTransition Is Nothing AndAlso chrome.ColorTransition Is Nothing, "unloaded scrollbar chrome leaves no clocks")
        End Using
    End Sub

    Private Shared Function ReferenceRadio(kind As PclRadioMotionKind, origin As Double, milliseconds As Double) As Double
        Dim clamp As Func(Of Double, Double) = Function(t) Math.Max(0, Math.Min(1, t))
        Dim fluent As Func(Of Double, Integer, Double) = Function(t, p) 1 - Math.Pow(1 - clamp(t), p)
        Dim back As Func(Of Double, Double, Double) = Function(t, p) 1 - Math.Pow(1 - clamp(t), 3 - p * 0.5) * Math.Cos(1.5 * Math.PI * clamp(t))
        Select Case kind
            Case PclRadioMotionKind.CheckRing : Return origin + (10 - origin) * fluent(milliseconds / 150, 2) + 8 * back((milliseconds - 90) / 300, 3)
            Case PclRadioMotionKind.CheckDot : Return origin + (9 - origin) * back(milliseconds / 390, 2)
            Case PclRadioMotionKind.UncheckRing : Return origin + (18 - origin) * fluent(milliseconds / 150, 3)
            Case PclRadioMotionKind.UncheckDot : Return origin * (1 - Math.Pow(clamp(milliseconds / 150), 3))
            Case PclRadioMotionKind.PressRing : Return origin + (16.5 - origin) * fluent(milliseconds / 1000, 4)
            Case Else : Return origin + (18 - origin) * fluent(milliseconds / 400, 4)
        End Select
    End Function

    Private Shared Sub VerifyRadioCurves(check As Action(Of Boolean, String))
        For Each kind As PclRadioMotionKind In [Enum].GetValues(GetType(PclRadioMotionKind))
            For Each origin In {0.0, 7.2, 16.7, 18.0}
                Dim animation As New PclRadioAnimation(origin, kind)
                Dim clone = DirectCast(animation.Clone(), PclRadioAnimation) : clone.Freeze()
                Dim valid = clone.IsFrozen
                For Each time In {0.0, 30, 75, 90, 100, 150, 240, 300, 390, 400, 1000}
                    valid = valid AndAlso Math.Abs(clone.Sample(time) - ReferenceRadio(kind, origin, time)) < 0.000001
                Next
                check(valid, "PCL radio " & kind.ToString() & " retains source deltas after cloning from " & origin.ToString(Globalization.CultureInfo.InvariantCulture))
            Next
        Next
    End Sub

    Private Sub VerifyRadioBoxMotion(directory As String, check As Action(Of Boolean, String))
        Dim first As New PclRadioBox With {.Content = "龙猫蓝", .GroupName = "ControlMotionFixture", .Width = 130, .Style = DirectCast(FindResource("PclAppearanceRadio"), Style)}
        Dim second As New PclRadioBox With {.Content = "深蓝", .GroupName = first.GroupName, .Width = 130, .Style = first.Style}
        first.MotionPolicy.EnabledForChecks = True : second.MotionPolicy.EnabledForChecks = True
        first.MotionPolicy.PauseForChecks = True : second.MotionPolicy.PauseForChecks = True
        Dim rows As New ControlLayoutProbe()
        rows.Children.Add(first) : rows.Children.Add(second)
        Dim host As New Border With {.Width = 300, .Height = 110, .Padding = New Thickness(16), .Child = rows}
        host.SetResourceReference(BackgroundProperty, "CardSurfaceBrush")
        Dim layout = ControlLayout(host), frames As New JArray()
        Using source = PreviewSurface.Attach(host, 300, 110)
            layout()
            check(first.Glyph.RingDiameter = 18 AndAlso first.Glyph.DotDiameter = 0 AndAlso first.Glyph.DotOpacity = 0, "unselected radio has source geometry without an initial transition")
            ControlKey(first, Key.Space, True)
            Dim press = first.RingTransition
            check(press.Timeline.Duration.TimeSpan.TotalMilliseconds = 1000 AndAlso Not first.IsChecked.GetValueOrDefault(), "holding Space compresses an unselected radio over PCL 1000 ms without selecting it")
            SeekControl(press, 400)
            check(Math.Abs(first.Glyph.RingDiameter - ReferenceRadio(PclRadioMotionKind.PressRing, 18, 400)) < 0.000001, "live held-radio geometry matches the source strong fluent curve")
            first.RaiseEvent(New KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, first, second) With {.RoutedEvent = Keyboard.LostKeyboardFocusEvent})
            Dim release = first.RingTransition
            check(release.Timeline.Duration.TimeSpan.TotalMilliseconds = 400, "abandoning a held radio returns its outline over PCL 400 ms")
            ControlKey(first, Key.Space, False)
            check(Not first.IsChecked.GetValueOrDefault(), "a Space release after focus loss does not select the abandoned radio")
            first.MotionPolicy.EnabledForChecks = False : first.MotionPolicy.EnabledForChecks = True
            For Each dark In {False, True}
                RestoreAppearance(New JObject From {{"darkMode", dark}})
                first.IsChecked = False : layout()
                first.MotionPolicy.EnabledForChecks = False : first.MotionPolicy.EnabledForChecks = True
                first.IsChecked = True
                Dim ring = first.RingTransition, dot = first.DotTransition
                ring.Controller.Pause() : dot.Controller.Pause()
                first.AlphaTransition.Controller.Pause() : layout()
                check(ring.Timeline.Duration.TimeSpan.TotalMilliseconds = 390 AndAlso dot.Timeline.Duration.TimeSpan.TotalMilliseconds = 390, "radio checked ring and dot use the complete 390 ms PCL timeline in dark mode " & dark)
                Dim measures = rows.Measures, arrangements = rows.Arrangements
                For Each time In {30.0, 90, 150, 240, 360}
                    SeekControl(ring, time) : SeekControl(dot, time) : layout()
                    If first.InkTransition IsNot Nothing Then SeekControl(first.InkTransition, time)
                    check(Math.Abs(first.Glyph.RingDiameter - ReferenceRadio(PclRadioMotionKind.CheckRing, 18, time)) < 0.000001 AndAlso
                          Math.Abs(first.Glyph.DotDiameter - ReferenceRadio(PclRadioMotionKind.CheckDot, 0, time)) < 0.000001, "live radio geometry matches source near " & time & " ms in dark mode " & dark)
                    frames.Add(New JObject From {{"dark", dark}, {"ms", time}, {"ring", first.Glyph.RingDiameter}, {"dot", first.Glyph.DotDiameter}})
                    SaveControlVisual(host, IO.Path.Combine(directory, "radio-" & If(dark, "dark", "blue") & "-" & time.ToString("000") & ".png"))
                Next
                check(rows.Measures = measures AndAlso rows.Arrangements = arrangements, "radio animation does not remeasure or rearrange its parent in dark mode " & dark)
                Dim start = first.Glyph.DotDiameter
                first.IsChecked = False
                Dim outgoing = first.DotTransition
                check(Math.Abs(first.Glyph.DotDiameter - start) < 0.000001, "unchecking during rebound preserves the displayed dot origin in dark mode " & dark)
                SeekControl(outgoing, 75)
                check(Math.Abs(first.Glyph.DotDiameter - start * 0.875) < 0.000001, "uncheck dot follows the source cubic ease-in in dark mode " & dark)
                Dim reversed = first.Glyph.DotDiameter
                first.IsChecked = True
                Dim incoming = first.DotTransition
                check(Math.Abs(first.Glyph.DotDiameter - reversed) < 0.000001, "rapid radio reversal preserves the new visible origin in dark mode " & dark)
                outgoing.Controller.SkipToFill() : ring.Controller.SkipToFill() : layout()
                check(first.DotTransition Is incoming, "old radio completions cannot clear the replacement in dark mode " & dark)
                SeekControl(first.RingTransition, 391) : SeekControl(first.DotTransition, 391) : SeekControl(first.AlphaTransition, 166)
                check(first.RingTransition Is Nothing AndAlso first.DotTransition Is Nothing AndAlso first.AlphaTransition Is Nothing AndAlso first.Glyph.RingDiameter = 18 AndAlso first.Glyph.DotDiameter = 9 AndAlso first.Glyph.DotOpacity = 1, "settled checked radio releases geometry clocks in dark mode " & dark)
            Next
            Dim peer = UIElementAutomationPeer.CreatePeerForElement(second)
            DirectCast(peer.GetPattern(PatternInterface.SelectionItem), ISelectionItemProvider).Select()
            check(second.IsChecked.GetValueOrDefault() AndAlso Not first.IsChecked.GetValueOrDefault(), "native automation selection retains radio-group exclusivity")
            rows.IsHitTestVisible = False
            Dim firstProvider = DirectCast(UIElementAutomationPeer.CreatePeerForElement(first).GetPattern(PatternInterface.SelectionItem), ISelectionItemProvider)
            firstProvider.Select()
            check(second.IsChecked.GetValueOrDefault() AndAlso Not first.IsChecked.GetValueOrDefault(), "radio automation selection obeys the inherited input gate")
            rows.IsHitTestVisible = True
            firstProvider.Select()
            check(first.IsChecked.GetValueOrDefault() AndAlso Not second.IsChecked.GetValueOrDefault(), "radio automation selection resumes with native grouping after the gate opens")
            For Each boundary In {"gate", "hide", "disable", "detach"}
                first.IsChecked = True
                Dim old = first.RingTransition
                ControlKey(second, Key.Space, True)
                Dim beforeGate = New JObject From {{"first", first.IsChecked}, {"second", second.IsChecked}, {"pressed", second.IsPressed}}
                Select Case boundary
                    Case "gate" : rows.IsHitTestVisible = False
                    Case "hide" : host.Visibility = Visibility.Collapsed
                    Case "disable" : rows.IsEnabled = False
                    Case Else : rows.Children.Remove(second) : rows.Children.Remove(first)
                End Select
                layout()
                check(first.RingTransition Is Nothing AndAlso first.DotTransition Is Nothing AndAlso first.InkTransition Is Nothing AndAlso second.RingTransition Is Nothing, "radio " & boundary & " settles geometry and color clocks")
                rows.IsHitTestVisible = True : rows.IsEnabled = True : host.Visibility = Visibility.Visible
                If first.Parent Is Nothing Then rows.Children.Add(first) : rows.Children.Add(second)
                layout()
                old?.Controller.SkipToFill() : ControlKey(second, Key.Space, False) : layout()
                IO.File.WriteAllText(IO.Path.Combine(directory, "radio-gate-" & boundary & ".json"), New JObject From {{"before", beforeGate}, {"first", first.IsChecked}, {"second", second.IsChecked}, {"pressed", second.IsPressed}}.ToString())
                check(first.IsChecked.GetValueOrDefault() AndAlso Not second.IsChecked.GetValueOrDefault() AndAlso Not second.IsPressed, "radio " & boundary & " rejects the old Space release after recovery")
                PressControlKey(second, Key.Space) : layout()
                check(second.IsChecked.GetValueOrDefault() AndAlso Not first.IsChecked.GetValueOrDefault(), "fresh native keyboard selection works after radio " & boundary)
            Next
            first.IsChecked = True
            Dim oldGlyph = first.Glyph, oldClock = first.RingTransition, template = first.Template
            first.Template = Nothing : layout()
            first.Template = template : layout()
            oldClock?.Controller.SkipToFill() : layout()
            check(first.Glyph IsNot oldGlyph AndAlso first.RingTransition Is Nothing AndAlso first.Glyph.DotDiameter = 9 AndAlso Not oldGlyph.HasAnimatedProperties, "radio template replacement detaches old clocks and reconstructs the current checked state")
            first.IsChecked = False
            Dim colorClock = first.InkTransition
            RestoreAppearance(New JObject From {{"darkMode", True}, {"theme", "deepblue"}})
            Dim newInk = DirectCast(first.Glyph.GetValue(PclRadioGlyph.StrokeProperty), SolidColorBrush).Color
            colorClock?.Controller.SkipToFill() : layout()
            check(first.InkTransition Is Nothing AndAlso DirectCast(first.Glyph.GetValue(PclRadioGlyph.StrokeProperty), SolidColorBrush).Color = newInk, "an old radio ink completion cannot restore the previous theme color")
            For Each control In {first, second}
                control.MotionPolicy.EnabledForChecks = False
            Next
            first.IsChecked = True : second.IsChecked = True : layout()
            check(first.Glyph.DotDiameter = 0 AndAlso second.Glyph.DotDiameter = 9 AndAlso first.RingTransition Is Nothing AndAlso second.RingTransition Is Nothing, "reduced motion radios apply mutually exclusive final geometry immediately")
            first.MotionPolicy.EnabledForChecks = True
            first.IsChecked = True
            oldClock = first.RingTransition
            first.MotionPolicy.EnabledForChecks = False
            oldClock.Controller.SkipToFill() : layout()
            check(first.RingTransition Is Nothing AndAlso first.Glyph.DotDiameter = 9, "turning reduced motion on during radio selection clears the old clock without losing selection")
            IO.File.WriteAllText(IO.Path.Combine(directory, "radio-source-samples.json"), frames.ToString())
            source.RootVisual = Nothing : FlushControlLayout()
            check(first.RingTransition Is Nothing AndAlso second.RingTransition Is Nothing AndAlso first.InkTransition Is Nothing, "unloaded radio controls leave no live geometry or ink clocks")
        End Using
    End Sub

    Private Sub VerifyAppearanceRadioIntegration(directory As String, check As Action(Of Boolean, String))
        Dim fixture As New MainWindow(True), content = DirectCast(fixture.Content, FrameworkElement)
        Dim host As New Border With {.Width = 850, .Height = 600, .Child = Nothing}, layout = ControlLayout(host)
        fixture.Content = Nothing : host.Child = content
        Using source = PreviewSurface.Attach(host, 850, 600)
            fixture.SwitchMainPage(True) : layout()
            Dim choices = fixture.AppearanceThemes.Children.OfType(Of PclRadioBox)().Where(Function(item) item.Visibility = Visibility.Visible).ToArray()
            For Each choice In choices
                choice.MotionPolicy.EnabledForChecks = True
            Next
            Dim blue = choices.Single(Function(item) CStr(item.Tag) = "blue"), deep = choices.Single(Function(item) CStr(item.Tag) = "deepblue")
            Dim prior = fixture.BuildSettings(0)
            fixture.SelectAppearanceTheme("deepblue")
            check(deep.RingTransition IsNot Nothing AndAlso deep.IsChecked.GetValueOrDefault() AndAlso Not blue.IsChecked.GetValueOrDefault(), "real appearance selection starts ring motion even when Checked synchronously applies the palette")
            Dim ring = deep.RingTransition
            SeekControl(ring, 100)
            fixture.DarkModeBox.IsChecked = True
            check(deep.RingTransition Is ring AndAlso DirectCast(deep.Glyph.GetValue(PclRadioGlyph.StrokeProperty), SolidColorBrush).Color = PclAnimation.Brush("ColorBrush2").Color AndAlso deep.InkTransition Is Nothing, "changing dark mode replaces ink immediately without restarting the active radio geometry")
            For index = 0 To 39
                fixture.SelectAppearanceTheme(If(index Mod 2 = 0, "blue", "deepblue"))
            Next
            layout()
            check(choices.Count(Function(item) item.IsChecked.GetValueOrDefault()) = 1 AndAlso fixture._appearance.Theme = "deepblue", "forty rapid real theme changes retain one checked choice and the final saved preference")
            Dim after = fixture.BuildSettings(0)
            prior.Remove("appearance") : after.Remove("appearance")
            check(JToken.DeepEquals(prior, after), "theme animation preserves download preferences and unrelated settings")
            For Each factor In {1.0, 1.5, 2.0}
                host.LayoutTransform = New ScaleTransform(factor, factor)
                layout()
                check(choices.All(Function(item) item.Glyph.ActualWidth = 20 AndAlso item.Glyph.ActualHeight = 20), "radio glyph bounds remain stable at synthetic layout scale " & factor.ToString(Globalization.CultureInfo.InvariantCulture))
            Next
            host.LayoutTransform = Transform.Identity : layout()
            If Not PumpMotionUntil(Function() choices.All(Function(item) item.RingTransition Is Nothing AndAlso item.DotTransition Is Nothing AndAlso item.InkTransition Is Nothing)) Then Throw New InvalidOperationException("real appearance controls did not settle")
            SaveControlVisual(host, IO.Path.Combine(directory, "appearance-controls-dark.png"))
            source.RootVisual = Nothing : FlushControlLayout()
            check(choices.All(Function(item) item.RingTransition Is Nothing AndAlso item.InkTransition Is Nothing), "detaching the real appearance page clears all local control clocks")
        End Using
    End Sub

    Private NotInheritable Class ControlLayoutProbe
        Inherits StackPanel
        Friend Property Measures As Integer
        Friend Property Arrangements As Integer
        Protected Overrides Function MeasureOverride(available As Size) As Size
            Measures += 1
            Return MyBase.MeasureOverride(available)
        End Function
        Protected Overrides Function ArrangeOverride(final As Size) As Size
            Arrangements += 1
            Return MyBase.ArrangeOverride(final)
        End Function
    End Class
End Class
