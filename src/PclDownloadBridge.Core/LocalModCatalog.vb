Imports System.IO
Imports System.IO.Compression
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq

' PCL 5ef81e4 LocalResourceFile / LocalResourceLoaders: enabled suffixes and
' instance-relative Mods. Metadata readers are local, without network/cache writes.
Public NotInheritable Class LocalModEntry
    Public ReadOnly Property FilePath As String
    Public ReadOnly Property FileName As String
    Public ReadOnly Property EnabledName As String
    Public ReadOnly Property Enabled As Boolean
    Public ReadOnly Property Length As Long
    Public ReadOnly Property Sha256 As String
    Public Property DisplayName As String
    Public Property Version As String
    Public Property LoaderSummary As String = "未知"
    Public Property Description As String
    Public Property Problem As String
    Public Property IsArchiveValid As Boolean
    Public Property Recognized As Boolean
    Public ReadOnly Property Warnings As New List(Of String)
    Private ReadOnly _ids As New List(Of String)
    Private ReadOnly _idSet As New HashSet(Of String)(StringComparer.Ordinal)
    Private _iconData As Byte()
    Private _iconWidth As Integer, _iconHeight As Integer
    Public ReadOnly Property IconWidth As Integer
        Get
            Return _iconWidth
        End Get
    End Property
    Public ReadOnly Property IconHeight As Integer
        Get
            Return _iconHeight
        End Get
    End Property
    Public ReadOnly Property IconData As Byte()
        Get
            Return If(_iconData Is Nothing, Nothing, DirectCast(_iconData.Clone(), Byte()))
        End Get
    End Property
    Friend ReadOnly Property IconLength As Integer
        Get
            Return If(_iconData Is Nothing, 0, _iconData.Length)
        End Get
    End Property
    Friend Sub SetIcon(data As Byte())
        _iconData = data
        Dim width As Long, height As Long
        If data IsNot Nothing AndAlso LocalModCatalog.TryIconDimensions(data, width, height) Then
            _iconWidth = CInt(width) : _iconHeight = CInt(height)
        Else
            _iconWidth = 0 : _iconHeight = 0
        End If
    End Sub
    Public ReadOnly Property ModIds As IReadOnlyList(Of String)
        Get
            Return _ids.AsReadOnly()
        End Get
    End Property
    Public ReadOnly Property CanToggle As Boolean
        Get
            Return Sha256 IsNot Nothing AndAlso LocalModCatalog.IsSupportedFile(FilePath)
        End Get
    End Property
    Friend Sub New(full As String, size As Long, digest As String)
        FilePath = full : FileName = Path.GetFileName(full)
        EnabledName = LocalModCatalog.EnabledFileName(full)
        Enabled = String.Equals(FileName, EnabledName, StringComparison.Ordinal)
        Length = size : Sha256 = digest
        DisplayName = Path.GetFileNameWithoutExtension(EnabledName)
    End Sub
    Friend Sub AddId(value As String)
        If value IsNot Nothing AndAlso (value.Length > 256 OrElse value.Any(Function(ch) Char.IsControl(ch))) Then Throw New InvalidDataException("Mod ID 超出读取范围或包含无效字符。")
        If Not String.IsNullOrWhiteSpace(value) AndAlso _idSet.Add(value) Then _ids.Add(value)
    End Sub
End Class

