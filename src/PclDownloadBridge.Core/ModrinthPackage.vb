Imports System.Collections.ObjectModel
Imports System.IO
Imports System.IO.Compression
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Newtonsoft.Json.Linq

Public NotInheritable Class ModrinthReference
    Public ReadOnly Property Path As String
    Public ReadOnly Property Sha1 As String
    Public ReadOnly Property Sha512 As String
    Public ReadOnly Property EstimatedSize As Long
    Public ReadOnly Property Client As String
    Public ReadOnly Property Downloads As ReadOnlyCollection(Of String)
    Friend Sub New(value As JObject)
        Path = Text(value, "path") : CurseForgePackage.ValidateRelative(Path)
        Dim hashes = TryCast(value("hashes"), JObject)
        If hashes Is Nothing Then Throw New InvalidDataException("Modrinth 文件缺少 hashes：" & Path)
        Sha1 = Text(hashes, "sha1") : Sha512 = Text(hashes, "sha512")
        If Not Regex.IsMatch(Sha1, "\A[0-9a-fA-F]{40}\z") OrElse Not Regex.IsMatch(Sha512, "\A[0-9a-fA-F]{128}\z") Then Throw New InvalidDataException("Modrinth 文件的 SHA-1 / SHA-512 无效：" & Path)
        EstimatedSize = IntegerValue(value, "fileSize", 0)
        If EstimatedSize > 16L * 1024 * 1024 * 1024 Then Throw New InvalidDataException("Modrinth 单文件预计大小超过限制：" & Path)
        Client = "required"
        Dim environment = value("env")
        If environment IsNot Nothing Then
            If Not TypeOf environment Is JObject Then Throw New InvalidDataException("Modrinth env 字段无效。")
            For Each side In {"client", "server"}
                Dim mode = Text(DirectCast(environment, JObject), side)
                If Not {"required", "optional", "unsupported"}.Contains(mode, StringComparer.Ordinal) Then Throw New InvalidDataException("Modrinth 环境声明无效：" & side)
                If side = "client" Then Client = mode
            Next
        End If
        Dim urls = TryCast(value("downloads"), JArray)
        If urls Is Nothing OrElse urls.Count = 0 OrElse urls.Count > 16 Then Throw New InvalidDataException("Modrinth 下载地址数量无效：" & Path)
        Dim addresses As New List(Of String)
        For Each url In urls
            If url.Type <> JTokenType.String Then Throw New InvalidDataException("Modrinth 下载地址必须是文本。")
            ModrinthDownloadPolicy.ValidateUrl(CStr(url), False, False)
            If Not addresses.Contains(CStr(url), StringComparer.Ordinal) Then addresses.Add(CStr(url))
        Next
        Downloads = addresses.AsReadOnly()
    End Sub
    Friend Function ToDownload() As DownloadFile
        Return New DownloadFile With {.RelativePath = Path, .Url = Downloads(0), .AlternateUrls = Downloads.Skip(1).ToArray(), .EstimatedSize = EstimatedSize,
            .Sha1 = Sha1, .Sha512 = Sha512, .Category = "modrinth"}
    End Function
    Friend Shared Function Text(value As JObject, field As String, Optional required As Boolean = True) As String
        Dim item = value(field)
        If item Is Nothing AndAlso Not required Then Return Nothing
        If item Is Nothing OrElse item.Type <> JTokenType.String OrElse (required AndAlso String.IsNullOrWhiteSpace(CStr(item))) Then Throw New InvalidDataException("Modrinth " & field & " 文本字段无效。")
        If CStr(item).Length > 4096 Then Throw New InvalidDataException("Modrinth " & field & " 文本字段过长。")
        Return CStr(item)
    End Function
    Friend Shared Function IntegerValue(value As JObject, field As String, minimum As Long) As Long
        Dim item = value(field)
        If item Is Nothing OrElse item.Type <> JTokenType.Integer Then Throw New InvalidDataException("Modrinth " & field & " 整数字段无效。")
        Dim result = CLng(item)
        If result < minimum Then Throw New InvalidDataException("Modrinth " & field & " 超出范围。")
        Return result
    End Function
