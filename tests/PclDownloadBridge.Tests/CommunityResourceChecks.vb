Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Module CommunityResourceChecks
    Private Sub Check(ok As Boolean, label As String, passed As List(Of String))
        If Not ok Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub
    Friend Function ProjectData(kind As CommunityResourceKind, Optional id As String = "resource1") As JObject
        Dim data = ModpackCatalogChecks.MrProject(id)
        data("project_type") = If(kind = CommunityResourceKind.DataPack, "mod", CommunityResources.ProjectType(kind))
        data("all_project_types") = New JArray(CommunityResources.ProjectType(kind))
        Return data
    End Function
    Friend Function ReleaseData(kind As CommunityResourceKind, Optional project As String = "resource1", Optional id As String = "release1") As JObject
        Dim data = ModpackCatalogChecks.MrRelease(project)
        data("id") = id
        Dim name = "resource" & If(kind = CommunityResourceKind.Mod, ".jar", ".zip")
        data("loaders") = New JArray(If(kind = CommunityResourceKind.Mod, "fabric", If(kind = CommunityResourceKind.DataPack, "datapack", If(kind = CommunityResourceKind.Shader, "iris", "minecraft"))))
        data("files") = New JArray(New JObject From {{"filename", name}, {"url", "https://cdn.modrinth.com/data/" & project & "/" & name}, {"primary", True}, {"size", 100}, {"hashes", New JObject From {{"sha1", New String("a"c, 40)}}}})
        Return data
    End Function
    Private Class Handler
        Inherits HttpMessageHandler
        Friend Route As Func(Of Uri, JToken)
        Protected Overrides Function SendAsync(request As HttpRequestMessage, token As CancellationToken) As Task(Of HttpResponseMessage)
            token.ThrowIfCancellationRequested()
            Return Task.FromResult(New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New StringContent(Route(request.RequestUri).ToString(), Encoding.UTF8, "application/json")})
        End Function
    End Class
    Friend Async Function RunAsync(root As String) As Task(Of List(Of String))
        Dim passed As New List(Of String), folder = Path.Combine(root, "community-" & Guid.NewGuid().ToString("N"))
        Await ResourceCatalogueRepairChecks.VerifyAsync(passed)
        Await ResourceBrowserFollowupChecks.VerifyAsync(root, passed)
        CataloguePresentationChecks.Verify(root, passed)
        Await CommunitySettingsChecks.VerifyAsync(passed)
        Directory.CreateDirectory(folder)
        For Each kind In {CommunityResourceKind.Mod, CommunityResourceKind.DataPack, CommunityResourceKind.ResourcePack, CommunityResourceKind.Shader}
            Dim query As New ModpackQuery With {.Kind = kind, .Text = "a & 中", .Minecraft = "1.20.1"}
            Check(ModpackCatalogClient.SearchUrl(ModpackPlatform.CurseForge, query, 40).Contains("classId=" & CommunityResources.ClassId(kind).ToString()) AndAlso
                Uri.UnescapeDataString(ModpackCatalogClient.SearchUrl(ModpackPlatform.Modrinth, query, 40)).Contains("project_type:" & CommunityResources.ProjectType(kind)), "resource search retains pinned PCL class and facet: " & kind.ToString(), passed)
            Dim project As New ModpackProject With {.Kind = kind, .Platform = ModpackPlatform.Modrinth, .Id = "resource1"}
            Dim release = ModpackCatalogClient.ParseRelease(project, ReleaseData(kind))
            Check(release IsNot Nothing AndAlso release.Kind = kind AndAlso CommunityResources.AcceptsFile(kind, release.FileName), "resource versions retain their actual kind and primary file: " & kind.ToString(), passed)
            Dim cf As New JObject From {{"id", 1234567}, {"modId", 12}, {"displayName", "resource"}, {"fileName", release.FileName}, {"downloadUrl", "https://edge.forgecdn.net/files/1234/567/" & release.FileName}, {"fileLength", 100}, {"releaseType", 1}, {"fileDate", "2026-10-01T00:00:00Z"}, {"gameVersions", New JArray("1.20.1", "Fabric")}, {"hashes", New JArray(New JObject From {{"algo", 1}, {"value", New String("a"c, 40)}})}}
            Dim cfRelease = ModpackCatalogClient.ParseRelease(New ModpackProject With {.Platform = ModpackPlatform.CurseForge, .Kind = kind, .Id = "12"}, cf)
            Check(cfRelease IsNot Nothing AndAlso cfRelease.Kind = kind, "CF files retain resource kind without entering ZIP modpack installation: " & kind.ToString(), passed)
        Next
        Dim modProject As New ModpackProject With {.Kind = CommunityResourceKind.Mod, .Platform = ModpackPlatform.Modrinth, .Id = "resource1"}
        Dim dataProject As New ModpackProject With {.Kind = CommunityResourceKind.DataPack, .Platform = ModpackPlatform.Modrinth, .Id = "resource1"}
        Check(ModpackCatalogClient.ParseRelease(modProject, ReleaseData(CommunityResourceKind.DataPack)) Is Nothing AndAlso ModpackCatalogClient.ParseRelease(dataProject, ReleaseData(CommunityResourceKind.Mod)) Is Nothing AndAlso modProject.Key <> dataProject.Key, "mixed Mod and datapack project contexts never accept each other's files or cache identity", passed)
        Dim plugin = ReleaseData(CommunityResourceKind.Mod) : plugin("loaders") = New JArray("paper")
        Check(ModpackCatalogClient.ParseRelease(modProject, plugin) Is Nothing, "mixed projects exclude plugin-only releases", passed)
        Dim dual = ReleaseData(CommunityResourceKind.Mod)
        dual("loaders") = New JArray("fabric", "datapack")
        DirectCast(dual("files"), JArray).Add(DirectCast(ReleaseData(CommunityResourceKind.DataPack)("files"), JArray)(0).DeepClone())
        DirectCast(dual("files")(1), JObject)("primary") = False
        Check(ModpackCatalogClient.ParseRelease(modProject, dual).FileName.EndsWith(".jar") AndAlso ModpackCatalogClient.ParseRelease(dataProject, dual).FileName.EndsWith(".zip"), "a dual-format version selects the matching Mod or datapack file independently of the other primary format", passed)
        Dim deps = ReleaseData(CommunityResourceKind.Mod)
        deps("dependencies") = New JArray(New JObject From {{"project_id", "dep1"}, {"version_id", "pin1"}, {"dependency_type", "required"}}, New JObject From {{"project_id", "optional1"}, {"dependency_type", "optional"}}, New JObject From {{"version_id", "onlypin1"}, {"dependency_type", "required"}}, New JObject From {{"project_id", "embedded1"}, {"dependency_type", "embedded"}})
        DirectCast(deps("dependencies"), JArray).Add(New JObject From {{"project_id", "resource1"}, {"dependency_type", "required"}})
        DirectCast(deps("dependencies"), JArray).Add(New JObject From {{"version_id", "release1"}, {"dependency_type", "required"}})
        Dim parsed = ModpackCatalogClient.ParseRelease(modProject, deps), copy = parsed.Copy()
        copy.Dependencies(0).ProjectId = "changed"
        Check(parsed.Dependencies.Length = 2 AndAlso parsed.Dependencies(0).ProjectId = "dep1" AndAlso parsed.Dependencies(0).VersionId = "pin1" AndAlso parsed.Dependencies(1).ProjectId = "", "required dependencies preserve fixed and version-only identities while snapshots own their values", passed)
        Dim handler As New Handler With {.Route = Function(uri)
            If uri.AbsolutePath.StartsWith("/v2/version/") Then Return New JObject From {{"id", "onlypin1"}, {"project_id", "dep1"}}
            Return ProjectData(CommunityResourceKind.Mod, "dep1")
        End Function}
        Using client As New ModpackCatalogClient(handler)
            Dim linked = Await client.ResolveDependencyAsync(ModpackPlatform.Modrinth, parsed.Dependencies(1), CancellationToken.None)
            Check(linked.Id = "dep1" AndAlso linked.PreferredVersionId = "onlypin1" AndAlso linked.Kind = CommunityResourceKind.Mod AndAlso linked.Website.Contains("/mod/"), "version-only dependency resolves to its verified project and retains the pin", passed)
        End Using
        handler = New Handler With {.Route = Function(uri)
            If uri.AbsolutePath.StartsWith("/v2/version/") Then Return New JObject From {{"id", "onlypin1"}, {"project_id", "dep1"}, {"loaders", New JArray("datapack")}}
            Dim metadata = ProjectData(CommunityResourceKind.DataPack, "dep1")
            metadata("all_project_types") = New JArray("mod", "datapack")
            Return metadata
        End Function}
        Using client As New ModpackCatalogClient(handler)
            Dim linked = Await client.ResolveDependencyAsync(ModpackPlatform.Modrinth, parsed.Dependencies(1), CancellationToken.None)
            Check(linked.Kind = CommunityResourceKind.DataPack AndAlso linked.PreferredVersionId = "onlypin1", "fixed datapack dependencies retain their actual version type inside a mixed Mod project", passed)
        End Using
        Dim loaderUrl = ModpackCatalogClient.SearchUrl(ModpackPlatform.CurseForge, New ModpackQuery With {.Kind = CommunityResourceKind.Mod, .Loader = "neoforge"}, 0)
        Check(loaderUrl.Contains("modLoaderType=6"), "NeoForge filtering uses the platform loader mapping", passed)
        Dim reserved = False
        Try
            CommunityResources.ValidateFileName(CommunityResourceKind.Mod, "CON.jar")
        Catch ex As InvalidDataException
            reserved = True
        End Try
        Check(reserved, "remote or user-selected Windows device aliases cannot become resource cache or destination files", passed)
        Dim wrongInstaller = False
        Try
            Await ModpackArchiveInstaller.InstallAsync(New ModpackRelease With {.Kind = CommunityResourceKind.Shader}, Path.Combine(folder, "must-not-create"), "wrong", New SourcePolicy(), Nothing, {}, Nothing, CancellationToken.None)
        Catch ex As InvalidOperationException
            wrongInstaller = True
        End Try
        Check(wrongInstaller AndAlso Not Directory.Exists(Path.Combine(folder, "must-not-create")), "ordinary resources cannot enter the modpack installer or create an instance directory", passed)
        Using server As New TestServer()
            Dim bytes = Encoding.UTF8.GetBytes(New String("x"c, 131072)), digest As String
            Using sha = SHA1.Create() : digest = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() : End Using
            server.Files("/file.zip") = bytes
            Dim release As New ModpackRelease With {.Platform = ModpackPlatform.Modrinth, .Kind = CommunityResourceKind.ResourcePack, .ProjectId = "resource1", .Id = "release1", .FileName = "file.zip", .Url = server.Url & "/file.zip", .Size = bytes.Length, .Sha1 = digest}
            Dim cache = Path.Combine(folder, "cache"), destination = Path.Combine(folder, "saved.zip"), policy As New ModrinthChecks.Policy(server.Url)
            Await CommunityResourceDownload.SaveAsync(release, destination, cache, policy, Nothing, CancellationToken.None)
            Check(File.ReadAllBytes(destination).SequenceEqual(bytes), "real resource acquisition and native atomic save retain every selected byte", passed)
            Dim requests = server.Requests.Values.Sum(), conflict = False
            Try
                Await CommunityResourceDownload.SaveAsync(release, destination, cache, policy, Nothing, CancellationToken.None)
            Catch ex As IOException
                conflict = True
            End Try
            Check(conflict AndAlso server.Requests.Values.Sum() = requests AndAlso File.ReadAllBytes(destination).SequenceEqual(bytes), "existing destination fails before network work and is never replaced", passed)
            Dim source = Await ModpackArchiveInstaller.AcquireAsync(release, cache, policy, Nothing, CancellationToken.None)
            Dim corrupt = release.Copy() : corrupt.Sha1 = New String("b"c, 40)
            Dim rejected = False, corruptPath = Path.Combine(folder, "corrupt.zip")
            Try
                CommunityResourceDownload.Publish(corrupt, source, corruptPath, CancellationToken.None)
            Catch ex As InvalidDataException
                rejected = True
            End Try
            Check(rejected AndAlso Not File.Exists(corruptPath), "the exact source read lease is rehashed against the selected resource before any destination is published", passed)
            Dim cancelledPath = Path.Combine(folder, "cancelled.zip")
            Using cancellation As New CancellationTokenSource()
                Try
                    CommunityResourceDownload.Publish(release, source, cancelledPath, cancellation.Token, beforePublish:=Sub() cancellation.Cancel())
                Catch ex As OperationCanceledException
                End Try
                Check(Not File.Exists(cancelledPath) AndAlso Not Directory.EnumerateFiles(folder, ".pcldb-resource-*.tmp").Any(), "cancellation at publication removes only the owned stage and leaves no destination", passed)
            End Using
            Await CommunityResourceDownload.SaveAsync(release, cancelledPath, cache, policy, Nothing, CancellationToken.None)
            Check(File.ReadAllBytes(cancelledPath).SequenceEqual(bytes) AndAlso server.Requests.Values.Sum() = requests, "cancelled resource save retries from the verified cache without new transfers", passed)
            Dim race = Path.Combine(folder, "race.zip"), raceBytes = Encoding.UTF8.GetBytes("other owner")
            Try
                CommunityResourceDownload.Publish(release, source, race, CancellationToken.None, beforePublish:=Sub() File.WriteAllBytes(race, raceBytes))
            Catch ex As IOException
            End Try
            Check(File.ReadAllBytes(race).SequenceEqual(raceBytes) AndAlso Not Directory.EnumerateFiles(folder, ".pcldb-resource-*.tmp").Any(), "a destination created during download publication belongs to its other owner and survives cleanup", passed)
            Dim mods = Path.Combine(folder, "mods") : Directory.CreateDirectory(mods)
            File.WriteAllBytes(Path.Combine(mods, "same.jar.disabled"), raceBytes)
            release.Kind = CommunityResourceKind.Mod
            release.FileName = "file.jar"
            Dim aliasConflict = False
            Try
                CommunityResourceDownload.Publish(release, source, Path.Combine(mods, "same.jar"), CancellationToken.None)
            Catch ex As IOException
                aliasConflict = True
            End Try
            Check(aliasConflict AndAlso Not File.Exists(Path.Combine(mods, "same.jar")) AndAlso File.ReadAllBytes(Path.Combine(mods, "same.jar.disabled")).SequenceEqual(raceBytes), "Save As into shared Mods retains disabled aliases and uses the local Mods gate", passed)
            Dim marker = Path.Combine(folder, "options.txt") : File.WriteAllText(marker, "retain settings")
            CommunityResourceDownload.Publish(release, source, Path.Combine(mods, "new.jar"), CancellationToken.None)
            Check(File.ReadAllBytes(Path.Combine(mods, "new.jar")).SequenceEqual(bytes) AndAlso File.ReadAllText(marker) = "retain settings", "file-only Mod save preserves existing game settings without installing components", passed)
        End Using
        Return passed
    End Function
    Friend Async Function LiveAsync(output As String) As Task
        Directory.CreateDirectory(output)
        Dim runRoot = Path.Combine(output, "samples-" & Guid.NewGuid().ToString("N")), rows As New JArray(), succeeded = True
        Directory.CreateDirectory(runRoot)
        For Each kind In {CommunityResourceKind.Mod, CommunityResourceKind.DataPack, CommunityResourceKind.ResourcePack, CommunityResourceKind.Shader}
            For Each platform In {ModpackPlatform.Modrinth, ModpackPlatform.CurseForge}
                Dim row As New JObject From {{"Kind", kind.ToString()}, {"Platform", platform.ToString()}}
                Try
                    Using client As New ModpackCatalogClient()
                        Dim page = Await client.SearchAsync(platform, New ModpackQuery With {.Kind = kind, .Platforms = platform}, 0, CancellationToken.None)
                        If page.Projects.Count = 0 Then Throw New IOException("Live search returned no projects.")
                        Dim project As ModpackProject = Nothing, releases As List(Of ModpackRelease) = Nothing, release As ModpackRelease = Nothing, skipped As New JArray()
                        For Each candidate In page.Projects.Take(6)
                            project = Await client.GetProjectAsync(platform, candidate.Id, CancellationToken.None, kind)
                            releases = Await client.GetVersionsAsync(project, CancellationToken.None)
                            release = releases.FirstOrDefault(Function(item) item.Size <= 32L * 1024 * 1024)
                            If release IsNot Nothing Then Exit For
                            skipped.Add(New JObject From {{"ProjectId", project.Id}, {"Name", project.Name}, {"DownloadableVersions", releases.Count}})
                        Next
                        row("SkippedSampleProjects") = skipped
                        If release Is Nothing Then Throw New IOException("No sample below 32 MiB in the live results; this is a probe limit, not an application restriction.")
                        Dim destinationFolder = Path.Combine(runRoot, kind.ToString() & "-" & platform.ToString()) : Directory.CreateDirectory(destinationFolder)
                        Dim destination = Path.Combine(destinationFolder, release.FileName)
                        Await CommunityResourceDownload.SaveAsync(release, destination, Path.Combine(runRoot, "cache"), New SourcePolicy(), Nothing, CancellationToken.None)
                        row("Succeeded") = True : row("ProjectId") = project.Id : row("ProjectName") = project.Name : row("VersionId") = release.Id
                        row("FileName") = release.FileName : row("Size") = release.Size : row("Versions") = releases.Count
                        row("Loaders") = New JArray(release.Loaders) : row("Destination") = destination
                        Using input = File.OpenRead(destination), sha = SHA256.Create()
                            row("SHA256") = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "")
                        End Using
                    End Using
                Catch ex As Exception
                    succeeded = False : row("Succeeded") = False : row("Exception") = ex.ToString()
                End Try
                rows.Add(row)
                Console.WriteLine(row.ToString(Newtonsoft.Json.Formatting.None))
                File.WriteAllText(Path.Combine(output, "live.json"), New JObject From {{"Succeeded", succeeded}, {"Rows", rows}, {"GameLaunched", False}, {"UserGameDirectoriesWritten", False}}.ToString(), New UTF8Encoding(False))
            Next
        Next
        If Not succeeded Then Throw New IOException("Some live resource probes failed; inspect live.json.")
    End Function
End Module
