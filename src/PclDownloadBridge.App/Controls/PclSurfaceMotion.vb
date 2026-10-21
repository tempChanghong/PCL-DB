Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports System.Windows.Media.Imaging

Namespace Controls
    ' PCL FormMain.PageChangeAnim + MyPageRight.TriggerEnter/ExitAnimation.
    ' The outgoing visual is frozen in an inert overlay. Logical page ownership changes
    ' immediately, so asynchronous loaders and keyboard routing never target a ghost page.
    Friend NotInheritable Class PclSurfaceMotion
        Private ReadOnly _host As Grid
        Private ReadOnly _overlay As Canvas
        Private ReadOnly _enabled As Func(Of Boolean)
        Private ReadOnly _units As Func(Of FrameworkElement, IEnumerable(Of FrameworkElement))
        Private ReadOnly _items As New List(Of Entry)
        Private _clock As ClockGroup
        Friend Event Settled As EventHandler

        Private Class Entry
            Friend Target As FrameworkElement
            Friend OriginalTransform As Transform
            Friend Group As TransformGroup
            Friend Offset As TranslateTransform
            Friend Opacity As Double
        End Class

        Friend Sub New(host As Grid, overlay As Canvas, enabled As Func(Of Boolean), units As Func(Of FrameworkElement, IEnumerable(Of FrameworkElement)))
            _host = host : _overlay = overlay : _enabled = enabled : _units = units
            AddHandler host.Unloaded, Sub() Finish()
            AddHandler host.IsVisibleChanged, Sub() If Not host.IsVisible Then Finish()
        End Sub

        Friend ReadOnly Property Transition As ClockGroup
            Get
                Return _clock
            End Get
        End Property
        Friend ReadOnly Property ItemCount As Integer
            Get
                Return _items.Count
            End Get
        End Property

        Friend Sub Change(outgoing As FrameworkElement, incoming As FrameworkElement, changePage As Action)
            If outgoing Is incoming Then
                changePage()
                Return
            End If
            Dim animate = _enabled() AndAlso _host.IsLoaded AndAlso _host.IsVisible AndAlso _host.ActualWidth > 0 AndAlso _host.ActualHeight > 0
            ' Capture before stopping the previous clock: rapid reversal starts at the last displayed frame.
            Dim snapshot = If(animate AndAlso outgoing IsNot Nothing, Capture(), Nothing)
            StopOwnedMotion()
            changePage()
            If Not animate Then Return
            incoming.UpdateLayout()
            Dim visible = _units(incoming).Where(Function(unit) InViewport(unit)).Take(16).ToList()
            If snapshot Is Nothing AndAlso visible.Count = 0 Then Return
            Dim plan As New ParallelTimeline
            Dim ghost As Image = Nothing
            If snapshot IsNot Nothing Then
                ghost = New Image With {.Source = snapshot, .Width = _host.ActualWidth, .Height = _host.ActualHeight,
                    .Stretch = Stretch.Fill, .IsHitTestVisible = False, .RenderTransform = New TranslateTransform()}
                _overlay.Children.Add(ghost)
                plan.Children.Add(New DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(70)))
                plan.Children.Add(New DoubleAnimation(0, -6, TimeSpan.FromMilliseconds(70)))
            End If
            Dim delay = If(snapshot Is Nothing, 0, 140)
            For Each unit In visible
                Dim entry As New Entry With {.Target = unit, .OriginalTransform = unit.RenderTransform, .Opacity = unit.Opacity,
                    .Offset = New TranslateTransform(0, -16), .Group = New TransformGroup()}
                If entry.OriginalTransform IsNot Nothing Then entry.Group.Children.Add(entry.OriginalTransform)
                entry.Group.Children.Add(entry.Offset)
                unit.SetCurrentValue(UIElement.RenderTransformProperty, entry.Group)
                unit.SetCurrentValue(UIElement.OpacityProperty, 0.0)
                _items.Add(entry)
                ' Hold zero from the first tick: a delayed DoubleAnimation uses the
                ' underlying base opacity before BeginTime, exposing the new page.
                Dim fade As New DoubleAnimationUsingKeyFrames
                fade.KeyFrames.Add(New DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)))
                fade.KeyFrames.Add(New DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))))
                fade.KeyFrames.Add(New EasingDoubleKeyFrame(entry.Opacity, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + 100)),
                    New PowerEase With {.Power = 2, .EasingMode = EasingMode.EaseOut}))
                plan.Children.Add(fade)
                plan.Children.Add(New PclPageYAnimation With {.BeginTime = TimeSpan.FromMilliseconds(delay)})
                delay += 25
            Next
            Dim clock = DirectCast(plan.CreateClock(True), ClockGroup)
            _clock = clock
            _host.IsHitTestVisible = False
            AddHandler clock.CurrentTimeInvalidated, Sub()
                                                        If _clock IsNot clock Then Return
                                                        Dim elapsed = If(clock.CurrentTime, TimeSpan.Zero).TotalMilliseconds
                                                        If elapsed >= 110 Then _overlay.Children.Clear()
                                                        If elapsed >= 140 Then _host.IsHitTestVisible = True
                                                    End Sub
            AddHandler clock.Completed, Sub() If _clock Is clock Then Finish()
            Dim index = 0
            If ghost IsNot Nothing Then
                ghost.ApplyAnimationClock(UIElement.OpacityProperty, DirectCast(clock.Children(0), AnimationClock))
                DirectCast(ghost.RenderTransform, TranslateTransform).ApplyAnimationClock(TranslateTransform.YProperty, DirectCast(clock.Children(1), AnimationClock))
                index = 2
            End If
            For Each entry In _items
                entry.Target.ApplyAnimationClock(UIElement.OpacityProperty, DirectCast(clock.Children(index), AnimationClock))
                entry.Offset.ApplyAnimationClock(TranslateTransform.YProperty, DirectCast(clock.Children(index + 1), AnimationClock))
                index += 2
            Next
        End Sub

        Friend Sub Finish()
            Dim notify = _clock IsNot Nothing
            StopOwnedMotion()
            If notify Then RaiseEvent Settled(Me, EventArgs.Empty)
        End Sub

        Private Sub StopOwnedMotion()
            Dim previous = _clock
            _clock = Nothing
            previous?.Controller.Remove()
            For Each entry In _items
                entry.Target.BeginAnimation(UIElement.OpacityProperty, Nothing)
                entry.Target.SetCurrentValue(UIElement.OpacityProperty, entry.Opacity)
                If entry.Target.RenderTransform Is entry.Group Then entry.Target.SetCurrentValue(UIElement.RenderTransformProperty, entry.OriginalTransform)
            Next
            _items.Clear()
            _overlay.Children.Clear()
            _host.IsHitTestVisible = True
        End Sub

        Private Function InViewport(unit As FrameworkElement) As Boolean
            If Not unit.IsVisible OrElse unit.ActualWidth <= 0 OrElse unit.ActualHeight <= 0 Then Return False
            Try
                Dim bounds = unit.TransformToAncestor(_host).TransformBounds(New Rect(unit.RenderSize))
                Return bounds.IntersectsWith(New Rect(_host.RenderSize))
            Catch ex As InvalidOperationException
                Return False
            End Try
        End Function

        Private Function Capture() As BitmapSource
            Try
                Dim w = _host.ActualWidth, h = _host.ActualHeight
                Dim transform = PresentationSource.FromVisual(_host)?.CompositionTarget?.TransformToDevice
                Dim scale = Math.Min(If(transform.HasValue, transform.Value.M11, 1), Math.Sqrt(4000000 / (w * h)))
                Dim drawing As New DrawingVisual()
                Using context = drawing.RenderOpen()
                    context.DrawRectangle(New VisualBrush(_host) With {.ViewboxUnits = BrushMappingMode.Absolute, .Viewbox = New Rect(0, 0, w, h), .Stretch = Stretch.Fill}, Nothing, New Rect(0, 0, w, h))
                End Using
                Dim bitmap As New RenderTargetBitmap(Math.Max(1, CInt(Math.Ceiling(w * scale))), Math.Max(1, CInt(Math.Ceiling(h * scale))), 96 * scale, 96 * scale, PixelFormats.Pbgra32)
                bitmap.Render(drawing) : bitmap.Freeze()
                Return bitmap
            Catch ex As InvalidOperationException
                Return Nothing
            Catch ex As ArgumentException
                Return Nothing
            End Try
        End Function

        Friend Shared Function Units(root As FrameworkElement) As IEnumerable(Of FrameworkElement)
            Dim result As New List(Of FrameworkElement)
            Collect(root, result)
            Return result
        End Function
        Private Shared Sub Collect(root As FrameworkElement, result As List(Of FrameworkElement))
            If root Is Nothing OrElse root.Visibility <> Visibility.Visible Then Return
            If TypeOf root Is PclCard OrElse TypeOf root Is PclHint OrElse TypeOf root Is TextBlock OrElse TypeOf root Is PclButton Then
                result.Add(root)
            ElseIf TypeOf root Is ContentControl Then
                Collect(TryCast(DirectCast(root, ContentControl).Content, FrameworkElement), result)
            ElseIf TypeOf root Is Decorator Then
                Collect(TryCast(DirectCast(root, Decorator).Child, FrameworkElement), result)
            ElseIf TypeOf root Is Panel Then
                For Each child As FrameworkElement In DirectCast(root, Panel).Children.OfType(Of FrameworkElement)()
                    Collect(child, result)
                Next
            End If
        End Sub
    End Class
End Namespace
