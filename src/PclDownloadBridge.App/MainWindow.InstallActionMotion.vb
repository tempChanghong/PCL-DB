Imports System.Windows
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private ReadOnly _installActionMotion As PclInstallActionMotion
    Private _installActionEntryOwner As PclSurfaceMotion

    Private Sub ReleaseInstallActionEntry(owner As PclSurfaceMotion, animate As Boolean)
        If _installActionEntryOwner IsNot owner Then Return
        _installActionEntryOwner = Nothing
        RefreshInstallAction(pageEntry:=animate)
    End Sub

    Private Sub RefreshInstallAction(Optional pageEntry As Boolean = False)
        If _installActionMotion Is Nothing Then Return
        Dim shown = Not _closing AndAlso DownloadPage.Visibility = Visibility.Visible AndAlso _downloadView = DownloadView.Install AndAlso _installActionEntryOwner Is Nothing
        Dim horizontal = DownloadPage.Visibility = Visibility.Visible AndAlso _pageSwitch.Transition IsNot Nothing AndAlso
            (_downloadView = DownloadView.Install OrElse _downloadView = DownloadView.Versions)
        _installActionMotion.SelectShown(shown, (horizontal OrElse pageEntry) AndAlso AllowPageMotion())
        ' A repeated refresh can arrive after a different page has taken over.
        ' Retire a previous exit/entry even when the logical target is unchanged.
        If _closing OrElse (Not horizontal AndAlso Not shown) Then _installActionMotion.Finish()
    End Sub
End Class
