Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Security.Cryptography
Imports System.Threading

' Outer catalogue archives have strict declared sizes. This is deliberately
' separate from mrpack inner fileSize's advisory-size compatibility behavior.
Public NotInheritable Class ModpackArchivePolicy
    Private Sub New()
    End Sub
    Public Shared Sub Validate(value As String, platform As ModpackPlatform, allowLoopback As Boolean)
        If platform <> ModpackPlatform.CurseForge AndAlso platform <> ModpackPlatform.Modrinth Then Throw New InvalidDataException("整合包平台无效。")
        Dim address As Uri = Nothing
        If String.IsNullOrWhiteSpace(value) OrElse value.Any(Function(ch) Char.IsWhiteSpace(ch) OrElse Char.IsControl(ch)) OrElse value.Contains("\"c) OrElse Not Uri.TryCreate(value, UriKind.Absolute, address) Then Throw New InvalidDataException("整合包下载地址无效。")
        If allowLoopback AndAlso address.IsLoopback AndAlso address.Scheme = "http" AndAlso address.UserInfo = "" Then Return
        If address.Scheme <> "https" OrElse Not address.IsDefaultPort OrElse address.UserInfo <> "" OrElse address.Fragment <> "" Then Throw New InvalidDataException("整合包下载地址必须使用标准 HTTPS。")
        Dim allowed = If(platform = ModpackPlatform.Modrinth, {"cdn.modrinth.com"}, If(platform = ModpackPlatform.CurseForge, {"edge.forgecdn.net", "mediafilez.forgecdn.net", "media.forgecdn.net"}, New String() {}))
        If Not allowed.Contains(address.Host, StringComparer.OrdinalIgnoreCase) Then Throw New InvalidDataException("整合包下载主机与内容平台不匹配。")
    End Sub
    Friend Shared Function CreateClient(platform As ModpackPlatform, allowLoopback As Boolean) As HttpClient
        Dim client As New HttpClient(New ArchiveRedirects(platform, allowLoopback)) With {.Timeout = Timeout.InfiniteTimeSpan}
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PclDownloadBridge/5.0.0-beta.1 (modpack archive)")
        Return client
    End Function
    Private Class ArchiveRedirects
        Inherits DelegatingHandler
        Private ReadOnly _platform As ModpackPlatform
        Private ReadOnly _loopback As Boolean
        Public Sub New(platform As ModpackPlatform, loopback As Boolean)
            MyBase.New(New HttpClientHandler With {.AllowAutoRedirect = False, .AutomaticDecompression = DecompressionMethods.None, .MaxConnectionsPerServer = 16})
            _platform = platform : _loopback = loopback
        End Sub
        Protected Overrides Async Function SendAsync(request As HttpRequestMessage, token As CancellationToken) As Task(Of HttpResponseMessage)
            Dim address = request.RequestUri, seen As New HashSet(Of String)(StringComparer.Ordinal)
            For hop = 0 To 5
                Validate(address.AbsoluteUri, _platform, _loopback)
                If Not seen.Add(address.AbsoluteUri) Then Throw New InvalidDataException("整合包下载重定向形成循环。")
                Using forwarded As New HttpRequestMessage(request.Method, address)
                    For Each header In request.Headers : forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value) : Next
                    Dim response = Await MyBase.SendAsync(forwarded, token).ConfigureAwait(False)
                    If Not {301, 302, 303, 307, 308}.Contains(CInt(response.StatusCode)) Then Return response
                    Try
                        If hop = 5 OrElse response.Headers.Location Is Nothing Then Throw New InvalidDataException("整合包下载重定向过多或缺少地址。")
                        address = New Uri(address, response.Headers.Location)
                    Finally
                        response.Dispose()
                    End Try
                End Using
            Next
            Throw New InvalidDataException("整合包下载重定向失败。")
        End Function
    End Class
End Class

