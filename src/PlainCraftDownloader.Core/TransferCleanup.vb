Imports System.IO
Imports System.Text
Imports System.Text.RegularExpressions

' After a checked target has been committed, cache cleanup must not restart the
' transfer or replace its outcome. Recovery of invalid/incomplete data stays strict.
Friend NotInheritable Class TransferCleanup
    Private Sub New()
    End Sub

    Friend Shared Sub DeleteCommitted(root As String, key As String)
        If Not Regex.IsMatch(key, "\A[0-9a-f]{64}\z") Then Throw New ArgumentException("无效的传输缓存标识。", NameOf(key))
        Dim relative = ".vanilla-downloader/transfers/" & key
        Try
            Dim cache = Path.GetDirectoryName(SafeFiles.UnderRoot(root, relative & "/entry"))
            If Not Directory.Exists(cache) Then Return
            For Each path In Directory.EnumerateFiles(cache, "segment-*.part").ToArray()
                Dim name = IO.Path.GetFileName(path)
                If Regex.IsMatch(name, "\Asegment-\d+\.part\z") Then DeleteFile(root, relative & "/" & name)
            Next
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            LogIncomplete(root, relative, ex)
        End Try
        For Each name In {"segments.json", "segments.tmp", "0.part", "1.part", "2.part", "3.part"}
            DeleteFile(root, relative & "/" & name)
        Next
    End Sub

    Private Shared Sub DeleteFile(root As String, relative As String)
        Try
            Dim path = SafeFiles.UnderRoot(root, relative)
            If File.Exists(path) Then File.Delete(path)
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            LogIncomplete(root, relative, ex)
        End Try
    End Sub

    Private Shared Sub LogIncomplete(root As String, relative As String, failure As Exception)
        Try
            Dim log = SafeFiles.UnderRoot(root, ".vanilla-downloader/download.log")
            File.AppendAllText(log, DateTimeOffset.Now.ToString("o") & " 下载缓存清理未完成：" & relative & " | " & failure.Message & vbCrLf, New UTF8Encoding(False))
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is ArgumentException
            ' Logging is optional once the verified target is published.
        End Try
    End Sub
End Class
