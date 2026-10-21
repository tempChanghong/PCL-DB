Imports System.Windows
Imports System.Windows.Media.Animation

Namespace Controls
    ' PCL 5ef81e4 MyCard.StartHeightAnimation and ModAnimation fluent curves.
    Friend Module PclCardMotion
        Friend Function HeightTransition(origin As Double, target As Double) As DoubleAnimationUsingKeyFrames
            Dim delta = target - origin, distance = Math.Abs(delta)
            Dim animation As New DoubleAnimationUsingKeyFrames
            animation.KeyFrames.Add(New LinearDoubleKeyFrame(origin, KeyTime.FromTimeSpan(TimeSpan.Zero)))
            If distance <= 800 Then
                animation.KeyFrames.Add(New EasingDoubleKeyFrame(target, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150)),
                    New PowerEase With {.Power = 5, .EasingMode = EasingMode.EaseOut}))
                animation.Duration = New Duration(TimeSpan.FromMilliseconds(150))
            Else
                Dim easeLength As Integer, easeTime As Integer, initialSpeed As Integer
                If delta < 0 Then
                    easeLength = 200 : easeTime = 150
                    initialSpeed = CInt((distance - easeLength) / 0.1)
                ElseIf distance > 3000 Then
                    initialSpeed = 5000
                    easeLength = CInt(distance - initialSpeed * 0.3) : easeTime = 400
                Else
                    easeLength = 150 : easeTime = 200 : initialSpeed = 4000
                End If
                ' AaHeight's Time is Integer; preserve VB's original rounding.
                Dim linearTime = CInt((distance - easeLength) / initialSpeed * 1000)
                Dim middle = origin + (distance - easeLength) * Math.Sign(delta)
                animation.KeyFrames.Add(New LinearDoubleKeyFrame(middle, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(linearTime))))
                animation.KeyFrames.Add(New EasingDoubleKeyFrame(target, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(linearTime + easeTime)),
                    New PclInitialSpeedEase(initialSpeed, easeTime / 1000.0, easeLength)))
                animation.Duration = New Duration(TimeSpan.FromMilliseconds(linearTime + easeTime))
            End If
            Return animation
        End Function

        Friend Function ArrowTransition(origin As Double, target As Double) As DoubleAnimation
            Return New DoubleAnimation(origin, target, TimeSpan.FromMilliseconds(250)) With {
                .EasingFunction = New PowerEase With {.Power = 5, .EasingMode = EasingMode.EaseOut}}
        End Function
    End Module

    ' The ratio curve is already ease-out; EaseIn mode avoids WPF reversing it.
    ' A dependency property retains alpha when WPF clones/freezes keyframes.
    Friend NotInheritable Class PclInitialSpeedEase
        Inherits EasingFunctionBase

        Private Shared ReadOnly AlphaProperty As DependencyProperty = DependencyProperty.Register("Alpha", GetType(Double), GetType(PclInitialSpeedEase), New PropertyMetadata(0.0))

        Public Sub New()
            EasingMode = EasingMode.EaseIn
        End Sub

        Friend Sub New(initialSpeed As Double, seconds As Double, distance As Double)
            Me.New()
            SetValue(AlphaProperty, Math.Max(0, initialSpeed * seconds / distance - 1))
        End Sub

        Protected Overrides Function EaseInCore(normalizedTime As Double) As Double
            Dim percent = Math.Max(0, Math.Min(1, normalizedTime))
            Dim alpha = CDbl(GetValue(AlphaProperty))
            If alpha = 0 Then Return percent
            Return (alpha + 1) * percent / (1 + alpha * percent)
        End Function

        Protected Overrides Function CreateInstanceCore() As Freezable
            Return New PclInitialSpeedEase
        End Function
    End Class
End Namespace
