Imports System.IO
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Public Sub VerifyBasicControlInputUi(directory As String, passed As List(Of String))
        CheckBasicControls(directory, passed, False)
    End Sub
    Public Sub VerifyBasicControlMotionUi(directory As String, passed As List(Of String))
        CheckBasicControls(directory, passed, True)
    End Sub
    Private Sub CheckBasicControls(directory As String, passed As List(Of String), motion As Boolean)
        Dim saved = _appearance.ToJson(), observations As New JObject
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        Try
            For Each dark In {False, True}
                For Each kind In {"button", "list"}
                    RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", dark}})
                    Dim button = If(kind = "button", New PclButton With {.Text = "刷新", .Height = 40}, Nothing)
                    Dim row = If(kind = "list", New PclListItem With {.Title = "版本", .Height = 42}, Nothing)
                    Dim control As FrameworkElement = If(button IsNot Nothing, DirectCast(button, FrameworkElement), row)
                    Dim peer As New Button With {.Content = "焦点目标", .Height = 30}, panel As New StackPanel
                    panel.Children.Add(control) : panel.Children.Add(peer)
                    Dim host As New Border With {.Width = 420, .Height = 170, .Child = panel}, layout = ControlLayout(host), clicks = 0
                    If button IsNot Nothing Then AddHandler button.Click, Sub() clicks += 1
                    If row IsNot Nothing Then AddHandler row.Click, Sub() clicks += 1
                    Using source = PreviewSurface.Attach(host, 420, 170)
                        layout()
                        If motion Then
                            CheckBasicMotion(control, button, row, layout, check, kind & "/dark=" & dark)
                        Else
                            For Each boundary In {"focus", "capture", "theme", "gate", "disable", "hide", "detach"}
                                Dim before = clicks
                                PointerEvent(control, Mouse.MouseDownEvent)
                                Select Case boundary
                                    Case "focus"
                                        control.RaiseEvent(New KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, control, peer) With {.RoutedEvent = Keyboard.LostKeyboardFocusEvent})
                                    Case "capture"
                                        control.RaiseEvent(New MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) With {.RoutedEvent = Mouse.LostMouseCaptureEvent})
                                    Case "theme"
                                        RestoreAppearance(New JObject From {{"theme", "deepblue"}, {"darkMode", dark}})
                                    Case "gate" : panel.IsHitTestVisible = False : panel.IsHitTestVisible = True
                                    Case "disable" : panel.IsEnabled = False : panel.IsEnabled = True
                                    Case "hide" : host.Visibility = Visibility.Collapsed : layout() : host.Visibility = Visibility.Visible : layout()
                                    Case "detach" : host.Child = Nothing : layout() : host.Child = panel : layout()
                                End Select
                                PointerEvent(control, Mouse.MouseUpEvent) : layout()
                                check(clicks = before, "abandoned " & kind & " press cannot activate after " & boundary & "/dark=" & dark)
                                before = clicks
                                PointerEvent(control, Mouse.MouseDownEvent) : PointerEvent(control, Mouse.MouseUpEvent) : layout()
                                check(clicks = before + 1, "fresh " & kind & " press still activates once after " & boundary & "/dark=" & dark)
                            Next
                            Dim accepted = clicks
                            panel.IsHitTestVisible = False
                            PointerEvent(control, Mouse.MouseDownEvent)
                            panel.IsHitTestVisible = True
                            PointerEvent(control, Mouse.MouseUpEvent)
                            check(clicks = accepted, "gated " & kind & " cannot prime a release for a later visible page/dark=" & dark)
                        End If
                    End Using
                Next
            Next
            File.WriteAllText(Path.Combine(directory, If(motion, "basic-control-motion.json", "basic-control-input.json")), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            RestoreAppearance(saved)
        End Try
    End Sub
    Private Sub CheckBasicMotion(control As FrameworkElement, button As PclButton, row As PclListItem, layout As Action, check As Action(Of Boolean, String), label As String)
        Dim propertyInfo = control.GetType().GetProperty("MotionPolicy", Reflection.BindingFlags.Instance Or Reflection.BindingFlags.NonPublic)
        If propertyInfo Is Nothing Then
            check(False, "basic control exposes the shared reduced-motion policy: " & label)
            Return
        End If
        Dim policy = DirectCast(propertyInfo.GetValue(control), PclControlMotionPolicy)
        policy.EnabledForChecks = True : policy.PauseForChecks = True
        PointerEvent(control, Mouse.MouseDownEvent)
        Dim running = If(button IsNot Nothing, button.ScaleTransition IsNot Nothing, row.HoverTransition IsNot Nothing)
        If row IsNot Nothing Then row.Selected = True
        policy.EnabledForChecks = False : layout()
        check(running AndAlso If(button IsNot Nothing, button.ScaleTransition Is Nothing, row.HoverTransition Is Nothing AndAlso row.SelectionTransition Is Nothing),
            "changing to reduced motion retires running press/selection clocks: " & label)
        Dim before = 0
        Dim click As RoutedEventHandler = Sub() before += 1
        If button IsNot Nothing Then AddHandler button.Click, click Else AddHandler row.Click, click
        PointerEvent(control, Mouse.MouseUpEvent)
        check(before = 0, "reduced-motion change cannot replay the retired press: " & label)
        PointerEvent(control, Mouse.MouseDownEvent) : PointerEvent(control, Mouse.MouseUpEvent)
        If row IsNot Nothing Then row.Selected = False : row.Selected = True
        check(before = 1 AndAlso If(button IsNot Nothing, button.ScaleTransition Is Nothing, row.HoverTransition Is Nothing AndAlso row.SelectionTransition Is Nothing),
            "reduced-motion controls stay clickable and selection stays synchronous: " & label)
        ControlPointer(control, True) : layout()
        Dim background = If(button IsNot Nothing, DirectCast(button.FindName("PanFore"), Border), row.Children.OfType(Of Border)().First())
        check(Not DirectCast(background.Background, SolidColorBrush).HasAnimatedProperties AndAlso Not DirectCast(background.BorderBrush, SolidColorBrush).HasAnimatedProperties,
            "reduced-motion hover has no residual color animation: " & label)
        policy.EnabledForChecks = True
        PointerEvent(control, Mouse.MouseDownEvent)
        check(If(button IsNot Nothing, button.ScaleTransition IsNot Nothing, row.HoverTransition IsNot Nothing),
            "re-enabling motion restores PCL press animation: " & label)
        PointerEvent(control, Mouse.MouseUpEvent)
        Dim release As Animation.Clock = If(button IsNot Nothing, DirectCast(button.ScaleTransition, Animation.Clock), row.HoverTransition)
        release?.Controller.Pause() : layout()
        control.RaiseEvent(New KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, control, Nothing) With {.RoutedEvent = Keyboard.LostKeyboardFocusEvent})
        check(release IsNot Nothing AndAlso If(button IsNot Nothing, button.ScaleTransition Is release, row.HoverTransition Is release),
            "ordinary focus loss preserves the already-released PCL return animation: " & label)
        If button IsNot Nothing Then RemoveHandler button.Click, click Else RemoveHandler row.Click, click
    End Sub
End Class
