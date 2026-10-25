Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports System.Windows.Input
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Public Function VerifyMotionUi(directory As String, passed As List(Of String)) As List(Of String)
        If Not _preview Then Throw New InvalidOperationException("Motion checks require isolated preview mode.")
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     If Not ok Then Throw New InvalidOperationException(label)
                                                     passed.Add(label)
                                                 End Sub
        VerifyVersionRowOwnership(directory, check)
        VerifyCardGestureOwnership(directory, check)
        VerifyScrollInteractionOwnership(directory, check)
        For Each time In {0.0, 35, 100, 175, 250, 350}
            Dim expected = -16 + 5 * (1 - Math.Pow(1 - Math.Min(1, time / 250), 3)) +
                11 * (1 - Math.Pow(1 - Math.Min(1, time / 350), 1.5) * Math.Cos(1.5 * Math.PI * Math.Min(1, time / 350)))
            check(Math.Abs(PclMotionCurves.PageY(time) - expected) < 0.000001, "PCL parallel fluent and cosine-back displacement at " & time)
        Next
        Dim wheel As New PclWheelAnimation(70, {New Point(50, 120), New Point(0, -40)})
        Dim clone = DirectCast(wheel.Clone(), PclWheelAnimation) : clone.Freeze()
        check(clone.IsFrozen AndAlso clone.Sample(300) = wheel.Sample(300) AndAlso clone.Inputs.Count = 2,
            "wheel animation preserves remaining increments when WPF clones and freezes it")
        Dim fixture As New MainWindow(True)
        fixture.VerifyLiveMotion(directory, check)
        VerifyWheelMotion(directory, check)
        VerifyMotionOwnership(check)
        Dim secondRound As New MainWindow(True)
        secondRound.VerifyExitAndBarMotion(directory, check)
        VerifySnapshotViewport(directory, check)
        Dim finalization As New MainWindow(True)
        finalization.VerifyMotionFinalization(directory, check)
        Dim about As New MainWindow(True)
        about.VerifyAboutMotion(directory, check)
        VerifyMotionStress(directory, check)
        VerifyMotionLayout(directory, check)
        VerifyPressMotion(directory, check)
        VerifySelectionMotion(directory, check)
        VerifyListSelectionMotion(directory, check)
        Dim folding As New MainWindow(True)
        folding.VerifyComponentFoldRefresh(directory, check)
        VerifyClippedLazyRows(check)
        Dim downloads As New MainWindow(True)
        downloads.VerifyDownloadCategoryMotion(directory, check)
        VerifyTaskPageMotion(directory, passed)
        VerifyTaskReturnFocus(directory, passed)
        VerifyInstallActionHandoff(directory, passed)
        VerifyInstallPressMotion(directory, passed)
        VerifyInstallActionMotion(directory, check)
        VerifyVanillaCardMotion(directory, check)
        VerifyControlMotionUi(directory, passed)
        VerifyTitleNavigation(check, directory, True)
        VerifyManagementMotion(directory, check)
        VerifyLocalModMotion(directory, check)
        VerifyLocalModFollowupMotion(directory, check)
        Return passed
    End Function

    Private Sub VerifyLiveMotion(directory As String, check As Action(Of Boolean, String))
        Dim content = DirectCast(Me.Content, FrameworkElement)
        Dim host As New Border With {.Width = 850, .Height = 600}
        Dim source As Interop.HwndSource = Nothing
        Dim layout As Action = Sub()
                                   host.Measure(New Size(850, 600)) : host.Arrange(New Rect(0, 0, 850, 600))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        Dim seek As Action(Of ClockGroup, Double) = Sub(clock, time)
                                                        clock.Controller.Pause() : layout()
                                                        clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(time), TimeSeekOrigin.BeginTime) : layout()
                                                    End Sub
        Try
            Me.Content = Nothing : host.Child = content
            source = PreviewSurface.Attach(host, 850, 600) : layout()
            check(_surfaceMotion.Transition Is Nothing, "ordinary fixture rendering remains deterministic and has no surface clock")
            _motionPreview = True
            Dim first As FrameworkElement = Nothing, original As Transform = Nothing
            SelectSurface(SettingsPage, Sub()
                                            first = SurfaceUnits(SettingsPage).First()
                                            original = first.RenderTransform
                                        End Sub)
            Dim clock = _surfaceMotion.Transition
            clock?.Controller.Pause() : layout()
            IO.File.WriteAllText(IO.Path.Combine(directory, "surface-state.txt"), String.Join(";", {"clock=" & (clock IsNot Nothing), "settings=" & SettingsPage.IsVisible, "download=" & DownloadPage.IsVisible, "ghost=" & PageGhost.Children.Count, "items=" & _surfaceMotion.ItemCount, "loaded=" & PageSurface.IsLoaded, "visible=" & PageSurface.IsVisible, "time=" & clock?.CurrentTime.ToString(), "parts=" & DirectCast(clock.Timeline, ParallelTimeline).Children.Count}))
            check(clock IsNot Nothing AndAlso SettingsPage.IsVisible AndAlso Not DownloadPage.IsVisible AndAlso PageGhost.Children.Count > 1,
                "page ownership changes immediately while an inert outgoing image is retained")
            Dim plan = DirectCast(clock.Timeline, ParallelTimeline)
            check(_surfaceMotion.ItemCount > 1 AndAlso _surfaceMotion.ItemCount <= 16 AndAlso plan.Children.OfType(Of PclPageYAnimation)().Count() = _surfaceMotion.ItemCount,
                "only bounded visible page units receive entry clocks")
            Dim entries = plan.Children.OfType(Of PclPageYAnimation)().ToArray()
            check(entries(0).BeginTime.Value.TotalMilliseconds = 140 AndAlso entries(1).BeginTime.Value.TotalMilliseconds = 165,
                "entry follows the PCL 110 plus 30 ms page delay and 25 ms item stagger")
            seek(clock, 20)
            IO.File.AppendAllText(IO.Path.Combine(directory, "surface-state.txt"), ";firstOpacity=" & first.Opacity & ";input=" & PageSurface.IsHitTestVisible & ";clock=" & clock.CurrentTime.ToString())
            check(first.Opacity = 0 AndAlso Not PageSurface.IsHitTestVisible, "incoming content stays hidden during the outgoing fade without a one-frame flash")
            check(Math.Abs(DirectCast(DirectCast(PageGhost.Children(0), Canvas).Children(0), Image).Opacity - (1 - 20.0 / 70)) < 0.01,
                "outgoing snapshot opacity follows the PCL seventy millisecond linear exit")
            seek(clock, 120)
            check(PageGhost.Children.Count = 0 AndAlso first.Opacity = 0, "outgoing image is released before incoming cards appear")
            seek(clock, 190)
            Dim offset = DirectCast(first.RenderTransform, TransformGroup).Children.OfType(Of TranslateTransform)().Last()
            check(Math.Abs(first.Opacity - 0.75) < 0.01 AndAlso Math.Abs(offset.Y - PclMotionCurves.PageY(50)) < 0.01 AndAlso PageSurface.IsHitTestVisible,
                "live WPF card opacity and displacement match source curves and restore pointer input")
            Dim retained = _surfaceMotion.Transition
            SwitchMainPage(True)
            check(_surfaceMotion.Transition Is retained, "same-page selection does not replay the entry")
            Dim focused = False
            _pendingSurfaceFocus = Sub() focused = True
            seek(clock, 1200)
            check(_surfaceMotion.Transition Is Nothing AndAlso first.RenderTransform Is original AndAlso first.Opacity = 1 AndAlso focused,
                "settling restores the original transform and opacity and consumes deferred keyboard focus")
            SettingsPage.ScrollToVerticalOffset(160) : layout()
            Dim saved = SettingsPage.VerticalOffset
            ShowAbout(Me, New RoutedEventArgs()) : layout()
            Dim old = _surfaceMotion.Transition
            seek(old, 190)
            BackFromAbout(Me, New RoutedEventArgs()) : layout()
            clock = _surfaceMotion.Transition
            check(clock IsNot old AndAlso Math.Abs(SettingsPage.VerticalOffset - saved) < 1,
                "a rapid about-page return restores settings scrolling before choosing entry units")
            old.Controller.SkipToFill() : layout()
            check(_surfaceMotion.Transition Is clock, "completion from a superseded page cannot settle its replacement")
            _surfaceMotion.Finish()
            SwitchMainPage(False) : _surfaceMotion.Finish() : layout()
            Dim version = _versions.Single(Function(item) item.Id = "1.20.1")
            SelectVersion(version) : FinishPageSwitchForCheck(layout)
            InstanceName.Text = "动画保留方案"
            Dim body = _componentViews(Core.ComponentKind.Fabric).Card.Body
            SwitchMainPage(True) : SwitchMainPage(False) : layout()
            check(_selectedEntry Is version AndAlso InstanceName.Text = "动画保留方案" AndAlso _componentViews(Core.ComponentKind.Fabric).Card.Body Is body AndAlso _pageSwitch.Transition Is Nothing,
                "surface changes preserve the install plan and settle the independent horizontal page animation")
            _surfaceMotion.Finish()
            NavigateToSetting(SpeedLimitBox, True) : layout()
            check(_pendingSurfaceFocus IsNot Nothing AndAlso SpeedLimitBox.IsVisible,
                "settings validation locates the target before entry and defers its focus")
            _surfaceMotion.Finish()
            ' Deterministic timeline samples are a visual preview, not a desktop frame-rate measurement.
            Dim frames = IO.Path.Combine(directory, "motion-frames")
            IO.Directory.CreateDirectory(frames)
            SwitchMainPage(False) : _surfaceMotion.Finish() : layout()
            SettingsPage.ScrollToTop() : layout()
            Dim frame = 0
            For stage = 0 To 2
                If stage = 0 Then
                    SwitchMainPage(True)
                ElseIf stage = 1 Then
                    ShowAbout(Me, New RoutedEventArgs())
                Else
                    BackFromAbout(Me, New RoutedEventArgs())
                End If
                layout() : clock = _surfaceMotion.Transition
                For tick = 0 To 29
                    If _surfaceMotion.Transition Is clock Then seek(clock, tick * (1000.0 / 30))
                    SaveControlVisual(host, IO.Path.Combine(frames, frame.ToString("0000") & ".png"))
                    frame += 1
                Next
                _surfaceMotion.Finish()
            Next
            check(frame = 90, "three isolated page-transition sequences export ninety inspectable timeline frames")
            SwitchMainPage(False) : layout()
            source.RootVisual = Nothing : FlushControlLayout()
            check(_surfaceMotion.Transition Is Nothing AndAlso PageGhost.Children.Count = 0 AndAlso PageSurface.IsHitTestVisible,
                "unloading releases page clocks images and input gates")
            _motionPreview = False
            SwitchMainPage(True)
            check(_surfaceMotion.Transition Is Nothing AndAlso SettingsPage.Visibility = Visibility.Visible,
                "disabled or detached motion selects its final page immediately")
        Finally
            _pendingSurfaceFocus = Nothing : _surfaceMotion.Finish()
            If source IsNot Nothing Then
                source.RootVisual = Nothing : source.Dispose()
            End If
            host.Child = Nothing : Me.Content = content
        End Try
    End Sub

    Private Shared Sub VerifyWheelMotion(directory As String, check As Action(Of Boolean, String))
        Dim body As New StackPanel()
        Dim singleLine As New TextBox(), multiLine As New TextBox With {.AcceptsReturn = True}
        Dim combo As New ComboBox(), nested As New ScrollViewer With {.Height = 40, .Content = New Border With {.Height = 500}}
        body.Children.Add(singleLine) : body.Children.Add(multiLine) : body.Children.Add(combo) : body.Children.Add(nested)
        body.Children.Add(New Border With {.Height = 3000})
        Dim viewer As New PclScrollViewer With {.Content = body, .Height = 200, .ForceMotionForChecks = True, .CanContentScroll = False}
        Dim host As New Border With {.Width = 400, .Height = 200, .Child = viewer}
        Dim layout As Action = Sub()
                                   host.Measure(New Size(400, 200)) : host.Arrange(New Rect(0, 0, 400, 200))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        Dim seek As Action(Of AnimationClock, Double) = Sub(clock, time)
                                                            clock.Controller.Pause() : layout()
                                                            clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(time), TimeSeekOrigin.BeginTime) : layout()
                                                        End Sub
        Using source = PreviewSurface.Attach(host, 400, 200)
            layout()
            check(viewer.ScrollableHeight > 2000, "smooth-wheel fixture has a real pixel-scrolling viewport")
            check(viewer.OwnsWheel(singleLine) AndAlso Not viewer.OwnsWheel(multiLine) AndAlso Not viewer.OwnsWheel(nested),
                "wheel routing preserves multiline editing and independently scrollable nested controls")
            combo.IsDropDownOpen = True : layout()
            check(Not viewer.OwnsWheel(combo) AndAlso Not viewer.OwnsWheel(New ComboBoxItem()), "open selectors and popup items retain wheel ownership")
            combo.IsDropDownOpen = False
            viewer.ScrollDelta(120)
            check(PumpMotionUntil(Function() viewer.MotionClock Is Nothing) AndAlso Math.Abs(viewer.VerticalOffset - 120) < 0.2,
                "an unpaused wheel clock reaches its displacement without cancelling on its own scroll events")
            viewer.ScrollToTop() : layout()
            viewer.ScrollDelta(120)
            Dim clock = viewer.MotionClock
            check(clock IsNot Nothing AndAlso clock.Timeline.Duration.TimeSpan.TotalMilliseconds = 300, "a wheel input creates the source three hundred millisecond motion")
            seek(clock, 60)
            Dim offset = viewer.VerticalOffset
            IO.File.WriteAllText(IO.Path.Combine(directory, "wheel-sample.txt"), "offset=" & offset & ";clock=" & clock.CurrentTime.ToString())
            check(Math.Abs(offset - 120 * (1 - Math.Pow(1 - clock.CurrentTime.Value.TotalMilliseconds / 300, 6))) < 0.1, "real scrolling follows PCL's sixth-power ease-out")
            viewer.ScrollDelta(120)
            Dim replacement = viewer.MotionClock
            replacement.Controller.Pause()
            check(replacement IsNot clock AndAlso DirectCast(replacement.Timeline, PclWheelAnimation).Inputs.Count = 2 AndAlso Math.Abs(viewer.VerticalOffset - offset) < 0.1,
                "continuous wheel input retains remaining increments without jumping or restarting the old easing")
            clock.Controller.SkipToFill() : layout()
            check(viewer.MotionClock Is replacement, "stale wheel completion cannot remove the newer input")
            seek(replacement, 301)
            check(Math.Abs(viewer.VerticalOffset - 240) < 0.2 AndAlso viewer.MotionClock Is Nothing, "overlapping wheel inputs settle at their combined displacement and release the clock")
            viewer.ScrollDelta(-120) : seek(viewer.MotionClock, 301)
            check(Math.Abs(viewer.VerticalOffset - 120) < 0.2, "reversed input retains the source displacement sign")
            viewer.ScrollDelta(-10000) : seek(viewer.MotionClock, 301)
            check(viewer.VerticalOffset = 0 AndAlso viewer.MotionClock Is Nothing, "wheel scrolling clamps at the top boundary")
            viewer.ScrollDelta(10000) : seek(viewer.MotionClock, 301)
            check(Math.Abs(viewer.VerticalOffset - viewer.ScrollableHeight) < 0.1, "wheel scrolling clamps at the bottom boundary")
            viewer.ScrollDelta(-120) : seek(viewer.MotionClock, 30)
            viewer.ScrollToVerticalOffset(200) : layout()
            check(viewer.MotionClock Is Nothing AndAlso Math.Abs(viewer.VerticalOffset - 200) < 0.1, "manual or programmatic positioning cancels the wheel clock")
            viewer.ScrollDelta(120) : layout()
            viewer.RaiseEvent(New MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) With {.RoutedEvent = Mouse.PreviewMouseDownEvent})
            check(viewer.MotionClock Is Nothing, "pointer manipulation releases the wheel clock before scrollbar dragging")
            viewer.ScrollDelta(120) : layout()
            viewer.RaiseEvent(New KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.PageDown) With {.RoutedEvent = Keyboard.PreviewKeyDownEvent})
            check(viewer.MotionClock Is Nothing, "keyboard navigation cancels smooth scrolling before native page commands")
            ' Layout can take longer than the 300 ms gesture on a busy machine.
            ' Verify natural completion separately, then hold the active gesture
            ' at a known phase while testing an extent change.
            viewer.ScrollDelta(120)
            Dim completedBeforeExtent = False
            AddHandler viewer.MotionClock.Completed, Sub() completedBeforeExtent = True
            Dim settledBeforeExtent = PumpMotionUntil(Function() viewer.MotionClock Is Nothing)
            body.Children.Add(New Border With {.Height = 300}) : layout()
            Dim settledStayedStopped = viewer.MotionClock Is Nothing
            viewer.ScrollDelta(120)
            Dim extentClock = viewer.MotionClock
            seek(extentClock, 60)
            Dim extentBefore = viewer.ExtentHeight, offsetBefore = viewer.VerticalOffset
            body.Children.Add(New Border With {.Height = 300}) : layout()
            Dim activePreserved = viewer.MotionClock Is extentClock AndAlso extentClock.IsPaused AndAlso viewer.ExtentHeight > extentBefore AndAlso Math.Abs(viewer.VerticalOffset - offsetBefore) < 0.2
            IO.File.WriteAllText(IO.Path.Combine(directory, "wheel-extent-timing.json"), Newtonsoft.Json.JsonConvert.SerializeObject(New With {
                .NaturalCompletion = completedBeforeExtent, .Settled = settledBeforeExtent, .SettledStayedStopped = settledStayedStopped,
                .ActivePreserved = activePreserved, .PausedTime = extentClock.CurrentTime, .OffsetBefore = offsetBefore, .OffsetAfter = viewer.VerticalOffset}, Newtonsoft.Json.Formatting.Indented))
            check(completedBeforeExtent AndAlso settledBeforeExtent AndAlso settledStayedStopped AndAlso activePreserved, "a content extent change preserves active wheel input while leaving a naturally completed gesture stopped")
            viewer.SmoothScrollingEnabled = False : viewer.ScrollDelta(120) : layout()
            check(viewer.MotionClock Is Nothing, "disabled scrolling uses immediate positioning")
            viewer.SmoothScrollingEnabled = True : viewer.ScrollDelta(120) : layout()
            source.RootVisual = Nothing : FlushControlLayout()
            check(viewer.MotionClock Is Nothing, "unloading releases the smooth-scroll animation")
        End Using
    End Sub

    Private Shared Sub VerifyMotionOwnership(check As Action(Of Boolean, String))
        Dim card As New PclCard With {.Title = "共存检查", .Body = New Border With {.Height = 240}, .Collapsible = True}
        Dim transform As New ScaleTransform(0.98, 0.98)
        card.RenderTransform = transform
        Dim built = 0
        Dim lazy As New PclCard With {.Title = "延迟构建", .Collapsed = True, .Collapsible = True,
            .Builder = Function()
                           built += 1
                           Return New TextBlock With {.Text = "延迟内容"}
                       End Function}
        Dim content As New StackPanel()
        content.Children.Add(card) : content.Children.Add(lazy)
        For i = 1 To 1200
            content.Children.Add(New TextBlock With {.Text = "离屏项 " & i, .Height = 30})
        Next
        Dim scroll As New PclScrollViewer With {.Content = content}
        Dim page As New Grid(), overlay As New Canvas With {.IsHitTestVisible = False}
        page.Children.Add(scroll) : page.Children.Add(overlay)
        Dim motion As New PclSurfaceMotion(page, overlay, Function() True, Function(root) PclSurfaceMotion.Units(root))
        Using source = PreviewSurface.Attach(page, 500, 450)
            page.Measure(New Size(500, 450)) : page.Arrange(New Rect(0, 0, 500, 450)) : page.UpdateLayout() : FlushControlLayout()
            motion.Change(Nothing, scroll, Sub()
                                          End Sub)
            Dim clock = motion.Transition
            clock.Controller.Pause() : FlushControlLayout()
            check(motion.ItemCount >= 2 AndAlso motion.ItemCount <= 16 AndAlso built = 0 AndAlso lazy.Body Is Nothing,
                "a twelve-hundred-item page animates only visible units without materializing collapsed card bodies")
            check(DirectCast(card.RenderTransform, TransformGroup).Children(0) Is transform,
                "page entry composes with an existing control transform")
            card.Collapsed = True
            Dim folding = card.HeightTransition
            check(folding IsNot Nothing, "card height folding can start while its page entry is active")
            motion.Finish()
            check(card.RenderTransform Is transform AndAlso card.HeightTransition Is folding AndAlso card.Collapsed,
                "finishing page entry restores the original transform without removing the card folding clock")
            motion.Change(Nothing, scroll, Sub()
                                          End Sub)
            check(PumpMotionUntil(Function() motion.Transition Is Nothing) AndAlso card.RenderTransform Is transform AndAlso card.Opacity = 1,
                "an unpaused page entry settles and removes its animation without a manual seek")
            source.RootVisual = Nothing : FlushControlLayout()
            check(motion.Transition Is Nothing AndAlso card.HeightTransition Is Nothing,
                "unloading independently releases both surface and card animation ownership")
        End Using
    End Sub

    Private Shared Function PumpMotionUntil(done As Func(Of Boolean), Optional timeoutMilliseconds As Integer = 2000) As Boolean
        Dim frame As New Threading.DispatcherFrame()
        Dim watch = System.Diagnostics.Stopwatch.StartNew()
        Dim timer As New Threading.DispatcherTimer(Threading.DispatcherPriority.Background) With {.Interval = TimeSpan.FromMilliseconds(10)}
        AddHandler timer.Tick, Sub()
                                   If done() OrElse watch.ElapsedMilliseconds >= timeoutMilliseconds Then frame.Continue = False
                               End Sub
        Try
            timer.Start() : Threading.Dispatcher.PushFrame(frame)
            Return done()
        Finally
            timer.Stop()
        End Try
    End Function
End Class
