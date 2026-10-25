Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Media.Animation

Namespace Controls
    ' PCL 5ef81e4 MyResourceItem.RefreshColor: animate only the backing plate.
    ' It has its own 0.8 / 0.996 / 1 scales, unlike MyListItem's whole-row press.
    Friend NotInheritable Class PclResourceHoverMotion
        Private ReadOnly _back As Border
        Private ReadOnly _scale As ScaleTransform
        Private ReadOnly _fill As SolidColorBrush
        Private _clock As ClockGroup
        Private _hasState As Boolean, _active As Boolean, _pressed As Boolean
        Private _color As Color

        Friend Sub New(back As Border)
            _back = back : _scale = DirectCast(back.RenderTransform, ScaleTransform)
            _fill = New SolidColorBrush(DirectCast(back.Background, SolidColorBrush).Color)
            back.Background = _fill
        End Sub
        Friend ReadOnly Property Transition As ClockGroup
            Get
                Return _clock
            End Get
        End Property
        Friend Sub RefreshPalette(color As Color)
            _color = color
            _fill.ApplyAnimationClock(SolidColorBrush.ColorProperty, Nothing)
            _fill.Color = color
        End Sub
        Friend Sub Update(active As Boolean, pressed As Boolean, color As Color, animate As Boolean, pauseForChecks As Boolean)
            If animate AndAlso _hasState AndAlso active = _active AndAlso pressed = _pressed AndAlso color = _color Then Return
            _hasState = True : _active = active : _pressed = pressed : _color = color
            Dim back = _scale.ScaleX, opacity = _back.Opacity, previous = _fill.Color
            Detach()
            Dim targetScale = If(active, If(pressed, 0.996, 1.0), 0.8), targetOpacity = If(active, 1.0, 0.0)
            If Not animate Then
                SetValues(targetScale, targetOpacity, color) : Return
            End If
            SetValues(back, opacity, previous)
            Dim time = If(active, 120, 180), plan As New ParallelTimeline()
            plan.Children.Add(New DoubleAnimation(opacity, targetOpacity, TimeSpan.FromMilliseconds(time)) With {.EasingFunction = If(active, Ease(), Nothing)})
            If active Then
                plan.Children.Add(New DoubleAnimation(back, targetScale, TimeSpan.FromMilliseconds(144)) With {.EasingFunction = Ease()})
            Else
                ' PCL After=True resets by -0.196 in 1ms after the 180ms fade.
                Dim reset As New DoubleAnimationUsingKeyFrames With {.Duration = TimeSpan.FromMilliseconds(181)}
                reset.KeyFrames.Add(New LinearDoubleKeyFrame(back, KeyTime.FromTimeSpan(TimeSpan.Zero)))
                reset.KeyFrames.Add(New EasingDoubleKeyFrame(0.996, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180)), Ease()))
                reset.KeyFrames.Add(New LinearDoubleKeyFrame(0.8, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(181))))
                plan.Children.Add(reset)
            End If
            plan.Children.Add(New PclRadioColorAnimation(previous, color, time))
            Dim current = DirectCast(plan.CreateClock(True), ClockGroup)
            If pauseForChecks Then current.Controller.Pause()
            _clock = current
            AddHandler current.Completed, Sub()
                                              If _clock IsNot current Then Return
                                              Detach() : SetValues(targetScale, targetOpacity, _color)
                                          End Sub
            _back.ApplyAnimationClock(UIElement.OpacityProperty, DirectCast(current.Children(0), AnimationClock))
            _scale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, DirectCast(current.Children(1), AnimationClock))
            _scale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, DirectCast(current.Children(1), AnimationClock))
            _fill.ApplyAnimationClock(SolidColorBrush.ColorProperty, DirectCast(current.Children(2), AnimationClock))
        End Sub
        Private Shared Function Ease() As IEasingFunction
            Return New PowerEase With {.Power = 3, .EasingMode = EasingMode.EaseOut}
        End Function
        Private Sub SetValues(scale As Double, opacity As Double, color As Color)
            _scale.ScaleX = scale : _scale.ScaleY = scale : _back.Opacity = opacity : _fill.Color = color
        End Sub
        Private Sub Detach()
            Dim previous = _clock : _clock = Nothing
            _back.ApplyAnimationClock(UIElement.OpacityProperty, Nothing)
            _scale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, Nothing) : _scale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, Nothing)
            _fill.ApplyAnimationClock(SolidColorBrush.ColorProperty, Nothing)
            previous?.Controller.Remove()
        End Sub
    End Class
End Namespace
