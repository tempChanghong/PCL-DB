Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module SourcePreferenceChecks
    Friend Async Function LiveAsync(directory As String) As Task
        directory = Path.GetFullPath(directory)
        If IO.Directory.Exists(directory) Then Throw New IOException("Live source check needs a new output directory.")
        IO.Directory.CreateDirectory(directory)
        Dim records As New JArray()
        For Each filePreference In {SourcePreference.OfficialFirst, SourcePreference.MirrorFirst}
            Dim policy As New CapturePolicy With {.Preference = filePreference, .VersionPreference = If(filePreference = SourcePreference.OfficialFirst, SourcePreference.MirrorFirst, SourcePreference.OfficialFirst)}
            Using timeout As New CancellationTokenSource(TimeSpan.FromMinutes(3))
                Dim versions = Await New MinecraftInstaller(policy).VersionsAsync(timeout.Token)
                Dim source As String = Nothing
                Dim progress As New InlineProgress(Sub(value) source = value.Source)
                Dim item As New DownloadFile With {.Url = "https://resources.download.minecraft.net/66/666999116c467f10622db1527a06ddc0a6efad2a", .Sha1 = "666999116c467f10622db1527a06ddc0a6efad2a", .Size = 1496916, .RelativePath = "unifont.zip", .Category = "asset"}
                Dim target = Path.Combine(directory, filePreference.ToString())
                Using engine As New DownloadEngine(policy, progress, {}, 2)
                    Await engine.DownloadAsync(target, {item}, timeout.Token)
                    If Not SafeFiles.Matches(Path.Combine(target, item.RelativePath), item) Then Throw New InvalidDataException("Live source hash mismatch.")
                    records.Add(New JObject From {{"FilePreference", filePreference.ToString()}, {"VersionPreference", policy.EffectiveVersionPreference.ToString()},
                        {"ManifestRequests", New JArray(policy.ManifestRequests)}, {"VersionCount", versions.Count},
                        {"FileCandidates", New JArray(policy.Candidates(item.Url, "asset"))}, {"LastFileSource", source},
                        {"NetworkBytes", engine.NetworkBytes}, {"Reused", engine.Reused}, {"VerifiedSize", item.Size}, {"VerifiedSha1", item.Sha1}})
                End Using
            End Using
        Next
        File.WriteAllText(Path.Combine(directory, "result.json"), records.ToString())
        Console.WriteLine("LIVE_SOURCE_OK " & Path.Combine(directory, "result.json"))
    End Function
    Private Class CapturePolicy
        Inherits SourcePolicy
        Friend ReadOnly ManifestRequests As New List(Of String)
        Protected Overrides Function MetadataUrl(source As String) As String
            SyncLock ManifestRequests
                ManifestRequests.Add(source)
            End SyncLock
            Return source
        End Function
    End Class
    Friend Async Function RunAsync(root As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "source-preference-matrix" Then Await MatrixAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "source-preference-catalogue" Then Await CataloguesAsync(passed)
        If onlyCase Is Nothing OrElse onlyCase = "source-preference-optifine" Then Await OptiFineAsync(root, passed)
        If passed.Count = 0 Then Throw New ArgumentException(onlyCase)
        Return passed
    End Function
    Private Sub Check(condition As Boolean, label As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub
    Private Function Utf8(value As String) As Byte()
        Return Encoding.UTF8.GetBytes(value)
    End Function
    Private Function Hash(bytes As Byte()) As String
        Using sha = Security.Cryptography.SHA1.Create()
            Return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function
    Private Class RoutedPolicy
        Inherits SourcePolicy
        Private ReadOnly _server As String
        Friend Sub New(server As String)
            _server = server : AllowLoopback = True
        End Sub
        Protected Overrides Function MetadataUrl(source As String) As String
            Dim uri As New Uri(source)
            If uri.IsLoopback Then Return source
            Return _server & If(uri.Host.Contains("bmclapi") OrElse uri.Host.Contains("mcimirror"), "/mirror", "/official") & uri.PathAndQuery
        End Function
        Public Overrides Function Candidates(url As String, category As String) As List(Of String)
            Dim result = MyBase.Candidates(url, category)
            ' Preserve original source identity for manifest timing; route only file
            ' transfers here, since their engine does not use MetadataUrl.
            Return If(category = "version", result, result.Select(Function(u) MetadataUrl(u)).ToList())
        End Function
    End Class
    Private Async Function MatrixAsync(root As String, passed As List(Of String)) As Task
        Dim entries As New JArray()
        For i = 1 To 200
            entries.Add(New JObject From {{"id", "fixture-" & i}, {"type", "release"}, {"sha1", New String("a"c, 40)}, {"url", "https://example.org/version"}, {"releaseTime", "2026-01-01T00:00:00Z"}})
        Next
        Dim manifest = Utf8(New JObject From {{"versions", entries}}.ToString())
        Dim bytes = Utf8("independently selected source file")
        For Each filePref As SourcePreference In [Enum].GetValues(GetType(SourcePreference))
            For Each listPref As SourcePreference In [Enum].GetValues(GetType(SourcePreference))
                Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(10))
                    server.Files("/official/mc/game/version_manifest_v2.json") = manifest
                    server.Files("/mirror/mc/game/version_manifest_v2.json") = manifest
                    server.Files("/official/client") = bytes : server.Files("/mirror/client") = bytes
                    Dim policy As New RoutedPolicy(server.Url) With {.Preference = filePref, .VersionPreference = listPref}
                    Dim versions = Await New MinecraftInstaller(policy).VersionsAsync(timeout.Token)
                    Dim listSource = If(listPref = SourcePreference.MirrorFirst, "mirror", "official")
                    Check(versions.Count = 200 AndAlso server.Requests("/" & listSource & "/mc/game/version_manifest_v2.json") = 1 AndAlso server.Requests.Count = 1, "version source " & listPref & " is independent of file source " & filePref, passed)
                    Dim officialFiles = filePref = SourcePreference.OfficialFirst OrElse (filePref = SourcePreference.Automatic AndAlso listPref <> SourcePreference.MirrorFirst)
                    Dim item As New DownloadFile With {.Url = "https://piston-data.mojang.com/client", .RelativePath = "client.jar", .Size = bytes.Length, .Sha1 = Hash(bytes)}
                    Dim target = Path.Combine(root, "preferences-" & filePref.ToString() & "-" & listPref.ToString())
                    Using engine As New DownloadEngine(policy, Nothing, {})
                        Await engine.DownloadAsync(target, {item}, timeout.Token)
                        Check(SafeFiles.Matches(Path.Combine(target, "client.jar"), item) AndAlso server.Requests("/" & If(officialFiles, "official", "mirror") & "/client") = 1 AndAlso server.Requests.Count = 2, "file source " & filePref & " survives list source " & listPref & " and verifies the whole file", passed)
                    End Using
                End Using
            Next
        Next
    End Function
    Private Async Function CataloguesAsync(passed As List(Of String)) As Task
        For Each preference In {SourcePreference.OfficialFirst, SourcePreference.MirrorFirst}
            Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(10))
                Dim policy As New RoutedPolicy(server.Url) With {.Preference = If(preference = SourcePreference.OfficialFirst, SourcePreference.MirrorFirst, SourcePreference.OfficialFirst), .VersionPreference = preference}
                Dim catalog As New ComponentCatalog(policy)
                server.Files("/official/net/minecraftforge/forge/maven-metadata.xml") = Utf8("<metadata><versioning><versions><version>1.20.1-47.2.0</version></versions></versioning></metadata>")
                server.Files("/mirror/forge/minecraft/1.20.1") = Utf8("[{""version"":""47.1.0"",""files"":[{""category"":""installer"",""format"":""jar""}]}]")
                server.Files("/official/api/maven/versions/releases/net/neoforged/neoforge") = Utf8("{""versions"":[""21.1.77""]}")
                server.Files("/mirror/neoforge/meta/api/maven/details/releases/net/neoforged/neoforge") = Utf8("{""files"":[{""name"":""21.1.78""},{""name"":""47.1.82""}]}")
                server.Files("/official/downloads") = Utf8("<td class='colForge'>Forge 47.2.18</td><a href='adloadx?f=OptiFine_1.20.1_HD_U_I6.jar'>Mirror</a>")
                server.Files("/mirror/optifine/versionList") = Utf8("[{""mcversion"":""1.20.1"",""type"":""HD_U"",""patch"":""I5"",""filename"":""OptiFine.jar"",""forge"":""N/A""}]")
                server.Files("/official/v2/versions/loader/1.20.1") = Utf8("[{""loader"":{""version"":""0.16.9"",""stable"":true}}]")
                server.Files("/mirror/fabric-meta/v2/versions/loader/1.20.1") = Utf8("[{""loader"":{""version"":""0.16.8"",""stable"":true}}]")
                Dim official = preference = SourcePreference.OfficialFirst
                For Each kind In {ComponentKind.Forge, ComponentKind.NeoForge, ComponentKind.OptiFine, ComponentKind.Fabric}
                    Dim mc = If(kind = ComponentKind.NeoForge, "1.21.1", "1.20.1")
                    Dim result = Await catalog.LoadAsync(kind, mc, timeout.Token)
                    Dim expected = If(kind = ComponentKind.Forge, If(official, "47.2.0", "47.1.0"), If(kind = ComponentKind.NeoForge, If(official, "21.1.77", "21.1.78"), If(kind = ComponentKind.OptiFine, If(official, "I6", "I5"), If(official, "0.16.9", "0.16.8"))))
                    Check(result.Count = 1 AndAlso result(0).Version = expected, kind & " list obeys " & preference & " with the opposite file preference", passed)
                Next
                If official Then
                    server.Files("/official/net/minecraftforge/forge/maven-metadata.xml") = Utf8("invalid XML")
                    Dim fallback = Await catalog.LoadAsync(ComponentKind.Forge, "1.20.1", timeout.Token)
                    Check(fallback.Single().Version = "47.1.0", "invalid preferred Forge XML falls back to parsed BMCLAPI JSON", passed)
                    server.Files("/official/downloads") = Utf8("invalid HTML")
                    fallback = Await catalog.LoadAsync(ComponentKind.OptiFine, "1.20.1", timeout.Token)
                    Check(fallback.Single().Version = "I5", "invalid preferred OptiFine HTML falls back to parsed BMCLAPI JSON", passed)
                End If
            End Using
        Next
    End Function
    Private Async Function OptiFineAsync(root As String, passed As List(Of String)) As Task
        Dim jar = OptiFinePayloadChecks.Jar(64 * 1024)
        For Each preference In {SourcePreference.OfficialFirst, SourcePreference.MirrorFirst}
            For Each invalidPreferred In {False, True}
                Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(10))
                    Dim policy As New RoutedPolicy(server.Url) With {.Preference = preference, .VersionPreference = If(preference = SourcePreference.OfficialFirst, SourcePreference.MirrorFirst, SourcePreference.OfficialFirst)}
                    server.Files("/official/adloadx?f=fixture.jar") = Utf8("<a href='downloadx?f=fixture.jar'>Download</a>")
                    server.Files("/official/downloadx?f=fixture.jar") = jar
                    server.Files("/mirror/optifine/fixture") = jar
                    Dim preferred = If(preference = SourcePreference.OfficialFirst, "/official/downloadx?f=fixture.jar", "/mirror/optifine/fixture")
                    If invalidPreferred Then server.Files(preferred) = Utf8("invalid installer")
                    Dim installRoot = Path.Combine(root, "optifine-source-" & preference & "-" & invalidPreferred)
                    Dim stage = Path.Combine(installRoot, ".vanilla-downloader", "test-stage")
                    Directory.CreateDirectory(stage) : File.WriteAllBytes(Path.Combine(stage, "fixture.jar"), {1})
                    Dim selected As New ComponentSelection()
                    selected.Entries(ComponentKind.LiteLoader) = New ComponentEntry With {.Kind = ComponentKind.LiteLoader, .Minecraft = "1.12.2", .Version = "1.12.2", .Data = New JObject From {{"libraries", New JArray()}, {"tweakClass", "fixture.Tweaker"}}}
                    selected.Entries(ComponentKind.OptiFine) = New ComponentEntry With {.Kind = ComponentKind.OptiFine, .Minecraft = "1.12.2", .Version = "fixture", .FileName = "fixture.jar", .Url = "https://bmclapi2.bangbang93.com/optifine/fixture"}
                    server.Files("/official/versions/com/mumfrey/liteloader/1.12.2/liteloader-1.12.2.jar") = Utf8("fixture runtime")
                    server.Files("/mirror/maven/com/mumfrey/liteloader/1.12.2/liteloader-1.12.2.jar") = Utf8("fixture runtime")
                    Dim metadata As New JObject From {{"id", "1.12.2"}, {"libraries", New JArray()}, {"downloads", New JObject From {{"client", New JObject From {{"url", server.Url & "/unused-client"}, {"size", 1}}}}}}
                    Await New ComponentInstaller(policy, selected, installRoot, {}, Nothing, 2, 0).ApplyAsync(metadata, "1.12.2", stage, "fixture", timeout.Token)
                    Check(server.Requests(preferred) = 1 AndAlso File.ReadAllBytes(Path.Combine(stage, "mods", "1.12.2", "fixture.jar")).SequenceEqual(jar), "OptiFine file uses " & preference & " and validates the installer (fallback=" & invalidPreferred & ")", passed)
                    Dim other = If(preference = SourcePreference.OfficialFirst, "/mirror/optifine/fixture", "/official/downloadx?f=fixture.jar")
                    Check(server.Requests.ContainsKey(other) = invalidPreferred, "OptiFine only contacts the alternate file source after preferred payload failure (" & preference & ", " & invalidPreferred & ")", passed)
                End Using
            Next
        Next
    End Function
End Module
