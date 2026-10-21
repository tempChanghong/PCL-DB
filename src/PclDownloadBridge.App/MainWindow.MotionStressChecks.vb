Imports System.Diagnostics
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Shared Sub VerifyMotionStress(directory As String, check As Action(Of Boolean, String))
        VerifyWheelBurst(directory, check)
        VerifyCardRefreshMotion(directory, check)
        Dim integration As New MainWindow(True)
        integration.VerifyComponentRefreshMotion(check)
    End Sub

    Private Shared Sub VerifyWheelBurst(directory As String, check As Action(Of Boolean, String))
        Dim viewer As New PclScrollViewer With {.Content = New Border With {.Height = 100000}, .ForceMotionForChecks = True}
        Dim host As New Border With {.Width = 400, .Height = 200, .Child = viewer}
        Dim layout As Action = Sub()
                                   host.Measure(New Size(400, 200)) : host.Arrange(New Rect(0, 0, 400, 200))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        Using source = PreviewSurface.Attach(host, 400, 200)
            layout()
            Dim watch = Stopwatch.StartNew()
            For sampleIndex As Integer = 1 To 500
                viewer.ScrollDelta(1)
            Next
            watch.Stop()
            Dim clock = viewer.MotionClock
            Dim pulses = DirectCast(clock.Timeline, PclWheelAnimation).Inputs.Count
            IO.File.WriteAllText(IO.Path.Combine(directory, "wheel-burst.json"), New JObject From {
                {"inputs", 500}, {"pulses", pulses}, {"submissionMs", watch.Elapsed.TotalMilliseconds},
                {"sampleEnd", DirectCast(clock.Timeline, PclWheelAnimation).Sample(300)}, {"desktopFpsMeasured", False}}.ToString())
            check(pulses = 1, "same-frame high-frequency wheel input has bounded motion state without losing accumulated distance")
            clock.Controller.Pause() : layout()
            clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(301), TimeSeekOrigin.BeginTime) : layout()
            check(Math.Abs(viewer.VerticalOffset - 500) < 0.1 AndAlso viewer.MotionClock Is Nothing,
                "five hundred merged wheel inputs finish at the exact combined displacement")
            viewer.ScrollDelta(120)
            Dim aged = viewer.MotionClock
            aged.Controller.Pause() : layout()
            aged.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(60), TimeSeekOrigin.BeginTime) : layout()
            Dim ageMilliseconds = aged.CurrentTime.Value.TotalMilliseconds
            Dim origin = viewer.VerticalOffset
            For sampleIndex As Integer = 1 To 250
                viewer.ScrollDelta(1)
            Next
            clock = viewer.MotionClock
            Dim plan = DirectCast(clock.Timeline, PclWheelAnimation)
            IO.File.WriteAllText(IO.Path.Combine(directory, "wheel-aged.json"), New JObject From {
                {"originBeforeBurst", origin}, {"originInPlan", plan.Sample(0)}, {"offsetAfterBurst", viewer.VerticalOffset}, {"consumedAge", ageMilliseconds},
                {"pulses", New JArray(plan.Inputs.Select(Function(pulse) New JObject From {{"age", pulse.X}, {"delta", pulse.Y}}))}}.ToString())
            check(plan.Inputs.Count = 2 AndAlso plan.Inputs(0).X = ageMilliseconds AndAlso plan.Inputs(1).X = 0,
                "coalescing preserves separately aged inputs instead of restarting their easing or rounding time")
            For Each tick In {0.0, 15, 60, 150, 300}
                Dim expected = origin + 120 * (Math.Pow(1 - ageMilliseconds / 300, 6) - Math.Pow(1 - Math.Min(1, (ageMilliseconds + tick) / 300), 6)) +
                    250 * (1 - Math.Pow(1 - tick / 300, 6))
                check(Math.Abs(plan.Sample(tick) - expected) < 0.000001, "merged wheel displacement follows the unmodified PCL superposition at " & tick)
            Next
            clock.Controller.Pause() : layout()
            aged.Controller.SkipToFill() : layout()
            check(viewer.MotionClock Is clock, "superseded burst completion cannot cancel the merged gesture")
            clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(301), TimeSeekOrigin.BeginTime) : layout()
            check(Math.Abs(viewer.VerticalOffset - 870) < 0.1, "aged and same-frame wheel bursts retain their total displacement")
            viewer.ScrollDelta(80) : viewer.ScrollDelta(-80) : layout()
            check(viewer.MotionClock Is Nothing AndAlso Math.Abs(viewer.VerticalOffset - 870) < 0.1,
                "opposite wheel inputs in one frame cancel without retaining a no-op animation")
        End Using
    End Sub

    Private Shared Sub VerifyCardRefreshMotion(directory As String, check As Action(Of Boolean, String))
        Dim card As New PclCard With {.Title = "组件列表刷新", .Collapsible = True, .Collapsed = True,
            .BodyMargin = New Thickness(0, 40, 0, 0), .Body = New Border With {.Height = 5000, .Background = Brushes.AliceBlue, .VerticalAlignment = VerticalAlignment.Top}}
        ' Real component cards flow from the top of a StackPanel; a standalone
        ' fixed-height child of ScrollViewer would be centered as it shrinks.
        Dim rows As New StackPanel()
        rows.Children.Add(card)
        Dim viewer As New PclScrollViewer With {.Content = rows}
        Dim host As New Border With {.Width = 500, .Height = 450, .Child = viewer}
        Dim layout As Action = Sub()
                                   host.Measure(New Size(500, 450)) : host.Arrange(New Rect(0, 0, 500, 450))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        Using source = PreviewSurface.Attach(host, 500, 450)
            layout() : card.Collapsed = False
            Dim opening = card.HeightTransition
            opening.Controller.Pause() : layout()
            opening.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(100), TimeSeekOrigin.BeginTime) : layout()
            Dim height = card.ActualHeight, top = card.TranslatePoint(New Point(), host).Y
            Dim built = 0
            card.RefreshBody(Function()
                                 built += 1
                                 Return New Border With {.Height = 200, .Background = Brushes.AliceBlue, .VerticalAlignment = VerticalAlignment.Top,
                                     .Child = New TextBlock With {.Text = "新组件列表 · 200 DIP", .Margin = New Thickness(20), .VerticalAlignment = VerticalAlignment.Top}}
                             End Function)
            Dim refreshed = card.HeightTransition
            IO.File.WriteAllText(IO.Path.Combine(directory, "card-refresh-state.json"), New JObject From {
                {"displayedHeight", height}, {"clockReplaced", refreshed IsNot opening},
                {"oldTarget", DirectCast(opening.Timeline, DoubleAnimationUsingKeyFrames).KeyFrames.Cast(Of DoubleKeyFrame)().Last().Value},
                {"newTarget", DirectCast(refreshed.Timeline, DoubleAnimationUsingKeyFrames).KeyFrames.Cast(Of DoubleKeyFrame)().Last().Value}}.ToString())
            check(refreshed IsNot opening AndAlso DirectCast(refreshed.Timeline, DoubleAnimationUsingKeyFrames).KeyFrames.Cast(Of DoubleKeyFrame)().Last().Value = 240,
                "a component body refresh replaces an in-flight expansion target instead of finishing at the old list height")
            refreshed.Controller.Pause() : layout()
            refreshed.Controller.SeekAlignedToLastTick(TimeSpan.Zero, TimeSeekOrigin.BeginTime) : layout()
            check(Math.Abs(DirectCast(refreshed.Timeline, DoubleAnimationUsingKeyFrames).KeyFrames(0).Value - height) < 0.01 AndAlso
                Math.Abs(card.ActualHeight - SampleCardMotion(DirectCast(refreshed.Timeline, DoubleAnimationUsingKeyFrames), refreshed.CurrentTime.Value.TotalMilliseconds)) < 0.01 AndAlso built = 1 AndAlso card.ClipToBounds,
                "retargeting starts at the displayed card height and builds the new content only once")
            opening.Controller.SkipToFill() : layout()
            check(card.HeightTransition Is refreshed AndAlso Math.Abs(card.ActualHeight - SampleCardMotion(DirectCast(refreshed.Timeline, DoubleAnimationUsingKeyFrames), refreshed.CurrentTime.Value.TotalMilliseconds)) < 0.01,
                "the discarded expansion cannot clear the replacement clock or jump to its obsolete height")
            Dim frames = IO.Path.Combine(directory, "card-refresh-frames")
            IO.Directory.CreateDirectory(frames)
            Dim anchored = True
            For frame = 0 To 14
                refreshed.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(frame * 10), TimeSeekOrigin.BeginTime) : layout()
                anchored = anchored AndAlso Math.Abs(card.TranslatePoint(New Point(), host).Y - top) < 0.01
                SaveControlVisual(host, IO.Path.Combine(frames, frame.ToString("0000") & ".png"))
            Next
            refreshed.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(75), TimeSeekOrigin.BeginTime) : layout()
            Dim middle = card.ActualHeight
            check(anchored AndAlso Math.Abs(middle - SampleCardMotion(DirectCast(refreshed.Timeline, DoubleAnimationUsingKeyFrames), refreshed.CurrentTime.Value.TotalMilliseconds)) < 0.01,
                "a refreshed card keeps PCL's fifth-power easing and anchored list position without a discontinuity")
            card.RefreshBody(Function() New Border With {.Height = 8000})
            Dim enlarged = card.HeightTransition
            enlarged.Controller.Pause() : layout()
            enlarged.Controller.SeekAlignedToLastTick(TimeSpan.Zero, TimeSeekOrigin.BeginTime) : layout()
            check(enlarged IsNot refreshed AndAlso Math.Abs(DirectCast(enlarged.Timeline, DoubleAnimationUsingKeyFrames).KeyFrames(0).Value - middle) < 0.01 AndAlso
                Math.Abs(card.ActualHeight - SampleCardMotion(DirectCast(enlarged.Timeline, DoubleAnimationUsingKeyFrames), enlarged.CurrentTime.Value.TotalMilliseconds)) < 0.01,
                "a second refresh reverses a running resize from its current displayed height")
            refreshed.Controller.SkipToFill() : layout()
            check(card.HeightTransition Is enlarged, "a retired resize completion cannot finish a second replacement")
            enlarged.Controller.SeekAlignedToLastTick(enlarged.Timeline.Duration.TimeSpan + TimeSpan.FromMilliseconds(1), TimeSeekOrigin.BeginTime) : layout()
            check(card.HeightTransition Is Nothing AndAlso Double.IsNaN(card.Height) AndAlso card.ActualHeight = 8040 AndAlso Not card.ClipToBounds,
                "the latest refreshed body settles to auto height without clipping or a stale animation")
            card.RefreshBody(Function() New Border With {.Height = 400})
            Dim stable = card.HeightTransition
            check(stable IsNot Nothing AndAlso DirectCast(stable.Timeline, DoubleAnimationUsingKeyFrames).KeyFrames.Cast(Of DoubleKeyFrame)().Last().Value = 440,
                "refreshing a settled expanded card animates its content resize like PCL's SizeChanged path")
            stable.Controller.SkipToFill() : layout()
            card.Collapsed = True
            Dim folding = card.HeightTransition
            folding.Controller.Pause() : layout()
            Dim rotation = DirectCast(card.Children.OfType(Of Grid)().Single().Children.OfType(Of Shapes.Path)().Single().RenderTransform, RotateTransform)
            card.SwapLogoRight = True
            check(rotation.Angle = 270 AndAlso Not rotation.HasAnimatedProperties AndAlso card.HeightTransition Is folding,
                "changing arrow orientation stops only its old rotation and preserves the independent height motion")
            viewer.Visibility = Visibility.Collapsed : layout()
            check(card.HeightTransition Is Nothing AndAlso card.Height = 40 AndAlso Not card.ClipToBounds AndAlso Not rotation.HasAnimatedProperties,
                "hiding a folding card releases height and arrow clocks without waiting for Unloaded")
            card.Collapsed = False : layout()
            check(card.HeightTransition Is Nothing AndAlso Double.IsNaN(card.Height) AndAlso rotation.Angle = 180 AndAlso Not rotation.HasAnimatedProperties,
                "hidden metadata and fold changes select their final state without starting invisible animations")
            viewer.Visibility = Visibility.Visible : layout()
            check(card.HeightTransition Is Nothing AndAlso card.ActualHeight = 440,
                "showing the card cannot revive hidden motion or restore an obsolete content height")
            card.RefreshBody(Function() New Border With {.Height = 400.8}) : layout()
            check(card.HeightTransition Is Nothing AndAlso Math.Abs(card.ActualHeight - 440.8) < 0.01,
                "sub-DIP content changes retain PCL's noise threshold without starting a resize clock")
            Dim fixed As New PclCard With {.Height = 123, .Body = New Border With {.Height = 100}}
            viewer.Content = fixed : layout()
            fixed.RefreshBody(Function() New Border With {.Height = 1000}) : layout()
            check(fixed.Height = 123 AndAlso fixed.HeightTransition Is Nothing,
                "body refresh preserves an explicitly fixed card height")
        End Using
    End Sub

    Private Sub VerifyComponentRefreshMotion(check As Action(Of Boolean, String))
        SelectVersion(_versions.Single(Function(item) item.Id = "1.20.1"))
        Dim entry = _selectedEntry
        Dim releases = Enumerable.Range(0, 60).Select(Function(index) New Core.ComponentEntry With {
            .Kind = Core.ComponentKind.Fabric, .Minecraft = entry.Id, .Version = "0.16." & index}).ToList()
        AcceptComponentList(Core.ComponentKind.Fabric, entry, _componentGeneration, releases)
        Dim content = DirectCast(Me.Content, FrameworkElement)
        Dim host As New Border With {.Width = 850, .Height = 600}
        Dim layout As Action = Sub()
                                   host.Measure(New Size(850, 600)) : host.Arrange(New Rect(0, 0, 850, 600))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        Me.Content = Nothing : host.Child = content
        Using source = PreviewSurface.Attach(host, 850, 600)
            Try
                layout()
                Dim card = _componentViews(Core.ComponentKind.Fabric).Card
                card.Collapsed = False
                Dim old = card.HeightTransition
                old.Controller.Pause() : layout()
                old.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(75), TimeSeekOrigin.BeginTime) : layout()
                Dim height = card.ActualHeight
                InstanceName.Text = "组件动画刷新保留名称"
                AcceptComponentList(Core.ComponentKind.Fabric, entry, _componentGeneration, releases.Take(3).ToList())
                Dim accepted = card.HeightTransition
                accepted.Controller.Pause() : layout()
                accepted.Controller.SeekAlignedToLastTick(TimeSpan.Zero, TimeSeekOrigin.BeginTime) : layout()
                check(accepted IsNot old AndAlso Math.Abs(card.ActualHeight - height) < 0.01 AndAlso _componentEntries(Core.ComponentKind.Fabric).Count = 3,
                    "the real component-response handler retargets an opening list at the current height")
                old.Controller.SkipToFill() : layout()
                check(card.HeightTransition Is accepted AndAlso _selectedEntry Is entry AndAlso InstanceName.Text = "组件动画刷新保留名称",
                    "component refresh motion keeps the selected Minecraft version and edited instance name")
            Finally
                source.RootVisual = Nothing : host.Child = Nothing : Me.Content = content
            End Try
        End Using
    End Sub
End Class
