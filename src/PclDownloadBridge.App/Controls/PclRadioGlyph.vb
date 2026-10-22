Imports System.Windows
Imports System.Windows.Media
Imports System.Windows.Media.Animation

Namespace Controls
    ' MyRadioBox's 18/9 DIP geometry, fixed 1.1 DIP outline and centre (10,10).
    ' Draw diameters directly instead of AaScale's per-frame Width/Height/Margin:
    ' this preserves the outline thickness without invalidating ancestor layout.
    Public NotInheritable Class PclRadioGlyph
        Inherits FrameworkElement
        Public Const RingSize As Double = 18
        Public Const DotSize As Double = 9
        Public Const OutlineThickness As Double = 1.1
        Public Shared ReadOnly RingDiameterProperty As DependencyProperty = DependencyProperty.Register("RingDiameter", GetType(Double), GetType(PclRadioGlyph), New FrameworkPropertyMetadata(RingSize, FrameworkPropertyMetadataOptions.AffectsRender))
        Public Shared ReadOnly DotDiameterProperty As DependencyProperty = DependencyProperty.Register("DotDiameter", GetType(Double), GetType(PclRadioGlyph), New FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender))
        Public Shared ReadOnly DotOpacityProperty As DependencyProperty = DependencyProperty.Register("DotOpacity", GetType(Double), GetType(PclRadioGlyph), New FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender))
        Public Shared ReadOnly StrokeProperty As DependencyProperty = DependencyProperty.Register("Stroke", GetType(Brush), GetType(PclRadioGlyph), New FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender))
        Public Shared ReadOnly RingFillProperty As DependencyProperty = DependencyProperty.Register("RingFill", GetType(Brush), GetType(PclRadioGlyph), New FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender))

        Friend ReadOnly Property RingDiameter As Double
            Get
                Return CDbl(GetValue(RingDiameterProperty))
            End Get
        End Property
        Friend ReadOnly Property DotDiameter As Double
            Get
                Return CDbl(GetValue(DotDiameterProperty))
            End Get
        End Property
        Friend ReadOnly Property DotOpacity As Double
            Get
                Return CDbl(GetValue(DotOpacityProperty))
            End Get
        End Property

        Protected Overrides Sub OnRender(context As DrawingContext)
            MyBase.OnRender(context)
            Dim center As New Point(10, 10)
            Dim radius = Math.Max(0, (RingDiameter - OutlineThickness) / 2)
            Dim ink = DirectCast(GetValue(StrokeProperty), Brush)
            context.DrawEllipse(DirectCast(GetValue(RingFillProperty), Brush), New Pen(ink, OutlineThickness), center, radius, radius)
            If DotDiameter <= 0 OrElse DotOpacity <= 0 Then Return
            context.PushOpacity(Math.Max(0, Math.Min(1, DotOpacity)))
            context.DrawEllipse(ink, Nothing, center, DotDiameter / 2, DotDiameter / 2)
            context.Pop()
        End Sub
    End Class

    Friend Enum PclRadioMotionKind
        CheckRing
        CheckDot
        UncheckRing
        UncheckDot
        PressRing
        ReleaseRing
    End Enum

    ' Direct transcription of MyRadioBox.SyncUI and mouse down/leave AaScale deltas.
    Friend NotInheritable Class PclRadioAnimation
        Inherits DoubleAnimationBase
        Private Shared ReadOnly OriginProperty As DependencyProperty = DependencyProperty.Register("Origin", GetType(Double), GetType(PclRadioAnimation), New PropertyMetadata(0.0))
        Private Shared ReadOnly KindProperty As DependencyProperty = DependencyProperty.Register("Kind", GetType(PclRadioMotionKind), GetType(PclRadioAnimation), New PropertyMetadata(PclRadioMotionKind.CheckRing))
        Public Sub New()
            Duration = New Duration(TimeSpan.FromMilliseconds(390))
        End Sub
        Friend Sub New(origin As Double, kind As PclRadioMotionKind)
            Me.New()
            SetValue(OriginProperty, origin) : SetValue(KindProperty, kind)
            Dim length = If(kind = PclRadioMotionKind.PressRing, 1000, If(kind = PclRadioMotionKind.ReleaseRing, 400,
                If(kind = PclRadioMotionKind.UncheckRing OrElse kind = PclRadioMotionKind.UncheckDot, 150, 390)))
            Duration = New Duration(TimeSpan.FromMilliseconds(length))
        End Sub
        Friend Function Sample(milliseconds As Double) As Double
            Dim start = CDbl(GetValue(OriginProperty))
            Select Case DirectCast(GetValue(KindProperty), PclRadioMotionKind)
                Case PclRadioMotionKind.CheckRing
                    Return start + (10 - start) * PclMotionCurves.Fluent(milliseconds / 150, 2) + 8 * PclMotionCurves.Back((milliseconds - 90) / 300)
                Case PclRadioMotionKind.CheckDot
                    Return start + (9 - start) * PclMotionCurves.Back(milliseconds / 390, 2)
                Case PclRadioMotionKind.UncheckRing
                    Return start + (18 - start) * PclMotionCurves.Fluent(milliseconds / 150, 3)
                Case PclRadioMotionKind.UncheckDot
                    Return start * (1 - Math.Pow(Math.Max(0, Math.Min(1, milliseconds / 150)), 3))
                Case PclRadioMotionKind.PressRing
                    Return start + (16.5 - start) * PclMotionCurves.Fluent(milliseconds / 1000, 4)
                Case Else
                    Return start + (18 - start) * PclMotionCurves.Fluent(milliseconds / 400, 4)
            End Select
        End Function
        Protected Overrides Function GetCurrentValueCore(origin As Double, destination As Double, clock As AnimationClock) As Double
            Return Sample(If(clock.CurrentTime, TimeSpan.Zero).TotalMilliseconds)
        End Function
        Protected Overrides Function CreateInstanceCore() As Freezable
            Return New PclRadioAnimation()
        End Function
    End Class
End Namespace
