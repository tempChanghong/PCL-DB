Imports System.Windows
Imports System.Windows.Input
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private _keyboardVersionReturn As WeakReference(Of PclListItem)
    Private _pendingDownloadFocus As Action
    Private _downloadFocusInputVersion As Long

    Private Shared Function GatedKeyboardSource(source As Object) As Boolean
        Dim target = TryCast(source, DependencyObject)
        While target IsNot Nothing
            Dim element = TryCast(target, UIElement)
            If element IsNot Nothing Then Return Not element.IsHitTestVisible
            target = LogicalTreeHelper.GetParent(target)
        End While
        Return False
    End Function
    Private Sub ShellPreviewKeyDown(sender As Object, e As KeyEventArgs)
        If e.Handled Then Return
        If Not GatedKeyboardSource(e.OriginalSource) Then
            _downloadFocusInputVersion += 1
            Return
        End If
        ' PCL gates exiting content by hit testing. WPF text editors and selectors
        ' can still receive keyboard input; navigation remains owned by the shell.
        If e.Key = Key.Escape Then ShellKeyDown(sender, e)
        e.Handled = True
    End Sub

    Private Sub ShellKeyDown(sender As Object, e As KeyEventArgs)
        If e.Key <> Key.Escape OrElse e.IsRepeat OrElse e.Handled Then Return
        If _onlinePrompt IsNot Nothing Then
            e.Handled = True
            If _onlineOptionalPrompt Then CancelInstall(Nothing, Nothing)
            CompleteOnlinePrompt(Nothing)
            Return
        End If
        If AboutPage.Visibility = Visibility.Visible Then
            e.Handled = True
            BackFromAbout(Me, e)
        ElseIf SettingsPage.Visibility = Visibility.Visible Then
            e.Handled = True
            If _settingsReturnManagement Then
                ShowManagement(Me, e)
            Else
                ShowDownload(Me, e)
                FocusDownloadPage()
            End If
        ElseIf ManagementPage.Visibility = Visibility.Visible Then
            e.Handled = NavigateTitleBack()
        ElseIf _downloadView = DownloadView.Progress Then
            e.Handled = NavigateTitleBack()
        ElseIf _downloadView = DownloadView.Modpacks AndAlso _packMode <> PackBrowserMode.Search Then
            If _packMode = PackBrowserMode.Detail Then
                e.Handled = NavigateTitleBack()
            Else
                e.Handled = True
                BackToPackSearch(Me, e)
                FocusDownloadPage()
            End If
        ElseIf _downloadView = DownloadView.Install Then
            e.Handled = True
            BackToVersions(Me, e)
            FocusDownloadPage()
        End If
    End Sub

    Private Sub FocusDownloadPage(Optional preserveInstallViewport As Boolean = False)
        QueueDownloadFocus(CreateDownloadFocusRequest(preserveInstallViewport))
    End Sub

    Private Function CreateDownloadFocusRequest(Optional preserveInstallViewport As Boolean = False) As Action
        Dim scope = FocusManager.GetFocusScope(DownloadPage)
        Dim previous = FocusManager.GetFocusedElement(scope)
        Dim view = _downloadView, mode = _packMode
        Dim inputVersion = _downloadFocusInputVersion
        Return Sub()
                   If _closing OrElse Not DownloadPage.IsVisible OrElse Not DownloadPage.IsEnabled OrElse _onlinePrompt IsNot Nothing OrElse
                       _downloadView <> view OrElse (view = DownloadView.Modpacks AndAlso _packMode <> mode) Then Return
                   Dim current = TryCast(FocusManager.GetFocusedElement(scope), UIElement)
                   ' PCL allows interaction while cards are entering. An automatic
                   ' completion must not replace a newer pointer or Tab choice.
                   If (current IsNot previous OrElse inputVersion <> _downloadFocusInputVersion) AndAlso current IsNot Nothing AndAlso
                       current.IsVisible AndAlso current.IsEnabled AndAlso current.IsHitTestVisible Then
                       If view = DownloadView.Modpacks Then _restorePackViewportFocus = False
                       Return
                   End If
                   If preserveInstallViewport AndAlso view = DownloadView.Install Then
                       ' PCL MyScrollViewer #3854 avoids focus-driven scrolling.
                       ' A task return restores the existing form, so do not
                       ' bring its possibly offscreen name editor into view.
                       FocusManager.SetFocusedElement(FocusManager.GetFocusScope(InstallScroll), InstallScroll)
                       InstallScroll.Focus()
                   Else
                       ApplyDownloadFocus()
                   End If
               End Sub
    End Function

    Private Sub QueueDownloadFocus(focus As Action)
        If _closing OrElse Not DownloadPage.IsVisible Then Return
        If _surfaceMotion.Transition IsNot Nothing Then
            _pendingSurfaceFocus = Sub() QueueDownloadFocus(focus)
            Return
        End If
        If _pageSwitch.HasMotion OrElse _categoryMotion.Transition IsNot Nothing Then
            _pendingDownloadFocus = focus
            _focusAfterPageSwitch = True
            Return
        End If
        focus()
    End Sub

    Private Sub ClearDownloadFocus()
        _focusAfterPageSwitch = False
        _pendingDownloadFocus = Nothing
    End Sub

    Private Sub RestoreDownloadFocus(sender As Object, e As EventArgs)
        If Not _focusAfterPageSwitch Then Return
        Dim focus = If(_pendingDownloadFocus, CreateDownloadFocusRequest())
        ClearDownloadFocus()
        QueueDownloadFocus(focus)
    End Sub

    Private Sub ApplyDownloadFocus()
        DownloadPage.UpdateLayout()
        If _downloadView = DownloadView.Modpacks Then
            If _restorePackViewportFocus Then
                _restorePackViewportFocus = False
                ' Returning must not focus the offscreen search/name field and
                ' let WPF BringIntoView discard the restored viewport.
                FocusManager.SetFocusedElement(FocusManager.GetFocusScope(ModpackPage), ModpackPage)
                ModpackPage.Focus()
                Return
            End If
            If _packMode = PackBrowserMode.Search Then
                PackSearchText.Focus()
            ElseIf _packMode = PackBrowserMode.Detail Then
                PackProjectWebsite.Focus()
            ElseIf _activeModrinth AndAlso _modrinthSelection IsNot Nothing AndAlso ModrinthInstanceName.IsEnabled Then
                ModrinthInstanceName.Focus()
            ElseIf _packSelection IsNot Nothing AndAlso PackInstanceName.IsEnabled Then
                PackInstanceName.Focus()
            Else
                ImportPackButton.Focus()
            End If
        ElseIf _downloadView = DownloadView.Install Then
            If InstanceName.IsEnabled Then
                InstanceName.Focus()
            Else
                InstallBack.Focus()
            End If
        ElseIf _downloadView = DownloadView.Progress Then
            If CancelButton.Visibility = Visibility.Visible AndAlso CancelButton.IsEnabled Then
                CancelButton.Focus()
            Else
                CopyTaskInfoButton.Focus()
            End If
        Else
            Dim row As PclListItem = Nothing
            If _keyboardVersionReturn IsNot Nothing AndAlso _keyboardVersionReturn.TryGetTarget(row) AndAlso row.IsVisible AndAlso row.Focusable AndAlso row.IsEnabled Then
                row.BringIntoView() : row.Focus()
            ElseIf RefreshButton.IsEnabled Then
                RefreshButton.Focus()
            Else
                DownloadTab.Focus()
            End If
        End If
    End Sub
End Class
