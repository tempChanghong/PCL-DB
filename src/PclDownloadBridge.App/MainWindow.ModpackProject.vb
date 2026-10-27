Imports System.Threading
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private _packVerifiedProject As ModpackProject
    ' A finished loader may legitimately contain no downloadable files. Keep its
    ' accepted owner separate from the output count, as PCL's loader state does.
    Private _packDetailCompletedProject As ModpackProject
    Private _packProjectRenderGeneration As Long

    ' The accepted search object remains the detail request's owner. Fresh project
    ' metadata supplies presentation/actions without modifying the search snapshot.
    Private Function ActivePackProject() As ModpackProject
        If _packProject IsNot Nothing AndAlso _packVerifiedProject IsNot Nothing AndAlso _packVerifiedProject.Key = _packProject.Key Then Return _packVerifiedProject
        Return _packProject
    End Function

    Private Sub RenderPackProject(project As ModpackProject)
        _packProjectRenderGeneration += 1
        Dim generation = _packProjectRenderGeneration, owner = _packProject, verifiedOwner = _packVerifiedProject
        RetirePackProjectAction()
        If Not OwnsPackProjectRender(generation, owner, verifiedOwner) Then Return
        If PackProjectIdentity.Children.Count = 0 OrElse DirectCast(PackProjectIdentity.Children(0), PclResourceItem).Tag IsNot project Then
            RetirePackDetailArtwork()
            ' Cancellation can synchronously render fresh metadata or another project.
            ' Leave that later header and its image request together.
            If Not OwnsPackProjectRender(generation, owner, verifiedOwner) Then Return
            PackProjectIdentity.Children.Clear()
            Dim intro As New PclResourceItem(project, _resourceVersionFamilies) With {.Tag = project, .CanInteract = False}
            PackProjectIdentity.Children.Add(intro)
        End If
        PackProjectTitle.Text = ResourceProjectNames.GetTitle(project).Title : PackProjectDescription.Text = project.Summary
        RefreshPackDetailArtwork()
        If Not OwnsPackProjectRender(generation, owner, verifiedOwner) Then Return
        PackProjectWebsite.Text = "转到 " & project.Platform.ToString()
        RefreshTitleNavigation()
    End Sub

    Private Function OwnsPackProjectRender(generation As Long, owner As ModpackProject, verifiedOwner As ModpackProject) As Boolean
        Return Not _closing AndAlso generation = _packProjectRenderGeneration AndAlso _packProject Is owner AndAlso _packVerifiedProject Is verifiedOwner
    End Function
End Class
