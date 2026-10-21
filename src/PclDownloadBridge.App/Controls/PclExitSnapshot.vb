Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Media.Imaging

Namespace Controls
    ' Disjoint exit pieces share one bounded frozen bitmap, never live controls.
    Friend NotInheritable Class PclExitSnapshot
        Friend ReadOnly Bitmap As BitmapSource
        Friend ReadOnly Width As Double
        Friend ReadOnly Height As Double
        Friend ReadOnly Layers As New List(Of Piece)
        Friend NotInheritable Class Piece
            Friend Mask As Geometry
            Friend Delay As Integer
            Friend ScrollBar As Boolean
            Friend Viewport As Rect
        End Class
        Private Sub New(bitmap As BitmapSource, width As Double, height As Double)
            Me.Bitmap = bitmap : Me.Width = width : Me.Height = height
        End Sub
        Friend Function ImageFor(piece As Piece) As Image
            Return New Image With {.Source = Bitmap, .Width = Width, .Height = Height, .Stretch = Stretch.Fill,
                .Clip = piece.Mask, .IsHitTestVisible = False, .RenderTransform = New TranslateTransform()}
        End Function
        Friend Shared Function Capture(host As FrameworkElement, units As IEnumerable(Of FrameworkElement), bar As FrameworkElement) As PclExitSnapshot
            Try
                Dim w = host.ActualWidth, h = host.ActualHeight
                If w <= 0 OrElse h <= 0 Then Return Nothing
                Dim device = PresentationSource.FromVisual(host)?.CompositionTarget?.TransformToDevice
                Dim scale = Math.Min(If(device.HasValue, device.Value.M11, 1), Math.Sqrt(4000000 / (w * h)))
                Dim visual As New DrawingVisual()
                ' Absolute VisualBrush coordinates include the source's layout
                ' offset. Excluding it shifts the frozen page by the title height.
                Dim offset = VisualTreeHelper.GetOffset(host)
                Using drawing = visual.RenderOpen()
                    drawing.DrawRectangle(New VisualBrush(host) With {.ViewboxUnits = BrushMappingMode.Absolute,
                        .Viewbox = New Rect(offset.X, offset.Y, w, h), .Stretch = Stretch.Fill}, Nothing, New Rect(0, 0, w, h))
                End Using
                Dim bitmap As New RenderTargetBitmap(Math.Max(1, CInt(Math.Floor(w * scale))), Math.Max(1, CInt(Math.Floor(h * scale))), 96 * scale, 96 * scale, PixelFormats.Pbgra32)
                bitmap.Render(visual) : bitmap.Freeze()
                Dim snapshot As New PclExitSnapshot(bitmap, w, h)
                Dim covered As Geometry = Geometry.Empty
                Dim append As Action(Of Rect, Rect, Integer, Boolean) =
                    Sub(bounds, viewport, delay, scrollbar)
                        If bounds.IsEmpty OrElse bounds.Width <= 0 OrElse bounds.Height <= 0 Then Return
                        Dim box As Geometry = New RectangleGeometry(bounds)
                        Dim mask As Geometry = New CombinedGeometry(GeometryCombineMode.Exclude, box, covered)
                        mask.Freeze()
                        snapshot.Layers.Add(New Piece With {.Mask = mask, .Delay = delay, .ScrollBar = scrollbar, .Viewport = viewport})
                        covered = New CombinedGeometry(GeometryCombineMode.Union, covered, box)
                        covered.Freeze()
                    End Sub
                If bar IsNot Nothing Then append(VisibleBounds(bar, host), ClipViewport(bar, host), 0, True)
                Dim index = 0
                For Each unit In units
                    Dim bounds = VisibleBounds(unit, host, 4)
                    If bounds.IsEmpty Then Continue For
                    append(bounds, ClipViewport(unit, host), index * 15, False)
                    index += 1
                    If index = 16 Then Exit For
                Next
                Dim remainder As Geometry = New CombinedGeometry(GeometryCombineMode.Exclude, New RectangleGeometry(New Rect(0, 0, w, h)), covered)
                remainder.Freeze()
                snapshot.Layers.Insert(0, New Piece With {.Mask = remainder, .Delay = 0, .Viewport = New Rect(0, 0, w, h)})
                Return snapshot
            Catch ex As InvalidOperationException
                Return Nothing
            Catch ex As ArgumentException
                Return Nothing
            End Try
        End Function

        ' Intersect clipping ancestors so a partly scrolled card never moves pixels
        ' belonging to the fixed toolbar. Padding retains its external shadow.
        Friend Shared Function VisibleBounds(unit As FrameworkElement, host As FrameworkElement, Optional padding As Double = 0) As Rect
            If unit Is Nothing OrElse Not unit.IsVisible OrElse unit.ActualWidth <= 0 OrElse unit.ActualHeight <= 0 Then Return Rect.Empty
            Try
                Dim local As New Rect(unit.RenderSize)
                local.Inflate(padding, padding)
                Dim bounds = unit.TransformToAncestor(host).TransformBounds(local)
                bounds.Intersect(ClipViewport(unit, host))
                Return If(bounds.Width > 0 AndAlso bounds.Height > 0, bounds, Rect.Empty)
            Catch ex As InvalidOperationException
                Return Rect.Empty
            End Try
        End Function

        Friend Shared Function ClipViewport(unit As FrameworkElement, host As FrameworkElement) As Rect
            Try
                Dim bounds As New Rect(host.RenderSize)
                Dim ancestor = TryCast(VisualTreeHelper.GetParent(unit), FrameworkElement)
                While ancestor IsNot Nothing AndAlso ancestor IsNot host AndAlso Not bounds.IsEmpty
                    If ancestor.ClipToBounds OrElse TypeOf ancestor Is ScrollContentPresenter Then
                        bounds.Intersect(ancestor.TransformToAncestor(host).TransformBounds(New Rect(ancestor.RenderSize)))
                    End If
                    If ancestor.Clip IsNot Nothing Then bounds.Intersect(ancestor.TransformToAncestor(host).TransformBounds(ancestor.Clip.Bounds))
                    ancestor = TryCast(VisualTreeHelper.GetParent(ancestor), FrameworkElement)
                End While
                Return bounds
            Catch ex As InvalidOperationException
                Return Rect.Empty
            End Try
        End Function
    End Class
End Namespace
