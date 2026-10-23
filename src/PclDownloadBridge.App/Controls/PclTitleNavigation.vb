Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Media.Animation

Namespace Controls
    ' PCL 5ef81e4 FormMain.PageChangeActual / PageChangeExit, first and sub layers.
    Friend NotInheritable Class PclTitleNavigation
        Private ReadOnly _host As FrameworkElement, _main As FrameworkElement, _inner As FrameworkElement
        Private ReadOnly _label As TextBlock, _enabled As Func(Of Boolean)
        Private ReadOnly _mainX As New TranslateTransform, _innerX As New TranslateTransform
        Private _clock As ClockGroup, _caption As ClockGroup
        Private _title As String
        ' PCL's initial XAML inset is -16; PageChangeExit leaves it at -18.
        Private _innerRestOffset As Double = -16
        Private ReadOnly _policy As PclControlMotionPolicy
        Friend Property PauseForChecks As Boolean
        Friend ReadOnly Property Transition As ClockGroup
            Get
                Return _clock
            End Get
        End Property
        Friend ReadOnly Property CaptionTransition As ClockGroup
            Get
                Return _caption
            End Get
        End Property

        Friend Sub New(host As FrameworkElement, main As FrameworkElement, inner As FrameworkElement, label As TextBlock, enabled As Func(Of Boolean))
            _host = host : _main = main : _inner = inner : _label = label : _enabled = enabled
            _policy = New PclControlMotionPolicy(host, AddressOf Finish, requiresInteraction:=False)
            main.RenderTransform = _mainX : inner.RenderTransform = _innerX
            AddHandler host.Unloaded, Sub() Finish()
            AddHandler host.IsVisibleChanged, Sub() If Not host.IsVisible Then Finish()
            AddHandler host.SizeChanged, Sub() Finish()
            Finish()
        End Sub

        Friend Sub SelectTitle(title As String)
            Dim wasInner = _title IsNot Nothing, isInner = title IsNot Nothing
            If _title = title AndAlso wasInner = isInner Then Return
            _title = title
            If wasInner = isInner Then
                ChangeCaption()
                Return
            End If
            If Not isInner Then _innerRestOffset = -18
            Dim mainOpacity = _main.Opacity, innerOpacity = _inner.Opacity, mainOffset = _mainX.X, innerOffset = _innerX.X
            StopClocks()
            If Not _enabled() OrElse Not _host.IsLoaded OrElse Not _host.IsVisible Then
                Finish() : Return
            End If
            _main.Visibility = Visibility.Visible : _inner.Visibility = Visibility.Visible
            _main.IsHitTestVisible = Not isInner : _inner.IsHitTestVisible = isInner
            If isInner Then _label.Text = title
            _label.Opacity = 1
            _main.Opacity = mainOpacity : _inner.Opacity = innerOpacity
            _mainX.X = mainOffset : _innerX.X = innerOffset
            Dim outgoing = If(isInner, _main, _inner), incoming = If(isInner, _inner, _main)
            Dim outX = If(isInner, _mainX, _innerX), inX = If(isInner, _innerX, _mainX)
            Dim plan As New ParallelTimeline With {.Duration = TimeSpan.FromMilliseconds(550)}
            plan.Children.Add(New DoubleAnimation(outgoing.Opacity, 0, TimeSpan.FromMilliseconds(150)))
            plan.Children.Add(New DoubleAnimation(outX.X, If(isInner, 12.0, -18.0), TimeSpan.FromMilliseconds(150)) With {
                .EasingFunction = New PowerEase With {.Power = If(isInner, 2, 3), .EasingMode = EasingMode.EaseIn}})
            plan.Children.Add(New DoubleAnimation(incoming.Opacity, 1, TimeSpan.FromMilliseconds(150)) With {.BeginTime = TimeSpan.FromMilliseconds(200)})
            plan.Children.Add(New DoubleAnimation(inX.X, 0, TimeSpan.FromMilliseconds(350)) With {
                .BeginTime = TimeSpan.FromMilliseconds(200), .EasingFunction = New PclTitleBackEase(If(isInner, 3, 2))})
            Dim clock = DirectCast(plan.CreateClock(True), ClockGroup) : _clock = clock
            AddHandler clock.Completed, Sub() If _clock Is clock Then Finish()
            outgoing.ApplyAnimationClock(UIElement.OpacityProperty, DirectCast(clock.Children(0), AnimationClock))
            outX.ApplyAnimationClock(TranslateTransform.XProperty, DirectCast(clock.Children(1), AnimationClock))
            incoming.ApplyAnimationClock(UIElement.OpacityProperty, DirectCast(clock.Children(2), AnimationClock))
            inX.ApplyAnimationClock(TranslateTransform.XProperty, DirectCast(clock.Children(3), AnimationClock))
            If PauseForChecks Then clock.Controller.Pause()
        End Sub

        Private Sub ChangeCaption()
            Dim opacity = _label.Opacity
            StopCaption()
            If _title Is Nothing OrElse _clock IsNot Nothing OrElse Not _enabled() OrElse Not _host.IsLoaded OrElse Not _host.IsVisible Then
                _label.Text = If(_title, "") : Return
            End If
            Dim plan As New ParallelTimeline With {.Duration = TimeSpan.FromMilliseconds(310)}
            Dim fade As New DoubleAnimationUsingKeyFrames
            fade.KeyFrames.Add(New LinearDoubleKeyFrame(opacity, KeyTime.FromTimeSpan(TimeSpan.Zero)))
            fade.KeyFrames.Add(New LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(130))))
            fade.KeyFrames.Add(New LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160))))
            fade.KeyFrames.Add(New LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(310))))
            plan.Children.Add(fade)
            Dim clock = DirectCast(plan.CreateClock(True), ClockGroup) : _caption = clock
            AddHandler clock.CurrentTimeInvalidated, Sub()
                                                        If _caption Is clock AndAlso clock.CurrentTime.HasValue AndAlso clock.CurrentTime.Value.TotalMilliseconds >= 130 Then _label.Text = _title
                                                    End Sub
            AddHandler clock.Completed, Sub()
                                            If _caption IsNot clock Then Return
                                            _label.Text = _title : StopCaption()
                                        End Sub
            _label.ApplyAnimationClock(UIElement.OpacityProperty, DirectCast(clock.Children(0), AnimationClock))
            If PauseForChecks Then clock.Controller.Pause()
        End Sub

        Friend Sub Finish()
            StopClocks()
            Dim inner = _title IsNot Nothing
            _main.Visibility = If(inner, Visibility.Collapsed, Visibility.Visible)
            _inner.Visibility = If(inner, Visibility.Visible, Visibility.Collapsed)
            _main.IsHitTestVisible = Not inner : _inner.IsHitTestVisible = inner
            _main.Opacity = If(inner, 0, 1) : _inner.Opacity = If(inner, 1, 0)
            _mainX.X = If(inner, 12, 0) : _innerX.X = If(inner, 0, _innerRestOffset)
            _label.Text = If(_title, "") : _label.Opacity = 1
        End Sub
        Private Sub StopCaption()
            Dim old = _caption : _caption = Nothing
            old?.Controller.Remove() : _label.BeginAnimation(UIElement.OpacityProperty, Nothing) : _label.Opacity = 1
        End Sub
        Private Sub StopClocks()
            Dim old = _clock : _clock = Nothing
            old?.Controller.Remove()
            For Each element In {_main, _inner}
                element.BeginAnimation(UIElement.OpacityProperty, Nothing)
            Next
            _mainX.BeginAnimation(TranslateTransform.XProperty, Nothing) : _innerX.BeginAnimation(TranslateTransform.XProperty, Nothing)
            StopCaption()
        End Sub
    End Class

    Friend NotInheritable Class PclTitleBackEase
        Inherits EasingFunctionBase
        Private Shared ReadOnly PowerProperty As DependencyProperty = DependencyProperty.Register("Power", GetType(Double), GetType(PclTitleBackEase), New PropertyMetadata(3.0))
        Public Sub New()
            EasingMode = EasingMode.EaseIn
        End Sub
        Friend Sub New(power As Double)
            Me.New() : SetValue(PowerProperty, power)
        End Sub
        Protected Overrides Function EaseInCore(progress As Double) As Double
            Return PclMotionCurves.Back(progress, CDbl(GetValue(PowerProperty)))
        End Function
        Protected Overrides Function CreateInstanceCore() As Freezable
            Return New PclTitleBackEase()
        End Function
    End Class
End Namespace
