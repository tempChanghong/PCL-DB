Imports System.Windows
Imports System.Windows.Media
Imports System.Windows.Media.Animation

Namespace Controls
    ' PCL 5ef81e4: PageDownloadInstall.EnterSelectPage / ExitSelectPage.
    ' Owns visual state only; component catalogues and download tasks remain in MainWindow.
    Friend NotInheritable Class PclPageSwitch
        Private ReadOnly _host As FrameworkElement
        Private ReadOnly _versions As FrameworkElement
        Private ReadOnly _install As FrameworkElement
        Private ReadOnly _versionOffset As New TranslateTransform
        Private ReadOnly _installOffset As New TranslateTransform
        Private _installSelected As Boolean
        Private _transition As ClockGroup
        Private ReadOnly _enabled As Func(Of Boolean)

        Friend Event Settled As EventHandler

        Friend Sub New(host As FrameworkElement, versions As FrameworkElement, install As FrameworkElement, Optional enabled As Func(Of Boolean) = Nothing)
            _host = host : _versions = versions : _install = install
            _enabled = If(enabled, Function() SystemParameters.ClientAreaAnimation)
            versions.RenderTransform = _versionOffset
            install.RenderTransform = _installOffset
            Settle(False)
            AddHandler host.Unloaded, Sub()
                                          If _transition IsNot Nothing Then Finish()
                                      End Sub
            AddHandler host.IsVisibleChanged, Sub() If Not host.IsVisible AndAlso _transition IsNot Nothing Then Finish()
            AddHandler host.SizeChanged, Sub() If _transition IsNot Nothing Then Finish()
        End Sub

        Friend ReadOnly Property Transition As ClockGroup
            Get
                Return _transition
            End Get
        End Property

        Friend Shared Function TransitionPlan(outOpacity As Double, outX As Double, inOpacity As Double, inX As Double, entering As Boolean) As ParallelTimeline
            Dim plan As New ParallelTimeline With {.Duration = New Duration(TimeSpan.FromMilliseconds(260))}
            plan.Children.Add(New DoubleAnimation(outOpacity, 0, TimeSpan.FromMilliseconds(70)) With {.BeginTime = TimeSpan.FromMilliseconds(10)})
            plan.Children.Add(New DoubleAnimation(outX, If(entering, -50.0, 50.0), TimeSpan.FromMilliseconds(90)) With {.BeginTime = TimeSpan.FromMilliseconds(10)})
            plan.Children.Add(New DoubleAnimation(inOpacity, 1, TimeSpan.FromMilliseconds(70)) With {.BeginTime = TimeSpan.FromMilliseconds(100)})
            plan.Children.Add(New DoubleAnimation(inX, 0, TimeSpan.FromMilliseconds(160)) With {
                .BeginTime = TimeSpan.FromMilliseconds(100), .EasingFunction = New PowerEase With {.Power = 5, .EasingMode = EasingMode.EaseOut}})
            Return plan
        End Function

        Friend Sub SelectInstall(value As Boolean, animate As Boolean)
            If _installSelected = value Then Return
            Dim versionOpacity = _versions.Opacity, versionX = _versionOffset.X
            Dim installOpacity = _install.Opacity, installX = _installOffset.X
            _installSelected = value
            StopTransition()
            If Not animate OrElse Not _enabled() OrElse Not _host.IsVisible OrElse Not _host.IsLoaded Then
                Settle(True)
                Return
            End If
            _versions.Opacity = versionOpacity : _versionOffset.X = versionX
            _install.Opacity = installOpacity : _installOffset.X = installX
            _versions.Visibility = Visibility.Visible : _install.Visibility = Visibility.Visible
            _versions.IsHitTestVisible = Not value : _install.IsHitTestVisible = value
            _host.IsHitTestVisible = False
            Dim outgoing = If(value, _versions, _install), incoming = If(value, _install, _versions)
            Dim outOffset = If(value, _versionOffset, _installOffset), inOffset = If(value, _installOffset, _versionOffset)
            Dim clock = DirectCast(TransitionPlan(outgoing.Opacity, outOffset.X, incoming.Opacity, inOffset.X, value).CreateClock(True), ClockGroup)
            _transition = clock
            AddHandler clock.CurrentTimeInvalidated, Sub()
                                                        If _transition Is clock AndAlso value AndAlso clock.CurrentTime.HasValue AndAlso clock.CurrentTime.Value.TotalMilliseconds >= 100 Then
                                                            _versions.Visibility = Visibility.Collapsed
                                                        End If
                                                    End Sub
            AddHandler clock.Completed, Sub()
                                            If _transition Is clock Then Finish()
                                        End Sub
            outgoing.ApplyAnimationClock(UIElement.OpacityProperty, DirectCast(clock.Children(0), AnimationClock))
            outOffset.ApplyAnimationClock(TranslateTransform.XProperty, DirectCast(clock.Children(1), AnimationClock))
            incoming.ApplyAnimationClock(UIElement.OpacityProperty, DirectCast(clock.Children(2), AnimationClock))
            inOffset.ApplyAnimationClock(TranslateTransform.XProperty, DirectCast(clock.Children(3), AnimationClock))
        End Sub

        Friend Sub Finish()
            StopTransition()
            Settle(True)
        End Sub

        Private Sub StopTransition()
            Dim previous = _transition
            _transition = Nothing
            previous?.Controller.Remove()
            _versions.BeginAnimation(UIElement.OpacityProperty, Nothing)
            _install.BeginAnimation(UIElement.OpacityProperty, Nothing)
            _versionOffset.BeginAnimation(TranslateTransform.XProperty, Nothing)
            _installOffset.BeginAnimation(TranslateTransform.XProperty, Nothing)
        End Sub

        Private Sub Settle(notify As Boolean)
            _versions.Visibility = If(_installSelected, Visibility.Collapsed, Visibility.Visible)
            _install.Visibility = If(_installSelected, Visibility.Visible, Visibility.Collapsed)
            _versions.Opacity = If(_installSelected, 0, 1)
            _install.Opacity = If(_installSelected, 1, 0)
            _versionOffset.X = If(_installSelected, -50, 0)
            ' PCL initially starts at 40 DIP; returning from the install page leaves it at 50 DIP.
            _installOffset.X = If(_installSelected, 0, If(notify, 50, 40))
            _versions.IsHitTestVisible = Not _installSelected
            _install.IsHitTestVisible = _installSelected
            _host.IsHitTestVisible = True
            If notify Then RaiseEvent Settled(Me, EventArgs.Empty)
        End Sub
    End Class
End Namespace
