Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Media.Imaging

Namespace Controls
    ' The layout XAML and lazy info/background/logo metrics are from PCL MyListItem.
    Partial Public Class PclListItem
        Public Event Click As RoutedEventHandler
        Public Shared ReadOnly TitleProperty As DependencyProperty = DependencyProperty.Register("Title", GetType(String), GetType(PclListItem), New PropertyMetadata(""))
        Public Shared ReadOnly ForegroundProperty As DependencyProperty = DependencyProperty.Register("Foreground", GetType(Brush), GetType(PclListItem), New PropertyMetadata(Brushes.Black))
        Public Shared ReadOnly FontSizeProperty As DependencyProperty = DependencyProperty.Register("FontSize", GetType(Double), GetType(PclListItem), New PropertyMetadata(14.0))
        Public Property Title As String
            Get
                Return CStr(GetValue(TitleProperty))
            End Get
            Set(value As String)
                SetValue(TitleProperty, value)
            End Set
        End Property
        Public Property Foreground As Brush
            Get
                Return DirectCast(GetValue(ForegroundProperty), Brush)
            End Get
            Set(value As Brush)
                SetValue(ForegroundProperty, value)
            End Set
        End Property
        Public Property FontSize As Double
            Get
                Return CDbl(GetValue(FontSizeProperty))
            End Get
            Set(value As Double)
                SetValue(FontSizeProperty, value)
            End Set
        End Property
        Private ReadOnly _info As TextBlock
        Private ReadOnly _background As Border
        Private ReadOnly _check As Border
        Private _infoTag As Border
        Private _infoTagText As TextBlock
        Private _logo As FrameworkElement
        Private _logoStretch As Stretch = Stretch.Fill
        Public Property LogoStretch As Stretch
            Get
                Return _logoStretch
            End Get
            Set(value As Stretch)
                _logoStretch = value
                If TypeOf _logo Is Image Then DirectCast(_logo, Image).Stretch = value
            End Set
        End Property
        Private _selected As Boolean
        Private _down As Boolean
        Private _outlineLogo As Boolean
        Private ReadOnly _hoverMotion As PclListHoverMotion
        Private ReadOnly _selectionMotion As PclListSelectionMotion
        Friend ReadOnly Property MotionPolicy As PclControlMotionPolicy
        Private ReadOnly Property AcceptsInput As Boolean
            Get
                Return IsEnabled AndAlso IsHitTestVisible AndAlso (Not IsLoaded OrElse IsVisible)
            End Get
        End Property
        Friend ReadOnly Property SelectionTransition As Animation.ClockGroup
            Get
                Return _selectionMotion.Transition
            End Get
        End Property
        Friend ReadOnly Property HoverTransition As Animation.ClockGroup
            Get
                Return _hoverMotion.Transition
            End Get
        End Property
        Public Property OutlineLogo As Boolean
            Get
                Return _outlineLogo
            End Get
            Set(value As Boolean)
                _outlineLogo = value
                RefreshMetrics()
            End Set
        End Property
        Private _isSidebar As Boolean
        Public Property IsSidebar As Boolean
            Get
                Return _isSidebar
            End Get
            Set(value As Boolean)
                If _isSidebar = value Then Return
                _isSidebar = value
                RefreshMetrics()
                RefreshHover(True)
            End Set
        End Property
        Public Property Selected As Boolean
            Get
                Return _selected
            End Get
            Set(value As Boolean)
                If _selected = value Then Return
                _selected = value
                _selectionMotion?.Update(value, AllowSelectionMotion())
                RefreshHover()
            End Set
        End Property
        Public Property Info As String
            Get
                Return _info.Text
            End Get
            Set(value As String)
                _info.Text = value
                _info.Visibility = If(String.IsNullOrEmpty(value), Visibility.Collapsed, Visibility.Visible)
                RefreshMetrics()
            End Set
        End Property
        ' CE MyListItem.PanTags / Tags: a separate auto-sized gray label before
        ' LabInfo. Add its column lazily so ordinary PCL rows retain their layout.
        Public Property InfoTag As String
            Get
                Return If(_infoTagText?.Text, "")
            End Get
            Set(value As String)
                value = If(value, "")
                If value = InfoTag Then Return
                If value.Length = 0 Then
                    Children.Remove(_infoTag)
                    ColumnDefinitions.RemoveAt(3)
                    SetColumnSpan(LabTitle, 1) : SetColumn(_info, 3) : SetColumnSpan(_background, 5)
                    _infoTag = Nothing : _infoTagText = Nothing
                    Return
                End If
                If _infoTag Is Nothing Then
                    ColumnDefinitions.Insert(3, New ColumnDefinition With {.Width = GridLength.Auto})
                    SetColumnSpan(LabTitle, 2) : SetColumn(_info, 4) : SetColumnSpan(_background, 6)
                    _infoTagText = New TextBlock With {.FontSize = 11}
                    _infoTagText.SetResourceReference(TextBlock.ForegroundProperty, "PclResourceTagInk")
                    _infoTag = New Border With {.Child = _infoTagText, .Padding = New Thickness(3, 1, 3, 1), .CornerRadius = New CornerRadius(3),
                        .Margin = New Thickness(3.5, 0, 0, 0), .VerticalAlignment = VerticalAlignment.Bottom, .IsHitTestVisible = False, .SnapsToDevicePixels = True, .UseLayoutRounding = False}
                    _infoTag.SetResourceReference(Border.BackgroundProperty, "PclResourceTagBackground")
                    SetColumn(_infoTag, 3) : SetRow(_infoTag, 2) : Children.Add(_infoTag)
                End If
                _infoTagText.Text = value
            End Set
        End Property
        Public Property Logo As String
            Get
                Return CStr(If(_logo?.Tag, ""))
            End Get
            Set(value As String)
                If _logo IsNot Nothing Then Children.Remove(_logo)
                _logo = Nothing
                value = If(value, "")
                If value.Length = 0 Then
                    RefreshMetrics()
                    Return
                End If
                If value.EndsWith(".png", StringComparison.OrdinalIgnoreCase) Then
                    _logo = New Image With {.Source = New BitmapImage(New Uri(value, UriKind.RelativeOrAbsolute)), .Margin = New Thickness(4, 5, 3, 5), .Stretch = _logoStretch}
                    RenderOptions.SetBitmapScalingMode(_logo, BitmapScalingMode.Linear)
                Else
                    _logo = New Shapes.Path With {.Data = Geometry.Parse(value), .Fill = Foreground, .Stretch = Stretch.Uniform, .Margin = New Thickness(If(Height < 40, 6, 8), 8, If(Height < 40, 4, 6), 8), .HorizontalAlignment = HorizontalAlignment.Center, .VerticalAlignment = VerticalAlignment.Center}
                    _logo.SetBinding(Shapes.Path.FillProperty, New System.Windows.Data.Binding("Foreground") With {.Source = Me})
                End If
                _logo.Tag = value
                _logo.IsHitTestVisible = False
                SetColumn(_logo, 2)
                SetRowSpan(_logo, 4)
                Children.Add(_logo)
                RefreshMetrics()
            End Set
        End Property
        Private Sub RefreshMetrics()
            ' PCL MyListItem.OnSizeChanged uses declared Height, including Auto:
            ' NaN < 40 is false, so Auto keeps the full-height row's metrics.
            Dim compact = Height < 40
            ColumnCheck.Width = New GridLength(If(IsSidebar, 6, If(compact, 4, 2)))
            ColumnLogo.Width = New GridLength(If(_logo Is Nothing, 0, 34) + If(compact, 0, 4))
            LabTitle.Margin = New Thickness(4, 0, 0, If(compact, 0, 2))
            If _logo Is Nothing Then Return
            _logo.Margin = If(TypeOf _logo Is Image, New Thickness(4, 5, 3, 5),
                New Thickness(If(compact, 6, 8), 8, If(compact, 4, 6), 8))
            RefreshLogoStyle()
        End Sub
        Private Sub RefreshLogoStyle()
            Dim path = TryCast(_logo, Shapes.Path)
            If path Is Nothing Then Return
            If Not _outlineLogo Then
                If path.Stroke IsNot Nothing Then
                    path.ClearValue(Shapes.Path.StrokeProperty)
                    path.SetBinding(Shapes.Path.FillProperty, New System.Windows.Data.Binding("Foreground") With {.Source = Me})
                End If
                path.Width = Double.NaN : path.Height = Double.NaN
                Return
            End If
            path.ClearValue(Shapes.Path.FillProperty)
            path.Fill = Nothing
            path.SetBinding(Shapes.Path.StrokeProperty, New System.Windows.Data.Binding("Foreground") With {.Source = Me})
            path.StrokeThickness = 1.65
            path.StrokeLineJoin = PenLineJoin.Round
            path.StrokeStartLineCap = PenLineCap.Round
            path.StrokeEndLineCap = PenLineCap.Round
            path.Width = 18 : path.Height = 18
            path.Margin = New Thickness(0)
        End Sub
        Public Sub New()
            InitializeComponent()
            SetResourceReference(ForegroundProperty, "ColorBrush1")
            Cursor = Cursors.Hand
            Focusable = IsHitTestVisible
            AddHandler IsHitTestVisibleChanged, Sub()
                                                   Focusable = IsHitTestVisible
                                               End Sub
            AddHandler GotKeyboardFocus, Sub() RefreshHover()
            AddHandler LostKeyboardFocus, Sub()
                                              If _down Then CancelPress() Else RefreshHover()
                                          End Sub
            _background = New Border With {.CornerRadius = New CornerRadius(6), .BorderThickness = New Thickness(1), .Background = Brush("ColorBrush7"), .BorderBrush = Brush("ColorBrush6"), .Opacity = 0, .IsHitTestVisible = False, .RenderTransform = New ScaleTransform(0.75, 0.75), .RenderTransformOrigin = New Point(0.5, 0.5)}
            SetColumnSpan(_background, 5)
            SetRowSpan(_background, 4)
            Children.Insert(0, _background)
            _info = New TextBlock With {.FontSize = 12, .Margin = New Thickness(4, 0, 0, 0), .Opacity = 0.6, .Visibility = Visibility.Collapsed, .IsHitTestVisible = False, .TextTrimming = TextTrimming.CharacterEllipsis}
            SetColumn(_info, 3)
            SetRow(_info, 2)
            Children.Add(_info)
            _check = New Border With {.Width = 5, .Height = 0, .Opacity = 0, .CornerRadius = New CornerRadius(2), .Margin = New Thickness(-1, 0, 0, 0), .Background = Brush("ColorBrush3"), .IsHitTestVisible = False, .HorizontalAlignment = HorizontalAlignment.Left, .VerticalAlignment = VerticalAlignment.Center}
            SetRowSpan(_check, 4)
            Children.Add(_check)
            _hoverMotion = New PclListHoverMotion(_background, DirectCast(RenderTransform, ScaleTransform))
            _selectionMotion = New PclListSelectionMotion(Me, _check)
            MotionPolicy = New PclControlMotionPolicy(Me, Sub()
                                                             CancelPress()
                                                             _selectionMotion.Update(_selected, False)
                                                         End Sub)
            PclTheme.Watch(Me, Sub()
                                  _background.BorderBrush = Brush("ColorBrush6")
                                  _check.Background = Brush("ColorBrush3")
                                  CancelPress()
                                  _selectionMotion.Update(_selected, False)
                              End Sub, Sub()
                                           _background.SetResourceReference(Border.BorderBrushProperty, "ColorBrush6")
                                           _check.SetResourceReference(Border.BackgroundProperty, "ColorBrush3")
                                           RefreshHover(True, paletteOnly:=True)
                                           _selectionMotion.RefreshPalette()
                                       End Sub)
            AddHandler SizeChanged, Sub(sender, args)
                                        RefreshMetrics()
                                        If args.HeightChanged Then _selectionMotion.Update(_selected, AllowSelectionMotion())
                                    End Sub
            AddHandler MouseEnter, Sub() RefreshHover()
            RefreshMetrics()
        End Sub
        Private Sub CancelPress()
            _down = False
            ' Unloaded controls may retain WPF's pointer cache. Keep their plate
            ' inert even though unhosted controls still accept explicit calls.
            RefreshHover(True, Not IsLoaded OrElse Not IsVisible)
        End Sub
        Private Function AllowSelectionMotion() As Boolean
            Return MotionPolicy IsNot Nothing AndAlso MotionPolicy.CanAnimate AndAlso ActualHeight > 0 AndAlso Not PclTheme.Changing
        End Function
        Private Sub RefreshHover(Optional immediate As Boolean = False, Optional inactive As Boolean = False, Optional paletteOnly As Boolean = False)
            If _hoverMotion Is Nothing Then Return
            _background.CornerRadius = New CornerRadius(If(IsSidebar, 0, 6))
            Dim interactive = Not inactive AndAlso IsEnabled AndAlso IsHitTestVisible AndAlso (Not IsLoaded OrElse IsVisible)
            Dim pressed = interactive AndAlso _down
            Dim active = interactive AndAlso (IsMouseOver OrElse pressed OrElse IsKeyboardFocused)
            Dim fill = Brush(If(pressed, "ColorBrush6", If(active, "ColorBrushBg1", "ColorBrush7"))).Color
            If paletteOnly Then _hoverMotion.RefreshPalette(fill) : Return
            _hoverMotion.Update(active, pressed, IsSidebar, fill,
                Not immediate AndAlso MotionPolicy IsNot Nothing AndAlso MotionPolicy.CanAnimate AndAlso interactive AndAlso Not PclTheme.Changing)
        End Sub
        Protected Overrides Sub OnMouseLeftButtonDown(e As MouseButtonEventArgs)
            MyBase.OnMouseLeftButtonDown(e)
            If Not AcceptsInput Then Return
            _down = True
            RefreshHover()
            e.Handled = True
        End Sub
        Protected Overrides Sub OnMouseLeave(e As MouseEventArgs)
            MyBase.OnMouseLeave(e)
            _down = False
            RefreshHover()
        End Sub
        Protected Overrides Sub OnMouseLeftButtonUp(e As MouseButtonEventArgs)
            MyBase.OnMouseLeftButtonUp(e)
            Dim clicked = _down AndAlso AcceptsInput
            _down = False
            RefreshHover()
            If clicked Then RaiseEvent Click(Me, e)
            e.Handled = True
        End Sub
        Protected Overrides Sub OnLostMouseCapture(e As MouseEventArgs)
            MyBase.OnLostMouseCapture(e)
            If _down Then CancelPress()
        End Sub
        Friend Sub Activate()
            If AcceptsInput Then RaiseEvent Click(Me, New RoutedEventArgs())
        End Sub
        Protected Overrides Sub OnKeyDown(e As KeyEventArgs)
            MyBase.OnKeyDown(e)
            If Not AcceptsInput OrElse (e.Key <> Key.Enter AndAlso e.Key <> Key.Space) Then Return
            e.Handled = True
            If Not e.IsRepeat Then RaiseEvent Click(Me, e)
        End Sub
    End Class
End Namespace
