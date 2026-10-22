Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Controls.Primitives
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Media.Animation

Namespace Controls
    ' MyScrollBar at 5ef81e4. State opacity belongs to the template body, so the
    ' page's existing bar fade/translation keeps independent clock ownership.
    Public NotInheritable Class PclScrollBarChrome
        Inherits Border

        Private _bar As ScrollBar
        Public ReadOnly Property Ink As New SolidColorBrush()
        Friend ReadOnly Property MotionPolicy As PclControlMotionPolicy
        Private ReadOnly _opacityMotion As PclMotionSlot
        Private ReadOnly _colorMotion As PclMotionSlot
        Friend ReadOnly Property OpacityTransition As AnimationClock
            Get
                Return _opacityMotion.Clock
            End Get
        End Property
        Friend ReadOnly Property ColorTransition As AnimationClock
            Get
                Return _colorMotion.Clock
            End Get
        End Property

        Public Sub New()
            _opacityMotion = New PclMotionSlot(Me, OpacityProperty)
            _colorMotion = New PclMotionSlot(Ink, SolidColorBrush.ColorProperty)
            MotionPolicy = New PclControlMotionPolicy(Me, Sub() Refresh(False))
            AddHandler Loaded, AddressOf Attach
            AddHandler Unloaded, Sub()
                                     Detach()
                                     Refresh(False)
                                 End Sub
            PclTheme.Watch(Me, Sub() Refresh(False))
        End Sub

        Private Sub Attach(sender As Object, args As RoutedEventArgs)
            Detach()
            _bar = TryCast(TemplatedParent, ScrollBar)
            If _bar IsNot Nothing Then
                AddHandler _bar.MouseEnter, AddressOf StateChanged
                AddHandler _bar.MouseLeave, AddressOf StateChanged
                AddHandler _bar.GotMouseCapture, AddressOf StateChanged
                AddHandler _bar.LostMouseCapture, AddressOf StateChanged
            End If
            Refresh(False)
        End Sub

        Private Sub Detach()
            If _bar Is Nothing Then Return
            RemoveHandler _bar.MouseEnter, AddressOf StateChanged
            RemoveHandler _bar.MouseLeave, AddressOf StateChanged
            RemoveHandler _bar.GotMouseCapture, AddressOf StateChanged
            RemoveHandler _bar.LostMouseCapture, AddressOf StateChanged
            ' Cancel only this template's gesture, never another control's capture.
            If IsMouseCaptureWithin Then Mouse.Capture(Nothing)
            _bar = Nothing
        End Sub

        Private Sub StateChanged(sender As Object, args As MouseEventArgs)
            Refresh(True)
        End Sub

        Friend Sub Refresh(animate As Boolean)
            If Not MotionPolicy.Interactive AndAlso IsMouseCaptureWithin Then Mouse.Capture(Nothing)
            Dim active = MotionPolicy.Interactive AndAlso _bar IsNot Nothing
            Dim state = ResolveState(_bar IsNot Nothing AndAlso IsLoaded AndAlso IsVisible,
                                     active AndAlso _bar.IsMouseCaptureWithin, active AndAlso _bar.IsMouseOver)
            Dim color = Brush(state.Item2).Color
            If animate AndAlso MotionPolicy.CanAnimate AndAlso Not PclTheme.Changing Then
                Dim duration = TimeSpan.FromMilliseconds(state.Item3)
                _opacityMotion.Animate(New DoubleAnimation(state.Item1, duration), state.Item1, MotionPolicy.PauseForChecks)
                _colorMotion.Animate(New ColorAnimation(color, duration), color, MotionPolicy.PauseForChecks)
            Else
                _opacityMotion.SetValue(state.Item1) : _colorMotion.SetValue(color)
            End If
        End Sub

        Friend Shared Function ResolveState(visible As Boolean, captured As Boolean, hovered As Boolean) As Tuple(Of Double, String, Integer)
            If Not visible Then Return Tuple.Create(0.0, "ColorBrush4", 20)
            If captured Then Return Tuple.Create(1.0, "ColorBrush4", 50)
            If hovered Then Return Tuple.Create(0.9, "ColorBrush3", 130)
            Return Tuple.Create(0.5, "ColorBrush4", 180)
        End Function
    End Class
End Namespace
