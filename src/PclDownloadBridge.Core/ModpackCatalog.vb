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
    Public Property Text As String = ""
    Public Property Minecraft As String = ""
    Public Property Category As String = "/"
    Public Property Platforms As ModpackPlatform = ModpackPlatform.All
    Public Function Copy() As ModpackQuery
        Return DirectCast(MemberwiseClone(), ModpackQuery)
    End Function
End Class

Public NotInheritable Class ModpackProject
    Public Property Platform As ModpackPlatform
    Public Property Id As String
    Public Property Name As String
    Public Property Summary As String
    Public Property IconUrl As String
    Public Property Website As String
    Public Property Downloads As Long
    Public Property Updated As DateTimeOffset
    Public Property GameVersions As String() = {}
    Public ReadOnly Property Key As String
        Get
            Return Platform.ToString() & ":" & Id
        End Get
    End Property
End Class

Public NotInheritable Class ModpackRelease
    Public Property Platform As ModpackPlatform
    Public Property ProjectId As String
    Public Property Id As String
    Public Property Name As String
    Public Property Version As String
    Public Property Published As DateTimeOffset
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
    Public Sub New(Optional handler As HttpMessageHandler = Nothing, Optional apiKey As String = Nothing)
        _client = New HttpClient(If(handler, New HttpClientHandler With {.AllowAutoRedirect = False, .AutomaticDecompression = DecompressionMethods.GZip Or DecompressionMethods.Deflate, .MaxConnectionsPerServer = 4})) With {.Timeout = Timeout.InfiniteTimeSpan}
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("PclDownloadBridge/1.0.0-beta.1 (modpack catalogue)")
        _key = If(apiKey, Environment.GetEnvironmentVariable("PCLDB_CURSEFORGE_API_KEY"))
    End Sub

    Public Shared Function SearchUrl(platform As ModpackPlatform, query As ModpackQuery, offset As Integer) As String
        If query Is Nothing OrElse offset < 0 OrElse query.Text Is Nothing OrElse query.Category Is Nothing OrElse query.Minecraft Is Nothing OrElse CInt(query.Platforms) < 1 OrElse CInt(query.Platforms) > 3 Then Throw New ArgumentException("搜索条件无效。")
        Dim categories = query.Category.Split("/"c)
        If categories.Length <> 2 OrElse query.Text.Length > 256 OrElse query.Minecraft.Length > 100 Then Throw New ArgumentException("搜索条件过长或分类无效。")
        Dim text = Uri.EscapeDataString(query.Text.Trim()), game = Uri.EscapeDataString(query.Minecraft.Trim())
        If platform = ModpackPlatform.CurseForge Then
            Dim url = "https://api.curseforge.com/v1/mods/search?gameId=432&classId=4471&sortField=2&sortOrder=desc&pageSize=40&index=" & offset.ToString(Globalization.CultureInfo.InvariantCulture)
            If text <> "" Then url &= "&searchFilter=" & text
            If game <> "" Then url &= "&gameVersion=" & game
            If categories(0) <> "" Then url &= "&categoryId=" & Uri.EscapeDataString(categories(0))
            Return url
        End If
        If platform <> ModpackPlatform.Modrinth Then Throw New ArgumentException("请选择一个内容平台。")
        Dim facets As New JArray()
        facets.Add(New JArray("project_type:modpack"))
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
            If platform = ModpackPlatform.Modrinth Then
                If Text(item, "project_type") <> "modpack" Then Continue For
            ElseIf Number(item, "classId") <> 4471 Then
                Continue For
            End If
            result.Projects.Add(ParseProject(platform, item, True))
        Next
        Return result
    End Function

    Public Async Function GetProjectAsync(platform As ModpackPlatform, id As String, token As CancellationToken) As Task(Of ModpackProject)
        ValidateId(platform, id)
        Dim url = If(platform = ModpackPlatform.Modrinth, "https://api.modrinth.com/v2/project/", "https://api.curseforge.com/v1/mods/") & id
        Dim data = Await JsonAsync(url, token).ConfigureAwait(False)
        If platform = ModpackPlatform.CurseForge Then data = ObjectValue(data, "data")
        If (platform = ModpackPlatform.Modrinth AndAlso Text(data, "project_type") <> "modpack") OrElse (platform = ModpackPlatform.CurseForge AndAlso Number(data, "classId") <> 4471) Then Throw New InvalidDataException("项目不是客户端整合包。")
        Dim result = ParseProject(platform, data, False)
        If result.Id <> id Then Throw New InvalidDataException("返回的整合包项目 ID 不匹配。")
        Return result
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
                If release IsNot Nothing AndAlso seen.Add(release.Id) Then result.Add(release)
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
        Dim result As New ModpackRelease With {.Platform = project.Platform, .ProjectId = project.Id, .Id = Text(data, "id"), .Name = Text(data, If(project.Platform = ModpackPlatform.Modrinth, "name", "displayName"))}
        ValidateId(project.Platform, result.Id)
        If project.Platform = ModpackPlatform.Modrinth Then
            If Text(data, "project_id") <> project.Id Then Throw New InvalidDataException("Modrinth 版本所属项目不匹配。")
            If Text(data, "status", False) <> "" AndAlso Text(data, "status") <> "listed" AndAlso Text(data, "status") <> "archived" Then Return Nothing
            Dim candidates = ArrayValue(data, "files").OfType(Of JObject)().Where(Function(file) Text(file, "filename").EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase)).ToList()
            Dim primary = candidates.Where(Function(file) CBool(If(file("primary"), New JValue(False)))).ToList()
            Dim chosen = If(primary.Count = 1, primary(0), If(candidates.Count = 1, candidates(0), Nothing))
            If chosen Is Nothing Then Return Nothing
            result.FileName = Text(chosen, "filename") : result.Url = Text(chosen, "url") : result.Size = Number(chosen, "size")
            Dim hashes = ObjectValue(chosen, "hashes")
            result.Sha1 = Digest(hashes, "sha1", 40) : result.Sha512 = Digest(hashes, "sha512", 128)
            result.Version = Text(data, "version_number") : result.Published = DateValue(data, "date_published")
            result.ReleaseType = Text(data, "version_type") : result.GameVersions = Strings(data, "game_versions") : result.Loaders = Strings(data, "loaders")
        Else
            If Text(data, "modId") <> project.Id Then Throw New InvalidDataException("CurseForge 文件所属项目不匹配。")
            If data("isServerPack") IsNot Nothing AndAlso CBool(data("isServerPack")) Then Return Nothing
            If data("isAvailable") IsNot Nothing AndAlso Not CBool(data("isAvailable")) Then Return Nothing
            If data("isEarlyAccessContent") IsNot Nothing AndAlso CBool(data("isEarlyAccessContent")) Then Return Nothing
            If data("fileStatus") IsNot Nothing AndAlso Number(data, "fileStatus") <> 4 Then Return Nothing
            result.FileName = Text(data, "fileName")
            If Not result.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) Then Return Nothing
            result.Url = Text(data, "downloadUrl", False)
            If result.Url = "" Then Return Nothing
            result.Size = Number(data, "fileLength") : result.Version = result.Name : result.Published = DateValue(data, "fileDate")
            result.ReleaseType = If(Number(data, "releaseType") = 1, "release", If(Number(data, "releaseType") = 2, "beta", "alpha"))
            Dim raw = ReadCurseForgeTags(data("gameVersions"), result)
            result.Loaders = raw.Where(Function(value) {"Forge", "NeoForge", "Fabric", "Quilt"}.Contains(value, StringComparer.OrdinalIgnoreCase)).ToArray()
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
        ' The archive is stored under a fixed cache name, not the remote filename.
        ' Keep ordinary long archive names while rejecting path-bearing metadata.
        If result.FileName.Length > 255 OrElse result.FileName <> result.FileName.Trim() OrElse result.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 Then Throw New InvalidDataException("整合包归档文件名无效。")
        If result.Size <= 0 OrElse result.Size > 2L * 1024 * 1024 * 1024 Then Return Nothing
        If result.Sha1 Is Nothing AndAlso result.Sha512 Is Nothing AndAlso result.Md5 Is Nothing Then Return Nothing
        ModpackArchivePolicy.Validate(result.Url, result.Platform, False)
        If result.Platform = ModpackPlatform.CurseForge Then
            Dim id = Long.Parse(result.Id, Globalization.CultureInfo.InvariantCulture), address As New Uri(result.Url)
            Dim expected = "/files/" & (id \ 1000).ToString(Globalization.CultureInfo.InvariantCulture) & "/" & (id Mod 1000).ToString(Globalization.CultureInfo.InvariantCulture) & "/" & result.FileName
            If Uri.UnescapeDataString(address.AbsolutePath) <> expected Then Throw New InvalidDataException("CurseForge 下载地址与指定文件不匹配。")
        End If
        Return result
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

    Private Shared Function IsExactMinecraftVersion(value As String) As Boolean
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

    Private Shared Function ParseProject(platform As ModpackPlatform, data As JObject, search As Boolean) As ModpackProject
        Dim result As New ModpackProject With {.Platform = platform}
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
            urls.Add(url.Replace("https://api.curseforge.com/", "https://www.curseforge.com/api/"))
            urls.Add(url.Replace("https://api.curseforge.com/", "https://mod.mcimirror.top/curseforge/"))
        End If
        Dim failures As New List(Of String)
        For Each address In urls
            token.ThrowIfCancellationRequested()
            Try
                Using deadline = CancellationTokenSource.CreateLinkedTokenSource(token), request As New HttpRequestMessage(HttpMethod.Get, address)
                    deadline.CancelAfter(TimeSpan.FromSeconds(15))
                    If New Uri(address).Host = "api.curseforge.com" AndAlso Not String.IsNullOrWhiteSpace(_key) Then request.Headers.TryAddWithoutValidation("x-api-key", _key)
                    Using response = Await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(False)
                        response.EnsureSuccessStatusCode()
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
            Catch ex As Exception When Not token.IsCancellationRequested
                failures.Add(New Uri(address).Host & "：" & If(TypeOf ex Is OperationCanceledException, "请求超时", ex.GetBaseException().Message))
            End Try
        Next
        token.ThrowIfCancellationRequested()
        Throw New IOException(String.Join("；", failures))
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
