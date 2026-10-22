Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Media.Animation

Namespace Controls
    ' PCL 5ef81e4 MyLoading.xaml / AniLoop: 250ms pause, 350ms backswing,
    ' then parallel 50deg fluent + 25deg weak elastic over 900ms; debris at 600ms.
    Friend NotInheritable Class PclLoadingView
        Inherits StackPanel
        Private ReadOnly _pick As RotateTransform
        Private ReadOnly _left As Shapes.Path
        Private ReadOnly _right As Shapes.Path
        Private _clock As ClockGroup
        Friend ReadOnly Property Transition As ClockGroup
            Get
                Return _clock
            End Get
        End Property
        Public Sub New(text As String)
            HorizontalAlignment = HorizontalAlignment.Center : Margin = New Thickness(20, 20, 20, 17)
            Dim graphic As New Grid With {.Width = 60, .Height = 47}
            _pick = New RotateTransform(55, 30, 30)
            Dim pickaxe As New Shapes.Path With {.Data = Geometry.Parse("M 963.6 858.2 410.816 305.504 C 508.116 213.304 609.204 196.8 711.104 128.6 837.11367 49.573762 879.34045 50.334062 751.5 49.5 611.3 52 471.8 96.2 353.3 182.4 309.8 155.7 252.1 161.2 214.5 198.9 176.9 236.6 171.3 294.2 198 337.7 111.8 456.3 67.6 595.8 65.1 735.9 63.315254 883.82034 65.077966 837.29308 144.2 695.488 212.4 593.588 228.888 492.4 321.088 395.2 L 873.9 948 c 0.60001 0.59999 1.6 0.6 2.2 0 l 87.5 -87.5 c 0.6 -0.7 0.6 -1.6 0 -2.3 z"),
                .Height = 35, .Stretch = Stretch.Uniform, .StrokeThickness = 2, .Margin = New Thickness(10, 6, 0, 0), .HorizontalAlignment = HorizontalAlignment.Left, .VerticalAlignment = VerticalAlignment.Top, .RenderTransform = _pick}
            pickaxe.SetResourceReference(Shapes.Path.StrokeProperty, "ColorBrush1") : graphic.Children.Add(pickaxe)
            _left = Debris(7, -45) : _right = Debris(14, 45) : graphic.Children.Add(_left) : graphic.Children.Add(_right)
            Dim ground As New Shapes.Rectangle With {.Width = 25, .Height = 2, .VerticalAlignment = VerticalAlignment.Bottom, .HorizontalAlignment = HorizontalAlignment.Left}
            ground.SetResourceReference(Shapes.Rectangle.FillProperty, "ColorBrush1") : graphic.Children.Add(ground) : Children.Add(graphic)
            Dim label As New TextBlock With {.Text = text, .FontSize = 16, .TextWrapping = TextWrapping.Wrap, .HorizontalAlignment = HorizontalAlignment.Center, .Margin = New Thickness(0, 10, 0, 0)}
            label.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1") : Children.Add(label)
            AddHandler Loaded, Sub() Refresh()
            AddHandler IsVisibleChanged, Sub() Refresh()
            AddHandler Unloaded, Sub() StopMotion()
        End Sub
        Private Shared Function Debris(left As Double, rotation As Double) As Shapes.Path
            Dim result As New Shapes.Path With {.Data = Geometry.Parse("M0,0L2,0L1,4"), .Stretch = Stretch.Fill, .Width = 3, .Height = 5, .Opacity = 0, .Margin = New Thickness(left, 41, 0, 0), .HorizontalAlignment = HorizontalAlignment.Left, .VerticalAlignment = VerticalAlignment.Top, .RenderTransformOrigin = New Point(0.5, 0.5)}
            result.SetResourceReference(Shapes.Path.FillProperty, "ColorBrush1")
            Dim group As New TransformGroup() : group.Children.Add(New RotateTransform(rotation)) : group.Children.Add(New TranslateTransform()) : result.RenderTransform = group
            Return result
        End Function
        Private Sub Refresh()
            StopMotion()
            If Not IsLoaded OrElse Not IsVisible OrElse Not SystemParameters.ClientAreaAnimation Then Return
            Dim timeline As New ParallelTimeline With {.Duration = New Duration(TimeSpan.FromMilliseconds(1500)), .RepeatBehavior = RepeatBehavior.Forever}
            timeline.Children.Add(New PclPickaxeAnimation())
            For Each direction In {-1.0, 1.0}
                Dim opacity As New DoubleAnimationUsingKeyFrames()
                opacity.KeyFrames.Add(New DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)))
                opacity.KeyFrames.Add(New DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(600))))
                opacity.KeyFrames.Add(New LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(650))))
                opacity.KeyFrames.Add(New LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(750))))
                timeline.Children.Add(opacity)
                timeline.Children.Add(New DoubleAnimation(0, direction * 5, TimeSpan.FromMilliseconds(180)) With {.BeginTime = TimeSpan.FromMilliseconds(600), .EasingFunction = New PowerEase With {.Power = 3, .EasingMode = EasingMode.EaseOut}})
                timeline.Children.Add(New DoubleAnimation(0, -6, TimeSpan.FromMilliseconds(180)) With {.BeginTime = TimeSpan.FromMilliseconds(600), .EasingFunction = New PowerEase With {.Power = 3, .EasingMode = EasingMode.EaseOut}})
            Next
            _clock = DirectCast(timeline.CreateClock(True), ClockGroup)
            _pick.ApplyAnimationClock(RotateTransform.AngleProperty, DirectCast(_clock.Children(0), AnimationClock))
            Dim index = 1
            For Each piece In {_left, _right}
                Dim offset = DirectCast(DirectCast(piece.RenderTransform, TransformGroup).Children(1), TranslateTransform)
                piece.ApplyAnimationClock(OpacityProperty, DirectCast(_clock.Children(index), AnimationClock))
                offset.ApplyAnimationClock(TranslateTransform.XProperty, DirectCast(_clock.Children(index + 1), AnimationClock))
                offset.ApplyAnimationClock(TranslateTransform.YProperty, DirectCast(_clock.Children(index + 2), AnimationClock)) : index += 3
            Next
        End Sub
        Private Sub StopMotion()
            Dim old = _clock : _clock = Nothing
            _pick.ApplyAnimationClock(RotateTransform.AngleProperty, Nothing) : _pick.Angle = 55
            For Each piece In {_left, _right}
                piece.ApplyAnimationClock(OpacityProperty, Nothing) : piece.Opacity = 0
                Dim offset = DirectCast(DirectCast(piece.RenderTransform, TransformGroup).Children(1), TranslateTransform)
                offset.ApplyAnimationClock(TranslateTransform.XProperty, Nothing) : offset.X = 0
                offset.ApplyAnimationClock(TranslateTransform.YProperty, Nothing) : offset.Y = 0
            Next
            old?.Controller.Remove()
        End Sub
    End Class
    Friend NotInheritable Class PclPickaxeAnimation
        Inherits DoubleAnimationBase
        Public Sub New()
            Duration = New Duration(TimeSpan.FromMilliseconds(1500))
        End Sub
        Friend Shared Function Sample(milliseconds As Double) As Double
            If milliseconds <= 250 Then Return 55
            If milliseconds < 600 Then
                Dim t = (milliseconds - 250) / 350
                Return 55 - 75 * Math.Pow(t, 2) * Math.Cos(1.5 * Math.PI * (1 - t))
            End If
            Dim u = Math.Max(0, Math.Min(1, (milliseconds - 600) / 900))
            Return -20 + 50 * PclMotionCurves.Fluent(u, 3) + 25 * (1 - Math.Pow(1 - u, 1.25) * Math.Cos(2.5 * Math.PI * Math.Pow(u, 1.5)))
        End Function
        Protected Overrides Function GetCurrentValueCore(origin As Double, destination As Double, clock As AnimationClock) As Double
            Return Sample(If(clock.CurrentTime, TimeSpan.Zero).TotalMilliseconds)
        End Function
        Protected Overrides Function CreateInstanceCore() As Freezable
            Return New PclPickaxeAnimation()
        End Function
    End Class
End Namespace
