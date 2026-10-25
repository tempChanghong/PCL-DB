Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media

Namespace Controls
    ' PCL MyComboBox / MyComboBoxItem: retain WPF selection, keyboard and accessibility behavior.
    Public Class PclComboBox
        Inherits ComboBox

        Public Sub New()
            PclTheme.Watch(Me, AddressOf RefreshColor, AddressOf RefreshColor)
            AddHandler Loaded, Sub() RefreshColor()
            AddHandler IsEnabledChanged, Sub() RefreshColor()
            AddHandler MouseEnter, Sub() RefreshColor()
            AddHandler MouseLeave, Sub() RefreshColor()
            AddHandler GotKeyboardFocus, Sub() RefreshColor()
            AddHandler LostKeyboardFocus, Sub() RefreshColor()
            AddHandler DropDownOpened, Sub() RefreshColor()
            AddHandler DropDownClosed, Sub() RefreshColor()
        End Sub

        Protected Overrides Function GetContainerForItemOverride() As DependencyObject
            Return New PclComboBoxItem()
        End Function

        Protected Overrides Function IsItemItsOwnContainerOverride(item As Object) As Boolean
            Return TypeOf item Is ComboBoxItem
        End Function

        Private Sub RefreshColor()
            Dim ink = If(Not IsEnabled, "ColorBrushGray5", If(IsDropDownOpen OrElse IsKeyboardFocusWithin, "ColorBrush3", If(IsMouseOver, "ColorBrush4", "ColorBrushBg0")))
            Dim fill = If(Not IsEnabled, "ColorBrushGray6", If(IsDropDownOpen OrElse IsMouseOver OrElse IsKeyboardFocusWithin, "ColorBrush7", "ColorBrushHalfWhite"))
            Dim duration = If(Not IsEnabled, 200, If(IsDropDownOpen, 10, 100))
            If IsLoaded Then
                ColorTo(Me, ForegroundProperty, Brush(ink).Color, duration)
                ColorTo(Me, BackgroundProperty, Brush(fill).Color, duration)
            Else
                SetResourceReference(ForegroundProperty, ink)
                SetResourceReference(BackgroundProperty, fill)
            End If
        End Sub

        Protected Overrides Sub OnPreviewMouseWheel(e As MouseWheelEventArgs)
            ' A closed selector inside the settings scroller must not silently change its value.
            If Not IsDropDownOpen Then
                e.Handled = True
                Dim parent = TryCast(Me.Parent, UIElement)
                parent?.RaiseEvent(New MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) With {.RoutedEvent = Mouse.MouseWheelEvent, .Source = Me})
                Return
            End If
            MyBase.OnPreviewMouseWheel(e)
        End Sub
    End Class

    Public Class PclComboBoxItem
        Inherits ComboBoxItem

        Public Sub New()
            PclTheme.Watch(Me, AddressOf RefreshColor, AddressOf RefreshColor)
            AddHandler Loaded, Sub() RefreshColor()
            AddHandler IsEnabledChanged, Sub() RefreshColor()
            AddHandler Selected, Sub() RefreshColor()
            AddHandler Unselected, Sub() RefreshColor()
            AddHandler MouseEnter, Sub() RefreshColor()
            AddHandler MouseLeave, Sub() RefreshColor()
            AddHandler IsKeyboardFocusWithinChanged, Sub() RefreshColor()
        End Sub

        Private Sub RefreshColor()
            Dim key = If(IsSelected, "ColorBrush6", If(IsMouseOver OrElse IsKeyboardFocusWithin, "ColorBrush8", "ColorBrushTransparent"))
            If IsLoaded Then
                ColorTo(Me, BackgroundProperty, Brush(key).Color, If(IsSelected OrElse IsMouseOver OrElse IsKeyboardFocusWithin, 100, 300))
            Else
                SetResourceReference(BackgroundProperty, key)
            End If
            Opacity = If(IsEnabled, 1, 0.4)
        End Sub
    End Class
End Namespace
