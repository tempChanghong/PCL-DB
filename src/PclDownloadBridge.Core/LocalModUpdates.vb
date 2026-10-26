Imports System.IO
Imports System.Security.Cryptography
Imports System.Threading

Public NotInheritable Class LocalModFingerprint
    Public ReadOnly Entry As LocalModEntry
    Public ReadOnly Sha1 As String, Md5 As String, CurseForge As UInteger
    Private Sub New(entry As LocalModEntry, sha1 As String, md5 As String, fingerprint As UInteger)
        Me.Entry = entry : Me.Sha1 = sha1 : Me.Md5 = md5 : CurseForge = fingerprint
    End Sub
    Public Shared Function Read(instance As InstalledInstance, entries As IEnumerable(Of LocalModEntry), token As CancellationToken) As List(Of LocalModFingerprint)
        Dim directory = LocalModCatalog.ModsDirectory(instance, token), result As New List(Of LocalModFingerprint)
        For Each modFile In entries
            token.ThrowIfCancellationRequested()
            If Not modFile.IsArchiveValid OrElse Not modFile.CanToggle Then Continue For
            If Not String.Equals(Path.GetFullPath(Path.Combine(directory, modFile.FileName)), modFile.FilePath, StringComparison.OrdinalIgnoreCase) Then Throw New InvalidDataException("Mod 不属于当前实例。")
            Using held = LocalModActions.NativeLease.Open(modFile.FilePath, False), sha1 = System.Security.Cryptography.SHA1.Create(), md5 = System.Security.Cryptography.MD5.Create(), sha256 = System.Security.Cryptography.SHA256.Create()
                Dim stream = held.Stream, buffer(65535) As Byte, filtered As Long = 0
                If stream.Length <> modFile.Length Then Throw New InvalidDataException("Mod 文件已变化，请刷新列表。")
                Do
                    token.ThrowIfCancellationRequested()
                    Dim count = stream.Read(buffer, 0, buffer.Length)
                    If count = 0 Then Exit Do
                    sha1.TransformBlock(buffer, 0, count, buffer, 0) : md5.TransformBlock(buffer, 0, count, buffer, 0) : sha256.TransformBlock(buffer, 0, count, buffer, 0)
                    For index = 0 To count - 1
                        If Not White(buffer(index)) Then filtered += 1
                    Next
                Loop
                sha1.TransformFinalBlock({}, 0, 0) : md5.TransformFinalBlock({}, 0, 0) : sha256.TransformFinalBlock({}, 0, 0)
                If Not String.Equals(Hex(sha256.Hash), modFile.Sha256, StringComparison.OrdinalIgnoreCase) Then Throw New InvalidDataException("Mod 内容已变化，请刷新列表。")
                stream.Position = 0
                Dim hash As UInteger = 1UI Xor CUInt(filtered And &HFFFFFFFFL), word As UInteger = 0, shift = 0
                Do
                    token.ThrowIfCancellationRequested()
                    Dim count = stream.Read(buffer, 0, buffer.Length)
                    If count = 0 Then Exit Do
                    For index = 0 To count - 1
                        Dim value = buffer(index)
                        If White(value) Then Continue For
                        word = word Or (CUInt(value) << shift) : shift += 8
                        If shift <> 32 Then Continue For
                        word = Multiply(word) : word = word Xor (word >> 24) : word = Multiply(word)
                        hash = Multiply(hash) Xor word : word = 0 : shift = 0
                    Next
                Loop
                If shift > 0 Then hash = Multiply(hash Xor word)
                hash = hash Xor (hash >> 13) : hash = Multiply(hash) : hash = hash Xor (hash >> 15)
                result.Add(New LocalModFingerprint(modFile, Hex(sha1.Hash), Hex(md5.Hash), hash))
            End Using
        Next
        InstalledInstanceCatalog.EnsureUnchanged(instance, token)
        Return result
    End Function
    Private Shared Function White(value As Byte) As Boolean
        Return value = 9 OrElse value = 10 OrElse value = 13 OrElse value = 32
    End Function
    Private Shared Function Multiply(value As UInteger) As UInteger
        Return CUInt((CULng(value) * &H5BD1E995UL) And &HFFFFFFFFUL)
    End Function
    Private Shared Function Hex(value As Byte()) As String
        Return BitConverter.ToString(value).Replace("-", "").ToLowerInvariant()
    End Function
    Public Function Matches(release As ModpackRelease) As Boolean
        Return release IsNot Nothing AndAlso release.Size = Entry.Length AndAlso (release.Sha1 IsNot Nothing OrElse release.Md5 IsNot Nothing) AndAlso
            (release.Sha1 Is Nothing OrElse String.Equals(release.Sha1, Sha1, StringComparison.OrdinalIgnoreCase)) AndAlso
            (release.Md5 Is Nothing OrElse String.Equals(release.Md5, Md5, StringComparison.OrdinalIgnoreCase))
    End Function
End Class

Public NotInheritable Class LocalModUpdate
    Public ReadOnly Entry As LocalModEntry, Project As ModpackProject
    Private ReadOnly _current As ModpackRelease, _release As ModpackRelease
    Public Sub New(entry As LocalModEntry, project As ModpackProject, current As ModpackRelease, release As ModpackRelease)
        Me.Entry = entry : Me.Project = project : _current = current.Copy() : _release = If(release Is Nothing, Nothing, release.Copy())
    End Sub
    Public ReadOnly Property Current As ModpackRelease
        Get
            Return _current.Copy()
        End Get
    End Property
    Public ReadOnly Property Release As ModpackRelease
        Get
            Return If(_release Is Nothing, Nothing, _release.Copy())
        End Get
    End Property
    Public ReadOnly Property HasUpdate As Boolean
        Get
            Return _release IsNot Nothing
        End Get
    End Property
    Public Shared Function Compatible(instance As InstalledInstance, current As ModpackRelease, release As ModpackRelease) As Boolean
        If release Is Nothing OrElse current Is Nothing OrElse current.Platform <> release.Platform OrElse current.ProjectId <> release.ProjectId OrElse current.Id = release.Id OrElse
            current.Kind <> CommunityResourceKind.Mod OrElse release.Kind <> CommunityResourceKind.Mod OrElse release.Published <= current.Published OrElse
            CommunityResourceInstallation.CompatibilityProblem(release, instance) IsNot Nothing Then Return False
        Dim before = current.Loaders.Select(Function(value) value.ToLowerInvariant()).Distinct().OrderBy(Function(value) value)
        Dim after = release.Loaders.Select(Function(value) value.ToLowerInvariant()).Distinct().OrderBy(Function(value) value)
        If Not before.SequenceEqual(after) Then Return False
        If release.Size = current.Size AndAlso ((release.Sha1 IsNot Nothing AndAlso String.Equals(release.Sha1, current.Sha1, StringComparison.OrdinalIgnoreCase)) OrElse
            (release.Md5 IsNot Nothing AndAlso String.Equals(release.Md5, current.Md5, StringComparison.OrdinalIgnoreCase))) Then Return False
        Return True
    End Function
End Class

Public NotInheritable Class LocalModUpdateCheckResult
    Public ReadOnly Matches As IReadOnlyList(Of LocalModUpdate), Errors As IReadOnlyList(Of String)
    Public Sub New(matches As IEnumerable(Of LocalModUpdate), Optional errors As IEnumerable(Of String) = Nothing)
        Me.Matches = matches.ToList().AsReadOnly() : Me.Errors = If(errors, Enumerable.Empty(Of String)()).ToList().AsReadOnly()
    End Sub
End Class
