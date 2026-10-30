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
            ' Lazy page policies (such as the Mod selection bar) can be created
            ' after Loaded has already fired. Start observing now, but do not
            ' invoke settle before the caller has assigned its policy field.
            If owner.IsLoaded Then
                AddHandler SystemParameters.StaticPropertyChanged, _changed
                _watching = True
            End If
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
