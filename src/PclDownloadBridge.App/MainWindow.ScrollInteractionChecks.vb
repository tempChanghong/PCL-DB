Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media.Animation
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Shared Sub VerifyScrollInteractionOwnership(directory As String, check As Action(Of Boolean, String))
        Dim viewer As New PclScrollViewer With {.Content = New Border With {.Height = 2600}, .CanContentScroll = False, .ForceMotionForChecks = True}
        Dim host As New Border With {.Width = 400, .Height = 180, .Child = viewer}, layout = ControlLayout(host)
        Using surface = PreviewSurface.Attach(host, 400, 180)
            layout() : viewer.ScrollToVerticalOffset(150) : layout()
            viewer.ScrollDelta(240)
            Dim scrolling = viewer.MotionClock
            SeekControl(scrolling, 60)
            Dim before = viewer.VerticalOffset
            host.IsEnabled = False
            IO.File.WriteAllText(IO.Path.Combine(directory, "scroll-disable-state.json"), New JObject From {
                {"before", before}, {"after", viewer.VerticalOffset}, {"clockOwnedAfterDisable", viewer.MotionClock IsNot Nothing},
                {"loaded", viewer.IsLoaded}, {"visible", viewer.IsVisible}, {"enabled", viewer.IsEnabled}}.ToString())
            check(viewer.MotionClock Is Nothing, "disabling a scroll owner's interaction immediately retires its wheel animation")
            host.IsEnabled = True : layout()
            viewer.MotionPolicy.PauseForChecks = True
            For Each boundary In {"disabled", "input-gate", "hidden", "detached", "smooth-off", "reduced-motion"}
                viewer.ScrollToVerticalOffset(150) : layout() : viewer.ScrollDelta(240)
                Dim old = viewer.MotionClock
                SeekControl(old, 60) : before = viewer.VerticalOffset
                Select Case boundary
                    Case "disabled" : host.IsEnabled = False
                    Case "input-gate" : host.IsHitTestVisible = False
                    Case "hidden" : host.Visibility = Visibility.Collapsed
                    Case "detached" : host.Child = Nothing
                    Case "smooth-off" : viewer.SmoothScrollingEnabled = False
                    Case "reduced-motion" : viewer.MotionPolicy.EnabledForChecks = False
                End Select
                layout()
                check(viewer.MotionClock Is Nothing AndAlso Math.Abs(viewer.VerticalOffset - before) < 0.1, "scroll interruption retires the old clock at the displayed position: " & boundary)
                viewer.ScrollDelta(120) : layout()
                Dim blocked = boundary <> "smooth-off" AndAlso boundary <> "reduced-motion"
                If blocked Then
                    RouteScrollWheel(viewer, -120) : layout()
                    check(viewer.MotionClock Is Nothing AndAlso Math.Abs(viewer.VerticalOffset - before) < 0.1, "inactive direct and routed wheel input cannot move the viewport: " & boundary)
                Else
                    check(viewer.MotionClock Is Nothing AndAlso Math.Abs(viewer.VerticalOffset - before - 120) < 0.1, "reduced scrolling accepts a fresh input as exact immediate displacement: " & boundary)
                End If
                host.IsEnabled = True : host.IsHitTestVisible = True : host.Visibility = Visibility.Visible
                If host.Child Is Nothing Then host.Child = viewer
                viewer.SmoothScrollingEnabled = True : viewer.MotionPolicy.EnabledForChecks = True : layout()
                Dim restartAt = viewer.VerticalOffset
                viewer.ScrollDelta(-80)
                Dim fresh = viewer.MotionClock
                old.Controller.SkipToFill() : layout()
                check(fresh IsNot Nothing AndAlso viewer.MotionClock Is fresh, "reopening interaction creates a fresh owner that an old completion cannot cancel: " & boundary)
                SeekControl(fresh, 301)
                check(viewer.MotionClock Is Nothing AndAlso Math.Abs(viewer.VerticalOffset - restartAt + 80) < 0.1, "a fresh gesture retains PCL's full displacement after interruption: " & boundary)
            Next

            ' Hold a real DP sample before the native ScrollViewer command has
            ' been acknowledged, then revoke input in that same dispatcher turn.
            viewer.ScrollToVerticalOffset(150) : layout() : viewer.ScrollDelta(240)
            scrolling = viewer.MotionClock : SeekControl(scrolling, 30) : before = viewer.VerticalOffset
            scrolling.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(120), TimeSeekOrigin.BeginTime)
            Dim requestsField = GetType(PclScrollViewer).GetField("_issuedOffsets", Reflection.BindingFlags.Instance Or Reflection.BindingFlags.NonPublic)
            Dim pending = DirectCast(requestsField.GetValue(viewer), List(Of Double))
            Dim pendingCount = pending.Count
            check(pendingCount > 0 AndAlso pending.Last() > before, "a real animation sample has a native scroll request pending before input is revoked")
            host.IsHitTestVisible = False : layout()
            IO.File.WriteAllText(IO.Path.Combine(directory, "scroll-pending-handoff.json"), New JObject From {
                {"before", before}, {"pendingCount", pendingCount}, {"after", viewer.VerticalOffset}, {"clockOwned", viewer.MotionClock IsNot Nothing}}.ToString())
            check(viewer.MotionClock Is Nothing AndAlso Math.Abs(viewer.VerticalOffset - before) < 0.1, "input revocation replaces the pending native tick instead of advancing behind the gate")
            host.IsHitTestVisible = True : scrolling.Controller.SkipToFill() : layout()
            check(Math.Abs(viewer.VerticalOffset - before) < 0.1, "a retired pending tick cannot reappear when input is restored")

            viewer.ScrollToVerticalOffset(150) : layout() : viewer.ScrollDelta(240)
            scrolling = viewer.MotionClock : SeekControl(scrolling, 30)
            scrolling.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(120), TimeSeekOrigin.BeginTime)
            Dim revoked = False, revokedAt = Double.NaN
            Dim revokeDuringLayout As ScrollChangedEventHandler = Sub(sender, args)
                                                                     If revoked OrElse args.OriginalSource IsNot viewer OrElse args.VerticalChange = 0 Then Return
                                                                     revoked = True : revokedAt = viewer.VerticalOffset : host.IsEnabled = False
                                                                 End Sub
            AddHandler viewer.ScrollChanged, revokeDuringLayout
            Try
                viewer.ScrollDelta(80) : layout()
                check(revoked, "acknowledging a pending native sample can revoke input during a replacement gesture's layout")
                IO.File.WriteAllText(IO.Path.Combine(directory, "scroll-reentrant-handoff.json"), New JObject From {
                    {"revokedAt", revokedAt}, {"after", viewer.VerticalOffset}, {"clockOwned", viewer.MotionClock IsNot Nothing}}.ToString())
                check(viewer.MotionClock Is Nothing AndAlso Math.Abs(viewer.VerticalOffset - revokedAt) < 0.1,
                    "a replacement gesture rechecks interaction after layout instead of applying a late immediate displacement")
            Finally
                RemoveHandler viewer.ScrollChanged, revokeDuringLayout
                host.IsEnabled = True : viewer.StopScrolling()
            End Try
        End Using
    End Sub

    Private Shared Sub RouteScrollWheel(target As UIElement, delta As Integer)
        Dim args As New MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) With {.RoutedEvent = Mouse.PreviewMouseWheelEvent}
        target.RaiseEvent(args)
        If Not args.Handled Then
            args.RoutedEvent = Mouse.MouseWheelEvent : target.RaiseEvent(args)
        End If
    End Sub

    Private Async Function VerifyDetailScrollHandoffAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim motion = _motionPreview, paused = _categoryMotion.PauseForChecks
        Dim forced = ModpackPage.ForceMotionForChecks, smooth = ModpackPage.SmoothScrollingEnabled
        Dim scrollPaused = ModpackPage.MotionPolicy.PauseForChecks, scrollOverride = ModpackPage.MotionPolicy.EnabledForChecks
        Dim offset = ModpackPage.VerticalOffset, searchOffset = _packSearchScroll, detailOffset = _packDetailScroll
        Dim session = _packSearchSession, project = _packProject, releases = _packReleases, generation = _taskGeneration
        Dim cards = PackVersionCards.Children.Cast(Of UIElement)().ToArray(), spacer As New Border With {.Height = 1800}
        Dim prompt As Task(Of String) = Nothing
        Try
            _motionPreview = True : _categoryMotion.PauseForChecks = True
            ModpackPage.ForceMotionForChecks = True : ModpackPage.MotionPolicy.PauseForChecks = True : ModpackPage.SmoothScrollingEnabled = True
            PackVersionCards.Children.Add(spacer) : ModpackPage.ScrollToHome() : layout()
            check(_packMode = PackBrowserMode.Detail AndAlso ModpackPage.ScrollableHeight > 500 AndAlso cards.Length > 0,
                "the actual detail page retains accepted version cards inside the scroll handoff fixture")
            ModpackPage.ScrollDelta(240)
            Dim old = ModpackPage.MotionClock
            SeekControl(old, 60)
            Dim before = ModpackPage.VerticalOffset
            prompt = PromptOnlinePackAsync("输入版本名称", "滚动交接检查", _selectedFolder.Location, "Scroll-Handoff", False, CancellationToken.None)
            _onlinePromptMotion.Controller.Pause() : layout()
            check(PackNameOverlay.IsVisible AndAlso Not PageSurface.IsEnabled AndAlso ModpackPage.MotionClock Is Nothing AndAlso Math.Abs(ModpackPage.VerticalOffset - before) < 0.1,
                "the real animated name dialog freezes background wheel motion when its page becomes disabled")
            RouteScrollWheel(ModpackPage, -120) : layout()
            check(ModpackPage.MotionClock Is Nothing AndAlso Math.Abs(ModpackPage.VerticalOffset - before) < 0.1,
                "wheel input routed during the real modal animation cannot move its background")
            CancelOnlinePackName(Nothing, Nothing)
            _onlinePromptMotion.Controller.SkipToFill() : Await WaitPackUiAsync(Function() _onlinePromptMotion Is Nothing)
            old.Controller.SkipToFill() : layout()
            check((Await prompt) Is Nothing AndAlso PageSurface.IsEnabled AndAlso Not PackNameOverlay.IsVisible AndAlso ModpackPage.MotionClock Is Nothing,
                "cancelled naming restores page interaction without reviving a retired wheel gesture")
            ModpackPage.ScrollDelta(80)
            Dim fresh = ModpackPage.MotionClock
            SeekControl(fresh, 30)
            before = ModpackPage.VerticalOffset
            check(fresh IsNot Nothing AndAlso before > 0, "a fresh wheel gesture is accepted after the real dialog closes")
            ChangePackMode(PackBrowserMode.Search) : layout()
            Dim leaving = _categoryMotion.Transition
            check(leaving IsNot Nothing AndAlso ModpackPage.MotionClock Is Nothing AndAlso _packMode = PackBrowserMode.Search,
                "detail departure hands scrolling to the existing page animation before restoring search")
            ChangePackMode(PackBrowserMode.Detail) : layout()
            Dim returning = _categoryMotion.Transition
            leaving.Controller.SkipToFill() : fresh.Controller.SkipToFill() : layout()
            check(returning IsNot Nothing AndAlso returning IsNot leaving AndAlso _categoryMotion.Transition Is returning AndAlso _packMode = PackBrowserMode.Detail,
                "old page and wheel completions cannot replace a rapid return to the accepted detail page")
            _categoryMotion.Finish() : layout()
            check(Math.Abs(ModpackPage.VerticalOffset - before) < 0.1 AndAlso ModpackPage.MotionClock Is Nothing AndAlso
                _packSearchSession Is session AndAlso _packProject Is project AndAlso _packReleases Is releases AndAlso _taskGeneration = generation AndAlso
                PackVersionCards.Children.Cast(Of UIElement)().Where(Function(item) item IsNot spacer).SequenceEqual(cards),
                "animated detail return preserves its viewport, accepted cards and installation ownership")
            IO.File.WriteAllText(IO.Path.Combine(directory, "detail-scroll-handoff.json"), New JObject From {
                {"restoredOffset", ModpackPage.VerticalOffset}, {"expectedOffset", before}, {"pageClockOwned", _categoryMotion.Transition IsNot Nothing},
                {"wheelClockOwned", ModpackPage.MotionClock IsNot Nothing}, {"taskUnchanged", _taskGeneration = generation}}.ToString())
        Finally
            CompleteOnlinePrompt(Nothing) : StopOnlinePromptMotion() : HideOnlinePrompt()
            _categoryMotion.Finish() : ModpackPage.StopScrolling()
            _motionPreview = motion : _categoryMotion.PauseForChecks = paused
            PackVersionCards.Children.Remove(spacer)
            ModpackPage.ForceMotionForChecks = forced : ModpackPage.MotionPolicy.EnabledForChecks = scrollOverride
            ModpackPage.MotionPolicy.PauseForChecks = scrollPaused : ModpackPage.SmoothScrollingEnabled = smooth
            _packSearchScroll = searchOffset : _packDetailScroll = detailOffset
            ModpackPage.ScrollToVerticalOffset(offset) : layout()
        End Try
    End Function
End Class
