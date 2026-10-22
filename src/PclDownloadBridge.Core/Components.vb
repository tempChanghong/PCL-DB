Imports System.IO
Imports System.Text.RegularExpressions
Imports System.Threading
Imports System.Xml.Linq
Imports Newtonsoft.Json.Linq

Public Enum ComponentKind
    Forge
    NeoForge
    Fabric
    FabricApi
    OptiFine
    OptiFabric
    LiteLoader
End Enum

Public Class ComponentEntry
    Public Property Kind As ComponentKind
    Public Property Version As String
    Public Property Minecraft As String
    Public Property Stable As Boolean = True
    Public Property Url As String
    Public Property FileName As String
    Public Property Sha1 As String
    Public Property Md5 As String
    Public Property Size As Long = -1
    Public Property RequiredForge As String
    Public Property Data As JObject
    Public Function Copy() As ComponentEntry
        Dim result = DirectCast(MemberwiseClone(), ComponentEntry)
        result.Data = DirectCast(Data?.DeepClone(), JObject)
        Return result
    End Function
    Public ReadOnly Property Title As String
        Get
            Return If(Kind = ComponentKind.FabricApi OrElse Kind = ComponentKind.OptiFabric, CStr(Data?("name")), Version)
        End Get
    End Property
End Class

Public Class ComponentSelection
    Public ReadOnly Entries As New Dictionary(Of ComponentKind, ComponentEntry)
    Public Property JavaPath As String
    Public Function Copy() As ComponentSelection
        Dim result As New ComponentSelection With {.JavaPath = JavaPath}
        For Each pair As KeyValuePair(Of ComponentKind, ComponentEntry) In Entries
            If pair.Value Is Nothing Then Throw New InvalidDataException("组件条目不能为空。")
            result.Entries.Add(pair.Key, pair.Value.Copy())
        Next
        Return result
    End Function
    Public Function Item(kind As ComponentKind) As ComponentEntry
        Dim value As ComponentEntry = Nothing
        Entries.TryGetValue(kind, value)
        Return value
    End Function
    Public Function Has(kind As ComponentKind) As Boolean
        Return Entries.ContainsKey(kind)
    End Function
    Public ReadOnly Property Description As String
        Get
            Return String.Join(" + ", Entries.OrderBy(Function(v) v.Key).Select(Function(v) ComponentCatalog.Label(v.Key) & " " & v.Value.Version))
        End Get
    End Property
    ' PCL PageDownloadInstall.Load*GetError and IsOptiFineSuitForForge.
    Public Function Unavailable(kind As ComponentKind, mc As String, Optional candidate As ComponentEntry = Nothing) As String
        If kind = ComponentKind.Forge Then
            If Has(ComponentKind.NeoForge) Then Return "与 NeoForge 不兼容"
            If Has(ComponentKind.Fabric) Then Return "与 Fabric 不兼容"
            If Has(ComponentKind.OptiFine) AndAlso candidate IsNot Nothing AndAlso Not OptiFineFits(Item(ComponentKind.OptiFine), candidate, mc) Then Return "仅兼容 OptiFine 指定的 Forge 版本"
        ElseIf kind = ComponentKind.NeoForge Then
            If Has(ComponentKind.Forge) OrElse Has(ComponentKind.Fabric) OrElse Has(ComponentKind.LiteLoader) Then Return "与已选模组加载器不兼容"
            If Has(ComponentKind.OptiFine) Then Return "与 OptiFine 不兼容"
        ElseIf kind = ComponentKind.Fabric Then
            If Has(ComponentKind.Forge) OrElse Has(ComponentKind.NeoForge) OrElse Has(ComponentKind.LiteLoader) Then Return "与已选模组加载器不兼容"
            If Has(ComponentKind.OptiFine) AndAlso CompareMc(mc, "1.20.4") > 0 Then Return "此版本的 Fabric 与 OptiFine 不兼容"
        ElseIf kind = ComponentKind.LiteLoader Then
            If Has(ComponentKind.Fabric) OrElse Has(ComponentKind.NeoForge) Then Return "与已选模组加载器不兼容"
        ElseIf kind = ComponentKind.FabricApi Then
            If Not Has(ComponentKind.Fabric) Then Return "需要安装 Fabric"
        ElseIf kind = ComponentKind.OptiFabric Then
            If Not Has(ComponentKind.Fabric) OrElse Not Has(ComponentKind.OptiFine) Then Return "需要安装 OptiFine 与 Fabric"
            If MinecraftVersionRules.ReleaseFamily(mc) < 160 Then Return "老版本需要手动安装 OptiFabric Origins"
        ElseIf kind = ComponentKind.OptiFine Then
            If Has(ComponentKind.NeoForge) Then Return "与 NeoForge 不兼容"
            If Has(ComponentKind.Fabric) AndAlso CompareMc(mc, "1.20.4") > 0 Then Return "此版本的 OptiFine 与 Fabric 不兼容"
            If Has(ComponentKind.Forge) AndAlso candidate IsNot Nothing AndAlso Not OptiFineFits(candidate, Item(ComponentKind.Forge), mc) Then Return "仅兼容特定版本的 Forge"
        End If
        Return Nothing
    End Function
    Public Sub Validate(mc As String)
        For Each pair In Entries
            If pair.Key <> pair.Value.Kind OrElse pair.Value.Minecraft <> mc Then Throw New InvalidDataException("组件不适用于所选 Minecraft 版本。")
            Dim issue = Unavailable(pair.Key, mc, pair.Value)
            If issue IsNot Nothing Then Throw New InvalidDataException(ComponentCatalog.Label(pair.Key) & "：" & issue)
        Next
        If Has(ComponentKind.Fabric) AndAlso Has(ComponentKind.OptiFine) AndAlso Not Has(ComponentKind.OptiFabric) Then Throw New InvalidDataException("Fabric + OptiFine 需要选择兼容的 OptiFabric。")
    End Sub
    Public Shared Function CompareMc(a As String, b As String) As Integer
        If a Is Nothing OrElse b Is Nothing Then Return StringComparer.OrdinalIgnoreCase.Compare(a, b)
        ' Reuse the fixed PCL MCUtils comparator, including numeric suffixes and pre/rc ordering.
        Return PclLibrarySelection.CompareVersion(a, b)
    End Function
    Public Shared Function OptiFineFits(opt As ComponentEntry, forge As ComponentEntry, mc As String) As Boolean
        If CompareMc(mc, "1.13") >= 0 AndAlso CompareMc(mc, "1.14.3") <= 0 Then Return False
        If opt.RequiredForge Is Nothing Then Return False
        If opt.RequiredForge.Trim().Length = 0 Then Return True
        If opt.RequiredForge.Contains(".") Then Return forge.Version = opt.RequiredForge
        Return forge.Version.Split("."c).Last() = opt.RequiredForge
    End Function
