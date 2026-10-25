Imports System.Threading
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private _packVerifiedProject As ModpackProject
    ' A finished loader may legitimately contain no downloadable files. Keep its
    ' accepted owner separate from the output count, as PCL's loader state does.
    Private _packDetailCompletedProject As ModpackProject

    ' The accepted search object remains the detail request's owner. Fresh project
    ' metadata supplies presentation/actions without modifying the search snapshot.
    Private Function ActivePackProject() As ModpackProject
        If _packProject IsNot Nothing AndAlso _packVerifiedProject IsNot Nothing AndAlso _packVerifiedProject.Key = _packProject.Key Then Return _packVerifiedProject
        Return _packProject
    End Function

    Private Sub RenderPackProject(project As ModpackProject)
        RetirePackProjectAction()
        PackProjectTitle.Text = ResourceProjectNames.GetTitle(project).Title : PackProjectDescription.Text = project.Summary
        If PackProjectIdentity.Children.Count = 0 OrElse DirectCast(PackProjectIdentity.Children(0), PclResourceItem).Tag IsNot project Then
            PackProjectIdentity.Children.Clear()
            Dim intro As New PclResourceItem(project, _resourceVersionFamilies) With {.Tag = project, .CanInteract = False}
            PackProjectIdentity.Children.Add(intro)
            LoadPackIconAsync(intro, project, If(_packArtworkRequest Is Nothing, CancellationToken.None, _packArtworkRequest.Token))
        End If
        PackProjectWebsite.Text = "转到 " & project.Platform.ToString()
        RefreshTitleNavigation()
    End Sub
End Class
