Imports System.IO
Imports System.Security.Cryptography
Imports Newtonsoft.Json.Linq

Public Class GameFolder
    Public Property Name As String
    Public Property Location As String
    Public Property Origin As String
    Public Property IsSelected As Boolean
    Public ReadOnly Property VersionCount As Integer
        Get
            Dim versions = Path.Combine(Location, "versions")
            If Not Directory.Exists(versions) Then Return 0
            Try
                Return Directory.EnumerateDirectories(versions).Count(Function(p) File.Exists(Path.Combine(p, Path.GetFileName(p) & ".json")))
            Catch ex As IOException
                Return 0
            Catch ex As UnauthorizedAccessException
                Return 0
            End Try
        End Get
    End Property
    Public Overrides Function ToString() As String
        Return Name & " · " & Location
    End Function
End Class

Public Class VersionEntry
    Public Property Id As String
    Public Property Kind As String
    Public Property Url As String
    Public Property Sha1 As String
    Public Property Released As DateTimeOffset
    ' Original UVMC catalogue ID. Id is the verified canonical Minecraft metadata ID.
    Public Property SupplementalId As String
    Public Function Copy() As VersionEntry
        ' Every stored field is a value or immutable string. Keep the identity,
        ' origin and checksum bound to this request across asynchronous stages.
        Return DirectCast(MemberwiseClone(), VersionEntry)
    End Function
    Public ReadOnly Property IsSupplemental As Boolean
        Get
            Return SupplementalId IsNot Nothing
        End Get
    End Property
    Public ReadOnly Property Subtitle As String
        Get
            Return If(IsSupplemental, SupplementalVersions.Label(Me) & " · 补充来源 UVMC", If(Kind = "release", "正式版", If(Kind = "snapshot", "快照版", "历史版本"))) & " · " & Released.ToString("yyyy/MM/dd")
        End Get
    End Property
End Class

Public Class DownloadFile
    Public Property RelativePath As String
    Public Property Url As String
    Public Property Size As Long = -1
    Public Property AlternateUrls As String()
    ' Modrinth fileSize is a progress estimate, never a hard integrity check.
    Public Property EstimatedSize As Long = -1
    Public Property Sha1 As String
    Public Property Sha512 As String
    Public Property Md5 As String
    ' Only the one-part multipart S3 ETag form (MD5 of the binary MD5, then -1).
    ' Ordinary ETags and unknown multipart boundaries are never checksums.
    Public Property S3SinglePartETag As String
    Public Property Category As String = "meta"
End Class

Public Class TransferProgress
    Public Property PhaseFractions As Dictionary(Of String, Double)
    Public Property PhaseTotal As Integer = -1
    Public Property PhaseCompleted As Integer
    Public Property Phase As String = "files"
    Public Property StageFraction As Double
    Public Property CurrentFile As String
    Public Property Source As String
    Public Property Retries As Integer
    Public Property KnownBytes As Long
    Public Property VerifiedBytes As Long
    Public Property ActiveConnections As Integer
    Public Property DynamicSplits As Integer
    Public Property Stage As String
    Public Property Detail As String
    Public Property Completed As Integer
    Public Property Total As Integer
    Public Property NetworkBytes As Long
    Public Property BytesPerSecond As Double
    Public Property Reused As Integer
    Public Property Fraction As Double
End Class

Public Class InstallResult
    Public Property DynamicSplits As Integer
    Public Property InstancePath As String
    Public Property FileCount As Integer
    Public Property Reused As Integer
    Public Property NetworkBytes As Long
End Class

