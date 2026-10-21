Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media

Namespace Controls
    ' XAML is copied from PCL MyButton at 5ef81e4. Event/animation adapter replaces
    ' MeloongCore while retaining its exact border/text tree and 100/200ms colors.
    Partial Public Class PclButton
        Public Event Click As RoutedEventHandler
        Public Property Text As String
            Get
                Return LabText.Text
            End Get
            Set(value As String)
                LabText.Text = value
            End Set
        End Property
        Private _highlight As Boolean
        Public Property Highlight As Boolean
            Get
                Return _highlight
            End Get
            Set(value As Boolean)
                _highlight = value
                If PanFore IsNot Nothing Then Refresh(Nothing, Nothing)
            End Set
        End Property
        Private _down As Boolean
        Public Sub New()
            InitializeComponent()
            PclTheme.Watch(Me, Sub() Refresh(Nothing, Nothing))
            Cursor = Cursors.Hand
            Focusable = True
            AddHandler Loaded, AddressOf Refresh
            AddHandler MouseEnter, AddressOf Refresh
            AddHandler MouseLeave, AddressOf Refresh
            AddHandler GotKeyboardFocus, AddressOf Refresh
            AddHandler LostKeyboardFocus, AddressOf Refresh
            AddHandler IsEnabledChanged, Sub()
                                             _down = False
                                             RestoreScale(300)
                                             Refresh(Nothing, Nothing)
                                         End Sub
        End Sub
        Private Sub Refresh(sender As Object, e As RoutedEventArgs)
            Dim active = IsEnabled AndAlso (IsMouseOver OrElse IsKeyboardFocused)
            Dim ink = If(Not IsEnabled, "ColorBrushGray4", If(active, "ColorBrush3", If(Highlight, "ColorBrush2", "ColorBrush1")))
            Dim fill = If(active, "ColorBrush7", "ColorBrushHalfWhite")
            If Not IsLoaded OrElse PclTheme.Changing Then
                PanFore.SetResourceReference(Border.BorderBrushProperty, ink)
                PanFore.SetResourceReference(BackgroundProperty, fill)
            Else
                Dim duration = If(active, 100, 200)
                ColorTo(PanFore, Border.BorderBrushProperty, Brush(ink).Color, duration)
                ColorTo(PanFore, BackgroundProperty, Brush(fill).Color, duration)
            End If
        End Sub
        Protected Overrides Sub OnMouseLeftButtonDown(e As MouseButtonEventArgs)
            MyBase.OnMouseLeftButtonDown(e)
            If Not IsEnabled Then Return
            _down = True
            Focus()
            NumberTo(DirectCast(PanFore.RenderTransform, ScaleTransform), ScaleTransform.ScaleXProperty, 0.955, 80)
            NumberTo(DirectCast(PanFore.RenderTransform, ScaleTransform), ScaleTransform.ScaleYProperty, 0.955, 80)
            e.Handled = True
        End Sub
        Protected Overrides Sub OnMouseLeftButtonUp(e As MouseButtonEventArgs)
            MyBase.OnMouseLeftButtonUp(e)
            Dim click = _down AndAlso IsEnabled
            _down = False
            RestoreScale(300)
            If click Then RaiseEvent Click(Me, e)
            e.Handled = True
        End Sub
        Protected Overrides Sub OnMouseLeave(e As MouseEventArgs)
            MyBase.OnMouseLeave(e)
            If Not _down Then Return
            _down = False
            RestoreScale(800)
        End Sub
        Private Sub RestoreScale(milliseconds As Integer)
            NumberTo(DirectCast(PanFore.RenderTransform, ScaleTransform), ScaleTransform.ScaleXProperty, 1, milliseconds)
            NumberTo(DirectCast(PanFore.RenderTransform, ScaleTransform), ScaleTransform.ScaleYProperty, 1, milliseconds)
        End Sub
        Protected Overrides Sub OnKeyDown(e As KeyEventArgs)
            MyBase.OnKeyDown(e)
            If IsEnabled AndAlso Not e.IsRepeat AndAlso (e.Key = Key.Enter OrElse e.Key = Key.Space) Then
                RaiseEvent Click(Me, New RoutedEventArgs())
                e.Handled = True
            End If
        End Sub
    End Class
End Namespace
