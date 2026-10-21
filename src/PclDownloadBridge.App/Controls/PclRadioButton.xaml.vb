Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media

Namespace Controls
    Partial Public Class PclRadioButton
        Public Event Click As RoutedEventHandler
        Private _checked As Boolean
        Private _down As Boolean
        Public Property Text As String
            Get
                Return LabText.Text
            End Get
            Set(value As String)
                LabText.Text = value
            End Set
        End Property
        Public Property Logo As String
            Get
                Return If(ShapeLogo.Data?.ToString(), "")
            End Get
            Set(value As String)
                ShapeLogo.Data = Geometry.Parse(value)
            End Set
        End Property
        Public Property Checked As Boolean
            Get
                Return _checked
            End Get
            Set(value As Boolean)
                _checked = value
                _down = False
                Refresh()
            End Set
        End Property
        Public Sub New()
            InitializeComponent()
            Cursor = Cursors.Hand
            Focusable = True
            AddHandler GotKeyboardFocus, Sub() Refresh()
            AddHandler LostKeyboardFocus, Sub() Refresh()
            PclTheme.Watch(Me, AddressOf Refresh)
            AddHandler Loaded, Sub() Refresh()
            AddHandler MouseEnter, Sub() Refresh()
            AddHandler IsEnabledChanged, Sub()
                                             _down = False
                                             Refresh()
                                         End Sub
        End Sub
        Private Sub Refresh()
            If ShapeLogo Is Nothing Then Return
            Dim backgroundColor = If(Checked, Colors.White, If(_down, Color.FromArgb(120, 255, 255, 255), If(IsMouseOver OrElse IsKeyboardFocused, Color.FromArgb(50, 255, 255, 255), Brush("ColorBrushSemiTransparent").Color)))
            If Not IsLoaded Then
                Background = New SolidColorBrush(backgroundColor)
                ShapeLogo.Fill = If(Checked, Brush("ColorBrush3"), Brushes.White)
                LabText.Foreground = If(Checked, Brush("ColorBrush3"), Brushes.White)
                Return
            End If
            Dim duration = If(Checked, 120, If(_down, 60, If(IsMouseOver, 90, 150)))
            ColorTo(Me, BackgroundProperty, backgroundColor, duration)
            Dim ink = If(Checked, Brush("ColorBrush3").Color, Colors.White)
            ColorTo(ShapeLogo, Shapes.Path.FillProperty, ink, duration)
            ColorTo(LabText, TextBlock.ForegroundProperty, ink, duration)
        End Sub
        Protected Overrides Sub OnMouseLeftButtonDown(e As MouseButtonEventArgs)
            MyBase.OnMouseLeftButtonDown(e)
            If Not IsEnabled Then Return
            e.Handled = True
            If Checked Then Return
            _down = True
            Refresh()
        End Sub
        Protected Overrides Sub OnKeyDown(e As KeyEventArgs)
            MyBase.OnKeyDown(e)
            If Not IsEnabled OrElse (e.Key <> Key.Enter AndAlso e.Key <> Key.Space) Then Return
            e.Handled = True
            If Not Checked AndAlso Not e.IsRepeat Then RaiseEvent Click(Me, e)
        End Sub
        Protected Overrides Sub OnMouseLeave(e As MouseEventArgs)
            MyBase.OnMouseLeave(e)
            _down = False
            Refresh()
        End Sub
        Protected Overrides Sub OnMouseLeftButtonUp(e As MouseButtonEventArgs)
            MyBase.OnMouseLeftButtonUp(e)
            Dim clicked = _down AndAlso IsEnabled AndAlso Not Checked
            _down = False
            Refresh()
            If clicked Then RaiseEvent Click(Me, e)
            e.Handled = True
        End Sub
    End Class
End Namespace
