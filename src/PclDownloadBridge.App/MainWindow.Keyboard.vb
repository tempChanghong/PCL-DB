Imports System.Windows
Imports System.Windows.Input
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private _keyboardVersionReturn As WeakReference(Of PclListItem)

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
        If e.Handled OrElse Not GatedKeyboardSource(e.OriginalSource) Then Return
        ' PCL gates exiting content by hit testing. WPF text editors and selectors
        ' can still receive keyboard input; navigation remains owned by the shell.
        If e.Key = Key.Escape Then ShellKeyDown(sender, e)
        e.Handled = True
    End Sub

    Private Sub ShellKeyDown(sender As Object, e As KeyEventArgs)
        If e.Key <> Key.Escape OrElse e.IsRepeat OrElse e.Handled Then Return
        If AboutPage.Visibility = Visibility.Visible Then
            e.Handled = True
            BackFromAbout(Me, e)
        ElseIf SettingsPage.Visibility = Visibility.Visible Then
            e.Handled = True
            ShowDownload(Me, e)
            FocusDownloadPage()
        ElseIf _downloadView = DownloadView.Progress Then
            e.Handled = True
            ResumeInstallation(Me, e)
            FocusDownloadPage()
        ElseIf _downloadView = DownloadView.Install Then
            e.Handled = True
            BackToVersions(Me, e)
            FocusDownloadPage()
        End If
    End Sub

    Private Sub FocusDownloadPage()
        If _surfaceMotion.Transition IsNot Nothing Then
            _pendingSurfaceFocus = AddressOf FocusDownloadPage
            Return
        End If
        If _pageSwitch.Transition IsNot Nothing OrElse _categoryMotion.Transition IsNot Nothing Then
            _focusAfterPageSwitch = True
            Return
        End If
        DownloadPage.UpdateLayout()
        If _downloadView = DownloadView.Modpacks Then
            If _activeModrinth AndAlso _modrinthSelection IsNot Nothing AndAlso ModrinthInstanceName.IsEnabled Then
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
