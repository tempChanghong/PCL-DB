Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Media.Animation

Namespace Controls
    ' PCL 5ef81e4 MyListItem.SetChecked: two parallel height deltas, not sequential frames.
    Friend NotInheritable Class PclSelectedHeightAnimation
        Inherits DoubleAnimationBase
        Private Shared ReadOnly OriginProperty As DependencyProperty = DependencyProperty.Register("Origin", GetType(Double), GetType(PclSelectedHeightAnimation), New PropertyMetadata(0.0))
        Private Shared ReadOnly TargetProperty As DependencyProperty = DependencyProperty.Register("Target", GetType(Double), GetType(PclSelectedHeightAnimation), New PropertyMetadata(0.0))

        Public Sub New()
            Duration = New Duration(TimeSpan.FromMilliseconds(300))
        End Sub
        Friend Sub New(origin As Double, target As Double)
            Me.New() : SetValue(OriginProperty, origin) : SetValue(TargetProperty, target)
        End Sub
        Friend Function Sample(milliseconds As Double) As Double
            Dim start = CDbl(GetValue(OriginProperty)), delta = CDbl(GetValue(TargetProperty)) - start
            Return Math.Max(0, start + delta * (0.4 * PclMotionCurves.Fluent(milliseconds / 200, 2) + 0.6 * PclMotionCurves.Back(milliseconds / 300, 2)))
        End Function
        Protected Overrides Function GetCurrentValueCore(origin As Double, destination As Double, clock As AnimationClock) As Double
            Return Sample(If(clock.CurrentTime, TimeSpan.Zero).TotalMilliseconds)
        End Function
        Protected Overrides Function CreateInstanceCore() As Freezable
            Return New PclSelectedHeightAnimation()
        End Function
    End Class

    Friend NotInheritable Class PclListSelectionMotion
        Private ReadOnly _implementation As Global.PclDownloadBridge.Presentation.ListSelectionMotion

        Friend Sub New(owner As PclListItem, marker As Border)
            _implementation = New Global.PclDownloadBridge.Presentation.ListSelectionMotion(owner, PclListItem.ForegroundProperty, marker,
                AddressOf PclAnimation.Brush, Function(origin, target) New PclSelectedHeightAnimation(origin, target))
        End Sub
        Friend ReadOnly Property Transition As ClockGroup
            Get
                Return _implementation.Transition
            End Get
        End Property
        Friend Sub RefreshPalette()
            _implementation.RefreshPalette()
        End Sub
        Friend Sub Update(selected As Boolean, animate As Boolean)
            _implementation.Update(selected, animate)
        End Sub
    End Class
End Namespace
