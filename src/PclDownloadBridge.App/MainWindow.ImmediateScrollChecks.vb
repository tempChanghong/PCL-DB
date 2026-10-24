Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Shared Sub VerifyImmediateScrollBursts(directory As String, check As Action(Of Boolean, String))
        Dim viewer As New PclScrollViewer With {.Content = New Border With {.Height = 2600}, .CanContentScroll = False}
        Dim host As New Border With {.Width = 400, .Height = 180, .Child = viewer}, layout = ControlLayout(host)
        Using source = PreviewSurface.Attach(host, 400, 180)
            layout()
            For Each mode In {"smooth-off", "reduced-motion"}
                viewer.SmoothScrollingEnabled = mode <> "smooth-off"
                viewer.MotionPolicy.EnabledForChecks = mode <> "reduced-motion"
                viewer.ScrollToVerticalOffset(100) : layout()
                RouteScrollWheel(viewer, -120) : RouteScrollWheel(viewer, -120) : RouteScrollWheel(viewer, -120) : layout()
                IO.File.WriteAllText(IO.Path.Combine(directory, "immediate-scroll-burst-state.json"), New JObject From {{"mode", mode}, {"origin", 100}, {"expected", 460}, {"actual", viewer.VerticalOffset}}.ToString())
                check(Math.Abs(viewer.VerticalOffset - 460) < 0.01 AndAlso viewer.MotionClock Is Nothing, "immediate vertical wheel input preserves all same-turn increments without an animation clock: " & mode)
                viewer.ScrollToVerticalOffset(100) : layout()
                RouteScrollWheel(viewer, -120) : RouteScrollWheel(viewer, -120) : RouteScrollWheel(viewer, 120) : layout()
                check(Math.Abs(viewer.VerticalOffset - 220) < 0.01, "same-turn reverse wheel input subtracts from the pending immediate destination: " & mode)
                viewer.ScrollToVerticalOffset(100) : layout()
                For index = 1 To 120 : RouteScrollWheel(viewer, -1) : Next
                layout()
                check(Math.Abs(viewer.VerticalOffset - 220) < 0.01, "small immediate wheel increments survive native command coalescing: " & mode)
                viewer.ScrollToVerticalOffset(viewer.ScrollableHeight - 10) : layout()
                RouteScrollWheel(viewer, -120) : RouteScrollWheel(viewer, 120) : layout()
                check(Math.Abs(viewer.VerticalOffset - viewer.ScrollableHeight + 120) < 0.01, "bottom-edge overshoot is discarded before immediate reverse input: " & mode)
                viewer.ScrollToVerticalOffset(10) : layout()
                RouteScrollWheel(viewer, 120) : RouteScrollWheel(viewer, -120) : layout()
                check(Math.Abs(viewer.VerticalOffset - 120) < 0.01, "top-edge overshoot does not swallow an immediate reverse input: " & mode)
                For Each boundary In {"disabled", "gate", "hidden", "detached", "pointer", "key"}
                    viewer.ScrollToVerticalOffset(100) : layout()
                    RouteScrollWheel(viewer, -120) : RouteScrollWheel(viewer, -120) : RouteScrollWheel(viewer, -120)
                    Select Case boundary
                        Case "disabled" : host.IsEnabled = False
                        Case "gate" : host.IsHitTestVisible = False
                        Case "hidden" : host.Visibility = Visibility.Collapsed
                        Case "detached" : host.Child = Nothing
                        Case "pointer" : PointerEvent(viewer, Mouse.PreviewMouseDownEvent)
                        Case "key"
                            viewer.RaiseEvent(New KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(viewer), Environment.TickCount, Key.Escape) With {.RoutedEvent = Keyboard.PreviewKeyDownEvent})
                    End Select
                    layout()
                    host.IsEnabled = True : host.IsHitTestVisible = True : host.Visibility = Visibility.Visible
                    If host.Child Is Nothing Then host.Child = viewer
                    layout()
                    check(Math.Abs(viewer.VerticalOffset - 100) < 0.01 AndAlso viewer.MotionClock Is Nothing, "immediate scroll handoff revokes pending native commands at the displayed offset: " & mode & "/" & boundary)
                    RouteScrollWheel(viewer, -120) : layout()
                    check(Math.Abs(viewer.VerticalOffset - 220) < 0.01, "fresh immediate wheel input starts from the accepted offset after handoff: " & mode & "/" & boundary)
                Next
                viewer.ScrollToVerticalOffset(100) : layout()
                Dim reentered = False
                Dim native As ScrollChangedEventHandler =
                    Sub(sender, e)
                        If e.OriginalSource Is viewer AndAlso e.VerticalChange <> 0 AndAlso Not reentered Then
                            reentered = True : RouteScrollWheel(viewer, -120) : RouteScrollWheel(viewer, -120)
                        End If
                    End Sub
                AddHandler viewer.ScrollChanged, native
                Try
                    RouteScrollWheel(viewer, -120) : layout()
                    check(reentered AndAlso Math.Abs(viewer.VerticalOffset - 460) < 0.01, "input during native immediate acknowledgement retains every new increment: " & mode)
                Finally
                    RemoveHandler viewer.ScrollChanged, native
                End Try
                viewer.ScrollToVerticalOffset(500) : layout() : RouteScrollWheel(viewer, -120) : layout()
                check(Math.Abs(viewer.VerticalOffset - 620) < 0.01, "external native scrolling becomes the next immediate wheel origin: " & mode)
            Next
            viewer.SmoothScrollingEnabled = False : viewer.MotionPolicy.EnabledForChecks = True
            viewer.ScrollToVerticalOffset(100) : layout() : RouteScrollWheel(viewer, -120)
            viewer.SmoothScrollingEnabled = True : viewer.MotionPolicy.PauseForChecks = True : RouteScrollWheel(viewer, -120)
            SeekControl(viewer.MotionClock, 301) : layout()
            check(Math.Abs(viewer.VerticalOffset - 340) < 0.01 AndAlso viewer.MotionClock Is Nothing, "switching from immediate to smooth scrolling preserves the accepted queued displacement")
            viewer.ScrollToVerticalOffset(100) : layout() : viewer.ScrollDelta(240)
            Dim old = viewer.MotionClock : SeekControl(old, 60) : layout()
            Dim before = viewer.VerticalOffset
            viewer.SmoothScrollingEnabled = False
            RouteScrollWheel(viewer, -120) : RouteScrollWheel(viewer, -120) : RouteScrollWheel(viewer, -120)
            old.Controller.SkipToFill() : layout()
            check(Math.Abs(viewer.VerticalOffset - before - 360) < 0.01 AndAlso viewer.MotionClock Is Nothing, "switching from smooth to immediate scrolling retires the old clock and retains every fresh wheel increment")
        End Using
    End Sub

    Private Async Function VerifyDetailImmediateScrollAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim motion = _motionPreview, smooth = ModpackPage.SmoothScrollingEnabled, enabled = ModpackPage.MotionPolicy.EnabledForChecks
        Dim offset = ModpackPage.VerticalOffset, searchOffset = _packSearchScroll, detailOffset = _packDetailScroll
        Dim project = _packProject, session = _packSearchSession, releases = _packReleases, generation = _taskGeneration
        Dim cards = PackVersionCards.Children.Cast(Of UIElement)().ToArray(), spacer As New Border With {.Height = 1800}
        Dim observations As New JArray
        Try
            _motionPreview = False : PackVersionCards.Children.Add(spacer) : layout()
            For Each mode In {"smooth-off", "reduced-motion"}
                ModpackPage.SmoothScrollingEnabled = mode <> "smooth-off"
                ModpackPage.MotionPolicy.EnabledForChecks = mode <> "reduced-motion"
                ModpackPage.ScrollToVerticalOffset(100) : layout()
                RouteScrollWheel(ModpackPage, -120) : RouteScrollWheel(ModpackPage, -120) : RouteScrollWheel(ModpackPage, -120) : layout()
                check(Math.Abs(ModpackPage.VerticalOffset - 460) < 0.01 AndAlso ModpackPage.MotionClock Is Nothing,
                    "the real detail page preserves a complete immediate wheel burst without starting a clock: " & mode)
                ModpackPage.ScrollToVerticalOffset(100) : layout()
                RouteScrollWheel(ModpackPage, -120) : RouteScrollWheel(ModpackPage, -120) : RouteScrollWheel(ModpackPage, -120)
                Dim prompt = PromptOnlinePackAsync("输入版本名称", "静态滚动交接", _selectedFolder.Location, "Immediate-Scroll", False, CancellationToken.None)
                layout()
                check(PackNameOverlay.IsVisible AndAlso Not PageSurface.IsEnabled AndAlso Math.Abs(ModpackPage.VerticalOffset - 100) < 0.01,
                    "the real naming dialog revokes unacknowledged immediate scrolling before disabling its page: " & mode)
                CancelOnlinePackName(Nothing, Nothing)
                _onlinePromptMotion?.Controller.SkipToFill()
                Await WaitPackUiAsync(Function() _onlinePromptMotion Is Nothing)
                Await prompt : layout()
                RouteScrollWheel(ModpackPage, -120) : RouteScrollWheel(ModpackPage, -120) : RouteScrollWheel(ModpackPage, -120) : layout()
                check(Not PackNameOverlay.IsVisible AndAlso PageSurface.IsEnabled AndAlso ModpackPage.MotionClock Is Nothing AndAlso Math.Abs(ModpackPage.VerticalOffset - 460) < 0.01,
                    "cancelling real naming accepts a fresh immediate burst from the displayed offset: " & mode)
                ChangePackMode(PackBrowserMode.Search) : layout() : ChangePackMode(PackBrowserMode.Detail) : layout()
                check(Math.Abs(ModpackPage.VerticalOffset - 460) < 0.01 AndAlso ModpackPage.MotionClock Is Nothing AndAlso _packProject Is project AndAlso _packSearchSession Is session AndAlso
                    _packReleases Is releases AndAlso _taskGeneration = generation AndAlso Not _busy AndAlso Not _choosingOnline AndAlso
                    PackVersionCards.Children.Cast(Of UIElement)().Where(Function(item) item IsNot spacer).SequenceEqual(cards),
                    "immediate detail return retains its viewport, accepted cards, files and installation ownership: " & mode)
                observations.Add(New JObject From {{"mode", mode}, {"restoredOffset", ModpackPage.VerticalOffset}, {"taskUnchanged", _taskGeneration = generation}})
            Next
            IO.File.WriteAllText(IO.Path.Combine(directory, "detail-immediate-scroll.json"), observations.ToString())
        Finally
            CompleteOnlinePrompt(Nothing) : StopOnlinePromptMotion() : HideOnlinePrompt()
            _categoryMotion.Finish() : ModpackPage.StopScrolling()
            PackVersionCards.Children.Remove(spacer)
            _motionPreview = motion : ModpackPage.SmoothScrollingEnabled = smooth : ModpackPage.MotionPolicy.EnabledForChecks = enabled
            _packSearchScroll = searchOffset : _packDetailScroll = detailOffset
            ModpackPage.ScrollToVerticalOffset(offset) : layout()
        End Try
    End Function
End Class
