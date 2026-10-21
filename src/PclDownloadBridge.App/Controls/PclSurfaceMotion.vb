Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Controls.Primitives
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Media.Animation

Namespace Controls
    ' PCL FormMain.PageChangeAnim + MyPageRight.TriggerEnter/ExitAnimation.
    ' Logical page ownership changes immediately; frozen exit layers are inert.
    Friend NotInheritable Class PclSurfaceMotion
        Private ReadOnly _host As Grid
        Private ReadOnly _overlay As Canvas
        Private ReadOnly _enabled As Func(Of Boolean)
        Private ReadOnly _units As Func(Of FrameworkElement, IEnumerable(Of FrameworkElement))
        Private ReadOnly _items As New List(Of Entry)
        Private _bar As BarEntry
        Private _surface As FrameworkElement
        Private _surfaceOpacity As Double
        Private _clock As ClockGroup
        Friend Event Settled As EventHandler
        Private Class Entry
            Friend Target As FrameworkElement
            Friend OriginalTransform As Transform
            Friend Group As TransformGroup
            Friend Offset As TranslateTransform
            Friend Opacity As Double
        End Class
        Private Class BarEntry
            Inherits Entry
            Friend Pointer As MouseButtonEventHandler
            Friend Hidden As DependencyPropertyChangedEventHandler
            Friend Unloaded As RoutedEventHandler
            Friend Key As KeyEventHandler
            Friend Touch As EventHandler(Of TouchEventArgs)
        End Class
        Friend Sub New(host As Grid, overlay As Canvas, enabled As Func(Of Boolean), units As Func(Of FrameworkElement, IEnumerable(Of FrameworkElement)))
            _host = host : _overlay = overlay : _enabled = enabled : _units = units
            AddHandler host.Unloaded, Sub() Finish()
            AddHandler host.IsVisibleChanged, Sub() If Not host.IsVisible Then Finish()
            AddHandler host.SizeChanged, Sub() Finish()
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

        Friend Sub Change(outgoing As FrameworkElement, incoming As FrameworkElement, changePage As Action, Optional contentRefresh As Boolean = False, Optional compositeExit As Boolean = False)
            If outgoing Is incoming AndAlso Not contentRefresh Then
                changePage()
                Return
            End If
            Dim animate = _enabled() AndAlso _host.IsLoaded AndAlso _host.IsVisible AndAlso _host.ActualWidth > 0 AndAlso _host.ActualHeight > 0
            ' On reversal, use one composited image: slicing a running transition
            ' by the logical incoming layout would split unrelated outgoing pixels.
            Dim snapshot As PclExitSnapshot = Nothing
            If animate AndAlso outgoing IsNot Nothing Then
                Dim segmented = _clock Is Nothing AndAlso Not contentRefresh AndAlso Not compositeExit
                snapshot = PclExitSnapshot.Capture(_host, If(segmented, _units(outgoing), Enumerable.Empty(Of FrameworkElement)()),
                    If(segmented, VisibleBar(outgoing), Nothing))
            End If
            StopOwnedMotion()
            changePage()
            If Not animate Then Return
            Try
                incoming.UpdateLayout()
                Dim visible = _units(incoming).Where(Function(unit) Not PclExitSnapshot.VisibleBounds(unit, _host).IsEmpty).Take(16).ToList()
                Dim bar = VisibleBar(incoming)
                If snapshot Is Nothing AndAlso visible.Count = 0 AndAlso bar Is Nothing Then Return
                Dim plan As New ParallelTimeline
                Dim bindings As New List(Of Action(Of AnimationClock))
                Dim append As Action(Of AnimationTimeline, Action(Of AnimationClock)) =
                    Sub(animation, bind)
                        plan.Children.Add(animation) : bindings.Add(bind)
                    End Sub
                If snapshot IsNot Nothing Then
                    For Each piece In snapshot.Layers
                        Dim image = snapshot.ImageFor(piece)
                        Dim offset = DirectCast(image.RenderTransform, TranslateTransform)
                        ' Keep viewport clipping outside the moving image. Clipping
                        ' only the source pixels would let a scrolled card slide over
                        ' the fixed toolbar when its exit translation is applied.
                        Dim layer As New Canvas With {.Width = snapshot.Width, .Height = snapshot.Height,
                            .Clip = New RectangleGeometry(piece.Viewport), .IsHitTestVisible = False}
                        layer.Children.Add(image)
                        _overlay.Children.Add(layer)
                        If piece.ScrollBar Then
                            append(New DoubleAnimation(0, 10, TimeSpan.FromMilliseconds(90)) With {
                                .EasingFunction = New PowerEase With {.Power = 3, .EasingMode = EasingMode.EaseIn}},
                                Sub(clock) offset.ApplyAnimationClock(TranslateTransform.XProperty, clock))
                        Else
                            append(New DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(70)) With {.BeginTime = TimeSpan.FromMilliseconds(piece.Delay)},
                                Sub(clock) image.ApplyAnimationClock(UIElement.OpacityProperty, clock))
                            append(New DoubleAnimation(0, -6, TimeSpan.FromMilliseconds(70)) With {.BeginTime = TimeSpan.FromMilliseconds(piece.Delay)},
                                Sub(clock) offset.ApplyAnimationClock(TranslateTransform.YProperty, clock))
                        End If
                    Next
                End If
                Dim delay = If(snapshot Is Nothing, 0, 140)
                _surface = incoming : _surfaceOpacity = incoming.Opacity
                ' Hide non-card chrome too, so new controls cannot bleed through
                ' gaps in the exiting page during the 110 + 30 ms wait.
                Dim reveal As New DoubleAnimationUsingKeyFrames
                reveal.KeyFrames.Add(New DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)))
                reveal.KeyFrames.Add(New DiscreteDoubleKeyFrame(_surfaceOpacity, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))))
                append(reveal, Sub(clock) incoming.ApplyAnimationClock(UIElement.OpacityProperty, clock))
                For Each unit In visible
                    Dim entry = Wrap(unit, 0, -16)
                    _items.Add(entry)
                    Dim fade As New DoubleAnimationUsingKeyFrames
                    fade.KeyFrames.Add(New DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)))
                    fade.KeyFrames.Add(New DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))))
                    fade.KeyFrames.Add(New EasingDoubleKeyFrame(entry.Opacity, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + 100)),
                        New PowerEase With {.Power = 2, .EasingMode = EasingMode.EaseOut}))
                    append(fade, Sub(clock) entry.Target.ApplyAnimationClock(UIElement.OpacityProperty, clock))
                    append(New PclPageYAnimation With {.BeginTime = TimeSpan.FromMilliseconds(delay)},
                        Sub(clock) entry.Offset.ApplyAnimationClock(TranslateTransform.YProperty, clock))
                    delay += 25
                Next
                If bar IsNot Nothing Then
                    Dim entry As New BarEntry
                    Wrap(bar, 10, 0, entry)
                    _bar = entry
                    entry.Pointer = Sub() ReleaseBar(entry)
                    entry.Hidden = Sub() If Not bar.IsVisible Then ReleaseBar(entry)
                    entry.Unloaded = Sub() ReleaseBar(entry)
                    entry.Key = Sub() ReleaseBar(entry)
                    entry.Touch = Sub() ReleaseBar(entry)
                    AddHandler bar.PreviewMouseDown, entry.Pointer
                    AddHandler bar.IsVisibleChanged, entry.Hidden
                    AddHandler bar.Unloaded, entry.Unloaded
                    AddHandler bar.PreviewKeyDown, entry.Key
                    AddHandler bar.PreviewTouchDown, entry.Touch
                    append(New DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(350)) With {
                        .BeginTime = TimeSpan.FromMilliseconds(If(snapshot Is Nothing, 0, 140)),
                        .EasingFunction = New PowerEase With {.Power = 3, .EasingMode = EasingMode.EaseOut}},
                        Sub(clock) entry.Offset.ApplyAnimationClock(TranslateTransform.XProperty, clock))
                End If
                Dim group = DirectCast(plan.CreateClock(True), ClockGroup)
                _clock = group
                _host.IsHitTestVisible = False
                AddHandler group.CurrentTimeInvalidated, Sub()
                                                            If _clock IsNot group Then Return
                                                            Dim elapsed = If(group.CurrentTime, TimeSpan.Zero).TotalMilliseconds
                                                            If elapsed >= 110 Then _overlay.Children.Clear()
                                                            If elapsed >= If(snapshot Is Nothing, 0, 140) Then _host.IsHitTestVisible = True
                                                        End Sub
                AddHandler group.Completed, Sub() If _clock Is group Then Finish()
                For i = 0 To bindings.Count - 1
                    bindings(i)(DirectCast(group.Children(i), AnimationClock))
                Next
                ' Prime inactive clocks after attaching them. Seeking to zero here
                ' would anchor the group to the previous dispatcher tick and count
                ' expensive snapshot preparation as already elapsed animation time.
                incoming.SetCurrentValue(UIElement.OpacityProperty, If(snapshot Is Nothing, _surfaceOpacity, 0.0))
                For Each entry In _items
                    entry.Target.SetCurrentValue(UIElement.OpacityProperty, 0.0)
                Next
            Catch
                StopOwnedMotion()
                Throw
            End Try
        End Sub

        Friend Sub Finish()
            Dim notify = _clock IsNot Nothing
            StopOwnedMotion()
            If notify Then RaiseEvent Settled(Me, EventArgs.Empty)
        End Sub
        Private Shared Function Wrap(target As FrameworkElement, x As Double, y As Double, Optional entry As Entry = Nothing) As Entry
            entry = If(entry, New Entry())
            entry.Target = target : entry.OriginalTransform = target.RenderTransform : entry.Opacity = target.Opacity
            entry.Offset = New TranslateTransform(x, y) : entry.Group = New TransformGroup()
            If entry.OriginalTransform IsNot Nothing Then entry.Group.Children.Add(entry.OriginalTransform)
            entry.Group.Children.Add(entry.Offset)
            target.SetCurrentValue(UIElement.RenderTransformProperty, entry.Group)
            Return entry
        End Function
        Private Sub ReleaseBar(entry As BarEntry)
            If _bar IsNot entry Then Return
            _bar = Nothing
            RemoveHandler entry.Target.PreviewMouseDown, entry.Pointer
            RemoveHandler entry.Target.IsVisibleChanged, entry.Hidden
            RemoveHandler entry.Target.Unloaded, entry.Unloaded
            RemoveHandler entry.Target.PreviewKeyDown, entry.Key
            RemoveHandler entry.Target.PreviewTouchDown, entry.Touch
            entry.Offset.BeginAnimation(TranslateTransform.XProperty, Nothing)
            If entry.Target.RenderTransform Is entry.Group Then entry.Target.SetCurrentValue(UIElement.RenderTransformProperty, entry.OriginalTransform)
        End Sub
        Private Sub StopOwnedMotion()
            Dim previous = _clock
            _clock = Nothing
            previous?.Controller.Remove()
            If _bar IsNot Nothing Then ReleaseBar(_bar)
            For Each entry In _items
                entry.Target.BeginAnimation(UIElement.OpacityProperty, Nothing)
                entry.Target.SetCurrentValue(UIElement.OpacityProperty, entry.Opacity)
                If entry.Target.RenderTransform Is entry.Group Then entry.Target.SetCurrentValue(UIElement.RenderTransformProperty, entry.OriginalTransform)
            Next
            _items.Clear()
            If _surface IsNot Nothing Then
                _surface.BeginAnimation(UIElement.OpacityProperty, Nothing)
                _surface.SetCurrentValue(UIElement.OpacityProperty, _surfaceOpacity)
                _surface = Nothing
            End If
            _overlay.Children.Clear()
            _host.IsHitTestVisible = True
        End Sub
        Private Shared Function VisibleBar(root As FrameworkElement) As ScrollBar
            If root Is Nothing OrElse Not root.IsVisible Then Return Nothing
            Dim viewer = TryCast(root, PclScrollViewer)
            If viewer IsNot Nothing Then Return viewer.VerticalBar
            For Each child In Children(root)
                Dim bar = VisibleBar(child)
                If bar IsNot Nothing Then Return bar
            Next
            Return Nothing
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
            Else
                For Each child In Children(root)
                    Collect(child, result)
                Next
            End If
        End Sub
        Private Shared Function Children(root As FrameworkElement) As IEnumerable(Of FrameworkElement)
            If TypeOf root Is PclCard Then Return Enumerable.Empty(Of FrameworkElement)()
            If TypeOf root Is ContentControl Then Return {TryCast(DirectCast(root, ContentControl).Content, FrameworkElement)}
            If TypeOf root Is Decorator Then Return {TryCast(DirectCast(root, Decorator).Child, FrameworkElement)}
            If TypeOf root Is Panel Then Return DirectCast(root, Panel).Children.OfType(Of FrameworkElement)()
            Return Enumerable.Empty(Of FrameworkElement)()
        End Function
    End Class
End Namespace
