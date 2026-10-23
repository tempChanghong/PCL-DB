Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Sub VerifyResourceRowMotion(directory As String, check As Action(Of Boolean, String))
        Dim row As New PclResourceItem(New ModpackProject With {.Name = "资源行交互夹具", .Summary = "只在隔离呈现源中验证输入。", .Platform = ModpackPlatform.Modrinth,
            .GameVersions = {"1.20.1"}, .Downloads = 123456, .Updated = New DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)})
        Dim items As New ControlLayoutProbe
        items.Children.Add(row)
        Dim host As New Border With {.Width = 520, .Height = 90, .Child = items}
        Dim layout = ControlLayout(host), clicks = 0
        Dim back = DirectCast(row.Children(0), Border), scale = DirectCast(back.RenderTransform, ScaleTransform)
        Dim fill = DirectCast(back.Background, SolidColorBrush)
        Dim stable As Func(Of Boolean) = Function() row.HoverTransition Is Nothing AndAlso Not back.HasAnimatedProperties AndAlso Not scale.HasAnimatedProperties AndAlso Not fill.HasAnimatedProperties
        Dim seek As Action(Of ClockGroup, Double) = Sub(clock, milliseconds)
                                                       If clock Is Nothing Then Throw New InvalidOperationException("Expected a resource-row clock.")
                                                       clock.Controller.Pause()
                                                       If Not PumpMotionUntil(Function() clock.IsPaused) Then Throw New InvalidOperationException("Resource-row clock did not pause.")
                                                       clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(milliseconds), TimeSeekOrigin.BeginTime) : layout()
                                                   End Sub
        AddHandler row.Click, Sub() clicks += 1
        Using source = PreviewSurface.Attach(host, 520, 90)
            Dim sendKey As Action(Of Key, Boolean) = Sub(value, down)
                                                    row.RaiseEvent(New KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, value) With {.RoutedEvent = If(down, Keyboard.KeyDownEvent, Keyboard.KeyUpEvent)})
                                                End Sub
            layout() : ControlPointer(row, True)
            PointerEvent(row, Mouse.MouseDownEvent) : ControlPointer(row, False) : ControlPointer(row, True) : PointerEvent(row, Mouse.MouseUpEvent)
            check(clicks = 0, "leaving a pressed resource row cancels the gesture even after reentering before release")
            ControlKey(row, Key.Space, True) : ControlKey(row, Key.Enter, False)
            check(clicks = 0, "a different key release cannot complete a resource-row keyboard gesture")
            ControlKey(row, Key.Space, False)
            check(clicks = 1, "the matching resource-row key release invokes exactly once")
            PointerEvent(row, Mouse.MouseDownEvent) : sendKey(Key.Space, False)
            check(clicks = 1, "an orphan keyboard release cannot complete a resource-row mouse gesture")
            PointerEvent(row, Mouse.MouseUpEvent) : PointerEvent(row, Mouse.MouseUpEvent)
            check(clicks = 2, "a matching resource-row mouse release invokes once and ignores duplicate releases")
            sendKey(Key.Enter, True) : PointerEvent(row, Mouse.MouseUpEvent)
            check(clicks = 2, "a mouse release cannot complete a resource-row keyboard gesture")
            sendKey(Key.Enter, True) : sendKey(Key.Space, True) : sendKey(Key.Space, False) : sendKey(Key.Enter, False) : sendKey(Key.Enter, False)
            check(clicks = 3, "the first resource-row key retains ownership across repeated and competing key presses")
            sendKey(Key.Space, True)
            row.RaiseEvent(New KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, row, Nothing) With {.RoutedEvent = Keyboard.LostKeyboardFocusEvent})
            sendKey(Key.Space, False)
            check(clicks = 3, "losing keyboard focus cancels the resource-row gesture before its late release")

            row.MotionPolicy.PauseForChecks = True
            For Each dark In {False, True}
                RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", dark}})
                row.MotionPolicy.EnabledForChecks = False : ControlPointer(row, False) : layout()
                check(back.CornerRadius = New CornerRadius(3) AndAlso Math.Abs(scale.ScaleX - 0.8) < 0.00001 AndAlso back.Opacity = 0 AndAlso stable(),
                    "resource-row backing geometry retains PCL's radius 3 and idle scale 0.8 in dark mode " & dark)
                row.MotionPolicy.EnabledForChecks = True
                Dim measures = items.Measures, arrangements = items.Arrangements
                ControlPointer(row, True)
                Dim hover = row.HoverTransition
                check(hover.Children(0).Timeline.Duration.TimeSpan.TotalMilliseconds = 120 AndAlso hover.Children(1).Timeline.Duration.TimeSpan.TotalMilliseconds = 144,
                    "resource hover fades over PCL 120 ms while the backing plate scales over 144 ms in dark mode " & dark)
                seek(hover, 72)
                check(Math.Abs(scale.ScaleX - 0.975) < 0.00001 AndAlso Math.Abs(back.Opacity - 0.936) < 0.00001 AndAlso
                    DirectCast(row.RenderTransform, ScaleTransform).ScaleX = 1 AndAlso items.Measures = measures AndAlso items.Arrangements = arrangements,
                    "resource hover follows cubic Fluent without scaling content or relayout in dark mode " & dark)
                For index = 1 To 500 : ControlPointer(row, True) : Next
                check(row.HoverTransition Is hover AndAlso back.Background Is fill, "unchanged resource hover retains its clock and brush in dark mode " & dark)
                seek(hover, 145)
                check(stable() AndAlso fill.Color = PclAnimation.Brush("ColorBrushBg1").Color AndAlso scale.ScaleX = 1 AndAlso back.Opacity = 1,
                    "resource hover settles to PCL Bg1 and releases all clocks in dark mode " & dark)
                SaveControlVisual(host, IO.Path.Combine(directory, "resource-hover-" & If(dark, "dark", "blue") & ".png"))
                PointerEvent(row, Mouse.MouseDownEvent)
                Dim press = row.HoverTransition
                seek(press, 72)
                check(Math.Abs(scale.ScaleX - 0.9965) < 0.00001 AndAlso DirectCast(row.RenderTransform, ScaleTransform).ScaleX = 1,
                    "resource press contracts only the backing plate toward 0.996 in dark mode " & dark)
                seek(press, 145)
                check(stable() AndAlso fill.Color = PclAnimation.Brush("ColorBrush6").Color, "resource press settles to PCL ColorBrush6 in dark mode " & dark)
                ControlPointer(row, False)
                Dim leaving = row.HoverTransition
                seek(leaving, 90)
                Dim displayed = scale.ScaleX, alpha = back.Opacity
                ControlPointer(row, True)
                Dim returning = row.HoverTransition
                check(Math.Abs(scale.ScaleX - displayed) < 0.00001 AndAlso Math.Abs(back.Opacity - alpha) < 0.00001,
                    "resource reentry starts at the displayed backing frame in dark mode " & dark)
                leaving.Controller.SkipToFill() : layout()
                check(row.HoverTransition Is returning, "a retired resource exit cannot reset a newer hover in dark mode " & dark)
                seek(returning, 145) : ControlPointer(row, False)
                leaving = row.HoverTransition : seek(leaving, 180)
                check(Math.Abs(scale.ScaleX - 0.996) < 0.00001 AndAlso back.Opacity = 0,
                    "resource exit keeps its near-full backing scale until the 180 ms fade ends in dark mode " & dark)
                seek(leaving, 182)
                check(stable() AndAlso Math.Abs(scale.ScaleX - 0.8) < 0.00001 AndAlso back.Background Is fill,
                    "resource exit resets to 0.8 after its source 1 ms tail and releases clocks in dark mode " & dark)
                Dim before = clicks
                PointerEvent(row, Mouse.MouseUpEvent)
                check(clicks = before, "resource animation reversal never revives the canceled mouse gesture in dark mode " & dark)
            Next
            For Each interruption In {"input-gate", "hidden", "detached", "disabled", "theme", "preference"}
                row.MotionPolicy.EnabledForChecks = False : ControlPointer(row, True)
                row.MotionPolicy.EnabledForChecks = True : sendKey(Key.Space, True)
                Dim obsolete = row.HoverTransition, before = clicks
                Select Case interruption
                    Case "input-gate" : items.IsHitTestVisible = False
                    Case "hidden" : host.Visibility = Visibility.Collapsed
                    Case "detached" : items.Children.Clear()
                    Case "disabled" : row.IsEnabled = False
                    Case "theme" : RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", False}})
                    Case Else : row.MotionPolicy.EnabledForChecks = False
                End Select
                layout()
                check(stable(), "resource-row lifecycle settles all backing clocks across " & interruption)
                Select Case interruption
                    Case "input-gate" : items.IsHitTestVisible = True
                    Case "hidden" : host.Visibility = Visibility.Visible
                    Case "detached" : items.Children.Add(row)
                    Case "disabled" : row.IsEnabled = True
                    Case "preference" : row.MotionPolicy.EnabledForChecks = True
                End Select
                layout() : obsolete?.Controller.SkipToFill() : sendKey(Key.Space, False) : PointerEvent(row, Mouse.MouseUpEvent)
                check(clicks = before AndAlso stable(), "resource-row restore rejects late releases and obsolete completions across " & interruption)
                sendKey(Key.Enter, True) : sendKey(Key.Enter, False)
                check(clicks = before + 1, "resource-row input can begin a fresh gesture after " & interruption)
            Next
            row.MotionPolicy.EnabledForChecks = False : ControlPointer(row, True)
            check(stable() AndAlso fill.Color = PclAnimation.Brush("ColorBrushBg1").Color AndAlso back.Opacity = 1,
                "disabled client-area animation retains immediate resource hover feedback without clocks")
        End Using
    End Sub
End Class
