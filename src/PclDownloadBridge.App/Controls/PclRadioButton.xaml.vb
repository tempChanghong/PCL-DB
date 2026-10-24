Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Media.Animation

Namespace Controls
    Partial Public Class PclRadioButton
        Public Event Click As RoutedEventHandler
        Private _checked As Boolean
        Private _down As Boolean
        Public Enum ColorState
            White
            Highlight
        End Enum
        Private _colorType As ColorState
        Private ReadOnly _fill As New SolidColorBrush, _logoInk As New SolidColorBrush, _textInk As New SolidColorBrush
        Private ReadOnly _fillMotion As PclMotionSlot, _logoMotion As PclMotionSlot, _textMotion As PclMotionSlot
        Friend ReadOnly Property MotionPolicy As PclControlMotionPolicy
        Friend ReadOnly Property ColorTransition As AnimationClock
            Get
                Return _fillMotion.Clock
            End Get
        End Property
        Friend ReadOnly Property InkTransition As AnimationClock
            Get
                Return _textMotion.Clock
            End Get
        End Property
        Friend ReadOnly Property LogoTransition As AnimationClock
            Get
                Return _logoMotion.Clock
            End Get
        End Property
        Public Property ColorType As ColorState
            Get
                Return _colorType
            End Get
            Set(value As ColorState)
                If _colorType = value Then Return
                _colorType = value : _down = False : Refresh()
            End Set
        End Property
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
                If _checked = value Then Return
                _checked = value
                _down = False
                Refresh()
            End Set
        End Property
        Public Sub New()
            InitializeComponent()
            Background = _fill : ShapeLogo.Fill = _logoInk : LabText.Foreground = _textInk
            _fillMotion = New PclMotionSlot(_fill, SolidColorBrush.ColorProperty)
            _logoMotion = New PclMotionSlot(_logoInk, SolidColorBrush.ColorProperty)
            _textMotion = New PclMotionSlot(_textInk, SolidColorBrush.ColorProperty)
            MotionPolicy = New PclControlMotionPolicy(Me, AddressOf CancelPress)
            Cursor = Cursors.Hand
            Focusable = True
            AddHandler GotKeyboardFocus, Sub() Refresh()
            AddHandler LostKeyboardFocus, Sub() CancelPress()
            PclTheme.Watch(Me, AddressOf CancelPress)
            AddHandler Loaded, Sub() Refresh()
            AddHandler MouseEnter, Sub() Refresh()
            AddHandler Unloaded, Sub()
                                     _down = False : Refresh(True, True)
                                 End Sub
            AddHandler IsVisibleChanged, Sub()
                                             If Not IsVisible Then _down = False
                                             Refresh(True, Not IsVisible)
                                         End Sub
            AddHandler IsHitTestVisibleChanged, Sub()
                                                    If Not IsHitTestVisible Then _down = False
                                                    Refresh(True)
                                                End Sub
            AddHandler IsEnabledChanged, Sub()
                                             _down = False
                                             Refresh(True)
                                         End Sub
            Refresh(True)
        End Sub
        Private Sub CancelPress()
            _down = False : Refresh(True)
        End Sub
        Private Sub Refresh(Optional immediate As Boolean = False, Optional inactive As Boolean = False)
            If _fillMotion Is Nothing Then Return
            Dim interactive = Not inactive AndAlso IsEnabled AndAlso IsHitTestVisible AndAlso (Not IsLoaded OrElse IsVisible)
            Dim pressed = interactive AndAlso _down, hovered = interactive AndAlso (IsMouseOver OrElse IsKeyboardFocused)
            Dim accent = Brush("ColorBrush3").Color, backgroundColor As Color, ink As Color
            If ColorType = ColorState.Highlight Then
                backgroundColor = If(Checked, accent, If(pressed, Brush("ColorBrush6").Color, If(hovered, Brush("ColorBrush7").Color, Brush("ColorBrushSemiTransparent").Color)))
                ink = If(Checked, Colors.White, accent)
            Else
                backgroundColor = If(Checked, Colors.White, If(pressed, Color.FromArgb(120, 255, 255, 255), If(hovered, Color.FromArgb(50, 255, 255, 255), Brush("ColorBrushSemiTransparent").Color)))
                ink = If(Checked, accent, Colors.White)
            End If
            Dim animate = Not immediate AndAlso interactive AndAlso Not PclTheme.Changing AndAlso MotionPolicy.CanAnimate
            Dim duration = If(Checked, 120, If(pressed, If(ColorType = ColorState.Highlight, 90, 60), If(hovered, 90, 150)))
            ChangeColor(_fillMotion, _fill, backgroundColor, duration, animate)
            ' PCL's press branch changes only the background; an existing ink fade
            ' retains its own duration instead of being restarted by the press.
            If Not pressed OrElse Checked OrElse Not animate Then
                ChangeColor(_logoMotion, _logoInk, ink, duration, animate)
                ChangeColor(_textMotion, _textInk, ink, duration, animate)
            End If
        End Sub
        Private Sub ChangeColor(slot As PclMotionSlot, target As SolidColorBrush, color As Color, duration As Integer, animate As Boolean)
            If Not animate Then
                slot.SetValue(color) : Return
            End If
            Dim current = TryCast(slot.Clock?.Timeline, PclRadioColorAnimation)
            If (current IsNot Nothing AndAlso current.Destination = color) OrElse (slot.Clock Is Nothing AndAlso target.Color = color) Then Return
            slot.Animate(New PclRadioColorAnimation(target.Color, color, duration), color, MotionPolicy.PauseForChecks)
        End Sub
        Protected Overrides Sub OnMouseLeftButtonDown(e As MouseButtonEventArgs)
            MyBase.OnMouseLeftButtonDown(e)
            If Not IsEnabled OrElse Not IsHitTestVisible Then Return
            If ColorType = ColorState.Highlight Then Focus()
            e.Handled = True
            If Checked Then Return
            _down = True
            Refresh()
        End Sub
        Protected Overrides Sub OnKeyDown(e As KeyEventArgs)
            MyBase.OnKeyDown(e)
            If Not IsEnabled OrElse Not IsHitTestVisible OrElse Not IsVisible OrElse (e.Key <> Key.Enter AndAlso e.Key <> Key.Space) Then Return
            e.Handled = True
            If Not Checked AndAlso Not e.IsRepeat Then RaiseEvent Click(Me, e)
        End Sub
        Protected Overrides Sub OnMouseLeave(e As MouseEventArgs)
            MyBase.OnMouseLeave(e)
            _down = False
            Refresh()
        End Sub
        Protected Overrides Sub OnLostMouseCapture(e As MouseEventArgs)
            MyBase.OnLostMouseCapture(e)
            CancelPress()
        End Sub
        Protected Overrides Sub OnMouseLeftButtonUp(e As MouseButtonEventArgs)
            MyBase.OnMouseLeftButtonUp(e)
            Dim clicked = _down AndAlso IsEnabled AndAlso IsHitTestVisible AndAlso Not Checked
            _down = False
            Refresh()
            If clicked Then RaiseEvent Click(Me, e)
            e.Handled = True
        End Sub
    End Class
End Namespace
