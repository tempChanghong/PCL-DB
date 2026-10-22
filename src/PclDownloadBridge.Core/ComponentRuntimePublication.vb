Imports System.IO
Imports System.Security.Cryptography
Imports System.Threading

' PCL McInstallLoadersGet migrates checked runtime libraries out of its private
' component tree. Keep that boundary, but never identify a hashless shared file
' by length alone. Local digests describe this payload, not upstream metadata.
Friend NotInheritable Class ComponentRuntimePublication
    Implements IDisposable
    Private ReadOnly _root As String
    Private ReadOnly _held As New List(Of FileStream)
    Private ReadOnly _frozen As New List(Of DownloadFile)
    Friend Sub New(root As String)
        _root = root
    End Sub
    Friend Shared Function HasDigest(item As DownloadFile) As Boolean
        Return Not String.IsNullOrWhiteSpace(item.Sha1) OrElse Not String.IsNullOrWhiteSpace(item.Sha512) OrElse
            Not String.IsNullOrWhiteSpace(item.Md5) OrElse Not String.IsNullOrWhiteSpace(item.S3SinglePartETag)
    End Function
    Friend ReadOnly Property FrozenFiles As List(Of DownloadFile)
        Get
            Return _frozen.ToList()
        End Get
    End Property
    Friend Sub Publish(work As String, files As IEnumerable(Of DownloadFile), token As CancellationToken)
        For Each item In files
            token.ThrowIfCancellationRequested()
            Dim source = SafeFiles.UnderRoot(work, item.RelativePath), destination = SafeFiles.UnderRoot(_root, item.RelativePath)
            Dim hashless = Not HasDigest(item)
            Using input As New FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read)
                If Not SafeFiles.Matches(source, item) Then Throw New InvalidDataException("组件支持库校验失败：" & item.RelativePath)
                Dim expected = If(hashless, Freeze(input, item, token), item)
                token.ThrowIfCancellationRequested()
                Dim existed = File.Exists(destination)
                If existed Then
                    Dim held As New FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read)
                    Try
                        If SafeFiles.Matches(destination, expected) Then
                            token.ThrowIfCancellationRequested()
                            _held.Add(held) : held = Nothing
                            If hashless Then _frozen.Add(expected)
                            Continue For
                        End If
                        If hashless Then Throw New IOException("已有共享支持库内容不同，不会覆盖无上游摘要的文件：" & item.RelativePath)
                    Finally
                        held?.Dispose()
                    End Try
                End If
                Directory.CreateDirectory(Path.GetDirectoryName(destination))
                Dim temporary = destination & ".component-" & Guid.NewGuid().ToString("N")
                Try
                    input.Position = 0
                    Using output As New FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                        Dim buffer(65535) As Byte
                        Do
                            token.ThrowIfCancellationRequested()
                            Dim count = input.Read(buffer, 0, buffer.Length)
                            If count = 0 Then Exit Do
                            output.Write(buffer, 0, count)
                        Loop
                    End Using
                    If Not SafeFiles.Matches(temporary, expected) Then Throw New InvalidDataException("组件支持库副本校验失败：" & item.RelativePath)
                    token.ThrowIfCancellationRequested()
                    ' An absent target must stay create-only, even if another writer
                    ' creates it between the check above and this same-volume rename.
                    If existed Then
                        SafeFiles.CommitFile(temporary, destination)
                    Else
                        File.Move(temporary, destination)
                    End If
                    Dim held As New FileStream(SafeFiles.UnderRoot(_root, item.RelativePath), FileMode.Open, FileAccess.Read, FileShare.Read)
                    Try
                        If Not SafeFiles.Matches(destination, expected) Then Throw New IOException("组件支持库在发布期间发生变化：" & item.RelativePath)
                        token.ThrowIfCancellationRequested()
                        _held.Add(held) : held = Nothing
                        If hashless Then _frozen.Add(expected)
                    Finally
                        held?.Dispose()
                    End Try
                Finally
                    If File.Exists(temporary) Then File.Delete(temporary)
                End Try
            End Using
        Next
    End Sub
    Private Shared Function Freeze(input As FileStream, item As DownloadFile, token As CancellationToken) As DownloadFile
        input.Position = 0
        Using hash = SHA512.Create()
            Dim buffer(65535) As Byte
            Do
                token.ThrowIfCancellationRequested()
                Dim count = input.Read(buffer, 0, buffer.Length)
                If count = 0 Then Exit Do
                hash.TransformBlock(buffer, 0, count, buffer, 0)
            Loop
            hash.TransformFinalBlock(New Byte() {}, 0, 0)
            token.ThrowIfCancellationRequested()
            Return New DownloadFile With {.RelativePath = item.RelativePath, .Size = input.Length,
                .Sha512 = BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant(), .Category = "library"}
        End Using
    End Function
    Public Sub Dispose() Implements IDisposable.Dispose
        For Each held In _held
            held.Dispose()
        Next
        _held.Clear()
    End Sub

    ' Work was removed after Apply. Only its frozen content expectations survive;
    ' reacquiring a lease must compare them, never bless current shared bytes anew.
    Friend NotInheritable Class PublishedLease
        Implements IDisposable
        Private ReadOnly _root As String
        Private ReadOnly _files As List(Of DownloadFile)
        Private ReadOnly _held As New List(Of FileStream)
        Friend Sub New(root As String, files As IEnumerable(Of DownloadFile), token As CancellationToken)
            _root = root : _files = files.ToList()
            Try
                For Each item In _files
                    token.ThrowIfCancellationRequested()
                    Dim path = SafeFiles.UnderRoot(root, item.RelativePath)
                    _held.Add(New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                Next
                Verify(token)
            Catch
                Dispose() : Throw
            End Try
        End Sub
        Friend Sub Verify(token As CancellationToken)
            For Each item In _files
                token.ThrowIfCancellationRequested()
                If Not SafeFiles.Matches(SafeFiles.UnderRoot(_root, item.RelativePath), item) Then Throw New IOException("组件支持库在实例发布前发生变化：" & item.RelativePath)
                token.ThrowIfCancellationRequested()
            Next
        End Sub
        Public Sub Dispose() Implements IDisposable.Dispose
            For Each held In _held
                held.Dispose()
            Next
            _held.Clear()
        End Sub
    End Class
End Class
