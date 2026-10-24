Imports System.Windows.Controls
Imports System.Windows.Input

Namespace Controls
    ' Keep PCL's one-line filter layout. Only an overflowing row consumes the
    ' wheel; a normal row lets the surrounding download page scroll vertically.
    Public NotInheritable Class PclFilterScrollViewer
        Inherits ScrollViewer

        Protected Overrides Sub OnMouseWheel(e As MouseWheelEventArgs)
            If e.Handled Then Return
            If Not IsEnabled OrElse Not IsHitTestVisible OrElse Not IsVisible Then
                e.Handled = True : Return
            End If
            If ScrollableWidth <= 0 Then Return
            ScrollToHorizontalOffset(HorizontalOffset - e.Delta / 3.0)
            e.Handled = True
        End Sub
    End Class
End Namespace
