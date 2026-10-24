Imports System.Windows
Imports System.Windows.Media.Animation

Namespace Controls
    Friend Enum PclInstallPressKind
        Down
        Up
        Leave
    End Enum

    ' PCL 5ef81e4 MyExtraTextButton: the inner press transform is independent
    ' from Show's outer scale. ModAnimation Strong=4, default Fluent/Back=3.
    Friend NotInheritable Class PclInstallPressAnimation
        Inherits DoubleAnimationBase
        Private Shared ReadOnly OriginProperty As DependencyProperty = DependencyProperty.Register("Origin", GetType(Double), GetType(PclInstallPressAnimation), New PropertyMetadata(1.0))
        Private Shared ReadOnly KindProperty As DependencyProperty = DependencyProperty.Register("Kind", GetType(PclInstallPressKind), GetType(PclInstallPressAnimation), New PropertyMetadata(PclInstallPressKind.Down))
        Public Sub New()
            Duration = New Duration(TimeSpan.FromMilliseconds(800))
        End Sub
        Friend Sub New(origin As Double, kind As PclInstallPressKind)
            Me.New()
            SetValue(OriginProperty, origin) : SetValue(KindProperty, kind)
            Duration = New Duration(TimeSpan.FromMilliseconds(If(kind = PclInstallPressKind.Down, 800, If(kind = PclInstallPressKind.Up, 300, 500))))
        End Sub
        Friend Function Sample(milliseconds As Double) As Double
            Dim origin = CDbl(GetValue(OriginProperty))
            Select Case DirectCast(GetValue(KindProperty), PclInstallPressKind)
                Case PclInstallPressKind.Down
                    Return origin + (0.85 - origin) * PclMotionCurves.Fluent(milliseconds / 800, 4) - 0.05 * PclMotionCurves.Fluent(milliseconds / 60, 3)
                Case PclInstallPressKind.Up
                    Return origin + (1 - origin) * PclMotionCurves.Back(milliseconds / 300)
                Case Else
                    Return origin + (1 - origin) * PclMotionCurves.Fluent(milliseconds / 500, 3)
            End Select
        End Function
        Protected Overrides Function GetCurrentValueCore(origin As Double, destination As Double, clock As AnimationClock) As Double
            Return Sample(If(clock.CurrentTime, TimeSpan.Zero).TotalMilliseconds)
        End Function
        Protected Overrides Function CreateInstanceCore() As Freezable
            Return New PclInstallPressAnimation()
        End Function
    End Class
End Namespace
