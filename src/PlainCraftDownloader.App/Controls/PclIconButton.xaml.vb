Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Media.Animation

Namespace Controls
    ' MyIconButton at 5ef81e4: original XAML, black theme used by the install page.
    Partial Public Class PclIconButton
        Inherits Border
        Public Event Click As RoutedEventHandler
        Private _down As Boolean
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
            Focusable = True : Cursor = Cursors.Hand
            AddHandler Loaded, Sub() RefreshColor(False)
            AddHandler IsEnabledChanged, Sub() RefreshColor(IsLoaded)
            AddHandler MouseEnter, Sub() RefreshColor(True)
            AddHandler GotKeyboardFocus, Sub() RefreshColor(True)
            AddHandler LostKeyboardFocus, Sub() RefreshColor(True)
            AddHandler MouseLeave, Sub()
                                       _down = False
                                       ScaleTo(1, 250)
                                       RefreshColor(True)
                                   End Sub
            AddHandler MouseLeftButtonDown, Sub(sender, args)
                                               If Not IsEnabled Then Return
                                               _down = True : Focus()
                                               ScaleTo(0.8, 400)
                                               args.Handled = True
                                           End Sub
            AddHandler MouseLeftButtonUp, Sub(sender, args)
                                             If Not _down OrElse Not IsEnabled Then Return
                                             _down = False
                                             ReleaseScale()
                                             args.Handled = True
                                             RaiseEvent Click(Me, args)
                                         End Sub
            AddHandler KeyDown, Sub(sender, args)
                                    If Not IsEnabled OrElse (args.Key <> Key.Enter AndAlso args.Key <> Key.Space) OrElse args.IsRepeat Then Return
                                    args.Handled = True
                                    RaiseEvent Click(Me, args)
                                End Sub
            RefreshColor(False)
        End Sub
        Private Sub RefreshColor(animate As Boolean)
            Dim targetColor = If(Not IsEnabled, Brush("ColorBrushGray5").Color, System.Windows.Media.Color.FromArgb(CByte(If(IsMouseOver OrElse IsKeyboardFocused, 230, 160)), 0, 0, 0))
            If animate Then
                ColorTo(Path, Shapes.Shape.FillProperty, targetColor, If(IsMouseOver OrElse Not IsEnabled, 120, 150))
            Else
                Path.Fill = New SolidColorBrush(targetColor)
            End If
            PanBack.Background = New SolidColorBrush(Colors.Transparent)
        End Sub
        Private Sub ScaleTo(value As Double, milliseconds As Integer)
            Dim scale = DirectCast(PanBack.RenderTransform, ScaleTransform)
            NumberTo(scale, ScaleTransform.ScaleXProperty, value, milliseconds)
            NumberTo(scale, ScaleTransform.ScaleYProperty, value, milliseconds)
        End Sub
        Private Sub ReleaseScale()
            Dim animation As New DoubleAnimationUsingKeyFrames()
            animation.KeyFrames.Add(New EasingDoubleKeyFrame(1.05, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(250))) With {.EasingFunction = New BackEase With {.Amplitude = 0.3, .EasingMode = EasingMode.EaseOut}})
            animation.KeyFrames.Add(New EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(500))) With {.EasingFunction = New PowerEase With {.Power = 3, .EasingMode = EasingMode.EaseOut}})
            Dim scale = DirectCast(PanBack.RenderTransform, ScaleTransform)
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation)
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation)
            RefreshColor(True)
        End Sub
    End Class
End Namespace
