Imports System.IO
Imports System.Text

' Best-effort cumulative game-directory log, shared by downloads and processors.
Friend Module InstallLog
    Private ReadOnly Gate As New Object()

    Friend Sub Append(root As String, message As String)
        If String.IsNullOrWhiteSpace(root) Then Return
        Try
            Dim path = SafeFiles.UnderRoot(root, ".vanilla-downloader/installer.log")
            SyncLock Gate
                Directory.CreateDirectory(IO.Path.GetDirectoryName(path))
                File.AppendAllText(path, DateTimeOffset.Now.ToString("o") & " " & message & vbCrLf, New UTF8Encoding(False))
            End SyncLock
        Catch
            ' Missing permissions, occupied paths or unavailable optional logs must
            ' never replace success, cancellation, or the original failure.
        End Try
    End Sub

    Friend Sub Failure(root As String, title As String, errors As IEnumerable(Of Exception))
        If String.IsNullOrWhiteSpace(root) Then Return
        Try
            Dim failures = errors.Where(Function(issue) issue IsNot Nothing).ToArray()
            If failures.Length > 0 Then Append(root, New AggregateException(title, failures).ToString())
        Catch
            ' Diagnostic collection/formatting is optional too.
        End Try
    End Sub
End Module
