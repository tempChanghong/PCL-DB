Imports System.Windows
Imports System.Windows.Media
Imports System.Windows.Media.Animation

Namespace Controls
    ' MyExtraTextButton.Show owns its outer scale; inner button state and the
    ' horizontal installation-page transition keep their own clocks and gates.
    Friend NotInheritable Class PclInstallActionMotion
        Private ReadOnly _host As FrameworkElement
        Private ReadOnly _enabled As Func(Of Boolean)
        Private ReadOnly _scale As New ScaleTransform()
        Private _shown As Boolean
        Private _transition As ClockGroup
        Friend Property PauseForChecks As Boolean

        Friend Sub New(host As FrameworkElement, enabled As Func(Of Boolean))
            _host = host : _enabled = enabled
            host.RenderTransformOrigin = New Point(0.5, 0.5)
            host.RenderTransform = _scale
            Settle()
            AddHandler host.Unloaded, Sub() Finish()
            AddHandler host.IsVisibleChanged, Sub() If Not host.IsVisible AndAlso _transition IsNot Nothing Then Finish()
        End Sub

        Friend ReadOnly Property Transition As ClockGroup
            Get
                Return _transition
            End Get
        End Property
        Friend ReadOnly Property Scale As ScaleTransform
            Get
                Return _scale
            End Get
        End Property

        Friend Sub SelectShown(value As Boolean, Optional animate As Boolean = True)
            If _shown = value Then Return
            Dim scaleOrigin = _scale.ScaleX, alphaOrigin = _host.Opacity
            _shown = value
            StopTransition()
            ' A collapsed host is expected on first entry; its parent decides
            ' whether the page is visible enough to start an animation.
            Dim parent = TryCast(VisualTreeHelper.GetParent(_host), UIElement)
            If Not animate OrElse Not _enabled() OrElse Not _host.IsLoaded OrElse parent Is Nothing OrElse Not parent.IsVisible Then
                Settle() : Return
            End If
            _scale.ScaleX = scaleOrigin : _scale.ScaleY = scaleOrigin
            _host.Opacity = alphaOrigin
            _host.Visibility = Visibility.Visible
            _host.IsHitTestVisible = False
            Dim plan As New ParallelTimeline With {.Duration = New Duration(TimeSpan.FromMilliseconds(If(value, 450, 100)))}
            plan.Children.Add(New PclInstallActionAnimation(scaleOrigin, value))
            plan.Children.Add(New PclInstallActionAnimation(alphaOrigin, value, opacity:=True))
            Dim clock = DirectCast(plan.CreateClock(True), ClockGroup)
            If PauseForChecks Then clock.Controller.Pause()
            _transition = clock
            AddHandler clock.CurrentTimeInvalidated,
                Sub()
                    If _transition Is clock AndAlso _shown AndAlso clock.CurrentTime.GetValueOrDefault().TotalMilliseconds >= 130 Then _host.IsHitTestVisible = True
                End Sub
            AddHandler clock.Completed, Sub() If _transition Is clock Then Finish()
            _scale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, DirectCast(clock.Children(0), AnimationClock))
            _scale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, DirectCast(clock.Children(0), AnimationClock))
            _host.ApplyAnimationClock(UIElement.OpacityProperty, DirectCast(clock.Children(1), AnimationClock))
        End Sub

        Friend Sub Finish()
            StopTransition()
            Settle()
        End Sub
        Private Sub StopTransition()
            Dim previous = _transition
            _transition = Nothing
            previous?.Controller.Remove()
            _scale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, Nothing)
            _scale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, Nothing)
            _host.ApplyAnimationClock(UIElement.OpacityProperty, Nothing)
        End Sub
        Private Sub Settle()
            _host.Visibility = If(_shown, Visibility.Visible, Visibility.Collapsed)
            _scale.ScaleX = If(_shown, 1, 0) : _scale.ScaleY = _scale.ScaleX
            _host.Opacity = If(_shown, 1, 0)
            _host.IsHitTestVisible = _shown
        End Sub
    End Class
End Namespace
