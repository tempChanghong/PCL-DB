Imports System.Windows
Imports System.Windows.Input
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private _promptFocusReturn As Func(Of Boolean)
    Private _promptFocusEpoch As Long

    Private Sub RetirePromptFocus()
        _promptFocusEpoch += 1 : _promptFocusReturn = Nothing
    End Sub

    Private Sub BeginPromptFocus(preferred As FrameworkElement)
        _promptFocusEpoch += 1
        ' Replacing a still-closing dialog retains the original page target,
        ' unless navigation or an explicit new caller has superseded it.
        If _promptFocusReturn Is Nothing OrElse preferred IsNot Nothing Then _promptFocusReturn = CapturePromptFocus(preferred)
    End Sub

    Private Function PromptViewport(surface As FrameworkElement) As PclScrollViewer
        If surface Is SettingsPage Then Return SettingsPage
        If surface Is AboutPage Then Return AboutPage
        Select Case _downloadView
            Case DownloadView.Modpacks : Return ModpackPage
            Case DownloadView.Install : Return InstallScroll
            Case DownloadView.Progress : Return DownloadProgress
            Case Else : Return VersionScroll
        End Select
    End Function

    Private Shared Function IsPromptFocusTarget(target As FrameworkElement, surface As FrameworkElement) As Boolean
        Return target IsNot Nothing AndAlso target.IsLoaded AndAlso target.IsVisible AndAlso target.IsEnabled AndAlso
            target.IsHitTestVisible AndAlso target.Focusable AndAlso (target Is surface OrElse target.IsDescendantOf(surface))
    End Function

    Private Shared Sub FocusPromptTarget(target As FrameworkElement, viewport As PclScrollViewer)
        ' PCL MyScrollViewer #3854 protects the viewport from focus-driven
        ' scrolling. Preserve the current, clamped position rather than an old
        ' offset captured before an asynchronous catalogue/layout change.
        Dim offset = viewport.VerticalOffset
        FocusManager.SetFocusedElement(FocusManager.GetFocusScope(target), target)
        target.Focus()
        viewport.ScrollToVerticalOffset(offset)
    End Sub

    Private Function CapturePromptFocus(preferred As FrameworkElement) As Func(Of Boolean)
        Dim surface = ActiveSurface(), viewport = PromptViewport(surface)
        Dim view = _downloadView, mode = _packMode, project = If(_packProject?.Key, ""), task = _taskGeneration, entry = _selectedEntry
        Dim target = If(preferred, TryCast(Keyboard.FocusedElement, FrameworkElement))
        If Not IsPromptFocusTarget(target, surface) Then target = TryCast(FocusManager.GetFocusedElement(FocusManager.GetFocusScope(surface)), FrameworkElement)
        Dim weakTarget = If(target Is Nothing, Nothing, New WeakReference(Of FrameworkElement)(target))
        Return Function()
                   If ActiveSurface() IsNot surface OrElse _taskGeneration <> task Then Return False
                   If surface Is DownloadPage AndAlso (_downloadView <> view OrElse
                       (view = DownloadView.Modpacks AndAlso (_packMode <> mode OrElse (mode = PackBrowserMode.Detail AndAlso If(_packProject?.Key, "") <> project))) OrElse
                       (view = DownloadView.Install AndAlso _selectedEntry IsNot entry)) Then Return False
                   Dim origin As FrameworkElement = Nothing
                   If weakTarget IsNot Nothing AndAlso weakTarget.TryGetTarget(origin) AndAlso IsPromptFocusTarget(origin, surface) Then
                       Dim bounds = PclExitSnapshot.VisibleBounds(origin, viewport)
                       If Not bounds.IsEmpty AndAlso bounds.Height >= origin.ActualHeight - 0.5 AndAlso bounds.Width >= origin.ActualWidth - 0.5 Then
                           FocusPromptTarget(origin, viewport) : Return True
                       End If
                   End If
                   If IsPromptFocusTarget(viewport, surface) Then FocusPromptTarget(viewport, viewport)
                   Return True
               End Function
    End Function

    Private Sub QueuePromptFocus(restore As Func(Of Boolean))
        If _closing Then Return
        Dim epoch = _promptFocusEpoch, surface = ActiveSurface(), viewport = PromptViewport(surface)
        Dim scope = FocusManager.GetFocusScope(surface), previous = FocusManager.GetFocusedElement(scope), inputVersion = _downloadFocusInputVersion
        Dim focus As Action = Sub()
                                  If _closing OrElse epoch <> _promptFocusEpoch OrElse _onlinePrompt IsNot Nothing OrElse PackNameOverlay.IsVisible OrElse
                                      ActiveSurface() IsNot surface OrElse Not surface.IsVisible OrElse Not surface.IsEnabled OrElse Not surface.IsHitTestVisible Then Return
                                  Dim current = TryCast(FocusManager.GetFocusedElement(scope), FrameworkElement)
                                  If (current IsNot previous OrElse inputVersion <> _downloadFocusInputVersion) AndAlso IsPromptFocusTarget(current, surface) Then
                                      If surface Is DownloadPage AndAlso _downloadView = DownloadView.Modpacks Then _restorePackViewportFocus = False
                                      Return
                                  End If
                                  If restore IsNot Nothing AndAlso restore() Then
                                      If surface Is DownloadPage AndAlso _downloadView = DownloadView.Modpacks Then _restorePackViewportFocus = False
                                      Return
                                  End If
                                  ' A result may already have opened task management or
                                  ' another page. Retain its newer focus; only supply a
                                  ' default when the old dialog still owns logical focus.
                                  If IsPromptFocusTarget(current, surface) Then
                                      If surface Is DownloadPage AndAlso _downloadView = DownloadView.Modpacks Then _restorePackViewportFocus = False
                                      Return
                                  End If
                                  If surface Is DownloadPage Then
                                      ApplyDownloadFocus()
                                  ElseIf IsPromptFocusTarget(viewport, surface) Then
                                      FocusPromptTarget(viewport, viewport)
                                  End If
                              End Sub
        If surface Is DownloadPage Then
            QueueDownloadFocus(focus)
        ElseIf _surfaceMotion.Transition IsNot Nothing Then
            _pendingSurfaceFocus = focus
        Else
            focus()
        End If
    End Sub
End Class
