Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Markup
Imports System.Windows.Media
Imports System.Windows.Media.Animation

Namespace Controls
    <ContentProperty("Body")>
    Public Class PclCard
        Inherits Grid
        Public Event FoldChanged As EventHandler
        Private ReadOnly _border As Border
        Private ReadOnly _shadow As PclDropShadow
        Private ReadOnly _title As TextBlock
        Private ReadOnly _arrow As Shapes.Path
        Private ReadOnly _presenter As ContentPresenter
        Private ReadOnly _header As Grid
        Private ReadOnly _headerInfo As ContentPresenter
        Public Property BodyMargin As Thickness
            Get
                Return _presenter.Margin
            End Get
            Set(value As Thickness)
                _presenter.Margin = value
            End Set
        End Property
        Public Property HeaderContent As UIElement
            Get
                Return TryCast(_headerInfo.Content, UIElement)
            End Get
            Set(value As UIElement)
                _headerInfo.Content = value
            End Set
        End Property
        Public Property HeaderContentMargin As Thickness
            Get
                Return _headerInfo.Margin
            End Get
            Set(value As Thickness)
                _headerInfo.Margin = value
            End Set
        End Property
        Public Property ShowHeader As Boolean
            Get
                Return _header.Visibility = Visibility.Visible
            End Get
            Set(value As Boolean)
                _header.Visibility = If(value, Visibility.Visible, Visibility.Collapsed)
            End Set
        End Property
        Private _collapsed As Boolean
        Private _swapLogoRight As Boolean
        Public Property SwapLogoRight As Boolean
            Get
                Return _swapLogoRight
            End Get
            Set(value As Boolean)
                _swapLogoRight = value
                DirectCast(_arrow.RenderTransform, RotateTransform).Angle = If(_collapsed, If(value, 270, 0), 180)
            End Set
        End Property
        Private _collapsible As Boolean
        Private _built As Boolean
        Private _headerPressed As Boolean
        Public Property Builder As Func(Of UIElement)
        Public Sub RefreshBody(builder As Func(Of UIElement))
            Me.Builder = builder
            _built = Not Collapsed
            Body = If(Collapsed, Nothing, builder.Invoke())
        End Sub
        Public Property Title As String
            Get
                Return _title.Text
            End Get
            Set(value As String)
                _title.Text = value
            End Set
        End Property
        Public Property Body As UIElement
            Get
                Return TryCast(_presenter.Content, UIElement)
            End Get
            Set(value As UIElement)
                _presenter.Content = value
            End Set
        End Property
        Public Property Collapsible As Boolean
            Get
                Return _collapsible
            End Get
            Set(value As Boolean)
                If _collapsible <> value Then _headerPressed = False
                _collapsible = value
                _arrow.Visibility = If(value, Visibility.Visible, Visibility.Collapsed)
                _header.Cursor = If(value, Input.Cursors.Hand, Input.Cursors.Arrow)
                _header.Focusable = value
            End Set
        End Property
        Public Property Collapsed As Boolean
            Get
                Return _collapsed
            End Get
            Set(value As Boolean)
                If _collapsed = value Then
                    If Not value AndAlso Not _built AndAlso Builder IsNot Nothing Then
                        Body = Builder.Invoke()
                        _built = True
                    End If
                    Return
                End If
                Dim oldHeight = ActualHeight
                Dim changed = _collapsed <> value
                StopHeightTransition()
                _collapsed = value
                Input.KeyboardNavigation.SetTabNavigation(_presenter, If(value, Input.KeyboardNavigationMode.None, Input.KeyboardNavigationMode.Continue))
                If Not value AndAlso Not _built AndAlso Builder IsNot Nothing Then
                    Body = Builder.Invoke()
                    _built = True
                End If
                If IsLoaded AndAlso changed Then
                    _presenter.Visibility = Visibility.Visible
                    _presenter.Measure(New Size(Math.Max(0, ActualWidth), Double.PositiveInfinity))
                    Dim target = If(value, 40, Math.Max(40, _presenter.DesiredSize.Height))
                    ClipToBounds = True
                    Dim clock = DirectCast(PclCardMotion.HeightTransition(oldHeight, target).CreateClock(True), AnimationClock)
                    _heightTransition = clock
                    AddHandler clock.Completed, Sub()
                                                        If _heightTransition IsNot clock Then Return
                                                        _heightTransition = Nothing
                                                        BeginAnimation(HeightProperty, Nothing)
                                                        Height = If(_collapsed, 40, Double.NaN)
                                                        _presenter.Visibility = If(_collapsed, Visibility.Collapsed, Visibility.Visible)
                                                        ClipToBounds = False
                                                    End Sub
                    Height = oldHeight
                    ApplyAnimationClock(HeightProperty, clock)
                Else
                    _presenter.Visibility = If(value, Visibility.Collapsed, Visibility.Visible)
                    Height = If(value, 40, Double.NaN)
                    ClipToBounds = False
                End If
                Dim rotation = DirectCast(_arrow.RenderTransform, RotateTransform)
                If IsLoaded AndAlso changed Then
                    rotation.BeginAnimation(RotateTransform.AngleProperty, PclCardMotion.ArrowTransition(rotation.Angle, If(value, If(SwapLogoRight, 270, 0), 180)))
                Else
                    rotation.Angle = If(value, If(SwapLogoRight, 270, 0), 180)
                End If
                RaiseEvent FoldChanged(Me, EventArgs.Empty)
            End Set
        End Property
        Public Sub New()
            Margin = New Thickness(0, 0, 0, 15)
            _shadow = New PclDropShadow With {.Margin = New Thickness(-3, -3, -3, -4), .ShadowRadius = 3, .CornerRadius = New CornerRadius(5), .Opacity = 0.07, .IsHitTestVisible = False}
            _shadow.SetResourceReference(PclDropShadow.ColorProperty, "ColorObject1")
            Children.Add(_shadow)
            _border = New Border With {.Background = New SolidColorBrush(Color.FromArgb(245, 255, 255, 255)), .CornerRadius = New CornerRadius(5)}
            Children.Add(_border)
            _presenter = New ContentPresenter With {.Margin = New Thickness(20, 40, 18, 18)}
            Children.Add(_presenter)
            Dim header As New Grid With {.Height = 40, .VerticalAlignment = VerticalAlignment.Top, .Background = Brushes.Transparent}
            _header = header
            AddHandler header.GotKeyboardFocus, Sub() Hover(True)
            AddHandler header.LostKeyboardFocus, Sub() Hover(IsMouseOver)
            AddHandler header.KeyDown, Sub(sender, args)
                                           If args.OriginalSource IsNot header OrElse Not Collapsible OrElse Not IsEnabled OrElse
                                               (args.Key <> Input.Key.Enter AndAlso args.Key <> Input.Key.Space) Then Return
                                           args.Handled = True
                                           If Not args.IsRepeat Then Collapsed = Not Collapsed
                                       End Sub
            _title = New TextBlock With {.Margin = New Thickness(15, 12, 0, 0), .FontSize = 13, .FontWeight = FontWeights.Bold, .Foreground = Brush("ColorBrush1"), .IsHitTestVisible = False}
            header.Children.Add(_title)
            _title.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1")
            _arrow = New Shapes.Path With {.Data = Geometry.Parse("M2,4 l-2,2 10,10 10,-10 -2,-2 -8,8 -8,-8 z"), .Stretch = Stretch.Uniform, .Height = 6, .Width = 10, .Margin = New Thickness(0, 17, 16, 0), .VerticalAlignment = VerticalAlignment.Top, .HorizontalAlignment = HorizontalAlignment.Right, .Fill = Brush("ColorBrush1"), .RenderTransform = New RotateTransform(), .RenderTransformOrigin = New Point(0.5, 0.5), .Visibility = Visibility.Collapsed}
            header.Children.Add(_arrow)
            _arrow.SetResourceReference(Shapes.Path.FillProperty, "ColorBrush1")
            _headerInfo = New ContentPresenter With {.Margin = New Thickness(132, 0, 35, 0)}
            header.Children.Add(_headerInfo)
            AddHandler header.MouseLeftButtonDown, Sub(sender, args)
                                                       _headerPressed = CanToggleHeader(args)
                                                   End Sub
            AddHandler header.MouseLeftButtonUp, Sub(sender, args)
                                                     Dim toggle = _headerPressed AndAlso CanToggleHeader(args)
                                                     _headerPressed = False
                                                     If Not toggle Then Return
                                                     Collapsed = Not Collapsed
                                                     args.Handled = True
                                                 End Sub
            AddHandler header.MouseLeave, Sub() _headerPressed = False
            AddHandler header.MouseMove, Sub(sender, args)
                                            header.Cursor = If(Collapsible AndAlso (Collapsed OrElse args.GetPosition(header).Y <= 34), Input.Cursors.Hand, Input.Cursors.Arrow)
                                        End Sub
            AddHandler IsEnabledChanged, Sub() _headerPressed = False
            Children.Add(header)
            PclTheme.Watch(Me, Sub()
                                  Dim active = IsMouseOver OrElse _header.IsKeyboardFocused
                                  _title.SetResourceReference(TextBlock.ForegroundProperty, If(active, "ColorBrush2", "ColorBrush1"))
                                  _arrow.SetResourceReference(Shapes.Path.FillProperty, If(active, "ColorBrush2", "ColorBrush1"))
                                  _shadow.BeginAnimation(PclDropShadow.ColorProperty, Nothing)
                                  _shadow.SetResourceReference(PclDropShadow.ColorProperty, If(active, "ColorObject4", "ColorObject1"))
                              End Sub)
            AddHandler MouseEnter, Sub() Hover(True)
            AddHandler MouseLeave, Sub() Hover(False)
            AddHandler Loaded, Sub() UpdateShadowOffset(VisualTreeHelper.GetDpi(Me))
            AddHandler Unloaded, Sub()
                                     If _heightTransition Is Nothing Then Return
                                     StopHeightTransition()
                                     Height = If(_collapsed, 40, Double.NaN)
                                     _presenter.Visibility = If(_collapsed, Visibility.Collapsed, Visibility.Visible)
                                     ClipToBounds = False
                                 End Sub
        End Sub
        Private _heightTransition As AnimationClock
        Friend ReadOnly Property HeightTransition As AnimationClock
            Get
                Return _heightTransition
            End Get
        End Property
        Private Sub StopHeightTransition()
            Dim previous = _heightTransition
            _heightTransition = Nothing
            If previous Is Nothing Then Return
            previous.Controller.Remove()
            ApplyAnimationClock(HeightProperty, Nothing)
        End Sub
        Private Function CanToggleHeader(args As Input.MouseButtonEventArgs) As Boolean
            Return Collapsible AndAlso IsEnabled AndAlso
                (Not IsLoaded OrElse args.GetPosition(_header).Y <= If(Collapsed, 40, 34))
        End Function
        Protected Overrides Sub OnDpiChanged(oldDpi As DpiScale, newDpi As DpiScale)
            MyBase.OnDpiChanged(oldDpi, newDpi)
            UpdateShadowOffset(newDpi)
        End Sub
        Private Sub UpdateShadowOffset(dpi As DpiScale)
            If _shadow IsNot Nothing Then _shadow.Margin = New Thickness(-3, -3, -3, -3 - 1 / dpi.DpiScaleY)
        End Sub
        Private Sub Hover(inside As Boolean)
            inside = inside OrElse _header.IsKeyboardFocused
            ColorTo(_title, TextBlock.ForegroundProperty, Brush(If(inside, "ColorBrush2", "ColorBrush1")).Color, 90)
            ColorTo(_arrow, Shapes.Path.FillProperty, Brush(If(inside, "ColorBrush2", "ColorBrush1")).Color, 90)
            _shadow.BeginAnimation(PclDropShadow.ColorProperty, New ColorAnimation(Brush(If(inside, "ColorBrush4", "ColorBrush1")).Color, TimeSpan.FromMilliseconds(90)))
            _shadow.BeginAnimation(OpacityProperty, New DoubleAnimation(If(inside, 0.4, 0.07), TimeSpan.FromMilliseconds(90)))
        End Sub
        Friend Function FocusHeader() As Boolean
            If Not Collapsible OrElse Not IsEnabled Then Return False
            _header.BringIntoView()
            Return _header.Focus()
        End Function
        Protected Overrides Sub OnKeyDown(e As Input.KeyEventArgs)
            MyBase.OnKeyDown(e)
            If e.Handled OrElse e.Key <> Input.Key.Escape OrElse e.IsRepeat OrElse Not IsEnabled OrElse Not Collapsible OrElse Collapsed Then Return
            e.Handled = True
            Collapsed = True
            FocusHeader()
        End Sub
    End Class
End Namespace
