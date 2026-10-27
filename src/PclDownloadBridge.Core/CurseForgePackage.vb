Imports System.Collections.ObjectModel
Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq

Public NotInheritable Class CurseForgeReference
    Public ReadOnly Property ProjectId As Long
    Public ReadOnly Property FileId As Long
    Public ReadOnly Property Required As Boolean
    Friend Sub New(project As Long, file As Long, required As Boolean)
        ProjectId = project : FileId = file : Me.Required = required
    End Sub
End Class

' Pinned PCL ModModpack: root/one-folder manifest, exact loader versions,
' required/optional file IDs and overrides. Parsing never writes a game directory.
Public NotInheritable Class CurseForgePackage
    Public ReadOnly Property SourcePath As String
    Public ReadOnly Property Sha256 As String
    Public ReadOnly Property Name As String
    Public ReadOnly Property Version As String
    Public ReadOnly Property Minecraft As String
    Public ReadOnly Property LoaderKind As ComponentKind?
    Public ReadOnly Property LoaderVersion As String
    Public ReadOnly Property References As ReadOnlyCollection(Of CurseForgeReference)
    Public ReadOnly Property OverrideFiles As ReadOnlyCollection(Of String)
    Friend ReadOnly BaseFolder As String
    Friend ReadOnly OverridesFolder As String
    Private Sub New(path As String, digest As String, archive As ZipArchive)
        SourcePath = path : Sha256 = digest
        ' Payloads have no fixed byte cap. Installation streams each override
        ' and checks its declared length without buffering the whole entry.
        Dim names As New Dictionary(Of String, ZipArchiveEntry)(StringComparer.OrdinalIgnoreCase)
        If archive.Entries.Count > 20000 Then Throw New InvalidDataException("整合包条目超过 20000 项限制。")
        For Each entry In archive.Entries
            Dim relative = entry.FullName.Replace("\"c, "/"c).TrimEnd("/"c)
            ValidateRelative(relative)
            If names.ContainsKey(relative) Then Throw New InvalidDataException("整合包存在重复或大小写冲突的路径：" & relative)
            If (entry.ExternalAttributes And &HF0000000) = &HA0000000 OrElse (entry.ExternalAttributes And CInt(FileAttributes.ReparsePoint)) <> 0 Then Throw New InvalidDataException("整合包包含链接条目：" & relative)
            names.Add(relative, entry)
        Next
        For Each pair In names
            Dim parent = pair.Key
            Do While parent.Contains("/"c)
                parent = parent.Substring(0, parent.LastIndexOf("/"c))
                If names.ContainsKey(parent) AndAlso Not IsDirectory(names(parent)) Then Throw New InvalidDataException("整合包文件与目录路径冲突：" & parent)
            Loop
        Next
        Dim manifests = names.Keys.Where(Function(key) key = "manifest.json" OrElse (key.EndsWith("/manifest.json", StringComparison.Ordinal) AndAlso key.Count(Function(ch) ch = "/"c) = 1)).ToList()
        Dim manifestName = If(manifests.Contains("manifest.json"), "manifest.json", If(manifests.Count = 1, manifests(0), Nothing))
        If manifestName Is Nothing Then Throw New InvalidDataException("未找到唯一的 CurseForge manifest.json；此入口仅支持 CurseForge ZIP。")
        BaseFolder = manifestName.Substring(0, manifestName.Length - "manifest.json".Length)
        Dim manifest As JObject
        Using input = names(manifestName).Open()
            manifest = JObject.Parse(ReadText(input, names(manifestName).Length, 2 * 1024 * 1024), New JsonLoadSettings With {.DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error})
        End Using
        If CStr(manifest("manifestType")) <> "minecraftModpack" OrElse Number(manifest, "manifestVersion") <> 1 Then Throw New InvalidDataException("不支持的 CurseForge 整合包格式版本。")
        Name = If(TextValue(manifest, "name", False), IO.Path.GetFileNameWithoutExtension(path)).Trim()
        If Name.Length = 0 Then Name = "CurseForge 整合包"
        Version = If(TextValue(manifest, "version", False), "")
        Dim game = TryCast(manifest("minecraft"), JObject)
        If game Is Nothing Then Throw New InvalidDataException("整合包未提供 Minecraft 信息。")
        Minecraft = TextValue(game, "version") : SafeFiles.ValidateInstanceName(Minecraft)
        Dim loaders = TryCast(game("modLoaders"), JArray)
        If loaders Is Nothing OrElse loaders.Count > 1 Then Throw New InvalidDataException("整合包加载器声明无效；目前支持一个 Forge、NeoForge 或 Fabric 加载器。")
        If loaders.Count = 1 Then
            Dim loader = TryCast(loaders(0), JObject)
            If loader Is Nothing Then Throw New InvalidDataException("整合包加载器条目无效。")
            Dim id = TextValue(loader, "id")
            For Each prefix In {"forge-", "neoforge-", "fabric-"}
                If Not id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) Then Continue For
                LoaderKind = If(prefix = "forge-", ComponentKind.Forge, If(prefix = "neoforge-", ComponentKind.NeoForge, ComponentKind.Fabric))
                LoaderVersion = id.Substring(prefix.Length)
            Next
            If Not LoaderKind.HasValue OrElse Not Text.RegularExpressions.Regex.IsMatch(If(LoaderVersion, ""), "\A[0-9][A-Za-z0-9.+_-]{0,100}\z") Then Throw New NotSupportedException("不支持或未固定版本的整合包加载器：" & id)
        End If
        Dim files = TryCast(manifest("files"), JArray)
        If files Is Nothing OrElse files.Count > 10000 Then Throw New InvalidDataException("整合包文件清单无效或超过 10000 项。")
        Dim refs As New List(Of CurseForgeReference), ids As New HashSet(Of Long)
        For Each item In files
            Dim value = TryCast(item, JObject)
            If value Is Nothing Then Throw New InvalidDataException("整合包文件条目无效。")
            Dim project = Number(value, "projectID"), file = Number(value, "fileID")
            If Not ids.Add(file) Then Throw New InvalidDataException("整合包重复声明文件 ID：" & file)
            Dim required = value("required")
            If required IsNot Nothing AndAlso required.Type <> JTokenType.Boolean Then Throw New InvalidDataException("整合包 required 字段无效。")
            refs.Add(New CurseForgeReference(project, file, required Is Nothing OrElse CBool(required)))
        Next
        References = refs.AsReadOnly()
        OverridesFolder = If(TextValue(manifest, "overrides", False), "overrides").TrimEnd("/"c)
        ValidateRelative(OverridesFolder)
        Dim prefixPath = BaseFolder & OverridesFolder & "/"
        OverrideFiles = names.Where(Function(pair) pair.Key.StartsWith(prefixPath, StringComparison.OrdinalIgnoreCase) AndAlso Not IsDirectory(pair.Value)).Select(Function(pair) pair.Key.Substring(prefixPath.Length)).ToList().AsReadOnly()
        For Each relative In OverrideFiles
            ValidateRelative(relative)
        Next
    End Sub
    Public Shared Function Read(path As String, Optional token As CancellationToken = Nothing) As CurseForgePackage
        token.ThrowIfCancellationRequested()
        path = IO.Path.GetFullPath(path)
        Using input As New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
            Dim digest = HashArchive(input, token)
            Using archive As New ZipArchive(input, ZipArchiveMode.Read)
                Return New CurseForgePackage(path, digest, archive)
            End Using
        End Using
    End Function
    Friend Shared Function HashArchive(input As Stream, token As CancellationToken) As String
        Using sha = Security.Cryptography.SHA256.Create()
            Dim buffer(65535) As Byte
            Do
                token.ThrowIfCancellationRequested()
                Dim count = input.Read(buffer, 0, buffer.Length)
                If count = 0 Then Exit Do
                sha.TransformBlock(buffer, 0, count, buffer, 0)
            Loop
            sha.TransformFinalBlock(New Byte() {}, 0, 0) : input.Position = 0
            Return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant()
        End Using
    End Function
    Friend Shared Function IsDirectory(entry As ZipArchiveEntry) As Boolean
        Return entry.FullName.EndsWith("/", StringComparison.Ordinal) OrElse entry.FullName.EndsWith("\", StringComparison.Ordinal)
    End Function
    Friend Shared Sub ValidateRelative(value As String)
        If String.IsNullOrWhiteSpace(value) OrElse value.Length > 32767 OrElse value.StartsWith("/", StringComparison.Ordinal) OrElse value.Contains("\"c) Then Throw New InvalidDataException("整合包路径无效：" & value)
        For Each segment In value.Split("/"c)
            SafeFiles.ValidatePathSegment(segment)
        Next
    End Sub
    Friend Shared Function Number(value As JObject, field As String, Optional minimum As Long = 1) As Long
        Dim item = value(field)
        If item Is Nothing OrElse item.Type <> JTokenType.Integer Then Throw New InvalidDataException("CurseForge " & field & " 字段无效。")
        Dim result = CLng(item)
        If result < minimum Then Throw New InvalidDataException("CurseForge " & field & " 超出范围。")
        Return result
    End Function
    Friend Shared Function TextValue(value As JObject, field As String, Optional required As Boolean = True) As String
        Dim item = value(field)
        If item Is Nothing OrElse item.Type = JTokenType.Null Then
            If Not required Then Return Nothing
        ElseIf item.Type = JTokenType.String AndAlso (Not required OrElse Not String.IsNullOrWhiteSpace(CStr(item))) Then
            Return CStr(item)
        End If
        Throw New InvalidDataException("CurseForge " & field & " 文本字段无效。")
    End Function
    Friend Shared Function ReadText(input As Stream, length As Long, maximum As Integer) As String
        If length > maximum Then Throw New InvalidDataException("整合包配置文件过大。")
        Using bytes As New MemoryStream()
            Dim buffer(8191) As Byte
            Do
                Dim count = input.Read(buffer, 0, CInt(Math.Min(buffer.Length, maximum - bytes.Length + 1)))
                If count = 0 Then Exit Do
                If bytes.Length + count > maximum Then Throw New InvalidDataException("整合包配置文件过大。")
                bytes.Write(buffer, 0, count)
            Loop
            If bytes.Length <> length Then Throw New InvalidDataException("整合包配置长度不匹配。")
            bytes.Position = 0
            Using reader As New StreamReader(bytes, Encoding.UTF8, True)
                Return reader.ReadToEnd()
            End Using
        End Using
    End Function
End Class
