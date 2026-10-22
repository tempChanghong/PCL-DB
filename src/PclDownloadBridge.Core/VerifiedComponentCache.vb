Imports System.IO
Imports System.Text.RegularExpressions
Imports System.Threading

' Private checksum-addressed stores for downloads and declared processor outputs.
' It never promotes install-only files into the launcher's shared libraries.
Friend Class VerifiedComponentCache
    Private ReadOnly _root As String
    Private ReadOnly _readRoots As List(Of String)
    Private ReadOnly _processorOutputs As Boolean

    Friend Sub New(root As String, reuseRoots As IEnumerable(Of String), Optional processorOutputs As Boolean = False)
        _root = root
        _readRoots = {root}.Concat(reuseRoots).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        _processorOutputs = processorOutputs
    End Sub

    Private Shared Function RelativePath(item As DownloadFile, Optional processorOutputs As Boolean = False) As String
        Dim algorithm As String, digest As String
        If Not String.IsNullOrWhiteSpace(item.Sha512) Then
            If Not Regex.IsMatch(item.Sha512, "\A[0-9a-fA-F]{128}\z") Then Return Nothing
            algorithm = "sha512" : digest = item.Sha512.ToLowerInvariant()
        ElseIf Not String.IsNullOrWhiteSpace(item.Sha1) Then
            If Not Regex.IsMatch(item.Sha1, "\A[0-9a-fA-F]{40}\z") Then Return Nothing
            algorithm = "sha1" : digest = item.Sha1.ToLowerInvariant()
        ElseIf Not String.IsNullOrWhiteSpace(item.Md5) Then
            If Not Regex.IsMatch(item.Md5, "\A[0-9a-fA-F]{32}\z") Then Return Nothing
            algorithm = "md5" : digest = item.Md5.ToLowerInvariant()
        ElseIf Regex.IsMatch(If(item.S3SinglePartETag, ""), "\A[0-9a-fA-F]{32}-1\z") Then
            algorithm = "s3-etag1" : digest = item.S3SinglePartETag.Substring(0, 32).ToLowerInvariant()
        Else
            ' A stable-looking filename or URL is not an integrity declaration.
            Return Nothing
        End If
        Return ".vanilla-downloader/" & If(processorOutputs, "processor-cache/", "component-cache/") & algorithm & "/" & digest.Substring(0, 2) & "/" & digest & ".bin"
    End Function

    Friend Function TryRestore(target As String, item As DownloadFile, token As CancellationToken) As Boolean
        Dim relative = RelativePath(item, _processorOutputs)
        If relative Is Nothing Then Return False
        For Each readRoot In _readRoots
            token.ThrowIfCancellationRequested()
            Dim temporary As String = Nothing
            Try
                Try
                    Dim candidate = SafeFiles.UnderRoot(readRoot, relative)
                    If Not SafeFiles.Matches(candidate, item) Then Continue For
                    temporary = target & ".cache-" & Guid.NewGuid().ToString("N")
                    CopyChecked(candidate, temporary, item, token)
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is ArgumentException
                    ' Optional cache reads/copies may fall back. A real destination
                    ' commit failure below must remain an error, just as with reuse.
                    Continue For
                End Try
                SafeFiles.CommitFile(temporary, target)
                Return True
            Finally
                DeleteTemporary(temporary)
            End Try
        Next
        Return False
    End Function

    Friend Sub Save(source As String, item As DownloadFile, token As CancellationToken)
        Dim relative = RelativePath(item, _processorOutputs)
        If relative Is Nothing Then Return
        Dim temporary As String = Nothing
        Try
            token.ThrowIfCancellationRequested()
            Dim target = SafeFiles.UnderRoot(_root, relative)
            If SafeFiles.Matches(target, item) Then Return
            Directory.CreateDirectory(Path.GetDirectoryName(target))
            temporary = target & ".cache-" & Guid.NewGuid().ToString("N")
            CopyChecked(source, temporary, item, token)
            SafeFiles.CommitFile(temporary, target)
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is ArgumentException
            ' Persisting a performance cache must not invalidate a completed download.
        Finally
            DeleteTemporary(temporary)
        End Try
    End Sub

    Friend Shared Sub ReleasePublishedClient(root As String, item As DownloadFile)
        Dim relative = RelativePath(item)
        If relative Is Nothing Then Return
        Try
            Dim cached = SafeFiles.UnderRoot(root, relative)
            ' versions/ now supplies future client reuse; retain this extra copy only
            ' when installation was interrupted before publication.
            If SafeFiles.Matches(cached, item) Then File.Delete(cached)
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is ArgumentException
            ' Cleanup must not turn a successful publication into an install failure.
        End Try
    End Sub

    Private Shared Sub CopyChecked(source As String, temporary As String, item As DownloadFile, token As CancellationToken)
        Using input As New FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read), output As New FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)
            Dim buffer(65535) As Byte
            Do
                token.ThrowIfCancellationRequested()
                Dim count = input.Read(buffer, 0, buffer.Length)
                If count = 0 Then Exit Do
                output.Write(buffer, 0, count)
            Loop
        End Using
        token.ThrowIfCancellationRequested()
        If Not SafeFiles.Matches(temporary, item) Then Throw New InvalidDataException("组件缓存副本校验失败。")
        token.ThrowIfCancellationRequested()
    End Sub

    Private Shared Sub DeleteTemporary(temporary As String)
        If temporary Is Nothing Then Return
        Try
            If File.Exists(temporary) Then File.Delete(temporary)
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            ' Never make an otherwise valid install fail because optional cleanup failed.
        End Try
    End Sub
End Class
