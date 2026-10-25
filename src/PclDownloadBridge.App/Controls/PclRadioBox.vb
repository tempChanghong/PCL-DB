Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Controls.Primitives
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports System.Windows.Automation
Imports System.Windows.Automation.Peers
Imports System.Windows.Automation.Provider

Namespace Controls
    ' Native WPF RadioButton keeps grouping, keyboard and automation semantics.
    ' Only the visual state follows PCL 5ef81e4 MyRadioBox.
    Public Class PclRadioBox
        Inherits RadioButton
        Friend ReadOnly Property MotionPolicy As PclControlMotionPolicy
        Friend Property Glyph As PclRadioGlyph
            Get
                Return _glyph
            End Get
            Private Set(value As PclRadioGlyph)
                _glyph = value
            End Set
        End Property
        Private _glyph As PclRadioGlyph
        Private ReadOnly _ink As New SolidColorBrush()
        Private ReadOnly _fill As New SolidColorBrush()
        Private ReadOnly _text As New SolidColorBrush()
        Private ReadOnly _inkMotion As PclMotionSlot
        Private ReadOnly _fillMotion As PclMotionSlot
        Private ReadOnly _textMotion As PclMotionSlot
        Private _ringMotion As PclMotionSlot, _dotMotion As PclMotionSlot, _alphaMotion As PclMotionSlot
        Private _gestureBlocked As Boolean
        Private _spaceDown As Boolean
        Friend ReadOnly Property RingTransition As AnimationClock
            Get
                Return _ringMotion?.Clock
            End Get
        End Property
        Friend ReadOnly Property DotTransition As AnimationClock
            Get
                Return _dotMotion?.Clock
            End Get
        End Property
        Friend ReadOnly Property AlphaTransition As AnimationClock
            Get
                Return _alphaMotion?.Clock
            End Get
        End Property
        Friend ReadOnly Property InkTransition As AnimationClock
            Get
                Return _inkMotion.Clock
            End Get
        End Property

        Public Sub New()
            _inkMotion = New PclMotionSlot(_ink, SolidColorBrush.ColorProperty)
            _fillMotion = New PclMotionSlot(_fill, SolidColorBrush.ColorProperty)
            _textMotion = New PclMotionSlot(_text, SolidColorBrush.ColorProperty)
            Foreground = _text
            MotionPolicy = New PclControlMotionPolicy(Me, Sub()
                                                             If Not MotionPolicy.Interactive Then
                                                                 _gestureBlocked = True : _spaceDown = False
                                                                 If IsMouseCaptured Then ReleaseMouseCapture()
                                                             End If
                                                             SettleGeometry()
                                                             RefreshColors(False)
                                                         End Sub)
            PclTheme.Watch(Me, Sub() RefreshColors(False), Sub() RefreshColors(False))
            AddHandler MouseEnter, Sub() RefreshColors(True)
            AddHandler MouseLeave, Sub() RefreshColors(True)
            AddHandler IsKeyboardFocusWithinChanged, Sub() RefreshColors(True)
        End Sub

        Public Overrides Sub OnApplyTemplate()
            SettleGeometry()
            _ringMotion = Nothing : _dotMotion = Nothing : _alphaMotion = Nothing
            MyBase.OnApplyTemplate()
            Glyph = TryCast(GetTemplateChild("Glyph"), PclRadioGlyph)
            If Glyph Is Nothing Then Return
            Glyph.SetValue(PclRadioGlyph.StrokeProperty, _ink)
            Glyph.SetValue(PclRadioGlyph.RingFillProperty, _fill)
            _ringMotion = New PclMotionSlot(Glyph, PclRadioGlyph.RingDiameterProperty)
            _dotMotion = New PclMotionSlot(Glyph, PclRadioGlyph.DotDiameterProperty)
            _alphaMotion = New PclMotionSlot(Glyph, PclRadioGlyph.DotOpacityProperty)
            SettleGeometry() : RefreshColors(False)
        End Sub

        Protected Overrides Sub OnChecked(args As RoutedEventArgs)
            ' Update before external Checked handlers synchronously apply a new palette.
            RefreshSelection()
            MyBase.OnChecked(args)
        End Sub
        Protected Overrides Sub OnUnchecked(args As RoutedEventArgs)
            RefreshSelection()
            MyBase.OnUnchecked(args)
        End Sub

        Private Sub RefreshSelection()
            If Glyph IsNot Nothing Then
                If MotionPolicy.CanAnimate Then
                    Dim selected = IsChecked.GetValueOrDefault()
                    _ringMotion.Animate(New PclRadioAnimation(Glyph.RingDiameter, If(selected, PclRadioMotionKind.CheckRing, PclRadioMotionKind.UncheckRing)), PclRadioGlyph.RingSize, MotionPolicy.PauseForChecks)
                    _dotMotion.Animate(New PclRadioAnimation(Glyph.DotDiameter, If(selected, PclRadioMotionKind.CheckDot, PclRadioMotionKind.UncheckDot)), If(selected, PclRadioGlyph.DotSize, 0.0), MotionPolicy.PauseForChecks)
                    If selected AndAlso Glyph.DotOpacity < 0.01 Then _alphaMotion.SetValue(1.0)
                    Dim alpha = If(selected, 1.0, 0.0)
                    _alphaMotion.Animate(New DoubleAnimation(alpha, TimeSpan.FromMilliseconds(75)) With {.BeginTime = TimeSpan.FromMilliseconds(If(selected, 90, 30))}, alpha, MotionPolicy.PauseForChecks)
                Else
                    SettleGeometry()
                End If
            End If
            RefreshColors(True, 150)
        End Sub

        Private Sub SettleGeometry()
            _ringMotion?.SetValue(PclRadioGlyph.RingSize)
            _dotMotion?.SetValue(If(IsChecked.GetValueOrDefault(), PclRadioGlyph.DotSize, 0.0))
            _alphaMotion?.SetValue(If(IsChecked.GetValueOrDefault(), 1.0, 0.0))
        End Sub

        Protected Overrides Sub OnPropertyChanged(args As DependencyPropertyChangedEventArgs)
            MyBase.OnPropertyChanged(args)
            If args.Property IsNot IsPressedProperty OrElse MotionPolicy Is Nothing OrElse Glyph Is Nothing Then Return
            RefreshPressed()
        End Sub

        Private Sub RefreshPressed()
            If Glyph Is Nothing Then Return
            Dim pressed = (IsPressed OrElse _spaceDown) AndAlso MotionPolicy.Interactive AndAlso Not _gestureBlocked
            If MotionPolicy.CanAnimate AndAlso Not IsChecked.GetValueOrDefault() Then
                _ringMotion.Animate(New PclRadioAnimation(Glyph.RingDiameter, If(pressed, PclRadioMotionKind.PressRing, PclRadioMotionKind.ReleaseRing)), If(pressed, 16.5, PclRadioGlyph.RingSize), MotionPolicy.PauseForChecks)
            ElseIf Not MotionPolicy.CanAnimate Then
                SettleGeometry()
            End If
            RefreshColors(True)
        End Sub

        Private Sub RefreshColors(animate As Boolean, Optional checkDuration As Integer = 0)
            If _inkMotion Is Nothing Then Return
            Dim hover = MotionPolicy IsNot Nothing AndAlso MotionPolicy.Interactive AndAlso (IsMouseOver OrElse IsKeyboardFocusWithin)
            Dim ringKey = If(Not IsEnabled, "ColorBrushGray5", If(hover, "ColorBrush3", If(IsChecked.GetValueOrDefault(), "ColorBrush2", "ColorBrush1")))
            Dim textKey = If(Not IsEnabled, "ColorBrushGray5", If(hover, "ColorBrush3", "ColorBrush1"))
            Dim pressed = MotionPolicy IsNot Nothing AndAlso MotionPolicy.Interactive AndAlso (IsPressed OrElse _spaceDown) AndAlso Not _gestureBlocked
            Dim ring = Brush(ringKey).Color, text = Brush(textKey).Color, fill = Brush(If(pressed, "ColorBrushBg1", "ColorBrushHalfWhite")).Color
            If animate AndAlso MotionPolicy.CanAnimate AndAlso Not PclTheme.Changing Then
                Dim duration = TimeSpan.FromMilliseconds(If(checkDuration > 0, checkDuration, If(hover, 100, 200)))
                _inkMotion.Animate(New ColorAnimation(ring, duration), ring, MotionPolicy.PauseForChecks)
                _textMotion.Animate(New ColorAnimation(text, duration), text, MotionPolicy.PauseForChecks)
                _fillMotion.Animate(New ColorAnimation(fill, TimeSpan.FromMilliseconds(100)), fill, MotionPolicy.PauseForChecks)
            Else
                _inkMotion.SetValue(ring) : _textMotion.SetValue(text) : _fillMotion.SetValue(fill)
            End If
        End Sub

        Protected Overrides Sub OnMouseLeftButtonDown(args As MouseButtonEventArgs)
            If Not MotionPolicy.Interactive Then
                _gestureBlocked = True : args.Handled = True : Return
            End If
            _gestureBlocked = False
            MyBase.OnMouseLeftButtonDown(args)
        End Sub
        Protected Overrides Sub OnKeyDown(args As KeyEventArgs)
            If Not MotionPolicy.Interactive Then
                _gestureBlocked = True : args.Handled = True : Return
            End If
            If Not args.IsRepeat Then _gestureBlocked = False
            If args.Key = Key.Space AndAlso (Keyboard.Modifiers And (ModifierKeys.Alt Or ModifierKeys.Control)) = ModifierKeys.None Then
                ' Own the pending release so a page gate can cancel it even when
                ' keyboard focus survives and native mouse capture is unavailable.
                args.Handled = True
                If Not _spaceDown Then
                    _spaceDown = True : RefreshPressed()
                End If
                Return
            End If
            MyBase.OnKeyDown(args)
        End Sub
        Protected Overrides Sub OnKeyUp(args As KeyEventArgs)
            If args.Key = Key.Space Then
                Dim activate = _spaceDown AndAlso Not _gestureBlocked AndAlso MotionPolicy.Interactive
                _spaceDown = False : args.Handled = True
                RefreshPressed()
                If activate Then MyBase.OnClick()
                Return
            End If
            MyBase.OnKeyUp(args)
        End Sub
        Protected Overrides Sub OnLostKeyboardFocus(args As KeyboardFocusChangedEventArgs)
            If _spaceDown Then
                _spaceDown = False : _gestureBlocked = True : RefreshPressed()
            End If
            MyBase.OnLostKeyboardFocus(args)
        End Sub
        Protected Overrides Sub OnClick()
            If Not _gestureBlocked AndAlso MotionPolicy.Interactive Then MyBase.OnClick()
        End Sub

        Protected Overrides Function OnCreateAutomationPeer() As AutomationPeer
            Return New RadioPeer(Me)
        End Function

        Private NotInheritable Class RadioPeer
            Inherits RadioButtonAutomationPeer
            Private ReadOnly _button As PclRadioBox
            Private _selection As ISelectionItemProvider
            Friend Sub New(button As PclRadioBox)
                MyBase.New(button)
                _button = button
            End Sub
            Public Overrides Function GetPattern(pattern As PatternInterface) As Object
                If pattern <> PatternInterface.SelectionItem Then Return MyBase.GetPattern(pattern)
                If _selection Is Nothing Then _selection = New GatedSelection(_button, DirectCast(MyBase.GetPattern(pattern), ISelectionItemProvider))
                Return _selection
            End Function
        End Class

        ' RadioButtonAutomationPeer.Select bypasses OnClick. Delegate its native
        ' grouping/selection rules, with the same page gate as pointer and keys.
        Private NotInheritable Class GatedSelection
            Implements ISelectionItemProvider
            Private ReadOnly _button As PclRadioBox
            Private ReadOnly _inner As ISelectionItemProvider
            Friend Sub New(button As PclRadioBox, inner As ISelectionItemProvider)
                _button = button : _inner = inner
            End Sub
            Private Function Allowed() As Boolean
                If Not _button.IsEnabled Then Throw New ElementNotEnabledException()
                Return _button.MotionPolicy.Interactive
            End Function
            Public Sub [Select]() Implements ISelectionItemProvider.Select
                If Allowed() Then _inner.Select()
            End Sub
            Public Sub AddToSelection() Implements ISelectionItemProvider.AddToSelection
                If Allowed() Then _inner.AddToSelection()
            End Sub
            Public Sub RemoveFromSelection() Implements ISelectionItemProvider.RemoveFromSelection
                If Allowed() Then _inner.RemoveFromSelection()
            End Sub
            Public ReadOnly Property IsSelected As Boolean Implements ISelectionItemProvider.IsSelected
                Get
                    Return _inner.IsSelected
                End Get
            End Property
            Public ReadOnly Property SelectionContainer As IRawElementProviderSimple Implements ISelectionItemProvider.SelectionContainer
                Get
                    Return _inner.SelectionContainer
                End Get
            End Property
        End Class
    End Class
End Namespace
