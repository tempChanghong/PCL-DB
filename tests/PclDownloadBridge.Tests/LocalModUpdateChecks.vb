Imports System.IO
Imports System.IO.Compression
Imports System.Net
Imports System.Net.Http
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module LocalModUpdateChecks
    Private Sub Check(value As Boolean, label As String, passed As List(Of String))
        If Not value Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub
    Private Function Jar(path As String, id As String, version As String) As String
        Directory.CreateDirectory(IO.Path.GetDirectoryName(path))
        Using stream = File.Create(path), zip As New ZipArchive(stream, ZipArchiveMode.Create), writer As New StreamWriter(zip.CreateEntry("fabric.mod.json").Open())
            writer.Write(New JObject From {{"schemaVersion", 1}, {"id", id}, {"version", version}, {"name", id}}.ToString())
        End Using
        Return path
    End Function
    Private Function Digest(path As String, algorithm As HashAlgorithm) As String
        Using algorithm
            Return BitConverter.ToString(algorithm.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant()
        End Using
    End Function
    Private Function Version(project As ModpackProject, path As String, id As String, published As String) As ModpackRelease
        Return New ModpackRelease With {.Kind = CommunityResourceKind.Mod, .Platform = project.Platform, .ProjectId = project.Id, .Id = id, .Name = id,
            .Version = id, .FileName = LocalModCatalog.EnabledFileName(path), .Size = New FileInfo(path).Length, .Sha1 = Digest(path, SHA1.Create()), .Md5 = Digest(path, MD5.Create()),
            .GameVersions = {"1.20.1"}, .Loaders = {"fabric"}, .Published = DateTimeOffset.Parse(published), .Url = "https://cdn.modrinth.com/data/resource1/" & IO.Path.GetFileName(path)}
    End Function
    Private Sub Reject(work As Action, label As String, passed As List(Of String))
        Dim rejected = False
        Try
            work()
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is OperationCanceledException
            rejected = True
        End Try
        Check(rejected, label, passed)
    End Sub
    Friend Async Function RunAsync(root As String) As Task(Of List(Of String))
        Dim passed As New List(Of String), testRoot = Path.Combine(root, "mod-updates-" & Guid.NewGuid().ToString("N"))
        For Each kind In {"PCL", "CE", "HMCL"}
        For Each isolated In {False, True}
            Dim label = kind & "-" & isolated.ToString(), fixture As New LocalModActionFollowupChecks.Fixture(testRoot, label, kind, isolated)
            Dim owner = fixture.Instance, mods = fixture.Mods
            Dim one = LocalModCatalog.Read(Jar(Path.Combine(mods, "one-1.jar"), "one", "1"), CancellationToken.None)
            Dim two = LocalModCatalog.Read(Jar(Path.Combine(mods, "two-1.jar.old"), "two", "1"), CancellationToken.None)
            Dim sourceOne = Jar(Path.Combine(testRoot, label, "downloads/one-2.jar"), "one", "2")
            Dim sourceTwo = Jar(Path.Combine(testRoot, label, "downloads/two-2.jar"), "two", "2")
            Dim project As New ModpackProject With {.Kind = CommunityResourceKind.Mod, .Platform = ModpackPlatform.Modrinth, .Id = "resource1", .Name = "one"}
            Dim before = Version(project, one.FilePath, "before01", "2026-01-01Z"), after = Version(project, sourceOne, "after001", "2026-02-01Z")
            Dim updateOne As New LocalModUpdate(one, project, before, after)
            Dim translated = LocalModCatalog.Read(Jar(Path.Combine(fixture.Root, "names/[中文] one-1.jar.disabled"), "one", "1"), CancellationToken.None)
            Check(LocalModActions.UpdateFileName(translated, before, after) = "[中文] one-2.jar.disabled", "PCL filename segment replacement retains translated prefix and disabled suffix " & label, passed)
            Dim custom = LocalModCatalog.Read(Jar(Path.Combine(fixture.Root, "names/favorite.jar.old"), "one", "1"), CancellationToken.None)
            Check(LocalModActions.UpdateFileName(custom, before, after) = custom.FileName, "fully custom filenames are retained without guessing a replacement " & label, passed)
            project = New ModpackProject With {.Kind = CommunityResourceKind.Mod, .Platform = ModpackPlatform.Modrinth, .Id = "resource2", .Name = "two"}
            Dim updateTwo As New LocalModUpdate(two, project, Version(project, two.FilePath, "before02", "2026-01-01Z"), Version(project, sourceTwo, "after002", "2026-02-01Z"))
            Dim files = LocalModFingerprint.Read(owner, {one, two}, CancellationToken.None)
            Check(files.Count = 2 AndAlso files(0).Sha1 = before.Sha1 AndAlso files(0).Md5 = before.Md5 AndAlso files(0).CurseForge = ReferenceFingerprint(File.ReadAllBytes(one.FilePath)), "file fingerprints bind SHA1 MD5 and CurseForge whitespace-filtered Murmur2 " & label, passed)
            Check(LocalModUpdate.Compatible(owner, before, after), "matching native Minecraft and loader accepts a newer file " & label, passed)
            For Each invalid In {"older", "loader", "game", "identity", "same-bytes"}
                Dim candidate = after.Copy()
                Select Case invalid
                    Case "older" : candidate.Published = before.Published
                    Case "loader" : candidate.Loaders = {"fabric", "forge"}
                    Case "game" : candidate.GameVersions = {"1.20.2"}
                    Case "identity" : candidate.ProjectId = "resource9"
                    Case "same-bytes" : candidate.Sha1 = before.Sha1 : candidate.Size = before.Size
                End Select
                Check(Not LocalModUpdate.Compatible(owner, before, candidate), "update rejects " & invalid & " " & label, passed)
            Next
            Using handler As New CatalogueHandler(files(0), before, after), client As New ModpackCatalogClient(handler, "", 2)
                Dim result = Await client.CheckLocalModsAsync(owner, {files(0)}, CancellationToken.None)
                Check(result.Errors.Count = 0 AndAlso result.Matches.Single().HasUpdate AndAlso handler.VerifiedRequests = 2, "batch update requests identify exact bytes and constrain game and loader " & label, passed)
            End Using
            Using handler As New CatalogueHandler(files(0), before, after) With {.FailRinth = True}, client As New ModpackCatalogClient(handler, "", 2)
                Dim result = Await client.CheckLocalModsAsync(owner, {files(0)}, CancellationToken.None)
                Check(result.Errors.Count = 1 AndAlso result.Errors(0).Contains("Modrinth"), "one platform failure returns an explicit partial-source result " & label, passed)
            End Using
            Using handler As New CatalogueHandler(files(0), before, after) With {.FailRinth = True, .MatchCurse = True}, client As New ModpackCatalogClient(handler, "", 2)
                Dim result = Await client.CheckLocalModsAsync(owner, {files(0)}, CancellationToken.None)
                Check(result.Errors.Count = 1 AndAlso result.Matches.Single().HasUpdate AndAlso result.Matches(0).Release.Platform = ModpackPlatform.CurseForge AndAlso handler.VerifiedRequests = 3,
                    "CurseForge exact fingerprint and digest resolve a loader-constrained update when Modrinth is down " & label, passed)
            End Using
            Using handler As New CatalogueHandler(files(0), before, after) With {.FailRinth = True, .MatchCurse = True, .WrongDigest = True}, client As New ModpackCatalogClient(handler, "", 2)
                Dim result = Await client.CheckLocalModsAsync(owner, {files(0)}, CancellationToken.None)
                Check(result.Matches.Count = 0, "a CurseForge fingerprint collision without a cryptographic match cannot offer an update " & label, passed)
            End Using
            Dim enabled = LocalModActions.SetEnabledBatch(owner, {one, two}, True, CancellationToken.None)
            Check(enabled.ChangedCount = 1 AndAlso enabled.SkippedCount = 1 AndAlso File.Exists(Path.Combine(mods, two.EnabledName)), "bulk enable changes only disabled files " & label, passed)
            two = LocalModCatalog.Read(Path.Combine(mods, two.EnabledName), CancellationToken.None)
            LocalModActions.SetEnabledBatch(owner, {two}, False, CancellationToken.None)
            two = LocalModCatalog.Read(Path.Combine(mods, two.EnabledName & ".disabled"), CancellationToken.None)
            updateTwo = New LocalModUpdate(two, updateTwo.Project, updateTwo.Current, updateTwo.Release)
            Dim originalBytes = File.ReadAllBytes(one.FilePath)
            File.AppendAllText(one.FilePath, "changed")
            Reject(Sub() LocalModActions.ApplyUpdates(owner, {updateOne, updateTwo}, {sourceOne, sourceTwo}, CancellationToken.None), "changed installed bytes reject an old accepted update snapshot " & label, passed)
            File.WriteAllBytes(one.FilePath, originalBytes)
            File.WriteAllText(Path.Combine(mods, "one-2.jar"), "keep")
            Reject(Sub() LocalModActions.ApplyUpdates(owner, {updateOne, updateTwo}, {sourceOne, sourceTwo}, CancellationToken.None), "late filename collision rejects entire update before replacing old files " & label, passed)
            Check(File.Exists(one.FilePath) AndAlso File.Exists(two.FilePath), "collision preserves both originals " & label, passed)
            File.Delete(Path.Combine(mods, "one-2.jar"))
            Using cancel As New CancellationTokenSource()
                Reject(Sub() LocalModActions.ApplyUpdatesCore(owner, New List(Of LocalModUpdate) From {updateOne, updateTwo}, New List(Of String) From {sourceOne, sourceTwo}, cancel.Token,
                    Sub(index)
                        If index = 1 Then cancel.Cancel()
                    End Sub), "cancel after first publication rolls back transaction " & label, passed)
            End Using
            Check(File.Exists(one.FilePath) AndAlso File.Exists(two.FilePath) AndAlso Not File.Exists(Path.Combine(mods, "one-2.jar")), "rollback restores exact old names including disabled suffix " & label, passed)
            Dim installed As LocalModActionResult
            Using server As New TestServer()
                server.Files("/one-2.jar") = File.ReadAllBytes(sourceOne) : server.Files("/two-2.jar") = File.ReadAllBytes(sourceTwo)
                Dim firstRelease = updateOne.Release, secondRelease = updateTwo.Release
                firstRelease.Url = server.Url & "/one-2.jar" : secondRelease.Url = server.Url & "/two-2.jar"
                Dim downloads = {New LocalModUpdate(one, updateOne.Project, updateOne.Current, firstRelease), New LocalModUpdate(two, updateTwo.Project, updateTwo.Current, secondRelease)}
                installed = Await LocalModUpdater.InstallAsync(owner, downloads, Path.Combine(fixture.Root, "cache"), New ModrinthChecks.Policy(server.Url), Nothing, CancellationToken.None, 4)
                Check(server.Requests.Count >= 2, "real loopback download and verification complete before the native replacement transaction " & label, passed)
            End Using
            Check(installed.ChangedCount = 2 AndAlso File.Exists(Path.Combine(mods, "one-2.jar")) AndAlso File.Exists(Path.Combine(mods, "two-2.jar.disabled")) AndAlso
                File.Exists(Path.Combine(installed.BackupDirectory, one.FileName)) AndAlso File.Exists(Path.Combine(installed.BackupDirectory, two.FileName)), "retry publishes verified updates preserves disabled state and keeps originals in backup " & label, passed)
            Dim updatedOne = LocalModCatalog.Read(Path.Combine(mods, "one-2.jar"), CancellationToken.None)
            Dim deleted = LocalModActions.DeleteBatch(owner, {updatedOne}, CancellationToken.None)
            Check(deleted.ChangedCount = 1 AndAlso Not File.Exists(updatedOne.FilePath) AndAlso File.Exists(Path.Combine(deleted.BackupDirectory, updatedOne.FileName)), "delete moves selected bytes to recoverable backup " & label, passed)
            Dim duplicate = Jar(Path.Combine(mods, "two-2.jar"), "two", "2")
            Dim disabledDuplicate = LocalModCatalog.Read(Path.Combine(mods, "two-2.jar.disabled"), CancellationToken.None)
            Dim removed = LocalModActions.DeleteBatch(owner, {disabledDuplicate}, CancellationToken.None)
            Check(removed.ChangedCount = 1 AndAlso File.Exists(duplicate) AndAlso Not File.Exists(disabledDuplicate.FilePath) AndAlso File.Exists(Path.Combine(removed.BackupDirectory, disabledDuplicate.FileName)),
                "explicit deletion can resolve an enabled-disabled alias conflict without touching the unselected file " & label, passed)
            Check(fixture.OriginalsUnchanged(), "all native instance and external launcher settings remain untouched " & label, passed)
        Next
        Next
        Return passed
    End Function
    Private Function ReferenceFingerprint(bytes As Byte()) As UInteger
        Dim data = bytes.Where(Function(value) value <> 9 AndAlso value <> 10 AndAlso value <> 13 AndAlso value <> 32).ToArray(), hash As ULong = 1UL Xor CULng(data.Length), position = 0
        While position + 4 <= data.Length
            Dim word As ULong = BitConverter.ToUInt32(data, position)
            word = (word * &H5BD1E995UL) And &HFFFFFFFFUL : word = word Xor (word >> 24) : word = (word * &H5BD1E995UL) And &HFFFFFFFFUL
            hash = ((hash * &H5BD1E995UL) And &HFFFFFFFFUL) Xor word : position += 4
        End While
        If position < data.Length Then
            Dim tail As ULong = 0
            For index = position To data.Length - 1 : tail = tail Or (CULng(data(index)) << ((index - position) * 8)) : Next
            hash = ((hash Xor tail) * &H5BD1E995UL) And &HFFFFFFFFUL
        End If
        hash = hash Xor (hash >> 13) : hash = (hash * &H5BD1E995UL) And &HFFFFFFFFUL : Return CUInt(hash Xor (hash >> 15))
    End Function
    Private Class CatalogueHandler
        Inherits HttpMessageHandler
        Private ReadOnly _file As LocalModFingerprint, _before As ModpackRelease, _after As ModpackRelease
        Friend FailRinth As Boolean, MatchCurse As Boolean, WrongDigest As Boolean, VerifiedRequests As Integer
        Friend Sub New(file As LocalModFingerprint, before As ModpackRelease, after As ModpackRelease)
            _file = file : _before = before : _after = after
        End Sub
        Private Function Raw(release As ModpackRelease) As JObject
            Dim data = CommunityResourceChecks.ReleaseData(CommunityResourceKind.Mod, release.ProjectId, release.Id)
            data("name") = release.Name : data("version_number") = release.Version : data("date_published") = release.Published.ToString("o")
            data("game_versions") = New JArray(release.GameVersions)
            data("files") = New JArray(New JObject From {{"primary", True}, {"filename", release.FileName}, {"url", release.Url}, {"size", release.Size}, {"hashes", New JObject From {{"sha1", release.Sha1}}}})
            Return data
        End Function
        Private Function CurseRaw(release As ModpackRelease, id As Integer) As JObject
            Return New JObject From {{"id", id}, {"modId", 321}, {"displayName", release.Name}, {"fileName", release.FileName}, {"fileLength", release.Size},
                {"downloadUrl", "https://edge.forgecdn.net/files/" & (id \ 1000).ToString() & "/" & (id Mod 1000).ToString() & "/" & release.FileName},
                {"fileDate", release.Published.ToString("o")}, {"releaseType", 1}, {"isAvailable", True}, {"fileFingerprint", _file.CurseForge},
                {"gameVersions", New JArray("1.20.1", "Fabric")}, {"hashes", New JArray(New JObject From {{"algo", 1}, {"value", If(WrongDigest, New String("c"c, 40), release.Sha1)}})}}
        End Function
        Protected Overrides Async Function SendAsync(request As HttpRequestMessage, token As CancellationToken) As Task(Of HttpResponseMessage)
            token.ThrowIfCancellationRequested()
            Dim result As JToken
            If (request.RequestUri.Host.Contains("modrinth") OrElse request.RequestUri.AbsolutePath.Contains("/modrinth/")) Then
                If FailRinth Then Throw New HttpRequestException("fixture source down")
                Dim body = JObject.Parse(Await request.Content.ReadAsStringAsync())
                If request.Method <> HttpMethod.Post OrElse CStr(body("algorithm")) <> "sha1" OrElse Not body("hashes").Values(Of String)().Contains(_file.Sha1) Then Throw New InvalidDataException("wrong fingerprint request")
                If request.RequestUri.AbsolutePath.EndsWith("/update") AndAlso (CStr(body("game_versions")(0)) <> "1.20.1" OrElse CStr(body("loaders")(0)) <> "fabric") Then Throw New InvalidDataException("wrong compatibility request")
                VerifiedRequests += 1
                result = New JObject From {{_file.Sha1, Raw(If(request.RequestUri.AbsolutePath.EndsWith("/update"), _after, _before))}}
            Else
                If Not MatchCurse Then
                    result = New JObject From {{"data", New JObject From {{"exactMatches", New JArray()}}}}
                ElseIf request.RequestUri.AbsolutePath.EndsWith("/fingerprints/432") Then
                    Dim body = JObject.Parse(Await request.Content.ReadAsStringAsync())
                    If CLng(body("fingerprints")(0)) <> CLng(_file.CurseForge) Then Throw New InvalidDataException("wrong CurseForge fingerprint request")
                    VerifiedRequests += 1
                    result = New JObject From {{"data", New JObject From {{"exactMatches", New JArray(New JObject From {{"id", 321}, {"file", CurseRaw(_before, 1001)}})}}}}
                ElseIf request.RequestUri.AbsolutePath.EndsWith("/mods/files") Then
                    VerifiedRequests += 1 : result = New JObject From {{"data", New JArray(CurseRaw(_after, 2001))}}
                ElseIf request.RequestUri.AbsolutePath.EndsWith("/mods") Then
                    VerifiedRequests += 1
                    Dim project = ModpackCatalogChecks.CfProject(321)
                    project("classId") = 6 : project("latestFilesIndexes") = New JArray(New JObject From {{"gameVersion", "1.20.1"}, {"modLoader", 4}, {"fileId", 2001}})
                    result = New JObject From {{"data", New JArray(project)}}
                Else
                    Throw New InvalidDataException("unexpected CF route")
                End If
            End If
            Return New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New StringContent(result.ToString(), Encoding.UTF8, "application/json")}
        End Function
    End Class
End Module
