Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Shared Sub VerifyFilterWheelBursts(directory As String, check As Action(Of Boolean, String))
        Dim viewer As New PclFilterScrollViewer With {.Content = New Border With {.Width = 2600, .Height = 40}, .HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            .VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, .CanContentScroll = False}
        Dim host As New Border With {.Width = 400, .Height = 90, .Child = viewer}, layout = ControlLayout(host)
        Dim wheel As Action(Of Integer) = Sub(delta) viewer.RaiseEvent(New MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) With {.RoutedEvent = Mouse.MouseWheelEvent})
        Using surface = PreviewSurface.Attach(host, 400, 90)
            layout() : viewer.ScrollToHorizontalOffset(100) : layout()
            wheel(-120) : wheel(-120) : wheel(-120) : layout()
            IO.File.WriteAllText(IO.Path.Combine(directory, "filter-wheel-burst-state.json"), New JObject From {{"origin", 100}, {"expected", 220}, {"actual", viewer.HorizontalOffset}}.ToString())
            check(Math.Abs(viewer.HorizontalOffset - 220) < 0.01, "same-turn horizontal filter wheel inputs retain every increment before native layout acknowledges the requests")
            viewer.ScrollToHorizontalOffset(100) : layout()
            wheel(-120) : wheel(-120) : wheel(120) : layout()
            check(Math.Abs(viewer.HorizontalOffset - 140) < 0.01, "opposite filter wheel input in one dispatcher turn reverses the pending destination instead of the stale native offset")
            viewer.ScrollToHorizontalOffset(100) : layout()
            For index = 1 To 120 : wheel(-1) : Next
            layout()
            check(Math.Abs(viewer.HorizontalOffset - 140) < 0.01, "small high-resolution wheel deltas accumulate without per-event rounding or native queue loss")
            viewer.ScrollToHorizontalOffset(viewer.ScrollableWidth - 10) : layout()
            wheel(-120) : wheel(120) : layout()
            check(Math.Abs(viewer.HorizontalOffset - viewer.ScrollableWidth + 40) < 0.01, "right-edge overshoot is discarded before a same-turn reverse filter input")
            viewer.ScrollToHorizontalOffset(10) : layout()
            wheel(120) : wheel(-120) : layout()
            check(Math.Abs(viewer.HorizontalOffset - 40) < 0.01, "left-edge overshoot cannot trap the next reverse filter input")

            Dim observations As New JArray
            For Each boundary In {"disabled", "gate", "hidden", "detached", "pointer", "key", "focus", "explicit"}
                viewer.ScrollToHorizontalOffset(100) : layout()
                wheel(-120) : wheel(-120) : wheel(-120)
                Select Case boundary
                    Case "disabled" : host.IsEnabled = False
                    Case "gate" : host.IsHitTestVisible = False
                    Case "hidden" : host.Visibility = Visibility.Collapsed
                    Case "detached" : host.Child = Nothing
                    Case "pointer" : PointerEvent(viewer, Mouse.PreviewMouseDownEvent)
                    Case "key"
                        viewer.RaiseEvent(New KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(viewer), Environment.TickCount, Key.Right) With {.RoutedEvent = Keyboard.PreviewKeyDownEvent})
                    Case "focus"
                        viewer.RaiseEvent(New KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, Nothing, viewer) With {.RoutedEvent = Keyboard.PreviewGotKeyboardFocusEvent})
                    Case "explicit" : viewer.StopScrolling()
                End Select
                layout()
                host.IsEnabled = True : host.IsHitTestVisible = True : host.Visibility = Visibility.Visible
                If host.Child Is Nothing Then host.Child = viewer
                layout()
                observations.Add(New JObject From {{"boundary", boundary}, {"afterCancel", viewer.HorizontalOffset}})
                IO.File.WriteAllText(IO.Path.Combine(directory, "filter-wheel-burst-interruptions.json"), observations.ToString())
                check(Math.Abs(viewer.HorizontalOffset - 100) < 0.01, "filter handoff replaces every unacknowledged native wheel request at the displayed position: " & boundary)
                wheel(-120) : layout()
                check(Math.Abs(viewer.HorizontalOffset - 140) < 0.01, "a fresh filter wheel starts from the accepted position after input handoff: " & boundary)
            Next

            viewer.ScrollToHorizontalOffset(100) : layout()
            Dim reentered = False
            Dim native As ScrollChangedEventHandler =
                Sub(sender, e)
                    If e.OriginalSource Is viewer AndAlso e.HorizontalChange <> 0 AndAlso Not reentered Then
                        reentered = True : wheel(-120) : wheel(-120)
                    End If
                End Sub
            AddHandler viewer.ScrollChanged, native
            Try
                wheel(-120) : layout()
                check(reentered AndAlso Math.Abs(viewer.HorizontalOffset - 220) < 0.01, "wheel input during native scroll acknowledgement retains both new increments")
            Finally
                RemoveHandler viewer.ScrollChanged, native
            End Try
            viewer.ScrollToHorizontalOffset(500) : layout() : wheel(-120) : layout()
            check(Math.Abs(viewer.HorizontalOffset - 540) < 0.01, "external native scrolling supplies the next wheel origin instead of an obsolete destination")
            wheel(-120) : DirectCast(viewer.Content, Border).Width = 200 : layout()
            check(viewer.ScrollableWidth = 0 AndAlso viewer.HorizontalOffset = 0, "shrinking filter content clamps and retires a pending horizontal wheel destination")
            DirectCast(viewer.Content, Border).Width = 2600 : layout() : wheel(-120) : layout()
            check(Math.Abs(viewer.HorizontalOffset - 40) < 0.01, "regrown filter content accepts fresh wheel input without reviving its retired destination")
            viewer.ScrollToHorizontalOffset(viewer.ScrollableWidth - 20) : layout() : wheel(-120)
            host.Width = 2500 : layout()
            Dim resized = viewer.HorizontalOffset
            check(resized <= viewer.ScrollableWidth AndAlso Math.Abs(viewer.ScrollableWidth - 100) < 0.01, "a wider filter viewport clamps an unacknowledged wheel to its new native range")
            host.Width = 400 : layout() : wheel(-120) : layout()
            check(Math.Abs(viewer.HorizontalOffset - resized - 40) < 0.01, "restoring the filter viewport width starts from its clamped position rather than the old edge target")
        End Using
    End Sub

    Private Sub VerifyPackFilterWheelBursts(directory As String, layout As Action, check As Action(Of Boolean, String))
        Dim viewer = PackVersionFilterViewport, session = _packSearchSession, project = _packProject, generation = _taskGeneration
        Dim wheel As Action(Of Integer) =
            Sub(delta)
                Dim args As New MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) With {.RoutedEvent = Mouse.PreviewMouseWheelEvent}
                viewer.RaiseEvent(args)
                If Not args.Handled Then
                    args.RoutedEvent = Mouse.MouseWheelEvent : viewer.RaiseEvent(args)
                End If
            End Sub
        Dim observations As New JObject
        Try
            viewer.ScrollToHorizontalOffset(100) : ModpackPage.ScrollToHome() : layout()
            wheel(-120) : wheel(-120) : wheel(-120) : layout()
            observations("burstOffset") = viewer.HorizontalOffset
            check(Math.Abs(viewer.HorizontalOffset - 220) < 0.01 AndAlso ModpackPage.VerticalOffset = 0 AndAlso ModpackPage.MotionClock Is Nothing,
                "the real overflowing detail filter retains a three-event horizontal burst without starting vertical page motion")
            viewer.ScrollToHorizontalOffset(100) : layout()
            wheel(-120) : wheel(-120) : wheel(-120)
            PageSurface.IsHitTestVisible = False : layout() : PageSurface.IsHitTestVisible = True : layout()
            observations("afterGate") = viewer.HorizontalOffset
            check(Math.Abs(viewer.HorizontalOffset - 100) < 0.01, "a real page handoff replaces pending filter wheel commands before the viewport can advance behind its input gate")
            wheel(-120) : layout()
            check(Math.Abs(viewer.HorizontalOffset - 140) < 0.01, "a new detail wheel uses the displayed position after page interaction is restored")
            Dim old = PackVersionFilters.Children(0)
            wheel(-120) : wheel(-120) : wheel(-120)
            InitializePackVersions() : layout()
            observations("afterRefresh") = viewer.HorizontalOffset
            check(viewer.HorizontalOffset = 0 AndAlso Not PackVersionFilters.Children.Contains(old) AndAlso _packGameFilter = "",
                "catalogue refresh retires the pending horizontal destination with its old capsules and resets the new viewport")
            wheel(-120) : layout()
            check(Math.Abs(viewer.HorizontalOffset - 40) < 0.01, "the refreshed overflowing catalogue accepts its next wheel from zero without reusing the old destination")
            check(_packSearchSession Is session AndAlso _packProject Is project AndAlso _taskGeneration = generation AndAlso Not _busy AndAlso Not _choosingOnline AndAlso
                ModpackPage.VerticalOffset = 0 AndAlso ModpackPage.MotionClock Is Nothing,
                "burst scrolling and catalogue handoff preserve accepted search, project and installation ownership")
            IO.File.WriteAllText(IO.Path.Combine(directory, "detail-filter-wheel-burst.json"), observations.ToString())
        Finally
            PageSurface.IsHitTestVisible = True : viewer.StopScrolling() : viewer.ScrollToLeftEnd() : layout()
        End Try
    End Sub
End Class
