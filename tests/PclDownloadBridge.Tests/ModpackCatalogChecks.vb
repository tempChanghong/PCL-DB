Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module ModpackCatalogChecks
    Private Sub Check(value As Boolean, label As String, passed As List(Of String))
        If Not value Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub
    Private Class Handler
        Inherits HttpMessageHandler
        Friend ReadOnly Addresses As New List(Of String)
        Friend Property Route As Func(Of Uri, JToken)
        Friend Property Delay As Integer
        Protected Overrides Async Function SendAsync(request As HttpRequestMessage, token As CancellationToken) As Task(Of HttpResponseMessage)
            If Delay > 0 Then Await Task.Delay(Delay, token)
            SyncLock Addresses : Addresses.Add(request.RequestUri.AbsoluteUri) : End SyncLock
            Dim data = Route(request.RequestUri)
            Return New HttpResponseMessage(If(data Is Nothing, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK)) With {.Content = New StringContent(If(data Is Nothing, "{}", data.ToString()), Encoding.UTF8, "application/json")}
        End Function
    End Class
    Friend Function MrProject(id As String) As JObject
        Return New JObject From {{"project_id", id}, {"id", id}, {"project_type", "modpack"}, {"title", "Same pack " & id}, {"slug", "fixture-" & id}, {"description", "fixture pack"}, {"icon_url", ""}, {"downloads", 1000}, {"date_modified", "2026-10-01T00:00:00Z"}, {"updated", "2026-10-01T00:00:00Z"}, {"versions", New JArray("1.20.1")}, {"game_versions", New JArray("1.20.1")}}
    End Function
    Friend Function CfProject(id As Integer) As JObject
        Return New JObject From {{"id", id}, {"classId", 4471}, {"name", "Same pack " & id}, {"slug", "fixture-" & id}, {"summary", "fixture pack"}, {"downloadCount", 1000}, {"dateModified", "2026-10-01T00:00:00Z"}}
    End Function
    Friend Function MrRelease(Optional project As String = "pack1") As JObject
        Return New JObject From {{"id", "version1"}, {"project_id", project}, {"name", "Pack 2.3"}, {"version_number", "2.3"}, {"status", "listed"}, {"version_type", "release"}, {"downloads", 20}, {"date_published", "2026-10-01T00:00:00Z"}, {"game_versions", New JArray("1.20.1")}, {"loaders", New JArray("fabric")},
            {"files", New JArray(New JObject From {{"filename", "server.jar"}, {"url", "https://cdn.modrinth.com/server.jar"}, {"primary", False}, {"size", 1}, {"hashes", New JObject From {{"sha1", New String("a"c, 40)}}}}, New JObject From {{"filename", "pack.mrpack"}, {"url", "https://cdn.modrinth.com/pack.mrpack"}, {"primary", True}, {"size", 100}, {"hashes", New JObject From {{"sha1", New String("a"c, 40)}, {"sha512", New String("b"c, 128)}}}})}}
    End Function
    Friend Async Function RunAsync(root As String) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        Dim query As New ModpackQuery With {.Text = "name & 中文", .Minecraft = "1.20.1", .Category = "4475/adventure"}
        Dim address = ModpackCatalogClient.SearchUrl(ModpackPlatform.Modrinth, query, 40)
        Dim facetsText = Uri.UnescapeDataString(address.Substring(address.IndexOf("&facets=", StringComparison.Ordinal) + 8))
        Dim facets = JArray.Parse(facetsText)
        Check(TypeOf facets(0) Is JArray AndAlso CStr(facets(0)(0)) = "project_type:modpack" AndAlso address.Contains("name%20%26%20"), "catalogue escapes queries and preserves nested MR modpack facets", passed)
        Check(ModpackCatalogClient.SearchUrl(ModpackPlatform.CurseForge, query, 40).Contains("classId=4471") AndAlso ModpackCatalogClient.SearchUrl(ModpackPlatform.CurseForge, query, 40).Contains("index=40"), "CF pack search uses the pack class and its own raw offset", passed)
        Dim mr As New ModpackProject With {.Platform = ModpackPlatform.Modrinth, .Id = "pack1"}
        Dim json = MrRelease(), release = ModpackCatalogClient.ParseRelease(mr, json)
        Check(release.FileName = "pack.mrpack" AndAlso release.Sha512 IsNot Nothing, "MR chooses the primary installable archive instead of files(0)", passed)
        CompatibilityChecks(release, passed)
        Dim longName = DirectCast(json.DeepClone(), JObject)
        longName("files")(1)("filename") = New String("x"c, 180) & ".mrpack"
        Check(ModpackCatalogClient.ParseRelease(mr, longName).FileName.Length > 100, "remote archive filenames do not inherit the shorter instance-name limit", passed)
        Dim extra = DirectCast(json("files")(1).DeepClone(), JObject) : extra("primary") = False
        DirectCast(json("files"), JArray).Add(extra) : json("files")(1)("primary") = False
        Check(ModpackCatalogClient.ParseRelease(mr, json) Is Nothing, "ambiguous non-primary MR archives are not silently selected", passed)
        json = MrRelease() : json("project_id") = "other"
        Dim rejected = False
        Try
            ModpackCatalogClient.ParseRelease(mr, json)
        Catch ex As InvalidDataException
            rejected = True
        End Try
        Check(rejected, "version responses cannot change the selected project identity", passed)
        For Each url In {"https://example.com/archive.mrpack", "http://cdn.modrinth.com/pack.mrpack", "https://cdn.modrinth.com@evil.example/a", "https://edge.forgecdn.net/files/a.zip"}
            rejected = False
            Try
                ModpackArchivePolicy.Validate(url, ModpackPlatform.Modrinth, False)
            Catch ex As InvalidDataException
                rejected = True
            End Try
            Check(rejected, "archive policy rejects an unbound MR URL: " & url, passed)
        Next
        Dim cf As New ModpackProject With {.Platform = ModpackPlatform.CurseForge, .Id = "12"}
        Dim cfJson As New JObject From {{"id", 1234567}, {"modId", 12}, {"displayName", "Client pack"}, {"fileName", "client.zip"}, {"downloadUrl", "https://edge.forgecdn.net/files/1234/567/client.zip"}, {"fileLength", 100}, {"isServerPack", False}, {"isAvailable", True}, {"releaseType", 1}, {"fileDate", "2026-10-01T00:00:00Z"}, {"gameVersions", New JArray("1.20.1", "Forge")}, {"hashes", New JArray(New JObject From {{"algo", 1}, {"value", New String("a"c, 40)}})}}
        Check(ModpackCatalogClient.ParseRelease(cf, cfJson).GameVersions.SequenceEqual({"1.20.1"}), "CF client archive binds IDs, CDN path, checksum and exact game versions", passed)
        Dim quiltJson = DirectCast(cfJson.DeepClone(), JObject) : quiltJson("gameVersions") = New JArray("1.20.1", "Quilt")
        Check(ModpackCompatibility.GetInstallBlockReason(ModpackCatalogClient.ParseRelease(cf, quiltJson)).Contains("Quilt"), "CF Quilt labels retain the same automatic-install compatibility guard", passed)
        cfJson("isServerPack") = True
        Check(ModpackCatalogClient.ParseRelease(cf, cfJson) Is Nothing, "CF server packs cannot be installed as client packs", passed)
        cfJson("isServerPack") = False : cfJson("downloadUrl") = "https://edge.forgecdn.net/files/1234/568/client.zip"
        rejected = False
        Try
            ModpackCatalogClient.ParseRelease(cf, cfJson)
        Catch ex As InvalidDataException
            rejected = True
        End Try
        Check(rejected, "CF archive URL must match the selected file ID", passed)

        cfJson("downloadUrl") = "https://edge.forgecdn.net/files/1234/567/client.zip"
        Dim cfHandler As New Handler With {.Route = Function(uri)
                                                       If Not uri.AbsolutePath.EndsWith("/files", StringComparison.Ordinal) Then
                                                           Dim metadata = CfProject(12) : metadata("downloadCount") = New JValue(1000.0R)
                                                           Return New JObject From {{"data", metadata}}
                                                       End If
                                                       Dim files As New JArray()
                                                       If uri.Query.Contains("index=50") Then
                                                           files.Add(cfJson.DeepClone())
                                                       Else
                                                           For i = 1 To 50
                                                               Dim serverPack = DirectCast(cfJson.DeepClone(), JObject)
                                                               serverPack("id") = 2000 + i : serverPack("isServerPack") = True : files.Add(serverPack)
                                                           Next
                                                       End If
                                                       Return New JObject From {{"data", files}, {"pagination", New JObject From {{"totalCount", 51}}}}
                                                   End Function}
        Using client As New ModpackCatalogClient(cfHandler, "")
            Dim verified = Await client.GetProjectAsync(ModpackPlatform.CurseForge, "12", CancellationToken.None)
            Dim versions = Await client.GetVersionsAsync(verified, CancellationToken.None)
            Check(verified.Downloads = 1000 AndAlso versions.Count = 1 AndAlso versions(0).Id = "1234567" AndAlso cfHandler.Addresses.Any(Function(value) value.Contains("index=50")), "CF project and version adapters advance past a full server-pack page to the exact client archive", passed)
        End Using
        DirectCast(cfJson("hashes"), JArray).Add(New JObject From {{"algo", 1}, {"value", New String("b"c, 40)}})
        rejected = False
        Try
            ModpackCatalogClient.ParseRelease(cf, cfJson)
        Catch ex As InvalidDataException
            rejected = True
        End Try
        Check(rejected, "conflicting archive checksums cannot overwrite an earlier declaration", passed)

        Dim handler As New Handler With {.Route = Function(uri)
                                                    Dim offset = If(uri.Query.Contains("offset=40") OrElse uri.Query.Contains("index=40"), 40, 0)
                                                    Dim items As New JArray()
                                                    For i = offset To offset + 39
                                                        items.Add(If(uri.Host = "api.modrinth.com", MrProject("p" & i), CfProject(i + 1)))
                                                    Next
                                                    Return If(uri.Host = "api.modrinth.com", New JObject From {{"hits", items}, {"total_hits", 80}}, New JObject From {{"data", items}, {"pagination", New JObject From {{"totalCount", 80}}}})
                                                End Function}
        Using client As New ModpackCatalogClient(handler, "")
            Dim session As New ModpackSearchSession(New ModpackQuery())
            Dim first = Await session.GetPageAsync(0, client, CancellationToken.None)
            Check(first.Count = 40 AndAlso session.Count = 80, "both platforms fetch concurrently into a stable 40-item presentation page", passed)
            Dim previous = handler.Addresses.Count
            Await session.GetPageAsync(1, client, CancellationToken.None)
            Check(handler.Addresses.Count = previous, "back and next presentation pages reuse the same search snapshot", passed)
            Await session.GetPageAsync(2, client, CancellationToken.None)
            Check(session.Count = 160 AndAlso handler.Addresses.Any(Function(value) value.Contains("offset=40")) AndAlso handler.Addresses.Any(Function(value) value.Contains("index=40")), "each source advances by its own raw count rather than the merged page number", passed)
            Dim all = (Await session.GetPageAsync(0, client, CancellationToken.None)).Concat(Await session.GetPageAsync(1, client, CancellationToken.None)).ToList()
            Check(all.Select(Function(item) item.Key).Distinct().Count() = 80 AndAlso all.Any(Function(item) item.Platform = ModpackPlatform.CurseForge) AndAlso all.Any(Function(item) item.Platform = ModpackPlatform.Modrinth), "same-name cross-platform packs retain independent identities", passed)
        End Using
        handler = New Handler With {.Route = Function(uri) If(uri.Host = "api.modrinth.com", New JObject From {{"hits", New JArray(MrProject("one"))}, {"total_hits", 1}}, Nothing)}
        Using client As New ModpackCatalogClient(handler, "")
            Dim session As New ModpackSearchSession(New ModpackQuery())
            Check((Await session.GetPageAsync(0, client, CancellationToken.None)).Count = 1 AndAlso session.ErrorMessage.Contains("CurseForge") AndAlso Not session.HasMore, "CF failure preserves MR results and a finite partial-failure state", passed)
        End Using
        handler = New Handler With {.Route = Function(uri) New JObject From {{"hits", New JArray(MrProject("one"))}, {"total_hits", 999}}}
        Using client As New ModpackCatalogClient(handler, "")
            query = New ModpackQuery With {.Category = "/optimization", .Text = "original", .Minecraft = "1.20.1"}
            Dim session As New ModpackSearchSession(query) : query.Text = "changed"
            Dim editorCopy = session.Query : editorCopy.Minecraft = "1.18.2" : editorCopy.Text = "draft"
            Check(session.Query.Minecraft = "1.20.1" AndAlso session.Query.Text = "original", "detail query snapshots are independent of both caller and returned editor copies", passed)
            Await session.GetPageAsync(0, client, CancellationToken.None)
            Check(handler.Addresses.All(Function(value) New Uri(value).Host = "api.modrinth.com") AndAlso handler.Addresses(0).Contains("original"), "single-platform categories and query snapshots do not leak into the other platform", passed)
            Check(Not session.HasMore AndAlso handler.Addresses.Count = 2, "repeated result pages cannot loop indefinitely while filling a page", passed)
        End Using
        handler = New Handler With {.Delay = 2000, .Route = Function(uri) Nothing}
        Using client As New ModpackCatalogClient(handler, ""), cancel As New CancellationTokenSource(20)
            rejected = False
            Try
                Await New ModpackSearchSession(New ModpackQuery()).GetPageAsync(0, client, cancel.Token)
            Catch ex As OperationCanceledException
                rejected = True
            End Try
            Check(rejected, "cancelled searches never publish a partially completed batch", passed)
        End Using
        Await ArchiveChecksAsync(root, passed)
        Await CurseForgeArchiveCheckAsync(root, passed)
        Return passed
    End Function
    Private Sub CompatibilityChecks(original As ModpackRelease, passed As List(Of String))
        Dim aliases As New Dictionary(Of String, ComponentKind?) From {{"Forge", ComponentKind.Forge}, {"NeoForge", ComponentKind.NeoForge}, {"neo-forge", ComponentKind.NeoForge}, {" FABRIC ", ComponentKind.Fabric}, {"fabric-loader", ComponentKind.Fabric}, {"minecraft", Nothing}, {"vanilla", Nothing}}
        For Each pair In aliases
            Dim release = original.Copy() : release.Loaders = {pair.Key}
            ModpackCompatibility.ValidateManifest(release, "1.20.1", pair.Value)
            Check(ModpackCompatibility.GetInstallBlockReason(release) Is Nothing, "supported catalogue loader aliases bind to the actual manifest: " & pair.Key, passed)
        Next
        For Each value In {"quilt", "Quilt-Loader", "liteloader", "rift"}
            Dim release = original.Copy() : release.Loaders = {value}
            Check(ModpackCompatibility.GetInstallBlockReason(release) IsNot Nothing, "known unsupported pack loaders are identified before acquisition: " & value, passed)
        Next
        For Each labels In {New String() {}, New String() {"future-loader"}, New String() {"quilt", "future-loader"}}
            Dim release = original.Copy() : release.Loaders = labels
            ModpackCompatibility.ValidateManifest(release, "1.20.1", ComponentKind.Fabric)
            Check(ModpackCompatibility.GetInstallBlockReason(release) Is Nothing, "missing or unfamiliar loader labels defer to the validated archive: " & String.Join(",", labels), passed)
        Next
        Dim mixed = original.Copy() : mixed.Loaders = {"fabric", "quilt"}
        ModpackCompatibility.ValidateManifest(mixed, "1.20.1", ComponentKind.Fabric)
        Check(ModpackCompatibility.GetInstallBlockReason(mixed) Is Nothing, "multiple loader tags are alternatives and do not reject a supported Fabric pack", passed)
        Dim rejected = False
        Try
            ModpackCompatibility.ValidateManifest(mixed, "1.20.1", ComponentKind.Forge)
        Catch ex As InvalidDataException
            rejected = True
        End Try
        Check(rejected, "a fully recognized catalogue label set cannot substitute a different loader", passed)
        mixed.Loaders = {"future-loader"} : rejected = False
        Try
            ModpackCompatibility.ValidateManifest(mixed, "1.20.2", ComponentKind.Fabric)
        Catch ex As InvalidDataException
            rejected = True
        End Try
        Check(rejected, "unfamiliar loader labels do not bypass exact Minecraft version validation", passed)
    End Sub
    Private Async Function CurseForgeArchiveCheckAsync(root As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(root, "catalogue-cf")
            Dim manifest As New JObject From {{"manifestType", "minecraftModpack"}, {"manifestVersion", 1}, {"name", "CF online"}, {"version", "1"}, {"author", "fixture"}, {"minecraft", New JObject From {{"version", "1.20.1"}, {"modLoaders", New JArray()}}}, {"files", New JArray()}, {"overrides", "overrides"}}
            Dim bytes As Byte()
            Using buffer As New MemoryStream()
                Using zip As New IO.Compression.ZipArchive(buffer, IO.Compression.ZipArchiveMode.Create, True)
                    Using writer As New StreamWriter(zip.CreateEntry("manifest.json").Open()) : writer.Write(manifest.ToString()) : End Using
                    Using writer As New StreamWriter(zip.CreateEntry("overrides/config/cf.txt").Open()) : writer.Write("CF online preserved") : End Using
                End Using
                bytes = buffer.ToArray()
            End Using
            fixture.Server.Files("/files/1234/567/client.zip") = bytes
            Dim versions As New JArray()
            For i = 0 To 199 : versions.Add(New JObject From {{"id", If(i = 0, "1.20.1", "fixture-" & i)}, {"type", "release"}, {"url", fixture.Entry.Url}, {"sha1", fixture.Entry.Sha1}, {"releaseTime", "2023-01-01T00:00:00Z"}}) : Next
            fixture.Server.Files("/mc/game/version_manifest_v2.json") = Encoding.UTF8.GetBytes(New JObject From {{"versions", versions}}.ToString())
            Dim release As New ModpackRelease With {.Platform = ModpackPlatform.CurseForge, .ProjectId = "12", .Id = "1234567", .Name = "CF online", .FileName = "client.zip", .Url = "https://edge.forgecdn.net/files/1234/567/client.zip", .Size = bytes.Length, .Sha1 = ModrinthChecks.Hash(bytes), .GameVersions = {"1.20.1"}}
            Dim mismatch = release.Copy() : mismatch.Loaders = {"Fabric"}
            Dim rejected = False
            Try
                Await ModpackArchiveInstaller.InstallAsync(mismatch, fixture.Root, "cf-mismatch", New ModrinthChecks.Policy(fixture.Server.Url), Nothing, {}, Nothing, CancellationToken.None)
            Catch ex As InvalidDataException
                rejected = True
            End Try
            Check(rejected AndAlso Not Directory.Exists(Path.Combine(fixture.Root, "versions", "cf-mismatch")) AndAlso fixture.Server.Requests.Keys.All(Function(path) path = "/files/1234/567/client.zip"), "CF loader mismatch stops before game transfers and atomic instance publication", passed)
            release.Loaders = {"minecraft"}
            Dim result = Await ModpackArchiveInstaller.InstallAsync(release, fixture.Root, "cf-online", New ModrinthChecks.Policy(fixture.Server.Url), Nothing, {}, Nothing, CancellationToken.None)
            Check(File.ReadAllText(Path.Combine(result.InstancePath, "config", "cf.txt")) = "CF online preserved" AndAlso File.Exists(Path.Combine(result.InstancePath, "cf-online.json")), "CF ZIP acquisition enters the existing isolated importer and publishes atomically", passed)
        End Using
    End Function
    Private Async Function ArchiveChecksAsync(root As String, passed As List(Of String)) As Task
        Dim manifest As New JObject From {{"formatVersion", 1}, {"game", "minecraft"}, {"name", "Online fixture"}, {"versionId", "display-version"}, {"files", New JArray()}, {"dependencies", New JObject From {{"minecraft", "1.20.1"}}}}
        Dim source = ModrinthChecks.Pack(root, "catalogue", manifest, New Dictionary(Of String, String) From {{"overrides/config/online.txt", "preserved"}})
        Dim bytes = File.ReadAllBytes(source)
        Using fixture As New InstallationChecks.InstallFixture(root, "catalogue")
            fixture.Server.Files("/archive.mrpack") = bytes
            Dim versions As New JArray()
            For i = 0 To 199 : versions.Add(New JObject From {{"id", If(i = 0, "1.20.1", "fixture-" & i)}, {"type", "release"}, {"url", fixture.Entry.Url}, {"sha1", fixture.Entry.Sha1}, {"releaseTime", "2023-01-01T00:00:00Z"}}) : Next
            fixture.Server.Files("/mc/game/version_manifest_v2.json") = Encoding.UTF8.GetBytes(New JObject From {{"versions", versions}}.ToString())
            Dim release As New ModpackRelease With {.Platform = ModpackPlatform.Modrinth, .ProjectId = "pack1", .Id = "remoteVersion", .Name = "Online fixture", .FileName = "archive.mrpack", .Url = "https://cdn.modrinth.com/archive.mrpack", .Size = bytes.Length, .Sha1 = ModrinthChecks.Hash(bytes), .Sha512 = ModrinthChecks.Hash(bytes, True), .GameVersions = {"1.20.1"}}
            Dim policy As New ModrinthChecks.Policy(fixture.Server.Url), archive = Await ModpackArchiveInstaller.AcquireAsync(release, fixture.Root, policy, Nothing, CancellationToken.None)
            Check(Not Directory.Exists(Path.Combine(fixture.Root, "versions")) AndAlso File.ReadAllBytes(archive).SequenceEqual(bytes), "archive acquisition verifies both hashes without creating a final instance", passed)
            Dim mismatched = release.Copy() : mismatched.Loaders = {"fabric"}
            Dim loaderRejected = False, requestsBeforeLoaderCheck = fixture.Server.Requests.Values.Sum()
            Try
                Await ModpackArchiveInstaller.InstallAsync(mismatched, fixture.Root, "loader-mismatch", policy, Nothing, {}, Nothing, CancellationToken.None)
            Catch ex As InvalidDataException
                loaderRejected = True
            End Try
            Check(loaderRejected AndAlso Not Directory.Exists(Path.Combine(fixture.Root, "versions", "loader-mismatch")) AndAlso fixture.Server.Requests.Values.Sum() = requestsBeforeLoaderCheck, "a loader-labelled release cannot silently publish a vanilla archive or start game downloads", passed)
            Dim unsupported = release.Copy() : unsupported.Loaders = {"quilt"}
            Dim blockedRoot = Path.Combine(fixture.Root, "unsupported-destination"), blocked = False
            Try
                Await ModpackArchiveInstaller.InstallAsync(unsupported, blockedRoot, "quilt", policy, Nothing, {}, Nothing, CancellationToken.None)
            Catch ex As NotSupportedException
                blocked = True
            End Try
            Check(blocked AndAlso Not Directory.Exists(blockedRoot) AndAlso fixture.Server.Requests.Values.Sum() = requestsBeforeLoaderCheck, "known unsupported installation makes no archive request, cache directory or instance", passed)
            Using cancelled As New CancellationTokenSource()
                cancelled.Cancel() : blocked = False
                Try
                    Await ModpackArchiveInstaller.InstallAsync(unsupported, blockedRoot, "quilt", policy, Nothing, {}, Nothing, cancelled.Token)
                Catch ex As OperationCanceledException
                    blocked = True
                End Try
                Check(blocked AndAlso Not Directory.Exists(blockedRoot), "explicit cancellation keeps cancellation semantics before compatibility preflight", passed)
            End Using
            Dim unsupportedManifest = DirectCast(manifest.DeepClone(), JObject)
            unsupportedManifest("dependencies")("quilt-loader") = "0.27.0"
            Dim unsupportedBytes = File.ReadAllBytes(ModrinthChecks.Pack(root, "catalogue-unsupported", unsupportedManifest))
            fixture.Server.Files("/unsupported.mrpack") = unsupportedBytes
            unsupported = release.Copy() : unsupported.Id = "unsupported-manifest" : unsupported.Loaders = {"fabric"} : unsupported.Url = "https://cdn.modrinth.com/unsupported.mrpack"
            unsupported.Size = unsupportedBytes.Length : unsupported.Sha1 = ModrinthChecks.Hash(unsupportedBytes) : unsupported.Sha512 = ModrinthChecks.Hash(unsupportedBytes, True)
            blocked = False
            Try
                Await ModpackArchiveInstaller.InstallAsync(unsupported, fixture.Root, "unsupported-manifest", policy, Nothing, {}, Nothing, CancellationToken.None)
            Catch ex As NotSupportedException
                blocked = True
            End Try
            Check(blocked AndAlso Not Directory.Exists(Path.Combine(fixture.Root, "versions", "unsupported-manifest")) AndAlso fixture.Server.Requests.Keys.All(Function(path) path.EndsWith(".mrpack", StringComparison.Ordinal)), "a supported catalogue tag cannot skip an unsupported required loader inside the archive", passed)
            Dim before = fixture.Server.Requests("/archive.mrpack")
            Await Task.WhenAll(ModpackArchiveInstaller.AcquireAsync(release, fixture.Root, policy, Nothing, CancellationToken.None), ModpackArchiveInstaller.AcquireAsync(release, fixture.Root, policy, Nothing, CancellationToken.None))
            Check(fixture.Server.Requests("/archive.mrpack") = before, "simultaneous retries reuse one verified archive without new requests", passed)
            release.Size += 1
            Dim rejected = False
            Try
                Await ModpackArchiveInstaller.AcquireAsync(release, fixture.Root, policy, Nothing, CancellationToken.None)
            Catch ex As Exception When Not TypeOf ex Is OperationCanceledException
                rejected = True
            End Try
            Check(rejected, "outer MRPACK size is strict even though inner fileSize is advisory", passed)
            release.Size -= 1 : release.Sha512 = New String("0"c, 128)
            rejected = False
            Try
                Await ModpackArchiveInstaller.AcquireAsync(release, fixture.Root, policy, Nothing, CancellationToken.None)
            Catch ex As Exception When Not TypeOf ex Is OperationCanceledException
                rejected = True
            End Try
            Check(rejected, "a valid SHA-1 cannot hide an invalid supplied SHA-512", passed)
            release.Sha512 = ModrinthChecks.Hash(bytes, True)
            Using cancel As New CancellationTokenSource()
                rejected = False
                Try
                    Await ModpackArchiveInstaller.InstallAsync(release, fixture.Root, "cancelled", policy, Nothing, {}, New ModrinthChecks.Progress(Sub(value)
                                                                                                                                                         If value.Phase = "archive" AndAlso value.StageFraction = 1 Then cancel.Cancel()
                                                                                                                                                     End Sub), cancel.Token)
                Catch ex As OperationCanceledException
                    rejected = True
                End Try
                Check(rejected AndAlso Not Directory.Exists(Path.Combine(fixture.Root, "versions", "cancelled")), "cancelling after archive verification prevents instance publication", passed)
            End Using
            Dim archiveFraction As Double
            Dim result = Await ModpackArchiveInstaller.InstallAsync(release, fixture.Root, "online", policy, Nothing, {}, New ModrinthChecks.Progress(Sub(value)
                                                                                                                                                      If value.Phase = "archive" Then archiveFraction = Math.Max(archiveFraction, value.StageFraction)
                                                                                                                                                  End Sub), CancellationToken.None)
            Check(archiveFraction = 1 AndAlso result.Reused > 0, "archive progress and reused-file statistics survive the handoff into installation", passed)
            Check(File.ReadAllText(Path.Combine(result.InstancePath, "config", "online.txt")) = "preserved" AndAlso File.Exists(Path.Combine(result.InstancePath, "online.json")), "archive-to-parser-to-existing-installer completes an isolated atomic installation", passed)
            Check(PclFolders.ReadIniValue(Path.Combine(result.InstancePath, "PCL", "Setup.ini"), "VersionArgumentIndieV2") = "True", "online installation keeps the same forced isolation as local import", passed)
            rejected = False
            Try
                Await ModpackArchiveInstaller.InstallAsync(release, fixture.Root, "online", policy, Nothing, {}, Nothing, CancellationToken.None)
            Catch ex As IOException
                rejected = True
            End Try
            Check(rejected AndAlso File.ReadAllText(Path.Combine(result.InstancePath, "config", "online.txt")) = "preserved", "same-name online installs preserve the existing instance", passed)

            fixture.Fabric()
            fixture.Server.Files("/v2/versions/loader/1.20.1/0.16.9/profile/json") = fixture.Server.Files("/v2/versions/loader/1.20.1/fixture/profile/json")
            fixture.Server.Files("/v2/versions/loader/1.20.1") = Encoding.UTF8.GetBytes(New JArray(New JObject From {{"loader", New JObject From {{"version", "0.16.9"}, {"stable", True}}}}).ToString())
            Dim fabricManifest = DirectCast(manifest.DeepClone(), JObject) : fabricManifest("dependencies")("fabric-loader") = "0.16.9"
            Dim fabricBytes = File.ReadAllBytes(ModrinthChecks.Pack(root, "catalogue-fabric", fabricManifest))
            fixture.Server.Files("/fabric.mrpack") = fabricBytes
            Dim fabric = release.Copy() : fabric.Id = "mixed-fabric" : fabric.Loaders = {"fabric", "quilt"} : fabric.Url = "https://cdn.modrinth.com/fabric.mrpack"
            fabric.Size = fabricBytes.Length : fabric.Sha1 = ModrinthChecks.Hash(fabricBytes) : fabric.Sha512 = ModrinthChecks.Hash(fabricBytes, True)
            Dim fabricResult = Await ModpackArchiveInstaller.InstallAsync(fabric, fixture.Root, "mixed-fabric", policy, Nothing, {}, New ModrinthChecks.Progress(Sub(value)
                                                                                                                                                                If value.Phase = "archive" Then fabric.Loaders(0) = "forge" : fabric.GameVersions(0) = "1.19.4"
                                                                                                                                                            End Sub), CancellationToken.None)
            Check(File.ReadAllText(Path.Combine(fabricResult.InstancePath, "mixed-fabric.json")).Contains("fixture.FabricLoader") AndAlso fabric.Loaders(0) = "forge", "mixed Fabric/Quilt labels install the exact Fabric manifest while caller mutations cannot change its snapshot", passed)
        End Using
    End Function

    Friend Async Function LiveAsync(root As String, Optional install As Boolean = False) As Task
        Directory.CreateDirectory(root)
        Using client As New ModpackCatalogClient()
            Dim query As New ModpackQuery With {.Text = "Zombie Invade 100 Days", .Platforms = ModpackPlatform.Modrinth}
            Dim projects = Await New ModpackSearchSession(query).GetPageAsync(0, client, CancellationToken.None)
            Dim project = projects.FirstOrDefault(Function(item) item.Name.IndexOf("Zombie Invade", StringComparison.OrdinalIgnoreCase) >= 0)
            If project Is Nothing Then Throw New InvalidDataException("Live search did not return the requested pack.")
            Dim verified = Await client.GetProjectAsync(project.Platform, project.Id, CancellationToken.None), versions = Await client.GetVersionsAsync(verified, CancellationToken.None)
            Dim release = versions.FirstOrDefault(Function(item) item.Version = "2.3")
            If release Is Nothing Then Throw New InvalidDataException("Live catalogue lacks version 2.3.")
            Dim gameRoot = Path.Combine(root, ".minecraft"), archive = Await ModpackArchiveInstaller.AcquireAsync(release, gameRoot, New SourcePolicy(), Nothing, CancellationToken.None)
            Dim parsed = DirectCast(ModpackArchiveInstaller.ReadPackage(archive, CancellationToken.None), ModrinthPackage)
            Dim record As New JObject From {{"project", project.Id}, {"version", release.Id}, {"displayVersion", release.Version}, {"archive", archive}, {"sha256", parsed.Sha256}, {"minecraft", parsed.Minecraft}, {"loader", parsed.LoaderVersion}, {"files", parsed.References.Count}, {"archiveVerified", True}, {"installed", False}}
            File.WriteAllText(Path.Combine(root, "result.json"), record.ToString())
            Console.WriteLine(record.ToString())
            If install Then
                Dim reuse = {Path.GetFullPath("artifacts/modpack-search-live/.minecraft"), Path.GetFullPath("artifacts/modrinth-import/live/.minecraft"), Path.GetFullPath("artifacts/modrinth-import/live-final/.minecraft"), Path.GetFullPath("artifacts/modrinth-polish/live/.minecraft")}
                Dim result = Await ModpackArchiveInstaller.InstallAsync(release, gameRoot, "Zombie-Search-2.3", New SourcePolicy(), Nothing, reuse, Nothing, CancellationToken.None)
                record("installed") = True : record("instance") = result.InstancePath : record("fileCount") = result.FileCount : record("networkBytes") = result.NetworkBytes
                File.WriteAllText(Path.Combine(root, "result.json"), record.ToString()) : Console.WriteLine(record.ToString())
            End If
        End Using
    End Function
End Module
