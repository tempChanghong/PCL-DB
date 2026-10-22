Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Media.Imaging
Imports PclDownloadBridge.Core

Namespace Controls
    ' PCL MyResourceItem.xaml at 5ef81e4: 64px row, 50px icon, 14/12px text.
    ' Use the same owned hover/press clocks as MyListItem's PCL port.
    Public NotInheritable Class PclResourceItem
        Inherits Grid
        Public Event Click As RoutedEventHandler
        Private ReadOnly _back As Border
        Private ReadOnly _motion As PclListHoverMotion
        Private _pressed As Boolean
        Public ReadOnly Property Icon As Image
        Public Sub New(project As ModpackProject)
            Height = 64 : Background = Brushes.Transparent : Cursor = Cursors.Hand : Focusable = True
            RenderTransformOrigin = New Point(0.5, 0.5) : RenderTransform = New ScaleTransform(1, 1)
            _back = New Border With {.CornerRadius = New CornerRadius(6), .BorderThickness = New Thickness(1), .Background = PclAnimation.Brush("ColorBrush7"), .BorderBrush = PclAnimation.Brush("ColorBrush6"), .Opacity = 0, .RenderTransform = New ScaleTransform(0.75, 0.75), .RenderTransformOrigin = New Point(0.5, 0.5), .IsHitTestVisible = False}
            Children.Add(_back)
            _motion = New PclListHoverMotion(_back, DirectCast(RenderTransform, ScaleTransform))
            Dim body As New Grid With {.Margin = New Thickness(7, 0, 7, 0), .IsHitTestVisible = False}
            body.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(50)})
            body.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(8)})
            body.ColumnDefinitions.Add(New ColumnDefinition())
            Icon = New Image With {.Width = 50, .Height = 50, .Stretch = Stretch.UniformToFill, .Clip = New RectangleGeometry(New Rect(0, 0, 50, 50), 6, 6), .Source = New BitmapImage(New Uri("pack://application:,,,/Images/Blocks/NoIcon.png"))}
            RenderOptions.SetBitmapScalingMode(Icon, BitmapScalingMode.HighQuality)
            body.Children.Add(Icon)
            Dim text As New Grid With {.VerticalAlignment = VerticalAlignment.Center}
            For Each rowHeight In {17, 18, 16} : text.RowDefinitions.Add(New RowDefinition With {.Height = New GridLength(rowHeight)}) : Next
            Dim title As New TextBlock With {.Text = project.Name, .FontSize = 14, .TextTrimming = TextTrimming.CharacterEllipsis}
            title.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1") : text.Children.Add(title)
            Dim summary As New TextBlock With {.Text = project.Summary, .FontSize = 12, .TextTrimming = TextTrimming.CharacterEllipsis}
            summary.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3") : SetRow(summary, 1) : text.Children.Add(summary)
            Dim details As New Grid
            details.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1.3, GridUnitType.Star)})
            details.ColumnDefinitions.Add(New ColumnDefinition()) : details.ColumnDefinitions.Add(New ColumnDefinition()) : details.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1.2, GridUnitType.Star)})
            Dim values = {If(project.GameVersions.Length > 0, project.GameVersions.Last(), "整合包"), "下载 " & project.Downloads.ToString("N0"), project.Updated.ToLocalTime().ToString("yyyy/MM/dd"), project.Platform.ToString()}
            For i = 0 To values.Length - 1
                Dim label As New TextBlock With {.Text = values(i), .FontSize = 12, .TextTrimming = TextTrimming.CharacterEllipsis, .Margin = New Thickness(0, 0, 6, 0)}
                label.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray4") : SetColumn(label, i) : details.Children.Add(label)
            Next
            SetRow(details, 2) : text.Children.Add(details) : SetColumn(text, 2) : body.Children.Add(text) : Children.Add(body)
            ToolTip = project.Name & vbCrLf & project.Summary & vbCrLf & project.Platform.ToString() & " · 更新于 " & project.Updated.ToLocalTime().ToString("yyyy/MM/dd")
            Automation.AutomationProperties.SetName(Me, project.Name & "，" & project.Platform.ToString())
            AddHandler MouseEnter, Sub() Refresh()
            AddHandler MouseLeave, Sub() Refresh()
            AddHandler GotKeyboardFocus, Sub() Refresh()
            AddHandler LostKeyboardFocus, Sub() ResetPressed()
            AddHandler LostMouseCapture, Sub() ResetPressed()
            AddHandler IsHitTestVisibleChanged, Sub() ResetPressed(False)
            AddHandler IsEnabledChanged, Sub() ResetPressed(False)
            AddHandler Unloaded, Sub() ResetPressed(False)
            PclTheme.Watch(Me, Sub()
                                   _back.BorderBrush = PclAnimation.Brush("ColorBrush6") : Refresh(False)
                               End Sub)
            Refresh(False)
        End Sub
        Private Sub ResetPressed(Optional animate As Boolean = True)
            _pressed = False : Refresh(animate)
        End Sub
        Private Sub Refresh(Optional animate As Boolean = True)
            _motion.Update(IsEnabled AndAlso IsHitTestVisible AndAlso (IsMouseOver OrElse IsKeyboardFocusWithin), _pressed, False, PclAnimation.Brush("ColorBrush7").Color, animate AndAlso IsLoaded AndAlso IsVisible AndAlso SystemParameters.ClientAreaAnimation)
        End Sub
        Protected Overrides Sub OnMouseLeftButtonDown(e As MouseButtonEventArgs)
            MyBase.OnMouseLeftButtonDown(e)
            If Not IsEnabled OrElse Not IsHitTestVisible Then Return
            Focus() : _pressed = True : CaptureMouse() : Refresh() : e.Handled = True
        End Sub
        Protected Overrides Sub OnMouseLeftButtonUp(e As MouseButtonEventArgs)
            MyBase.OnMouseLeftButtonUp(e)
            Dim activate = _pressed AndAlso IsMouseOver AndAlso IsEnabled AndAlso IsHitTestVisible
            _pressed = False : ReleaseMouseCapture() : Refresh() : e.Handled = True
            If activate Then RaiseEvent Click(Me, New RoutedEventArgs())
        End Sub
        Protected Overrides Sub OnKeyDown(e As KeyEventArgs)
            MyBase.OnKeyDown(e)
            If Not IsEnabled OrElse Not IsHitTestVisible OrElse e.IsRepeat OrElse (e.Key <> Key.Enter AndAlso e.Key <> Key.Space) Then Return
            _pressed = True : Refresh() : e.Handled = True
        End Sub
        Protected Overrides Sub OnKeyUp(e As KeyEventArgs)
            MyBase.OnKeyUp(e)
            If e.Key <> Key.Enter AndAlso e.Key <> Key.Space Then Return
            Dim activate = _pressed AndAlso IsEnabled AndAlso IsHitTestVisible
            _pressed = False : Refresh() : e.Handled = True
            If activate Then RaiseEvent Click(Me, New RoutedEventArgs())
        End Sub
    End Class
End Namespace
