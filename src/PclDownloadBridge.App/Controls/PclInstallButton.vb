Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Automation
Imports System.Windows.Automation.Peers
Imports System.Windows.Automation.Provider
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Media.Animation

Namespace Controls
    Public NotInheritable Class PclInstallButton
        Inherits Button
        Private _scale As ScaleTransform
        Private _clock As AnimationClock
        Private _mouseDown As Boolean, _spaceDown As Boolean, _enterDown As Boolean
        Private _blocked As Boolean, _releasing As Boolean
        Friend ReadOnly Property MotionPolicy As PclControlMotionPolicy
        Friend ReadOnly Property PressTransition As AnimationClock
            Get
                Return _clock
            End Get
        End Property
        Friend ReadOnly Property PressScale As ScaleTransform
            Get
                Return _scale
            End Get
        End Property
        Friend ReadOnly Property HasPressGesture As Boolean
            Get
                Return _mouseDown OrElse _spaceDown OrElse _enterDown
            End Get
        End Property
        Public Sub New()
            MotionPolicy = New PclControlMotionPolicy(Me, AddressOf CancelPress)
            PclTheme.Watch(Me, AddressOf CancelPress)
        End Sub
        Public Overrides Sub OnApplyTemplate()
            CancelPress()
            MyBase.OnApplyTemplate()
            Dim target = TryCast(GetTemplateChild("PART_Press"), FrameworkElement)
            _scale = TryCast(target?.RenderTransform, ScaleTransform)
            If _scale IsNot Nothing AndAlso _scale.IsFrozen Then
                _scale = _scale.Clone() : target.RenderTransform = _scale
            End If
            ResetScale()
        End Sub
        Protected Overrides Sub OnPropertyChanged(args As DependencyPropertyChangedEventArgs)
            ' Clearing a template need not call OnApplyTemplate. Retire the old
            ' visual before it leaves the tree, including its native gesture.
            If args.Property Is TemplateProperty Then CancelPress()
            MyBase.OnPropertyChanged(args)
        End Sub
        Private Sub ResetScale()
            Dim old = _clock : _clock = Nothing
            old?.Controller.Remove()
            If _scale Is Nothing Then Return
            _scale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, Nothing)
            _scale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, Nothing)
            _scale.ScaleX = 1 : _scale.ScaleY = 1
        End Sub
        Private Sub CancelPress()
            _blocked = True : _mouseDown = False : _spaceDown = False : _enterDown = False
            ResetScale()
            If IsMouseCaptured AndAlso Not _releasing Then ReleaseMouseCapture()
        End Sub
        Private Sub AnimatePress(kind As PclInstallPressKind)
            If _scale Is Nothing Then Return
            If Not MotionPolicy.CanAnimate Then
                ResetScale() : Return
            End If
            Dim origin = _scale.ScaleX
            ResetScale() : _scale.ScaleX = origin : _scale.ScaleY = origin
            Dim clock = DirectCast(New PclInstallPressAnimation(origin, kind).CreateClock(True), AnimationClock)
            If MotionPolicy.PauseForChecks Then clock.Controller.Pause()
            _clock = clock
            If kind <> PclInstallPressKind.Down Then AddHandler clock.Completed, Sub() If _clock Is clock Then ResetScale()
            _scale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, clock)
            _scale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, clock)
        End Sub
        Protected Overrides Sub OnMouseLeftButtonDown(args As MouseButtonEventArgs)
            If Not MotionPolicy.Interactive Then
                _blocked = True : args.Handled = True : Return
            End If
            _blocked = False : _mouseDown = True
            MyBase.OnMouseLeftButtonDown(args)
            If _mouseDown AndAlso MotionPolicy.Interactive Then AnimatePress(PclInstallPressKind.Down)
        End Sub
        Protected Overrides Sub OnMouseLeftButtonUp(args As MouseButtonEventArgs)
            Dim allowed = _mouseDown AndAlso Not _blocked AndAlso MotionPolicy.Interactive
            If Not allowed Then _blocked = True
            _releasing = True
            Try
                MyBase.OnMouseLeftButtonUp(args)
            Finally
                _releasing = False : _mouseDown = False
            End Try
            If allowed AndAlso MotionPolicy.Interactive Then AnimatePress(PclInstallPressKind.Up)
        End Sub
        Protected Overrides Sub OnMouseLeave(args As MouseEventArgs)
            MyBase.OnMouseLeave(args)
            If Not _mouseDown Then Return
            _mouseDown = False : _blocked = True
            AnimatePress(PclInstallPressKind.Leave)
        End Sub
        Protected Overrides Sub OnLostMouseCapture(args As MouseEventArgs)
            MyBase.OnLostMouseCapture(args)
            If Not _releasing AndAlso HasPressGesture Then CancelPress()
        End Sub
        Protected Overrides Sub OnLostKeyboardFocus(args As KeyboardFocusChangedEventArgs)
            MyBase.OnLostKeyboardFocus(args)
            If _spaceDown OrElse _enterDown Then CancelPress()
        End Sub
        Protected Overrides Sub OnKeyDown(args As KeyEventArgs)
            If args.Key <> Key.Space AndAlso args.Key <> Key.Enter Then
                MyBase.OnKeyDown(args) : Return
            End If
            If Not MotionPolicy.Interactive OrElse args.IsRepeat Then
                args.Handled = True : Return
            End If
            _blocked = False
            If args.Key = Key.Space Then _spaceDown = True Else _enterDown = True
            AnimatePress(PclInstallPressKind.Down)
            MyBase.OnKeyDown(args)
        End Sub
        Protected Overrides Sub OnKeyUp(args As KeyEventArgs)
            If args.Key <> Key.Space AndAlso args.Key <> Key.Enter Then
                MyBase.OnKeyUp(args) : Return
            End If
            Dim allowed = If(args.Key = Key.Space, _spaceDown, _enterDown) AndAlso Not _blocked AndAlso MotionPolicy.Interactive
            If Not allowed Then _blocked = True
            _releasing = True
            Try
                MyBase.OnKeyUp(args)
            Finally
                _releasing = False
                If args.Key = Key.Space Then _spaceDown = False Else _enterDown = False
            End Try
            If allowed AndAlso MotionPolicy.Interactive Then AnimatePress(PclInstallPressKind.Up)
            args.Handled = True
        End Sub
        Protected Overrides Sub OnClick()
            ' Preserve explicit native Button automation, but reject a release
            ' belonging to a gesture retired by a page gate or template change.
            If MotionPolicy.Interactive AndAlso (Not _releasing OrElse Not _blocked) Then MyBase.OnClick()
        End Sub
        Protected Overrides Function OnCreateAutomationPeer() As AutomationPeer
            Return New InstallPeer(Me)
        End Function
        Private NotInheritable Class InstallPeer
            Inherits ButtonAutomationPeer
            Private ReadOnly _button As PclInstallButton
            Private _invoke As IInvokeProvider
            Friend Sub New(button As PclInstallButton)
                MyBase.New(button) : _button = button
            End Sub
            Public Overrides Function GetPattern(pattern As PatternInterface) As Object
                If pattern <> PatternInterface.Invoke Then Return MyBase.GetPattern(pattern)
                If _invoke Is Nothing Then _invoke = New GatedInvoke(_button, DirectCast(MyBase.GetPattern(pattern), IInvokeProvider))
                Return _invoke
            End Function
        End Class
        Private NotInheritable Class GatedInvoke
            Implements IInvokeProvider
            Private ReadOnly _button As PclInstallButton
            Private ReadOnly _inner As IInvokeProvider
            Friend Sub New(button As PclInstallButton, inner As IInvokeProvider)
                _button = button : _inner = inner
            End Sub
            Public Sub Invoke() Implements IInvokeProvider.Invoke
                If Not _button.IsEnabled Then Throw New ElementNotEnabledException()
                If _button.MotionPolicy.Interactive Then _inner.Invoke()
            End Sub
        End Class
    End Class
End Namespace
