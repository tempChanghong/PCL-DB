Imports System.Windows
Imports System.Windows.Media
Imports System.Windows.Media.Animation

Namespace Controls
    ' PCL 5ef81e4 ModAnimation: fluent powers and the cosine Back curve.
    Friend Module PclMotionCurves
        Friend Function Fluent(progress As Double, power As Double) As Double
            Return Global.PclDownloadBridge.Presentation.MotionCurves.Fluent(progress, power)
        End Function

        Friend Function Back(progress As Double, Optional power As Double = 3) As Double
            Return Global.PclDownloadBridge.Presentation.MotionCurves.Back(progress, power)
        End Function

        Friend Function PageY(milliseconds As Double) As Double
            ' AaTranslateY(5,250,fluent) + AaTranslateY(11,350,back), running in parallel.
            Return Global.PclDownloadBridge.Presentation.MotionCurves.PageY(milliseconds)
        End Function
    End Module

    Friend NotInheritable Class PclPageYAnimation
        Inherits DoubleAnimationBase
        Public Sub New()
            Duration = New Duration(TimeSpan.FromMilliseconds(350))
        End Sub
        Protected Overrides Function GetCurrentValueCore(origin As Double, destination As Double, clock As AnimationClock) As Double
            Return PclMotionCurves.PageY(If(clock.CurrentTime, TimeSpan.Zero).TotalMilliseconds)
        End Function
        Protected Overrides Function CreateInstanceCore() As Freezable
            Return New PclPageYAnimation()
        End Function
    End Class

    ' MyMsgInput.Loaded: a 40px offset returning with Weak Back over 300ms.
    Friend NotInheritable Class PclDialogYAnimation
        Inherits DoubleAnimationBase
        Public Sub New()
            Duration = New Duration(TimeSpan.FromMilliseconds(300))
        End Sub
        Protected Overrides Function GetCurrentValueCore(origin As Double, destination As Double, clock As AnimationClock) As Double
            Return 40 * (1 - PclMotionCurves.Back(If(clock.CurrentTime, TimeSpan.Zero).TotalMilliseconds / 300, 2))
        End Function
        Protected Overrides Function CreateInstanceCore() As Freezable
            Return New PclDialogYAnimation()
        End Function
    End Class

    ' Remaining increments of overlapping wheel inputs, rather than restarting a target tween.
    Friend NotInheritable Class PclWheelAnimation
        Inherits DoubleAnimationBase
        Private Shared ReadOnly OriginProperty As DependencyProperty = DependencyProperty.Register("Origin", GetType(Double), GetType(PclWheelAnimation), New PropertyMetadata(0.0))
        Private Shared ReadOnly InputsProperty As DependencyProperty = DependencyProperty.Register("Inputs", GetType(PointCollection), GetType(PclWheelAnimation), New PropertyMetadata(Nothing))
        Public Sub New()
            Duration = New Duration(TimeSpan.FromMilliseconds(300))
        End Sub
        Friend Sub New(origin As Double, inputs As IEnumerable(Of Point))
            Me.New()
            SetValue(OriginProperty, origin)
            SetValue(InputsProperty, New PointCollection(inputs))
        End Sub
        Friend ReadOnly Property Inputs As PointCollection
            Get
                Return DirectCast(GetValue(InputsProperty), PointCollection)
            End Get
        End Property
        Friend Function Sample(milliseconds As Double) As Double
            Dim result = CDbl(GetValue(OriginProperty))
            If Inputs IsNot Nothing Then
                For Each pulse As Point In Inputs
                    result += pulse.Y * (PclMotionCurves.Fluent((pulse.X + milliseconds) / 300, 6) - PclMotionCurves.Fluent(pulse.X / 300, 6))
                Next
            End If
            Return result
        End Function
        Protected Overrides Function GetCurrentValueCore(origin As Double, destination As Double, clock As AnimationClock) As Double
            Return Sample(If(clock.CurrentTime, TimeSpan.Zero).TotalMilliseconds)
        End Function
        Protected Overrides Function CreateInstanceCore() As Freezable
            Return New PclWheelAnimation()
        End Function
    End Class
End Namespace
