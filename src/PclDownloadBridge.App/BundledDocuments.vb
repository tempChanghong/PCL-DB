Imports System.IO
Imports System.Security.Cryptography
Imports System.Text
Imports PclDownloadBridge.Core

Friend NotInheritable Class BundledDocuments
    Friend Shared ReadOnly Names As String() = {"LICENCE", "README.md", "SOURCE_REFERENCES.md", "THIRD_PARTY_NOTICES.md", "VALIDATION.md", "PATCH_MECHANISMS.md", "VS2026_DEBUG.md"}

    Friend Shared Function Read(name As String) As String
        If Not Names.Contains(name, StringComparer.Ordinal) Then Throw New ArgumentException("未知随附文档：" & name)
        Using source = GetType(BundledDocuments).Assembly.GetManifestResourceStream("PclDownloadBridge.Documents." & name)
            If source Is Nothing Then Throw New FileNotFoundException("程序缺少内嵌文档：" & name)
            Using reader As New StreamReader(source, Encoding.UTF8, True)
                Return reader.ReadToEnd()
            End Using
        End Using
    End Function

    Friend Shared Function Materialize(name As String, root As String) As String
        Dim content = Read(name)
        ' The standalone notices action must also expose the full upstream licence.
        If name = "THIRD_PARTY_NOTICES.md" Then content &= vbCrLf & vbCrLf & "----- LICENCE -----" & vbCrLf & Read("LICENCE")
        Dim bytes = New UTF8Encoding(False).GetBytes(content)
        Dim digest As String
        Using hash = SHA256.Create()
            digest = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
        End Using
        Dim path = SafeFiles.UnderRoot(root, digest & "/" & name)
        If File.Exists(path) AndAlso File.ReadAllBytes(path).SequenceEqual(bytes) Then Return path
        Directory.CreateDirectory(IO.Path.GetDirectoryName(path))
        Dim temporary = path & "." & Guid.NewGuid().ToString("N") & ".tmp"
        Try
            File.WriteAllBytes(temporary, bytes)
            Try
                SafeFiles.CommitFile(temporary, path)
            Catch ex As IOException
                ' A concurrent opener may have just published exactly the same content.
                If Not File.Exists(path) OrElse Not File.ReadAllBytes(path).SequenceEqual(bytes) Then Throw
            End Try
            Return path
        Finally
            If File.Exists(temporary) Then File.Delete(temporary)
        End Try
    End Function
End Class
