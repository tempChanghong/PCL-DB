Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Media.Animation

Namespace Controls
    ' PCL 5ef81e4 MyLocalModItem.Checked: the two height deltas run in parallel.
    ' Selection owns its marker and title ink independently of hover/action motion.
    Friend NotInheritable Class PclLocalModSelectionMotion
        Private ReadOnly _marker As Border, _title As TextBlock
        Private _clock As ClockGroup, _ink As SolidColorBrush
        Private _selected As Boolean, _key As String

        Friend Sub New(marker As Border, title As TextBlock)
            _marker = marker : _title = title
        End Sub
        Friend ReadOnly Property Transition As ClockGroup
            Get
                Return _clock
            End Get
        End Property
        Friend Sub RefreshPalette(key As String)
            _key = key
            If _ink Is Nothing Then
                _title.SetResourceReference(TextBlock.ForegroundProperty, key)
            Else
                _ink.ApplyAnimationClock(SolidColorBrush.ColorProperty, Nothing)
                _ink.Color = PclAnimation.Brush(key).Color
            End If
        End Sub
        Friend Sub Update(selected As Boolean, key As String, animate As Boolean, pause As Boolean)
            If Not animate Then
                Snap(selected, key) : Return
            End If
            If selected = _selected AndAlso key = _key Then Return
            Dim height = _marker.Height, opacity = _marker.Opacity
            Dim color = DirectCast(_title.Foreground, SolidColorBrush).Color
            Detach()
            _selected = selected : _key = key
            _marker.Height = height : _marker.Opacity = opacity
            _ink = New SolidColorBrush(color) : _title.Foreground = _ink
            Dim plan As New ParallelTimeline()
            If selected Then
                plan.Children.Add(New PclSelectedHeightAnimation(height, 32))
                plan.Children.Add(New DoubleAnimation(opacity, 1, TimeSpan.FromMilliseconds(30)))
            Else
                plan.Children.Add(New DoubleAnimation(height, 0, TimeSpan.FromMilliseconds(120)) With {
                    .EasingFunction = New PowerEase With {.Power = 2, .EasingMode = EasingMode.EaseIn}})
                ' Hold the sampled opacity during the first 40 ms, including a
                ' reversal of a partly entered marker before the next render tick.
                Dim alpha As New DoubleAnimationUsingKeyFrames With {.Duration = TimeSpan.FromMilliseconds(110)}
                alpha.KeyFrames.Add(New DiscreteDoubleKeyFrame(opacity, KeyTime.FromTimeSpan(TimeSpan.Zero)))
                alpha.KeyFrames.Add(New DiscreteDoubleKeyFrame(opacity, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(40))))
                alpha.KeyFrames.Add(New LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110))))
                plan.Children.Add(alpha)
            End If
            plan.Children.Add(New PclRadioColorAnimation(color, PclAnimation.Brush(key).Color, If(selected, 200, 120)))
            Dim current = DirectCast(plan.CreateClock(True), ClockGroup)
            If pause Then current.Controller.Pause()
            _clock = current
            AddHandler current.Completed, Sub()
                                              If _clock Is current Then Snap(_selected, _key)
                                          End Sub
            _marker.ApplyAnimationClock(FrameworkElement.HeightProperty, DirectCast(current.Children(0), AnimationClock))
            _marker.ApplyAnimationClock(UIElement.OpacityProperty, DirectCast(current.Children(1), AnimationClock))
            _ink.ApplyAnimationClock(SolidColorBrush.ColorProperty, DirectCast(current.Children(2), AnimationClock))
        End Sub
        Private Sub Snap(selected As Boolean, key As String)
            Detach()
            _selected = selected : _key = key
            _marker.Height = If(selected, 32.0, 0.0) : _marker.Opacity = If(selected, 1.0, 0.0)
            _title.SetResourceReference(TextBlock.ForegroundProperty, key)
        End Sub
        Private Sub Detach()
            Dim previous = _clock : _clock = Nothing
            _marker.ApplyAnimationClock(FrameworkElement.HeightProperty, Nothing)
            _marker.ApplyAnimationClock(UIElement.OpacityProperty, Nothing)
            _ink?.ApplyAnimationClock(SolidColorBrush.ColorProperty, Nothing)
            _ink = Nothing
            previous?.Controller.Remove()
        End Sub
    End Class
End Namespace
