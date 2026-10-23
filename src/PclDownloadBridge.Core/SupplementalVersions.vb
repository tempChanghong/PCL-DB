Imports System.Globalization
Imports System.IO
Imports System.Threading
Imports System.Text.RegularExpressions
Imports Newtonsoft.Json.Linq

' PCL 5ef81e4 DlClientListBmclapiMain merges extra entries before using the normal
' installation pipeline. UVMC is a separate public catalogue, not PCL's private list.
Public NotInheritable Class SupplementalVersions
    Public Const ManifestUrl As String = "https://zkitefly.github.io/unlisted-versions-of-minecraft/version_manifest.json"
    Public Const LibraryReferenceUrl As String = "https://piston-meta.mojang.com/v1/packages/3c514114d9c2a3ea78f72c4f9fb4eeb56747135a/1.5.1.json"
    Public Const LibraryReferenceSha1 As String = "3c514114d9c2a3ea78f72c4f9fb4eeb56747135a"
    Public Const AlphaLibraryReferenceUrl As String = "https://piston-meta.mojang.com/v1/packages/51a5c512af384d3d2a79a3efb93f7d4b9a1c6ec2/inf-20100618.json"
    Public Const AlphaLibraryReferenceSha1 As String = "51a5c512af384d3d2a79a3efb93f7d4b9a1c6ec2"
    Public Const LegacyAssetIndexUrl As String = "https://launchermeta.mojang.com/mc-staging/assets/legacy/c0fd82e8ce9fbc93119e40d96d5a4e62cfa3f729/legacy.json"
    Public Const LegacyAssetIndexSha1 As String = "c0fd82e8ce9fbc93119e40d96d5a4e62cfa3f729"
    Private Const BaseUrl As String = "https://zkitefly.github.io/unlisted-versions-of-minecraft/files/"
    ' Initialize before Aliases, which consumes this same read-only registry.
    Private Shared ReadOnly SnapshotIds As IReadOnlyList(Of String) = Array.AsReadOnly(New String() {
        "13w12~", "13w10b", "13w10a", "13w09c", "13w09b", "11w47a",
        "13w09a", "13w07a", "13w11a", "13w06a", "13w05b", "13w05a", "13w04a", "13w03a", "13w02b", "13w02a",
        "13w01b", "13w01a", "12w50b", "12w50a", "12w49a", "12w42b", "12w42a", "12w41b", "12w41a", "12w40b",
        "12w40a", "12w39b", "12w39a", "12w38b", "12w38a", "12w37a", "12w36a", "12w34b", "12w34a", "12w32a",
        "12w30a", "12w30b", "12w30c", "12w30d", "12w30e", "12w27a", "12w26a", "12w25a", "12w24a", "12w23a", "12w23b",
        "12w22a", "12w21a", "12w21b", "12w19a", "12w18a", "12w17a", "12w16a", "1.2", "12w08a", "12w07a", "12w07b"})
    Private Shared ReadOnly Aliases As Dictionary(Of String, String) = CreateAliases()

    Public Shared ReadOnly Property HistoricalSnapshotIds As IReadOnlyList(Of String)
        Get
            Return SnapshotIds
        End Get
    End Property

    Friend Shared Function IsHistoricalSnapshot(id As String) As Boolean
        Return SnapshotIds.Contains(id, StringComparer.Ordinal)
    End Function

    Private Shared Function CreateAliases() As Dictionary(Of String, String)
        Dim result As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        ' These catalogue -> metadata identities have been checked individually.
        For Each color In {"blue", "red", "purple"}
            result.Add("2point0_" & color, "2.0_" & color)
            result.Add("2.0_" & color, "2.0_" & color)
        Next
        For Each id In {"c0.0.12a_03", "a1.1.1", "b1.9-pre6", "1.0.0-rc1", "inf-20100630-1"}.Concat(SnapshotIds)
            result.Add(id, id)
        Next
        Return result
    End Function

    Private Shared Function CanonicalId(id As String) As String
        Dim canonical As String = Nothing
        Return If(id IsNot Nothing AndAlso Aliases.TryGetValue(id, canonical), canonical, id)
    End Function

    Public Shared Function Label(entry As VersionEntry) As String
        If entry.Id.StartsWith("2.0_", StringComparison.Ordinal) Then Return "愚人节版"
        Return If(entry.Kind = "snapshot", "旧快照", "远古版")
    End Function

    Private Shared Function ValidKind(id As String, kind As String) As Boolean
        If IsHistoricalSnapshot(id) Then Return kind = "snapshot"
        Select Case id
            Case "c0.0.12a_03", "a1.1.1", "inf-20100630-1" : Return kind = "old_alpha"
            Case "b1.9-pre6", "1.0.0-rc1" : Return kind = "old_beta"
            Case Else : Return kind = "snapshot" OrElse kind = "pending"
        End Select
    End Function

    Public Shared Function DisplayName(entry As VersionEntry) As String
        Select Case entry.Id
            Case "2.0_blue" : Return "2.0（蓝色版）"
            Case "2.0_red" : Return "2.0（红色版）"
            Case "2.0_purple" : Return "2.0（紫色版）"
            Case Else : Return entry.Id
        End Select
    End Function

    ' Bounded, optional work. The UI shows the ordinary catalogue before awaiting it.
    ' No persistent cache: a failed refresh cannot resurrect an obsolete declaration.
    Public Shared Async Function LoadAsync(policy As SourcePolicy, token As CancellationToken) As Task(Of List(Of VersionEntry))
        Using timeout = CancellationTokenSource.CreateLinkedTokenSource(token)
            timeout.CancelAfter(TimeSpan.FromSeconds(3))
            Try
                Dim body = Await policy.GetTextAsync(ManifestUrl, timeout.Token).ConfigureAwait(False)
                If body.Length > 1024 * 1024 Then Throw New InvalidDataException("补充版本列表过大。")
                Return Parse(JObject.Parse(body))
            Catch ex As Exception When TypeOf ex Is IO.IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is Net.Http.HttpRequestException OrElse
                TypeOf ex Is OperationCanceledException OrElse TypeOf ex Is Newtonsoft.Json.JsonException OrElse TypeOf ex Is ArgumentException OrElse
                (TypeOf ex Is InvalidOperationException AndAlso TypeOf ex.InnerException Is ArgumentException)
                ' net48 HttpContent wraps unsupported response charsets this way.
                token.ThrowIfCancellationRequested()
                Return New List(Of VersionEntry)()
            End Try
        End Using
    End Function

    Public Shared Function Parse(manifest As JObject) As List(Of VersionEntry)
        Dim versions = TryCast(manifest("versions"), JArray)
        If versions Is Nothing OrElse versions.Count > 4096 Then Throw New InvalidDataException("补充版本列表格式无效。")
        Dim parsed As New List(Of VersionEntry)
        For Each value In versions.OfType(Of JObject)()
            Dim rawId = StringValue(value("id")), canonical As String = Nothing
            If rawId Is Nothing OrElse Not Aliases.TryGetValue(rawId, canonical) Then Continue For
            Dim kind = StringValue(value("type")), url = StringValue(value("url")), sha1 = StringValue(value("sha1"))
            Dim released As DateTimeOffset
            If Not ValidKind(canonical, kind) OrElse url <> MetadataUrl(canonical) OrElse
                (value("sha1") IsNot Nothing AndAlso Not IsSha1(sha1)) OrElse
                Not TryRelease(value("releaseTime"), released) Then Continue For
            parsed.Add(New VersionEntry With {.Id = canonical, .SupplementalId = rawId, .Kind = kind, .Url = url, .Sha1 = sha1, .Released = released})
        Next
        ' A contradictory duplicate excludes that identity; array order cannot pick a winner.
        Return parsed.GroupBy(Function(entry) entry.Id, StringComparer.OrdinalIgnoreCase).
            Where(Function(group) group.All(Function(entry) SameDeclaration(entry, group.First()))).
            Select(Function(group) group.First()).ToList()
    End Function

    Public Shared Function Merge(normal As IEnumerable(Of VersionEntry), extra As IEnumerable(Of VersionEntry)) As List(Of VersionEntry)
        Dim result = normal.ToList()
        Dim occupied As New HashSet(Of String)(result.Select(Function(entry) CanonicalId(entry.Id)), StringComparer.OrdinalIgnoreCase)
        For Each group In extra.GroupBy(Function(entry) CanonicalId(entry.Id), StringComparer.OrdinalIgnoreCase)
            If Not occupied.Add(group.Key) OrElse Not group.All(Function(entry) SameDeclaration(entry, group.First())) Then Continue For
            result.Add(group.First())
        Next
        Return result
    End Function

    Private Shared Function SameDeclaration(left As VersionEntry, right As VersionEntry) As Boolean
        Return left.Id = right.Id AndAlso left.Url = right.Url AndAlso left.Kind = right.Kind AndAlso
            String.Equals(left.Sha1, right.Sha1, StringComparison.OrdinalIgnoreCase) AndAlso left.Released = right.Released
    End Function

    Private Shared Function MetadataUrl(id As String) As String
        Dim slug = id.Replace("2.0_", "2point0_")
        If id = "b1.9-pre6" Then slug = "b1_9-pre6"
        If id = "13w12~" Then slug = "13w12-"
        If id = "1.2" Then slug = "1_2"
        Return BaseUrl & slug & "/" & slug & ".json"
    End Function

    Private Shared Function StringValue(value As JToken) As String
        Return If(value IsNot Nothing AndAlso value.Type = JTokenType.String, CStr(value), Nothing)
    End Function

    Private Shared Function IsSha1(value As String) As Boolean
        Return Regex.IsMatch(If(value, ""), "\A[0-9a-fA-F]{40}\z")
    End Function

    Private Shared Function TryRelease(value As JToken, ByRef released As DateTimeOffset) As Boolean
        If value IsNot Nothing AndAlso value.Type = JTokenType.Date Then
            released = value.ToObject(Of DateTimeOffset)()
            Return True
        End If
        Return DateTimeOffset.TryParse(StringValue(value), CultureInfo.InvariantCulture, DateTimeStyles.None, released)
    End Function

    Private Shared Function UsesAlphaReference(id As String) As Boolean
        Return id = "1.0.0-rc1" OrElse id = "inf-20100630-1"
    End Function

    Private Shared Function CanSupplyLegacyIndex(id As String, metadata As JObject) As Boolean
        ' Narrower than PCL McAssetsGetIndex(ReturnLegacyOnError): only these
        ' audited profiles, with BOTH properties absent. Null/malformed/download
        ' failures must never silently switch the resource tree.
        Return UsesAlphaReference(id) AndAlso metadata.Property("assetIndex") Is Nothing AndAlso metadata.Property("assets") Is Nothing
    End Function

    Public Shared Sub ValidateMetadata(entry As VersionEntry, metadata As JObject)
        If StringValue(metadata("id")) <> entry.Id OrElse metadata("inheritsFrom") IsNot Nothing Then Throw New InvalidDataException("版本元数据不是所选原版版本。")
        If Not entry.IsSupplemental Then Return
        Dim canonical As String = Nothing
        If Not Aliases.TryGetValue(entry.SupplementalId, canonical) OrElse canonical <> entry.Id OrElse entry.Url <> MetadataUrl(canonical) OrElse
            Not ValidKind(canonical, entry.Kind) OrElse
            (Not String.IsNullOrEmpty(entry.Sha1) AndAlso Not IsSha1(entry.Sha1)) Then Throw New InvalidDataException("补充版本身份无效。")
        If String.IsNullOrWhiteSpace(StringValue(metadata("mainClass"))) OrElse Not TypeOf metadata("libraries") Is JArray OrElse
            Not ValidKind(canonical, StringValue(metadata("type"))) OrElse metadata("jar") IsNot Nothing Then Throw New InvalidDataException("补充版本元数据结构无效。")
        If DirectCast(metadata("libraries"), JArray).Any(Function(item) Not TypeOf item Is JObject) Then Throw New InvalidDataException("补充版本包含无效的支持库声明。")
        If DirectCast(metadata("libraries"), JArray).OfType(Of JObject)().Any(Function(library) library.Property("downloads") IsNot Nothing AndAlso Not TypeOf library("downloads") Is JObject) Then Throw New InvalidDataException("补充版本的支持库下载声明损坏，不能按缺失字段补全。")
        Dim index = TryCast(metadata("assetIndex"), JObject)
        If Not CanSupplyLegacyIndex(canonical, metadata) AndAlso
            (index Is Nothing OrElse String.IsNullOrWhiteSpace(StringValue(index("id"))) OrElse Not IsSha1(StringValue(index("sha1")))) Then Throw New InvalidDataException("补充版本资源索引缺少有效校验。")
        SupplementalLaunchRecipe.ValidateSource(canonical, metadata)
    End Sub

    Public Shared Async Function PrepareLibrariesAsync(entry As VersionEntry, metadata As JObject, policy As SourcePolicy, token As CancellationToken) As Task(Of JObject)
        token.ThrowIfCancellationRequested()
        Dim result = DirectCast(metadata.DeepClone(), JObject)
        If Not entry.IsSupplemental Then Return result
        ValidateMetadata(entry, result)
        If CanSupplyLegacyIndex(entry.Id, result) Then
            ' PCL 5ef81e4 McAssetsGetIndex's fixed legacy declaration. Keep
            ' virtual assets in assets/virtual/legacy even with a private index ID.
            result("assets") = "legacy"
            result("assetIndex") = New JObject From {{"id", "legacy"}, {"sha1", LegacyAssetIndexSha1}, {"size", 134284}, {"totalSize", 111220701}, {"url", LegacyAssetIndexUrl}}
        End If
        Dim libraries = DirectCast(result("libraries"), JArray).OfType(Of JObject)().ToList()
        If Not libraries.Any(Function(library) library.Property("downloads") Is Nothing) Then
            SupplementalLaunchRecipe.ValidateLibraries(entry.Id, result)
            Return result
        End If
        Dim alpha = UsesAlphaReference(entry.Id)
        If Not alpha AndAlso Not entry.Id.StartsWith("2.0_", StringComparison.Ordinal) Then Throw New InvalidDataException("该历史版本的支持库下载信息不完整，没有对应的已校验参考清单。")
        ' UVMC 2.0 profiles retain the original Maven/rules/native declarations,
        ' without download records. Match them to the SHA-1 verified Mojang 1.5.1
        ' profile, preserving this version's client, arguments and asset index.
        ' RC1/Infdev explicitly use the 1.6 Alpha family. Never guess a reference
        ' from the version's age or use the 2.0/1.5 wrapper for them.
        Dim reference = Await policy.GetJsonAsync(If(alpha, AlphaLibraryReferenceUrl, LibraryReferenceUrl), token, If(alpha, AlphaLibraryReferenceSha1, LibraryReferenceSha1)).ConfigureAwait(False)
        If StringValue(reference("id")) <> If(alpha, "inf-20100618", "1.5.1") OrElse Not TypeOf reference("libraries") Is JArray Then Throw New InvalidDataException("旧版支持库参考清单无效。")
        For Each library In libraries
            token.ThrowIfCancellationRequested()
            If library.Property("downloads") IsNot Nothing Then Continue For
            Dim declaration = DirectCast(library.DeepClone(), JObject) : declaration.Remove("downloads")
            Dim matches As New List(Of JToken)
            For Each candidate In DirectCast(reference("libraries"), JArray).OfType(Of JObject)()
                Dim shape = DirectCast(candidate.DeepClone(), JObject) : shape.Remove("downloads")
                If JToken.DeepEquals(shape, declaration) AndAlso TypeOf candidate("downloads") Is JObject Then matches.Add(candidate("downloads"))
            Next
            If matches.Count = 0 OrElse Not matches.All(Function(item) JToken.DeepEquals(item, matches(0))) Then Throw New InvalidDataException("补充版本的支持库声明与已校验参考不一致：" & StringValue(library("name")))
            library("downloads") = matches(0).DeepClone()
        Next
        token.ThrowIfCancellationRequested()
        SupplementalLaunchRecipe.ValidateLibraries(entry.Id, result)
        Return result
    End Function

    Public Shared Sub ValidatePlan(files As IEnumerable(Of DownloadFile))
        For Each file In files
            If Not IsSha1(file.Sha1) OrElse file.Size < 0 Then Throw New InvalidDataException("补充版本文件缺少有效的 SHA-1 或大小：" & file.RelativePath)
        Next
    End Sub
End Class
