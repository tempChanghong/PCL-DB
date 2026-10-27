Imports System.Collections.Concurrent
Imports System.Collections.ObjectModel
Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Newtonsoft.Json.Linq

Public NotInheritable Class CurseForgeResolvedFile
    Public ReadOnly Property Reference As CurseForgeReference
    Public ReadOnly Property Download As DownloadFile
    Public ReadOnly Property InformationSource As String
    Public ReadOnly Property ChecksumSource As String
    Friend Sub New(reference As CurseForgeReference, file As DownloadFile, source As String, checksum As String)
        Me.Reference = reference : Download = file : InformationSource = source : ChecksumSource = checksum
    End Sub
End Class

Public NotInheritable Class CurseForgeQueryException
    Inherits IOException
    Public ReadOnly Property Failures As ReadOnlyCollection(Of String)
    Public Sub New(failures As IEnumerable(Of String))
        Me.New(failures.ToList())
    End Sub
    Private Sub New(failures As List(Of String))
        MyBase.New($"整合包的 {failures.Count} 个文件信息查询未完成，未开始安装。不会跳过缺失或无校验依据的文件。" & Environment.NewLine &
            String.Join(Environment.NewLine, failures.Take(8)) & If(failures.Count > 8, Environment.NewLine & "其余查询失败记录见日志。", ""))
        Me.Failures = failures.AsReadOnly()
    End Sub
    Public Overrides Function ToString() As String
        Return MyBase.ToString() & Environment.NewLine & "CurseForge query failures:" & Environment.NewLine & String.Join(Environment.NewLine, Failures)
    End Function
End Class