Public NotInheritable Class LocalModCatalog
    Private Const MetadataLimit As Integer = 2 * 1024 * 1024
    Private Sub New()
    End Sub

    ' Remove one terminal state suffix only. A name containing ".old" elsewhere
    ' is not renamed, and repeated state suffixes are not silently normalized.
    Public Shared Function EnabledFileName(filePath As String) As String
        Dim name = Path.GetFileName(filePath), extension = Path.GetExtension(name)
        If extension.Equals(".disabled", StringComparison.OrdinalIgnoreCase) OrElse extension.Equals(".old", StringComparison.OrdinalIgnoreCase) Then
            name = Path.GetFileNameWithoutExtension(name)
        End If
        Return name
    End Function
    Public Shared Function IsSupportedFile(filePath As String) As Boolean
        If String.IsNullOrWhiteSpace(filePath) Then Return False
        Dim extension = Path.GetExtension(EnabledFileName(filePath))
        Return {".jar", ".zip", ".litemod"}.Contains(extension, StringComparer.OrdinalIgnoreCase)
    End Function
    Public Shared Function ModsDirectory(instance As InstalledInstance, Optional token As CancellationToken = Nothing) As String
        InstalledInstanceCatalog.EnsureUnchanged(instance, token)
        Dim full = Path.GetDirectoryName(SafeFiles.UnderRoot(instance.RunDirectory, "mods/entry"))
        ValidatePath(full, True)
        Return full
    End Function
    Public Shared Function Scan(instance As InstalledInstance, token As CancellationToken) As List(Of LocalModEntry)
        token.ThrowIfCancellationRequested()
        Dim full = ModsDirectory(instance, token), result As New List(Of LocalModEntry), iconBudget = 32 * 1024 * 1024
        If Directory.Exists(full) Then
            For Each filePath In Directory.EnumerateFiles(full)
                token.ThrowIfCancellationRequested()
                If IsSupportedFile(filePath) Then
                    Dim item = Read(filePath, token)
                    If item.IconLength > iconBudget Then item.SetIcon(Nothing) Else iconBudget -= item.IconLength
                    result.Add(item)
                End If
            Next
        End If
        InstalledInstanceCatalog.EnsureUnchanged(instance, token)
        For Each group In result.GroupBy(Function(item) item.EnabledName, StringComparer.OrdinalIgnoreCase).Where(Function(items) items.Count() > 1)
            For Each item In group
                item.Warnings.Add("同时存在同名的启用或禁用文件，请先确认文件冲突。")
            Next
        Next
        For Each group In result.SelectMany(Function(item) item.ModIds.Select(Function(id) New With {.Id = id, .Item = item})).GroupBy(Function(value) value.Id, StringComparer.Ordinal).Where(Function(items) items.Select(Function(value) value.Item.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            For Each item In group.Select(Function(value) value.Item).Distinct()
                item.Warnings.Add("多个文件声明了同一 Mod ID：" & group.Key)
            Next
        Next
        Return result.OrderBy(Function(item) item.DisplayName, StringComparer.CurrentCultureIgnoreCase).ThenBy(Function(item) item.FileName, StringComparer.OrdinalIgnoreCase).ToList()
    End Function
    Public Shared Function Read(filePath As String, token As CancellationToken) As LocalModEntry
        token.ThrowIfCancellationRequested()
        Dim full = Path.GetFullPath(filePath), result As New LocalModEntry(full, -1, Nothing)
        Try
            If Not IsSupportedFile(full) Then Throw New InvalidDataException("不支持的 Mod 文件扩展名。")
            ValidatePath(full, False)
            Using stream As New FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read)
                Dim length = stream.Length
                Dim digestValue = Digest(stream, token)
                result = New LocalModEntry(full, length, digestValue)
                stream.Position = 0
                Using archive As New ZipArchive(stream, ZipArchiveMode.Read, True)
                    ' ZipArchive reads its central directory lazily. Validate that
                    ' structure before distinguishing metadata errors from ZIP errors.
                    Dim entryCount = archive.Entries.Count
                    result.IsArchiveValid = True
                    ReadMetadata(archive, result, token)
                End Using
            End Using
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException OrElse
            TypeOf ex Is ArgumentException OrElse TypeOf ex Is NotSupportedException OrElse TypeOf ex Is Newtonsoft.Json.JsonException OrElse TypeOf ex Is FormatException
            result.Problem = DisplayText(ex.Message, 1024, False)
        End Try
        token.ThrowIfCancellationRequested()
        Return result
    End Function
    Private Shared Sub ValidatePath(full As String, expectDirectory As Boolean)
        Dim current = Path.GetFullPath(full)
        If expectDirectory AndAlso File.Exists(current) Then Throw New IOException("文件占用了 Mod 目录。")
        While current IsNot Nothing
            If (File.Exists(current) OrElse Directory.Exists(current)) AndAlso (File.GetAttributes(current) And FileAttributes.ReparsePoint) <> 0 Then Throw New IOException("Mod 路径包含链接，请选择真实目录。")
            current = Path.GetDirectoryName(current)
        End While
    End Sub
    Private Shared Function Digest(stream As Stream, token As CancellationToken) As String
        Using hash = SHA256.Create()
            Dim buffer(65535) As Byte
            Do
                token.ThrowIfCancellationRequested()
                Dim count = stream.Read(buffer, 0, buffer.Length)
                If count = 0 Then Exit Do
                hash.TransformBlock(buffer, 0, count, buffer, 0)
            Loop
            hash.TransformFinalBlock(New Byte() {}, 0, 0)
            Return BitConverter.ToString(hash.Hash).Replace("-", "")
        End Using
    End Function

    Private Class Identity
        Friend Id As String
        Friend Name As String
        Friend Version As String
        Friend Description As String
        Friend Loader As String
        Friend Icon As String
    End Class
    Private Shared Sub ReadMetadata(archive As ZipArchive, result As LocalModEntry, token As CancellationToken)
        ' PCL-CE 915ac297 ModLocalComp documents these local metadata formats;
        ' loader declarations are displayed, never treated as compatibility proof.
        Dim entries As New Dictionary(Of String, ZipArchiveEntry)(StringComparer.Ordinal)
        Dim relevant As New HashSet(Of String)({"fabric.mod.json", "quilt.mod.json", "META-INF/mods.toml", "META-INF/neoforge.mods.toml", "mcmod.info", "litemod.json", "META-INF/MANIFEST.MF"}, StringComparer.Ordinal)
        For Each entry In archive.Entries
            token.ThrowIfCancellationRequested()
            If Not relevant.Contains(entry.FullName) Then Continue For
            If entries.ContainsKey(entry.FullName) Then Throw New InvalidDataException("Mod 元数据条目重复：" & entry.FullName)
            entries.Add(entry.FullName, entry)
        Next
        Dim manifest As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        If entries.ContainsKey("META-INF/MANIFEST.MF") Then
            Try
                manifest = ReadManifest(entries("META-INF/MANIFEST.MF"), token)
            Catch ex As Exception When TypeOf ex Is InvalidDataException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is DecoderFallbackException
                result.Warnings.Add("无法读取 JAR 清单：" & ex.Message)
            End Try
        End If
        Dim identities As New List(Of Identity), problems As New List(Of String)
        Dim modern = entries.Keys.Any(Function(name) name = "fabric.mod.json" OrElse name = "quilt.mod.json" OrElse name.EndsWith("mods.toml", StringComparison.Ordinal))
        For Each name In {"fabric.mod.json", "quilt.mod.json", "META-INF/neoforge.mods.toml", "META-INF/mods.toml", "mcmod.info", "litemod.json"}
            If Not entries.ContainsKey(name) OrElse (name = "mcmod.info" AndAlso modern) Then Continue For
            Try
                Dim text = ReadText(entries(name), token)
                Select Case name
                    Case "fabric.mod.json" : identities.Add(ReadFabric(ParseObject(text)))
                    Case "quilt.mod.json" : identities.Add(ReadQuilt(ParseObject(text)))
                    Case "META-INF/mods.toml", "META-INF/neoforge.mods.toml" : identities.AddRange(LocalModToml.Read(text, If(name.Contains("neoforge"), "NeoForge", "Forge")))
                    Case "mcmod.info" : identities.AddRange(ReadLegacy(text))
                    Case "litemod.json" : identities.Add(ReadLite(ParseObject(text)))
                End Select
            Catch ex As Exception When TypeOf ex Is InvalidDataException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is Newtonsoft.Json.JsonException OrElse TypeOf ex Is FormatException OrElse TypeOf ex Is DecoderFallbackException
                problems.Add(name & "：" & ex.Message)
            End Try
        Next
        Dim primaryChosen As Boolean = False
        For Each identity In identities
            token.ThrowIfCancellationRequested()
            If String.IsNullOrWhiteSpace(identity.Id) Then Continue For
            result.AddId(identity.Id)
            If Not primaryChosen Then
                If Not String.IsNullOrWhiteSpace(identity.Name) Then result.DisplayName = DisplayText(identity.Name, 256, True)
                result.Version = DisplayText(ResolveVersion(identity.Version, manifest), 256, True)
                result.Description = DisplayText(identity.Description, 4096, False)
                primaryChosen = True
            End If
        Next
        result.Recognized = result.ModIds.Count > 0
        If identities.Count > 0 Then result.LoaderSummary = String.Join(" / ", identities.Select(Function(item) item.Loader).Distinct(StringComparer.Ordinal))
        If Not result.Recognized AndAlso (manifest.ContainsKey("FMLCorePlugin") OrElse manifest.ContainsKey("FMLModType")) Then
            result.Recognized = True : result.LoaderSummary = "Forge"
            If manifest.ContainsKey("Implementation-Title") Then result.DisplayName = DisplayText(manifest("Implementation-Title"), 256, True)
            If manifest.ContainsKey("Implementation-Version") Then result.Version = DisplayText(manifest("Implementation-Version"), 256, True)
            result.Warnings.Add("此文件通过 JAR 清单识别，未声明常规 Mod ID。")
        End If
        If Not result.Recognized Then result.Warnings.Add("未识别本地 Mod 元数据，兼容性未知。")
        If problems.Count > 0 Then result.Problem = DisplayText(String.Join("；", problems), 1024, False)
        For Each identity In identities
            Dim icon = ReadIcon(archive, identity.Icon, token)
            If icon IsNot Nothing Then result.SetIcon(icon) : Exit For
        Next
        If Not String.IsNullOrEmpty(result.Version) AndAlso result.Version.Contains("${") Then
            result.Version = Nothing : result.Warnings.Add("版本包含未解析的构建占位符。")
        End If
    End Sub
    Private Shared Function IconPath(value As JToken) As String
        If value?.Type = JTokenType.String Then Return CStr(value)
        Dim sizes = TryCast(value, JObject)
        If sizes Is Nothing Then Return Nothing
        Dim candidates As New List(Of KeyValuePair(Of Integer, String))
        For Each item In sizes.Properties()
            Dim size As Integer
            If Integer.TryParse(item.Name, size) AndAlso size > 0 AndAlso item.Value.Type = JTokenType.String Then candidates.Add(New KeyValuePair(Of Integer, String)(size, CStr(item.Value)))
        Next
        Return candidates.OrderBy(Function(item) If(item.Key >= 64, 0, 1)).ThenBy(Function(item) Math.Abs(CLng(item.Key) - 64)).Select(Function(item) item.Value).FirstOrDefault()
    End Function
    Private Shared Function ReadIcon(archive As ZipArchive, name As String, token As CancellationToken) As Byte()
        If String.IsNullOrWhiteSpace(name) OrElse name.Length > 1024 OrElse name.Any(Function(ch) Char.IsControl(ch)) Then Return Nothing
        name = name.Replace("\", "/")
        If name.StartsWith("/", StringComparison.Ordinal) OrElse name.Contains(":") OrElse name.Split("/"c).Any(Function(part) part = ".." OrElse part = "." OrElse part = "") Then Return Nothing
        Try
            Dim matches = archive.Entries.Where(Function(entry) entry.FullName = name).Take(2).ToArray()
            If matches.Length <> 1 OrElse matches(0).Length > 1024 * 1024 Then Return Nothing
            Dim bytes = ReadBytes(matches(0), token)
            If Not SafeIconDimensions(bytes) Then Return Nothing
            Return bytes
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is ArgumentException
            Return Nothing
        End Try
    End Function
    Friend Shared Function SafeIconDimensions(bytes As Byte()) As Boolean
        Dim width As Long, height As Long
        Return TryIconDimensions(bytes, width, height)
    End Function
    Friend Shared Function TryIconDimensions(bytes As Byte(), ByRef width As Long, ByRef height As Long) As Boolean
        width = 0 : height = 0
        If bytes.Length < 24 OrElse bytes.Length > 1024 * 1024 Then Return False
        If bytes.Take(8).SequenceEqual(New Byte() {137, 80, 78, 71, 13, 10, 26, 10}) Then
            width = CLng(bytes(16)) * 16777216 + CLng(bytes(17)) * 65536 + CLng(bytes(18)) * 256 + bytes(19)
            height = CLng(bytes(20)) * 16777216 + CLng(bytes(21)) * 65536 + CLng(bytes(22)) * 256 + bytes(23)
        ElseIf bytes(0) = 255 AndAlso bytes(1) = 216 Then
            ' Several real Mod archives store JPEG bytes under an .png name.
            Dim offset = 2
            While offset + 3 < bytes.Length
                If bytes(offset) <> 255 Then Return False
                While offset < bytes.Length AndAlso bytes(offset) = 255 : offset += 1 : End While
                If offset >= bytes.Length Then Return False
                Dim marker = CInt(bytes(offset)) : offset += 1
                If marker = 217 OrElse marker = 218 Then Return False
                If marker = 1 OrElse (marker >= 208 AndAlso marker <= 215) Then Continue While
                If offset + 1 >= bytes.Length Then Return False
                Dim size = CInt(bytes(offset)) * 256 + bytes(offset + 1)
                If size < 2 OrElse offset + size > bytes.Length Then Return False
                If marker >= 192 AndAlso marker <= 207 AndAlso Not {196, 200, 204}.Contains(marker) Then
                    If size < 8 Then Return False
                    height = CInt(bytes(offset + 3)) * 256 + bytes(offset + 4)
                    width = CInt(bytes(offset + 5)) * 256 + bytes(offset + 6)
                    Exit While
                End If
                offset += size
            End While
        End If
        Return width >= 1 AndAlso height >= 1 AndAlso width <= 4096 AndAlso height <= 4096
    End Function
    Private Shared Function DisplayText(value As String, maximum As Integer, singleLine As Boolean) As String
        If value Is Nothing Then Return Nothing
        If singleLine Then value = value.Replace(vbCr, " ").Replace(vbLf, " ").Replace(ChrW(9), " "c).Trim()
        If value.Length <= maximum Then Return value
        Dim count = maximum - 1
        If Char.IsHighSurrogate(value(count - 1)) Then count -= 1
        Return value.Substring(0, count) & "…"
    End Function
    Private Shared Function ReadText(entry As ZipArchiveEntry, token As CancellationToken) As String
        Using reader As New StreamReader(New MemoryStream(ReadBytes(entry, token)), New UTF8Encoding(False, True), True)
            Return reader.ReadToEnd()
        End Using
    End Function
    Private Shared Function ReadBytes(entry As ZipArchiveEntry, token As CancellationToken) As Byte()
        If entry.Length > MetadataLimit Then Throw New InvalidDataException("Mod 元数据超过读取范围。")
        Using source = entry.Open(), buffer As New MemoryStream()
            Dim bytes(65535) As Byte
            Do
                token.ThrowIfCancellationRequested()
                Dim count = source.Read(bytes, 0, bytes.Length)
                If count = 0 Then Exit Do
                If buffer.Length + count > MetadataLimit Then Throw New InvalidDataException("Mod 元数据超过读取范围。")
                buffer.Write(bytes, 0, count)
            Loop
            Return buffer.ToArray()
        End Using
    End Function
    Private Shared Function ParseObject(text As String) As JObject
        Return JObject.Parse(text, New JsonLoadSettings With {.DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error})
    End Function
    Private Shared Function Scalar(value As JToken, Optional required As Boolean = False) As String
        If value Is Nothing OrElse value.Type = JTokenType.Null Then
            If required Then Throw New InvalidDataException("缺少 Mod 标识。")
            Return Nothing
        End If
        If value.Type <> JTokenType.String Then Throw New InvalidDataException("Mod 文本字段类型无效。")
        Dim text = CStr(value)
        If required AndAlso String.IsNullOrWhiteSpace(text) Then Throw New InvalidDataException("Mod 标识为空。")
        Return text
    End Function
    Private Shared Function ReadFabric(json As JObject) As Identity
        Return New Identity With {.Id = Scalar(json("id"), True), .Name = Scalar(json("name")), .Version = Scalar(json("version")), .Description = Scalar(json("description")), .Loader = "Fabric", .Icon = IconPath(json("icon"))}
    End Function
    Private Shared Function ReadQuilt(json As JObject) As Identity
        Dim loader = TryCast(json("quilt_loader"), JObject)
        If loader Is Nothing Then Throw New InvalidDataException("缺少 Quilt 加载器信息。")
        Dim metadata = TryCast(loader("metadata"), JObject)
        Return New Identity With {.Id = Scalar(loader("id"), True), .Name = Scalar(metadata?("name")), .Version = Scalar(loader("version")), .Description = Scalar(metadata?("description")), .Loader = "Quilt", .Icon = IconPath(metadata?("icon"))}
    End Function
    Private Shared Function ReadLite(json As JObject) As Identity
        Return New Identity With {.Id = Scalar(json("name"), True), .Name = If(Scalar(json("displayName")), Scalar(json("name"))), .Version = Scalar(json("version")), .Description = Scalar(json("description")), .Loader = "LiteLoader", .Icon = IconPath(json("logoFile"))}
    End Function
    Private Shared Function ReadLegacy(text As String) As IEnumerable(Of Identity)
        Dim json = JToken.Parse(text, New JsonLoadSettings With {.DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error})
        Dim items = TryCast(json, JArray)
        If items Is Nothing AndAlso TypeOf json Is JObject Then items = TryCast(json("modList"), JArray)
        If items Is Nothing OrElse items.Count = 0 OrElse items.Any(Function(item) Not TypeOf item Is JObject) Then Throw New InvalidDataException("旧版 Mod 信息列表无效。")
        Return items.OfType(Of JObject)().Select(Function(item) New Identity With {.Id = Scalar(item("modid"), True), .Name = Scalar(item("name")), .Version = Scalar(item("version")), .Description = Scalar(item("description")), .Loader = "Forge", .Icon = IconPath(item("logoFile"))}).ToList()
    End Function
    Private Shared Function ResolveVersion(value As String, manifest As Dictionary(Of String, String)) As String
        If value = "${file.jarVersion}" Then
            Dim resolved As String = Nothing
            If manifest.TryGetValue("Implementation-Version", resolved) Then Return resolved
            Return Nothing
        End If
        Return value
    End Function
    Private Shared Function ReadManifest(entry As ZipArchiveEntry, token As CancellationToken) As Dictionary(Of String, String)
        Dim bytes = ReadBytes(entry, token), offset = 0, hasHeader = False
        ' JAR manifests fold at byte boundaries, including within a UTF-8 code
        ' point. Unfold the main section before decoding, as java.util.jar does.
        If bytes.Length >= 3 AndAlso bytes(0) = &HEF AndAlso bytes(1) = &HBB AndAlso bytes(2) = &HBF Then offset = 3
        Using unfolded As New MemoryStream()
            While offset < bytes.Length
                token.ThrowIfCancellationRequested()
                Dim finish = offset
                While finish < bytes.Length AndAlso bytes(finish) <> 10 AndAlso bytes(finish) <> 13
                    finish += 1
                End While
                If finish = offset Then Exit While ' Named entry sections do not supply Mod metadata.
                If bytes(offset) = 32 Then
                    If Not hasHeader Then Throw New InvalidDataException("JAR 清单续行缺少字段。")
                    unfolded.Write(bytes, offset + 1, finish - offset - 1)
                Else
                    If hasHeader Then unfolded.WriteByte(10)
                    unfolded.Write(bytes, offset, finish - offset)
                    hasHeader = True
                End If
                offset = finish
                If offset < bytes.Length AndAlso bytes(offset) = 13 Then offset += 1
                If offset < bytes.Length AndAlso bytes(offset) = 10 Then offset += 1
            End While
            Return ReadManifest(New UTF8Encoding(False, True).GetString(unfolded.ToArray()))
        End Using
    End Function
    Private Shared Function ReadManifest(text As String) As Dictionary(Of String, String)
        Dim result As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase), key As String = Nothing
        For Each line In text.Replace(vbCrLf, vbLf).Replace(vbCr, vbLf).Split(ChrW(10))
            If line.Length = 0 Then Exit For ' Main attributes, never a named entry section.
            If line.StartsWith(" ", StringComparison.Ordinal) Then
                If key Is Nothing Then Throw New InvalidDataException("JAR 清单续行缺少字段。")
                result(key) &= line.Substring(1)
            Else
                Dim colon = line.IndexOf(":"c)
                If colon <= 0 Then Throw New InvalidDataException("JAR 清单字段无效。")
                key = line.Substring(0, colon)
                If result.ContainsKey(key) Then Throw New InvalidDataException("JAR 清单字段重复。")
                result.Add(key, line.Substring(colon + 1).TrimStart(" "c))
            End If
        Next
        Return result
    End Function

    ' Bounded TOML scalar/table reader used only for [[mods]] metadata. Values in
    ' other tables are lexed (so strings/comments cannot fabricate a mods table)
    ' but are never interpreted as dependency or compatibility declarations.
    Private NotInheritable Class LocalModToml
        Friend Shared Function Read(text As String, loader As String) As List(Of Identity)
            Dim result As New List(Of Identity), current As Dictionary(Of String, String) = Nothing
            For Each statement In Statements(text)
                Dim line = statement.Trim()
                If line.Length = 0 Then Continue For
                If line.StartsWith("[", StringComparison.Ordinal) Then
                    If current IsNot Nothing Then result.Add(ToIdentity(current, loader))
                    current = Nothing
                    Dim table = line.Trim("["c, "]"c).Trim()
                    If table = "mods" OrElse table = """mods""" OrElse table = "'mods'" Then current = New Dictionary(Of String, String)(StringComparer.Ordinal)
                ElseIf current IsNot Nothing Then
                    Dim equals = line.IndexOf("="c)
                    If equals < 1 Then Throw New InvalidDataException("Mod TOML 字段无效。")
                    Dim key = line.Substring(0, equals).Trim()
                    If key.StartsWith("""", StringComparison.Ordinal) OrElse key.StartsWith("'", StringComparison.Ordinal) Then key = StringValue(key)
                    If Not {"modId", "displayName", "version", "description", "logoFile"}.Contains(key, StringComparer.Ordinal) Then Continue For
                    If key = "logoFile" Then
                        Try
                            If current.ContainsKey(key) Then current(key) = Nothing Else current.Add(key, StringValue(line.Substring(equals + 1).Trim()))
                        Catch ex As InvalidDataException
                            current(key) = Nothing
                        End Try
                        Continue For
                    End If
                    If current.ContainsKey(key) Then Throw New InvalidDataException("Mod TOML 字段重复：" & key)
                    current.Add(key, StringValue(line.Substring(equals + 1).Trim()))
                End If
            Next
            If current IsNot Nothing Then result.Add(ToIdentity(current, loader))
            If result.Count = 0 Then Throw New InvalidDataException("TOML 中没有有效的 mods 信息。")
            Return result
        End Function
        Private Shared Function ToIdentity(values As Dictionary(Of String, String), loader As String) As Identity
            Dim id As String = Nothing, name As String = Nothing, version As String = Nothing, description As String = Nothing, icon As String = Nothing
            If Not values.TryGetValue("modId", id) OrElse String.IsNullOrWhiteSpace(id) Then Throw New InvalidDataException("TOML 中缺少 Mod ID。")
            values.TryGetValue("displayName", name) : values.TryGetValue("version", version) : values.TryGetValue("description", description)
            values.TryGetValue("logoFile", icon)
            Return New Identity With {.Id = id, .Name = name, .Version = version, .Description = description, .Loader = loader, .Icon = icon}
        End Function
        Private Shared Function Statements(text As String) As List(Of String)
            Dim result As New List(Of String), buffer As New StringBuilder(), quote As Char = ChrW(0), triple As Boolean = False, comment As Boolean = False, escaped As Boolean = False
            Dim depth As Integer = 0, i As Integer = 0
            While i < text.Length
                Dim ch = text(i)
                If comment Then
                    If ch <> ChrW(10) AndAlso ch <> ChrW(13) Then i += 1 : Continue While
                    comment = False
                End If
                If quote <> ChrW(0) Then
                    If escaped Then
                        buffer.Append(ch) : escaped = False
                    ElseIf quote = """"c AndAlso ch = "\"c Then
                        buffer.Append(ch) : escaped = True
                    ElseIf ch = quote AndAlso (Not triple OrElse (i + 2 < text.Length AndAlso text(i + 1) = quote AndAlso text(i + 2) = quote)) Then
                        Dim quoteCount = 1
                        If triple Then
                            quoteCount = 3
                            While i + quoteCount < text.Length AndAlso text(i + quoteCount) = quote
                                quoteCount += 1
                            End While
                            ' TOML permits one or two literal quotes immediately
                            ' before a multiline string's three closing quotes.
                            If quoteCount > 5 Then Throw New InvalidDataException("TOML 多行字符串引号无效。")
                        End If
                        buffer.Append(ch, quoteCount) : i += quoteCount - 1
                        quote = ChrW(0) : triple = False
                    Else
                        If Not triple AndAlso (ch = ChrW(10) OrElse ch = ChrW(13)) Then Throw New InvalidDataException("TOML 单行字符串未结束。")
                        buffer.Append(ch)
                    End If
                ElseIf ch = "#"c Then
                    comment = True
                ElseIf ch = """"c OrElse ch = "'"c Then
                    quote = ch : triple = i + 2 < text.Length AndAlso text(i + 1) = ch AndAlso text(i + 2) = ch
                    buffer.Append(ch)
                    If triple Then buffer.Append(ch).Append(ch) : i += 2
                ElseIf ch = ChrW(10) OrElse ch = ChrW(13) Then
                    If depth = 0 Then
                        result.Add(buffer.ToString()) : buffer.Clear()
                    Else
                        buffer.Append(ch)
                    End If
                Else
                    If ch = "["c OrElse ch = "{"c Then depth += 1
                    If ch = "]"c OrElse ch = "}"c Then depth -= 1
                    If depth < 0 Then Throw New InvalidDataException("TOML 括号无效。")
                    buffer.Append(ch)
                End If
                i += 1
            End While
            If quote <> ChrW(0) OrElse depth <> 0 Then Throw New InvalidDataException("TOML 字符串或数组未结束。")
            If buffer.Length > 0 Then result.Add(buffer.ToString())
            Return result
        End Function
        Private Shared Function StringValue(value As String) As String
            If value.Length < 2 Then Throw New InvalidDataException("TOML Mod 信息必须是字符串。")
            Dim quote = value(0), triple = value.StartsWith("""""""", StringComparison.Ordinal) OrElse value.StartsWith("'''", StringComparison.Ordinal)
            If quote <> """"c AndAlso quote <> "'"c Then Throw New InvalidDataException("TOML Mod 信息必须是字符串。")
            Dim edge = If(triple, 3, 1)
            If value.Length < edge * 2 OrElse value.Substring(value.Length - edge) <> New String(quote, edge) Then Throw New InvalidDataException("TOML 字符串未结束。")
            Dim inner = value.Substring(edge, value.Length - edge * 2).Replace(vbCrLf, vbLf).Replace(vbCr, vbLf)
            If triple AndAlso inner.StartsWith(vbLf, StringComparison.Ordinal) Then inner = inner.Substring(1)
            If quote = "'"c Then Return inner
            Dim result As New StringBuilder(), i = 0
            While i < inner.Length
                Dim ch = inner(i)
                If ch <> "\"c Then
                    result.Append(ch)
                Else
                    i += 1
                    If i >= inner.Length Then Throw New InvalidDataException("TOML 转义未结束。")
                    Select Case inner(i)
                        Case "b"c : result.Append(ChrW(8))
                        Case "t"c : result.Append(ChrW(9))
                        Case "n"c : result.Append(vbLf)
                        Case "f"c : result.Append(ChrW(12))
                        Case "r"c : result.Append(vbCr)
                        Case """"c : result.Append(""""c)
                        Case "\"c : result.Append("\"c)
                        Case "u"c, "U"c
                            Dim count = If(inner(i) = "u"c, 4, 8)
                            If i + count >= inner.Length Then Throw New InvalidDataException("TOML Unicode 转义不完整。")
                            Dim point As Integer
                            If Not Integer.TryParse(inner.Substring(i + 1, count), Globalization.NumberStyles.HexNumber, Globalization.CultureInfo.InvariantCulture, point) Then Throw New InvalidDataException("TOML Unicode 转义无效。")
                            If point < 0 OrElse point > &H10FFFF OrElse (point >= &HD800 AndAlso point <= &HDFFF) Then Throw New InvalidDataException("TOML Unicode 码点无效。")
                            result.Append(Char.ConvertFromUtf32(point)) : i += count
                        Case ChrW(10), ChrW(13), " "c, ChrW(9)
                            If Not triple Then Throw New InvalidDataException("TOML 单行字符串包含换行。")
                            ' A continuation backslash may be followed by spaces
                            ' or tabs, but must reach a newline before other text.
                            Dim hasNewline = inner(i) = ChrW(10) OrElse inner(i) = ChrW(13)
                            While i + 1 < inner.Length AndAlso (inner(i + 1) = " "c OrElse inner(i + 1) = ChrW(9) OrElse inner(i + 1) = ChrW(10) OrElse inner(i + 1) = ChrW(13))
                                i += 1
                                hasNewline = hasNewline OrElse inner(i) = ChrW(10) OrElse inner(i) = ChrW(13)
                            End While
                            If Not hasNewline Then Throw New InvalidDataException("TOML 字符串转义无效。")
                        Case Else : Throw New InvalidDataException("TOML 字符串转义无效。")
                    End Select
                End If
                i += 1
            End While
            Return result.ToString()
        End Function
    End Class
End Class
