Imports System.Windows
Imports System.Windows.Media
Imports System.Windows.Media.Animation

Namespace Controls
    Friend Enum PclPressKind
        ButtonDown
        ButtonUp
        ButtonLeave
        IconDown
        IconUp
        IconLeave
    End Enum

    ' PCL 5ef81e4 MyButton/MyIconButton: AaScaleTransform increments run in parallel.
    Friend NotInheritable Class PclPressAnimation
        Inherits DoubleAnimationBase
        Private Shared ReadOnly OriginProperty As DependencyProperty = DependencyProperty.Register("Origin", GetType(Double), GetType(PclPressAnimation), New PropertyMetadata(1.0))
        Private Shared ReadOnly KindProperty As DependencyProperty = DependencyProperty.Register("Kind", GetType(PclPressKind), GetType(PclPressAnimation), New PropertyMetadata(PclPressKind.ButtonDown))

        Public Sub New()
            Duration = New Duration(TimeSpan.FromMilliseconds(700))
        End Sub

        Friend Sub New(origin As Double, kind As PclPressKind)
            Me.New()
            SetValue(OriginProperty, origin) : SetValue(KindProperty, kind)
            Duration = New Duration(TimeSpan.FromMilliseconds(Length(kind)))
        End Sub

        Private Shared Function Length(kind As PclPressKind) As Integer
            Select Case kind
                Case PclPressKind.ButtonDown : Return 700
                Case PclPressKind.ButtonUp : Return 310
                Case PclPressKind.ButtonLeave : Return 800
                Case PclPressKind.IconDown : Return 400
                Case Else : Return 250
            End Select
        End Function

        Friend Function Sample(milliseconds As Double) As Double
            Dim start = CDbl(GetValue(OriginProperty))
            Select Case DirectCast(GetValue(KindProperty), PclPressKind)
                Case PclPressKind.ButtonDown
                    Return start + (0.955 - start) * PclMotionCurves.Fluent(milliseconds / 80, 5) - 0.01 * PclMotionCurves.Fluent(milliseconds / 700, 3)
                Case PclPressKind.ButtonUp
                    Return start + (1 - start) * PclMotionCurves.Fluent((milliseconds - 10) / 300, 3)
                Case PclPressKind.ButtonLeave
                    Return start + (1 - start) * PclMotionCurves.Fluent(milliseconds / 800, 4)
                Case PclPressKind.IconDown
                    Return start + (0.8 - start) * PclMotionCurves.Fluent(milliseconds / 400, 4)
                Case PclPressKind.IconUp
                    Return start + (1.05 - start) * PclMotionCurves.Back(milliseconds / 250, 2) - 0.05 * PclMotionCurves.Fluent(milliseconds / 250, 4)
                Case Else
                    Return start + (1 - start) * PclMotionCurves.Fluent(milliseconds / 250, 3)
            End Select
        End Function

        Protected Overrides Function GetCurrentValueCore(origin As Double, destination As Double, clock As AnimationClock) As Double
            Return Sample(If(clock.CurrentTime, TimeSpan.Zero).TotalMilliseconds)
        End Function

        Protected Overrides Function CreateInstanceCore() As Freezable
            Return New PclPressAnimation()
        End Function
    End Class

    ' Each control owns one shared X/Y clock. Page gates and removal cancel the gesture.
    Friend NotInheritable Class PclPressMotion
        Private ReadOnly _owner As FrameworkElement
        Private ReadOnly _scale As ScaleTransform
        Private ReadOnly _cancelPress As Action
        Private ReadOnly _canAnimate As Func(Of Boolean)
        Private ReadOnly _pauseForChecks As Func(Of Boolean)
        Private _clock As AnimationClock

        Friend Sub New(owner As FrameworkElement, scale As ScaleTransform, cancelPress As Action, Optional canAnimate As Func(Of Boolean) = Nothing, Optional pauseForChecks As Func(Of Boolean) = Nothing)
            _owner = owner : _scale = scale : _cancelPress = cancelPress
            _canAnimate = canAnimate
            _pauseForChecks = pauseForChecks
            AddHandler owner.Unloaded, Sub() Cancel()
            AddHandler owner.IsVisibleChanged, Sub()
                                                   If Not owner.IsVisible Then Cancel()
                                               End Sub
            AddHandler owner.IsHitTestVisibleChanged, Sub()
                                                          If Not owner.IsHitTestVisible Then Cancel()
                                                      End Sub
            AddHandler owner.IsEnabledChanged, Sub()
                                                   If Not owner.IsEnabled Then Cancel()
                                               End Sub
        End Sub

        Friend ReadOnly Property Transition As AnimationClock
            Get
                Return _clock
            End Get
        End Property

        Friend Sub Start(kind As PclPressKind)
            If Not _owner.IsLoaded Then
                ResetScale() : Return
            End If
            If Not _owner.IsVisible OrElse Not _owner.IsHitTestVisible OrElse Not _owner.IsEnabled Then
                Cancel() : Return
            End If
            If _canAnimate IsNot Nothing AndAlso Not _canAnimate() Then
                ResetScale() : Return
            End If
            Dim origin = _scale.ScaleX
            ResetScale()
            ' A newly attached WPF clock may not be active until the next tick.
            _scale.ScaleX = origin : _scale.ScaleY = origin
            Dim clock = DirectCast(New PclPressAnimation(origin, kind).CreateClock(True), AnimationClock)
            If _pauseForChecks IsNot Nothing AndAlso _pauseForChecks() Then clock.Controller.Pause()
            _clock = clock
            If kind <> PclPressKind.ButtonDown AndAlso kind <> PclPressKind.IconDown Then
                AddHandler clock.Completed, Sub()
                                                If _clock Is clock Then ResetScale()
                                            End Sub
            End If
            _scale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, clock)
            _scale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, clock)
        End Sub

        Friend Sub Cancel()
            _cancelPress() : ResetScale()
        End Sub

        Private Sub ResetScale()
            Dim old = _clock : _clock = Nothing
            _scale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, Nothing)
            _scale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, Nothing)
            old?.Controller.Remove()
            _scale.ScaleX = 1 : _scale.ScaleY = 1
        End Sub
    End Class
End Namespace