End Class

' Primary references: PCL ModDownload Dl*List and McDownload*Loader at 5ef81e4.
Public Class ComponentCatalog
    Private ReadOnly _policy As SourcePolicy
    Public Sub New(policy As SourcePolicy)
        _policy = policy
    End Sub
    Public Shared Function Label(kind As ComponentKind) As String
        Return If(kind = ComponentKind.FabricApi, "Fabric API", kind.ToString())
    End Function
    Public Shared Function Icon(kind As ComponentKind) As String
        Select Case kind
            Case ComponentKind.Forge : Return "Anvil"
            Case ComponentKind.NeoForge : Return "NeoForge"
            Case ComponentKind.Fabric, ComponentKind.FabricApi : Return "Fabric"
            Case ComponentKind.OptiFine : Return "GrassPath"
            Case ComponentKind.OptiFabric : Return "OptiFabric"
            Case Else : Return "Egg"
        End Select
    End Function
    Public Async Function LoadAsync(kind As ComponentKind, mc As String, token As CancellationToken) As Task(Of List(Of ComponentEntry))
        Dim result As New List(Of ComponentEntry)
        Select Case kind
            Case ComponentKind.Fabric
                Dim data = Await _policy.GetVersionListTokenAsync("https://meta.fabricmc.net/v2/versions/loader/" & Uri.EscapeDataString(mc), token, AddressOf ParseFabricMetadata).ConfigureAwait(False)
                For Each item In data
                    result.Add(New ComponentEntry With {.Kind = kind, .Minecraft = mc, .Version = CStr(item("loader")("version")), .Stable = CBool(item("loader")("stable"))})
                Next
            Case ComponentKind.FabricApi
                Dim project = "P7dR8mSH"
                Dim url = "https://api.modrinth.com/v2/project/" & project & "/version?loaders=" & Uri.EscapeDataString("[""fabric""]") & "&game_versions=" & Uri.EscapeDataString(New JArray(mc).ToString(Newtonsoft.Json.Formatting.None))
                Dim data = Await _policy.GetVersionListTokenAsync(url, token,
                    Function(body) ParseFabricApiMetadata(body, mc, _policy.AllowLoopback)).ConfigureAwait(False)
                For Each item As JObject In data
                    Dim file = SelectFabricApiFile(item)
                    result.Add(New ComponentEntry With {.Kind = kind, .Minecraft = mc, .Version = CStr(item("version_number")), .Stable = CStr(item("version_type")) = "release", .Url = CStr(file("url")), .FileName = CStr(file("filename")), .Sha1 = CStr(file("hashes")("sha1")), .Size = CLng(file("size")), .Data = item})
                Next
            Case ComponentKind.OptiFabric
                ' PCL DlOptiFabricLoader uses CurseForge project 322385, not a
                ' similarly-named Modrinth project. No upstream API credentials used.
                If MinecraftVersionRules.ReleaseFamily(mc) < 160 OrElse ComponentSelection.CompareMc(mc, "1.20.4") > 0 Then Return result
                Dim offset = 0
                Dim seen As New HashSet(Of Long)
                Do
                    Dim publicUrl = "https://www.curseforge.com/api/v1/mods/322385/files?pageSize=50&pageIndex=" & (offset \ 50)
                    Dim mirrorUrl = "https://mod.mcimirror.top/curseforge/v1/mods/322385/files?pageSize=50&index=" & offset
                    Dim sources = If(_policy.EffectiveVersionPreference <> SourcePreference.MirrorFirst, {publicUrl, mirrorUrl}, {mirrorUrl, publicUrl})
                    Dim acceptedIds = seen.ToArray()
                    Dim data = Await _policy.RaceVersionListTokenAsync(sources.SelectMany(Function(u) _policy.Candidates(u, "version")).Distinct(), token,
                        Function(body) ParseOptiFabricPage(body, mc, acceptedIds)).ConfigureAwait(False)
                    Dim files = DirectCast(data("data"), JArray)
                    For Each item As JObject In files
                        If Not seen.Add(CLng(item("id"))) Then Throw New InvalidDataException("OptiFabric 列表返回了重复的分页。")
                        If Not item("gameVersions").Values(Of String)().Contains(mc) Then Continue For
                        If item("projectId") IsNot Nothing AndAlso CInt(item("projectId")) <> 322385 Then Throw New InvalidDataException("OptiFabric 项目编号不匹配。")
                        Dim id = CLng(item("id")), fileName = CStr(item("fileName"))
                        Dim hash = SelectOptiFabricHash(item)
                        Dim algorithm = If(hash Is Nothing, 0, CInt(hash("algo")))
                        Dim checksum = CStr(hash?("value"))?.ToLowerInvariant()
                        Dim url = CStr(item("downloadUrl"))
                        If String.IsNullOrWhiteSpace(url) Then url = "https://edge.forgecdn.net/files/" & (id \ 1000) & "/" & (id Mod 1000) & "/" & Uri.EscapeDataString(fileName)
                        Dim version = fileName.Replace("optifabric-", "").Replace(".jar", "").TrimStart("v"c)
                        item("name") = CStr(item("displayName"))
                        Dim size = CLng(item("fileLength"))
                        result.Add(New ComponentEntry With {.Kind = kind, .Minecraft = mc, .Version = version, .Stable = CInt(item("releaseType")) = 1, .Url = url, .FileName = fileName, .Sha1 = If(algorithm = 1, checksum, Nothing), .Md5 = If(algorithm = 2, checksum, Nothing), .Size = If(size = 0, -1L, size), .Data = item})
                    Next
                    offset += files.Count
                    If files.Count < 50 Then Exit Do
                    If offset >= 2000 Then Throw New InvalidDataException("OptiFabric 文件列表超过合理范围。")
                Loop
            Case ComponentKind.Forge
                Dim data = Await _policy.GetVersionListAsync(
                    "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml",
                    "https://bmclapi2.bangbang93.com/forge/minecraft/" & Uri.EscapeDataString(mc.Replace("-", "_")), token,
                    Function(body) ParseForgeMetadata(body, mc), AddressOf ParseForgeMirrorMetadata).ConfigureAwait(False)
                Dim coordinates As New HashSet(Of String)(StringComparer.Ordinal)
                If data IsNot Nothing Then
                    For Each item In data
                        Dim file = item("files").FirstOrDefault(Function(f) CStr(f("category")) = "installer" AndAlso CStr(f("format")) = "jar")
                        If file Is Nothing Then Continue For
                        Dim release = ForgeRelease.Parse(CStr(item("version")), CStr(item("branch")), mc)
                        If release Is Nothing OrElse Not coordinates.Add(release.FileVersion) Then Continue For
                        Dim coordinate = mc.Replace("-", "_") & "-" & release.FileVersion
                        Dim checksum = CStr(file("hash"))
                        result.Add(New ComponentEntry With {.Kind = kind, .Minecraft = mc, .Version = release.VersionName, .Url = "https://maven.minecraftforge.net/net/minecraftforge/forge/" & coordinate & "/forge-" & coordinate & "-installer.jar", .Sha1 = If(checksum?.Length = 40, checksum, Nothing), .Md5 = If(checksum?.Length = 32, checksum, Nothing), .Data = DirectCast(item, JObject)})
                    Next
                End If
                result = result.OrderByDescending(Function(v) SortKey(v.Version), StringComparer.Ordinal).ToList()
            Case ComponentKind.NeoForge
                If ComponentSelection.CompareMc(mc, "1.20.1") < 0 Then Return result
                Dim package = If(mc = "1.20.1", "forge", "neoforge")
                Dim parse As Func(Of String, JToken) = Function(body) ParseNeoForgeMetadata(body, package = "forge")
                Dim data = Await _policy.GetVersionListAsync(
                    "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/" & package,
                    "https://bmclapi2.bangbang93.com/neoforge/meta/api/maven/details/releases/net/neoforged/" & package,
                    token, parse, parse).ConfigureAwait(False)
                Dim releases = data("versions").Values(Of String)().Select(Function(value) NeoForgeRelease.Parse(value, package = "forge")).Where(Function(value) value IsNot Nothing AndAlso value.Minecraft = mc).
                    OrderByDescending(Function(value) value.NumericVersion).ThenByDescending(Function(value) SortKey(value.VersionName), StringComparer.OrdinalIgnoreCase)
                For Each release In releases
                    result.Add(New ComponentEntry With {.Kind = kind, .Minecraft = mc, .Version = release.VersionName, .Stable = release.Stable, .Url = "https://maven.neoforged.net/releases/net/neoforged/" & package & "/" & release.ApiName & "/" & package & "-" & release.ApiName & "-installer.jar"})
                Next
            Case ComponentKind.OptiFine
                Dim data = Await _policy.GetVersionListAsync("https://optifine.net/downloads", "https://bmclapi2.bangbang93.com/optifine/versionList",
                    token, Function(body) ParseOptiFinePage(body), AddressOf ParseOptiFineMirrorMetadata).ConfigureAwait(False)
                For Each item As JObject In data
                    If CStr(item("mcversion")).Replace("1.8.0", "1.8").Replace("1.9.0", "1.9") <> mc Then Continue For
                    Dim required = If(CStr(item("forge")), "").Replace("Forge ", "").Replace("#", "")
                    If required.Contains("N/A") Then required = Nothing
                    result.Add(New ComponentEntry With {.Kind = kind, .Minecraft = mc, .Version = (CStr(item("type")).Replace("HD_U", "").Trim("_"c) & " " & CStr(item("patch"))).Trim(), .Stable = Not CStr(item("patch")).Contains("pre"), .Url = "https://bmclapi2.bangbang93.com/optifine/" & CStr(item("mcversion")) & "/" & CStr(item("type")) & "/" & CStr(item("patch")), .FileName = CStr(item("filename")), .RequiredForge = required, .Data = item})
                Next
                result = result.OrderByDescending(Function(v) v.Stable).ThenByDescending(Function(v) SortKey(v.Version), StringComparer.Ordinal).ToList()
            Case ComponentKind.LiteLoader
                If ComponentSelection.CompareMc(mc, "1.12.2") > 0 OrElse mc.StartsWith("1.5", StringComparison.Ordinal) OrElse mc.StartsWith("1.6", StringComparison.Ordinal) Then Return result
                Dim data = Await _policy.GetVersionListTokenAsync("https://dl.liteloader.com/versions/versions.json", token,
                    Function(body) ParseLiteLoaderMetadata(body, mc)).ConfigureAwait(False)
                Dim game = data("versions")?(mc)
                Dim latest = If(game?("artefacts"), game?("snapshots"))?("com.mumfrey:liteloader")?("latest")
                If latest IsNot Nothing Then result.Add(New ComponentEntry With {.Kind = kind, .Minecraft = mc, .Version = CStr(latest("version")), .Stable = Not String.Equals(CStr(latest("stream")), "SNAPSHOT", StringComparison.OrdinalIgnoreCase), .Data = DirectCast(latest, JObject)})
        End Select
        Return result
    End Function

    ' PCL's per-source loaders reject missing required fields before reporting
    ' success. Preserve valid empty arrays and ignore unrelated extra fields.
    Private Shared Function ParseFabricMetadata(body As String) As JToken
        Dim entries = JArray.Parse(body)
        For Each value In entries
            Dim item = TryCast(value, JObject)
            Dim loader = If(item Is Nothing, Nothing, TryCast(item("loader"), JObject))
            If loader Is Nothing Then Throw New InvalidDataException("Fabric 版本条目缺少 loader 对象。")
            ValidateCatalogueText(loader, "version", "Fabric")
            Dim stable = loader("stable")
            If stable Is Nothing OrElse stable.Type <> JTokenType.Boolean Then Throw New InvalidDataException("Fabric 版本条目的 stable 字段无效。")
        Next
        Return entries
    End Function

    Private Shared Function ParseFabricApiMetadata(body As String, mc As String, allowLoopback As Boolean) As JToken
        Dim entries As JArray
        ' Keep the original UTC/offset strings. The default DateTime conversion
        ' followed by a string cast can lose the zone before publication sorting.
        Using input As New StringReader(body), reader As New Newtonsoft.Json.JsonTextReader(input) With {.DateParseHandling = Newtonsoft.Json.DateParseHandling.None}
            entries = JArray.Load(reader)
            While reader.Read()
                If reader.TokenType <> Newtonsoft.Json.JsonToken.Comment Then Throw New InvalidDataException("Fabric API 版本列表含有多余内容。")
            End While
        End Using
        Dim accepted As New List(Of JObject)
        For Each value In entries
            Dim item = TryCast(value, JObject)
            If item Is Nothing Then Throw New InvalidDataException("Fabric API 版本条目格式无效。")
            ValidateCatalogueText(item, "project_id", "Fabric API")
            If CStr(item("project_id")) <> "P7dR8mSH" Then Throw New InvalidDataException("Fabric API 项目编号不匹配。")
            ' Like PCL's compatibility filter, verify each result before listing it.
            ' This endpoint supplies exact game/loader arrays instead of relying
            ' on the display-name version ranges used by PCL's global catalogue.
            If Not CatalogueContains(item, "game_versions", mc) OrElse Not CatalogueContains(item, "loaders", "fabric") Then Continue For
            For Each field In {"name", "version_number", "version_type"}
                ValidateCatalogueText(item, field, "Fabric API")
            Next
            Dim published = item("date_published"), instant As DateTimeOffset
            If published Is Nothing OrElse published.Type <> JTokenType.String OrElse
                Not DateTimeOffset.TryParse(CStr(published), Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.None, instant) Then Throw New InvalidDataException("Fabric API 发布时间无效。")
            Dim file = SelectFabricApiFile(item)
            If file Is Nothing Then Continue For ' PCL also omits versions without a download.
            SafeFiles.ValidateInstanceName(CStr(file("filename")))
            ValidateCatalogueText(file, "url", "Fabric API")
            Dim address As Uri = Nothing
            If Not Uri.TryCreate(CStr(file("url")), UriKind.Absolute, address) OrElse
                (address.Scheme <> "https" AndAlso Not (allowLoopback AndAlso address.IsLoopback AndAlso address.Scheme = "http")) Then Throw New InvalidDataException("Fabric API 下载地址无效。")
            ReadCatalogueInteger(file, "size", "Fabric API", 1)
            Dim hashes = TryCast(file("hashes"), JObject)
            If hashes Is Nothing Then Throw New InvalidDataException("Fabric API 校验信息格式无效。")
            Dim sha1 = hashes("sha1")
            If sha1 IsNot Nothing AndAlso sha1.Type <> JTokenType.Null AndAlso
                (sha1.Type <> JTokenType.String OrElse Not Regex.IsMatch(CStr(sha1), "\A[0-9a-fA-F]{40}\z")) Then Throw New InvalidDataException("Fabric API SHA-1 校验字段无效。")
            accepted.Add(item)
        Next
        ' PCL FabricApi_Loaded orders by publication time before auto-selection.
        Return New JArray(accepted.OrderByDescending(Function(item) DateTimeOffset.Parse(CStr(item("date_published")), Globalization.CultureInfo.InvariantCulture)))
    End Function

    Private Shared Function CatalogueContains(item As JObject, field As String, expected As String) As Boolean
        Dim values = TryCast(item(field), JArray)
        If values Is Nothing OrElse values.Any(Function(value) value.Type <> JTokenType.String) Then Throw New InvalidDataException("Fabric API 的 " & field & " 字段无效。")
        Return values.Values(Of String)().Any(Function(value) String.Equals(value, expected, StringComparison.OrdinalIgnoreCase))
    End Function

    Private Shared Function SelectFabricApiFile(item As JObject) As JObject
        Dim files = TryCast(item("files"), JArray)
        If files Is Nothing Then Throw New InvalidDataException("Fabric API 文件列表格式无效。")
        Dim primary As JObject = Nothing, fallback As JObject = Nothing
        For Each value In files
            Dim file = TryCast(value, JObject)
            If file Is Nothing Then Throw New InvalidDataException("Fabric API 文件条目格式无效。")
            ValidateCatalogueText(file, "filename", "Fabric API")
            Dim selected = file("primary")
            If selected IsNot Nothing AndAlso selected.Type <> JTokenType.Null AndAlso selected.Type <> JTokenType.Boolean Then Throw New InvalidDataException("Fabric API primary 字段无效。")
            If selected IsNot Nothing AndAlso selected.Type = JTokenType.Boolean AndAlso CBool(selected) Then
                If primary IsNot Nothing Then Throw New InvalidDataException("Fabric API 声明了多个主文件。")
                primary = file
            End If
            If fallback Is Nothing AndAlso IsRuntimeApiJar(CStr(file("filename"))) Then fallback = file
        Next
        If primary Is Nothing Then Return fallback
        If Not IsRuntimeApiJar(CStr(primary("filename"))) Then Throw New InvalidDataException("Fabric API 主文件不是运行用 JAR。")
        Return primary
    End Function

    Private Shared Function IsRuntimeApiJar(filename As String) As Boolean
        Return filename.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) AndAlso
            Not filename.EndsWith("-sources.jar", StringComparison.OrdinalIgnoreCase) AndAlso
            Not filename.EndsWith("-javadoc.jar", StringComparison.OrdinalIgnoreCase)
    End Function

    Private Shared Function ParseOptiFineMirrorMetadata(body As String) As JToken
        Dim entries = JArray.Parse(body)
        For Each value In entries
            Dim item = TryCast(value, JObject)
            If item Is Nothing Then Throw New InvalidDataException("OptiFine 版本条目格式无效。")
            For Each field In {"mcversion", "type", "patch", "filename"}
                ValidateCatalogueText(item, field, "OptiFine")
            Next
            Dim forge = item("forge")
            ' PCL accepts both dotted Forge versions and legacy numeric builds.
            If forge IsNot Nothing AndAlso forge.Type <> JTokenType.Null AndAlso forge.Type <> JTokenType.String AndAlso forge.Type <> JTokenType.Integer Then Throw New InvalidDataException("OptiFine 版本条目的 forge 字段无效。")
        Next
        Return entries
    End Function

    Private Shared Sub ValidateCatalogueText(item As JObject, field As String, catalogue As String)
        Dim value = item(field)
        If value Is Nothing OrElse value.Type <> JTokenType.String OrElse String.IsNullOrWhiteSpace(CStr(value)) Then Throw New InvalidDataException(catalogue & " 版本条目的 " & field & " 字段无效。")
    End Sub

    Private Shared Function SelectOptiFabricHash(item As JObject) As JObject
        ' PCL ResourceVersion: prefer SHA-1, otherwise use the declared MD5.
        Dim hashes = TryCast(item("hashes"), JArray)
        If hashes Is Nothing Then Return Nothing
        Return DirectCast(If(hashes.FirstOrDefault(Function(hash) CInt(hash("algo")) = 1),
                             hashes.FirstOrDefault(Function(hash) CInt(hash("algo")) = 2)), JObject)
    End Function

    Private Shared Function ParseOptiFabricPage(body As String, mc As String, acceptedIds As IEnumerable(Of Long)) As JToken
        Dim data = JObject.Parse(body)
        Dim files = TryCast(data("data"), JArray)
        If files Is Nothing OrElse files.Count > 50 Then Throw New InvalidDataException("OptiFabric 分页格式或大小无效。")
        ' Each racing source owns its tentative IDs. Only the accepted page may
        ' update the caller's set after all losing requests have finished.
        Dim ids As New HashSet(Of Long)(acceptedIds)
        For Each value In files
            Dim item = TryCast(value, JObject)
            If item Is Nothing Then Throw New InvalidDataException("OptiFabric 文件条目格式无效。")
            Dim id = ReadCatalogueInteger(item, "id", "OptiFabric", 1)
            If Not ids.Add(id) Then Throw New InvalidDataException("OptiFabric 列表返回了重复的分页。")
            Dim games = TryCast(item("gameVersions"), JArray)
            If games Is Nothing OrElse games.Any(Function(game) game.Type <> JTokenType.String) Then Throw New InvalidDataException("OptiFabric 游戏版本列表格式无效。")
            If Not games.Values(Of String)().Contains(mc) Then Continue For
            If item("projectId") IsNot Nothing AndAlso ReadCatalogueInteger(item, "projectId", "OptiFabric", 1) <> 322385 Then Throw New InvalidDataException("OptiFabric 项目编号不匹配。")
            ValidateCatalogueText(item, "fileName", "OptiFabric")
            ValidateCatalogueText(item, "displayName", "OptiFabric")
            ReadCatalogueInteger(item, "fileLength", "OptiFabric", -1)
            If ReadCatalogueInteger(item, "releaseType", "OptiFabric") > Integer.MaxValue Then Throw New InvalidDataException("OptiFabric 发布类型无效。")
            Dim url = item("downloadUrl")
            If url IsNot Nothing AndAlso url.Type <> JTokenType.Null AndAlso url.Type <> JTokenType.String Then Throw New InvalidDataException("OptiFabric 下载地址字段无效。")
            Dim hashes = item("hashes")
            If hashes Is Nothing OrElse hashes.Type = JTokenType.Null Then Continue For
            If Not TypeOf hashes Is JArray Then Throw New InvalidDataException("OptiFabric 校验列表格式无效。")
            For Each hashValue In hashes
                Dim hash = TryCast(hashValue, JObject)
                If hash Is Nothing Then Throw New InvalidDataException("OptiFabric 校验条目格式无效。")
                Dim algorithm = ReadCatalogueInteger(hash, "algo", "OptiFabric")
                If algorithm > Integer.MaxValue Then Throw New InvalidDataException("OptiFabric 校验类型无效。")
                If algorithm <> 1 Then Continue For
                ValidateCatalogueText(hash, "value", "OptiFabric")
                If Not Regex.IsMatch(CStr(hash("value")), "\A[0-9a-fA-F]{40}\z") Then Throw New InvalidDataException("OptiFabric SHA-1 校验字段无效。")
            Next
            Dim selected = SelectOptiFabricHash(item)
            If selected IsNot Nothing AndAlso CInt(selected("algo")) = 2 Then
                ValidateCatalogueText(selected, "value", "OptiFabric")
                If Not Regex.IsMatch(CStr(selected("value")), "\A[0-9a-fA-F]{32}\z") Then Throw New InvalidDataException("OptiFabric MD5 校验字段无效。")
            End If
        Next
        Return data
    End Function

    Private Shared Function ReadCatalogueInteger(item As JObject, field As String, catalogue As String, Optional minimum As Long = 0) As Long
        Dim value = item(field)
        If value Is Nothing OrElse value.Type <> JTokenType.Integer OrElse CLng(value) < minimum Then Throw New InvalidDataException(catalogue & " 版本条目的 " & field & " 字段无效。")
        Return CLng(value)
    End Function

    Private Shared Function ParseLiteLoaderMetadata(body As String, mc As String) As JToken
        Dim data = JObject.Parse(body)
        Dim versions = TryCast(data("versions"), JObject)
        If versions Is Nothing Then Throw New InvalidDataException("LiteLoader 缺少 versions 对象。")
        If versions(mc) Is Nothing Then Return data
        Dim game = TryCast(versions(mc), JObject)
        Dim releases = If(game Is Nothing, Nothing, TryCast(If(game("artefacts"), game("snapshots")), JObject))
        Dim package = If(releases Is Nothing, Nothing, TryCast(releases("com.mumfrey:liteloader"), JObject))
        Dim latest = If(package Is Nothing, Nothing, TryCast(package("latest"), JObject))
        If latest Is Nothing Then Throw New InvalidDataException("LiteLoader 所选游戏版本缺少 latest 对象。")
        For Each field In {"version", "stream", "tweakClass"}
            ValidateCatalogueText(latest, field, "LiteLoader")
        Next
        Dim libraries = TryCast(latest("libraries"), JArray)
        If libraries Is Nothing OrElse libraries.Any(Function(library) Not TypeOf library Is JObject) Then Throw New InvalidDataException("LiteLoader 支持库列表格式无效。")
        Return data
    End Function

    Private Shared Function ParseForgeMirrorMetadata(body As String) As JToken
        Dim entries = JArray.Parse(body)
        For Each value In entries
            Dim item = TryCast(value, JObject)
            If item Is Nothing Then Throw New InvalidDataException("Forge 版本条目格式无效。")
            Dim files = TryCast(item("files"), JArray)
            If files Is Nothing Then Throw New InvalidDataException("Forge 文件列表格式无效。")
            For Each file As JObject In files
                If CStr(file("category")) <> "installer" OrElse CStr(file("format")) <> "jar" Then Continue For
                Dim hash = file("hash")
                If hash Is Nothing OrElse hash.Type = JTokenType.Null Then Continue For
                If hash.Type <> JTokenType.String Then Throw New InvalidDataException("Forge 安装器校验字段格式无效。")
                Dim checksum = CStr(hash).Trim()
                If checksum.Length > 0 AndAlso Not ForgelikeInstallerChecksum.IsChecksum(checksum) Then Throw New InvalidDataException("Forge 安装器校验字段格式无效。")
                file("hash") = checksum
            Next
        Next
        Return entries
    End Function

    Private Shared Function ParseForgeMetadata(body As String, mc As String) As JToken
        Dim xml = XDocument.Parse(body)
        If xml.Root Is Nothing OrElse xml.Root.Name.LocalName <> "metadata" Then Throw New InvalidDataException("Forge Maven 版本列表无效。")
        Dim result As New JArray()
        Dim game = mc.Replace("-", "_")
        For Each value In xml.Descendants("version").Select(Function(x) x.Value).Where(Function(v) v.StartsWith(game & "-", StringComparison.Ordinal))
            Dim release = ForgeRelease.Parse(value.Substring(game.Length + 1), Nothing, mc)
            If release Is Nothing Then Continue For
            result.Add(New JObject From {{"version", release.VersionName}, {"branch", release.Branch}, {"files", New JArray(New JObject From {{"category", "installer"}, {"format", "jar"}})}})
        Next
        Return result
    End Function
    Private Shared Function ParseNeoForgeMetadata(body As String, Optional legacy As Boolean = False) As JToken
        ' Same version-name extraction as PCL GetNeoForgeEntries, across official
        ' versions arrays and BMCLAPI's Maven details response.
        JToken.Parse(body)
        Dim values = Regex.Matches(body, "(?<="")" & NeoForgeRelease.Pattern & "(?="")", RegexOptions.IgnoreCase).Cast(Of Match)().Select(Function(m) m.Value).Where(Function(value) NeoForgeRelease.Parse(value, legacy) IsNot Nothing).Distinct().ToList()
        If values.Count = 0 Then Throw New InvalidDataException("NeoForge 版本列表为空或格式无效。")
        Return New JObject From {{"versions", New JArray(values)}}
    End Function

    Public Shared Function SortKey(value As String) As String
        Return Regex.Replace(value, "\d+", Function(m) m.Value.PadLeft(8, "0"c))
    End Function
    Public Shared Function ParseOptiFinePage(page As String) As JArray
        ' PCL DlOptiFineListOfficialMain: official filenames and colForge values.
        Dim names = Regex.Matches(page, "(?<=OptiFine_)[0-9A-Za-z_.]+(?=\.jar[""'])").Cast(Of Match)().Select(Function(m) m.Value).ToList()
        Dim forge = Regex.Matches(page, "(?<=colForge'>)[^<]*").Cast(Of Match)().Select(Function(m) m.Value).ToList()
        If names.Count = 0 Then Throw New InvalidDataException("OptiFine 官方版本列表解析失败。")
        Dim entries As New JArray
        Dim seen As New HashSet(Of String)
        For i = 0 To names.Count - 1
            Dim parts = names(i).Split("_"c)
            If parts.Length < 4 OrElse Not seen.Add(names(i)) Then Continue For
            Dim preview = names(i).Contains("_pre")
            Dim type = String.Join("_", parts.Skip(1).Take(parts.Length - 2))
            Dim fileName = If(preview, "preview_", "") & "OptiFine_" & names(i) & ".jar"
            entries.Add(New JObject From {{"mcversion", parts(0)}, {"type", type}, {"patch", parts.Last()}, {"filename", fileName}, {"forge", If(forge.Count = names.Count, forge(i), "N/A")}})
        Next
        Return entries
    End Function
End Class
