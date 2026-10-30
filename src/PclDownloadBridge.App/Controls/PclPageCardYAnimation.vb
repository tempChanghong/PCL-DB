Imports System.Windows
Imports System.Windows.Media.Animation

Namespace Controls
    ' MyPageRight combines AaTranslateY(5, 250, fluent) and
    ' AaTranslateY(11, 350, back). Scale its remaining displacement when a
    ' horizontal page transition reverses, rather than jumping back to -16.
    Friend NotInheritable Class PclPageCardYAnimation
        Inherits DoubleAnimationBase

        Private Shared ReadOnly OriginProperty As DependencyProperty =
            DependencyProperty.Register("Origin", GetType(Double), GetType(PclPageCardYAnimation), New PropertyMetadata(-16.0))

        Public Sub New()
            Duration = New Duration(TimeSpan.FromMilliseconds(350))
        End Sub

        Friend Sub New(origin As Double)
            Me.New()
            SetValue(OriginProperty, origin)
        End Sub

        Friend Function Sample(milliseconds As Double) As Double
            Return Global.PclDownloadBridge.Presentation.PageMotion.CardOffset(CDbl(GetValue(OriginProperty)), milliseconds)
        End Function

        Protected Overrides Function GetCurrentValueCore(origin As Double, destination As Double, clock As AnimationClock) As Double
            Return Sample(clock.CurrentTime.GetValueOrDefault().TotalMilliseconds)
        End Function

        Protected Overrides Function CreateInstanceCore() As Freezable
            Return New PclPageCardYAnimation()
        End Function
    End Class
End Namespace
