Imports System.ComponentModel
Imports System.Windows
Imports System.Windows.Media.Animation

Namespace Controls
    ' A slot owns one property, including its base value. Replacement starts at the
    ' displayed value; an old completion can never settle a newer clock.
    Friend NotInheritable Class PclMotionSlot
        Private ReadOnly _target As DependencyObject
        Private ReadOnly _property As DependencyProperty
        Friend Property Clock As AnimationClock
            Get
                Return _clock
            End Get
            Private Set(value As AnimationClock)
                _clock = value
            End Set
        End Property
        Private _clock As AnimationClock

        Friend Sub New(target As DependencyObject, [property] As DependencyProperty)
            _target = target : _property = [property]
        End Sub

        Friend Sub Animate(animation As AnimationTimeline, finalValue As Object, Optional pauseForChecks As Boolean = False)
            Dim origin = _target.GetValue(_property)
            SetValue(origin)
            Dim current = DirectCast(animation.CreateClock(True), AnimationClock)
            If pauseForChecks Then current.Controller.Pause()
            Clock = current
            AddHandler current.Completed, Sub()
                                              If Clock Is current Then SetValue(finalValue)
                                          End Sub
            Apply(current)
        End Sub

        Friend Sub SetValue(value As Object)
            Dim previous = Clock : Clock = Nothing
            Apply(Nothing)
            previous?.Controller.Remove()
            ' These are privately owned properties. SetCurrentValue would keep a
            ' coerced default (e.g. opacity 1) as the next clock's origin.
            _target.SetValue(_property, value)
        End Sub

        Private Sub Apply(clock As AnimationClock)
            If TypeOf _target Is Animatable Then
                DirectCast(_target, Animatable).ApplyAnimationClock(_property, clock)
            Else
                DirectCast(_target, UIElement).ApplyAnimationClock(_property, clock)
            End If
        End Sub
    End Class

    ' Shared by local controls and decorative loaders. Never retains unloaded controls in
    ' the static Windows preference event. A preference change settles immediately.
    Friend NotInheritable Class PclControlMotionPolicy
        Private ReadOnly _owner As FrameworkElement
        Private ReadOnly _settle As Action
        Private ReadOnly _changed As PropertyChangedEventHandler
        Private ReadOnly _requiresInteraction As Boolean
        Private _watching As Boolean
        Private _override As Boolean?
        Friend Property PauseForChecks As Boolean

        Friend Sub New(owner As FrameworkElement, settle As Action, Optional requiresInteraction As Boolean = True)
            _owner = owner : _settle = settle : _requiresInteraction = requiresInteraction
            _changed = Sub(sender, args)
                           If args.PropertyName <> NameOf(SystemParameters.ClientAreaAnimation) AndAlso Not String.IsNullOrEmpty(args.PropertyName) Then Return
                           If owner.Dispatcher.CheckAccess() Then
                               If _watching Then settle()
                           Else
                               owner.Dispatcher.BeginInvoke(New Action(Sub()
                                                                           If _watching Then settle()
                                                                       End Sub))
                           End If
                       End Sub
            AddHandler owner.Loaded, Sub()
                                         If Not _watching Then AddHandler SystemParameters.StaticPropertyChanged, _changed
                                         _watching = True : settle()
                                     End Sub
            AddHandler owner.Unloaded, Sub()
                                           If _watching Then RemoveHandler SystemParameters.StaticPropertyChanged, _changed
                                           _watching = False : settle()
                                       End Sub
            AddHandler owner.IsVisibleChanged, Sub() settle()
            AddHandler owner.IsEnabledChanged, Sub() settle()
            AddHandler owner.IsHitTestVisibleChanged, Sub() settle()
        End Sub

        Friend ReadOnly Property Interactive As Boolean
            Get
                Return _owner.IsEnabled AndAlso _owner.IsHitTestVisible AndAlso _owner.IsLoaded AndAlso _owner.IsVisible
            End Get
        End Property

        Friend ReadOnly Property CanAnimate As Boolean
            Get
                Return _watching AndAlso _owner.IsLoaded AndAlso _owner.IsVisible AndAlso
                    (Not _requiresInteraction OrElse Interactive) AndAlso _override.GetValueOrDefault(SystemParameters.ClientAreaAnimation)
            End Get
        End Property

        ' Per-control deterministic seam; never changes the user's Windows settings.
        Friend Property EnabledForChecks As Boolean?
            Get
                Return _override
            End Get
            Set(value As Boolean?)
                _override = value : _settle()
            End Set
        End Property
    End Class
End Namespace
