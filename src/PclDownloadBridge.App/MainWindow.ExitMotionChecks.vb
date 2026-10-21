Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports System.Windows.Media.Imaging
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Sub VerifyExitAndBarMotion(directory As String, check As Action(Of Boolean, String))
        Dim content = DirectCast(Me.Content, FrameworkElement)
        Dim host As New Border With {.Width = 850, .Height = 600}
        Dim layout As Action = Sub()
                                   host.Measure(New Size(host.Width, host.Height)) : host.Arrange(New Rect(0, 0, host.Width, host.Height))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        Dim seek As Action(Of ClockGroup, Double) = Sub(clock, time)
                                                        clock.Controller.Pause() : layout()
                                                        clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(time), TimeSeekOrigin.BeginTime) : layout()
                                                    End Sub
        Me.Content = Nothing : host.Child = content
        Using source = PreviewSurface.Attach(host, 850, 600)
            Try
                layout() : _motionPreview = True
                SwitchMainPage(True) : _surfaceMotion.Finish() : layout()
                Dim oldBar = SettingsPage.VerticalBar
                check(oldBar IsNot Nothing, "a real settings template exposes its visible vertical scrollbar")
                Dim originalBarTransform As New ScaleTransform(1, 0.99)
                oldBar.RenderTransform = originalBarTransform
                SaveControlVisual(host, IO.Path.Combine(directory, "exit-before.png"))
                ShowAbout(Me, New RoutedEventArgs())
                Dim clock = _surfaceMotion.Transition
                check(AboutPage.Opacity = 0 AndAlso clock IsNot Nothing, "new page chrome is hidden even before the first animation tick")
                clock.Controller.Pause() : layout() : seek(clock, 0)
                SaveControlVisual(host, IO.Path.Combine(directory, "exit-zero.png"))
                Dim difference = MotionImageDifference(IO.Path.Combine(directory, "exit-before.png"), IO.Path.Combine(directory, "exit-zero.png"), 70)
                IO.File.WriteAllText(IO.Path.Combine(directory, "exit-zero-pixels.txt"), "mean absolute RGB difference=" & difference)
                check(difference < 4, "a zero-time segmented exit preserves page placement without applying the title-bar layout offset twice")
                Dim layers = PageGhost.Children.Cast(Of Canvas)().ToArray()
                Dim images = layers.Select(Function(layer) DirectCast(layer.Children(0), Image)).ToArray()
                Dim plan = DirectCast(clock.Timeline, ParallelTimeline)
                Dim fades = plan.Children.OfType(Of DoubleAnimation)().Where(Function(a) a.From.HasValue AndAlso a.From.Value = 1 AndAlso a.To.Value = 0).ToArray()
                check(fades.Length > 2 AndAlso fades.Skip(1).Select(Function(a) a.BeginTime.Value.TotalMilliseconds).SequenceEqual(
                    Enumerable.Range(0, fades.Length - 1).Select(Function(i) CDbl(i * 15))),
                    "steady-page exit fades stagger each visible unit by the source fifteen milliseconds")
                check(fades.All(Function(a) a.Duration.TimeSpan.TotalMilliseconds = 70 AndAlso a.EasingFunction Is Nothing),
                    "each exit opacity uses the source seventy millisecond linear curve")
                check(images.Length = fades.Length + 1 AndAlso images.All(Function(item) ReferenceEquals(item.Source, images(0).Source)) AndAlso images(0).Source.IsFrozen,
                    "segmented exit shares one immutable bitmap across every card and scrollbar")
                seek(clock, 20)
                check(Math.Abs(images(2).Opacity - (1 - 20.0 / 70)) < 0.01 AndAlso Math.Abs(images(3).Opacity - (1 - 5.0 / 70)) < 0.01,
                    "real exit layers retain different opacity samples at the stagger boundary")
                check(Math.Abs(DirectCast(images(2).RenderTransform, TranslateTransform).Y + 6 * 20.0 / 70) < 0.01,
                    "real exit layer displacement retains the source six-DIP upward motion")
                check(Math.Abs(DirectCast(images(1).RenderTransform, TranslateTransform).X - 10 * Math.Pow(20.0 / 90, 3)) < 0.01,
                    "outgoing scrollbar moves ten DIP over ninety milliseconds with PCL cubic ease-in")
                check(images.All(Function(item) Not item.IsHitTestVisible) AndAlso layers.All(Function(layer) Not layer.IsHitTestVisible) AndAlso AboutPage.Opacity = 0,
                    "exit layers never receive input or expose incoming page chrome")
                seek(clock, 110)
                check(PageGhost.Children.Count = 0 AndAlso AboutPage.Opacity = 0,
                    "FormMain's fixed one-hundred-ten millisecond replacement clears even delayed exit layers")
                seek(clock, 190)
                Dim bar = AboutPage.VerticalBar
                Dim transformed = TryCast(bar.RenderTransform, TransformGroup)
                check(transformed IsNot Nothing AndAlso Math.Abs(transformed.Children.OfType(Of TranslateTransform)().Last().X - 10 * Math.Pow(1 - 50.0 / 350, 3)) < 0.01,
                    "incoming scrollbar uses ten-to-zero displacement over three hundred fifty milliseconds with PCL cubic ease-out")
                AboutPage.SmoothScrollingEnabled = True : AboutPage.ForceMotionForChecks = True
                AboutPage.ScrollDelta(120) : layout()
                check(AboutPage.MotionClock IsNot Nothing AndAlso _surfaceMotion.Transition Is clock,
                    "scrollbar horizontal entry and vertical wheel scrolling have independent clocks")
                bar.RaiseEvent(New MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) With {.RoutedEvent = Mouse.PreviewMouseDownEvent})
                check(AboutPage.MotionClock Is Nothing AndAlso Not TypeOf bar.RenderTransform Is TransformGroup AndAlso _surfaceMotion.Transition Is clock,
                    "scrollbar manipulation settles its horizontal transform and wheel motion while keeping card entry active")
                _surfaceMotion.Finish()
                BackFromAbout(Me, New RoutedEventArgs())
                clock = _surfaceMotion.Transition : clock.Controller.Pause() : layout()
                check(DirectCast(oldBar.RenderTransform, TransformGroup).Children(0) Is originalBarTransform,
                    "scrollbar entry composes with the exact pre-existing template transform")
                Dim template = SettingsPage.Template
                SettingsPage.Template = Nothing : SettingsPage.ApplyTemplate() : layout()
                check(oldBar.RenderTransform Is originalBarTransform AndAlso _surfaceMotion.Transition Is clock,
                    "template unloading releases the old scrollbar without cancelling page entry")
                SettingsPage.Template = template : SettingsPage.ApplyTemplate() : layout()
                Dim replacement = SettingsPage.VerticalBar
                check(replacement IsNot Nothing AndAlso replacement IsNot oldBar AndAlso Not TypeOf replacement.RenderTransform Is TransformGroup,
                    "a rebuilt scrollbar starts without stale translation or old event handlers")
                _surfaceMotion.Finish()
                ShowAbout(Me, New RoutedEventArgs())
                clock = _surfaceMotion.Transition : clock.Controller.Pause() : layout()
                host.Height = 500 : layout()
                check(_surfaceMotion.Transition Is Nothing AndAlso PageGhost.Children.Count = 0 AndAlso PageSurface.IsHitTestVisible AndAlso AboutPage.Opacity = 1 AndAlso Not TypeOf AboutPage.VerticalBar.RenderTransform Is TransformGroup,
                    "window resizing settles page and scrollbar motion before old-size snapshots can persist")
                BackFromAbout(Me, New RoutedEventArgs())
                Dim newer = _surfaceMotion.Transition : newer.Controller.Pause() : layout()
                clock.Controller.SkipToFill() : layout()
                check(_surfaceMotion.Transition Is newer, "a resized-away clock cannot finish the next page entry")
                ShowAbout(Me, New RoutedEventArgs())
                Dim reverse = _surfaceMotion.Transition : reverse.Controller.Pause() : layout()
                check(PageGhost.Children.Count = 1 AndAlso DirectCast(DirectCast(PageGhost.Children(0), Canvas).Children(0), Image).Clip.Bounds.Width > PageSurface.ActualWidth * 0.9,
                    "rapid reversal freezes the full composite instead of cutting it by the next page layout")
                _surfaceMotion.Finish()
                host.Height = 1800 : layout()
                BackFromAbout(Me, New RoutedEventArgs())
                clock = _surfaceMotion.Transition : clock.Controller.Pause() : layout()
                check(SettingsPage.VerticalBar Is Nothing AndAlso Not DirectCast(clock.Timeline, ParallelTimeline).Children.OfType(Of DoubleAnimation)().Any(
                    Function(a) a.Duration.TimeSpan.TotalMilliseconds = 350 AndAlso a.From.HasValue AndAlso a.From.Value = 10),
                    "pages that fit the viewport create no phantom scrollbar animation")
                source.RootVisual = Nothing : FlushControlLayout()
                check(_surfaceMotion.Transition Is Nothing AndAlso SettingsPage.Opacity = 1 AndAlso PageSurface.IsHitTestVisible,
                    "unloading restores root opacity as well as card and scrollbar ownership")
            Finally
                _pendingSurfaceFocus = Nothing : _surfaceMotion.Finish()
                source.RootVisual = Nothing : host.Child = Nothing : Me.Content = content
            End Try
        End Using
    End Sub

    Private Shared Function MotionImageDifference(first As String, second As String, top As Integer) As Double
        Dim load As Func(Of String, BitmapSource) = Function(path) New FormatConvertedBitmap(
            BitmapFrame.Create(New Uri(IO.Path.GetFullPath(path)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad), PixelFormats.Bgra32, Nothing, 0)
        Dim a = load(first), b = load(second)
        If a.PixelWidth <> b.PixelWidth OrElse a.PixelHeight <> b.PixelHeight Then Return Double.PositiveInfinity
        Dim stride = a.PixelWidth * 4
        Dim left(stride * a.PixelHeight - 1) As Byte, right(left.Length - 1) As Byte
        a.CopyPixels(left, stride, 0) : b.CopyPixels(right, stride, 0)
        Dim total As Long = 0
        For i = top * stride To left.Length - 1 Step 4
            For channel = 0 To 2
                total += Math.Abs(CInt(left(i + channel)) - CInt(right(i + channel)))
            Next
        Next
        Return total / CDbl(a.PixelWidth * (a.PixelHeight - top) * 3)
    End Function

    Private Shared Sub VerifySnapshotViewport(directory As String, check As Action(Of Boolean, String))
        Dim root As New Grid With {.ClipToBounds = True}
        root.RowDefinitions.Add(New RowDefinition With {.Height = New GridLength(45)})
        root.RowDefinitions.Add(New RowDefinition())
        Dim toolbar As New Border With {.Background = Brushes.CornflowerBlue, .Child = New TextBlock With {.Text = "固定工具条"}}
        root.Children.Add(toolbar)
        Dim rows As New StackPanel()
        For i = 1 To 1200
            rows.Children.Add(New TextBlock With {.Text = "长列表项 " & i, .Height = 30})
        Next
        Dim viewer As New PclScrollViewer With {.Content = rows, .CanContentScroll = False}
        Grid.SetRow(viewer, 1) : root.Children.Add(viewer)
        Dim host As New Border With {.Child = root, .Width = 500, .Height = 450}
        Dim layout As Action = Sub()
                                   host.Measure(New Size(host.Width, host.Height)) : host.Arrange(New Rect(0, 0, host.Width, host.Height))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        Using source = PreviewSurface.Attach(host, 500, 450)
            layout() : viewer.ScrollToVerticalOffset(30005) : layout()
            Dim watch = System.Diagnostics.Stopwatch.StartNew()
            Dim snapshot = PclExitSnapshot.Capture(root, PclSurfaceMotion.Units(viewer), viewer.VerticalBar)
            watch.Stop()
            IO.File.WriteAllText(IO.Path.Combine(directory, "snapshot-timing.txt"), "warm-layout capture ms=" & watch.Elapsed.TotalMilliseconds)
            check(snapshot IsNot Nothing AndAlso snapshot.Layers.Count > 2 AndAlso snapshot.Layers.Count <= 18,
                "a long scrolled page segments its visible tail rather than only the first sixteen data items")
            check(snapshot.Layers.Skip(1).Where(Function(piece) Not piece.ScrollBar).All(Function(piece) piece.Viewport.Top >= 45 AndAlso piece.Mask.Bounds.Top >= 45),
                "scrolled exit pieces keep a stationary viewport clip below the fixed toolbar")
            Dim area = snapshot.Layers.Sum(Function(piece) piece.Mask.GetArea())
            check(Math.Abs(area - root.ActualWidth * root.ActualHeight) < 0.1,
                "disjoint exit masks cover exactly the original snapshot without double-painted overlaps or holes")
            viewer.LayoutTransform = New ScaleTransform(1.25, 1.25) : layout()
            Dim visible = PclSurfaceMotion.Units(viewer).First(Function(item) Not PclExitSnapshot.VisibleBounds(item, root).IsEmpty)
            check(PclExitSnapshot.VisibleBounds(visible, root, 4).Top >= 45 AndAlso PclExitSnapshot.ClipViewport(visible, root).Top >= 45,
                "ancestor scaling retains host-coordinate viewport clipping")
            host.Width = 3200 : host.Height = 1800 : layout()
            snapshot = PclExitSnapshot.Capture(root, PclSurfaceMotion.Units(viewer), viewer.VerticalBar)
            check(CLng(snapshot.Bitmap.PixelWidth) * snapshot.Bitmap.PixelHeight <= 4000000 AndAlso snapshot.Bitmap.IsFrozen,
                "large-window exit capture respects the four-million-pixel allocation ceiling including rounding")
            check(PclExitSnapshot.VisibleBounds(visible, New Grid()).IsEmpty,
                "a detached or unrelated host cannot contribute an invalid exit rectangle")
            source.RootVisual = Nothing
        End Using
    End Sub
End Class
