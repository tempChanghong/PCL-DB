Imports System.Windows
Imports System.Windows.Media.Animation

Namespace Controls
    ' PCL 5ef81e4 MyExtraTextButton.Show: parallel scale increments, with a
    ' separate opacity track. The origin is sampled on reversal so a new
    ' navigation does not reset the visible button to zero between frames.
    Friend NotInheritable Class PclInstallActionAnimation
        Inherits DoubleAnimationBase

        Private Shared ReadOnly OriginProperty As DependencyProperty = DependencyProperty.Register("Origin", GetType(Double), GetType(PclInstallActionAnimation), New PropertyMetadata(0.0))
        Private Shared ReadOnly ShowingProperty As DependencyProperty = DependencyProperty.Register("Showing", GetType(Boolean), GetType(PclInstallActionAnimation), New PropertyMetadata(True))
        Private Shared ReadOnly OpacityChannelProperty As DependencyProperty = DependencyProperty.Register("OpacityChannel", GetType(Boolean), GetType(PclInstallActionAnimation), New PropertyMetadata(False))

        Public Sub New()
            Duration = New Duration(TimeSpan.FromMilliseconds(450))
        End Sub

        Friend Sub New(origin As Double, shown As Boolean, Optional opacity As Boolean = False)
            Me.New()
            SetValue(OriginProperty, origin)
            SetValue(ShowingProperty, shown)
            SetValue(OpacityChannelProperty, opacity)
            Duration = New Duration(TimeSpan.FromMilliseconds(If(shown, If(opacity, 130, 450), 100)))
        End Sub

        Friend Function Sample(milliseconds As Double) As Double
            Dim origin = CDbl(GetValue(OriginProperty))
            Dim showing = CBool(GetValue(ShowingProperty))
            If CBool(GetValue(OpacityChannelProperty)) Then
                If showing Then Return origin + (1 - origin) * Progress((milliseconds - 50) / 80)
                Return origin * (1 - Progress((milliseconds - 50) / 50))
            End If
            If showing Then
                Return Math.Max(0, origin + (0.15 - origin) * PclMotionCurves.Back((milliseconds - 50) / 400) +
                    0.85 * PclMotionCurves.Fluent((milliseconds - 50) / 160, 3))
            End If
            Return origin * (1 - Math.Pow(Progress(milliseconds / 100), 2))
        End Function

        Private Shared Function Progress(value As Double) As Double
            Return Math.Max(0, Math.Min(1, value))
        End Function

        Protected Overrides Function GetCurrentValueCore(origin As Double, destination As Double, clock As AnimationClock) As Double
            Return Sample(If(clock.CurrentTime, TimeSpan.Zero).TotalMilliseconds)
        End Function

        Protected Overrides Function CreateInstanceCore() As Freezable
            Return New PclInstallActionAnimation()
        End Function
    End Class
End Namespace
