Imports System.Collections.Concurrent
Imports System.IO
Imports System.IO.Compression
Imports System.Net
Imports System.Net.Http
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module CurseForgeChecks
    Private Sub Check(value As Boolean, label As String, passed As List(Of String))
        If Not value Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub
    Private Function Digest(bytes As Byte(), Optional md5 As Boolean = False, Optional composite As Boolean = False) As String
        Using hash As System.Security.Cryptography.HashAlgorithm = If(md5, DirectCast(System.Security.Cryptography.MD5.Create(), System.Security.Cryptography.HashAlgorithm), System.Security.Cryptography.SHA1.Create())
            Dim result = hash.ComputeHash(bytes)
            If composite Then result = hash.ComputeHash(result)
            Return BitConverter.ToString(result).Replace("-", "").ToLowerInvariant() & If(composite, "-1", "")
        End Using
    End Function
    Private Function Manifest(Optional loader As String = "fabric-0.16.9") As JObject
        Return New JObject From {{"manifestType", "minecraftModpack"}, {"manifestVersion", 1}, {"name", " Fixture pack "}, {"version", "1.0"}, {"overrides", "overrides"},
            {"minecraft", New JObject From {{"version", "1.20.1"}, {"modLoaders", If(loader Is Nothing, New JArray(), New JArray(New JObject From {{"id", loader}, {"primary", True}}))}}},
            {"files", New JArray(New JObject From {{"projectID", 1}, {"fileID", 1001}, {"required", True}}, New JObject From {{"projectID", 2}, {"fileID", 1002}, {"required", False}})}}
    End Function
    Private Function Pack(root As String, label As String, Optional manifest As JObject = Nothing, Optional entries As Dictionary(Of String, String) = Nothing, Optional prefix As String = "") As String
        Directory.CreateDirectory(root)
        Dim target = Path.Combine(root, "cf-" & label & ".zip")
        Using archive = ZipFile.Open(target, ZipArchiveMode.Create)
            Using writer As New StreamWriter(archive.CreateEntry(prefix & "manifest.json").Open())
                writer.Write(If(manifest, CurseForgeChecks.Manifest()).ToString())
            End Using
            If entries Is Nothing Then entries = New Dictionary(Of String, String) From {{"overrides/config/fixture.txt", "original configuration"}, {"overrides/PCL/Setup.ini", "VersionArgumentIndieV2:False" & vbCrLf & "UserSetting:preserved" & vbCrLf & "IsStar:True"}}
            For Each pair In entries
                Using writer As New StreamWriter(archive.CreateEntry(prefix & pair.Key).Open())
                    writer.Write(pair.Value)
                End Using
            Next
        End Using
        Return target
    End Function
    Private Class Handler
        Inherits HttpMessageHandler
        Friend ReadOnly Requests As New ConcurrentQueue(Of String)
        Friend ReadOnly Bytes As New Dictionary(Of Long, Byte()) From {{1001, Encoding.UTF8.GetBytes("fixture required mod")}, {1002, Encoding.UTF8.GetBytes("fixture optional mod")}}
        Friend Mode As String = "sha1"
        Friend Mutate As Action(Of JObject)
        Friend HeadMutate As Action(Of HttpResponseMessage)
        Friend Seen As Action(Of HttpRequestMessage)
        Friend Suffix As String = ".jar"
        Friend ProjectClass As Long = 12
        Friend MirrorSucceeds As Boolean
        Protected Overrides Function SendAsync(request As HttpRequestMessage, token As CancellationToken) As Task(Of HttpResponseMessage)
            token.ThrowIfCancellationRequested()
            Requests.Enqueue(request.RequestUri.AbsoluteUri) : Seen?.Invoke(request)
            Dim segments = request.RequestUri.AbsolutePath.Split("/"c), id = Long.Parse(segments.Last(Function(part) Long.TryParse(part, Nothing)))
            If (request.RequestUri.Host = "mod.mcimirror.top" AndAlso Not MirrorSucceeds) OrElse Mode = "missing" Then Return Task.FromResult(New HttpResponseMessage(HttpStatusCode.ServiceUnavailable) With {.Content = New StringContent("unavailable")})
            If Not request.RequestUri.AbsolutePath.Contains("/files/") Then Return Task.FromResult(New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New StringContent(New JObject From {{"data", New JObject From {{"id", id}, {"classId", ProjectClass}}}}.ToString())})
            If request.Method = HttpMethod.Head Then
                Dim response As New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New ByteArrayContent(New Byte() {}), .RequestMessage = New HttpRequestMessage(HttpMethod.Head, $"https://mediafilez.forgecdn.net/files/{id \ 1000}/{id Mod 1000}/fixture-{id}{Suffix}")}
                response.Content.Headers.ContentLength = Bytes(id).Length
                response.Headers.Server.ParseAdd("AmazonS3") : response.Headers.Add("x-amz-server-side-encryption", "AES256")
                response.Headers.ETag = New Headers.EntityTagHeaderValue("""" & Digest(Bytes(id), True, Mode = "etag1") & """")
                HeadMutate?.Invoke(response)
                Return Task.FromResult(response)
            End If
            Dim data As New JObject From {{"id", id}, {"projectId", id - 1000}, {"fileName", $"fixture-{id}{Suffix}"}, {"fileLength", Bytes(id).Length}, {"gameVersions", New JArray("1.20.1", "Fabric")}, {"status", 4}, {"isCompatibleWithClient", True}}
            If Mode = "sha1" OrElse Mode = "md5" Then data("hashes") = New JArray(New JObject From {{"algo", If(Mode = "sha1", 1, 2)}, {"value", Digest(Bytes(id), Mode = "md5")}})
            Mutate?.Invoke(data)
            Return Task.FromResult(New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New StringContent(New JObject From {{"data", data}}.ToString())})
        End Function
    End Class
    Private Class UnknownLengthContent
        Inherits HttpContent
        Protected Overrides Function TryComputeLength(ByRef length As Long) As Boolean
            length = 0 : Return False
        End Function
        Protected Overrides Function SerializeToStreamAsync(stream As Stream, context As TransportContext) As Task
            Return Task.FromResult(0)
        End Function
    End Class
    Private Class Policy
        Inherits SourcePolicy
        Private ReadOnly _server As String
        Friend Sub New(server As String)
            _server = server : AllowLoopback = True
        End Sub
        Public Overrides Function Candidates(url As String, category As String) As List(Of String)
            Return New List(Of String) From {_server & New Uri(url).AbsolutePath}
        End Function
        Protected Overrides Function MetadataUrl(source As String) As String
            Return _server & New Uri(source).AbsolutePath
        End Function
    End Class
    Private Class Progress
        Implements IProgress(Of TransferProgress)
        Private ReadOnly _action As Action(Of TransferProgress)
        Friend Sub New(action As Action(Of TransferProgress))
            _action = action
        End Sub
        Public Sub Report(value As TransferProgress) Implements IProgress(Of TransferProgress).Report
            _action(value)
        End Sub
    End Class
    Friend Async Function RunAsync(root As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "curseforge-all" OrElse onlyCase = "curseforge-package" Then Packages(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "curseforge-all" OrElse onlyCase = "curseforge-resolver" Then Await ResolverAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "curseforge-all" OrElse onlyCase = "curseforge-install" Then Await InstallsAsync(root, passed)
        If passed.Count = 0 Then Throw New ArgumentException(onlyCase)
        Return passed
    End Function
    Private Sub Packages(root As String, passed As List(Of String))
        Dim source = Pack(root, "valid"), package = CurseForgePackage.Read(source)
        Check(package.Name = "Fixture pack" AndAlso package.Minecraft = "1.20.1" AndAlso package.LoaderKind.GetValueOrDefault() = ComponentKind.Fabric AndAlso package.LoaderVersion = "0.16.9", "CF package fixes exact game/loader versions and trims the suggested name", passed)
        Check(package.References.Count = 2 AndAlso package.References(0).Required AndAlso Not package.References(1).Required AndAlso package.OverrideFiles.Count = 2, "CF package retains required/optional identities and override files", passed)
        Check(CurseForgePackage.Read(Pack(root, "wrapped", prefix:="one/")).OverrideFiles.Count = 2, "CF package accepts PCL's single wrapping folder", passed)
        Check(Not Directory.Exists(Path.Combine(root, "versions")), "CF parsing creates no game directory", passed)
        Dim changes As New Dictionary(Of String, Action(Of JObject)) From {
            {"format", Sub(value) value("manifestVersion") = 2}, {"type", Sub(value) value("manifestType") = "other"},
            {"id type", Sub(value) value("files")(0)("fileID") = "1001"}, {"id value", Sub(value) value("files")(0)("fileID") = 0},
            {"required type", Sub(value) value("files")(0)("required") = "true"}, {"duplicate identity", Sub(value) value("files")(1)("fileID") = 1001},
            {"loader", Sub(value) value("minecraft")("modLoaders")(0)("id") = "quilt-0.27.0"}, {"unfixed loader", Sub(value) value("minecraft")("modLoaders")(0)("id") = "forge-recommended"},
            {"multiple loaders", Sub(value) DirectCast(value("minecraft")("modLoaders"), JArray).Add(New JObject From {{"id", "forge-47.2.0"}})},
            {"overrides traversal", Sub(value) value("overrides") = "../outside"}}
        For Each pair In changes
            Dim manifest = CurseForgeChecks.Manifest() : pair.Value.Invoke(manifest)
            Dim rejected = False
            Try
                CurseForgePackage.Read(Pack(root, "invalid-" & pair.Key.Replace(" ", "-"), manifest))
            Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is NotSupportedException
                rejected = True
            End Try
            Check(rejected, "CF package rejects " & pair.Key, passed)
        Next
        For Each path In {"overrides/../escape", "/root", "overrides/CON.txt", "overrides/a.", "overrides/a ", "overrides/ads:stream", "overrides//empty"}
            Dim rejected = False
            Try
                CurseForgePackage.Read(Pack(root, "path-" & passed.Count, entries:=New Dictionary(Of String, String) From {{path, "bad"}}))
            Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException
                rejected = True
            End Try
            Check(rejected, "CF package rejects nonportable or escaping ZIP path " & path, passed)
        Next
        For Each entries In {New Dictionary(Of String, String) From {{"overrides/a", "file"}, {"overrides/A", "duplicate"}}, New Dictionary(Of String, String) From {{"overrides/a", "file"}, {"overrides/a/child", "child"}}}
            Dim rejected = False
            Try
                CurseForgePackage.Read(Pack(root, "collision-" & passed.Count, entries:=entries))
            Catch ex As Exception When TypeOf ex Is InvalidDataException OrElse TypeOf ex Is CurseForgeQueryException
                rejected = True
            End Try
            Check(rejected, "CF package rejects duplicate casing or file/ancestor collisions", passed)
        Next
        Dim linked = Pack(root, "symlink")
        Using archive = ZipFile.Open(linked, ZipArchiveMode.Update)
            Dim entry = archive.CreateEntry("overrides/link") : entry.ExternalAttributes = &HA1FF0000
        End Using
        Dim linkedRejected = False
        Try
            CurseForgePackage.Read(linked)
        Catch ex As Exception When TypeOf ex Is InvalidDataException OrElse TypeOf ex Is CurseForgeQueryException
            linkedRejected = True
        End Try
        Check(linkedRejected, "CF package rejects Unix ZIP symlinks without following them", passed)
    End Sub
    Private Async Function ResolverAsync(root As String, passed As List(Of String)) As Task
        Dim package = CurseForgePackage.Read(Pack(root, "resolver"))
        For Each mode In {"sha1", "md5", "etag", "etag1"}
            Using handler As New Handler With {.Mode = mode}, client As New HttpClient(handler), resolver As New CurseForgeFileResolver(New SourcePolicy(), client, "")
                Dim files = Await resolver.ResolveAsync(package, {}, CancellationToken.None)
                Check(files.Count = 1 AndAlso files(0).Reference.FileId = 1001, "CF resolver installs required files and leaves optional files out: " & mode, passed)
                Dim file = files(0).Download, destination = Path.Combine(root, "hash-" & mode)
                IO.File.WriteAllBytes(destination, handler.Bytes(1001))
                Check(SafeFiles.Matches(destination, file) AndAlso files(0).ChecksumSource.Length > 0, "CF declared remote integrity is executable and has explicit provenance: " & mode, passed)
                Dim bad = DirectCast(handler.Bytes(1001).Clone(), Byte()) : bad(0) = bad(0) Xor CByte(1) : IO.File.WriteAllBytes(destination, bad)
                Check(Not SafeFiles.Matches(destination, file), "CF checksum rejects a same-length corruption: " & mode, passed)
                Dim selected = Await resolver.ResolveAsync(package, {1002L}, CancellationToken.None)
                Check(selected.Count = 2 AndAlso selected(1).Reference.FileId = 1002, "CF optional choice retains manifest order and exact file ID: " & mode, passed)
            End Using
        Next
        Dim mutations As New Dictionary(Of String, Action(Of JObject)) From {
            {"wrong file ID", Sub(value) value("id") = 1003}, {"wrong project", Sub(value) value("projectId") = 99},
            {"wrong game", Sub(value) value("gameId") = 1}, {"wrong MC", Sub(value) value("gameVersions") = New JArray("1.21")},
            {"unavailable", Sub(value) value("isAvailable") = False}, {"server-only", Sub(value) value("isCompatibleWithClient") = False},
            {"early access", Sub(value) value("isEarlyAccessContent") = True}, {"invalid size", Sub(value) value("fileLength") = -1},
            {"invalid filename", Sub(value) value("fileName") = "../escape.jar"}, {"bad hash", Sub(value) value("hashes")(0)("value") = "bad"},
            {"foreign download host", Sub(value) value("downloadUrl") = "https://example.invalid/file.jar"},
            {"wrong download identity", Sub(value) value("downloadUrl") = "https://edge.forgecdn.net/files/1/9/fixture-1001.jar"}}
        For Each pair In mutations
            Using handler As New Handler With {.Mutate = pair.Value}, client As New HttpClient(handler), resolver As New CurseForgeFileResolver(New SourcePolicy(), client, "")
                Dim rejected = False
                Try
                    Await resolver.ResolveAsync(package, {}, CancellationToken.None)
                Catch ex As Exception When TypeOf ex Is InvalidDataException OrElse TypeOf ex Is CurseForgeQueryException
                    rejected = ex.Message.Contains("1001")
                End Try
                Check(rejected, "CF resolver fails with exact identity instead of skipping " & pair.Key, passed)
            End Using
        Next
        Dim headMutations As New Dictionary(Of String, Action(Of HttpResponseMessage)) From {
            {"weak ETag", Sub(value) value.Headers.ETag = New Headers.EntityTagHeaderValue("""" & New String("a"c, 32) & """", True)},
            {"multiple parts", Sub(value) value.Headers.ETag = New Headers.EntityTagHeaderValue("""" & New String("a"c, 32) & "-2""")},
            {"unknown encryption", Sub(value) value.Headers.Remove("x-amz-server-side-encryption")},
            {"KMS encryption", Sub(value)
                                   value.Headers.Remove("x-amz-server-side-encryption") : value.Headers.Add("x-amz-server-side-encryption", "aws:kms")
                               End Sub},
            {"non-S3 server", Sub(value) value.Headers.Server.Clear()}, {"length mismatch", Sub(value) value.Content.Headers.ContentLength = 1},
            {"missing length", Sub(value)
                                   value.Content.Dispose() : value.Content = New UnknownLengthContent()
                               End Sub},
            {"wrong CDN identity", Sub(value) value.RequestMessage.RequestUri = New Uri("https://mediafilez.forgecdn.net/files/1/9/fixture-1001.jar")},
            {"non-CDN host", Sub(value) value.RequestMessage.RequestUri = New Uri("https://example.invalid/files/1/1/fixture-1001.jar")}}
        For Each pair In headMutations
            Using handler As New Handler With {.Mode = "etag", .HeadMutate = pair.Value}, client As New HttpClient(handler), resolver As New CurseForgeFileResolver(New SourcePolicy(), client, "")
                Dim rejected = False
                Try
                    Await resolver.ResolveAsync(package, {}, CancellationToken.None)
                Catch ex As Exception When TypeOf ex Is InvalidDataException OrElse TypeOf ex Is CurseForgeQueryException
                    rejected = True
                End Try
                Check(rejected, "CF resolver never guesses an MD5 from " & pair.Key, passed)
            End Using
        Next
        Using handler As New Handler(), client As New HttpClient(handler), resolver As New CurseForgeFileResolver(New SourcePolicy(), client, "")
            Dim rejected = False
            Try
                Await resolver.ResolveAsync(package, {999L}, CancellationToken.None)
            Catch ex As ArgumentException
                rejected = True
            End Try
            Check(rejected AndAlso handler.Requests.Count = 0, "CF rejects a foreign optional ID before any network request", passed)
        End Using
        For Each classId In {12L, 6552L}
            Using handler As New Handler With {.Suffix = ".zip", .ProjectClass = classId}, client As New HttpClient(handler), resolver As New CurseForgeFileResolver(New SourcePolicy(), client, "")
                Dim files = Await resolver.ResolveAsync(package, {}, CancellationToken.None)
                Check(files(0).Download.RelativePath.StartsWith(If(classId = 12, "resourcepacks/", "shaderpacks/"), StringComparison.Ordinal), "CF ZIP resource uses its verified project class: " & classId, passed)
            End Using
        Next
        Using handler As New Handler With {.Mutate = Sub(value) value("downloadUrl") = "https://edge.forgecdn.net/files/1/1/fixture-1001.jar"}, client As New HttpClient(handler), resolver As New CurseForgeFileResolver(New SourcePolicy(), client, "")
            Dim files = Await resolver.ResolveAsync(package, {}, CancellationToken.None)
            Check(files(0).Download.Url = "https://edge.forgecdn.net/files/1/1/fixture-1001.jar", "CF API-provided checked CDN links remain independent of the website download endpoint", passed)
        End Using
        Using handler As New Handler With {.MirrorSucceeds = True}, client As New HttpClient(handler), resolver As New CurseForgeFileResolver(New SourcePolicy With {.VersionPreference = SourcePreference.MirrorFirst}, client, "")
            Dim files = Await resolver.ResolveAsync(package, {}, CancellationToken.None)
            Check(files(0).InformationSource = "mod.mcimirror.top" AndAlso handler.Requests.Count = 1, "CF mirror-first queries can accept an identity-checked API response without public fallback", passed)
        End Using
        Using handler As New Handler(), client As New HttpClient(handler), resolver As New CurseForgeFileResolver(New SourcePolicy(), client, "fixture-key")
            Dim leaked = False
            handler.Seen = Sub(request)
                               If request.Headers.Contains("x-api-key") AndAlso request.RequestUri.Host <> "api.curseforge.com" Then leaked = True
                           End Sub
            Await resolver.ResolveAsync(package, {}, CancellationToken.None)
            Check(Not leaked AndAlso handler.Requests.First().StartsWith("https://api.curseforge.com/", StringComparison.Ordinal), "CF own API key only targets the official API and is absent from public/CDN requests", passed)
        End Using
        Using cancel As New CancellationTokenSource(), handler As New Handler(), client As New HttpClient(handler), resolver As New CurseForgeFileResolver(New SourcePolicy(), client, "")
            handler.Seen = Sub(request) cancel.Cancel()
            Dim cancelled = False
            Try
                Await resolver.ResolveAsync(package, {1002L}, cancel.Token)
            Catch ex As OperationCanceledException
                cancelled = True
            End Try
            Check(cancelled, "CF query cancellation drains the bounded request group", passed)
        End Using
    End Function
    Private Sub Configure(fixture As InstallationChecks.InstallFixture, handler As Handler)
        fixture.Fabric()
        fixture.Server.Files("/v2/versions/loader/1.20.1/0.16.9/profile/json") = fixture.Server.Files("/v2/versions/loader/1.20.1/fixture/profile/json")
        fixture.Server.Files("/v2/versions/loader/1.20.1") = Encoding.UTF8.GetBytes(New JArray(New JObject From {{"loader", New JObject From {{"version", "0.16.9"}, {"stable", True}}}}).ToString())
        Dim versions As New JArray()
        For i = 0 To 199
            versions.Add(New JObject From {{"id", If(i = 0, "1.20.1", "fixture-" & i)}, {"type", "release"}, {"url", fixture.Entry.Url}, {"sha1", fixture.Entry.Sha1}, {"releaseTime", "2023-01-01T00:00:00Z"}})
        Next
        fixture.Server.Files("/mc/game/version_manifest_v2.json") = Encoding.UTF8.GetBytes(New JObject From {{"versions", versions}}.ToString())
        For Each pair In handler.Bytes
            fixture.Server.Files($"/api/v1/mods/{pair.Key - 1000}/files/{pair.Key}/download") = pair.Value
            fixture.Server.Files($"/files/{pair.Key \ 1000}/{pair.Key Mod 1000}/fixture-{pair.Key}.jar") = pair.Value
        Next
    End Sub
    Private Async Function InstallsAsync(root As String, passed As List(Of String)) As Task
        For Each mode In {"sha1", "md5", "etag", "etag1"}
            Using fixture As New InstallationChecks.InstallFixture(root, "cf-" & mode), handler As New Handler With {.Mode = mode}, client As New HttpClient(handler), resolver As New CurseForgeFileResolver(New SourcePolicy(), client, "")
                Configure(fixture, handler)
                Dim package = CurseForgePackage.Read(Pack(root, "install-" & mode)), policy As New Policy(fixture.Server.Url), installer As New CurseForgeInstaller(policy, resolver:=resolver)
                Dim sharedPath = Path.Combine(fixture.Root, "mods", "user.jar"), globalIni = Path.Combine(fixture.Root, "PCL", "Setup.ini")
                Directory.CreateDirectory(Path.GetDirectoryName(sharedPath)) : IO.File.WriteAllText(sharedPath, "existing shared mod")
                Directory.CreateDirectory(Path.GetDirectoryName(globalIni)) : IO.File.WriteAllText(globalIni, "LaunchArgumentIndieV2:0")
                Dim cancelled = False, heldArchive = False
                handler.Seen = Sub(request)
                                   Try
                                       Using write As New FileStream(package.SourcePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)
                                       End Using
                                   Catch ex As IOException
                                       heldArchive = True
                                   End Try
                               End Sub
                Using cancel As New CancellationTokenSource()
                    Try
                        Await installer.InstallAsync(package, fixture.Root, "pack", {}, {1002L}, New Progress(Sub(value)
                            If value.Stage = "完成安装" Then cancel.Cancel()
                        End Sub), cancel.Token, connections:=4)
                    Catch ex As OperationCanceledException
                        cancelled = True
                    End Try
                End Using
                Check(cancelled AndAlso Not Directory.Exists(Path.Combine(fixture.Root, "versions", "pack")), "CF cancellation before publication leaves no instance: " & mode, passed)
                Check(IO.File.ReadAllText(sharedPath) = "existing shared mod" AndAlso IO.File.ReadAllText(globalIni) = "LaunchArgumentIndieV2:0", "CF cancellation preserves shared Mods and PCL global preferences: " & mode, passed)
                Dim result = Await installer.InstallAsync(package, fixture.Root, "pack", {}, {1002L}, Nothing, CancellationToken.None, connections:=4)
                Check(result.NetworkBytes = 0, "CF retry reuses verified clients, libraries and Mods without payload traffic: " & mode, passed)
                Check(PclFolders.ReadIniValue(Path.Combine(result.InstancePath, "PCL", "Setup.ini"), "VersionArgumentIndieV2") = "True" AndAlso PclFolders.ReadIniValue(Path.Combine(result.InstancePath, "PCL", "Setup.ini"), "UserSetting") = "preserved", "CF forces isolation while preserving unrelated packaged INI preferences: " & mode, passed)
                Check(IO.File.ReadAllText(Path.Combine(result.InstancePath, "config", "fixture.txt")) = "original configuration" AndAlso handler.Bytes(1001).SequenceEqual(IO.File.ReadAllBytes(Path.Combine(result.InstancePath, "mods", "fixture-1001.jar"))) AndAlso IO.File.Exists(Path.Combine(result.InstancePath, "mods", "fixture-1002.jar")), "CF atomically publishes exact Mods, chosen optional content and overrides: " & mode, passed)
                Dim ledger = JObject.Parse(IO.File.ReadAllText(Path.Combine(result.InstancePath, "PCL", "CurseForgeInstall.json")))
                Check(CStr(ledger("archiveSha256")) = package.Sha256 AndAlso DirectCast(ledger("files"), JArray).Count = 2 AndAlso CStr(JObject.Parse(IO.File.ReadAllText(Path.Combine(result.InstancePath, "pack.json")))("mainClass")) = "fixture.FabricLoader", "CF publishes package provenance with the requested loader profile: " & mode, passed)
                Dim rejected = False, count = handler.Requests.Count
                Try
                    Await installer.InstallAsync(package, fixture.Root, "pack", {}, {}, Nothing, CancellationToken.None)
                Catch ex As IOException
                    rejected = True
                End Try
                Check(rejected AndAlso handler.Requests.Count = count, "CF refuses an existing instance before querying or changing it: " & mode, passed)
                Check(heldArchive, "CF holds its original ZIP against writes during asynchronous queries: " & mode, passed)
                Using released As New FileStream(package.SourcePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)
                    Check(released.CanWrite, "CF success/cancellation releases the original archive for later replacement: " & mode, passed)
                End Using
            End Using
        Next
        For Each issue In {"missing", "changed-archive", "override-file", "ancestor", "late-instance", "late-mod", "late-config", "missing-loader", "corrupt-download"}
            Using fixture As New InstallationChecks.InstallFixture(root, "cf-error-" & issue), handler As New Handler(), client As New HttpClient(handler), resolver As New CurseForgeFileResolver(New SourcePolicy(), client, "")
                Configure(fixture, handler)
                Dim entries As Dictionary(Of String, String) = Nothing
                If issue = "override-file" Then entries = New Dictionary(Of String, String) From {{"overrides/pack.json", "external metadata"}}
                If issue = "ancestor" Then entries = New Dictionary(Of String, String) From {{"overrides/mods", "file"}}
                Dim source = Pack(root, "error-" & issue, Manifest(If(issue = "missing-loader", "fabric-0.99.0", "fabric-0.16.9")), entries:=entries), package = CurseForgePackage.Read(source)
                If issue = "changed-archive" Then IO.File.AppendAllText(source, "changed")
                If issue = "missing" Then handler.Mode = "missing"
                If issue = "corrupt-download" Then fixture.Server.Files("/api/v1/mods/1/files/1001/download") = Encoding.UTF8.GetBytes("corrupt required mod")
                Dim installer As New CurseForgeInstaller(New Policy(fixture.Server.Url), resolver:=resolver), rejected = False
                Dim progress As New Progress(Sub(value)
                    If value.Stage <> "完成安装" Then Return
                    If issue = "late-instance" Then
                        Directory.CreateDirectory(Path.Combine(fixture.Root, "versions", "pack"))
                        IO.File.WriteAllText(Path.Combine(fixture.Root, "versions", "pack", "external.txt"), "external")
                    ElseIf issue = "late-mod" OrElse issue = "late-config" Then
                        Dim stage = Directory.EnumerateDirectories(Path.Combine(fixture.Root, ".vanilla-downloader", "staging")).Single()
                        IO.File.WriteAllText(Path.Combine(stage, "payload", If(issue = "late-mod", "mods/fixture-1001.jar", "config/fixture.txt")), "mutated")
                    End If
                End Sub)
                Try
                    Await installer.InstallAsync(package, fixture.Root, "pack", {}, {}, progress, CancellationToken.None, connections:=4)
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is ArgumentException
                    rejected = True
                End Try
                Check(rejected, "CF fails rather than publishing an incomplete or conflicting instance: " & issue, passed)
                If issue = "late-instance" Then
                    Check(IO.File.ReadAllText(Path.Combine(fixture.Root, "versions", "pack", "external.txt")) = "external", "CF failed publication preserves a concurrently created external instance", passed)
                Else
                    Check(Not Directory.Exists(Path.Combine(fixture.Root, "versions", "pack")), "CF failed import leaves no published instance: " & issue, passed)
                End If
                Dim staging = Path.Combine(fixture.Root, ".vanilla-downloader", "staging")
                Check(Not Directory.Exists(staging) OrElse (Not Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Any() AndAlso Not Directory.EnumerateDirectories(staging, "payload", SearchOption.AllDirectories).Any()), "CF failed import releases owned payloads and leaves no staging files: " & issue, passed)
            End Using
        Next
    End Function
End Module
