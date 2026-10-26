Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Media.Animation

Namespace Controls
    ' PageDownloadInstall.SetPanelVisibility from PCL 5ef81e4.
    ' The separate clear button is never part of this animated panel.
    Friend NotInheritable Class PclComponentSummary
        Inherits Grid

        Private _shown As Boolean = True
        Private ReadOnly _offset As New TranslateTransform
        Private _transition As ClockGroup
        ' Clock sampling can pause before attachment, avoiding a short 60-ms
        ' fade completing while the test builds its dispatcher pause frame.
        Friend Property PauseForChecks As Boolean

        Public Sub New()
            Height = 18
            VerticalAlignment = VerticalAlignment.Center
            RenderTransform = _offset
            ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})
            ColumnDefinitions.Add(New ColumnDefinition())
            AddHandler Unloaded, Sub()
                                     If _transition IsNot Nothing Then Settle()
                                 End Sub
        End Sub

        Friend ReadOnly Property Transition As ClockGroup
            Get
                Return _transition
            End Get
        End Property

        Friend ReadOnly Property Offset As Double
            Get
                Return _offset.Y
            End Get
        End Property

        Friend Shared Function TransitionPlan(opacity As Double, offset As Double, shown As Boolean) As ParallelTimeline
            Dim duration = If(shown, 150, 60)
            Dim result As New ParallelTimeline With {.Duration = New Duration(TimeSpan.FromMilliseconds(duration))}
            result.Children.Add(New DoubleAnimation(opacity, If(shown, 1.0, 0.0), TimeSpan.FromMilliseconds(60)))
            result.Children.Add(New DoubleAnimation(offset, If(shown, 0.0, 6.0), TimeSpan.FromMilliseconds(duration)) With {
                .EasingFunction = If(shown, New PowerEase With {.Power = 3, .EasingMode = EasingMode.EaseOut}, Nothing)})
            Return result
        End Function

        Friend Sub SetShown(value As Boolean)
            If _shown = value Then Return
            _shown = value
            IsHitTestVisible = value
            If Not IsLoaded Then
                Settle()
                Return
            End If
            Dim opacity = Me.Opacity, offset = _offset.Y
            StopTransition()
            Me.Opacity = opacity : _offset.Y = offset
            Dim clock = DirectCast(TransitionPlan(opacity, offset, value).CreateClock(True), ClockGroup)
            If PauseForChecks Then clock.Controller.Pause()
            _transition = clock
            AddHandler clock.Completed, Sub()
                                            If _transition Is clock Then Settle()
                                        End Sub
            ApplyAnimationClock(OpacityProperty, DirectCast(clock.Children(0), AnimationClock))
            _offset.ApplyAnimationClock(TranslateTransform.YProperty, DirectCast(clock.Children(1), AnimationClock))
        End Sub

        Private Sub StopTransition()
            Dim previous = _transition
            _transition = Nothing
            previous?.Controller.Remove()
            BeginAnimation(OpacityProperty, Nothing)
            _offset.BeginAnimation(TranslateTransform.YProperty, Nothing)
        End Sub

        Private Sub Settle()
            StopTransition()
            Opacity = If(_shown, 1, 0)
            _offset.Y = If(_shown, 0, 6)
        End Sub
    End Class
End Namespace