' PCL DlModRequest / InstallPackCurseForge file identity and hash semantics.
' The public website API is an independent keyless adapter, not PCL credentials.
Public Class CurseForgeFileResolver
    Implements IDisposable
    Private ReadOnly _policy As SourcePolicy
    Private ReadOnly _client As HttpClient
    Private ReadOnly _ownsClient As Boolean
    Private ReadOnly _apiKey As String
    Public Sub New(policy As SourcePolicy, Optional client As HttpClient = Nothing, Optional apiKey As String = Nothing)
        _policy = policy : _apiKey = If(apiKey, Environment.GetEnvironmentVariable("PCLDB_CURSEFORGE_API_KEY"))
        _ownsClient = client Is Nothing
        _client = If(client, New HttpClient(New HttpClientHandler With {.AllowAutoRedirect = False, .AutomaticDecompression = DecompressionMethods.GZip Or DecompressionMethods.Deflate}) With {.Timeout = TimeSpan.FromSeconds(20)})
        If _ownsClient Then _client.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-DB/1.0 (+https://github.com/tempChanghong/PCL-DB)")
    End Sub
    Public Async Function ResolveAsync(package As CurseForgePackage, optionalFileIds As IEnumerable(Of Long), token As CancellationToken, Optional progress As IProgress(Of TransferProgress) = Nothing) As Task(Of List(Of CurseForgeResolvedFile))
        If package Is Nothing Then Throw New ArgumentNullException(NameOf(package))
        Dim selected As New HashSet(Of Long)(If(optionalFileIds, Enumerable.Empty(Of Long)()))
        If selected.Any(Function(id) Not package.References.Any(Function(item) Not item.Required AndAlso item.FileId = id)) Then Throw New ArgumentException("可选文件选择不属于此整合包。")
        Dim references = package.References.Where(Function(item) item.Required OrElse selected.Contains(item.FileId)).ToList()
        Dim errors As New ConcurrentQueue(Of String), results(references.Count - 1) As CurseForgeResolvedFile, completed As Integer
        Using gate As New SemaphoreSlim(4), linked = CancellationTokenSource.CreateLinkedTokenSource(token)
            Dim tasks = references.Select(Async Function(reference, index)
                Await gate.WaitAsync(linked.Token).ConfigureAwait(False)
                Try
                    Try
                        results(index) = Await ResolveFileAsync(reference, package.Minecraft, linked.Token).ConfigureAwait(False)
                    Catch ex As Exception When Not TypeOf ex Is OperationCanceledException
                        errors.Enqueue($"项目 {reference.ProjectId} / 文件 {reference.FileId}：{ex.Message}")
                    End Try
                    Dim count = Interlocked.Increment(completed)
                    progress?.Report(New TransferProgress With {.Phase = "metadata", .Stage = "查询整合包文件", .Detail = $"已查询 {count}/{references.Count}，失败 {errors.Count}", .StageFraction = If(references.Count = 0, 1, count / CDbl(references.Count)), .PhaseTotal = references.Count, .PhaseCompleted = count})
                Catch
                    linked.Cancel() : Throw
                Finally
                    gate.Release()
                End Try
            End Function).ToArray()
            Await Task.WhenAll(tasks).ConfigureAwait(False)
        End Using
        token.ThrowIfCancellationRequested()
        If Not errors.IsEmpty Then Throw New CurseForgeQueryException(errors)
        Return results.ToList()
    End Function
    Public Async Function ResolveFileAsync(reference As CurseForgeReference, mc As String, token As CancellationToken) As Task(Of CurseForgeResolvedFile)
        If reference Is Nothing Then Throw New ArgumentNullException(NameOf(reference))
        Dim path = $"/v1/mods/{reference.ProjectId}/files/{reference.FileId}"
        Dim publicUrl = "https://www.curseforge.com/api" & path
        Dim sources = If(_policy.EffectiveVersionPreference = SourcePreference.MirrorFirst,
            {"https://mod.mcimirror.top/curseforge" & path, publicUrl}, {publicUrl, "https://mod.mcimirror.top/curseforge" & path}).ToList()
        If Not String.IsNullOrWhiteSpace(_apiKey) Then sources.Insert(0, "https://api.curseforge.com" & path)
        Dim failures As New List(Of String)
        For Each source In sources
            token.ThrowIfCancellationRequested()
            Try
                Dim data = TryCast((Await JsonAsync(source, token).ConfigureAwait(False))("data"), JObject)
                If data Is Nothing Then Throw New InvalidDataException("指定文件不存在或未公开")
                If CurseForgePackage.Number(data, "id") <> reference.FileId OrElse CurseForgePackage.Number(data, If(data("modId") Is Nothing, "projectId", "modId")) <> reference.ProjectId Then Throw New InvalidDataException("返回的项目或文件 ID 不匹配")
                If data("gameId") IsNot Nothing AndAlso CurseForgePackage.Number(data, "gameId") <> 432 Then Throw New InvalidDataException("文件不属于 Minecraft")
                For Each field In {"isAvailable", "isCompatibleWithClient"}
                    If data(field) IsNot Nothing AndAlso (data(field).Type <> JTokenType.Boolean OrElse Not CBool(data(field))) Then Throw New InvalidDataException("文件不可用于客户端")
                Next
                If data("isEarlyAccessContent") IsNot Nothing AndAlso (data("isEarlyAccessContent").Type <> JTokenType.Boolean OrElse CBool(data("isEarlyAccessContent"))) Then Throw New InvalidDataException("文件需要提前访问权限")
                If data("status") IsNot Nothing AndAlso CurseForgePackage.Number(data, "status") <> 4 Then Throw New InvalidDataException("文件未处于公开发布状态")
                Dim games = TryCast(data("gameVersions"), JArray)
                If games Is Nothing OrElse games.Any(Function(value) value.Type <> JTokenType.String) OrElse Not games.Values(Of String)().Contains(mc) Then Throw New InvalidDataException("文件未声明支持整合包指定的 Minecraft 版本")
                Dim filename = CurseForgePackage.TextValue(data, "fileName")
                SafeFiles.ValidatePathSegment(filename)
                Dim folder = "mods/"
                If Not filename.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) Then
                    Dim project = TryCast((Await JsonAsync(source.Substring(0, source.LastIndexOf("/files/", StringComparison.Ordinal)), token).ConfigureAwait(False))("data"), JObject)
                    If project Is Nothing OrElse CurseForgePackage.Number(project, "id") <> reference.ProjectId Then Throw New InvalidDataException("无法识别非 JAR 文件类型")
                    Dim classId = CurseForgePackage.Number(project, "classId")
                    If Not filename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) OrElse (classId <> 12 AndAlso classId <> 6552) Then Throw New NotSupportedException("当前导入只支持模组 JAR、资源包 ZIP 和光影 ZIP")
                    folder = If(classId = 12, "resourcepacks/", "shaderpacks/")
                End If
                Dim file As New DownloadFile With {.RelativePath = folder & filename, .Size = CurseForgePackage.Number(data, "fileLength"), .Url = publicUrl & "/download", .Category = "modpack"}
                Dim suppliedUrl = CurseForgePackage.TextValue(data, "downloadUrl", False)
                If Not String.IsNullOrWhiteSpace(suppliedUrl) Then
                    Dim address As Uri = Nothing
                    If Not Uri.TryCreate(suppliedUrl, UriKind.Absolute, address) OrElse Not IsBoundCdn(address, reference.FileId, filename) Then Throw New InvalidDataException("API 下载地址与指定的官方 CDN 文件身份不一致")
                    file.Url = address.AbsoluteUri
                End If
                Dim hashes = data("hashes")
                If hashes IsNot Nothing AndAlso hashes.Type <> JTokenType.Null Then
                    If Not TypeOf hashes Is JArray Then Throw New InvalidDataException("文件摘要列表无效")
                    For Each value In DirectCast(hashes, JArray)
                        Dim hash = TryCast(value, JObject)
                        If hash Is Nothing Then Throw New InvalidDataException("文件摘要条目无效")
                        Dim algorithm = CurseForgePackage.Number(hash, "algo"), digest = CurseForgePackage.TextValue(hash, "value").ToLowerInvariant()
                        If algorithm = 1 OrElse algorithm = 2 Then
                            If Not Regex.IsMatch(digest, If(algorithm = 1, "\A[0-9a-f]{40}\z", "\A[0-9a-f]{32}\z")) Then Throw New InvalidDataException("文件摘要无效")
                            If algorithm = 1 Then
                                If file.Sha1 IsNot Nothing AndAlso file.Sha1 <> digest Then Throw New InvalidDataException("SHA-1 声明冲突")
                                file.Sha1 = digest
                            Else
                                If file.Md5 IsNot Nothing AndAlso file.Md5 <> digest Then Throw New InvalidDataException("MD5 声明冲突")
                                file.Md5 = digest
                            End If
                        End If
                    Next
                End If
                Dim checksumSource = "CurseForge API 摘要"
                If file.Sha1 Is Nothing AndAlso file.Md5 Is Nothing Then
                    Await BindS3ChecksumAsync(file, reference.FileId, filename, token).ConfigureAwait(False)
                    checksumSource = If(file.Md5 IsNot Nothing, "官方 CDN S3 单段 MD5", "官方 CDN S3 单分片组合 MD5")
                End If
                Return New CurseForgeResolvedFile(reference, file, New Uri(source).Host, checksumSource)
            Catch ex As Exception When Not TypeOf ex Is OperationCanceledException
                token.ThrowIfCancellationRequested()
                failures.Add(New Uri(source).Host & "：" & ex.Message)
            End Try
        Next
        Throw New InvalidDataException(String.Join("；", failures))
    End Function
    Private Async Function JsonAsync(url As String, token As CancellationToken) As Task(Of JObject)
        Using deadline = CancellationTokenSource.CreateLinkedTokenSource(token)
            deadline.CancelAfter(TimeSpan.FromSeconds(20))
            Try
                Return Await JsonCoreAsync(url, deadline.Token).ConfigureAwait(False)
            Catch ex As Exception When token.IsCancellationRequested
                Throw New OperationCanceledException("CurseForge 查询已取消。", ex, token)
            Catch ex As OperationCanceledException
                Throw New IOException("CurseForge 文件信息查询超时。", ex)
            End Try
        End Using
    End Function
    Private Async Function JsonCoreAsync(url As String, token As CancellationToken) As Task(Of JObject)
        Using request As New HttpRequestMessage(HttpMethod.Get, url)
            If New Uri(url).Host = "api.curseforge.com" AndAlso Not String.IsNullOrWhiteSpace(_apiKey) Then request.Headers.Add("x-api-key", _apiKey)
            Using response = Await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(False)
                response.EnsureSuccessStatusCode()
                Using registration = token.Register(Sub() response.Dispose()), input = Await response.Content.ReadAsStreamAsync().ConfigureAwait(False), output As New MemoryStream()
                    Dim buffer(8191) As Byte
                    Do
                        Dim count = Await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(False)
                        If count = 0 Then Exit Do
                        If output.Length + count > 2 * 1024 * 1024 Then Throw New InvalidDataException("CurseForge 文件信息超过大小限制")
                        output.Write(buffer, 0, count)
                    Loop
                    Return JObject.Parse(Encoding.UTF8.GetString(output.ToArray()), New JsonLoadSettings With {.DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error})
                End Using
            End Using
        End Using
    End Function
    Private Async Function BindS3ChecksumAsync(file As DownloadFile, id As Long, filename As String, token As CancellationToken) As Task
        Using deadline = CancellationTokenSource.CreateLinkedTokenSource(token)
            deadline.CancelAfter(TimeSpan.FromSeconds(30))
            Try
                Await BindS3ChecksumCoreAsync(file, id, filename, deadline.Token).ConfigureAwait(False)
            Catch ex As Exception When token.IsCancellationRequested
                Throw New OperationCanceledException("CDN 校验信息查询已取消。", ex, token)
            Catch ex As OperationCanceledException
                Throw New IOException("CDN 校验信息查询超时。", ex)
            End Try
        End Using
    End Function
    Private Async Function BindS3ChecksumCoreAsync(file As DownloadFile, id As Long, filename As String, token As CancellationToken) As Task
        Dim url = file.Url
        For redirect = 0 To 5
            Using request As New HttpRequestMessage(HttpMethod.Head, url), response = Await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(False)
                If CInt(response.StatusCode) >= 300 AndAlso CInt(response.StatusCode) < 400 Then
                    If response.Headers.Location Is Nothing Then Throw New InvalidDataException("文件重定向缺少地址")
                    Dim nextUrl As New Uri(New Uri(url), response.Headers.Location)
                    If Not IsCdn(nextUrl) Then Throw New InvalidDataException("文件重定向未指向官方 CDN")
                    url = nextUrl.AbsoluteUri : Continue For
                End If
                response.EnsureSuccessStatusCode()
                Dim finalUri = If(response.RequestMessage?.RequestUri, New Uri(url))
                If Not IsBoundCdn(finalUri, id, filename) Then Throw New InvalidDataException("官方 CDN 文件地址与指定 ID/文件名不一致")
                Dim encryption As IEnumerable(Of String) = Nothing
                If Not response.Content.Headers.ContentLength.HasValue OrElse response.Content.Headers.ContentLength.Value <> file.Size OrElse response.Content.Headers.ContentEncoding.Any() OrElse Not response.Headers.Server.Any(Function(server) server.Product?.Name = "AmazonS3") OrElse Not response.Headers.TryGetValues("x-amz-server-side-encryption", encryption) OrElse encryption.SingleOrDefault() <> "AES256" Then Throw New InvalidDataException("CDN 未提供可确认的单段 S3 校验依据")
                Dim etag = response.Headers.ETag
                If etag Is Nothing OrElse etag.IsWeak Then Throw New InvalidDataException("CDN 缺少可靠摘要")
                Dim digest = etag.Tag.Trim(""""c).ToLowerInvariant()
                If Regex.IsMatch(digest, "\A[0-9a-f]{32}\z") Then
                    file.Md5 = digest
                ElseIf Regex.IsMatch(digest, "\A[0-9a-f]{32}-1\z") Then
                    file.S3SinglePartETag = digest
                Else
                    Throw New InvalidDataException("CDN 多分片或未知 ETag 不能作为完整文件 MD5")
                End If
                file.Url = finalUri.AbsoluteUri
                Return
            End Using
        Next
        Throw New InvalidDataException("文件重定向过多")
    End Function
    Private Shared Function IsCdn(uri As Uri) As Boolean
        Return uri.Scheme = "https" AndAlso uri.IsDefaultPort AndAlso uri.UserInfo.Length = 0 AndAlso {"mediafilez.forgecdn.net", "edge.forgecdn.net", "media.forgecdn.net"}.Contains(uri.Host)
    End Function
    Private Shared Function IsBoundCdn(uri As Uri, id As Long, filename As String) As Boolean
        If Not IsCdn(uri) Then Return False
        Dim match = Regex.Match(uri.AbsolutePath, "\A/files/([0-9]+)/([0-9]+)/([^/]+)\z")
        Return match.Success AndAlso Long.Parse(match.Groups(1).Value) = id \ 1000 AndAlso Long.Parse(match.Groups(2).Value) = id Mod 1000 AndAlso Uri.UnescapeDataString(match.Groups(3).Value) = filename
    End Function
    Public Sub Dispose() Implements IDisposable.Dispose
        If _ownsClient Then _client.Dispose()
    End Sub
End Class
