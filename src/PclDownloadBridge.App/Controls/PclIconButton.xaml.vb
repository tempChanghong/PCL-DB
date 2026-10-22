Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports System.Windows.Automation
Imports System.Windows.Automation.Peers
Imports System.Windows.Automation.Provider

Namespace Controls
    ' MyIconButton at 5ef81e4: original geometry, White title-bar role and
    ' 400ms press / 250ms parallel rebound. Neutral dark ink is a local adaptation.
    Partial Public Class PclIconButton
        Inherits Border
        Public Event Click As RoutedEventHandler
        Private _down As Boolean
        Private _spaceDown As Boolean
        Private ReadOnly _pressMotion As PclPressMotion
        Private ReadOnly _ink As New SolidColorBrush()
        Private ReadOnly _background As New SolidColorBrush()
        Private ReadOnly _inkMotion As PclMotionSlot
        Private ReadOnly _backgroundMotion As PclMotionSlot
        Friend ReadOnly Property MotionPolicy As PclControlMotionPolicy
        Friend ReadOnly Property InkTransition As AnimationClock
            Get
                Return _inkMotion.Clock
            End Get
        End Property
        Friend ReadOnly Property BackgroundTransition As AnimationClock
            Get
                Return _backgroundMotion.Clock
            End Get
        End Property
        Public Enum Themes
            Neutral
            White
        End Enum
        Private _theme As Themes
        Public Property Theme As Themes
            Get
                Return _theme
            End Get
            Set(value As Themes)
                _theme = value : RefreshColor(False)
            End Set
        End Property
        Friend ReadOnly Property ScaleTransition As AnimationClock
            Get
                Return _pressMotion.Transition
            End Get
        End Property
        Private _logoScale As Double = 1
        Private ReadOnly Property AcceptsInput As Boolean
            Get
                ' Preserve the existing unhosted-control invocation contract. Once
                ' loaded, hidden pages and inherited input gates reject new input.
                Return IsEnabled AndAlso IsHitTestVisible AndAlso (Not IsLoaded OrElse IsVisible)
            End Get
        End Property
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
            Path.Fill = _ink : PanBack.Background = _background
            _inkMotion = New PclMotionSlot(_ink, SolidColorBrush.ColorProperty)
            _backgroundMotion = New PclMotionSlot(_background, SolidColorBrush.ColorProperty)
            _pressMotion = New PclPressMotion(Me, DirectCast(PanBack.RenderTransform, ScaleTransform), Sub()
                                                                                                       _down = False : _spaceDown = False
                                                                                                   End Sub, Function() MotionPolicy.CanAnimate, Function() MotionPolicy.PauseForChecks)
            MotionPolicy = New PclControlMotionPolicy(Me, Sub()
                                                             _pressMotion.Cancel()
                                                             RefreshColor(False)
                                                         End Sub)
            Focusable = True : Cursor = Cursors.Hand
            AddHandler MouseEnter, Sub() RefreshColor(True)
            AddHandler GotKeyboardFocus, Sub() RefreshColor(True)
            AddHandler LostKeyboardFocus, Sub()
                                              If _down OrElse _spaceDown Then _pressMotion.Cancel()
                                              RefreshColor(True)
                                          End Sub
            AddHandler MouseLeave, Sub()
                                       _down = False
                                       _pressMotion.Start(PclPressKind.IconLeave)
                                       RefreshColor(True)
                                   End Sub
            AddHandler MouseLeftButtonDown, Sub(sender, args)
                                               If Not AcceptsInput Then Return
                                               _down = True : Focus()
                                               _pressMotion.Start(PclPressKind.IconDown)
                                               args.Handled = True
                                           End Sub
            AddHandler MouseLeftButtonUp, Sub(sender, args)
                                             If Not _down OrElse Not AcceptsInput Then Return
                                             _down = False
                                             _pressMotion.Start(PclPressKind.IconUp)
                                             RefreshColor(True)
                                             args.Handled = True
                                             RaiseEvent Click(Me, args)
                                         End Sub
            AddHandler KeyDown, Sub(sender, args)
                                    If Not AcceptsInput OrElse (args.Key <> Key.Enter AndAlso args.Key <> Key.Space) Then Return
                                    args.Handled = True
                                    If args.IsRepeat Then Return
                                    _pressMotion.Start(PclPressKind.IconDown)
                                    If args.Key = Key.Space Then
                                        _spaceDown = True
                                    Else
                                        _pressMotion.Start(PclPressKind.IconUp)
                                        RaiseEvent Click(Me, args)
                                    End If
                                End Sub
            AddHandler KeyUp, Sub(sender, args)
                                  If args.Key <> Key.Space Then Return
                                  args.Handled = True
                                  Dim activate = _spaceDown AndAlso AcceptsInput
                                  _spaceDown = False
                                  If Not activate Then Return
                                  _pressMotion.Start(PclPressKind.IconUp)
                                  RaiseEvent Click(Me, args)
                              End Sub
            RefreshColor(False)
            PclTheme.Watch(Me, Sub() RefreshColor(IsLoaded))
        End Sub
        Private Sub RefreshColor(animate As Boolean)
            If _inkMotion Is Nothing Then Return
            Dim hover = MotionPolicy IsNot Nothing AndAlso MotionPolicy.Interactive AndAlso (IsMouseOver OrElse IsKeyboardFocused)
            Dim ink = Brush("PclIconButtonInk").Color
            ' Keep caption ink light even when ColorBrush8 becomes dark in our dark palette.
            Dim targetColor = If(Not IsEnabled, Brush("ColorBrushGray5").Color,
                If(Theme = Themes.White, Colors.White, Color.FromArgb(CByte(If(hover, 230, 160)), ink.R, ink.G, ink.B)))
            Dim back = Color.FromArgb(CByte(If(Theme = Themes.White AndAlso hover, 50, 0)), 255, 255, 255)
            If animate AndAlso MotionPolicy.CanAnimate AndAlso Not PclTheme.Changing Then
                Dim duration = TimeSpan.FromMilliseconds(If(hover OrElse Not IsEnabled, 120, 150))
                _inkMotion.Animate(New ColorAnimation(targetColor, duration), targetColor, MotionPolicy.PauseForChecks)
                _backgroundMotion.Animate(New ColorAnimation(back, duration), back, MotionPolicy.PauseForChecks)
            Else
                _inkMotion.SetValue(targetColor) : _backgroundMotion.SetValue(back)
            End If
        End Sub

        Protected Overrides Function OnCreateAutomationPeer() As AutomationPeer
            Return New IconPeer(Me)
        End Function

        Private NotInheritable Class IconPeer
            Inherits FrameworkElementAutomationPeer
            Implements IInvokeProvider
            Private ReadOnly _button As PclIconButton
            Friend Sub New(button As PclIconButton)
                MyBase.New(button)
                _button = button
            End Sub
            Protected Overrides Function GetAutomationControlTypeCore() As AutomationControlType
                Return AutomationControlType.Button
            End Function
            Protected Overrides Function GetClassNameCore() As String
                Return NameOf(PclIconButton)
            End Function
            Public Overrides Function GetPattern(pattern As PatternInterface) As Object
                Return If(pattern = PatternInterface.Invoke, Me, MyBase.GetPattern(pattern))
            End Function
            Public Sub Invoke() Implements IInvokeProvider.Invoke
                If Not _button.IsEnabled Then Throw New ElementNotEnabledException()
                If Not _button.MotionPolicy.Interactive Then Return
                _button.ActivateFromAutomation()
            End Sub
        End Class

        Private Sub ActivateFromAutomation()
            _pressMotion.Start(PclPressKind.IconUp)
            RaiseEvent Click(Me, New RoutedEventArgs())
        End Sub
    End Class
End Namespace
