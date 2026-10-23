Imports System.Windows
Imports System.Windows.Input
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private ReadOnly _titleNavigation As PclTitleNavigation
    Private _restorePackViewportFocus As Boolean

    Private Sub RefreshTitleNavigation()
        If _titleNavigation Is Nothing Then Return
        Dim title As String = Nothing
        If DownloadPage.Visibility = Visibility.Visible Then
            If _downloadView = DownloadView.Progress Then
                title = "任务管理"
            ElseIf _downloadView = DownloadView.Modpacks AndAlso _packMode = PackBrowserMode.Detail Then
                title = If(ActivePackProject()?.Name, "整合包详情")
            End If
        End If
        _titleNavigation.SelectTitle(title)
        Dim child = _downloadView = DownloadView.Progress OrElse (_downloadView = DownloadView.Modpacks AndAlso _packMode = PackBrowserMode.Detail)
        DownloadSidebar.Visibility = If(child, Visibility.Collapsed, Visibility.Visible)
        DownloadPage.ColumnDefinitions(0).Width = New GridLength(If(child, 0, 135))
        DownloadToolbar.Visibility = If(child, Visibility.Collapsed, Visibility.Visible)
    End Sub

    ' The existing page owners are the return stack: task -> selected options or
    ' detail -> accepted search snapshot. Back never cancels an installation.
    Private Function NavigateTitleBack() As Boolean
        If _closing OrElse _choosingOnline OrElse _onlinePrompt IsNot Nothing OrElse DownloadPage.Visibility <> Visibility.Visible Then Return False
        If _downloadView = DownloadView.Progress Then
            ResumeInstallation(Me, New RoutedEventArgs())
            Return True
        ElseIf _downloadView = DownloadView.Modpacks AndAlso _packMode = PackBrowserMode.Detail Then
            _restorePackViewportFocus = True
            BackToPackSearch(Me, New RoutedEventArgs())
        Else
            Return False
        End If
        FocusDownloadPage()
        Return True
    End Function

    Private Sub BackFromTitle(sender As Object, e As RoutedEventArgs)
        Dim handled = NavigateTitleBack()
        If e IsNot Nothing AndAlso e.RoutedEvent IsNot Nothing Then e.Handled = handled
    End Sub
    Private Sub ShellBackMouseDown(sender As Object, e As MouseButtonEventArgs)
        If e.Handled Then Return
        If Not GatedKeyboardSource(e.OriginalSource) Then _downloadFocusInputVersion += 1
        If e.ChangedButton <> MouseButton.XButton1 AndAlso e.ChangedButton <> MouseButton.XButton2 Then Return
        e.Handled = NavigateTitleBack()
    End Sub
End Class
