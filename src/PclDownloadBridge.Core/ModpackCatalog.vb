Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Newtonsoft.Json.Linq

<Flags>
Public Enum ModpackPlatform
    CurseForge = 1
    Modrinth = 2
    All = 3
End Enum

' PCL 5ef81e4 PageResource / ResourceSearcher / ResourceVersion. Catalogue
' identities are platform-qualified and never inferred from a display name.
Public NotInheritable Class ModpackQuery
    Public Property Kind As CommunityResourceKind = CommunityResourceKind.Modpack
    Public Property Loader As String = ""
    Public Property Text As String = ""
    Public Property Minecraft As String = ""
    Public Property Category As String = "/"
    Public Property Platforms As ModpackPlatform = ModpackPlatform.All
    Public Function Copy() As ModpackQuery
        Return DirectCast(MemberwiseClone(), ModpackQuery)
    End Function
End Class

Public NotInheritable Class ModpackProject
    Public Property Kind As CommunityResourceKind = CommunityResourceKind.Modpack
    Public Property PreferredVersionId As String
    Public Property Platform As ModpackPlatform
    Public Property Id As String
    Public Property Name As String
    Public Property Slug As String
    Public Property Summary As String
    Public Property IconUrl As String
    Public Property Website As String
    Public Property Downloads As Long
    Public Property Updated As DateTimeOffset
    Public Property GameVersions As String() = {}
    ' Advisory project declarations. File compatibility and install identity
    ' continue to come exclusively from the selected ModpackRelease/archive.
    Public Property Tags As String() = {}
    Public Property Loaders As String() = {}
    Public ReadOnly Property Key As String
        Get
            Return Platform.ToString() & ":" & Id & If(Kind = CommunityResourceKind.Modpack, "", ":" & Kind.ToString())
        End Get
    End Property
End Class

Public NotInheritable Class ModpackRelease
    Public Property Kind As CommunityResourceKind = CommunityResourceKind.Modpack
    Public Property Dependencies As ResourceDependency() = {}
    Public Property Platform As ModpackPlatform
    Public Property ProjectId As String
    Public Property Id As String
    Public Property Name As String
    Public Property Version As String
    Public Property Published As DateTimeOffset
    ' Advisory platform statistics; absent or malformed counters cannot invalidate
    ' an otherwise verified archive. A known zero remains distinct from unknown.
    Public Property Downloads As Long?
    Public Property ReleaseType As String
    Public Property GameVersions As String() = {}
    ' Missing compatibility metadata may defer to the archive, but an explicitly
    ' unusable declaration must remain distinguishable from missing metadata.
    Public Property GameVersionMetadataError As String
    Public Property Loaders As String() = {}
    Public Property FileName As String
    Public Property Url As String
    Public Property Size As Long
    Public Property Sha1 As String
    Public Property Sha512 As String
    Public Property Md5 As String
    Public Function Copy() As ModpackRelease
        Dim result = DirectCast(MemberwiseClone(), ModpackRelease)
        result.GameVersions = DirectCast(GameVersions.Clone(), String())
        result.Loaders = DirectCast(Loaders.Clone(), String())
        result.Dependencies = Dependencies.Select(Function(item) New ResourceDependency With {.ProjectId = item.ProjectId, .VersionId = item.VersionId}).ToArray()
        Return result
    End Function
End Class

Public NotInheritable Class ModpackCatalogPage
    Public Property Projects As New List(Of ModpackProject)
    Public Property Returned As Integer
    Public Property Total As Long
End Class

