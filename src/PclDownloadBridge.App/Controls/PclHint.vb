Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media

Namespace Controls
    Public Enum PclHintTheme
        Red
        Blue
        Yellow
    End Enum
    ' Non-closable MyHint at PCL 5ef81e4; geometry and original theme colors.
    Public Class PclHint
        Inherits Border
        Private ReadOnly _text As TextBlock
        Private _theme As PclHintTheme
        Public Property Theme As PclHintTheme
            Get
                Return _theme
            End Get
            Set(value As PclHintTheme)
                _theme = value
                RefreshColors()
            End Set
        End Property
        Private Sub RefreshColors()
            If _text Is Nothing Then Return
            ' Keep fixed PCL's original light colors; CE MyHint uses semantic hue
            ' and CurrentTone (dark L7=.225 / L2=.75), independent of accent hue.
            If PclTheme.IsDarkMode Then
                Dim hue = If(Theme = PclHintTheme.Yellow, 40.0, If(Theme = PclHintTheme.Blue, 210.0, 355.0))
                Background = New SolidColorBrush(PclTheme.Hsl2(hue, 90, 22.5))
                BorderBrush = New SolidColorBrush(PclTheme.Hsl2(hue, 90, 75))
                _text.Foreground = New SolidColorBrush(PclTheme.Hsl2(hue, 90, 75))
            Else
                Dim colors = If(Theme = PclHintTheme.Yellow, {"#ffebd7", "#f57a00", "#d86c00"}, If(Theme = PclHintTheme.Blue, {"#d9ecff", "#1172d4", "#0f64b8"}, {"#ffdddf", "#d82929", "#bf0b0b"}))
                Background = New SolidColorBrush(CType(ColorConverter.ConvertFromString(colors(0)), Color))
                BorderBrush = New SolidColorBrush(CType(ColorConverter.ConvertFromString(colors(1)), Color))
                _text.Foreground = New SolidColorBrush(CType(ColorConverter.ConvertFromString(colors(2)), Color))
            End If
        End Sub
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
            RefreshColors()
            PclTheme.Watch(Me, AddressOf RefreshColors)
        End Sub
    End Class
End Namespace
