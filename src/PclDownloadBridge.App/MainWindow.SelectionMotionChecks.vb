Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Shared Sub VerifySelectionMotion(directory As String, check As Action(Of Boolean, String))
        Dim row As New PclListItem With {.Title = "版本选择", .Height = 42}
        Dim tab As New PclRadioButton With {.Text = "设置", .Logo = "M 0,0 L 12,0 L 12,12 L 0,12 Z"}
        Dim items As New StackPanel()
        items.Children.Add(tab) : items.Children.Add(row)
        Dim host As New Border With {.Width = 400, .Height = 180, .Child = items}
        Dim layout As Action = Sub()
                                   host.Measure(New Size(400, 180)) : host.Arrange(New Rect(0, 0, 400, 180))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        Dim down As Func(Of Object, Boolean) = Function(control) CBool(control.GetType().GetField("_down", Reflection.BindingFlags.NonPublic Or Reflection.BindingFlags.Instance).GetValue(control))
        Dim rowActions = 0, tabActions = 0
        AddHandler row.Click, Sub() rowActions += 1
        AddHandler tab.Click, Sub() tabActions += 1
        Dim data As New JObject()
        Using source = PreviewSurface.Attach(host, 400, 180)
            layout()
            For Each interruption In {"input-gate", "hidden", "detached"}
                rowActions = 0 : tabActions = 0
                PointerEvent(row, Mouse.MouseDownEvent) : PointerEvent(tab, Mouse.MouseDownEvent)
                Select Case interruption
                    Case "input-gate" : items.IsHitTestVisible = False : layout()
                    Case "hidden" : host.Visibility = Visibility.Collapsed : layout()
                    Case Else : items.Children.Clear() : layout()
                End Select
                Dim cleared = Not down(row) AndAlso Not down(tab)
                Select Case interruption
                    Case "input-gate" : items.IsHitTestVisible = True : layout()
                    Case "hidden" : host.Visibility = Visibility.Visible : layout()
                    Case Else : items.Children.Add(tab) : items.Children.Add(row) : layout()
                End Select
                PointerEvent(row, Mouse.MouseUpEvent) : PointerEvent(tab, Mouse.MouseUpEvent)
                data(interruption) = New JObject From {{"pressCleared", cleared}, {"rowActions", rowActions}, {"tabActions", tabActions}}
            Next
            PointerEvent(row, Mouse.MouseDownEvent) : layout()
            Dim background = row.Children.OfType(Of Border)().First()
            Dim brush = background.Background
            For index = 1 To 500
                row.Selected = row.Selected
            Next
            data("sameRowStateRetainsBrush") = background.Background Is brush
            LeavePointer(row) : layout()
            tabActions = 0
            PointerEvent(tab, Mouse.MouseDownEvent)
            tab.Checked = tab.Checked
            PointerEvent(tab, Mouse.MouseUpEvent)
            data("sameTabStateActions") = tabActions
            IO.File.WriteAllText(IO.Path.Combine(directory, "selection-lifecycle.json"), data.ToString())
            For Each interruption In {"input-gate", "hidden", "detached"}
                Dim sample = data(interruption)
                check(CBool(sample("pressCleared")) AndAlso CInt(sample("rowActions")) = 0 AndAlso CInt(sample("tabActions")) = 0,
                    "selection controls discard their old press across " & interruption)
            Next
            check(CBool(data("sameRowStateRetainsBrush")), "five hundred unchanged row selections do not replace the hover brush or restart its motion")
            check(CInt(data("sameTabStateActions")) = 1, "writing the same top-tab selection preserves an in-progress user press")
            VerifyRowHoverTimelines(row, tab, items, host, layout, check, directory)
        End Using
    End Sub

    Private Shared Sub SetSelectionHover(control As UIElement, over As Boolean)
        ' Only the hidden fixture's hover flag changes; the native desktop pointer is untouched.
        Dim setter = GetType(UIElement).GetMethod("WriteFlag", Reflection.BindingFlags.Instance Or Reflection.BindingFlags.NonPublic)
        Dim flag = [Enum].Parse(setter.GetParameters()(0).ParameterType, "IsMouseOverCache")
        setter.Invoke(control, New Object() {flag, over})
        control.RaiseEvent(New MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) With {.RoutedEvent = If(over, Mouse.MouseEnterEvent, Mouse.MouseLeaveEvent)})
    End Sub

    Private Shared Sub VerifyRowHoverTimelines(row As PclListItem, tab As PclRadioButton, items As Panel, host As Border, layout As Action, check As Action(Of Boolean, String), directory As String)
        Dim background = row.Children.OfType(Of Border)().First()
        Dim back = DirectCast(background.RenderTransform, ScaleTransform), whole = DirectCast(row.RenderTransform, ScaleTransform)
        Dim fluent As Func(Of Double, Double) = Function(value) 1 - Math.Pow(1 - Math.Max(0, Math.Min(1, value)), 3)
        Dim seek As Action(Of ClockGroup, Double) = Sub(clock, sampleTime)
                                                       clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(sampleTime), TimeSeekOrigin.BeginTime) : layout()
                                                   End Sub
        SetSelectionHover(row, False)
        items.IsHitTestVisible = False : items.IsHitTestVisible = True : layout()
        SetSelectionHover(row, True)
        Dim entry = row.HoverTransition : entry.Controller.Pause() : layout()
        Dim plan = DirectCast(entry.Timeline, ParallelTimeline)
        check(plan.Children(0).Duration.TimeSpan.TotalMilliseconds = 120 AndAlso
            DirectCast(DirectCast(plan.Children(0), DoubleAnimation).EasingFunction, PowerEase).Power = 3 AndAlso
            plan.Children(1).Duration.TimeSpan.TotalMilliseconds = 192 AndAlso plan.Children(2).Duration.TimeSpan.TotalMilliseconds = 144,
            "list hover uses PCL's eased 120ms fade, 192ms background scale and 144ms row return")
        seek(entry, 60)
        Dim time = entry.CurrentTime.Value.TotalMilliseconds
        check(Math.Abs(background.Opacity - fluent(time / 120)) < 0.00001 AndAlso
            Math.Abs(back.ScaleX - (0.75 + 0.25 * fluent(time / 192))) < 0.00001 AndAlso back.ScaleX = back.ScaleY,
            "live list hover opacity and both background axes follow the source cubic curve")
        Dim retained = background.Background
        For index = 1 To 500
            row.Selected = row.Selected
        Next
        check(row.HoverTransition Is entry AndAlso background.Background Is retained,
            "unchanged list state preserves the same live clock and brush across five hundred updates")
        row.Selected = True
        check(row.HoverTransition Is entry AndAlso row.Selected AndAlso row.SelectionTransition IsNot Nothing,
            "changing the selected marker and ink starts its own transition without restarting hover")
        row.Selected = False
        Dim initialBack = back.ScaleX, initialOpacity = background.Opacity
        PointerEvent(row, Mouse.MouseDownEvent)
        Dim pressed = row.HoverTransition : pressed.Controller.Pause()
        check(back.ScaleX = initialBack AndAlso background.Opacity = initialOpacity,
            "pressing a half-entered list row preserves its displayed geometry before WPF's next tick")
        layout() : seek(pressed, 60)
        time = pressed.CurrentTime.Value.TotalMilliseconds
        check(Math.Abs(whole.ScaleX - (1 - 0.02 * fluent(time / 108))) < 0.00001 AndAlso whole.ScaleX = whole.ScaleY,
            "a pressed list row reaches 0.98 using the source 108ms cubic curve")
        Dim releasedFrom = whole.ScaleX
        PointerEvent(row, Mouse.MouseUpEvent)
        Dim released = row.HoverTransition : released.Controller.Pause() : layout() : seek(released, 60)
        time = released.CurrentTime.Value.TotalMilliseconds
        check(Math.Abs(whole.ScaleX - (releasedFrom + (1 - releasedFrom) * fluent(time / 144))) < 0.00001,
            "releasing a hovered list row returns from its displayed scale over 144ms")
        initialBack = back.ScaleX : initialOpacity = background.Opacity
        Dim initialWhole = whole.ScaleX
        SetSelectionHover(row, False)
        Dim exitClock = row.HoverTransition : exitClock.Controller.Pause() : layout()
        plan = DirectCast(exitClock.Timeline, ParallelTimeline)
        check(plan.Children(0).Duration.TimeSpan.TotalMilliseconds = 180 AndAlso DirectCast(plan.Children(0), DoubleAnimation).EasingFunction Is Nothing AndAlso
            plan.Children(2).Duration.TimeSpan.TotalMilliseconds = 540 AndAlso plan.Children(1).Duration.TimeSpan.TotalMilliseconds = 541,
            "list exit keeps the 180ms linear fade, 540ms row return and deferred one-millisecond background reset")
        seek(exitClock, 90)
        time = exitClock.CurrentTime.Value.TotalMilliseconds
        check(Math.Abs(background.Opacity - initialOpacity * Math.Max(0, 1 - time / 180)) < 0.00001 AndAlso
            Math.Abs(back.ScaleX - (initialBack + (0.996 - initialBack) * fluent(time / 180))) < 0.00001 AndAlso
            Math.Abs(whole.ScaleX - (initialWhole + (1 - initialWhole) * fluent(time / 540))) < 0.00001,
            "list exit keeps its background near full size during the fade instead of shrinking it visibly")
        Dim enteredFrom = background.Opacity, reenteredBack = back.ScaleX
        SetSelectionHover(row, True)
        entry = row.HoverTransition : entry.Controller.Pause()
        check(background.Opacity = enteredFrom AndAlso back.ScaleX = reenteredBack,
            "rapid reentry continues from the displayed exit opacity and background scale")
        layout() : seek(entry, 60)
        time = entry.CurrentTime.Value.TotalMilliseconds
        check(Math.Abs(background.Opacity - (enteredFrom + (1 - enteredFrom) * fluent(time / 120))) < 0.00001,
            "reentered row opacity uses the remaining distance with the original source ease")
        exitClock.Controller.SkipToFill() : layout()
        check(row.HoverTransition Is entry, "completion from a replaced exit cannot reset the new row hover")
        seek(entry, 193)
        check(row.HoverTransition Is Nothing AndAlso background.Opacity = 1 AndAlso back.ScaleX = 1 AndAlso whole.ScaleX = 1 AndAlso
            Not background.HasAnimatedProperties AndAlso Not back.HasAnimatedProperties AndAlso Not whole.HasAnimatedProperties AndAlso
            Not DirectCast(background.Background, SolidColorBrush).HasAnimatedProperties,
            "settled row hover releases opacity, color and both scale clocks at exact final values")

        Dim frames = IO.Path.Combine(directory, "row-hover-frames") : IO.Directory.CreateDirectory(frames)
        SetSelectionHover(row, False)
        exitClock = row.HoverTransition : exitClock.Controller.Pause() : layout()
        For index = 0 To 12
            seek(exitClock, index * 40)
            SaveControlVisual(host, IO.Path.Combine(frames, index.ToString("D2") & ".png"))
        Next
        check(background.Opacity = 0 AndAlso Math.Abs(back.ScaleX - 0.996) < 0.00001,
            "an invisible exiting background stays at 0.996 until all preceding source animations finish")
        seek(exitClock, 542)
        check(row.HoverTransition Is Nothing AndAlso background.Opacity = 0 AndAlso back.ScaleX = 0.75 AndAlso whole.ScaleX = 1,
            "the deferred background reset finishes at 0.75 without leaving held clocks")

        For Each interruption In {"input-gate", "hidden", "detached", "disabled"}
            SetSelectionHover(row, True) : PointerEvent(row, Mouse.MouseDownEvent) : PointerEvent(tab, Mouse.MouseDownEvent) : layout()
            Select Case interruption
                Case "input-gate" : items.IsHitTestVisible = False : layout()
                Case "hidden" : host.Visibility = Visibility.Collapsed : layout()
                Case "disabled" : items.IsEnabled = False : layout()
                Case Else : items.Children.Clear() : layout()
            End Select
            check(row.HoverTransition Is Nothing AndAlso background.Opacity = 0 AndAlso back.ScaleX = 0.75 AndAlso whole.ScaleX = 1 AndAlso
                Not back.HasAnimatedProperties AndAlso Not whole.HasAnimatedProperties AndAlso Not background.HasAnimatedProperties,
                "list " & interruption & " immediately releases motion and restores inert geometry")
            check(Not DirectCast(tab.Background, SolidColorBrush).HasAnimatedProperties AndAlso
                Not DirectCast(tab.ShapeLogo.Fill, SolidColorBrush).HasAnimatedProperties AndAlso Not DirectCast(tab.LabText.Foreground, SolidColorBrush).HasAnimatedProperties,
                "top-tab " & interruption & " replaces animated colors with stable palette values")
            SetSelectionHover(row, False)
            Select Case interruption
                Case "input-gate" : items.IsHitTestVisible = True : layout()
                Case "hidden" : host.Visibility = Visibility.Visible : layout()
                Case "disabled" : items.IsEnabled = True : layout()
                Case Else : items.Children.Add(tab) : items.Children.Add(row) : layout()
            End Select
            Dim actions = 0
            Dim count As RoutedEventHandler = Sub() actions += 1
            AddHandler row.Click, count : AddHandler tab.Click, count
            PointerEvent(row, Mouse.MouseUpEvent) : PointerEvent(tab, Mouse.MouseUpEvent)
            check(actions = 0, "restoring selection controls after " & interruption & " ignores the cancelled release")
            PointerEvent(row, Mouse.MouseDownEvent) : PointerEvent(row, Mouse.MouseUpEvent)
            PointerEvent(tab, Mouse.MouseDownEvent) : PointerEvent(tab, Mouse.MouseUpEvent)
            check(actions = 2, "selection controls accept new complete clicks after " & interruption)
            RemoveHandler row.Click, count : RemoveHandler tab.Click, count
        Next
        row.IsHitTestVisible = False
        check(Not row.Focusable AndAlso row.HoverTransition Is Nothing AndAlso background.Opacity = 0,
            "readonly About rows remain unfocusable and cannot acquire a hover clock")
        row.Selected = True
        check(row.Selected AndAlso background.Opacity = 0 AndAlso DirectCast(row.Foreground, SolidColorBrush).Color = PclAnimation.Brush("ColorBrush2").Color,
            "a selected readonly row retains its marker and text accent without enabling input")
        row.IsHitTestVisible = True : layout()
        check(row.Focusable AndAlso row.Selected, "restoring row input preserves its selection and keyboard eligibility")
        tab.Checked = True
        Dim ink = PclAnimation.Brush("ColorBrush3").Color
        host.Visibility = Visibility.Collapsed : layout()
        check(tab.Checked AndAlso DirectCast(tab.Background, SolidColorBrush).Color = Colors.White AndAlso DirectCast(tab.LabText.Foreground, SolidColorBrush).Color = ink,
            "hiding a selected top tab settles its selected palette instead of clearing its logical page")
        host.Visibility = Visibility.Visible : layout()
    End Sub
End Class
