Imports System.Globalization
Imports System.IO
Imports System.Threading
Imports System.Text.RegularExpressions
Imports Newtonsoft.Json.Linq

' PCL 5ef81e4 DlClientListBmclapiMain merges extra entries before using the normal
' installation pipeline. UVMC is a separate public catalogue, not PCL's private list.
Public NotInheritable Class SupplementalVersions
    Public Const ManifestUrl As String = "https://zkitefly.github.io/unlisted-versions-of-minecraft/version_manifest.json"
    Private Const BaseUrl As String = "https://zkitefly.github.io/unlisted-versions-of-minecraft/files/"
    Private Shared ReadOnly Aliases As Dictionary(Of String, String) = CreateAliases()

    Private Shared Function CreateAliases() As Dictionary(Of String, String)
        Dim result As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        ' These catalogue -> metadata identities have been checked individually.
        For Each suffix In {"16_combat-0", "16_combat-1", "16_combat-2", "16_combat-3", "16_combat-4", "16_combat-5", "16_combat-6",
                            "15_combat-1", "15_combat-6", "14_combat-0", "14_combat-3", "14_combat-212796"}
            result.Add("1_" & suffix, "1." & suffix)
            result.Add("1." & suffix, "1." & suffix)
        Next
        For number = 1 To 7
            Dim id = "1.18_experimental-snapshot-" & number.ToString(CultureInfo.InvariantCulture)
            result.Add(id, id)
        Next
        result.Add("1.19_deep_dark_experimental_snapshot-1", "1.19_deep_dark_experimental_snapshot-1")
        Return result
    End Function

    Private Shared Function CanonicalId(id As String) As String
        Dim canonical As String = Nothing
        Return If(id IsNot Nothing AndAlso Aliases.TryGetValue(id, canonical), canonical, id)
    End Function

    Public Shared Function Label(entry As VersionEntry) As String
        Return If(entry.Id.Contains("_combat-"), "战斗测试版", "实验快照")
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
            If (kind <> "pending" AndAlso kind <> "snapshot") OrElse url <> MetadataUrl(canonical) OrElse
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
        Dim slug = id.Replace("."c, "_"c)
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

    Public Shared Sub ValidateMetadata(entry As VersionEntry, metadata As JObject)
        If StringValue(metadata("id")) <> entry.Id OrElse metadata("inheritsFrom") IsNot Nothing Then Throw New InvalidDataException("版本元数据不是所选原版版本。")
        If Not entry.IsSupplemental Then Return
        Dim canonical As String = Nothing
        If Not Aliases.TryGetValue(entry.SupplementalId, canonical) OrElse canonical <> entry.Id OrElse entry.Url <> MetadataUrl(canonical) OrElse
            (Not String.IsNullOrEmpty(entry.Sha1) AndAlso Not IsSha1(entry.Sha1)) Then Throw New InvalidDataException("补充版本身份无效。")
        If String.IsNullOrWhiteSpace(StringValue(metadata("mainClass"))) OrElse Not TypeOf metadata("libraries") Is JArray OrElse
            (StringValue(metadata("type")) <> "pending" AndAlso StringValue(metadata("type")) <> "snapshot") OrElse metadata("jar") IsNot Nothing Then Throw New InvalidDataException("补充版本元数据结构无效。")
        Dim index = TryCast(metadata("assetIndex"), JObject)
        If index Is Nothing OrElse String.IsNullOrWhiteSpace(StringValue(index("id"))) OrElse Not IsSha1(StringValue(index("sha1"))) Then Throw New InvalidDataException("补充版本资源索引缺少有效校验。")
    End Sub

    Public Shared Sub ValidatePlan(files As IEnumerable(Of DownloadFile))
        For Each file In files
            If Not IsSha1(file.Sha1) OrElse file.Size < 0 Then Throw New InvalidDataException("补充版本文件缺少有效的 SHA-1 或大小：" & file.RelativePath)
        Next
    End Sub
End Class