Public NotInheritable Class ModpackCatalogClient
    Implements IDisposable
    Public Const PageSize As Integer = 40
    Private ReadOnly _client As HttpClient
    Private ReadOnly _key As String
    Private ReadOnly _communitySource As Integer
    Public Sub New(Optional handler As HttpMessageHandler = Nothing, Optional apiKey As String = Nothing, Optional communitySource As Integer = 2)
        _client = New HttpClient(If(handler, New HttpClientHandler With {.AllowAutoRedirect = False, .AutomaticDecompression = DecompressionMethods.GZip Or DecompressionMethods.Deflate, .MaxConnectionsPerServer = 4})) With {.Timeout = Timeout.InfiniteTimeSpan}
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("PclDownloadBridge/5.0.0-beta.1 (modpack catalogue)")
        _key = If(apiKey, Environment.GetEnvironmentVariable("PCLDB_CURSEFORGE_API_KEY"))
        If communitySource < 0 OrElse communitySource > 2 Then Throw New ArgumentOutOfRangeException(NameOf(communitySource))
        _communitySource = communitySource
    End Sub

    Public Shared Function SearchUrl(platform As ModpackPlatform, query As ModpackQuery, offset As Integer) As String
        If query IsNot Nothing Then
            CommunityResources.Name(query.Kind)
            If query.Loader Is Nothing OrElse Not {"", "fabric", "forge", "neoforge", "quilt", "liteloader"}.Contains(query.Loader, StringComparer.Ordinal) Then Throw New ArgumentException("加载器无效。")
        End If
        If query Is Nothing OrElse offset < 0 OrElse query.Text Is Nothing OrElse query.Category Is Nothing OrElse query.Minecraft Is Nothing OrElse CInt(query.Platforms) < 1 OrElse CInt(query.Platforms) > 3 Then Throw New ArgumentException("搜索条件无效。")
        Dim categories = query.Category.Split("/"c)
        If categories.Length <> 2 OrElse query.Text.Length > 256 OrElse query.Minecraft.Length > 100 Then Throw New ArgumentException("搜索条件过长或分类无效。")
        Dim text = Uri.EscapeDataString(query.Text.Trim()), game = Uri.EscapeDataString(query.Minecraft.Trim())
        If platform = ModpackPlatform.CurseForge Then
            Dim url = "https://api.curseforge.com/v1/mods/search?gameId=432&classId=" & CommunityResources.ClassId(query.Kind).ToString() & "&sortField=2&sortOrder=desc&pageSize=40&index=" & offset.ToString(Globalization.CultureInfo.InvariantCulture)
            If query.Loader <> "" Then url &= "&modLoaderType=" & New Dictionary(Of String, String) From {{"forge", "1"}, {"liteloader", "3"}, {"fabric", "4"}, {"quilt", "5"}, {"neoforge", "6"}}(query.Loader)
            If text <> "" Then url &= "&searchFilter=" & text
            If game <> "" Then url &= "&gameVersion=" & game
            If categories(0) <> "" Then url &= "&categoryId=" & Uri.EscapeDataString(categories(0))
            Return url
        End If
        If platform <> ModpackPlatform.Modrinth Then Throw New ArgumentException("请选择一个内容平台。")
        Dim facets As New JArray()
        facets.Add(New JArray("project_type:" & CommunityResources.ProjectType(query.Kind)))
        If query.Loader <> "" Then facets.Add(New JArray("categories:" & query.Loader))
        If categories(1) <> "" Then facets.Add(New JArray("categories:" & categories(1)))
        If game <> "" Then facets.Add(New JArray("versions:" & query.Minecraft.Trim()))
        Return "https://api.modrinth.com/v2/search?limit=40&index=relevance&offset=" & offset.ToString(Globalization.CultureInfo.InvariantCulture) & "&query=" & text & "&facets=" & Uri.EscapeDataString(facets.ToString(Newtonsoft.Json.Formatting.None))
    End Function

    Public Async Function SearchAsync(platform As ModpackPlatform, query As ModpackQuery, offset As Integer, token As CancellationToken) As Task(Of ModpackCatalogPage)
        Dim data = Await JsonAsync(SearchUrl(platform, query, offset), token).ConfigureAwait(False)
        Dim items = ArrayValue(data, If(platform = ModpackPlatform.Modrinth, "hits", "data"))
        If items.Count > PageSize Then Throw New InvalidDataException("搜索返回的条目数超过请求上限。")
        Dim result As New ModpackCatalogPage With {.Returned = items.Count}
        result.Total = If(platform = ModpackPlatform.Modrinth, Number(data, "total_hits"), Number(ObjectValue(data, "pagination"), "totalCount"))
        For Each item In items.OfType(Of JObject)()
            If Not CommunityResources.Matches(platform, item, query.Kind) Then Continue For
            Dim project = ParseProject(platform, item, True, query.Kind)
            FixResourceWebsite(project, item)
            result.Projects.Add(project)
        Next
        Return result
    End Function

    Public Async Function GetProjectAsync(platform As ModpackPlatform, id As String, token As CancellationToken, Optional kind As CommunityResourceKind = CommunityResourceKind.Modpack) As Task(Of ModpackProject)
        ValidateId(platform, id)
        Dim url = If(platform = ModpackPlatform.Modrinth, "https://api.modrinth.com/v2/project/", "https://api.curseforge.com/v1/mods/") & id
        Dim data = Await JsonAsync(url, token).ConfigureAwait(False)
        If platform = ModpackPlatform.CurseForge Then data = ObjectValue(data, "data")
        If Not CommunityResources.Matches(platform, data, kind) Then Throw New InvalidDataException("项目的资源类型不匹配。")
        Dim result = ParseProject(platform, data, False, kind)
        FixResourceWebsite(result, data)
        If result.Id <> id Then Throw New InvalidDataException("返回的整合包项目 ID 不匹配。")
        Return result
    End Function

    Private Shared Sub FixResourceWebsite(project As ModpackProject, data As JObject)
        If project.Kind = CommunityResourceKind.Modpack Then Return
        Dim slug = Uri.EscapeDataString(Text(data, "slug"))
        If project.Platform = ModpackPlatform.Modrinth Then
            project.Website = "https://modrinth.com/" & If(project.Kind = CommunityResourceKind.DataPack, "datapack", CommunityResources.ProjectType(project.Kind)) & "/" & slug
        Else
            Dim section = If(project.Kind = CommunityResourceKind.Mod, "mc-mods", If(project.Kind = CommunityResourceKind.ResourcePack, "texture-packs", If(project.Kind = CommunityResourceKind.Shader, "shaders", "data-packs")))
            project.Website = "https://www.curseforge.com/minecraft/" & section & "/" & slug
        End If
    End Sub

    Public Async Function ResolveDependencyAsync(platform As ModpackPlatform, dependency As ResourceDependency, token As CancellationToken) As Task(Of ModpackProject)
        Dim id = dependency.ProjectId
        Dim kind = CommunityResourceKind.Any
        If platform = ModpackPlatform.Modrinth AndAlso Not String.IsNullOrEmpty(dependency.VersionId) Then
            ValidateId(platform, dependency.VersionId)
            Dim version = Await JsonAsync("https://api.modrinth.com/v2/version/" & dependency.VersionId, token).ConfigureAwait(False)
            If Text(version, "id") <> dependency.VersionId OrElse (Not String.IsNullOrEmpty(id) AndAlso Text(version, "project_id") <> id) Then Throw New InvalidDataException("前置版本所属项目不匹配。")
            id = Text(version, "project_id")
            Dim loaders = TryCast(version("loaders"), JArray)
            If loaders IsNot Nothing AndAlso loaders.Values(Of String)().Contains("datapack") AndAlso Not loaders.Values(Of String)().Any(Function(value) {"fabric", "forge", "neoforge", "quilt", "liteloader"}.Contains(value)) Then kind = CommunityResourceKind.DataPack
        End If
        Dim project = Await GetProjectAsync(platform, id, token, kind).ConfigureAwait(False)
        project.PreferredVersionId = dependency.VersionId
        Return project
    End Function

    Public Async Function GetVersionsAsync(project As ModpackProject, token As CancellationToken) As Task(Of List(Of ModpackRelease))
        ValidateId(project.Platform, project.Id)
        Dim result As New List(Of ModpackRelease), seen As New HashSet(Of String)(StringComparer.Ordinal), fetched As New HashSet(Of String)(StringComparer.Ordinal)
        Dim offset = 0
        For page = 0 To 99
            token.ThrowIfCancellationRequested()
            Dim items As JArray, total As Long
            If project.Platform = ModpackPlatform.Modrinth Then
                ' PCL requests all versions, then builds the game-version filter locally.
                Dim root = Await TokenAsync("https://api.modrinth.com/v2/project/" & project.Id & "/version?include_changelog=false", token).ConfigureAwait(False)
                items = TryCast(root, JArray)
                If items Is Nothing Then Throw New InvalidDataException("Modrinth 版本列表无效。")
                total = items.Count
            Else
                Dim root = Await JsonAsync("https://api.curseforge.com/v1/mods/" & project.Id & "/files?pageSize=50&index=" & offset.ToString(Globalization.CultureInfo.InvariantCulture), token).ConfigureAwait(False)
                items = ArrayValue(root, "data") : total = Number(ObjectValue(root, "pagination"), "totalCount")
                If items.Count > 50 Then Throw New InvalidDataException("CurseForge 版本分页无效。")
            End If
            Dim rawIds As New HashSet(Of String)(StringComparer.Ordinal)
            For Each item In items.OfType(Of JObject)()
                Dim release = ParseRelease(project, item)
                rawIds.Add(Text(item, "id"))
                If release IsNot Nothing AndAlso (String.IsNullOrEmpty(project.PreferredVersionId) OrElse release.Id = project.PreferredVersionId) AndAlso seen.Add(release.Id) Then result.Add(release)
            Next
            offset += items.Count
            If rawIds.Count > 0 AndAlso rawIds.All(Function(id) fetched.Contains(id)) Then Throw New InvalidDataException("整合包版本分页返回了重复结果，请重试。")
            fetched.UnionWith(rawIds)
            If project.Platform = ModpackPlatform.Modrinth OrElse offset >= total OrElse items.Count = 0 Then Exit For
            If rawIds.Count = 0 Then Throw New InvalidDataException("整合包版本分页没有前进，请重试。")
            If page = 99 Then Throw New InvalidDataException("整合包版本过多，请缩小查询范围。")
        Next
        Return result.OrderByDescending(Function(item) item.Published).ToList()
    End Function

    Public Shared Function ParseRelease(project As ModpackProject, data As JObject) As ModpackRelease
        Dim result As New ModpackRelease With {.Kind = project.Kind, .Platform = project.Platform, .ProjectId = project.Id, .Id = Text(data, "id"), .Name = Text(data, If(project.Platform = ModpackPlatform.Modrinth, "name", "displayName"))}
        ValidateId(project.Platform, result.Id)
        If project.Platform = ModpackPlatform.Modrinth Then
            If Text(data, "project_id") <> project.Id Then Throw New InvalidDataException("Modrinth 版本所属项目不匹配。")
            If Text(data, "status", False) <> "" AndAlso Text(data, "status") <> "listed" AndAlso Text(data, "status") <> "archived" Then Return Nothing
            If Not CommunityResources.FileKind(project, Strings(data, "loaders")).HasValue Then Return Nothing
            Dim candidates = ArrayValue(data, "files").OfType(Of JObject)().Where(Function(file) If(project.Kind = CommunityResourceKind.Modpack, Text(file, "filename").EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase), CommunityResources.AcceptsFile(project.Kind, Text(file, "filename"))) AndAlso Not {"sources-jar", "dev-jar", "javadoc-jar", "signature"}.Contains(Text(file, "file_type", False))).ToList()
            Dim primary = candidates.Where(Function(file) CBool(If(file("primary"), New JValue(False)))).ToList()
            Dim chosen = If(primary.Count = 1, primary(0), If(candidates.Count = 1, candidates(0), Nothing))
            If chosen Is Nothing Then Return Nothing
            result.FileName = Text(chosen, "filename") : result.Url = Text(chosen, "url") : result.Size = Number(chosen, "size")
            Dim hashes = ObjectValue(chosen, "hashes")
            result.Sha1 = Digest(hashes, "sha1", 40) : result.Sha512 = Digest(hashes, "sha512", 128)
            result.Version = Text(data, "version_number") : result.Published = DateValue(data, "date_published")
            result.ReleaseType = Text(data, "version_type") : result.GameVersions = Strings(data, "game_versions") : result.Loaders = Strings(data, "loaders")
            If project.Kind <> CommunityResourceKind.Modpack Then result.Dependencies = ReadDependencies(project.Platform, data)
        Else
            If Text(data, "modId") <> project.Id Then Throw New InvalidDataException("CurseForge 文件所属项目不匹配。")
            If Flag(data, "isServerPack", False) Then Return Nothing
            If Not Flag(data, "isAvailable", True) Then Return Nothing
            If Flag(data, "isEarlyAccessContent", False) Then Return Nothing
            If data("fileStatus") IsNot Nothing AndAlso Number(data, "fileStatus") <> 4 Then Return Nothing
            result.FileName = Text(data, "fileName")
            If Not CommunityResources.AcceptsFile(project.Kind, result.FileName) OrElse (project.Kind = CommunityResourceKind.Modpack AndAlso Not result.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) Then Return Nothing
            result.Url = Text(data, "downloadUrl", False)
            If result.Url = "" Then
                ' PCL ResourceVersion.FromPlatformJson retains public files whose
                ' Core API downloadUrl is null. Preserve exact file identity and
                ' escaping; the normal size, hash and CDN path checks still apply.
                Dim fileIdNumber = Long.Parse(result.Id, Globalization.CultureInfo.InvariantCulture)
                result.Url = "https://edge.forgecdn.net/files/" & (fileIdNumber \ 1000).ToString(Globalization.CultureInfo.InvariantCulture) & "/" &
                    (fileIdNumber Mod 1000).ToString(Globalization.CultureInfo.InvariantCulture) & "/" & Uri.EscapeDataString(result.FileName)
            End If
            result.Size = Number(data, "fileLength") : result.Version = result.Name : result.Published = DateValue(data, "fileDate")
            result.ReleaseType = If(Number(data, "releaseType") = 1, "release", If(Number(data, "releaseType") = 2, "beta", "alpha"))
            Dim raw = ReadCurseForgeTags(data("gameVersions"), result)
            result.Loaders = raw.Where(Function(value) {"Forge", "NeoForge", "Fabric", "Quilt"}.Contains(value, StringComparer.OrdinalIgnoreCase)).ToArray()
            If project.Kind <> CommunityResourceKind.Modpack Then
                result.GameVersionMetadataError = Nothing
                result.Dependencies = ReadDependencies(project.Platform, data)
            End If
            For Each hash In ArrayValue(data, "hashes").OfType(Of JObject)()
                Dim algorithm = Number(hash, "algo")
                If algorithm = 1 Then
                    Dim sha1 = Digest(New JObject From {{"sha1", Text(hash, "value")}}, "sha1", 40)
                    If result.Sha1 IsNot Nothing AndAlso result.Sha1 <> sha1 Then Throw New InvalidDataException("归档 SHA-1 声明冲突。")
                    result.Sha1 = sha1
                End If
                If algorithm = 2 Then
                    Dim md5 = Digest(New JObject From {{"md5", Text(hash, "value")}}, "md5", 32)
                    If result.Md5 IsNot Nothing AndAlso result.Md5 <> md5 Then Throw New InvalidDataException("归档 MD5 声明冲突。")
                    result.Md5 = md5
                End If
            Next
        End If
        result.Downloads = ReadDownloadCount(data(If(project.Platform = ModpackPlatform.Modrinth, "downloads", "downloadCount")))
        ' The archive is stored under a fixed cache name, not the remote filename.
        ' Keep ordinary long archive names while rejecting path-bearing metadata.
        If result.FileName.Length > 255 OrElse result.FileName <> result.FileName.Trim() OrElse result.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 Then Throw New InvalidDataException("整合包归档文件名无效。")
        If result.Kind <> CommunityResourceKind.Modpack Then CommunityResources.ValidateFileName(result.Kind, result.FileName)
        If result.Size <= 0 Then Return Nothing
        If result.Sha1 Is Nothing AndAlso result.Sha512 Is Nothing AndAlso result.Md5 Is Nothing Then Return Nothing
        ModpackArchivePolicy.Validate(result.Url, result.Platform, False)
        If result.Platform = ModpackPlatform.CurseForge Then
            Dim id = Long.Parse(result.Id, Globalization.CultureInfo.InvariantCulture), address As New Uri(result.Url)
            Dim expected = "/files/" & (id \ 1000).ToString(Globalization.CultureInfo.InvariantCulture) & "/" & (id Mod 1000).ToString(Globalization.CultureInfo.InvariantCulture) & "/" & result.FileName
            If Uri.UnescapeDataString(address.AbsolutePath) <> expected Then Throw New InvalidDataException("CurseForge 下载地址与指定文件不匹配。")
        End If
        Return result
    End Function

    Private Shared Function ReadDependencies(platform As ModpackPlatform, data As JObject) As ResourceDependency()
        Dim result As New List(Of ResourceDependency)
        Dim values = TryCast(data("dependencies"), JArray)
        If values Is Nothing Then Return {}
        If values.Count > 256 Then Throw New InvalidDataException("前置声明过多。")
        For Each item In values.OfType(Of JObject)()
            If platform = ModpackPlatform.Modrinth Then
                If Text(item, "dependency_type", False) <> "required" Then Continue For
                Dim project = Text(item, "project_id", False), version = Text(item, "version_id", False)
                If project <> "" Then ValidateId(platform, project)
                If version <> "" Then ValidateId(platform, version)
                If project <> "" OrElse version <> "" Then result.Add(New ResourceDependency With {.ProjectId = project, .VersionId = version})
            ElseIf Number(item, "relationType") = 3 Then
                Dim id = Text(item, "modId") : ValidateId(platform, id)
                result.Add(New ResourceDependency With {.ProjectId = id})
            End If
        Next
        Dim owner = Text(data, If(platform = ModpackPlatform.Modrinth, "project_id", "modId")), versionId = Text(data, "id")
        Return result.Where(Function(item) item.ProjectId <> owner AndAlso (platform <> ModpackPlatform.Modrinth OrElse item.VersionId <> versionId)).
            GroupBy(Function(item) item.ProjectId & ":" & item.VersionId, StringComparer.Ordinal).Select(Function(group) group.First()).ToArray()
    End Function

    Private Shared Function ReadDownloadCount(value As JToken) As Long?
        If value Is Nothing OrElse (value.Type <> JTokenType.Integer AndAlso value.Type <> JTokenType.Float) Then Return Nothing
        Dim number As Decimal
        If Not Decimal.TryParse(value.ToString(Newtonsoft.Json.Formatting.None), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, number) OrElse
            number < 0 OrElse number > Long.MaxValue OrElse Decimal.Truncate(number) <> number Then Return Nothing
        Return CLng(number)
    End Function

    Private Shared Function ReadCurseForgeTags(declaration As JToken, release As ModpackRelease) As String()
        ' Fixed PCL ResourceVersion.FromPlatformJson distinguishes loader labels
        ' from Minecraft versions, then normalizes previews for display. Here the
        ' original exact IDs must survive for the archive/manifest identity check.
        If declaration Is Nothing OrElse declaration.Type = JTokenType.Null Then Return {}
        Dim values = TryCast(declaration, JArray)
        If values Is Nothing OrElse values.Any(Function(value) value.Type <> JTokenType.String OrElse CStr(value).Length > 100) Then
            release.GameVersionMetadataError = "CurseForge 文件的 Minecraft 版本声明无效，无法核对整合包清单。请刷新重试或选择其他版本。"
            Return {}
        End If
        Dim tags = values.Values(Of String)().Select(Function(value) value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        release.GameVersions = tags.Where(AddressOf IsExactMinecraftVersion).ToArray()
        If release.GameVersions.Length = 0 Then
            release.GameVersionMetadataError = "CurseForge 文件未声明可识别的 Minecraft 版本，无法核对整合包清单。请刷新重试或选择其他版本。"
        End If
        Return tags
    End Function

    Friend Shared Function IsExactMinecraftVersion(value As String) As Boolean
        ' McVersion.IsStandardFormat / GetVanillaName at PCL 5ef81e4 are the
        ' modern/pre/rc/year-snapshot basis. Anchor the entire tag: never extract
        ' a version-like substring from Forge, Java or other mixed labels.
        Const modern = "\A(?:1\.[0-9]+(?:\.[0-9]+)?|(?:2[6-9]|[3-9][0-9])\.[0-9]+(?:\.[0-9]+)?)(?:-(?:pre|rc|snapshot-?)[1-9][0-9]*| Pre-Release(?: [1-9][0-9]*)?)?(?:_unobfuscated)?\z"
        Const weekly = "\A[0-9]{2}w[0-9]{2}[a-z]\z"
        Const historical = "\A(?:[ab]1\.[0-9]+(?:\.[0-9]+)?(?:_[0-9]+[a-z]?)?(?:-?pre[1-9][0-9]*)?|c0\.[0-9]+(?:\.[0-9]+)?[a-z]?(?:_[0-9]+[a-z]?)?|(?:in|inf|rd)-[0-9]{6,8})\z"
        Dim options = RegexOptions.IgnoreCase Or RegexOptions.CultureInvariant
        Return Regex.IsMatch(value, modern, options) OrElse Regex.IsMatch(value, weekly, options) OrElse
            Regex.IsMatch(value, historical, options) OrElse value = "2.0"
    End Function

    Private Shared Function ParseProject(platform As ModpackPlatform, data As JObject, search As Boolean, kind As CommunityResourceKind) As ModpackProject
        Dim result As New ModpackProject With {.Platform = platform, .Kind = If(kind = CommunityResourceKind.Any, CommunityResources.Infer(platform, data), kind)}
        If platform = ModpackPlatform.Modrinth Then
            result.Id = Text(data, If(search, "project_id", "id")) : result.Name = Text(data, "title") : result.Summary = Text(data, "description", False)
            result.IconUrl = Text(data, "icon_url", False) : result.Website = "https://modrinth.com/modpack/" & Uri.EscapeDataString(Text(data, "slug"))
            result.Downloads = Number(data, "downloads") : result.Updated = DateValue(data, If(search, "date_modified", "updated"))
            result.GameVersions = Strings(data, If(search, "versions", "game_versions"))
        Else
            result.Id = Text(data, "id") : result.Name = Text(data, "name") : result.Summary = Text(data, "summary", False)
            result.IconUrl = If(TryCast(data("logo"), JObject) Is Nothing, "", Text(DirectCast(data("logo"), JObject), "thumbnailUrl", False))
            result.Website = "https://www.curseforge.com/minecraft/modpacks/" & Uri.EscapeDataString(Text(data, "slug"))
            result.Downloads = Number(data, "downloadCount") : result.Updated = DateValue(data, "dateModified")
        End If
        ValidateId(platform, result.Id)
        result.Slug = Text(data, "slug")
        If platform = ModpackPlatform.CurseForge Then result.GameVersions = ModpackProjectMetadata.ReadVersions(result, data)
        result.Tags = ModpackProjectMetadata.ReadTags(platform, data("categories"), result.Kind)
        result.Loaders = ModpackProjectMetadata.ReadLoaders(result, data)
        Return result
    End Function

    Private Async Function JsonAsync(url As String, token As CancellationToken) As Task(Of JObject)
        Dim result = TryCast(Await TokenAsync(url, token).ConfigureAwait(False), JObject)
        If result Is Nothing Then Throw New InvalidDataException("平台返回的 JSON 对象无效。")
        Return result
    End Function
    Private Async Function TokenAsync(url As String, token As CancellationToken) As Task(Of JToken)
        Dim urls As New List(Of String) From {url}
        If url.StartsWith("https://api.curseforge.com/", StringComparison.Ordinal) Then
            urls.Clear()
            If Not String.IsNullOrWhiteSpace(_key) Then urls.Add(url)
            ' PCL DlModRequest / DlSourceModGet use the Core API and its MCIM
            ' mirror. Website /api/v1 file lists have projectId/dateCreated and
            ' omit downloadUrl/hashes, so HTTP 200 there is not a Core response.
            urls.Add(url.Replace("https://api.curseforge.com/", "https://mod.mcimirror.top/curseforge/"))
        End If
        If url.StartsWith("https://api.modrinth.com/", StringComparison.Ordinal) Then urls.Add(url.Replace("https://api.modrinth.com/", "https://mod.mcimirror.top/modrinth/"))
        If _communitySource = 0 Then urls = urls.OrderByDescending(Function(address) New Uri(address).Host = "mod.mcimirror.top").ToList()
        Dim failures As New List(Of String)
        For Each address In urls
            Dim mirror = New Uri(address).Host = "mod.mcimirror.top"
            For attempt = 1 To If(mirror, 2, 1)
                token.ThrowIfCancellationRequested()
                Dim retry = False
                Try
                    Return Await FetchTokenAsync(address, If(mirror OrElse _communitySource = 1, 10, If(_communitySource = 2, 30, 15)), token).ConfigureAwait(False)
                Catch ex As Exception When Not token.IsCancellationRequested
                    failures.Add(New Uri(address).Host & "：" & If(TypeOf ex Is OperationCanceledException, "请求超时", ex.GetBaseException().Message))
                    Dim http = TryCast(ex, CatalogueHttpException)
                    retry = If(http IsNot Nothing, http.Transient, TypeOf ex Is HttpRequestException OrElse TypeOf ex Is OperationCanceledException OrElse
                        (TypeOf ex Is IOException AndAlso Not TypeOf ex Is InvalidDataException))
                End Try
                If Not retry OrElse Not mirror OrElse attempt = 2 Then Exit For
                ' Fixed PCL DlModRequest repeats its ten-second MCIM request.
                ' Keep retries bounded and cancellable, never retry bad metadata.
                Await Task.Delay(200, token).ConfigureAwait(False)
            Next
        Next
        token.ThrowIfCancellationRequested()
        Throw New IOException(String.Join("；", failures.Distinct()))
    End Function
    Private Class CatalogueHttpException
        Inherits HttpRequestException
        Friend ReadOnly Transient As Boolean
        Friend Sub New(code As Integer, reason As String)
            MyBase.New("HTTP " & code.ToString(Globalization.CultureInfo.InvariantCulture) & If(String.IsNullOrWhiteSpace(reason), "", " (" & reason.Substring(0, Math.Min(reason.Length, 256)) & ")"))
            Transient = code = 408 OrElse code = 429 OrElse code >= 500
        End Sub
    End Class
    Private Async Function FetchTokenAsync(address As String, seconds As Integer, token As CancellationToken) As Task(Of JToken)
        Using deadline = CancellationTokenSource.CreateLinkedTokenSource(token), request As New HttpRequestMessage(HttpMethod.Get, address)
            deadline.CancelAfter(TimeSpan.FromSeconds(seconds))
            If New Uri(address).Host = "api.curseforge.com" AndAlso Not String.IsNullOrWhiteSpace(_key) Then request.Headers.TryAddWithoutValidation("x-api-key", _key)
            Using response = Await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(False)
                If Not response.IsSuccessStatusCode Then Throw New CatalogueHttpException(CInt(response.StatusCode), response.ReasonPhrase)
                Using registration = deadline.Token.Register(Sub() response.Dispose()), input = Await response.Content.ReadAsStreamAsync().ConfigureAwait(False), buffer As New MemoryStream()
                    Dim block(8191) As Byte
                    Do
                        Dim read = Await input.ReadAsync(block, 0, block.Length, deadline.Token).ConfigureAwait(False)
                        If read = 0 Then Exit Do
                        If buffer.Length + read > 16L * 1024 * 1024 Then Throw New InvalidDataException("平台响应超过大小限制。")
                        buffer.Write(block, 0, read)
                    Loop
                    token.ThrowIfCancellationRequested()
                    Return JToken.Parse(System.Text.Encoding.UTF8.GetString(buffer.ToArray()), New JsonLoadSettings With {.DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error})
                End Using
            End Using
        End Using
    End Function
    Private Shared Sub ValidateId(platform As ModpackPlatform, id As String)
        If platform <> ModpackPlatform.Modrinth AndAlso platform <> ModpackPlatform.CurseForge Then Throw New ArgumentException("平台无效。")
        If id Is Nothing OrElse Not Regex.IsMatch(id, If(platform = ModpackPlatform.Modrinth, "\A[A-Za-z0-9]{1,32}\z", "\A[1-9][0-9]{0,12}\z")) Then Throw New InvalidDataException("平台项目或文件 ID 无效。")
    End Sub
    Private Shared Function Text(data As JObject, field As String, Optional required As Boolean = True) As String
        Dim value = data(field)
        If value Is Nothing OrElse value.Type = JTokenType.Null Then
            If Not required Then Return ""
            Throw New InvalidDataException("平台缺少字段：" & field)
        End If
        If value.Type <> JTokenType.String AndAlso value.Type <> JTokenType.Integer Then Throw New InvalidDataException("平台文本字段无效：" & field)
        Dim result = CStr(value)
        If result.Length > 8192 OrElse (required AndAlso String.IsNullOrWhiteSpace(result)) Then Throw New InvalidDataException("平台文本字段无效：" & field)
        Return result
    End Function
    Private Shared Function Number(data As JObject, field As String) As Long
        If data(field) IsNot Nothing AndAlso data(field).Type = JTokenType.Float Then
            Dim numericValue As Decimal
            If Decimal.TryParse(data(field).ToString(), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, numericValue) AndAlso numericValue >= 0 AndAlso numericValue <= Long.MaxValue AndAlso Decimal.Truncate(numericValue) = numericValue Then Return CLng(numericValue)
            Throw New InvalidDataException("平台数值字段无效：" & field)
        End If
        Dim result As Long
        If Not Long.TryParse(Text(data, field), result) OrElse result < 0 Then Throw New InvalidDataException("平台数值字段无效：" & field)
        Return result
    End Function
    Private Shared Function Flag(data As JObject, field As String, fallback As Boolean) As Boolean
        ' MCIM can serialize an optional Core API flag as explicit JSON null.
        ' Give it the same default as an omitted flag; preserve explicit values.
        Dim value = data(field)
        If value Is Nothing OrElse value.Type = JTokenType.Null Then Return fallback
        If value.Type <> JTokenType.Boolean Then Throw New InvalidDataException("平台布尔字段无效：" & field)
        Return CBool(value)
    End Function
    Private Shared Function ObjectValue(data As JObject, field As String) As JObject
        Dim result = TryCast(data(field), JObject)
        If result Is Nothing Then Throw New InvalidDataException("平台对象字段无效：" & field)
        Return result
    End Function
    Private Shared Function ArrayValue(data As JObject, field As String) As JArray
        Dim result = TryCast(data(field), JArray)
        If result Is Nothing Then Throw New InvalidDataException("平台数组字段无效：" & field)
        Return result
    End Function
    Private Shared Function Strings(data As JObject, field As String) As String()
        Dim array = TryCast(data(field), JArray)
        If array Is Nothing Then Return {}
        If array.Any(Function(value) value.Type <> JTokenType.String OrElse CStr(value).Length > 100) Then Throw New InvalidDataException("平台版本或分类字段无效。")
        Return array.Values(Of String)().Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
    End Function
    Private Shared Function DateValue(data As JObject, field As String) As DateTimeOffset
        Dim value = data(field), result As DateTimeOffset
        If value Is Nothing OrElse Not DateTimeOffset.TryParse(value.ToString(), Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AssumeUniversal, result) Then Throw New InvalidDataException("平台日期字段无效：" & field)
        Return result
    End Function
    Private Shared Function Digest(data As JObject, field As String, length As Integer) As String
        Dim value = Text(data, field, False).ToLowerInvariant()
        If value = "" Then Return Nothing
        If Not Regex.IsMatch(value, "\A[0-9a-f]{" & length & "}\z") Then Throw New InvalidDataException("归档摘要无效：" & field)
        Return value
    End Function
    Public Sub Dispose() Implements IDisposable.Dispose
        _client.Dispose()
    End Sub
End Class
