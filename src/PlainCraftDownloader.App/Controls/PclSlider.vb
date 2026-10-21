Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input

Namespace Controls
    ' Visual proportions and colors from PCL MySlider; WPF owns dragging, keys and RangeValue automation.
    Public Class PclSlider
        Inherits Slider

        Public Sub New()
            PclTheme.Watch(Me, AddressOf RefreshColor)
            AddHandler Loaded, Sub() RefreshColor()
            AddHandler IsEnabledChanged, Sub() RefreshColor()
            AddHandler MouseEnter, Sub() RefreshColor()
            AddHandler MouseLeave, Sub() RefreshColor()
            AddHandler GotKeyboardFocus, Sub() RefreshColor()
            AddHandler LostKeyboardFocus, Sub() RefreshColor()
            AddHandler IsMouseCaptureWithinChanged, Sub() RefreshColor()
        End Sub

        Private Sub RefreshColor()
            Dim active = IsMouseOver OrElse IsKeyboardFocusWithin OrElse IsMouseCaptureWithin
            Dim key = If(Not IsEnabled, "ColorBrushGray5", If(active, "ColorBrush3", "ColorBrushBg0"))
            If IsLoaded Then
                ColorTo(Me, ForegroundProperty, Brush(key).Color, If(Not IsEnabled, 200, If(active, 40, 100)))
            Else
                SetResourceReference(ForegroundProperty, key)
            End If
        End Sub

        Protected Overrides Sub OnPreviewMouseWheel(e As MouseWheelEventArgs)
            e.Handled = True
            Dim parent = TryCast(Me.Parent, UIElement)
            parent?.RaiseEvent(New MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) With {.RoutedEvent = Mouse.MouseWheelEvent, .Source = Me})
        End Sub
    End Class
End Namespace
