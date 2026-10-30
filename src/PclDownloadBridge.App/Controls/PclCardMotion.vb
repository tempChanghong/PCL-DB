Imports System.Windows
Imports System.Windows.Media.Animation

Namespace Controls
    ' PCL 5ef81e4 MyCard.StartHeightAnimation and ModAnimation fluent curves.
    Friend Module PclCardMotion
        Friend Function HeightTransition(origin As Double, target As Double) As DoubleAnimationUsingKeyFrames
            Return Global.PclDownloadBridge.Presentation.CardMotion.HeightTransition(origin, target,
                Function(initialSpeed, seconds, distance) New PclInitialSpeedEase(initialSpeed, seconds, distance))
        End Function

        Friend Function ArrowTransition(origin As Double, target As Double) As DoubleAnimation
            Return Global.PclDownloadBridge.Presentation.CardMotion.ArrowTransition(origin, target)
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
            SetValue(AlphaProperty, Global.PclDownloadBridge.Presentation.CardMotion.InitialSpeedAlpha(initialSpeed, seconds, distance))
        End Sub

        Protected Overrides Function EaseInCore(normalizedTime As Double) As Double
            Dim percent = Math.Max(0, Math.Min(1, normalizedTime))
            Dim alpha = CDbl(GetValue(AlphaProperty))
            Return Global.PclDownloadBridge.Presentation.CardMotion.InitialSpeedValue(percent, alpha)
        End Function

        Protected Overrides Function CreateInstanceCore() As Freezable
            Return New PclInitialSpeedEase
        End Function
    End Class
End Namespace
