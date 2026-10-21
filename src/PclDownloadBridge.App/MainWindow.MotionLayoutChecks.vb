Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Shared Sub VerifyMotionLayout(directory As String, check As Action(Of Boolean, String))
        VerifyWheelDuringLayout(directory, check)
        VerifyCardWidthMotion(directory, check)
    End Sub

    Private Shared Sub VerifyWheelDuringLayout(directory As String, check As Action(Of Boolean, String))
        Dim card As New PclCard With {.Collapsible = True, .Collapsed = True, .Body = New Border With {.Height = 5000}}
        Dim rows As New StackPanel()
        rows.Children.Add(New Border With {.Height = 1000}) : rows.Children.Add(card) : rows.Children.Add(New Border With {.Height = 1000})
        Dim viewer As New PclScrollViewer With {.Content = rows, .ForceMotionForChecks = True}
        Dim host As New Border With {.Width = 500, .Height = 400, .Child = viewer}
        Dim layout As Action = Sub()
                                   host.Measure(New Size(host.Width, host.Height)) : host.Arrange(New Rect(0, 0, host.Width, host.Height))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        Using source = PreviewSurface.Attach(host, 500, 400)
            layout() : viewer.ScrollToVerticalOffset(200) : layout()
            card.Collapsed = False
            Dim expanding = card.HeightTransition
            expanding.Controller.Pause() : layout()
            expanding.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(80), TimeSeekOrigin.BeginTime) : layout()
            viewer.ScrollDelta(120)
            Dim scrolling = viewer.MotionClock
            scrolling.Controller.Pause() : layout()
            scrolling.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(60), TimeSeekOrigin.BeginTime) : layout()
            Dim offset = viewer.VerticalOffset
            expanding.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(100), TimeSeekOrigin.BeginTime) : layout()
            IO.File.WriteAllText(IO.Path.Combine(directory, "wheel-layout-state.json"), New JObject From {
                {"clockRetained", viewer.MotionClock Is scrolling}, {"offsetBefore", offset}, {"offsetAfter", viewer.VerticalOffset},
                {"extent", viewer.ExtentHeight}, {"cardHeight", card.ActualHeight}}.ToString())
            check(viewer.MotionClock Is scrolling AndAlso Math.Abs(viewer.VerticalOffset - offset) < 0.01,
                "card expansion changes the scroll extent without cancelling the wheel gesture or jumping its offset")
            scrolling.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(120), TimeSeekOrigin.BeginTime) : layout()
            check(Math.Abs(viewer.VerticalOffset - (200 + 120 * (1 - Math.Pow(1 - scrolling.CurrentTime.Value.TotalMilliseconds / 300, 6)))) < 0.01,
                "a wheel gesture continues along its original curve after the card extent grows")
            card.Collapsed = True
            Dim collapsing = card.HeightTransition
            collapsing.Controller.Pause() : layout()
            collapsing.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(75), TimeSeekOrigin.BeginTime) : layout()
            check(viewer.MotionClock Is scrolling, "a simultaneous card collapse also retains the remaining wheel gesture")
            scrolling.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(301), TimeSeekOrigin.BeginTime) : layout()
            check(Math.Abs(viewer.VerticalOffset - 320) < 0.01 AndAlso viewer.MotionClock Is Nothing,
                "expansion and collapse do not discard part of a wheel input's final distance")

            Dim spacer As New Border With {.Height = 3000}
            viewer.Content = spacer : layout()
            viewer.ScrollToEnd() : layout()
            viewer.ScrollDelta(-120)
            scrolling = viewer.MotionClock : scrolling.Controller.Pause() : layout()
            scrolling.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(60), TimeSeekOrigin.BeginTime) : layout()
            spacer.Height = 1000 : layout()
            check(viewer.MotionClock Is scrolling AndAlso Math.Abs(viewer.VerticalOffset - viewer.ScrollableHeight) < 0.01,
                "native clamping after content shrink retains a gesture instead of mistaking the clamp for external navigation")
            Dim boundary = viewer.ScrollableHeight
            Dim requestField = GetType(PclScrollViewer).GetField("_requestedOffset", Reflection.BindingFlags.NonPublic Or Reflection.BindingFlags.Instance)
            Dim sampleField = GetType(PclScrollViewer).GetField("_lastSample", Reflection.BindingFlags.NonPublic Or Reflection.BindingFlags.Instance)
            Dim clampedState As New JObject From {{"boundary", boundary}, {"beforeTime", If(scrolling.CurrentTime?.TotalMilliseconds, -1)},
                {"beforeSample", CDbl(sampleField.GetValue(viewer))}, {"beforeRequested", CDbl(requestField.GetValue(viewer))}}
            Dim remaining = 120 * Math.Pow(1 - scrolling.CurrentTime.Value.TotalMilliseconds / 300, 6)
            scrolling.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(301), TimeSeekOrigin.BeginTime) : layout()
            clampedState("actualEnd") = viewer.VerticalOffset
            clampedState("expectedEnd") = boundary - remaining
            clampedState("ownedAfterEnd") = viewer.MotionClock Is scrolling
            IO.File.WriteAllText(IO.Path.Combine(directory, "wheel-clamp-state.json"), clampedState.ToString())
            check(Math.Abs(viewer.VerticalOffset - (boundary - remaining)) < 0.01,
                "the rest of an upward gesture continues from the new clamped boundary")
            viewer.ScrollDelta(-80)
            scrolling = viewer.MotionClock : scrolling.Controller.Pause() : layout()
            spacer.Height = 1100 : viewer.ScrollToVerticalOffset(200) : layout()
            check(viewer.MotionClock Is Nothing AndAlso Math.Abs(viewer.VerticalOffset - 200) < 0.01,
                "external positioning still cancels wheel motion when it arrives in the same layout as an extent change")

            spacer.Height = 3000 : viewer.ScrollToVerticalOffset(100) : layout()
            viewer.ScrollDelta(120)
            scrolling = viewer.MotionClock : scrolling.Controller.Pause() : layout()
            scrolling.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(60), TimeSeekOrigin.BeginTime)
            spacer.Height = 3300 : layout()
            check(viewer.MotionClock Is scrolling AndAlso Math.Abs(viewer.VerticalOffset - DirectCast(scrolling.Timeline, PclWheelAnimation).Sample(scrolling.CurrentTime.Value.TotalMilliseconds)) < 0.01,
                "a queued own scroll acknowledgement remains valid when layout grows its content at the same time")
            host.Height = 420 : layout()
            check(viewer.MotionClock Is Nothing, "a real viewport-height change still releases scrolling geometry")
        End Using
    End Sub

    Private Shared Sub VerifyCardWidthMotion(directory As String, check As Action(Of Boolean, String))
        Dim text As New TextBlock With {.Text = String.Concat(Enumerable.Repeat("窗口缩放中的动画需要保持当前高度并重新测量换行。", 35)),
            .TextWrapping = TextWrapping.Wrap, .FontSize = 16}
        Dim card As New PclCard With {.Collapsible = True, .Collapsed = True, .Body = text}
        Dim rows As New StackPanel()
        rows.Children.Add(card)
        Dim host As New Border With {.Width = 520, .Height = 400, .Child = New ScrollViewer With {.Content = rows}}
        Dim layout As Action = Sub()
                                   host.Measure(New Size(host.Width, host.Height)) : host.Arrange(New Rect(0, 0, host.Width, host.Height))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        Using source = PreviewSurface.Attach(host, 520, 400)
            layout() : card.Collapsed = False
            Dim old = card.HeightTransition
            old.Controller.Pause() : layout()
            old.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(60), TimeSeekOrigin.BeginTime) : layout()
            Dim height = card.ActualHeight
            host.Width = 260 : layout()
            Dim resized = card.HeightTransition
            IO.File.WriteAllText(IO.Path.Combine(directory, "card-width-state.json"), New JObject From {
                {"clockReplaced", resized IsNot old}, {"displayedBefore", height}, {"displayedAfter", card.ActualHeight},
                {"oldTarget", DirectCast(old.Timeline, DoubleAnimationUsingKeyFrames).KeyFrames.Cast(Of DoubleKeyFrame)().Last().Value},
                {"newTarget", DirectCast(resized.Timeline, DoubleAnimationUsingKeyFrames).KeyFrames.Cast(Of DoubleKeyFrame)().Last().Value},
                {"bodyDesiredHeight", text.DesiredSize.Height}}.ToString())
            check(resized IsNot old, "resizing a wrapping card replaces its obsolete expansion target before restoring auto height")
            resized.Controller.Pause() : layout()
            resized.Controller.SeekAlignedToLastTick(TimeSpan.Zero, TimeSeekOrigin.BeginTime) : layout()
            Dim target = DirectCast(resized.Timeline, DoubleAnimationUsingKeyFrames).KeyFrames.Cast(Of DoubleKeyFrame)().Last().Value
            Dim oldTarget = DirectCast(old.Timeline, DoubleAnimationUsingKeyFrames).KeyFrames.Cast(Of DoubleKeyFrame)().Last().Value
            check(target > oldTarget AndAlso Math.Abs(DirectCast(resized.Timeline, DoubleAnimationUsingKeyFrames).KeyFrames(0).Value - height) < 0.01 AndAlso
                Math.Abs(card.ActualHeight - SampleCardMotion(DirectCast(resized.Timeline, DoubleAnimationUsingKeyFrames), resized.CurrentTime.Value.TotalMilliseconds)) < 0.01 AndAlso card.Body Is text,
                "narrowing a running expansion remeasures wrapping text while retaining its displayed height and content")
            old.Controller.SkipToFill() : layout()
            check(card.HeightTransition Is resized, "completion from the old width cannot settle the replacement expansion")
            resized.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(60), TimeSeekOrigin.BeginTime) : layout()
            IO.File.WriteAllText(IO.Path.Combine(directory, "card-width-sample.json"), New JObject From {
                {"actual", card.ActualHeight}, {"height", card.Height}, {"expected", SampleCardMotion(DirectCast(resized.Timeline, DoubleAnimationUsingKeyFrames), resized.CurrentTime.Value.TotalMilliseconds)},
                {"owned", card.HeightTransition Is resized}, {"width", card.ActualWidth}, {"time", If(resized.CurrentTime?.TotalMilliseconds, -1)},
                {"paused", resized.IsPaused}, {"state", resized.CurrentState.ToString()}}.ToString())
            check(Math.Abs(card.ActualHeight - SampleCardMotion(DirectCast(resized.Timeline, DoubleAnimationUsingKeyFrames), resized.CurrentTime.Value.TotalMilliseconds)) < 0.01,
                "width retargeting preserves the PCL distance-based height curve in real layout")
            height = card.ActualHeight
            host.Width = 440 : layout()
            Dim widened = card.HeightTransition
            widened.Controller.Pause() : layout()
            widened.Controller.SeekAlignedToLastTick(TimeSpan.Zero, TimeSeekOrigin.BeginTime) : layout()
            check(widened IsNot resized AndAlso Math.Abs(DirectCast(widened.Timeline, DoubleAnimationUsingKeyFrames).KeyFrames(0).Value - height) < 0.01 AndAlso
                Math.Abs(card.ActualHeight - SampleCardMotion(DirectCast(widened.Timeline, DoubleAnimationUsingKeyFrames), widened.CurrentTime.Value.TotalMilliseconds)) < 0.01,
                "a second width change reverses the reflow resize from its current visible height")
            resized.Controller.SkipToFill() : layout()
            check(card.HeightTransition Is widened, "a retired narrow-width clock cannot finish the wider reflow")
            Dim finalTarget = DirectCast(widened.Timeline, DoubleAnimationUsingKeyFrames).KeyFrames.Cast(Of DoubleKeyFrame)().Last().Value
            widened.Controller.SeekAlignedToLastTick(widened.Timeline.Duration.TimeSpan + TimeSpan.FromMilliseconds(1), TimeSeekOrigin.BeginTime) : layout()
            check(card.HeightTransition Is Nothing AndAlso Double.IsNaN(card.Height) AndAlso Math.Abs(card.ActualHeight - finalTarget) < 0.01 AndAlso Not card.ClipToBounds,
                "the resized card restores auto height at the measured endpoint without a final reflow jump")
            card.RefreshBody(Function() New Border With {.Height = 2000})
            Dim fixedBody = card.HeightTransition
            fixedBody.Controller.Pause() : layout()
            host.Width = 380 : layout()
            check(card.HeightTransition Is fixedBody, "width changes that preserve the content target do not restart a fixed-height body expansion")
            card.Collapsed = True
            Dim folding = card.HeightTransition
            folding.Controller.Pause() : layout()
            host.Width = 360 : layout()
            check(card.HeightTransition Is folding, "width changes do not restart a collapse whose target remains forty DIP")
            host.Visibility = Visibility.Collapsed : layout()
            host.Width = 340 : layout()
            check(card.HeightTransition Is Nothing AndAlso card.Height = 40 AndAlso Not card.ClipToBounds,
                "hiding and resizing a card retires the width target and leaves its final folded state")
        End Using
    End Sub
End Class
