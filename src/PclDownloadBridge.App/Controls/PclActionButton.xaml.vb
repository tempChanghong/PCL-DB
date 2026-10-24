Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports System.Windows.Automation
Imports System.Windows.Automation.Peers
Imports System.Windows.Automation.Provider

Namespace Controls
    ' PCL/CE MyIconTextButton's 27px geometry and 70/100/200ms color states,
    ' with native Button keyboard/automation and the bridge's lifecycle policy.
    Partial Public Class PclActionButton
        Public Shared ReadOnly TextProperty As DependencyProperty = DependencyProperty.Register("Text", GetType(String), GetType(PclActionButton), New PropertyMetadata(""))
        Public Shared ReadOnly LogoProperty As DependencyProperty = DependencyProperty.Register("Logo", GetType(Geometry), GetType(PclActionButton), New PropertyMetadata(Nothing))
        Public Shared ReadOnly OutlinedLogoProperty As DependencyProperty = DependencyProperty.Register("OutlinedLogo", GetType(Boolean), GetType(PclActionButton), New PropertyMetadata(False))
        Public Property OutlinedLogo As Boolean
            Get
                Return CBool(GetValue(OutlinedLogoProperty))
            End Get
            Set(value As Boolean)
                SetValue(OutlinedLogoProperty, value)
            End Set
        End Property
        Public Property Text As String
            Get
                Return CStr(GetValue(TextProperty))
            End Get
            Set(value As String)
                SetValue(TextProperty, value)
            End Set
        End Property
        Public Property Logo As Geometry
            Get
                Return DirectCast(GetValue(LogoProperty), Geometry)
            End Get
            Set(value As Geometry)
                SetValue(LogoProperty, value)
            End Set
        End Property
        Private ReadOnly _ink As New SolidColorBrush()
        Private ReadOnly _fill As New SolidColorBrush()
        Private ReadOnly _inkMotion As PclMotionSlot
        Private ReadOnly _fillMotion As PclMotionSlot
        Friend ReadOnly Property MotionPolicy As PclControlMotionPolicy
        Friend ReadOnly Property InkTransition As AnimationClock
            Get
                Return _inkMotion.Clock
            End Get
        End Property
        Friend ReadOnly Property FillTransition As AnimationClock
            Get
                Return _fillMotion.Clock
            End Get
        End Property
        Private _blocked As Boolean, _mouseDown As Boolean, _spaceDown As Boolean, _enterDown As Boolean, _releasing As Boolean
        Private _inputGeneration As Long
        Public Sub New()
            InitializeComponent()
            Foreground = _ink : Background = _fill
            _inkMotion = New PclMotionSlot(_ink, SolidColorBrush.ColorProperty)
            _fillMotion = New PclMotionSlot(_fill, SolidColorBrush.ColorProperty)
            MotionPolicy = New PclControlMotionPolicy(Me, Sub()
                                                             CancelGesture()
                                                             RefreshColors(False)
                                                         End Sub)
            PclTheme.Watch(Me, Sub()
                                   CancelGesture()
                                   RefreshColors(False)
                               End Sub)
            AddHandler MouseEnter, Sub() RefreshColors(True)
            AddHandler GotKeyboardFocus, Sub() RefreshColors(True)
            AddHandler LostKeyboardFocus, Sub()
                                              CancelGesture()
                                              RefreshColors(True)
                                          End Sub
            RefreshColors(False)
        End Sub
        Private Sub CancelGesture()
            _inputGeneration += 1
            _blocked = True : _mouseDown = False : _spaceDown = False : _enterDown = False
            If IsMouseCaptured AndAlso Not _releasing Then ReleaseMouseCapture()
        End Sub
        Private Sub RefreshColors(animate As Boolean)
            If _inkMotion Is Nothing Then Return
            Dim active = MotionPolicy.Interactive AndAlso (IsMouseOver OrElse IsKeyboardFocused)
            Dim pressed = active AndAlso IsPressed AndAlso (_mouseDown OrElse _spaceDown)
            Dim ink = PclAnimation.Brush(If(Not IsEnabled, "ColorBrushGray5", If(active, "ColorBrush3", "ColorBrush1"))).Color
            Dim fill = PclAnimation.Brush(If(pressed, "ColorBrush6", If(active, "ColorBrushBg1", "ColorBrushSemiTransparent"))).Color
            SetResourceReference(BorderBrushProperty, "ColorBrush6")
            If animate AndAlso MotionPolicy.CanAnimate AndAlso Not PclTheme.Changing Then
                Dim duration = TimeSpan.FromMilliseconds(If(pressed, 70, If(active, 100, 200)))
                _inkMotion.Animate(New ColorAnimation(ink, duration), ink, MotionPolicy.PauseForChecks)
                _fillMotion.Animate(New ColorAnimation(fill, duration), fill, MotionPolicy.PauseForChecks)
            Else
                _inkMotion.SetValue(ink) : _fillMotion.SetValue(fill)
            End If
        End Sub
        Public Overrides Sub OnApplyTemplate()
            CancelGesture()
            MyBase.OnApplyTemplate()
            RefreshColors(False)
        End Sub
        Protected Overrides Sub OnPropertyChanged(args As DependencyPropertyChangedEventArgs)
            MyBase.OnPropertyChanged(args)
            If args.Property Is IsPressedProperty Then RefreshColors(Not _blocked)
            If args.Property Is TemplateProperty Then
                CancelGesture() : RefreshColors(False)
            End If
        End Sub
        Protected Overrides Sub OnMouseLeftButtonDown(args As MouseButtonEventArgs)
            If Not MotionPolicy.Interactive Then args.Handled = True : Return
            _blocked = False : _mouseDown = True
            MyBase.OnMouseLeftButtonDown(args)
        End Sub
        Protected Overrides Sub OnMouseLeftButtonUp(args As MouseButtonEventArgs)
            If Not _mouseDown OrElse Not MotionPolicy.Interactive Then _blocked = True
            _releasing = True
            Try
                MyBase.OnMouseLeftButtonUp(args)
            Finally
                _releasing = False : _mouseDown = False
            End Try
        End Sub
        Protected Overrides Sub OnMouseLeave(args As MouseEventArgs)
            MyBase.OnMouseLeave(args)
            If _mouseDown Then CancelGesture()
            RefreshColors(True)
        End Sub
        Protected Overrides Sub OnLostMouseCapture(args As MouseEventArgs)
            MyBase.OnLostMouseCapture(args)
            If Not _releasing Then CancelGesture()
        End Sub
        Protected Overrides Sub OnKeyDown(args As KeyEventArgs)
            If args.Key <> Key.Enter AndAlso args.Key <> Key.Space Then
                MyBase.OnKeyDown(args) : Return
            End If
            If Not MotionPolicy.Interactive OrElse args.IsRepeat Then args.Handled = True : Return
            _blocked = False
            If args.Key = Key.Space Then _spaceDown = True Else _enterDown = True
            MyBase.OnKeyDown(args)
        End Sub
        Protected Overrides Sub OnKeyUp(args As KeyEventArgs)
            If args.Key <> Key.Enter AndAlso args.Key <> Key.Space Then
                MyBase.OnKeyUp(args) : Return
            End If
            If Not If(args.Key = Key.Space, _spaceDown, _enterDown) OrElse Not MotionPolicy.Interactive Then _blocked = True
            _releasing = True
            Try
                MyBase.OnKeyUp(args)
            Finally
                _releasing = False
                If args.Key = Key.Space Then _spaceDown = False Else _enterDown = False
            End Try
            args.Handled = True
        End Sub
        Protected Overrides Sub OnClick()
            If MotionPolicy.Interactive AndAlso (Not _releasing OrElse Not _blocked) Then MyBase.OnClick()
        End Sub
        Protected Overrides Function OnCreateAutomationPeer() As AutomationPeer
            Return New ActionPeer(Me)
        End Function
        Private Sub QueueAutomationInvoke()
            If Not IsEnabled Then Throw New ElementNotEnabledException()
            If Not MotionPolicy.Interactive Then Return
            Dim generation = _inputGeneration
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, New Action(Sub()
                                                                                                    If generation = _inputGeneration AndAlso MotionPolicy.Interactive Then OnClick()
                                                                                                End Sub))
        End Sub
        Private NotInheritable Class ActionPeer
            Inherits ButtonAutomationPeer
            Implements IInvokeProvider
            Private ReadOnly _button As PclActionButton
            Friend Sub New(button As PclActionButton)
                MyBase.New(button) : _button = button
            End Sub
            Public Overrides Function GetPattern(pattern As PatternInterface) As Object
                Return If(pattern = PatternInterface.Invoke, Me, MyBase.GetPattern(pattern))
            End Function
            Public Sub Invoke() Implements IInvokeProvider.Invoke
                _button.QueueAutomationInvoke()
            End Sub
        End Class
    End Class
End Namespace
