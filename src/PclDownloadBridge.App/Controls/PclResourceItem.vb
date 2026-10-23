Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Media.Imaging
Imports PclDownloadBridge.Core

Namespace Controls
    ' PCL MyResourceItem.xaml at 5ef81e4: 64px row, 50px icon, 14/12px text.
    ' The resource row has its own backing-plate animation and gesture ownership.
    Public NotInheritable Class PclResourceItem
        Inherits Grid
        Public Event Click As RoutedEventHandler
        Private ReadOnly _back As Border
        Private ReadOnly _motion As PclResourceHoverMotion
        Private _mouseDown As Boolean
        Private _keyDown As Key = Key.None
        Friend ReadOnly Property MotionPolicy As PclControlMotionPolicy
        Friend ReadOnly Property HoverTransition As Animation.ClockGroup
            Get
                Return _motion.Transition
            End Get
        End Property
        Public ReadOnly Property Icon As Image
        Public Sub New(project As ModpackProject)
            Height = 64 : Background = Brushes.Transparent : Cursor = Cursors.Hand : Focusable = True
            RenderTransformOrigin = New Point(0.5, 0.5) : RenderTransform = New ScaleTransform(1, 1)
            _back = New Border With {.CornerRadius = New CornerRadius(3), .BorderThickness = New Thickness(1), .SnapsToDevicePixels = True, .Background = PclAnimation.Brush("ColorBrush7"), .BorderBrush = PclAnimation.Brush("ColorBrush6"), .Opacity = 0, .RenderTransform = New ScaleTransform(0.8, 0.8), .RenderTransformOrigin = New Point(0.5, 0.5), .IsHitTestVisible = False}
            Children.Add(_back)
            _motion = New PclResourceHoverMotion(_back)
            MotionPolicy = New PclControlMotionPolicy(Me, Sub() ResetPressed(False))
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
            AddHandler MouseLeave, Sub()
                                       _mouseDown = False : ReleaseOwnCapture() : Refresh()
                                   End Sub
            AddHandler GotKeyboardFocus, Sub() Refresh()
            AddHandler LostKeyboardFocus, Sub() ResetPressed()
            AddHandler LostMouseCapture, Sub()
                                            _mouseDown = False : Refresh()
                                        End Sub
            PclTheme.Watch(Me, Sub()
                                   _back.BorderBrush = PclAnimation.Brush("ColorBrush6") : ResetPressed(False)
                               End Sub)
            Refresh(False)
        End Sub
        Private Sub ResetPressed(Optional animate As Boolean = True)
            _mouseDown = False : _keyDown = Key.None : ReleaseOwnCapture() : Refresh(animate)
        End Sub
        Private Sub ReleaseOwnCapture()
            If IsMouseCaptured Then ReleaseMouseCapture()
        End Sub
        Private Sub Refresh(Optional animate As Boolean = True)
            Dim active = MotionPolicy.Interactive AndAlso (IsMouseOver OrElse IsKeyboardFocusWithin)
            Dim pressed = active AndAlso (_mouseDown OrElse _keyDown <> Key.None)
            Dim color = PclAnimation.Brush(If(active, If(pressed, "ColorBrush6", "ColorBrushBg1"), "ColorBrush7")).Color
            _motion.Update(active, pressed, color, animate AndAlso MotionPolicy.CanAnimate AndAlso Not PclTheme.Changing, MotionPolicy.PauseForChecks)
        End Sub
        Protected Overrides Sub OnMouseLeftButtonDown(e As MouseButtonEventArgs)
            MyBase.OnMouseLeftButtonDown(e)
            If Not MotionPolicy.Interactive Then Return
            e.Handled = True
            If _keyDown <> Key.None Then Return
            Focus() : _mouseDown = True : CaptureMouse() : Refresh()
        End Sub
        Protected Overrides Sub OnMouseLeftButtonUp(e As MouseButtonEventArgs)
            MyBase.OnMouseLeftButtonUp(e)
            Dim activate = _mouseDown AndAlso IsMouseOver AndAlso MotionPolicy.Interactive
            _mouseDown = False : ReleaseOwnCapture() : Refresh() : e.Handled = True
            If activate Then RaiseEvent Click(Me, New RoutedEventArgs())
        End Sub
        Protected Overrides Sub OnKeyDown(e As KeyEventArgs)
            MyBase.OnKeyDown(e)
            If Not MotionPolicy.Interactive OrElse (e.Key <> Key.Enter AndAlso e.Key <> Key.Space) Then Return
            e.Handled = True
            If e.IsRepeat OrElse _mouseDown OrElse _keyDown <> Key.None Then Return
            _keyDown = e.Key : Refresh()
        End Sub
        Protected Overrides Sub OnKeyUp(e As KeyEventArgs)
            MyBase.OnKeyUp(e)
            If e.Key <> Key.Enter AndAlso e.Key <> Key.Space Then Return
            e.Handled = True
            If _keyDown <> e.Key Then Return
            Dim activate = MotionPolicy.Interactive
            _keyDown = Key.None : Refresh()
            If activate Then RaiseEvent Click(Me, New RoutedEventArgs())
        End Sub
    End Class
End Namespace
