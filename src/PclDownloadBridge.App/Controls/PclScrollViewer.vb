Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Controls.Primitives
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Media.Animation

Namespace Controls
    Public Class PclScrollViewer
        Inherits ScrollViewer

        Private _smoothScrollingEnabled As Boolean = True
        Public Property SmoothScrollingEnabled As Boolean
            Get
                Return _smoothScrollingEnabled
            End Get
            Set(value As Boolean)
                If _smoothScrollingEnabled = value Then Return
                _smoothScrollingEnabled = value
                If Not value Then StopInteractiveScrolling()
            End Set
        End Property
        Friend ReadOnly Property MotionPolicy As PclControlMotionPolicy
        Private _forceMotionForChecks As Boolean
        Friend Property ForceMotionForChecks As Boolean
            Get
                Return _forceMotionForChecks
            End Get
            Set(value As Boolean)
                If _forceMotionForChecks = value Then Return
                _forceMotionForChecks = value
                If value Then MotionPolicy.EnabledForChecks = True Else MotionPolicy.EnabledForChecks = Nothing
            End Set
        End Property
        Private _clock As AnimationClock
        Private _requestedOffset As Double = Double.NaN
        Private _lastSample As Double
        Private ReadOnly _issuedOffsets As New List(Of Double)
        Private Shared ReadOnly AnimatedOffsetProperty As DependencyProperty = DependencyProperty.Register("AnimatedOffset", GetType(Double), GetType(PclScrollViewer),
            New PropertyMetadata(0.0, Sub(target, args) DirectCast(target, PclScrollViewer).ApplyOffset(CDbl(args.NewValue))))

        Public Sub New()
            MotionPolicy = New PclControlMotionPolicy(Me, AddressOf StopInteractiveScrolling)
            AddHandler ScrollChanged, AddressOf OffsetChanged
        End Sub

        Friend ReadOnly Property MotionClock As AnimationClock
            Get
                Return _clock
            End Get
        End Property

        Friend ReadOnly Property VerticalBar As ScrollBar
            Get
                ApplyTemplate()
                If ComputedVerticalScrollBarVisibility <> Visibility.Visible Then Return Nothing
                Dim bar = TryCast(GetTemplateChild("PART_VerticalScrollBar"), ScrollBar)
                Return If(bar IsNot Nothing AndAlso bar.IsVisible AndAlso bar.ActualWidth > 0 AndAlso bar.ActualHeight > 0, bar, Nothing)
            End Get
        End Property

        Private Sub ApplyOffset(value As Double)
            If _clock Is Nothing Then Return
            ' Match PCL's incremental AaDouble callback: discard overshoot on
            ' each tick so a later reverse input is not trapped behind an edge.
            Dim increment = value - _lastSample
            _lastSample = value
            _requestedOffset = Math.Max(0, Math.Min(ScrollableHeight, _requestedOffset + increment))
            _issuedOffsets.Add(_requestedOffset)
            If _issuedOffsets.Count > 64 Then _issuedOffsets.RemoveAt(0)
            ScrollToVerticalOffset(_requestedOffset)
        End Sub

        Private Sub OffsetChanged(sender As Object, args As ScrollChangedEventArgs)
            If Not ReferenceEquals(args.OriginalSource, Me) OrElse _clock Is Nothing Then Return
            If args.ViewportHeightChange <> 0 Then
                StopScrolling()
                Return
            End If
            Dim layoutClamp = False
            If args.ExtentHeightChange <> 0 Then
                ' Folding and refreshed cards change the extent on every frame.
                ' Retain the gesture; only clamp outstanding requests to its new
                ' range. A real external position change must still cancel it.
                _requestedOffset = Math.Max(0, Math.Min(ScrollableHeight, _requestedOffset))
                For index = 0 To _issuedOffsets.Count - 1
                    _issuedOffsets(index) = Math.Max(0, Math.Min(ScrollableHeight, _issuedOffsets(index)))
                Next
                Dim previousOffset = VerticalOffset - args.VerticalChange
                layoutClamp = Math.Abs(VerticalOffset - Math.Max(0, Math.Min(ScrollableHeight, previousOffset))) < 0.5
            End If
            If args.VerticalChange <> 0 Then
                ' ScrollToVerticalOffset is queued. A ScrollChanged may acknowledge
                ' an earlier animation tick while the latest command is still pending.
                Dim own = _issuedOffsets.FindIndex(Function(offset) Math.Abs(offset - VerticalOffset) < 0.5)
                If own >= 0 Then
                    _issuedOffsets.RemoveRange(0, own + 1)
                ElseIf Not layoutClamp AndAlso Math.Abs(VerticalOffset - _requestedOffset) > 0.5 Then
                    StopScrolling()
                End If
            End If
        End Sub

        Private Sub StopInteractiveScrolling()
            Dim active = _clock IsNot Nothing, offset = VerticalOffset
            StopScrolling()
            ' Native scroll commands are queued. Replace the last issued tick,
            ' while preserving normal completion's final queued displacement.
            If active Then ScrollToVerticalOffset(offset)
        End Sub

        Friend Sub StopScrolling()
            Dim old = _clock
            _clock = Nothing
            old?.Controller.Remove()
            BeginAnimation(AnimatedOffsetProperty, Nothing)
            SetValue(AnimatedOffsetProperty, VerticalOffset)
            _requestedOffset = Double.NaN
            _issuedOffsets.Clear()
        End Sub

        Friend Sub ScrollDelta(delta As Double)
            If Double.IsNaN(delta) OrElse Double.IsInfinity(delta) OrElse delta = 0 Then Return
            If _issuedOffsets.Count > 0 Then UpdateLayout()
            ' A native acknowledgement can change page/modal ownership while
            ' UpdateLayout runs; validate the owner again before any displacement.
            If Not MotionPolicy.Interactive Then
                StopInteractiveScrolling() : Return
            End If
            If Not SmoothScrollingEnabled OrElse Not MotionPolicy.CanAnimate Then
                StopScrolling()
                ScrollToVerticalOffset(Math.Max(0, Math.Min(ScrollableHeight, VerticalOffset + delta)))
                Return
            End If
            Dim inputs As New List(Of Point)
            If _clock IsNot Nothing Then
                Dim elapsed = If(_clock.CurrentTime, TimeSpan.Zero).TotalMilliseconds
                For Each pulse As Point In DirectCast(_clock.Timeline, PclWheelAnimation).Inputs
                    If pulse.X + elapsed < 300 Then AppendPulse(inputs, pulse.X + elapsed, pulse.Y)
                Next
            End If
            AppendPulse(inputs, 0, delta)
            Dim origin = VerticalOffset
            StopScrolling()
            If inputs.Count = 0 Then Return
            ' This private DP has no bindings. Its real base value must survive
            ' attaching a new, not-yet-active clock; SetCurrentValue can revert
            ' to the default zero at that point and queue a spurious scroll jump.
            SetValue(AnimatedOffsetProperty, origin)
            _lastSample = origin
            _requestedOffset = origin
            Dim clock = DirectCast(New PclWheelAnimation(origin, inputs).CreateClock(True), AnimationClock)
            If MotionPolicy.PauseForChecks Then clock.Controller.Pause()
            _clock = clock
            AddHandler clock.Completed, Sub() If _clock Is clock Then StopScrolling()
            ApplyAnimationClock(AnimatedOffsetProperty, clock)
        End Sub

        Private Shared Sub AppendPulse(inputs As List(Of Point), age As Double, delta As Double)
            ' Pulses are ordered oldest first. Equal-age increments share the
            ' exact same PCL curve, so coalescing them is lossless, including
            ' opposite inputs in one frame. Do not quantize different ages.
            If inputs.Count > 0 AndAlso inputs(inputs.Count - 1).X = age Then
                Dim combined = inputs(inputs.Count - 1).Y + delta
                If combined = 0 Then
                    inputs.RemoveAt(inputs.Count - 1)
                Else
                    inputs(inputs.Count - 1) = New Point(age, combined)
                End If
            ElseIf delta <> 0 Then
                inputs.Add(New Point(age, delta))
            End If
        End Sub

        Friend Function OwnsWheel(source As DependencyObject) As Boolean
            If source Is Nothing Then Return True
            Dim visual = TryCast(source, Visual)
            If visual IsNot Nothing AndAlso PresentationSource.FromVisual(visual)?.RootVisual?.GetType().FullName = "System.Windows.Controls.Primitives.PopupRoot" Then Return False
            Dim cursor = source
            While cursor IsNot Nothing AndAlso cursor IsNot Me
                Dim combo = TryCast(cursor, ComboBox)
                If TypeOf cursor Is ComboBoxItem OrElse (combo IsNot Nothing AndAlso combo.IsDropDownOpen) Then Return False
                Dim text = TryCast(cursor, TextBox)
                If text IsNot Nothing AndAlso text.AcceptsReturn Then Return False
                ' The source-shaped one-line filter has a horizontal overflow
                ' adapter. Let its bubble handler consume the wheel before this
                ' ancestor starts vertical motion during the preview route.
                Dim filter = TryCast(cursor, PclFilterScrollViewer)
                If filter IsNot Nothing AndAlso filter.ScrollableWidth > 0 Then Return False
                Dim nested = TryCast(cursor, ScrollViewer)
                If nested IsNot Nothing AndAlso nested.ScrollableHeight > 0 Then Return False
                cursor = If(TypeOf cursor Is Visual OrElse TypeOf cursor Is Media.Media3D.Visual3D, VisualTreeHelper.GetParent(cursor), LogicalTreeHelper.GetParent(cursor))
            End While
            Return True
        End Function

        Protected Overrides Sub OnPreviewMouseWheel(args As MouseWheelEventArgs)
            If Not MotionPolicy.Interactive Then
                args.Handled = True : Return
            End If
            If args.Handled OrElse args.Delta = 0 OrElse ScrollableHeight <= 0 OrElse Not OwnsWheel(TryCast(args.OriginalSource, DependencyObject)) Then
                MyBase.OnPreviewMouseWheel(args)
                Return
            End If
            args.Handled = True
            ScrollDelta(-args.Delta)
        End Sub

        Protected Overrides Sub OnPreviewMouseDown(args As MouseButtonEventArgs)
            StopScrolling()
            MyBase.OnPreviewMouseDown(args)
        End Sub
        Protected Overrides Sub OnPreviewKeyDown(args As KeyEventArgs)
            StopScrolling()
            MyBase.OnPreviewKeyDown(args)
        End Sub
        Protected Overrides Sub OnPreviewTouchDown(args As TouchEventArgs)
            StopScrolling()
            MyBase.OnPreviewTouchDown(args)
        End Sub
    End Class
End Namespace
