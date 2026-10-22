Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Shared Sub VerifyPressMotion(directory As String, check As Action(Of Boolean, String))
        VerifyPressCurves(check)
        Dim cardPress = ObserveCardPressLifecycle(directory)
        Dim button As New PclButton With {.Text = "下载", .Width = 120, .Height = 35}
        Dim icon As New PclIconButton With {.Logo = "M 0,8 L 8,0 L 8,5 L 16,5 L 16,11 L 8,11 L 8,16 Z", .Width = 32, .Height = 32}
        icon.MotionPolicy.EnabledForChecks = True
        Dim rows As New StackPanel()
        rows.Children.Add(button) : rows.Children.Add(icon)
        Dim host As New Border With {.Width = 300, .Height = 160, .Child = rows}
        Dim layout As Action = Sub()
                                   host.Measure(New Size(300, 160)) : host.Arrange(New Rect(0, 0, 300, 160))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        Dim actions = 0
        AddHandler button.Click, Sub() actions += 1
        AddHandler icon.Click, Sub() actions += 1
        Dim pressed As Func(Of Object, Boolean) = Function(control) CBool(control.GetType().GetField("_down", Reflection.BindingFlags.NonPublic Or Reflection.BindingFlags.Instance).GetValue(control))
        Dim observations As New JObject()
        Using source = PreviewSurface.Attach(host, 300, 160)
            layout()
            PointerEvent(button, Mouse.MouseDownEvent) : PointerEvent(icon, Mouse.MouseDownEvent)
            If Not PumpMotionUntil(Function() button.ScaleTransition.CurrentState = ClockState.Filling) Then Throw New InvalidOperationException("press fixture dispatcher did not finish the held animation")
            Dim held = DirectCast(button.PanFore.RenderTransform, ScaleTransform).ScaleX
            observations("heldButtonScale") = held
            rows.IsHitTestVisible = False : layout()
            Dim gated = Not pressed(button) AndAlso Not pressed(icon)
            Dim gateScale = button.ScaleTransition Is Nothing AndAlso icon.ScaleTransition Is Nothing AndAlso
                Not DirectCast(button.PanFore.RenderTransform, ScaleTransform).HasAnimatedProperties AndAlso
                Not DirectCast(icon.PanBack.RenderTransform, ScaleTransform).HasAnimatedProperties
            observations("gateCancelledPress") = gated
            rows.IsHitTestVisible = True : layout()
            PointerEvent(button, Mouse.MouseUpEvent) : PointerEvent(icon, Mouse.MouseUpEvent)
            observations("gateActions") = actions
            Dim gateActions = actions
            actions = 0
            PointerEvent(button, Mouse.MouseDownEvent) : PointerEvent(icon, Mouse.MouseDownEvent)
            host.Visibility = Visibility.Collapsed : layout()
            Dim hidden = Not pressed(button) AndAlso Not pressed(icon)
            observations("hideCancelledPress") = hidden
            host.Visibility = Visibility.Visible : layout()
            PointerEvent(button, Mouse.MouseUpEvent) : PointerEvent(icon, Mouse.MouseUpEvent)
            observations("hideActions") = actions
            Dim hideActions = actions
            actions = 0
            PointerEvent(button, Mouse.MouseDownEvent) : PointerEvent(icon, Mouse.MouseDownEvent)
            rows.Children.Clear() : layout()
            Dim detached = Not pressed(button) AndAlso Not pressed(icon)
            observations("detachCancelledPress") = detached
            rows.Children.Add(button) : rows.Children.Add(icon) : layout()
            PointerEvent(button, Mouse.MouseUpEvent) : PointerEvent(icon, Mouse.MouseUpEvent)
            observations("detachActions") = actions
            IO.File.WriteAllText(IO.Path.Combine(directory, "press-lifecycle.json"), observations.ToString())
            check(Math.Abs(held - 0.945) < 0.00001, "holding an action button includes PCL's parallel seven-hundred-millisecond micro-shrink")
            check(gated AndAlso gateActions = 0 AndAlso CBool(cardPress("gate")), "page input gating cancels pressed buttons icons and card headers before restoring hit testing")
            check(gateScale, "page input gating removes both owned scale clocks instead of retaining an invisible animation")
            check(hidden AndAlso hideActions = 0 AndAlso CBool(cardPress("hidden")), "hidden pages and card headers cannot retain a pressed action until they reappear")
            check(detached AndAlso actions = 0 AndAlso CBool(cardPress("detached")), "detaching and reattaching action controls cancels old presses while fresh clicks remain available")
            VerifyLivePress(button, icon, host, rows, layout, check, directory)
        End Using
    End Sub

    Private Shared Sub VerifyPressCurves(check As Action(Of Boolean, String))
        For Each kind As PclPressKind In [Enum].GetValues(GetType(PclPressKind))
            For Each start In {1.0, 0.873}
                Dim plan As New PclPressAnimation(start, kind)
                Dim clone = DirectCast(plan.Clone(), PclPressAnimation) : clone.Freeze()
                Dim matches = clone.IsFrozen AndAlso clone.Duration = plan.Duration
                For Each time In {0.0, 5, 10, 20, 40, 80, 120, 175, 250, 310, 400, 700, 800}
                    matches = matches AndAlso Math.Abs(clone.Sample(time) - ReferencePressScale(kind, start, time)) < 0.0000001
                Next
                check(matches, "cloned frozen " & kind.ToString() & " retains PCL's parallel curves from origin " & start.ToString(Globalization.CultureInfo.InvariantCulture))
            Next
        Next
    End Sub

    Private Shared Function ReferencePressScale(kind As PclPressKind, start As Double, time As Double) As Double
        ' Independent transcription of 5ef81e4 AaScaleTransform deltas and ModAnimation easing.
        Dim fluent As Func(Of Double, Integer, Double) = Function(t, power) 1 - Math.Pow(1 - Math.Max(0, Math.Min(1, t)), power)
        Select Case kind
            Case PclPressKind.ButtonDown : Return start + (0.955 - start) * fluent(time / 80, 5) - 0.01 * fluent(time / 700, 3)
            Case PclPressKind.ButtonUp : Return start + (1 - start) * fluent((time - 10) / 300, 3)
            Case PclPressKind.ButtonLeave : Return start + (1 - start) * fluent(time / 800, 4)
            Case PclPressKind.IconDown : Return start + (0.8 - start) * fluent(time / 400, 4)
            Case PclPressKind.IconUp
                Dim t = Math.Max(0, Math.Min(1, time / 250))
                Return start + (1.05 - start) * (1 - Math.Pow(1 - t, 2) * Math.Cos(1.5 * Math.PI * t)) - 0.05 * fluent(time / 250, 4)
            Case Else : Return start + (1 - start) * fluent(time / 250, 3)
        End Select
    End Function

    Private Shared Sub VerifyLivePress(button As PclButton, icon As PclIconButton, host As Border, rows As Panel, layout As Action, check As Action(Of Boolean, String), directory As String)
        Dim sample As Action(Of AnimationClock, ScaleTransform, PclPressKind, Double, Double) =
            Sub(clock, scale, kind, start, time)
                clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(time), TimeSeekOrigin.BeginTime) : layout()
                Dim actualTime = clock.CurrentTime.Value.TotalMilliseconds
                Dim expected = ReferencePressScale(kind, start, actualTime)
                check(Math.Abs(scale.ScaleX - expected) < 0.00001 AndAlso Math.Abs(scale.ScaleY - expected) < 0.00001,
                    "live " & kind.ToString() & " matches both PCL scale axes near " & time & " ms")
            End Sub
        Dim scaleOf As Func(Of FrameworkElement, ScaleTransform) = Function(control) If(TypeOf control Is PclButton,
            DirectCast(DirectCast(control, PclButton).PanFore.RenderTransform, ScaleTransform),
            DirectCast(DirectCast(control, PclIconButton).PanBack.RenderTransform, ScaleTransform))
        For Each control As FrameworkElement In {DirectCast(button, FrameworkElement), icon}
            Dim isIcon = TypeOf control Is PclIconButton
            Dim clockOf As Func(Of AnimationClock) = Function() If(isIcon, icon.ScaleTransition, button.ScaleTransition)
            Dim scale = scaleOf(control)
            Dim downKind = If(isIcon, PclPressKind.IconDown, PclPressKind.ButtonDown)
            Dim upKind = If(isIcon, PclPressKind.IconUp, PclPressKind.ButtonUp)
            Dim leaveKind = If(isIcon, PclPressKind.IconLeave, PclPressKind.ButtonLeave)
            PointerEvent(control, Mouse.MouseDownEvent)
            Dim down = clockOf() : down.Controller.Pause() : layout()
            check(down.Timeline.Duration.TimeSpan.TotalMilliseconds = If(isIcon, 400, 700), "source press duration for " & downKind.ToString())
            sample(down, scale, downKind, 1, 40)
            sample(down, scale, downKind, 1, 175)
            Dim releasedFrom = scale.ScaleX
            PointerEvent(control, Mouse.MouseUpEvent)
            Dim up = clockOf() : up.Controller.Pause()
            check(Math.Abs(scale.ScaleX - releasedFrom) < 0.00001, "releasing " & downKind.ToString() & " preserves its displayed origin before the first clock tick")
            layout()
            check(up.Timeline.Duration.TimeSpan.TotalMilliseconds = If(isIcon, 250, 310), "source release duration for " & upKind.ToString())
            sample(up, scale, upKind, releasedFrom, 40)
            sample(up, scale, upKind, releasedFrom, 175)
            down.Controller.SkipToFill() : layout()
            check(clockOf() Is up, "superseded press clocks cannot clear a newer " & upKind.ToString())
            up.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(900), TimeSeekOrigin.BeginTime) : layout()
            check(clockOf() Is Nothing AndAlso scale.ScaleX = 1 AndAlso scale.ScaleY = 1 AndAlso Not scale.HasAnimatedProperties,
                "completed " & upKind.ToString() & " releases its clock and returns to the exact base scale")

            PointerEvent(control, Mouse.MouseDownEvent)
            down = clockOf() : down.Controller.Pause() : layout()
            sample(down, scale, downKind, 1, 80)
            Dim leftFrom = scale.ScaleX
            LeavePointer(control)
            Dim leave = clockOf() : leave.Controller.Pause() : layout()
            check(leave.Timeline.Duration.TimeSpan.TotalMilliseconds = If(isIcon, 250, 800), "source cancellation duration for " & leaveKind.ToString())
            sample(leave, scale, leaveKind, leftFrom, 120)
            Dim repressedFrom = scale.ScaleX
            PointerEvent(control, Mouse.MouseDownEvent)
            down = clockOf() : down.Controller.Pause()
            check(Math.Abs(scale.ScaleX - repressedFrom) < 0.00001, "repressing during " & leaveKind.ToString() & " retains its visible scale")
            layout() : sample(down, scale, downKind, repressedFrom, 40)
            leave.Controller.SkipToFill() : layout()
            check(clockOf() Is down, "an old leave completion cannot clear a replacement " & downKind.ToString())
            rows.IsEnabled = False : layout()
            check(clockOf() Is Nothing AndAlso scale.ScaleX = 1 AndAlso Not scale.HasAnimatedProperties,
                "ancestor disable cancels and releases the active " & downKind.ToString())
            rows.IsEnabled = True : layout()
            Dim clicks = 0
            Dim count As RoutedEventHandler = Sub() clicks += 1
            If isIcon Then AddHandler icon.Click, count Else AddHandler button.Click, count
            PointerEvent(control, Mouse.MouseUpEvent)
            check(clicks = 0 AndAlso clockOf() Is Nothing, "reenabling " & downKind.ToString() & " cannot replay an unmatched release")
            PointerEvent(control, Mouse.MouseDownEvent) : PointerEvent(control, Mouse.MouseUpEvent)
            check(clicks = 1 AndAlso clockOf() IsNot Nothing, "a new complete click still works after cancelling " & downKind.ToString())
            If isIcon Then RemoveHandler icon.Click, count Else RemoveHandler button.Click, count
            host.Visibility = Visibility.Collapsed : layout()
            check(clockOf() Is Nothing AndAlso scale.ScaleX = 1 AndAlso Not scale.HasAnimatedProperties,
                "hiding a releasing " & upKind.ToString() & " removes its pending completion")
            host.Visibility = Visibility.Visible : layout()
        Next

        PointerEvent(button, Mouse.MouseDownEvent) : PointerEvent(icon, Mouse.MouseDownEvent)
        Dim buttonClock = button.ScaleTransition, iconClock = icon.ScaleTransition
        buttonClock.Controller.Pause() : iconClock.Controller.Pause() : layout()
        buttonClock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(701), TimeSeekOrigin.BeginTime)
        iconClock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(401), TimeSeekOrigin.BeginTime) : layout()
        PointerEvent(button, Mouse.MouseUpEvent) : PointerEvent(icon, Mouse.MouseUpEvent)
        buttonClock = button.ScaleTransition : iconClock = icon.ScaleTransition
        buttonClock.Controller.Pause() : iconClock.Controller.Pause() : layout()
        Dim frames = IO.Path.Combine(directory, "press-motion-frames") : IO.Directory.CreateDirectory(frames)
        For index = 0 To 12
            buttonClock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(index * 25), TimeSeekOrigin.BeginTime)
            If index <= 9 Then iconClock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(index * 25), TimeSeekOrigin.BeginTime)
            If index = 10 Then iconClock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(251), TimeSeekOrigin.BeginTime)
            layout() : SaveControlVisual(host, IO.Path.Combine(frames, index.ToString("D2") & ".png"))
        Next
        buttonClock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(311), TimeSeekOrigin.BeginTime) : layout()
        check(button.ScaleTransition Is Nothing AndAlso icon.ScaleTransition Is Nothing, "parallel button and icon releases finish independently in the recorded frames")
    End Sub
End Class
