Imports System.IO
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Sub VerifyPackFilterWheelRoutes(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String))
        Dim added = 0
        Dim verify As Action(Of Boolean, String) = Sub(ok, label)
                                                      check(ok, label) : added += 1
                                                  End Sub
        Dim width = host.Width, previousMotion = _motionPreview
        Dim smooth = ModpackPage.SmoothScrollingEnabled, forced = ModpackPage.ForceMotionForChecks
        Dim surfacePaused = _surfaceMotion.PauseForChecks, detailPaused = _packDetailMotion.PauseForChecks
        Dim session = _packSearchSession, project = _packProject, taskGeneration = _taskGeneration, selection = _packGameFilter
        Dim cards = PackVersionCards.Children.Cast(Of Object)().ToArray()
        Dim observations As New JArray
        Dim wheel As Func(Of UIElement, Integer, Boolean) =
            Function(target, delta)
                Dim args As New MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) With {.RoutedEvent = Mouse.PreviewMouseWheelEvent}
                target.RaiseEvent(args)
                Dim preview = args.Handled
                If Not args.Handled Then
                    args.RoutedEvent = Mouse.MouseWheelEvent : target.RaiseEvent(args)
                End If
                layout()
                observations.Add(New JObject From {{"phase", "route"}, {"delta", delta}, {"previewHandled", preview}, {"handled", args.Handled},
                    {"horizontal", PackVersionFilterViewport.HorizontalOffset}, {"vertical", ModpackPage.VerticalOffset}, {"verticalAnimating", ModpackPage.MotionClock IsNot Nothing}})
                File.WriteAllText(Path.Combine(directory, "filter-wheel-nested-routes.json"), observations.ToString())
                Return args.Handled AndAlso Not preview
            End Function
        Dim seek As Action(Of ClockGroup, Double) = Sub(clock, ms)
                                                        PauseSelectionClock(clock)
                                                        clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(ms), TimeSeekOrigin.BeginTime) : layout()
                                                    End Sub
        Try
            ModpackPage.StopScrolling() : ModpackPage.ScrollToHome()
            PackVersionFilterViewport.ScrollToLeftEnd() : layout()
            ModpackPage.SmoothScrollingEnabled = True : ModpackPage.ForceMotionForChecks = True
            Dim capsule = DirectCast(PackVersionFilters.Children(0), PclRadioButton)
            Dim visual = DirectCast(VisualTreeHelper.GetChild(capsule, 0), UIElement)
            verify(ModpackPage.ScrollableHeight > 0 AndAlso PackVersionFilterViewport.ScrollableWidth > 0 AndAlso PackVersionFilterViewport.ScrollableHeight = 0,
                "nested wheel fixture contains an overflowing horizontal filter inside a genuinely scrollable details page")
            verify(Not ModpackPage.OwnsWheel(PackVersionFilterViewport) AndAlso Not ModpackPage.OwnsWheel(capsule) AndAlso Not ModpackPage.OwnsWheel(visual),
                "vertical preview routing delegates overflow from the filter its capsule and the capsule's nested visual")
            verify(wheel(capsule, -120) AndAlso PackVersionFilterViewport.HorizontalOffset > 0 AndAlso ModpackPage.VerticalOffset = 0 AndAlso ModpackPage.MotionClock Is Nothing,
                "a full preview-to-bubble capsule wheel route moves only the horizontal filter and creates no vertical inertia")
            verify(wheel(visual, 120) AndAlso PackVersionFilterViewport.HorizontalOffset = 0 AndAlso ModpackPage.VerticalOffset = 0,
                "reverse wheel over a nested capsule visual returns the filter without moving the page")
            verify(wheel(capsule, 120) AndAlso PackVersionFilterViewport.HorizontalOffset = 0 AndAlso ModpackPage.MotionClock Is Nothing,
                "the horizontal filter keeps wheel ownership at its left boundary")
            PackVersionFilterViewport.ScrollToRightEnd() : layout()
            Dim right = PackVersionFilterViewport.HorizontalOffset
            verify(wheel(PackVersionFilterViewport, -120) AndAlso Math.Abs(PackVersionFilterViewport.HorizontalOffset - right) < 0.01 AndAlso ModpackPage.VerticalOffset = 0,
                "the horizontal filter also keeps wheel ownership at its right boundary")
            verify(wheel(capsule, 120) AndAlso PackVersionFilterViewport.HorizontalOffset < right AndAlso ModpackPage.MotionClock Is Nothing,
                "opposite wheel input leaves the horizontal boundary without starting an outer wheel clock")
            _motionPreview = True : _packDetailMotion.PauseForChecks = True : _surfaceMotion.PauseForChecks = True
            ChangePackContent(PackBrowserMode.Detail, Sub()
                                                    End Sub)
            Dim detail = _packDetailMotion.Transition
            verify(detail IsNot Nothing, "nested wheel fixture uses the actual details-content page animation")
            seek(detail, 175)
            PackVersionFilterViewport.ScrollToLeftEnd() : layout()
            verify(wheel(visual, -120) AndAlso PackVersionFilterViewport.HorizontalOffset > 0 AndAlso ModpackPage.VerticalOffset = 0 AndAlso
                ModpackPage.MotionClock Is Nothing AndAlso _packDetailMotion.Transition Is detail,
                "horizontal wheel during details-card entry preserves its page clock and vertical viewport")
            SaveControlVisual(host, Path.Combine(directory, "filter-wheel-detail-entry.png"))
            _packDetailMotion.Finish()
            SwitchMainPage(True) : _surfaceMotion.Finish() : SwitchMainPage(False) : layout()
            Dim root = _surfaceMotion.Transition
            verify(root IsNot Nothing, "nested wheel fixture also enters through the actual root-page handoff")
            seek(root, 240)
            PackVersionFilterViewport.ScrollToLeftEnd() : layout()
            verify(wheel(capsule, -120) AndAlso PackVersionFilterViewport.HorizontalOffset > 0 AndAlso ModpackPage.VerticalOffset = 0 AndAlso
                ModpackPage.MotionClock Is Nothing AndAlso _surfaceMotion.Transition Is root,
                "horizontal wheel during root-page entry leaves its animation and vertical scroll owner unchanged")
            SaveControlVisual(host, Path.Combine(directory, "filter-wheel-root-entry.png"))
            _surfaceMotion.Finish()
            host.Width = 2200 : layout()
            verify(PackVersionFilterViewport.ScrollableWidth = 0 AndAlso ModpackPage.ScrollableHeight > 0 AndAlso ModpackPage.OwnsWheel(capsule),
                "when a wider window removes overflow the same filter gives wheel ownership back to the details page")
            wheel(capsule, -120)
            Dim vertical = ModpackPage.MotionClock
            verify(vertical IsNot Nothing, "a fitting row starts the existing source-style outer wheel animation")
            vertical.Controller.Pause()
            vertical.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(300), TimeSeekOrigin.BeginTime) : layout()
            verify(ModpackPage.VerticalOffset > 0 AndAlso PackVersionFilterViewport.HorizontalOffset = 0,
                "a full routed wheel over fitting capsules scrolls the page vertically")
            observations.Add(New JObject From {{"phase", "vertical-complete"}, {"horizontal", PackVersionFilterViewport.HorizontalOffset}, {"vertical", ModpackPage.VerticalOffset}})
            File.WriteAllText(Path.Combine(directory, "filter-wheel-nested-routes.json"), observations.ToString())
            verify(_packSearchSession Is session AndAlso _packProject Is project AndAlso _taskGeneration = taskGeneration AndAlso _packGameFilter = selection AndAlso
                PackVersionCards.Children.Cast(Of Object)().SequenceEqual(cards),
                "wheel routing layout and entry animation preserve accepted project cards filters and installation ownership")
        Finally
            _packDetailMotion.Finish() : _surfaceMotion.Finish()
            _motionPreview = previousMotion : _surfaceMotion.PauseForChecks = surfacePaused : _packDetailMotion.PauseForChecks = detailPaused
            ModpackPage.StopScrolling() : ModpackPage.SmoothScrollingEnabled = smooth : ModpackPage.ForceMotionForChecks = forced
            host.Width = width : ModpackPage.ScrollToHome() : PackVersionFilterViewport.ScrollToLeftEnd() : layout()
        End Try
        File.WriteAllText(Path.Combine(directory, "filter-wheel-route-count.json"), New JObject From {{"checks", added}}.ToString())
    End Sub
End Class
