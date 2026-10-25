Imports System.IO
Imports System.Threading
Imports System.Security.Cryptography

' Downloading a resource never invokes a modpack installer or changes game options.
' Publication uses the same read/rename leases as local Mods, without overwrite.
Public NotInheritable Class CommunityResourceDownload
    Private Sub New()
    End Sub
    Public Shared Async Function SaveAsync(release As ModpackRelease, destination As String, cache As String, policy As SourcePolicy, progress As IProgress(Of TransferProgress), token As CancellationToken, Optional connections As Integer = 16, Optional limit As Long = 0, Optional instance As InstalledInstance = Nothing) As Task
        release = release.Copy()
        token.ThrowIfCancellationRequested()
        ValidateDestination(release, destination)
        Dim acquisition As New InlineProgress(Sub(value)
                                                 value.Phase = "archive" : value.Stage = "下载并校验文件"
                                                 value.StageFraction = If(value.Completed >= value.Total AndAlso value.Total > 0, 1, Math.Min(0.99, value.NetworkBytes / CDbl(release.Size)))
                                                 progress?.Report(value)
                                             End Sub)
        Dim source = Await ModpackArchiveInstaller.AcquireAsync(release, cache, policy, acquisition, token, connections, limit).ConfigureAwait(False)
        progress?.Report(New TransferProgress With {.Phase = "commit", .Stage = "保存文件", .StageFraction = 0})
        Await Task.Run(Sub() Publish(release, source, destination, token, instance), token).ConfigureAwait(False)
        progress?.Report(New TransferProgress With {.Phase = "commit", .Stage = "文件已保存", .StageFraction = 1})
    End Function
    Private Shared Sub ValidateDestination(release As ModpackRelease, destination As String)
        If release.Kind = CommunityResourceKind.Modpack OrElse release.Kind = CommunityResourceKind.Any OrElse Not CommunityResources.AcceptsFile(release.Kind, release.FileName) OrElse Not CommunityResources.AcceptsFile(release.Kind, Path.GetFileName(destination)) Then Throw New InvalidDataException("保存文件的扩展名与资源类型不符。")
        CommunityResources.ValidateFileName(release.Kind, release.FileName)
        CommunityResources.ValidateFileName(release.Kind, Path.GetFileName(destination))
        If release.Size <= 0 OrElse (release.Sha1 Is Nothing AndAlso release.Sha512 Is Nothing AndAlso release.Md5 Is Nothing) Then Throw New InvalidDataException("资源缺少有效大小或校验摘要。")
        Dim full = Path.GetFullPath(destination)
        If Path.GetFileName(full) <> Path.GetFileName(destination) OrElse Not Directory.Exists(Path.GetDirectoryName(full)) Then Throw New DirectoryNotFoundException("保存文件夹不存在。")
        If File.Exists(full) OrElse Directory.Exists(full) Then Throw New IOException("目标已存在，请选择其他文件名；已有文件已保留。")
    End Sub
    Friend Shared Sub Publish(release As ModpackRelease, source As String, destination As String, token As CancellationToken, Optional instance As InstalledInstance = Nothing, Optional beforePublish As Action = Nothing)
        token.ThrowIfCancellationRequested()
        ValidateDestination(release, destination)
        destination = Path.GetFullPath(destination)
        Dim directory = Path.GetDirectoryName(destination), leases As New List(Of IDisposable)
        Dim stage As LocalModActions.NativeLease = Nothing, published = False
        Try
            If release.Kind = CommunityResourceKind.Mod AndAlso Path.GetFileName(directory).Equals("mods", StringComparison.OrdinalIgnoreCase) Then
                leases.Add(New LocalModActions.OperationGate(instance, directory, token))
                LocalModActions.EnsureAliases(directory, Path.GetFileName(destination), Nothing)
            Else
                Dim current = directory
                While current IsNot Nothing
                    Dim identity As String = Nothing
                    leases.Add(LocalModActions.NativeLease.OpenDirectory(current, identity))
                    current = Path.GetDirectoryName(current)
                End While
            End If
            Using input = LocalModActions.NativeLease.Open(source, False)
                ' Verify under the same source lease that supplies the published bytes.
                VerifySource(input.Stream, release, token)
                stage = LocalModActions.NativeLease.Create(Path.Combine(directory, ".pcldb-resource-" & Guid.NewGuid().ToString("N") & ".tmp"))
                Dim buffer(65535) As Byte
                Do
                    token.ThrowIfCancellationRequested()
                    Dim count = input.Stream.Read(buffer, 0, buffer.Length)
                    If count = 0 Then Exit Do
                    stage.Stream.Write(buffer, 0, count)
                Loop
                stage.Stream.Flush(True)
                beforePublish?.Invoke()
                If instance IsNot Nothing Then InstalledInstanceCatalog.EnsureUnchanged(instance, token)
                token.ThrowIfCancellationRequested()
                stage.Rename(destination)
                published = True
            End Using
        Finally
            Try
                If stage IsNot Nothing Then
                    Try
                        If Not published Then stage.DeleteOnClose()
                    Finally
                        stage.Dispose()
                    End Try
                End If
            Finally
                For Each handle In leases.AsEnumerable().Reverse() : handle.Dispose() : Next
            End Try
        End Try
    End Sub
    Private Shared Sub VerifySource(input As Stream, release As ModpackRelease, token As CancellationToken)
        If input.Length <> release.Size Then Throw New InvalidDataException("下载缓存已改变，请重新下载。")
        Dim hashes As New List(Of Tuple(Of HashAlgorithm, String))
        Try
            If release.Sha1 IsNot Nothing Then hashes.Add(Tuple.Create(Of HashAlgorithm, String)(SHA1.Create(), release.Sha1))
            If release.Sha512 IsNot Nothing Then hashes.Add(Tuple.Create(Of HashAlgorithm, String)(SHA512.Create(), release.Sha512))
            If release.Md5 IsNot Nothing Then hashes.Add(Tuple.Create(Of HashAlgorithm, String)(MD5.Create(), release.Md5))
            Dim buffer(65535) As Byte
            Do
                token.ThrowIfCancellationRequested()
                Dim count = input.Read(buffer, 0, buffer.Length)
                If count = 0 Then Exit Do
                For Each hash In hashes : hash.Item1.TransformBlock(buffer, 0, count, buffer, 0) : Next
            Loop
            For Each hash In hashes
                hash.Item1.TransformFinalBlock(New Byte() {}, 0, 0)
                If Not String.Equals(BitConverter.ToString(hash.Item1.Hash).Replace("-", ""), hash.Item2, StringComparison.OrdinalIgnoreCase) Then Throw New InvalidDataException("下载缓存已改变，请重新下载。")
            Next
            input.Position = 0
        Finally
            For Each hash In hashes : hash.Item1.Dispose() : Next
        End Try
    End Sub
End Class
