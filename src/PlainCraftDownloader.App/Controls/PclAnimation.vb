Imports System.Windows
Imports System.Windows.Media
Imports System.Windows.Media.Animation

Namespace Controls
    Friend Module PclAnimation
        Friend Function Brush(key As String) As SolidColorBrush
            Return DirectCast(Application.Current.FindResource(key), SolidColorBrush).Clone()
        End Function
        Friend Sub ColorTo(target As DependencyObject, prop As DependencyProperty, color As Color, milliseconds As Integer)
            Dim previous = TryCast(target.GetValue(prop), SolidColorBrush)
            Dim mutable As New SolidColorBrush(If(previous Is Nothing, Colors.Transparent, previous.Color))
            target.SetValue(prop, mutable)
            If PclTheme.Changing Then
                mutable.Color = color
                Return
            End If
            mutable.BeginAnimation(SolidColorBrush.ColorProperty, New ColorAnimation(color, TimeSpan.FromMilliseconds(milliseconds)))
        End Sub
        Friend Sub NumberTo(target As Animatable, prop As DependencyProperty, value As Double, milliseconds As Integer)
            target.BeginAnimation(prop, New DoubleAnimation(value, TimeSpan.FromMilliseconds(milliseconds)) With {.EasingFunction = New PowerEase With {.Power = 3, .EasingMode = EasingMode.EaseOut}})
        End Sub
    End Module
End Namespace
