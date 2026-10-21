Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Controls.Primitives
Imports System.Windows.Media

Namespace Controls
    ' Geometry and 18/12 DIP proportions from PCL MyCheckBox.xaml.
    Public Class PclCheckBox
        Inherits CheckBox

        Public Sub New()
            PclTheme.Watch(Me, Sub() RefreshAppearance())
            AddHandler Loaded, Sub() RefreshAppearance()
            AddHandler Checked, Sub() RefreshAppearance()
            AddHandler Unchecked, Sub() RefreshAppearance()
            AddHandler IsEnabledChanged, Sub() RefreshAppearance()
            AddHandler MouseEnter, Sub() RefreshAppearance()
            AddHandler MouseLeave, Sub() RefreshAppearance()
            AddHandler IsKeyboardFocusWithinChanged, Sub() RefreshAppearance()
            AddHandler PreviewMouseLeftButtonDown, Sub() RefreshAppearance(True)
            AddHandler PreviewMouseLeftButtonUp, Sub() RefreshAppearance()
        End Sub

        Public Overrides Sub OnApplyTemplate()
            MyBase.OnApplyTemplate()
            RefreshAppearance()
        End Sub

        Private Sub RefreshAppearance(Optional pressed As Boolean = False)
            Dim outline = TryCast(Template?.FindName("ShapeBorder", Me), Border)
            Dim mark = TryCast(Template?.FindName("ShapeCheck", Me), Shapes.Path)
            If outline Is Nothing OrElse mark Is Nothing Then Return
            Dim ink = If(Not IsEnabled, "ColorBrushGray5", If(IsMouseOver OrElse IsKeyboardFocusWithin, "ColorBrush3", If(IsChecked = True, "ColorBrush2", "ColorBrush1")))
            Dim textInk = If(Not IsEnabled, "ColorBrushGray5", If(IsMouseOver OrElse IsKeyboardFocusWithin, "ColorBrush3", "ColorBrush1"))
            Dim fill = If(pressed AndAlso IsEnabled, "ColorBrushBg1", "ColorBrushHalfWhite")
            Dim scale = DirectCast(mark.RenderTransform, ScaleTransform)
            If scale.IsFrozen Then
                scale = scale.Clone()
                mark.RenderTransform = scale
            End If
            Dim target = If(IsChecked = True, 1.0, 0.0)
            If IsLoaded Then
                Dim duration = If(IsMouseOver, 100, 200)
                ColorTo(outline, Border.BorderBrushProperty, Brush(ink).Color, duration)
                ColorTo(outline, Border.BackgroundProperty, Brush(fill).Color, 100)
                ColorTo(Me, ForegroundProperty, Brush(textInk).Color, duration)
                NumberTo(scale, ScaleTransform.ScaleXProperty, target, 150)
                NumberTo(scale, ScaleTransform.ScaleYProperty, target, 150)
            Else
                outline.SetResourceReference(Border.BorderBrushProperty, ink)
                outline.SetResourceReference(Border.BackgroundProperty, fill)
                SetResourceReference(ForegroundProperty, textInk)
                scale.ScaleX = target : scale.ScaleY = target
            End If
        End Sub

        Friend Sub Activate()
            If IsEnabled Then MyBase.OnClick()
        End Sub
    End Class
End Namespace
