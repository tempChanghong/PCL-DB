Imports System.Windows
Imports System.Windows.Controls
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private ReadOnly _surfaceMotion As PclSurfaceMotion
    Private _motionPreview As Boolean
    Private _pendingSurfaceFocus As Action

    Private Function AllowPageMotion() As Boolean
        Return Not _closing AndAlso (Not _preview OrElse _motionPreview) AndAlso (SystemParameters.ClientAreaAnimation OrElse _motionPreview)
    End Function

    Private Sub RestoreSurfaceFocus()
        Dim focus = _pendingSurfaceFocus
        _pendingSurfaceFocus = Nothing
        If Not _closing Then focus?.Invoke()
    End Sub

    Private Sub InitializeMotion()
        For Each viewer In MotionViewers()
            viewer.SmoothScrollingEnabled = Not _preview
        Next
        AddHandler _surfaceMotion.Settled, Sub() RestoreSurfaceFocus()
        AddHandler PageSurface.Loaded, Sub()
                                          If Not _preview Then _surfaceMotion.Change(Nothing, ActiveSurface(), Sub()
                                                                                                             End Sub)
                                      End Sub
    End Sub

    Private Function MotionViewers() As PclScrollViewer()
        Return {VersionScroll, InstallScroll, ModpackPage, DownloadProgress, SettingsPage, AboutPage}
    End Function
    Private Function ActiveSurface() As FrameworkElement
        Return If(AboutPage.Visibility = Visibility.Visible, DirectCast(AboutPage, FrameworkElement),
            If(SettingsPage.Visibility = Visibility.Visible, DirectCast(SettingsPage, FrameworkElement), DownloadPage))
    End Function
    Private Function SurfaceUnits(surface As FrameworkElement) As IEnumerable(Of FrameworkElement)
        If surface Is DownloadPage Then
            Return PclSurfaceMotion.Units(DownloadContent(_downloadView))
        End If
        Return PclSurfaceMotion.Units(surface)
    End Function
    Private Sub SelectSurface(target As FrameworkElement, Optional prepare As Action = Nothing)
        Dim previous = ActiveSurface()
        If previous IsNot target Then
            _pendingSurfaceFocus = Nothing
            _focusAfterPageSwitch = False
            For Each viewer In MotionViewers()
                viewer.StopScrolling()
            Next
        End If
        _surfaceMotion.Change(previous, target,
            Sub()
                ' Capture the composited category frame before releasing its
                ' clocks; settling first would flash the fully entered page.
                If previous IsNot target Then
                    _categoryMotion.Finish()
                    _packResultMotion?.Finish()
                End If
                DownloadPage.Visibility = If(target Is DownloadPage, Visibility.Visible, Visibility.Collapsed)
                SettingsPage.Visibility = If(target Is SettingsPage, Visibility.Visible, Visibility.Collapsed)
                AboutPage.Visibility = If(target Is AboutPage, Visibility.Visible, Visibility.Collapsed)
                DownloadTab.Checked = target Is DownloadPage
                SettingsTab.Checked = target IsNot DownloadPage
                prepare?.Invoke()
            End Sub, compositeExit:=previous IsNot target AndAlso (_pageSwitch.Transition IsNot Nothing OrElse _categoryMotion.Transition IsNot Nothing OrElse (_packResultMotion IsNot Nothing AndAlso _packResultMotion.Transition IsNot Nothing)))
    End Sub
End Class
