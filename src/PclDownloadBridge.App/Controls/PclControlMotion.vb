Imports System.ComponentModel
Imports System.Windows
Imports System.Windows.Media.Animation

Namespace Controls
    ' A slot owns one property, including its base value. Replacement starts at the
    ' displayed value; an old completion can never settle a newer clock.
    Friend NotInheritable Class PclMotionSlot
        Private ReadOnly _implementation As Global.PclDownloadBridge.Presentation.MotionSlot
        Friend Property Clock As AnimationClock
            Get
                Return _implementation.Clock
            End Get
            Private Set(value As AnimationClock)
                _implementation.Clock = value
            End Set
        End Property
        Friend Sub New(target As DependencyObject, [property] As DependencyProperty)
            _implementation = New Global.PclDownloadBridge.Presentation.MotionSlot(target, [property])
        End Sub

        Friend Sub Animate(animation As AnimationTimeline, finalValue As Object, Optional pauseForChecks As Boolean = False)
            _implementation.Animate(animation, finalValue, pauseForChecks)
        End Sub

        Friend Sub SetValue(value As Object)
            _implementation.SetValue(value)
        End Sub
    End Class

    ' Shared by local controls and decorative loaders. Never retains unloaded controls in
    ' the static Windows preference event. A preference change settles immediately.
    Friend NotInheritable Class PclControlMotionPolicy
        Private ReadOnly _implementation As Global.PclDownloadBridge.Presentation.ControlMotionPolicy
        Friend Property PauseForChecks As Boolean
            Get
                Return _implementation.PauseForChecks
            End Get
            Set(value As Boolean)
                _implementation.PauseForChecks = value
            End Set
        End Property

        Friend Sub New(owner As FrameworkElement, settle As Action, Optional requiresInteraction As Boolean = True)
            _implementation = New Global.PclDownloadBridge.Presentation.ControlMotionPolicy(owner, settle, requiresInteraction)
        End Sub

        Friend ReadOnly Property Interactive As Boolean
            Get
                Return _implementation.Interactive
            End Get
        End Property

        Friend ReadOnly Property CanAnimate As Boolean
            Get
                Return _implementation.CanAnimate
            End Get
        End Property

        ' Per-control deterministic seam; never changes the user's Windows settings.
        Friend Property EnabledForChecks As Boolean?
            Get
                Return _implementation.EnabledForChecks
            End Get
            Set(value As Boolean?)
                _implementation.EnabledForChecks = value
            End Set
        End Property
    End Class
End Namespace