Public Module SafeFiles
    Public Function UnderRoot(root As String, relative As String) As String
        If String.IsNullOrWhiteSpace(relative) OrElse Path.IsPathRooted(relative) OrElse relative.Contains(":"c) Then Throw New InvalidDataException("文件路径无效：" & relative)
        Dim basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) & Path.DirectorySeparatorChar
        Dim target = Path.GetFullPath(Path.Combine(basePath, relative.Replace("/"c, Path.DirectorySeparatorChar)))
        If Not target.StartsWith(basePath, StringComparison.OrdinalIgnoreCase) Then Throw New InvalidDataException("文件路径超出游戏目录：" & relative)
        ' Do not follow junctions/symlinks out of the selected game directory.
        Dim current = Path.GetDirectoryName(target)
        Do While current IsNot Nothing AndAlso current.Length >= basePath.TrimEnd(Path.DirectorySeparatorChar).Length
            If Directory.Exists(current) AndAlso (File.GetAttributes(current) And FileAttributes.ReparsePoint) <> 0 Then Throw New IOException("目录包含链接，请选择真实游戏目录：" & current)
            current = Path.GetDirectoryName(current)
        Loop
        If File.Exists(target) AndAlso (File.GetAttributes(target) And FileAttributes.ReparsePoint) <> 0 Then Throw New IOException("目标文件是链接：" & target)
        Return target
    End Function

    Public Sub ValidateInstanceName(value As String)
        If String.IsNullOrWhiteSpace(value) OrElse value <> value.Trim() OrElse value.EndsWith(".", StringComparison.Ordinal) OrElse value.Length > 100 OrElse value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 OrElse value = "." OrElse value = ".." Then Throw New ArgumentException("实例名称无效，请使用普通文件夹名称。")
        If System.Text.RegularExpressions.Regex.IsMatch(value, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase) Then Throw New ArgumentException("实例名称是 Windows 保留名称。")
    End Sub

    Public Function Matches(path As String, item As DownloadFile) As Boolean
        If Not File.Exists(path) Then Return False
        Dim info As New FileInfo(path)
        If item.Size >= 0 AndAlso info.Length <> item.Size Then Return False
        ' An explicitly declared or hashed empty file is legitimate; unknown empty
        ' downloads still must not be mistaken for completed reusable files.
        If info.Length = 0 AndAlso item.Size <> 0 AndAlso String.IsNullOrWhiteSpace(item.Sha1) AndAlso String.IsNullOrWhiteSpace(item.Sha512) AndAlso String.IsNullOrWhiteSpace(item.Md5) Then Return False
        If Not String.IsNullOrWhiteSpace(item.Sha512) Then
            Using stream = File.OpenRead(path), sha = SHA512.Create()
                If Not String.Equals(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""), item.Sha512, StringComparison.OrdinalIgnoreCase) Then Return False
            End Using
        End If
        If Not String.IsNullOrWhiteSpace(item.Sha1) Then
            Using stream = File.OpenRead(path), sha = SHA1.Create()
                If Not String.Equals(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""), item.Sha1, StringComparison.OrdinalIgnoreCase) Then Return False
            End Using
        End If
        If Not String.IsNullOrWhiteSpace(item.Md5) Then
            Using stream = File.OpenRead(path), hash = Security.Cryptography.MD5.Create()
                If Not String.Equals(BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", ""), item.Md5, StringComparison.OrdinalIgnoreCase) Then Return False
            End Using
        End If
        If Not String.IsNullOrWhiteSpace(item.S3SinglePartETag) Then
            If Not Text.RegularExpressions.Regex.IsMatch(item.S3SinglePartETag, "\A[0-9a-fA-F]{32}-1\z") Then Return False
            Using stream = File.OpenRead(path), hash = Security.Cryptography.MD5.Create()
                Dim digest = hash.ComputeHash(hash.ComputeHash(stream))
                If Not String.Equals(BitConverter.ToString(digest).Replace("-", "") & "-1", item.S3SinglePartETag, StringComparison.OrdinalIgnoreCase) Then Return False
            End Using
        End If
        Return True
    End Function

    Public Sub CommitFile(temp As String, target As String)
        If File.Exists(target) Then
            File.Replace(temp, target, Nothing)
        Else
            File.Move(temp, target)
        End If
    End Sub
End Module
