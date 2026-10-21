Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media

Namespace Controls
    ' Non-closable red MyHint at PCL 5ef81e4; geometry and colors are kept intact.
    Public Class PclHint
        Inherits Border
        Private ReadOnly _text As TextBlock
        Public Property Text As String
            Get
                Return _text.Text
            End Get
            Set(value As String)
                _text.Text = value
            End Set
        End Property
        Public Sub New()
            UseLayoutRounding = True : SnapsToDevicePixels = True
            BorderThickness = New Thickness(3, 0, 0, 0) : CornerRadius = New CornerRadius(2)
            Background = New SolidColorBrush(Color.FromRgb(255, 221, 223))
            BorderBrush = New SolidColorBrush(Color.FromRgb(216, 41, 41))
            _text = New TextBlock With {.IsHitTestVisible = False, .LineHeight = 16, .Margin = New Thickness(0, 0, 0, -1),
                .Padding = New Thickness(12, 9, 12, 9), .VerticalAlignment = VerticalAlignment.Center,
                .TextWrapping = TextWrapping.Wrap, .Foreground = New SolidColorBrush(Color.FromRgb(191, 11, 11))}
            Dim panel As New Grid With {.UseLayoutRounding = False, .SnapsToDevicePixels = False}
            panel.Children.Add(_text)
            Child = panel
        End Sub
    End Class
End Namespace
