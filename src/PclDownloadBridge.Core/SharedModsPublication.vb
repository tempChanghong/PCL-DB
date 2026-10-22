Imports System.IO
Imports System.Threading

' PCL ModDownloadLib installs add-ons into PathIndie/mods. Shared add-ons use
' the same owned-file rollback as legacy resources, inside the instance commit.
Friend NotInheritable Class SharedModsPublication
    Private Sub New()
    End Sub
    Friend Shared Sub Preflight(root As String, files As List(Of DownloadFile), modable As Boolean)
        ValidateFolders(root, modable)
        LegacyResourcePublication.Validate(root, files, True, "共享 Mods")
        For Each item In files
            Dim target = SafeFiles.UnderRoot(root, item.RelativePath)
            If File.Exists(target) AndAlso (Not String.IsNullOrWhiteSpace(item.Sha1) OrElse Not String.IsNullOrWhiteSpace(item.Md5)) AndAlso Not SafeFiles.Matches(target, item) Then Throw New IOException("已有共享 Mods 内容不同，不会覆盖：" & item.RelativePath)
        Next
    End Sub

    Private Shared Sub ValidateFolders(root As String, modable As Boolean)
        For Each folder In If(modable, {"mods", "resourcepacks"}, {"resourcepacks"})
            Dim path = SafeFiles.UnderRoot(root, folder & "/.pcl-db-directory")
            If File.Exists(IO.Path.GetDirectoryName(path)) Then Throw New IOException("文件占用了共享目录：" & folder)
        Next
    End Sub

    Friend Shared Function CheckedFiles(stage As String, expected As List(Of DownloadFile), token As CancellationToken) As List(Of DownloadFile)
        Dim result As New List(Of DownloadFile)
        For Each item In expected
            token.ThrowIfCancellationRequested()
            Dim path = SafeFiles.UnderRoot(stage, item.RelativePath)
            If Not SafeFiles.Matches(path, item) Then Throw New InvalidDataException("待发布的组件 Mods 校验失败：" & item.RelativePath)
            ' OptiFine may have no remote digest. Freeze the verified staged bytes,
            ' so an existing same-name shared mod is compared by content, not size.
            Using sha = Security.Cryptography.SHA1.Create(), input = File.OpenRead(path)
                result.Add(New DownloadFile With {.RelativePath = item.RelativePath, .Size = input.Length,
                    .Sha1 = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant(), .Category = "component"})
            End Using
        Next
        Return result
    End Function

    Friend Shared Sub FinishFolders(root As String, stage As String, modable As Boolean)
        ValidateFolders(root, modable)
        For Each folder In If(modable, {"mods", "resourcepacks"}, {"resourcepacks"})
            Directory.CreateDirectory(Path.GetDirectoryName(SafeFiles.UnderRoot(root, folder & "/.pcl-db-directory")))
            Dim privateFolder = Path.GetDirectoryName(SafeFiles.UnderRoot(stage, folder & "/.pcl-db-directory"))
            ' Remove empty private folders only. In particular no mod may remain in
            ' versions/name/mods, where PCL could infer a different isolation value.
            If Not Directory.Exists(privateFolder) Then Continue For
            RemoveEmpty(stage, folder)
        Next
    End Sub
    Private Shared Sub RemoveEmpty(stage As String, relative As String)
        Dim target = SafeFiles.UnderRoot(stage, relative)
        For Each child In Directory.EnumerateDirectories(target)
            RemoveEmpty(stage, relative & "/" & Path.GetFileName(child))
        Next
        Directory.Delete(target, False)
    End Sub
End Class
