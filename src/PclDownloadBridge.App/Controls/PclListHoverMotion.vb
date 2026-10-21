Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Media.Animation

Namespace Controls
    ' MyListItem.RefreshColor at PCL 5ef81e4. StateLast avoids restarting identical states.
    Friend NotInheritable Class PclListHoverMotion
        Private ReadOnly _background As Border
        Private ReadOnly _backScale As ScaleTransform
        Private ReadOnly _wholeScale As ScaleTransform
        Private _fill As SolidColorBrush
        Private _clock As ClockGroup
        Private _hasState As Boolean
        Private _active As Boolean, _pressed As Boolean, _sidebar As Boolean
        Private _color As Color

        Friend Sub New(background As Border, wholeScale As ScaleTransform)
            _background = background
            _backScale = DirectCast(background.RenderTransform, ScaleTransform)
            _wholeScale = wholeScale
        End Sub

        Friend ReadOnly Property Transition As ClockGroup
            Get
                Return _clock
            End Get
        End Property

        Friend Sub Update(active As Boolean, pressed As Boolean, sidebar As Boolean, color As Color, animate As Boolean)
            If animate AndAlso _hasState AndAlso active = _active AndAlso pressed = _pressed AndAlso sidebar = _sidebar AndAlso color = _color Then Return
            _hasState = True : _active = active : _pressed = pressed : _sidebar = sidebar : _color = color
            Dim back = _backScale.ScaleX, whole = _wholeScale.ScaleX, opacity = _background.Opacity
            Dim previous = DirectCast(_background.Background, SolidColorBrush).Color
            Detach()
            Dim targetBack = If(active OrElse sidebar, 1.0, 0.75), targetWhole = If(pressed, 0.98, 1.0), targetOpacity = If(active, 1.0, 0.0)
            If Not animate Then
                SetValues(targetBack, targetWhole, targetOpacity, color) : Return
            End If
            ' Keep the displayed base until WPF activates the new clocks on its next tick.
            SetValues(back, whole, opacity, previous)
            Dim time = If(active, 120, 180)
            Dim plan As New ParallelTimeline()
            plan.Children.Add(Tween(opacity, targetOpacity, time, If(active, 3, 0)))
            If active OrElse sidebar Then
                plan.Children.Add(Tween(back, targetBack, CInt(time * 1.6), 3))
            Else
                ' After=True waits for ALL preceding increments, including the 540ms row return.
                ' Keep the background near full size while fading, reset it only when invisible.
                Dim reset As New DoubleAnimationUsingKeyFrames With {.Duration = New Duration(TimeSpan.FromMilliseconds(541))}
                reset.KeyFrames.Add(New LinearDoubleKeyFrame(back, KeyTime.FromTimeSpan(TimeSpan.Zero)))
                reset.KeyFrames.Add(New EasingDoubleKeyFrame(0.996, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180)), Ease()))
                reset.KeyFrames.Add(New LinearDoubleKeyFrame(0.996, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(540))))
                reset.KeyFrames.Add(New LinearDoubleKeyFrame(0.75, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(541))))
                plan.Children.Add(reset)
            End If
            plan.Children.Add(Tween(whole, targetWhole, If(active, If(pressed, 108, 144), 540), 3))
            plan.Children.Add(New ColorAnimation(previous, color, TimeSpan.FromMilliseconds(time)))
            Dim clock = DirectCast(plan.CreateClock(True), ClockGroup)
            _clock = clock
            AddHandler clock.Completed, Sub()
                                            If _clock IsNot clock Then Return
                                            Detach() : SetValues(targetBack, targetWhole, targetOpacity, color)
                                        End Sub
            _background.ApplyAnimationClock(UIElement.OpacityProperty, DirectCast(clock.Children(0), AnimationClock))
            _backScale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, DirectCast(clock.Children(1), AnimationClock))
            _backScale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, DirectCast(clock.Children(1), AnimationClock))
            _wholeScale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, DirectCast(clock.Children(2), AnimationClock))
            _wholeScale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, DirectCast(clock.Children(2), AnimationClock))
            _fill.ApplyAnimationClock(SolidColorBrush.ColorProperty, DirectCast(clock.Children(3), AnimationClock))
        End Sub

        Private Shared Function Ease() As PowerEase
            Return New PowerEase With {.Power = 3, .EasingMode = EasingMode.EaseOut}
        End Function

        Private Shared Function Tween(origin As Double, target As Double, milliseconds As Integer, power As Integer) As DoubleAnimation
            Return New DoubleAnimation(origin, target, TimeSpan.FromMilliseconds(milliseconds)) With {.EasingFunction = If(power = 0, Nothing, Ease())}
        End Function

        Private Sub SetValues(back As Double, whole As Double, opacity As Double, color As Color)
            _backScale.ScaleX = back : _backScale.ScaleY = back
            _wholeScale.ScaleX = whole : _wholeScale.ScaleY = whole
            _background.Opacity = opacity
            _fill = New SolidColorBrush(color) : _background.Background = _fill
        End Sub

        Private Sub Detach()
            Dim old = _clock : _clock = Nothing
            _background.ApplyAnimationClock(UIElement.OpacityProperty, Nothing)
            _backScale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, Nothing)
            _backScale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, Nothing)
            _wholeScale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, Nothing)
            _wholeScale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, Nothing)
            _fill?.ApplyAnimationClock(SolidColorBrush.ColorProperty, Nothing)
            old?.Controller.Remove()
        End Sub
    End Class
End Namespace
