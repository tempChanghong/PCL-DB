Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Public Sub VerifyKeyboardHandoffControlUi(directory As String, passed As List(Of String))
        Dim saved = _appearance.ToJson(), observations As New JObject
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        Try
            For Each dark In {False, True}
                For Each animated In {False, True}
                    RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", dark}})
                    Dim button As New PclButton With {.Text = "确认", .Height = 35}, panel As New StackPanel
                    panel.Children.Add(button)
                    Dim host As New Border With {.Width = 420, .Height = 150, .Child = panel}, layout = ControlLayout(host), clicks = 0
                    AddHandler button.Click, Sub() clicks += 1
                    button.MotionPolicy.EnabledForChecks = animated : button.MotionPolicy.PauseForChecks = True
                    Dim label = "/dark=" & dark & "/animated=" & animated
                    Using surface = PreviewSurface.Attach(host, 420, 150)
                        layout()
                        ControlKey(button, Key.Space, True)
                        check(clicks = 0 AndAlso (button.ScaleTransition IsNot Nothing) = animated, "Space down defers the action and owns the PCL press" & label)
                        ControlKey(button, Key.Space, False)
                        check(clicks = 1 AndAlso (button.ScaleTransition IsNot Nothing) = animated, "Space release activates once with the configured motion policy" & label)
                        ControlKey(button, Key.Space, False)
                        check(clicks = 1, "an unmatched Space release cannot reactivate the button" & label)
                        ControlKey(button, Key.Enter, True) : ControlKey(button, Key.Enter, False)
                        check(clicks = 2, "Enter preserves its single key-down activation" & label)
                        Dim mixed = clicks
                        ControlKey(button, Key.Space, True) : PointerEvent(button, Mouse.MouseDownEvent) : PointerEvent(button, Mouse.MouseUpEvent)
                        check(clicks = mixed, "a pointer cannot steal a held Space gesture" & label)
                        ControlKey(button, Key.Space, False)
                        check(clicks = mixed + 1, "held Space keeps single activation ownership" & label)
                        mixed = clicks
                        PointerEvent(button, Mouse.MouseDownEvent) : ControlKey(button, Key.Space, True) : ControlKey(button, Key.Space, False)
                        check(clicks = mixed, "Space cannot steal a held pointer gesture" & label)
                        PointerEvent(button, Mouse.MouseUpEvent)
                        check(clicks = mixed + 1, "held pointer keeps single activation ownership" & label)
                        For Each boundary In {"focus", "capture", "gate", "hide", "detach", "theme", "preference"}
                            Dim before = clicks
                            ControlKey(button, Key.Space, True)
                            Select Case boundary
                                Case "focus" : button.RaiseEvent(New KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, button, Nothing) With {.RoutedEvent = Keyboard.LostKeyboardFocusEvent})
                                Case "capture" : button.RaiseEvent(New MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) With {.RoutedEvent = Mouse.LostMouseCaptureEvent})
                                Case "gate" : panel.IsHitTestVisible = False : panel.IsHitTestVisible = True
                                Case "hide" : host.Visibility = Visibility.Collapsed : layout() : host.Visibility = Visibility.Visible : layout()
                                Case "detach" : host.Child = Nothing : layout() : host.Child = panel : layout()
                                Case "theme" : RestoreAppearance(New JObject From {{"theme", "deepblue"}, {"darkMode", dark}})
                                Case "preference" : button.MotionPolicy.EnabledForChecks = Not animated : button.MotionPolicy.EnabledForChecks = animated
                            End Select
                            ControlKey(button, Key.Space, False)
                            check(clicks = before AndAlso button.ScaleTransition Is Nothing, "retired Space cannot activate after " & boundary & label)
                            before = clicks
                            ControlKey(button, Key.Space, True) : ControlKey(button, Key.Space, False)
                            check(clicks = before + 1, "fresh Space remains available after " & boundary & label)
                        Next
                    End Using
                Next
            Next
            File.WriteAllText(Path.Combine(directory, "keyboard-control.json"), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            RestoreAppearance(saved)
        End Try
    End Sub
    Public Sub VerifyKeyboardHandoffPromptUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, saved = _appearance.ToJson(), observations As New JObject
        Dim host As New Border With {.Width = 900, .Height = 550}, layout = ControlLayout(host)
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 550)
                layout()
                owner.CheckPromptExitKeyboard(layout, check)
            End Using
            File.WriteAllText(Path.Combine(directory, "keyboard-prompt.json"), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            owner._closing = True : owner.CompleteOnlinePrompt(Nothing) : owner.StopOnlinePromptMotion() : owner.HideOnlinePrompt()
            owner.CancelResourceRequests() : owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._pageSwitch.Finish() : owner._surfaceMotion.Finish() : owner._categoryMotion.Finish()
            owner.RestoreAppearance(saved) : host.Child = Nothing : owner.Content = original
        End Try
    End Sub
    Private Sub CheckPromptExitKeyboard(layout As Action, check As Action(Of Boolean, String))
        For Each dark In {False, True}
            RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", dark}})
            _motionPreview = False : ShowDownload(Nothing, Nothing)
            SelectVersion(_versions.Single(Function(item) item.Id = "1.20.1")) : _pageSwitch.Finish() : layout()
            _motionPreview = True
            Dim selected = _selectedEntry, pending = PromptOnlinePackAsync("确认", "退场键盘检查", Nothing, Nothing, True, CancellationToken.None)
            _onlinePromptMotion.Controller.Pause()
            ControlKey(OnlinePackCancel, Key.Escape, True)
            Dim closing = _onlinePromptMotion : closing?.Controller.Pause()
            check(pending.IsCompleted AndAlso pending.Result Is Nothing AndAlso _downloadView = DownloadView.Install AndAlso closing IsNot Nothing,
                "first Escape cancels only the visible prompt/dark=" & dark)
            For Each time In {0.0, 50, 150}
                closing.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(time), Animation.TimeSeekOrigin.BeginTime) : layout()
                ControlKey(OnlinePackCancel, Key.Escape, True)
                check(_downloadView = DownloadView.Install AndAlso _selectedEntry Is selected AndAlso _onlinePromptMotion Is closing AndAlso PackNameOverlay.Visibility = Visibility.Visible,
                    "Escape remains inside the closing overlay at " & time & "ms/dark=" & dark)
            Next
            closing.Controller.SkipToFill()
            If Not PumpMotionUntil(Function() _onlinePromptMotion Is Nothing) Then Throw New TimeoutException("Prompt exit did not settle.")
            layout()
            check(PackNameOverlay.Visibility = Visibility.Collapsed AndAlso PageSurface.IsEnabled AndAlso TopTabs.IsEnabled,
                "finished prompt exit restores background input/dark=" & dark)
            ControlKey(InstallScroll, Key.Escape, True) : _pageSwitch.Finish() : layout()
            check(_downloadView = DownloadView.Versions, "a fresh Escape can navigate after the overlay is hidden/dark=" & dark)
            _motionPreview = False : SetDownloadView(DownloadView.Install) : _pageSwitch.Finish() : layout() : _motionPreview = True
            Dim first = PromptOnlinePackAsync("确认", "空格取消后重开", Nothing, Nothing, True, CancellationToken.None)
            _onlinePromptMotion.Controller.Pause()
            ControlKey(OnlinePackConfirm, Key.Space, True)
            check(Not first.IsCompleted, "holding Space cannot prematurely confirm the current prompt/dark=" & dark)
            ControlKey(OnlinePackCancel, Key.Escape, True)
            _onlinePromptMotion?.Controller.Pause()
            Dim second = PromptOnlinePackAsync("确认", "新的确认所有者", Nothing, Nothing, True, CancellationToken.None)
            _onlinePromptMotion.Controller.Pause()
            ControlKey(OnlinePackConfirm, Key.Space, False)
            check(first.IsCompleted AndAlso first.Result Is Nothing AndAlso Not second.IsCompleted,
                "a retired Space release cannot confirm a replacement prompt/dark=" & dark)
            ControlKey(OnlinePackConfirm, Key.Space, True) : ControlKey(OnlinePackConfirm, Key.Space, False)
            check(second.IsCompleted AndAlso second.Result = "yes", "a replacement prompt accepts a fresh Space gesture/dark=" & dark)
            _onlinePromptMotion.Controller.SkipToFill()
            If Not PumpMotionUntil(Function() _onlinePromptMotion Is Nothing) Then Throw New TimeoutException("Replacement prompt exit did not settle.")
            layout()
        Next
    End Sub
End Class
