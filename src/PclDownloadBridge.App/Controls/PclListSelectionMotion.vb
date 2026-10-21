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
        Private ReadOnly _owner As PclListItem
        Private ReadOnly _marker As Border
        Private _clock As ClockGroup
        Private _ink As SolidColorBrush
        Private _hasState As Boolean, _selected As Boolean
        Private _targetHeight As Double
        Private _targetColor As Color

        Friend Sub New(owner As PclListItem, marker As Border)
            _owner = owner : _marker = marker
        End Sub
        Friend ReadOnly Property Transition As ClockGroup
            Get
                Return _clock
            End Get
        End Property
        Private Function InkKey(selected As Boolean) As String
            Return If(selected, If(_owner.Height < 40, "ColorBrush3", "ColorBrush2"), "ColorBrush1")
        End Function
        Friend Sub Update(selected As Boolean, animate As Boolean)
            Dim height = If(selected, Math.Max(0, _owner.ActualHeight - 12), 0)
            Dim color = PclAnimation.Brush(InkKey(selected)).Color
            If animate AndAlso _hasState AndAlso selected = _selected AndAlso height = _targetHeight AndAlso color = _targetColor Then Return
            If Not animate Then
                Snap(selected) : Return
            End If
            ' Stable Auto/Stretch geometry follows layout without starting an empty selection.
            If _hasState AndAlso selected = _selected AndAlso _clock Is Nothing Then
                Snap(selected) : Return
            End If
            _hasState = True : _selected = selected : _targetHeight = height : _targetColor = color
            Dim start = _marker.ActualHeight, opacity = _marker.Opacity
            Dim previous = TryCast(_owner.Foreground, SolidColorBrush)
            Dim oldColor = If(previous Is Nothing, PclAnimation.Brush("ColorBrush1").Color, previous.Color)
            Detach()
            _marker.Height = start : _marker.Opacity = opacity
            _marker.Margin = New Thickness(-1, 0, 0, 0) : _marker.VerticalAlignment = VerticalAlignment.Center
            _ink = New SolidColorBrush(oldColor) : _owner.Foreground = _ink
            Dim plan As New ParallelTimeline()
            If selected Then
                plan.Children.Add(New PclSelectedHeightAnimation(start, height))
                plan.Children.Add(New DoubleAnimation(opacity, 1, TimeSpan.FromMilliseconds(30)))
            Else
                plan.Children.Add(New DoubleAnimation(start, 0, TimeSpan.FromMilliseconds(120)) With {
                    .EasingFunction = New PowerEase With {.Power = 2, .EasingMode = EasingMode.EaseIn}})
                plan.Children.Add(New DoubleAnimation(opacity, 0, TimeSpan.FromMilliseconds(70)) With {.BeginTime = TimeSpan.FromMilliseconds(40)})
            End If
            plan.Children.Add(New ColorAnimation(oldColor, color, TimeSpan.FromMilliseconds(If(selected, 200, 120))))
            Dim clock = DirectCast(plan.CreateClock(True), ClockGroup) : _clock = clock
            AddHandler clock.Completed, Sub()
                                            If _clock Is clock Then Snap(selected)
                                        End Sub
            _marker.ApplyAnimationClock(FrameworkElement.HeightProperty, DirectCast(clock.Children(0), AnimationClock))
            _marker.ApplyAnimationClock(UIElement.OpacityProperty, DirectCast(clock.Children(1), AnimationClock))
            _ink.ApplyAnimationClock(SolidColorBrush.ColorProperty, DirectCast(clock.Children(2), AnimationClock))
        End Sub
        Private Sub Snap(selected As Boolean)
            Detach()
            _hasState = True : _selected = selected : _targetHeight = If(selected, Math.Max(0, _owner.ActualHeight - 12), 0)
            _targetColor = PclAnimation.Brush(InkKey(selected)).Color
            _marker.Height = If(selected, Double.NaN, 0)
            _marker.Margin = New Thickness(-1, If(selected, 6, 0), 0, If(selected, 6, 0))
            _marker.VerticalAlignment = If(selected, VerticalAlignment.Stretch, VerticalAlignment.Center)
            _marker.Opacity = If(selected, 1, 0)
            _marker.SetResourceReference(Border.BackgroundProperty, "ColorBrush3")
            _owner.SetResourceReference(PclListItem.ForegroundProperty, InkKey(selected))
        End Sub
        Private Sub Detach()
            Dim old = _clock : _clock = Nothing
            _marker.ApplyAnimationClock(FrameworkElement.HeightProperty, Nothing)
            _marker.ApplyAnimationClock(UIElement.OpacityProperty, Nothing)
            _ink?.ApplyAnimationClock(SolidColorBrush.ColorProperty, Nothing)
            old?.Controller.Remove()
        End Sub
    End Class
End Namespace