End Class

' PCL ModModpack.InstallPackModrinth at 5ef81e4: dependency IDs, client env,
' downloads[], overrides then client-overrides, single wrapping folder support.
Public NotInheritable Class ModrinthPackage
    Public ReadOnly Property SourcePath As String
    Public ReadOnly Property Sha256 As String
    Public ReadOnly Property Name As String
    Public ReadOnly Property Version As String
    Public ReadOnly Property Summary As String
    Public ReadOnly Property Minecraft As String
    Public ReadOnly Property LoaderKind As ComponentKind?
    Public ReadOnly Property LoaderVersion As String
    Public ReadOnly Property References As ReadOnlyCollection(Of ModrinthReference)
    Public ReadOnly Property OverrideFiles As ReadOnlyCollection(Of String)
    Friend ReadOnly OverrideSources As ReadOnlyDictionary(Of String, String)
    Private Sub New(path As String, digest As String, archive As ZipArchive)
        SourcePath = path : Sha256 = digest
        Dim names As New Dictionary(Of String, ZipArchiveEntry)(StringComparer.OrdinalIgnoreCase), total As Long
        If archive.Entries.Count > 20000 Then Throw New InvalidDataException("整合包条目超过 20000 项限制。")
        For Each entry In archive.Entries
            Dim relative = entry.FullName.Replace("\"c, "/"c).TrimEnd("/"c)
            CurseForgePackage.ValidateRelative(relative)
            If names.ContainsKey(relative) Then Throw New InvalidDataException("整合包存在重复或大小写冲突的路径：" & relative)
            If (entry.ExternalAttributes And &HF0000000) = &HA0000000 OrElse (entry.ExternalAttributes And CInt(FileAttributes.ReparsePoint)) <> 0 Then Throw New InvalidDataException("整合包包含链接条目：" & relative)
            names.Add(relative, entry) : total += entry.Length
            If entry.Length > 128L * 1024 * 1024 OrElse total > 2L * 1024 * 1024 * 1024 Then Throw New InvalidDataException("整合包解压内容超过大小限制。")
        Next
        Dim candidates = If(names.Keys.Contains("modrinth.index.json", StringComparer.Ordinal), {"modrinth.index.json"}, names.Keys.Where(Function(key) key.EndsWith("/modrinth.index.json", StringComparison.Ordinal) AndAlso key.Count(Function(ch) ch = "/"c) = 1).ToArray())
        ' Match PCL's root precedence, then require a unique wrapping folder.
        If candidates.Length <> 1 Then Throw New InvalidDataException("未找到唯一的 Modrinth modrinth.index.json。")
        Dim index = candidates(0), prefix = index.Substring(0, index.Length - "modrinth.index.json".Length), manifest As JObject
        Using input = names(index).Open()
            manifest = JObject.Parse(CurseForgePackage.ReadText(input, names(index).Length, 8 * 1024 * 1024), New JsonLoadSettings With {.DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error})
        End Using
        If ModrinthReference.IntegerValue(manifest, "formatVersion", 1) <> 1 OrElse ModrinthReference.Text(manifest, "game") <> "minecraft" Then Throw New NotSupportedException("不支持的 Modrinth 整合包格式或游戏。")
        Name = ModrinthReference.Text(manifest, "name").Trim() : Version = ModrinthReference.Text(manifest, "versionId")
        Summary = If(ModrinthReference.Text(manifest, "summary", False), "")
        Dim dependencies = TryCast(manifest("dependencies"), JObject)
        If dependencies Is Nothing Then Throw New InvalidDataException("Modrinth 整合包缺少 dependencies。")
        Minecraft = ModrinthReference.Text(dependencies, "minecraft") : SafeFiles.ValidateInstanceName(Minecraft)
        For Each pair In dependencies.Properties()
            Dim kind As ComponentKind
            Select Case pair.Name
                Case "minecraft" : Continue For
                Case "forge" : kind = ComponentKind.Forge
                Case "neoforge", "neo-forge" : kind = ComponentKind.NeoForge
                Case "fabric-loader" : kind = ComponentKind.Fabric
                Case Else : Throw New NotSupportedException("暂不支持整合包依赖：" & pair.Name & "；不会跳过所需加载器。")
            End Select
            If LoaderKind.HasValue Then Throw New InvalidDataException("整合包声明了多个加载器。")
            LoaderKind = kind : LoaderVersion = ModrinthReference.Text(dependencies, pair.Name)
            If Not Regex.IsMatch(LoaderVersion, "\A[0-9][A-Za-z0-9.+_-]{0,100}\z") Then Throw New InvalidDataException("整合包必须指定固定的加载器版本。")
        Next
        Dim files = TryCast(manifest("files"), JArray), refs As New List(Of ModrinthReference), targets As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        If files Is Nothing OrElse files.Count > 10000 Then Throw New InvalidDataException("整合包文件清单无效或超过 10000 项。")
        For Each item In files
            Dim value = TryCast(item, JObject)
            If value Is Nothing Then Throw New InvalidDataException("Modrinth 文件条目无效。")
            Dim reference As New ModrinthReference(value)
            If Not targets.Add(reference.Path) Then Throw New InvalidDataException("Modrinth 清单路径重复或大小写冲突：" & reference.Path)
            refs.Add(reference)
        Next
        References = refs.AsReadOnly()
        Dim winning As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        For Each layer In {"overrides/", "client-overrides/"}
            Dim start = prefix & layer
            For Each pair In names.Where(Function(item) Not CurseForgePackage.IsDirectory(item.Value) AndAlso item.Key.StartsWith(start, StringComparison.OrdinalIgnoreCase))
                Dim relative = pair.Key.Substring(start.Length)
                CurseForgePackage.ValidateRelative(relative) : winning(relative) = pair.Value.FullName
            Next
        Next
        ' Check ZIP topology as well as the final client view. Server-only files
        ' are never applied, but cannot smuggle a link or escaping archive path.
        ValidateParents(names.Keys, Function(key) Not CurseForgePackage.IsDirectory(names(key)))
        ' PCL skips unchosen optional files before checking their destination.
        ' Check required content here; the install plan checks the actual choices
        ' together before metadata requests or any game-directory writes.
        ValidateParents(refs.Where(Function(item) item.Client = "required").Select(Function(item) item.Path).Concat(winning.Keys).Distinct(StringComparer.OrdinalIgnoreCase), Function(key) True)
        OverrideSources = New ReadOnlyDictionary(Of String, String)(winning)
        OverrideFiles = winning.Keys.ToList().AsReadOnly()
    End Sub
    Private Shared Sub ValidateParents(paths As IEnumerable(Of String), isFile As Func(Of String, Boolean))
        Dim targets As New HashSet(Of String)(paths, StringComparer.OrdinalIgnoreCase)
        For Each relative In targets
            Dim parent = relative
            Do While parent.Contains("/"c)
                parent = parent.Substring(0, parent.LastIndexOf("/"c))
                If targets.Contains(parent) AndAlso isFile(parent) Then Throw New InvalidDataException("整合包文件与目录路径冲突：" & parent)
            Loop
        Next
    End Sub
    Public Shared Function Read(path As String, Optional token As CancellationToken = Nothing) As ModrinthPackage
        token.ThrowIfCancellationRequested() : path = IO.Path.GetFullPath(path)
        Using input As New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
            Dim digest = CurseForgePackage.HashArchive(input, token)
            Using archive As New ZipArchive(input, ZipArchiveMode.Read)
                Dim result As New ModrinthPackage(path, digest, archive)
                token.ThrowIfCancellationRequested() : Return result
            End Using
        End Using
    End Function
End Class
