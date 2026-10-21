Imports System.Net.Http
Imports System.Threading
Imports System.Diagnostics
Imports Newtonsoft.Json.Linq

Public Enum SourcePreference
    Automatic
    OfficialFirst
    MirrorFirst
End Enum

' PCL DlPreferMojang is session state, independent of the current settings page.
Public Class SourceSelectionState
    Private _preferOfficial As Integer
    Public ReadOnly Property PreferOfficial As Boolean
        Get
            Return Volatile.Read(_preferOfficial) <> 0
        End Get
    End Property
    Friend Sub ObserveOfficial(elapsed As TimeSpan)
        If elapsed.TotalMilliseconds < 4000 Then Interlocked.Exchange(_preferOfficial, 1)
    End Sub
End Class

' Adapted URL mapping from PCL ModDownload.DlSourceAssetsGet / DlSourceLibraryGet /
' DlSourceLauncherOrMetaGet at 5ef81e4. No PCL identity, credentials, or signed CDN URLs.
Public Class SourcePolicy
    Private ReadOnly _selection As SourceSelectionState
    Public Sub New(Optional selection As SourceSelectionState = Nothing)
        _selection = If(selection, New SourceSelectionState())
    End Sub
    Public Property Preference As SourcePreference = SourcePreference.Automatic
    ' Legacy callers keep their combined preference; the app sets both explicitly.
    Public Property VersionPreference As SourcePreference?
    Public ReadOnly Property EffectiveVersionPreference As SourcePreference
        Get
            Return VersionPreference.GetValueOrDefault(Preference)
        End Get
    End Property
    Public Property AllowLoopback As Boolean
    Public ReadOnly Property PreferOfficial As Boolean
        Get
            Return Preference = SourcePreference.OfficialFirst OrElse (Preference = SourcePreference.Automatic AndAlso _selection.PreferOfficial)
        End Get
    End Property

    Public Overridable Function Candidates(url As String, category As String) As List(Of String)
        Dim original As New Uri(url, UriKind.Absolute)
        ' PCL's legacy loader metadata still contains HTTP Maven links.
        If original.Scheme = "http" AndAlso Not original.IsLoopback Then
            url = New UriBuilder(original) With {.Scheme = "https", .Port = -1}.Uri.AbsoluteUri
            original = New Uri(url)
        End If
        If original.Scheme <> "https" AndAlso Not (AllowLoopback AndAlso original.IsLoopback AndAlso original.Scheme = "http") Then Throw New ArgumentException("下载地址必须使用 HTTPS。")
        Dim mirrors As New List(Of String)
        Select Case original.Host.ToLowerInvariant()
            Case "resources.download.minecraft.net"
                mirrors.Add("https://bmclapi2.bangbang93.com/assets" & original.PathAndQuery)
            Case "libraries.minecraft.net"
                mirrors.Add("https://bmclapi2.bangbang93.com/maven" & original.PathAndQuery)
                mirrors.Add("https://bmclapi2.bangbang93.com/libraries" & original.PathAndQuery)
            Case "piston-meta.mojang.com", "piston-data.mojang.com"
                ' PCL selects the mirror namespace by file category on these hosts.
                Select Case category
                    Case "asset"
                        mirrors.Add("https://bmclapi2.bangbang93.com/assets" & original.PathAndQuery)
                    Case "library"
                        mirrors.Add("https://bmclapi2.bangbang93.com/maven" & original.PathAndQuery)
                        mirrors.Add("https://bmclapi2.bangbang93.com/libraries" & original.PathAndQuery)
                    Case Else
                        mirrors.Add("https://bmclapi2.bangbang93.com" & original.PathAndQuery)
                End Select
            Case "launchermeta.mojang.com", "launcher.mojang.com"
                mirrors.Add("https://bmclapi2.bangbang93.com" & original.PathAndQuery)
            Case "meta.fabricmc.net"
                mirrors.Add("https://bmclapi2.bangbang93.com/fabric-meta" & original.PathAndQuery)
            Case "maven.fabricmc.net", "maven.minecraftforge.net", "files.minecraftforge.net", "maven.neoforged.net"
                Dim path = original.PathAndQuery
                If path.StartsWith("/releases/", StringComparison.Ordinal) Then path = path.Substring(9)
                If path.StartsWith("/maven/", StringComparison.Ordinal) Then path = path.Substring(6)
                If Not path.StartsWith("/api/", StringComparison.Ordinal) Then mirrors.Add("https://bmclapi2.bangbang93.com/maven" & path)
            Case "dl.liteloader.com"
                If original.AbsolutePath = "/versions/versions.json" Then
                    mirrors.Add("https://bmclapi2.bangbang93.com/maven/com/mumfrey/liteloader/versions.json")
                ElseIf original.AbsolutePath.StartsWith("/versions/", StringComparison.Ordinal) Then
                    mirrors.Add("https://bmclapi2.bangbang93.com/maven" & original.PathAndQuery.Substring(9))
                End If
        End Select
        Dim officialFirst = If(category = "version", EffectiveVersionPreference <> SourcePreference.MirrorFirst, PreferOfficial)
        If Not officialFirst Then Return mirrors.Concat({url}).Distinct().ToList()
        Return {url}.Concat(mirrors).Distinct().ToList()
    End Function

    Public Async Function GetVersionManifestAsync(token As CancellationToken) As Task(Of JObject)
        Const official As String = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"
        Dim urls = Candidates(official, "version")
        ' PCL DlClientListMain: auto probes official first (5s), mirror-first waits
        ' 30s and official-first waits 60s. A failed source starts fallback at once.
        If EffectiveVersionPreference = SourcePreference.Automatic Then urls = {official}.Concat(urls).Distinct().ToList()
        Dim delay = VersionFallbackDelay()
        Dim result = Await RaceValueAsync(urls, token, Nothing, delay,
            Sub(value)
                Dim versions = TryCast(value("versions"), JArray)
                If versions Is Nothing OrElse versions.Count < 200 Then Throw New IO.InvalidDataException("Minecraft 版本列表不完整。")
                For Each version In versions
                    Dim released As DateTimeOffset
                    If Not TypeOf version Is JObject OrElse String.IsNullOrWhiteSpace(CStr(version("id"))) OrElse
                        String.IsNullOrWhiteSpace(CStr(version("type"))) OrElse
                        Not Text.RegularExpressions.Regex.IsMatch(If(CStr(version("sha1")), ""), "\A[0-9a-fA-F]{40}\z") OrElse
                        Not DateTimeOffset.TryParse(CStr(version("releaseTime")), Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.None, released) Then Throw New IO.InvalidDataException("Minecraft 版本条目无效。")
                    CandidatesForValidation(CStr(version("url")))
                Next
            End Sub,
            Sub(source, elapsed)
                If source = official Then _selection.ObserveOfficial(elapsed)
            End Sub).ConfigureAwait(False)
        Return DirectCast(result, JObject)
    End Function

    Private Function VersionFallbackDelay() As Integer
        Return If(EffectiveVersionPreference = SourcePreference.Automatic, 5000, If(EffectiveVersionPreference = SourcePreference.MirrorFirst, 30000, 60000))
    End Function

    Public Async Function GetVersionListTokenAsync(url As String, token As CancellationToken) As Task(Of JToken)
        Dim urls = Candidates(url, "version")
        Return Await RaceVersionListTokenAsync(urls, token).ConfigureAwait(False)
    End Function

    Public Async Function RaceVersionListTokenAsync(urls As IEnumerable(Of String), token As CancellationToken) As Task(Of JToken)
        Return Await RaceValueAsync(urls, token, Nothing, VersionFallbackDelay()).ConfigureAwait(False)
    End Function

    ' Some official catalogues are HTML/XML while BMCLAPI returns JSON. Parse each
    ' before selecting a winner so an invalid preferred response still falls back.
    Public Async Function GetVersionListAsync(official As String, mirror As String, token As CancellationToken, Optional parseOfficial As Func(Of String, JToken) = Nothing, Optional parseMirror As Func(Of String, JToken) = Nothing) As Task(Of JToken)
        Dim urls = If(EffectiveVersionPreference = SourcePreference.MirrorFirst, {mirror, official}, {official, mirror})
        Return Await RaceValueAsync(urls, token, Nothing, VersionFallbackDelay(), parse:=Function(source, body)
            Dim parser = If(source = official, parseOfficial, parseMirror)
            Return If(parser Is Nothing, JToken.Parse(body), parser(body))
        End Function).ConfigureAwait(False)
    End Function

    Public Async Function GetJsonAsync(url As String, token As CancellationToken, Optional sha1 As String = Nothing) As Task(Of JObject)
        Return Await RaceJsonAsync(Candidates(url, "meta"), token, sha1, If(Preference = SourcePreference.Automatic, 1500, 5000)).ConfigureAwait(False)
    End Function

    Public Async Function GetTokenAsync(url As String, token As CancellationToken) As Task(Of JToken)
        Return Await RaceTokenAsync(Candidates(url, "meta"), token).ConfigureAwait(False)
    End Function

    Public Async Function RaceTokenAsync(urls As IEnumerable(Of String), token As CancellationToken) As Task(Of JToken)
        ' Reuse the same validated, cancellable metadata race for JSON arrays.
        Return Await RaceValueAsync(urls, token, Nothing, If(Preference = SourcePreference.Automatic, 1500, 5000)).ConfigureAwait(False)
    End Function

    Public Async Function GetTextAsync(url As String, token As CancellationToken) As Task(Of String)
        CandidatesForValidation(url)
        url = MetadataUrl(url)
        CandidatesForValidation(url)
        Using content = Await ReadMetadataAsync(url, token).ConfigureAwait(False)
            Dim value = Await content.ReadAsStringAsync().ConfigureAwait(False)
            token.ThrowIfCancellationRequested()
            Return value
        End Using
    End Function

    ' PCL SendRequest separates headers from body reads. Enforce this tool's byte
    ' bound while streaming, so a known bad response does not delay source fallback.
    Private Async Function ReadMetadataAsync(url As String, token As CancellationToken) As Task(Of ByteArrayContent)
        Const maximum As Integer = 32 * 1024 * 1024
        Using deadline = CancellationTokenSource.CreateLinkedTokenSource(token)
            deadline.CancelAfter(TimeSpan.FromSeconds(45))
            Try
                Using client = CreateClient(), response = Await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(False)
                    response.EnsureSuccessStatusCode()
                    If response.Content.Headers.ContentLength.HasValue AndAlso response.Content.Headers.ContentLength.Value > maximum Then Throw New IO.InvalidDataException("元数据超过 32 MiB 大小限制。")
                    ' net48 stream reads may surface disposal/IO errors on cancellation;
                    ' close the response and normalize the caller's cancellation below.
                    Using registration = deadline.Token.Register(Sub() response.Dispose()),
                          input = Await response.Content.ReadAsStreamAsync().ConfigureAwait(False), output As New IO.MemoryStream()
                        Dim buffer(16383) As Byte
                        Do
                            Dim count = Await input.ReadAsync(buffer, 0, CInt(Math.Min(buffer.Length, maximum - output.Length + 1)), deadline.Token).ConfigureAwait(False)
                            If count = 0 Then Exit Do
                            If output.Length + count > maximum Then Throw New IO.InvalidDataException("元数据超过 32 MiB 大小限制。")
                            output.Write(buffer, 0, count)
                        Loop
                        token.ThrowIfCancellationRequested()
                        Dim content As New ByteArrayContent(output.ToArray())
                        content.Headers.ContentType = response.Content.Headers.ContentType
                        Return content
                    End Using
                End Using
            Catch ex As Exception When token.IsCancellationRequested
                Throw New OperationCanceledException("元数据请求已取消。", ex, token)
            Catch ex As Exception When deadline.IsCancellationRequested
                Throw New TaskCanceledException("元数据读取超时。", ex)
            End Try
        End Using
    End Function

    ' PCL DlSourceLoader starts the preferred loader first, then starts a fallback
    ' after a grace period. Return the first validated result and cancel the rest.
    Public Async Function RaceJsonAsync(urls As IEnumerable(Of String), token As CancellationToken, Optional sha1 As String = Nothing, Optional fallbackDelay As Integer = 1500) As Task(Of JObject)
        Return DirectCast(Await RaceValueAsync(urls, token, sha1, fallbackDelay).ConfigureAwait(False), JObject)
    End Function

    Private Async Function RaceValueAsync(urls As IEnumerable(Of String), token As CancellationToken, sha1 As String, fallbackDelay As Integer, Optional validate As Action(Of JToken) = Nothing, Optional observed As Action(Of String, TimeSpan) = Nothing, Optional parse As Func(Of String, String, JToken) = Nothing) As Task(Of JToken)
        Dim pending As New List(Of Task(Of JToken))
        Dim failures As New List(Of Exception)
        Dim candidates = urls.Distinct().ToList()
        For Each url In candidates
            CandidatesForValidation(url)
        Next
        Using linked = CancellationTokenSource.CreateLinkedTokenSource(token)
            Dim fallbackNow As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
            For i = 0 To candidates.Count - 1
                pending.Add(FetchJsonAsync(candidates(i), linked.Token, sha1, i * fallbackDelay, fallbackNow.Task, validate, observed, parse))
            Next
            Dim result As JToken = Nothing
            While pending.Count > 0
                Dim finished = Await Task.WhenAny(pending).ConfigureAwait(False)
                pending.Remove(finished)
                Try
                    result = Await finished.ConfigureAwait(False)
                    Exit While
                Catch ex As Exception
                    failures.Add(ex)
                    fallbackNow.TrySetResult(True)
                    If token.IsCancellationRequested Then Exit While
                End Try
            End While
            linked.Cancel()
            ' Observe and join losing requests, including streaming cancellation.
            Try
                Await Task.WhenAll(pending).ConfigureAwait(False)
            Catch ex As Exception
                If result Is Nothing Then failures.Add(ex)
            End Try
            token.ThrowIfCancellationRequested()
            If result IsNot Nothing Then Return result
        End Using
        Throw New AggregateException("所有元数据来源均失败。", failures)
    End Function

    Private Sub CandidatesForValidation(url As String)
        Dim uri As New Uri(url, UriKind.Absolute)
        If uri.Scheme <> "https" AndAlso Not (AllowLoopback AndAlso uri.IsLoopback AndAlso uri.Scheme = "http") Then Throw New ArgumentException("下载地址必须使用 HTTPS。")
    End Sub

    ' Transport seam retains source identity when exercising the selection policy.
    Protected Overridable Function MetadataUrl(source As String) As String
        Return source
    End Function

    Private Async Function FetchJsonAsync(source As String, token As CancellationToken, sha1 As String, delay As Integer, fallbackNow As Task, validate As Action(Of JToken), observed As Action(Of String, TimeSpan), parse As Func(Of String, String, JToken)) As Task(Of JToken)
                If delay > 0 Then Await Task.WhenAny(Task.Delay(delay, token), fallbackNow).ConfigureAwait(False)
                token.ThrowIfCancellationRequested()
                Dim clock = Stopwatch.StartNew()
                Dim url = MetadataUrl(source)
                CandidatesForValidation(url)
                Using content = Await ReadMetadataAsync(url, token).ConfigureAwait(False)
                    Dim bytes = Await content.ReadAsByteArrayAsync().ConfigureAwait(False)
                    If Not String.IsNullOrWhiteSpace(sha1) Then
                        Using hash = Security.Cryptography.SHA1.Create()
                            If Not String.Equals(BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", ""), sha1, StringComparison.OrdinalIgnoreCase) Then Throw New IO.InvalidDataException("元数据 SHA-1 校验失败。")
                        End Using
                    End If
                    Dim body = Text.Encoding.UTF8.GetString(bytes)
                    Dim result = If(parse Is Nothing, JToken.Parse(body), parse(source, body))
                    validate?.Invoke(result)
                    token.ThrowIfCancellationRequested()
                    observed?.Invoke(source, clock.Elapsed)
                    Return result
                End Using
    End Function

    Public Shared Function CreateClient() As HttpClient
        Dim client As New HttpClient(New HttpClientHandler With {.AutomaticDecompression = Net.DecompressionMethods.None, .MaxConnectionsPerServer = 16}) With {.Timeout = TimeSpan.FromSeconds(45)}
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PclDownloadBridge/0.52 (local Minecraft installer)")
        Return client
    End Function
End Class
