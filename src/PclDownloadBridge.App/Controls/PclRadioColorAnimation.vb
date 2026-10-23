Imports System.Windows
Imports System.Windows.Media
Imports System.Windows.Media.Animation

Namespace Controls
    ' PCL ModAnimation.AniType.Color + ModBase.MyColor.Lerp / ClampToByte:
    ' interpolate ARGB bytes linearly, retaining fractional progress until rounding.
    Friend NotInheritable Class PclRadioColorAnimation
        Inherits ColorAnimationBase
        Private Shared ReadOnly OriginProperty As DependencyProperty = DependencyProperty.Register("Origin", GetType(Color), GetType(PclRadioColorAnimation), New PropertyMetadata(Colors.Transparent))
        Private Shared ReadOnly DestinationProperty As DependencyProperty = DependencyProperty.Register("Destination", GetType(Color), GetType(PclRadioColorAnimation), New PropertyMetadata(Colors.Transparent))
        Public Sub New()
        End Sub
        Friend Sub New(origin As Color, destination As Color, milliseconds As Integer)
            SetValue(OriginProperty, origin) : SetValue(DestinationProperty, destination)
            Duration = TimeSpan.FromMilliseconds(milliseconds)
        End Sub
        Friend ReadOnly Property Destination As Color
            Get
                Return DirectCast(GetValue(DestinationProperty), Color)
            End Get
        End Property
        Friend Function Sample(progress As Double) As Color
            Dim origin = DirectCast(GetValue(OriginProperty), Color), target = Destination
            progress = Math.Max(0, Math.Min(1, progress))
            Return Color.FromArgb(Channel(origin.A, target.A, progress), Channel(origin.R, target.R, progress), Channel(origin.G, target.G, progress), Channel(origin.B, target.B, progress))
        End Function
        Private Shared Function Channel(first As Byte, last As Byte, progress As Double) As Byte
            Return CByte(Math.Round(first + (CInt(last) - first) * progress))
        End Function
        Protected Overrides Function GetCurrentValueCore(defaultOriginValue As Color, defaultDestinationValue As Color, animationClock As AnimationClock) As Color
            Return Sample(animationClock.CurrentProgress.GetValueOrDefault())
        End Function
        Protected Overrides Function CreateInstanceCore() As Freezable
            Return New PclRadioColorAnimation()
        End Function
    End Class
End Namespace
