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
        Private ReadOnly _motion As Global.PclDownloadBridge.Presentation.PageSwitchMotion
        Private ReadOnly _enabled As Func(Of Boolean)
        Private ReadOnly _cardsEnabled As Func(Of Boolean)
        Private ReadOnly _cards As PclPageCardMotion
        Private _finishing As Boolean
        Private _selectionGeneration As Long

        Friend Event Settled As EventHandler

        Friend Sub New(host As FrameworkElement, versions As FrameworkElement, install As FrameworkElement, Optional enabled As Func(Of Boolean) = Nothing,
                       Optional cardUnits As Func(Of FrameworkElement, IEnumerable(Of FrameworkElement)) = Nothing,
                       Optional cardsEnabled As Func(Of Boolean) = Nothing)
            _host = host : _versions = versions : _install = install
            _motion = New Global.PclDownloadBridge.Presentation.PageSwitchMotion(versions, install, _versionOffset, _installOffset,
                Sub() Settle(_cards Is Nothing OrElse Not _cards.HasMotion))
            _enabled = If(enabled, Function() SystemParameters.ClientAreaAnimation)
            _cardsEnabled = If(cardsEnabled, _enabled)
            If cardUnits IsNot Nothing Then
                _cards = New PclPageCardMotion(host, cardUnits)
                AddHandler _cards.Settled, Sub()
                                              If _motion.Transition Is Nothing AndAlso Not _finishing Then RaiseEvent Settled(Me, EventArgs.Empty)
                                          End Sub
            End If
            versions.RenderTransform = _versionOffset
            install.RenderTransform = _installOffset
            Settle(False, initial:=True)
            AddHandler host.Unloaded, Sub()
                                          If HasMotion Then Finish()
                                      End Sub
            AddHandler host.IsVisibleChanged, Sub() If Not host.IsVisible AndAlso HasMotion Then Finish()
            AddHandler host.SizeChanged, Sub() If HasMotion Then Finish()
        End Sub

        Friend ReadOnly Property CardMotion As PclPageCardMotion
            Get
                Return _cards
            End Get
        End Property
        Friend ReadOnly Property HasMotion As Boolean
            Get
                Return _motion.Transition IsNot Nothing OrElse (_cards IsNot Nothing AndAlso _cards.HasMotion)
            End Get
        End Property

        Friend ReadOnly Property Transition As ClockGroup
            Get
                Return _motion.Transition
            End Get
        End Property

        Friend Shared Function TransitionPlan(outOpacity As Double, outX As Double, inOpacity As Double, inX As Double, entering As Boolean) As ParallelTimeline
            Return Global.PclDownloadBridge.Presentation.PageSwitchMotion.TransitionPlan(outOpacity, outX, inOpacity, inX, entering)
        End Function

        Friend Sub SelectInstall(value As Boolean, animate As Boolean)
            If _installSelected = value Then Return
            _selectionGeneration += 1
            Dim generation = _selectionGeneration
            Dim versionOpacity = _versions.Opacity, versionX = _versionOffset.X
            Dim installOpacity = _install.Opacity, installX = _installOffset.X
            If generation <> _selectionGeneration Then Return
            _installSelected = value
            StopTransition()
            If generation <> _selectionGeneration Then Return
            Dim canAnimate = animate AndAlso _enabled() AndAlso _host.IsVisible AndAlso _host.IsLoaded
            If generation <> _selectionGeneration Then Return
            If Not canAnimate Then
                Finish()
                Return
            End If
            _versions.Opacity = versionOpacity
            If generation <> _selectionGeneration Then Return
            _versionOffset.X = versionX
            If generation <> _selectionGeneration Then Return
            _install.Opacity = installOpacity
            If generation <> _selectionGeneration Then Return
            _installOffset.X = installX
            If generation <> _selectionGeneration Then Return
            _versions.Visibility = Visibility.Visible
            If generation <> _selectionGeneration Then Return
            _install.Visibility = Visibility.Visible
            If generation <> _selectionGeneration Then Return
            _versions.IsHitTestVisible = Not value
            If generation <> _selectionGeneration Then Return
            _install.IsHitTestVisible = value
            If generation <> _selectionGeneration Then Return
            _host.IsHitTestVisible = False
            If generation <> _selectionGeneration Then Return
            Dim outgoing = If(value, _versions, _install), incoming = If(value, _install, _versions)
            Dim outOffset = If(value, _versionOffset, _installOffset), inOffset = If(value, _installOffset, _versionOffset)
            _finishing = True
            Try
                If _cards IsNot Nothing Then
                    Dim animateCards = _cardsEnabled()
                    If generation <> _selectionGeneration Then Return
                    If animateCards Then
                        _cards.Change(outgoing, incoming)
                    Else
                        _cards.Finish()
                    End If
                    If generation <> _selectionGeneration Then Return
                End If
            Catch
                If generation = _selectionGeneration Then Finish()
                Throw
            Finally
                _finishing = False
            End Try
            If generation <> _selectionGeneration Then Return
            _motion.Start(outgoing, incoming, outOffset, inOffset, value)
        End Sub

        Friend Sub Finish()
            _selectionGeneration += 1
            Dim generation = _selectionGeneration
            _finishing = True
            Try
                StopTransition()
                If generation <> _selectionGeneration Then Return
                _cards?.Finish()
                If generation <> _selectionGeneration Then Return
            Finally
                _finishing = False
            End Try
            Settle(True)
        End Sub

        Private Sub StopTransition()
            _motion.Stop()
        End Sub

        Private Sub Settle(notify As Boolean, Optional initial As Boolean = False)
            Dim generation = _selectionGeneration
            _versions.Visibility = If(_installSelected, Visibility.Collapsed, Visibility.Visible)
            If generation <> _selectionGeneration Then Return
            _install.Visibility = If(_installSelected, Visibility.Visible, Visibility.Collapsed)
            If generation <> _selectionGeneration Then Return
            _versions.Opacity = If(_installSelected, 0, 1)
            If generation <> _selectionGeneration Then Return
            _install.Opacity = If(_installSelected, 1, 0)
            If generation <> _selectionGeneration Then Return
            _versionOffset.X = If(_installSelected, -50, 0)
            If generation <> _selectionGeneration Then Return
            ' PCL initially starts at 40 DIP; returning from the install page leaves it at 50 DIP.
            _installOffset.X = If(_installSelected, 0, If(initial, 40, 50))
            If generation <> _selectionGeneration Then Return
            _versions.IsHitTestVisible = Not _installSelected
            If generation <> _selectionGeneration Then Return
            _install.IsHitTestVisible = _installSelected
            If generation <> _selectionGeneration Then Return
            _host.IsHitTestVisible = True
            If generation <> _selectionGeneration Then Return
            If notify Then RaiseEvent Settled(Me, EventArgs.Empty)
        End Sub
    End Class
End Namespace