Public NotInheritable Class ModpackArchiveInstaller
    Private Sub New()
    End Sub
    Public Shared Async Function AcquireAsync(release As ModpackRelease, root As String, policy As SourcePolicy, progress As IProgress(Of TransferProgress), token As CancellationToken, Optional connections As Integer = 16, Optional limit As Long = 0) As Task(Of String)
        release = release.Copy()
        ModpackArchivePolicy.Validate(release.Url, release.Platform, policy.AllowLoopback)
        If release.Size <= 0 OrElse (release.Sha1 Is Nothing AndAlso release.Sha512 Is Nothing AndAlso release.Md5 Is Nothing) Then Throw New InvalidDataException("归档缺少有效大小或校验摘要。")
        Dim key As String
        Using sha = SHA256.Create()
            key = BitConverter.ToString(sha.ComputeHash(Text.Encoding.UTF8.GetBytes(release.Platform.ToString() & "|" & release.ProjectId & "|" & release.Id & "|" & release.Sha1 & "|" & release.Sha512 & "|" & release.Md5))).Replace("-", "").ToLowerInvariant()
        End Using
        Dim relative = If(release.Kind = CommunityResourceKind.Modpack,
            ".vanilla-downloader/pack-archives/" & key & "/original." & If(release.Platform = ModpackPlatform.Modrinth, "mrpack", "zip"),
            ".vanilla-downloader/community-files/" & release.Kind.ToString() & "/" & key & "/" & release.FileName)
        Dim file As New DownloadFile With {.RelativePath = relative, .Url = release.Url, .Size = release.Size, .Sha1 = release.Sha1, .Sha512 = release.Sha512, .Md5 = release.Md5, .Category = If(release.Platform = ModpackPlatform.Modrinth, "modrinth-archive", "curseforge-archive")}
        If release.Platform = ModpackPlatform.CurseForge Then
            file.AlternateUrls = {"edge.forgecdn.net", "mediafilez.forgecdn.net", "media.forgecdn.net"}.Select(Function(host) New UriBuilder(release.Url) With {.Host = host}).Select(Function(address) address.Uri.AbsoluteUri).Where(Function(address) address <> release.Url).ToArray()
        End If
        Dim leasePath = SafeFiles.UnderRoot(root, ".vanilla-downloader/pack-archives/" & key & "/acquire.lock")
        Directory.CreateDirectory(IO.Path.GetDirectoryName(leasePath))
        Using lease = Await AcquireLeaseAsync(leasePath, token).ConfigureAwait(False), budget As New DownloadBudget(connections, limit), engine As New DownloadEngine(policy, progress, Enumerable.Empty(Of String)(), connections, root, useComponentCache:=True, budget:=budget)
            Await engine.DownloadAsync(root, {file}, token).ConfigureAwait(False)
        End Using
        token.ThrowIfCancellationRequested()
        Dim path = SafeFiles.UnderRoot(root, relative)
        If Not SafeFiles.Matches(path, file) Then Throw New InvalidDataException("整合包归档校验未通过。")
        Return path
    End Function
    Private Shared Async Function AcquireLeaseAsync(path As String, token As CancellationToken) As Task(Of FileStream)
        Dim clock = Diagnostics.Stopwatch.StartNew()
        Do
            token.ThrowIfCancellationRequested()
            Try
                Return New FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
            Catch ex As IOException When clock.Elapsed < TimeSpan.FromMinutes(3)
            End Try
            Await Task.Delay(100, token).ConfigureAwait(False)
        Loop
    End Function

    ' Online and local acquisition converge at the same content detection,
    ' parsers and installers; catalogue display metadata never supplies dependencies.
    Public Shared Function ReadPackage(path As String, token As CancellationToken) As Object
        If ModpackFormats.Detect(path) = ModpackFormat.Modrinth Then Return ModrinthPackage.Read(path, token)
        Return CurseForgePackage.Read(path, token)
    End Function
    Private Shared Function ReadSelectedPackage(path As String, release As ModpackRelease, token As CancellationToken) As Object
        token.ThrowIfCancellationRequested()
        ' PCL's detail page hands a completed archive to ModpackInstall. Our cache is
        ' shared across retries: bind that handoff to the selected release while a
        ' read lease prevents changes through detection, parsing and SHA-256 capture.
        ' The import plan checks that captured SHA-256 again after optional selection.
        Using lease As New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
            If lease.Length <> release.Size Then Throw New InvalidDataException("整合包归档在下载后发生变化，请重试。")
            Dim hashes As New List(Of Tuple(Of HashAlgorithm, String))
            Try
                If release.Sha1 IsNot Nothing Then hashes.Add(Tuple.Create(Of HashAlgorithm, String)(SHA1.Create(), release.Sha1))
                If release.Sha512 IsNot Nothing Then hashes.Add(Tuple.Create(Of HashAlgorithm, String)(SHA512.Create(), release.Sha512))
                If release.Md5 IsNot Nothing Then hashes.Add(Tuple.Create(Of HashAlgorithm, String)(MD5.Create(), release.Md5))
                If hashes.Count = 0 Then Throw New InvalidDataException("归档缺少校验摘要。")
                Dim buffer(65535) As Byte
                Do
                    token.ThrowIfCancellationRequested()
                    Dim count = lease.Read(buffer, 0, buffer.Length)
                    If count = 0 Then Exit Do
                    For Each hash In hashes : hash.Item1.TransformBlock(buffer, 0, count, buffer, 0) : Next
                Loop
                For Each hash In hashes
                    hash.Item1.TransformFinalBlock(New Byte() {}, 0, 0)
                    If Not String.Equals(BitConverter.ToString(hash.Item1.Hash).Replace("-", ""), hash.Item2, StringComparison.OrdinalIgnoreCase) Then Throw New InvalidDataException("整合包归档在下载后发生变化，请重试。")
                Next
                token.ThrowIfCancellationRequested()
                Return ReadPackage(path, token)
            Finally
                For Each hash In hashes : hash.Item1.Dispose() : Next
            End Try
        End Using
    End Function
    Public Shared Async Function InstallAsync(release As ModpackRelease, root As String, name As String, policy As SourcePolicy, pclDirectory As String, reuse As IEnumerable(Of String), progress As IProgress(Of TransferProgress), token As CancellationToken, Optional connections As Integer = 16, Optional limit As Long = 0, Optional javaPath As String = Nothing, Optional chooseOptional As Func(Of ModrinthPackage, CancellationToken, Task(Of IEnumerable(Of String))) = Nothing, Optional launcherTarget As LauncherInstallTarget = Nothing) As Task(Of InstallResult)
        release = release.Copy()
        token.ThrowIfCancellationRequested()
        If release.Kind <> CommunityResourceKind.Modpack Then Throw New InvalidOperationException("普通资源请使用文件下载，不会创建游戏实例。")
        launcherTarget?.ValidateDestination(root)
        InstallPreflight.ValidateDestination(root, name, False)
        Dim blocked = ModpackCompatibility.GetInstallBlockReason(release)
        If blocked IsNot Nothing Then Throw New NotSupportedException(blocked)
        Dim archiveBytes As Long, archiveReused As Integer, archiveSplits As Integer
        Dim archiveProgress As New InlineProgress(Sub(value)
                                                     archiveBytes = Math.Max(archiveBytes, value.NetworkBytes)
                                                     archiveReused = Math.Max(archiveReused, value.Reused) : archiveSplits = Math.Max(archiveSplits, value.DynamicSplits)
                                                     value.StageFraction = If(value.Total > 0 AndAlso value.Completed >= value.Total, 1, Math.Min(0.95, value.NetworkBytes / CDbl(release.Size)))
                                                     value.Phase = "archive" : value.Stage = "下载并校验整合包归档"
                                                     progress?.Report(value)
                                                 End Sub)
        Dim path = Await AcquireAsync(release, root, policy, archiveProgress, token, connections, limit).ConfigureAwait(False)
        progress?.Report(New TransferProgress With {.Phase = "archive", .Stage = "整合包归档已校验", .StageFraction = 1, .NetworkBytes = archiveBytes, .Reused = archiveReused, .DynamicSplits = archiveSplits})
        Dim parsed = Await Task.Run(Function() ReadSelectedPackage(path, release, token), token).ConfigureAwait(False)
        token.ThrowIfCancellationRequested()
        Dim result As InstallResult
        If release.Platform = ModpackPlatform.Modrinth Then
            Dim package = TryCast(parsed, ModrinthPackage)
            If package Is Nothing Then Throw New InvalidDataException("下载文件与所选 Modrinth 格式不符。")
            ModpackCompatibility.ValidateManifest(release, package.Minecraft, package.LoaderKind)
            Dim selected = If(chooseOptional Is Nothing, Enumerable.Empty(Of String)(), Await chooseOptional(package, token).ConfigureAwait(False))
            token.ThrowIfCancellationRequested()
            result = Await New ModrinthInstaller(policy, pclDirectory, launcherTarget).InstallAsync(package, root, name, reuse, selected, progress, token, connections, limit, javaPath).ConfigureAwait(False)
        Else
            Dim package = TryCast(parsed, CurseForgePackage)
            If package Is Nothing Then Throw New InvalidDataException("下载文件与所选 CurseForge 格式不符。")
            ModpackCompatibility.ValidateManifest(release, package.Minecraft, package.LoaderKind)
            result = Await New CurseForgeInstaller(policy, pclDirectory, launcherTarget:=launcherTarget).InstallAsync(package, root, name, reuse, package.References.Where(Function(file) Not file.Required).Select(Function(file) file.FileId), progress, token, connections, limit, javaPath).ConfigureAwait(False)
        End If
        result.NetworkBytes += archiveBytes : result.Reused += archiveReused : result.DynamicSplits += archiveSplits
        Return result
    End Function
    Private Class InlineProgress
        Implements IProgress(Of TransferProgress)
        Private ReadOnly _report As Action(Of TransferProgress)
        Public Sub New(report As Action(Of TransferProgress))
            _report = report
        End Sub
        Public Sub Report(value As TransferProgress) Implements IProgress(Of TransferProgress).Report
            _report(value)
        End Sub
    End Class
End Class
