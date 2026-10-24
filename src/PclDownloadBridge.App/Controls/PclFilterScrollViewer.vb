Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input

Namespace Controls
    ' Keep PCL's one-line filter layout. Only an overflowing row consumes the
    ' wheel; a normal row lets the surrounding download page scroll vertically.
    Public NotInheritable Class PclFilterScrollViewer
        Inherits ScrollViewer

        Private _requestedOffset As Double = Double.NaN
        Private ReadOnly _issuedOffsets As New List(Of Double)

        Public Sub New()
            AddHandler ScrollChanged, AddressOf OffsetChanged
            AddHandler IsEnabledChanged, Sub() If Not IsEnabled Then StopScrolling()
            AddHandler IsHitTestVisibleChanged, Sub() If Not IsHitTestVisible Then StopScrolling()
            AddHandler IsVisibleChanged, Sub() If Not IsVisible Then StopScrolling()
            AddHandler Unloaded, Sub() StopScrolling()
            AddHandler PreviewMouseDown, Sub() StopScrolling()
            AddHandler PreviewKeyDown, Sub() StopScrolling()
            AddHandler PreviewTouchDown, Sub() StopScrolling()
            AddHandler PreviewGotKeyboardFocus, Sub() StopScrolling()
        End Sub

        Friend Sub StopScrolling()
            Dim pending = Not Double.IsNaN(_requestedOffset)
            _requestedOffset = Double.NaN : _issuedOffsets.Clear()
            ' ScrollViewer queues native offsets. Revoke an unacknowledged wheel
            ' target at the displayed position before another owner takes input.
            If pending Then ScrollToHorizontalOffset(HorizontalOffset)
        End Sub

        Private Sub OffsetChanged(sender As Object, e As ScrollChangedEventArgs)
            If e.OriginalSource IsNot Me OrElse Double.IsNaN(_requestedOffset) Then Return
            If e.ViewportWidthChange <> 0 OrElse e.ExtentWidthChange <> 0 Then
                StopScrolling() : Return
            End If
            If Math.Abs(HorizontalOffset - _requestedOffset) < 0.01 Then
                _requestedOffset = Double.NaN : _issuedOffsets.Clear()
            ElseIf e.HorizontalChange <> 0 Then
                Dim own = _issuedOffsets.FindIndex(Function(offset) Math.Abs(offset - HorizontalOffset) < 0.01)
                If own >= 0 Then
                    _issuedOffsets.RemoveRange(0, own + 1)
                Else
                    StopScrolling()
                End If
            End If
        End Sub

        Protected Overrides Sub OnMouseWheel(e As MouseWheelEventArgs)
            If e.Handled Then Return
            If Not IsEnabled OrElse Not IsHitTestVisible OrElse Not IsVisible Then
                e.Handled = True : Return
            End If
            If ScrollableWidth <= 0 Then Return
            Dim origin = If(Double.IsNaN(_requestedOffset), HorizontalOffset, _requestedOffset)
            _requestedOffset = Math.Max(0, Math.Min(ScrollableWidth, origin - e.Delta / 3.0))
            _issuedOffsets.Add(_requestedOffset)
            If _issuedOffsets.Count > 64 Then _issuedOffsets.RemoveAt(0)
            ScrollToHorizontalOffset(_requestedOffset)
            e.Handled = True
        End Sub
    End Class
End Namespace
