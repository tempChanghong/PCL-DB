Imports System.IO

' PCL ModNet.LoaderDownload deduplicates by normalized LocalPath before starting.
' Merge compatible checks instead of discarding stronger duplicate declarations.
Friend NotInheritable Class DownloadPlan
    Private Sub New()
    End Sub

    Friend Shared Function Merge(files As IEnumerable(Of DownloadFile), Optional root As String = Nothing) As List(Of DownloadFile)
        Dim result As New List(Of DownloadFile)
        Dim targets As New Dictionary(Of String, DownloadFile)(StringComparer.OrdinalIgnoreCase)
        For Each item In files
            If item Is Nothing OrElse String.IsNullOrWhiteSpace(item.RelativePath) Then Throw New InvalidDataException("下载文件路径为空。")
            ' Planners have no root yet; retain unresolved dot segments until the full
            ' batch can be validated against its actual destination, without writes.
            Dim key = If(root Is Nothing, item.RelativePath.Replace("\"c, "/"c), SafeFiles.UnderRoot(root, item.RelativePath))
            Dim existing As DownloadFile = Nothing
            If targets.TryGetValue(key, existing) Then
                If existing.Size >= 0 AndAlso item.Size >= 0 AndAlso existing.Size <> item.Size Then Conflict(item.RelativePath, "文件大小")
                If existing.Size < 0 AndAlso item.Size >= 0 Then existing.Size = item.Size
                existing.Sha1 = MergeHash(existing.Sha1, item.Sha1, item.RelativePath, "SHA-1")
                existing.Md5 = MergeHash(existing.Md5, item.Md5, item.RelativePath, "MD5")
                existing.S3SinglePartETag = MergeHash(existing.S3SinglePartETag, item.S3SinglePartETag, item.RelativePath, "S3 ETag")
            Else
                ' Never mutate records owned by metadata/component callers. Preserve
                ' the first source/category and ordinary transfer-cache key spelling.
                Dim copy As New DownloadFile With {.RelativePath = item.RelativePath, .Url = item.Url, .Size = item.Size,
                    .Sha1 = item.Sha1, .Md5 = item.Md5, .S3SinglePartETag = item.S3SinglePartETag, .Category = item.Category}
                targets.Add(key, copy)
                result.Add(copy)
            End If
        Next
        Return result
    End Function

    Private Shared Function MergeHash(first As String, incoming As String, relative As String, algorithm As String) As String
        If String.IsNullOrWhiteSpace(first) Then Return If(String.IsNullOrWhiteSpace(incoming), first, incoming)
        If Not String.IsNullOrWhiteSpace(incoming) AndAlso Not String.Equals(first, incoming, StringComparison.OrdinalIgnoreCase) Then Conflict(relative, algorithm)
        Return first
    End Function

    Private Shared Sub Conflict(relative As String, field As String)
        Throw New InvalidDataException("同一下载文件的" & field & "声明冲突：" & relative)
    End Sub
End Class
