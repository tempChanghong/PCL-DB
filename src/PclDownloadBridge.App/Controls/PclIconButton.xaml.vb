Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Media.Animation

Namespace Controls
    ' MyIconButton at 5ef81e4: original XAML and black light-mode ink.
    ' Dark-mode neutral ink and press cancellation are local adaptations.
    Partial Public Class PclIconButton
        Inherits Border
        Public Event Click As RoutedEventHandler
        Private _down As Boolean
        Private ReadOnly _pressMotion As PclPressMotion
        Friend ReadOnly Property ScaleTransition As AnimationClock
            Get
                Return _pressMotion.Transition
            End Get
        End Property
        Private _logoScale As Double = 1
        Public Property Logo As String
            Get
                Return Path.Data?.ToString()
            End Get
            Set(value As String)
                Path.Data = Geometry.Parse(value)
            End Set
        End Property
        Public Property LogoScale As Double
            Get
                Return _logoScale
            End Get
            Set(value As Double)
                _logoScale = value
                Path.RenderTransform = New ScaleTransform(value, value)
            End Set
        End Property
        Public Sub New()
            InitializeComponent()
            _pressMotion = New PclPressMotion(Me, DirectCast(PanBack.RenderTransform, ScaleTransform), Sub() _down = False)
            Focusable = True : Cursor = Cursors.Hand
            AddHandler Loaded, Sub() RefreshColor(False)
            AddHandler IsEnabledChanged, Sub()
                                             _down = False
                                             RefreshColor(IsLoaded)
                                         End Sub
            AddHandler MouseEnter, Sub() RefreshColor(True)
            AddHandler GotKeyboardFocus, Sub() RefreshColor(True)
            AddHandler LostKeyboardFocus, Sub() RefreshColor(True)
            AddHandler MouseLeave, Sub()
                                       _down = False
                                       _pressMotion.Start(PclPressKind.IconLeave)
                                       RefreshColor(True)
                                   End Sub
            AddHandler MouseLeftButtonDown, Sub(sender, args)
                                               If Not IsEnabled Then Return
                                               _down = True : Focus()
                                               _pressMotion.Start(PclPressKind.IconDown)
                                               args.Handled = True
                                           End Sub
            AddHandler MouseLeftButtonUp, Sub(sender, args)
                                             If Not _down OrElse Not IsEnabled Then Return
                                             _down = False
                                             _pressMotion.Start(PclPressKind.IconUp)
                                             RefreshColor(True)
                                             args.Handled = True
                                             RaiseEvent Click(Me, args)
                                         End Sub
            AddHandler KeyDown, Sub(sender, args)
                                    If Not IsEnabled OrElse (args.Key <> Key.Enter AndAlso args.Key <> Key.Space) OrElse args.IsRepeat Then Return
                                    args.Handled = True
                                    RaiseEvent Click(Me, args)
                                End Sub
            RefreshColor(False)
            PclTheme.Watch(Me, Sub() RefreshColor(IsLoaded))
        End Sub
        Private Sub RefreshColor(animate As Boolean)
            Dim ink = Brush("PclIconButtonInk").Color
            Dim targetColor = If(Not IsEnabled, Brush("ColorBrushGray5").Color, System.Windows.Media.Color.FromArgb(CByte(If(IsMouseOver OrElse IsKeyboardFocused, 230, 160)), ink.R, ink.G, ink.B))
            If animate Then
                ColorTo(Path, Shapes.Shape.FillProperty, targetColor, If(IsMouseOver OrElse Not IsEnabled, 120, 150))
            Else
                Path.Fill = New SolidColorBrush(targetColor)
            End If
            PanBack.Background = New SolidColorBrush(Colors.Transparent)
        End Sub
    End Class
End Namespace
