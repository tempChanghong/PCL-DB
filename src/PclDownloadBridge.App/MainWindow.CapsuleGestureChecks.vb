Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Sub VerifyCapsuleGestureOwnership(directory As String, check As Action(Of Boolean, String))
        Dim tab As New PclRadioButton With {.Text = "1.20.1", .ColorType = PclRadioButton.ColorState.Highlight}
        Dim peer As New PclRadioButton With {.Text = "全部", .ColorType = PclRadioButton.ColorState.Highlight, .Checked = True}
        Dim row As New StackPanel With {.Orientation = Orientation.Horizontal, .VerticalAlignment = VerticalAlignment.Center}
        row.Children.Add(peer) : row.Children.Add(tab)
        Dim host As New Border With {.Width = 300, .Height = 90, .Child = row}, layout = ControlLayout(host), clicks = 0
        AddHandler tab.Click, Sub()
                                  clicks += 1 : tab.Checked = True : peer.Checked = False
                              End Sub
        Using surface = PreviewSurface.Attach(host, 300, 90)
            layout() : tab.MotionPolicy.EnabledForChecks = True : tab.MotionPolicy.PauseForChecks = True
            Dim observations As New JArray
            For Each dark In {False, True}
                For Each role In {PclRadioButton.ColorState.Highlight, PclRadioButton.ColorState.White}
                    For Each interruption In {"focus", "theme", "capture"}
                        tab.MotionPolicy.EnabledForChecks = False
                        tab.Checked = False : peer.Checked = True : tab.ColorType = role : peer.ColorType = role
                        RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", dark}}) : layout()
                        SetSelectionHover(tab, True) : tab.MotionPolicy.EnabledForChecks = True
                        Dim before = clicks
                        PointerEvent(tab, Mouse.MouseDownEvent)
                        Dim old = tab.ColorTransition
                        Select Case interruption
                            Case "focus"
                                tab.RaiseEvent(New KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, tab, peer) With {.RoutedEvent = Keyboard.LostKeyboardFocusEvent})
                            Case "theme"
                                RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", Not dark}})
                            Case "capture"
                                tab.RaiseEvent(New MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) With {.RoutedEvent = Mouse.LostMouseCaptureEvent})
                        End Select
                        layout()
                        Dim expected = If(role = PclRadioButton.ColorState.Highlight, PclAnimation.Brush("ColorBrush7").Color, Color.FromArgb(50, 255, 255, 255))
                        check(tab.ColorTransition Is Nothing AndAlso tab.InkTransition Is Nothing AndAlso tab.LogoTransition Is Nothing AndAlso DirectCast(tab.Background, SolidColorBrush).Color = expected,
                            "capsule cancellation removes pressed color clocks and restores the live hover palette: " & role.ToString() & "/" & dark & "/" & interruption)
                        old?.Controller.SkipToFill() : PointerEvent(tab, Mouse.MouseUpEvent) : layout()
                        observations.Add(New JObject From {{"role", role.ToString()}, {"dark", dark}, {"interruption", interruption}, {"clicksBefore", before}, {"clicksAfterRelease", clicks}, {"selectedAfterRelease", tab.Checked}})
                        IO.File.WriteAllText(IO.Path.Combine(directory, "capsule-cancellation-states.json"), observations.ToString())
                        check(clicks = before AndAlso Not tab.Checked AndAlso peer.Checked,
                            "an abandoned capsule press cannot select after cancellation or old clock completion: " & role.ToString() & "/" & dark & "/" & interruption)
                        PointerEvent(tab, Mouse.MouseDownEvent) : PointerEvent(tab, Mouse.MouseUpEvent) : layout()
                        check(clicks = before + 1 AndAlso tab.Checked AndAlso Not peer.Checked AndAlso tab.ColorTransition.Timeline.Duration.TimeSpan.TotalMilliseconds = 120,
                            "a fresh capsule gesture still selects exactly once with PCL's selection transition: " & role.ToString() & "/" & dark & "/" & interruption)
                    Next
                Next
            Next
        End Using
    End Sub

    Private Sub VerifyFilterGestureOwnership(directory As String, layout As Action, check As Action(Of Boolean, String))
        Dim appearance = _appearance.ToJson(), generation = _taskGeneration, session = _packSearchSession, project = _packProject
        Dim all = DirectCast(PackVersionFilters.Children(0), PclRadioButton)
        Dim target = PackVersionFilters.Children.OfType(Of PclRadioButton)().Single(Function(button) button.Text = "1.20")
        Dim enabled = target.MotionPolicy.EnabledForChecks, paused = target.MotionPolicy.PauseForChecks
        Try
            SelectPackVersionFilter(target) : layout()
            Dim card = DirectCast(PackVersionCards.Children(0), PclCard), body = card.Body
            Dim release = DirectCast(DirectCast(body, StackPanel).Children(0), PclListItem).Tag
            SelectPackVersionFilter(all) : layout()
            target.MotionPolicy.EnabledForChecks = True : target.MotionPolicy.PauseForChecks = True
            Dim cards = PackVersionCards.Children.Cast(Of UIElement)().ToArray()
            For Each interruption In {"focus", "theme", "capture"}
                SetSelectionHover(target, True) : PointerEvent(target, Mouse.MouseDownEvent)
                Dim old = target.ColorTransition
                Select Case interruption
                    Case "focus"
                        target.RaiseEvent(New KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, target, all) With {.RoutedEvent = Keyboard.LostKeyboardFocusEvent})
                    Case "theme"
                        RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", Not _appearance.DarkMode}})
                    Case "capture"
                        target.RaiseEvent(New MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) With {.RoutedEvent = Mouse.LostMouseCaptureEvent})
                End Select
                old?.Controller.SkipToFill() : PointerEvent(target, Mouse.MouseUpEvent) : layout()
                check(_packGameFilter = "" AndAlso all.Checked AndAlso Not target.Checked AndAlso PackVersionCards.Children.Cast(Of UIElement)().SequenceEqual(cards),
                    "the real detail filter keeps its selected family and cards after an abandoned press: " & interruption)
                PointerEvent(target, Mouse.MouseDownEvent) : PointerEvent(target, Mouse.MouseUpEvent) : layout()
                check(_packGameFilter = "1.20" AndAlso target.Checked AndAlso Not all.Checked AndAlso PackVersionCards.Children.Count = 1 AndAlso
                    PackVersionCards.Children(0) Is card AndAlso card.Body Is body AndAlso DirectCast(DirectCast(body, StackPanel).Children(0), PclListItem).Tag Is release,
                    "a fresh filter gesture reuses its accepted card, rows and archive identity: " & interruption)
                SelectPackVersionFilter(all) : layout()
            Next
            PointerEvent(target, Mouse.MouseDownEvent)
            Dim retired = target.ColorTransition
            InitializePackVersions() : layout()
            retired?.Controller.SkipToFill() : PointerEvent(target, Mouse.MouseUpEvent) : layout()
            check(Not PackVersionFilters.Children.Contains(target) AndAlso _packGameFilter = "" AndAlso DirectCast(PackVersionFilters.Children(0), PclRadioButton).Checked,
                "catalogue refresh retires a pressed old capsule before its pointer release can change the new selection")
            check(_packSearchSession Is session AndAlso _packProject Is project AndAlso _taskGeneration = generation AndAlso Not _busy AndAlso Not _choosingOnline,
                "filter interruption and refresh preserve search ownership and cannot start an installation")
            IO.File.WriteAllText(IO.Path.Combine(directory, "detail-filter-gesture-state.json"), New JObject From {
                {"selectedFamily", _packGameFilter}, {"taskUnchanged", _taskGeneration = generation}, {"oldCapsuleRetired", Not PackVersionFilters.Children.Contains(target)}}.ToString())
        Finally
            target.MotionPolicy.EnabledForChecks = enabled : target.MotionPolicy.PauseForChecks = paused
            RestoreAppearance(appearance) : layout()
        End Try
    End Sub
End Class
