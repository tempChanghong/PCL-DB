Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module ModrinthChecks
    Private Sub Check(value As Boolean, label As String, passed As List(Of String))
        If Not value Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub
    Friend Function Hash(bytes As Byte(), Optional sha512 As Boolean = False) As String
        Using algorithm As Security.Cryptography.HashAlgorithm = If(sha512, DirectCast(Security.Cryptography.SHA512.Create(), Security.Cryptography.HashAlgorithm), Security.Cryptography.SHA1.Create())
            Return BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function
    Friend Function Download(reference As ModrinthReference) As DownloadFile
        Return New DownloadFile With {.RelativePath = reference.Path, .Url = reference.Downloads(0), .AlternateUrls = reference.Downloads.Skip(1).ToArray(),
            .EstimatedSize = reference.EstimatedSize, .Sha1 = reference.Sha1, .Sha512 = reference.Sha512, .Category = "modrinth"}
    End Function
    Friend Function Manifest(Optional loader As Boolean = True) As JObject
        Dim files As New JArray()
        For Each label In {"required", "optional", "unsupported"}
            Dim bytes = Encoding.UTF8.GetBytes("fixture " & label & " content")
            files.Add(New JObject From {{"path", "mods/" & label & ".jar"}, {"hashes", New JObject From {{"sha1", Hash(bytes)}, {"sha512", Hash(bytes, True)}}},
                {"fileSize", bytes.Length + 999}, {"env", New JObject From {{"client", label}, {"server", "required"}}}, {"downloads", New JArray("https://cdn.modrinth.com/" & label)}})
        Next
        Dim dependencies As New JObject From {{"minecraft", "1.20.1"}}
        If loader Then dependencies("fabric-loader") = "0.16.9"
        Return New JObject From {{"formatVersion", 1}, {"game", "minecraft"}, {"versionId", "2.3"}, {"name", "MR fixture"}, {"dependencies", dependencies}, {"files", files}}
    End Function
    Friend Function Pack(root As String, label As String, Optional manifest As JObject = Nothing, Optional entries As Dictionary(Of String, String) = Nothing, Optional prefix As String = "") As String
        Directory.CreateDirectory(root)
        Dim target = Path.Combine(root, "mr-" & label & ".mrpack")
        Using archive = ZipFile.Open(target, ZipArchiveMode.Create)
            Using writer As New StreamWriter(archive.CreateEntry(prefix & "modrinth.index.json").Open())
                writer.Write(If(manifest, ModrinthChecks.Manifest()).ToString())
            End Using
            If entries Is Nothing Then entries = New Dictionary(Of String, String) From {{"overrides/config/second.txt", "small"}, {"overrides/config/fixture.txt", "base"}, {"client-overrides/config/fixture.txt", "client"},
                {"server-overrides/config/server.txt", "server"}, {"overrides/PCL/Setup.ini", "UserSetting:preserved" & vbCrLf & "VersionArgumentIndieV2:False"},
                {"client-overrides/PCL/Setup.ini", "UserSetting:client" & vbCrLf & "VersionArgumentIndie:0" & vbCrLf & "IsStar:True"}}
            For Each entry In entries
                Using writer As New StreamWriter(archive.CreateEntry(prefix & entry.Key).Open())
                    writer.Write(entry.Value)
                End Using
            Next
        End Using
        Return target
    End Function
    Friend Class Policy
        Inherits SourcePolicy
        Private ReadOnly _server As String
        Friend Sub New(server As String)
            _server = server : AllowLoopback = True
        End Sub
        Public Overrides Function Candidates(url As String, category As String) As List(Of String)
            Return New List(Of String) From {_server & New Uri(url).PathAndQuery}
        End Function
        Protected Overrides Function MetadataUrl(source As String) As String
            Return _server & New Uri(source).PathAndQuery
        End Function
    End Class
    Friend Class Progress
        Implements IProgress(Of TransferProgress)
        Private ReadOnly _callback As Action(Of TransferProgress)
        Friend Sub New(callback As Action(Of TransferProgress))
            _callback = callback
        End Sub
        Public Sub Report(value As TransferProgress) Implements IProgress(Of TransferProgress).Report
            _callback(value)
        End Sub
    End Class
    Friend Async Function RunAsync(root As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "modrinth-all" OrElse onlyCase = "modrinth-environment" Then Await ModrinthEnvironmentChecks.RunAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "modrinth-all" OrElse onlyCase = "modrinth-package" Then Packages(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "modrinth-all" OrElse onlyCase = "modrinth-download" Then Await DownloadsAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "modrinth-all" OrElse onlyCase = "modrinth-install" Then Await InstallsAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "modrinth-all" OrElse onlyCase = "modrinth-isolation" Then Await IsolationPublicationAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "modrinth-all" OrElse onlyCase = "modrinth-choices" Then Await OptionalTopologyAsync(root, passed)
        If passed.Count = 0 Then Throw New ArgumentException("Unknown Modrinth regression: " & onlyCase)
        Return passed
    End Function
    Private Sub Packages(root As String, passed As List(Of String))
        Dim package = ModrinthPackage.Read(Pack(root, "valid"))
        Check(package.Minecraft = "1.20.1" AndAlso package.LoaderKind.GetValueOrDefault() = ComponentKind.Fabric AndAlso package.LoaderVersion = "0.16.9", "MR keeps exact declared game and loader versions", passed)
        Check(package.References.Count = 3 AndAlso package.References(1).Client = "optional" AndAlso package.OverrideFiles.Count = 3, "MR retains client env and flattens only permitted override layers", passed)
        Check(ModpackFormats.Detect(package.SourcePath) = ModpackFormat.Modrinth AndAlso ModrinthPackage.Read(Pack(root, "wrapped", prefix:="one/")).OverrideFiles.Count = 3, "MR content detection accepts root and PCL single wrapping folder", passed)
        Dim nestedIndex = Pack(root, "nested-index", entries:=New Dictionary(Of String, String) From {{"overrides/modrinth.index.json", "ordinary packaged file"}, {"overrides/manifest.json", "ordinary packaged file"}})
        Check(ModpackFormats.Detect(nestedIndex) = ModpackFormat.Modrinth AndAlso ModrinthPackage.Read(nestedIndex).OverrideFiles.Count = 2, "MR root index takes precedence over ordinary packaged manifest filenames", passed)
        Dim changes As New Dictionary(Of String, Action(Of JObject)) From {
            {"format", Sub(j) j("formatVersion") = 2}, {"game", Sub(j) j("game") = "other"}, {"missing minecraft", Sub(j) DirectCast(j("dependencies"), JObject).Remove("minecraft")},
            {"unknown dependency", Sub(j) j("dependencies")("quilt-loader") = "0.27.0"}, {"multiple loaders", Sub(j) j("dependencies")("forge") = "47.4.10"},
            {"floating loader", Sub(j) j("dependencies")("fabric-loader") = "latest"}, {"sha512", Sub(j) j("files")(0)("hashes")("sha512") = "abc"},
            {"sha1", Sub(j) j("files")(0)("hashes")("sha1") = New String("z"c, 40)}, {"missing sha512", Sub(j) DirectCast(j("files")(0)("hashes"), JObject).Remove("sha512")},
            {"negative size", Sub(j) j("files")(0)("fileSize") = -1}, {"size type", Sub(j) j("files")(0)("fileSize") = "42"},
            {"env", Sub(j) j("files")(0)("env")("client") = "sometimes"}, {"null env", Sub(j) j("files")(0)("env") = Nothing},
            {"duplicate path", Sub(j) j("files")(1)("path") = "MODS/REQUIRED.JAR"}, {"empty urls", Sub(j) j("files")(0)("downloads") = New JArray()},
            {"unknown host", Sub(j) j("files")(0)("downloads") = New JArray("https://cdn.modrinth.com.attacker.invalid/mod.jar")},
            {"http", Sub(j) j("files")(0)("downloads") = New JArray("http://cdn.modrinth.com/mod.jar")},
            {"credentials", Sub(j) j("files")(0)("downloads") = New JArray("https://user@cdn.modrinth.com/mod.jar")},
            {"loopback", Sub(j) j("files")(0)("downloads") = New JArray("https://127.0.0.1/mod.jar")},
            {"bad percent escape", Sub(j) j("files")(0)("downloads") = New JArray("https://cdn.modrinth.com/a%ZZ.jar")},
            {"url spaces", Sub(j) j("files")(0)("downloads") = New JArray("https://cdn.modrinth.com/a b.jar")}}
        For Each pair In changes
            Dim value = Manifest() : pair.Value.Invoke(value)
            Reject(Sub() ModrinthPackage.Read(Pack(root, "bad-" & passed.Count, value)), "MR rejects " & pair.Key, passed)
        Next
        For Each path In {"../escape", "mods/../outside", "/root", "mods/CON.jar", "mods/a.", "mods/a ", "mods/ads:stream", "mods//empty", "C:/absolute"}
            Dim value = Manifest() : value("files")(0)("path") = path
            Reject(Sub() ModrinthPackage.Read(Pack(root, "bad-path-" & passed.Count, value)), "MR rejects unsafe declared path " & path, passed)
        Next
        For Each entries In {New Dictionary(Of String, String) From {{"overrides/a", "1"}, {"overrides/A", "2"}},
            New Dictionary(Of String, String) From {{"overrides/a", "file"}, {"client-overrides/a/child", "child"}},
            New Dictionary(Of String, String) From {{"overrides/../escape", "bad"}}, New Dictionary(Of String, String) From {{"overrides/mods", "file"}}}
            Reject(Sub() ModrinthPackage.Read(Pack(root, "bad-entries-" & passed.Count, entries:=entries)), "MR rejects archive/final-client topology conflict " & passed.Count, passed)
        Next
        Dim duplicate = Pack(root, "duplicate-json")
        Using zip = ZipFile.Open(duplicate, ZipArchiveMode.Update)
            zip.GetEntry("modrinth.index.json").Delete()
            Using writer As New StreamWriter(zip.CreateEntry("modrinth.index.json").Open())
                writer.Write("{""game"":""minecraft"",""game"":""other""}")
            End Using
        End Using
        Reject(Sub() ModrinthPackage.Read(duplicate), "MR rejects duplicate JSON properties", passed)
        Dim linked = Pack(root, "symlink")
        Using zip = ZipFile.Open(linked, ZipArchiveMode.Update)
            zip.CreateEntry("overrides/link").ExternalAttributes = &HA1FF0000
        End Using
        Reject(Sub() ModrinthPackage.Read(linked), "MR rejects ZIP symbolic links", passed)
        Dim absentEnv = Manifest() : DirectCast(absentEnv("files")(0), JObject).Remove("env")
        Check(ModrinthPackage.Read(Pack(root, "absent-env", absentEnv)).References(0).Client = "required", "MR omitted env defaults to a required client file like PCL", passed)
        For Each loader In {"forge", "neoforge", "neo-forge", "fabric-loader"}
            Dim value = Manifest(False) : value("dependencies")(loader) = "47.4.10"
            Check(ModrinthPackage.Read(Pack(root, "loader-" & loader, value)).LoaderKind.HasValue, "MR recognizes PCL dependency identifier " & loader, passed)
        Next
    End Sub
    Private Async Function IsolationPublicationAsync(root As String, passed As List(Of String)) As Task
        For Each mode In {"disabled", "missing", "legacy", "duplicate", "malformed", "case-only"}
            Using fixture As New InstallationChecks.InstallFixture(root, "mr-isolation-" & mode)
                Configure(fixture)
                Dim package = ModrinthPackage.Read(Pack(root, "isolation-" & mode, Manifest(False), New Dictionary(Of String, String) From {{"overrides/config/fixture.txt", "config"}}))
                Dim globalIni = Path.Combine(fixture.Root, "PCL", "Setup.ini"), globalText = "LaunchArgumentIndieV2:0" & vbCrLf & "UserSetting:keep"
                Directory.CreateDirectory(Path.GetDirectoryName(globalIni)) : File.WriteAllText(globalIni, globalText)
                Dim rejected = False, installer As New ModrinthInstaller(New Policy(fixture.Server.Url))
                Try
                    Await installer.InstallAsync(package, fixture.Root, "pack", {}, {}, New Progress(Sub(value)
                        If value.Stage <> "完成安装" Then Return
                        Dim stage = Directory.EnumerateDirectories(Path.Combine(fixture.Root, ".vanilla-downloader", "staging")).Single()
                        Dim target = Path.Combine(stage, "payload", "PCL", "Setup.ini")
                        Select Case mode
                            Case "disabled" : File.WriteAllText(target, "VersionArgumentIndie:2" & vbCrLf & "VersionArgumentIndieV2:False")
                            Case "missing" : File.Delete(target)
                            Case "legacy" : File.WriteAllText(target, "VersionArgumentIndie:2" & vbCrLf & "VersionArgumentIndieV2:True")
                            Case "duplicate" : File.AppendAllText(target, "VersionArgumentIndieV2:False" & vbCrLf)
                            Case "malformed" : File.WriteAllText(target, "VersionArgumentIndie:1" & vbCrLf & "VersionArgumentIndieV2:invalid")
                            Case "case-only" : File.WriteAllText(target, "versionargumentindie:1" & vbCrLf & "versionargumentindiev2:True")
                        End Select
                    End Sub), CancellationToken.None, connections:=4)
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException
                    rejected = True
                End Try
                Check(rejected AndAlso Not Directory.Exists(Path.Combine(fixture.Root, "versions", "pack")), "MR publication rejects changed generated isolation without a packaged INI: " & mode, passed)
                Check(File.ReadAllText(globalIni) = globalText, "MR isolation publication failure preserves global settings: " & mode, passed)
                Dim retry = Await installer.InstallAsync(package, fixture.Root, "pack", {}, {}, Nothing, CancellationToken.None, connections:=4)
                Check(retry.NetworkBytes = 0 AndAlso PclFolders.ReadIniValue(Path.Combine(retry.InstancePath, "PCL", "Setup.ini"), "VersionArgumentIndieV2") = "True", "MR isolation failure retries with verified content and regenerated settings: " & mode, passed)
            End Using
        Next
    End Function
    Private Async Function OptionalTopologyAsync(root As String, passed As List(Of String)) As Task
        For Each mode In {"none", "parent", "child", "both"}
            Using fixture As New InstallationChecks.InstallFixture(root, "mr-topology-" & mode)
                Configure(fixture)
                Dim manifest = ModrinthChecks.Manifest(False)
                manifest("files")(1)("path") = "config/feature"
                manifest("files")(2)("path") = "config/feature/settings.json" : manifest("files")(2)("env")("client") = "optional"
                Dim package = ModrinthPackage.Read(Pack(root, "topology-" & mode, manifest))
                Dim choices = If(mode = "none", New String() {}, If(mode = "parent", {"config/feature"}, If(mode = "child", {"config/feature/settings.json"}, {"config/feature", "config/feature/settings.json"})))
                Dim rejected = False, result As InstallResult = Nothing
                Try
                    result = Await New ModrinthInstaller(New Policy(fixture.Server.Url)).InstallAsync(package, fixture.Root, "pack", {}, choices, Nothing, CancellationToken.None)
                Catch ex As InvalidDataException
                    rejected = True
                End Try
                If mode = "both" Then
                    Check(rejected AndAlso Not Directory.Exists(fixture.Root) AndAlso fixture.Server.Requests.Count = 0, "MR mutually conflicting optional choices stop before network or game writes", passed)
                Else
                    Check(Not rejected AndAlso result IsNot Nothing AndAlso File.Exists(Path.Combine(result.InstancePath, "mods", "required.jar")), "MR unselected optional topology does not block valid client installation: " & mode, passed)
                End If
            End Using
        Next
    End Function
    Private Sub Reject(action As Action, label As String, passed As List(Of String))
        Dim rejected = False
        Try
            action()
        Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is NotSupportedException OrElse TypeOf ex Is Newtonsoft.Json.JsonException
            rejected = True
        End Try
        Check(rejected, label, passed)
    End Sub
    Private Async Function DownloadsAsync(root As String, passed As List(Of String)) As Task
        Using server As New TestServer()
            Dim bytes(1024 * 1024 - 1) As Byte, random As New Random(2206) : random.NextBytes(bytes)
            server.Files("/good") = bytes : server.Files("/bad") = Encoding.UTF8.GetBytes("bad")
            Dim item As New DownloadFile With {.RelativePath = "mods/file.jar", .Url = "https://cdn.modrinth.com/missing", .AlternateUrls = {"https://cdn.modrinth.com/good"}, .EstimatedSize = 1, .Sha1 = Hash(bytes), .Sha512 = Hash(bytes, True), .Category = "modrinth"}
            Dim target = Path.Combine(root, "mr-download"), policy As New Policy(server.Url)
            Using engine As New DownloadEngine(policy, Nothing, {}, 2, target, True)
                Await engine.DownloadAsync(target, {item}, CancellationToken.None)
                Check(SafeFiles.Matches(SafeFiles.UnderRoot(target, item.RelativePath), item), "MR ordered URL fallback downloads verified content despite wrong advisory size", passed)
                Check(engine.Retries > 0 AndAlso item.Size = -1 AndAlso item.EstimatedSize = 1, "MR transfer snapshots preserve caller declarations and retry state", passed)
            End Using
            item.RelativePath = "other/file.jar"
            Dim before = server.Requests.Values.Sum()
            Using engine As New DownloadEngine(policy, Nothing, {}, 2, target, True)
                Await engine.DownloadAsync(target, {item}, CancellationToken.None)
                Check(engine.NetworkBytes = 0 AndAlso server.Requests.Values.Sum() = before, "MR SHA512 cache reuses identical content at a new path without requests", passed)
            End Using
            item.Sha512 = New String("0"c, 128)
            Check(Not SafeFiles.Matches(SafeFiles.UnderRoot(target, item.RelativePath), item), "MR SHA512 rejects content even when SHA1 matches", passed)
            item.Sha512 = Hash(bytes, True) : item.Sha1 = New String("0"c, 40)
            Check(Not SafeFiles.Matches(SafeFiles.UnderRoot(target, item.RelativePath), item), "MR SHA1 rejects content even when SHA512 matches", passed)
            item.Sha1 = Hash(bytes) : item.RelativePath = "resume/file.jar" : item.Url = "https://cdn.modrinth.com/good" : item.AlternateUrls = Nothing
            Dim cancelled = False
            Using cancellation As New CancellationTokenSource(), engine As New DownloadEngine(policy, New Progress(Sub(value)
                                                                                                                   If value.NetworkBytes >= 65536 Then cancellation.Cancel()
                                                                                                               End Sub), {}, 1, Path.Combine(root, "mr-resume")) With {.BytesPerSecondLimit = 256 * 1024}
                Try
                    Await engine.DownloadAsync(Path.Combine(root, "mr-resume"), {item}, cancellation.Token)
                Catch ex As OperationCanceledException
                    cancelled = True
                End Try
            End Using
            Check(cancelled AndAlso Not File.Exists(SafeFiles.UnderRoot(Path.Combine(root, "mr-resume"), item.RelativePath)), "MR cancellation retains only unpublished transfer data", passed)
            Dim rangeBefore = server.RangeRequests
            Using engine As New DownloadEngine(policy, Nothing, {}, 1, Path.Combine(root, "mr-resume"))
                Await engine.DownloadAsync(Path.Combine(root, "mr-resume"), {item}, CancellationToken.None)
                Check(engine.NetworkBytes < bytes.Length AndAlso server.RangeRequests > rangeBefore AndAlso SafeFiles.Matches(SafeFiles.UnderRoot(Path.Combine(root, "mr-resume"), item.RelativePath), item), "MR cancelled single transfer resumes with server length and rechecks both hashes", passed)
            End Using
            ' A mutable alternate list must be captured before progress callbacks.
            item.RelativePath = "snapshot/file.jar" : item.Url = "https://cdn.modrinth.com/missing" : item.AlternateUrls = {"https://cdn.modrinth.com/good"}
            Using engine As New DownloadEngine(policy, New Progress(Sub(value) item.AlternateUrls(0) = "https://cdn.modrinth.com/bad"), {}, 1, Path.Combine(root, "mr-snapshot"))
                Await engine.DownloadAsync(Path.Combine(root, "mr-snapshot"), {item}, CancellationToken.None)
                Check(SafeFiles.Matches(SafeFiles.UnderRoot(Path.Combine(root, "mr-snapshot"), item.RelativePath), item), "MR alternate URLs are immutable during asynchronous transfer", passed)
            End Using
            item.Url = "https://cdn.modrinth.com/good" : item.AlternateUrls = Nothing
            For Each mode In {"unsupported-head", "small-head", "large-head", "ignored-range", "large-split"}
                If mode = "unsupported-head" Then server.HeadStatusCodes("/good") = 405
                If mode = "small-head" Then server.HeadLengths("/good") = 1
                If mode = "large-head" Then server.HeadLengths("/good") = bytes.Length + 1
                If mode = "ignored-range" Then server.IgnoreRanges = True
                If mode = "large-split" Then
                    ReDim bytes(9 * 1024 * 1024 - 1) : random.NextBytes(bytes)
                    server.Files("/good") = bytes : item.Sha1 = Hash(bytes) : item.Sha512 = Hash(bytes, True)
                End If
                Dim location = Path.Combine(root, "mr-" & mode), rangesBefore = server.RangeRequests
                Using engine As New DownloadEngine(policy, Nothing, {}, 4, location)
                    Await engine.DownloadAsync(location, {item}, CancellationToken.None)
                    Check(SafeFiles.Matches(SafeFiles.UnderRoot(location, item.RelativePath), item), "MR verifies hashes with server behavior " & mode, passed)
                    If mode = "large-split" Then Check(server.RangeRequests > rangesBefore, "MR large hashed files enter the PCL segmented Range scheduler", passed)
                End Using
                Dim unusedLength As Long, unusedStatus As Integer
                server.HeadLengths.TryRemove("/good", unusedLength) : server.HeadStatusCodes.TryRemove("/good", unusedStatus) : server.IgnoreRanges = False
            Next
            Dim empty As New DownloadFile With {.RelativePath = "empty.txt", .Url = "https://cdn.modrinth.com/empty", .Category = "modrinth", .EstimatedSize = 42, .Sha1 = Hash(New Byte() {}), .Sha512 = Hash(New Byte() {}, True)}
            server.Files("/empty") = New Byte() {}
            Using engine As New DownloadEngine(policy, Nothing, {}, 1, Path.Combine(root, "mr-empty"))
                Await engine.DownloadAsync(Path.Combine(root, "mr-empty"), {empty}, CancellationToken.None)
                Check(SafeFiles.Matches(Path.Combine(root, "mr-empty", "empty.txt"), empty), "MR preserves explicitly hashed empty files without trusting advisory size", passed)
            End Using
            For i = 0 To 3
                server.Files("/redirect" & i) = New Byte() {} : server.Redirects("/redirect" & i) = If(i = 3, "/good", "/redirect" & (i + 1))
            Next
            item.Url = "https://cdn.modrinth.com/redirect0"
            Using engine As New DownloadEngine(policy, Nothing, {}, 2, Path.Combine(root, "mr-redirect"))
                Await engine.DownloadAsync(Path.Combine(root, "mr-redirect"), {item}, CancellationToken.None)
                Check(SafeFiles.Matches(SafeFiles.UnderRoot(Path.Combine(root, "mr-redirect"), item.RelativePath), item), "MR follows bounded multi-hop redirects with Range and integrity checks", passed)
            End Using
            For Each mode In {"cycle", "host", "sha512"}
                item.Url = "https://cdn.modrinth.com/redirect0"
                server.Redirects("/redirect0") = If(mode = "cycle", "/redirect0", If(mode = "host", "https://blocked.invalid/mod.jar", "/good"))
                If mode = "sha512" Then item.Sha512 = New String("0"c, 128)
                Dim location = Path.Combine(root, "mr-rejected-" & mode), rejected = False
                Using engine As New DownloadEngine(policy, Nothing, {}, 1, location)
                    Try
                        Await engine.DownloadAsync(location, {item}, CancellationToken.None)
                    Catch ex As Exception When TypeOf ex Is AggregateException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException
                        rejected = True
                    End Try
                End Using
                Check(rejected AndAlso Not File.Exists(SafeFiles.UnderRoot(location, item.RelativePath)), "MR rejects transfer before publication: " & mode, passed)
            Next
        End Using
    End Function
    Friend Sub Configure(fixture As InstallationChecks.InstallFixture)
        fixture.Fabric()
        fixture.Server.Files("/v2/versions/loader/1.20.1/0.16.9/profile/json") = fixture.Server.Files("/v2/versions/loader/1.20.1/fixture/profile/json")
        fixture.Server.Files("/v2/versions/loader/1.20.1") = Encoding.UTF8.GetBytes(New JArray(New JObject From {{"loader", New JObject From {{"version", "0.16.9"}, {"stable", True}}}}).ToString())
        Dim versions As New JArray()
        For i = 0 To 199
            versions.Add(New JObject From {{"id", If(i = 0, "1.20.1", "fixture-" & i)}, {"type", "release"}, {"url", fixture.Entry.Url}, {"sha1", fixture.Entry.Sha1}, {"releaseTime", "2023-01-01T00:00:00Z"}})
        Next
        fixture.Server.Files("/mc/game/version_manifest_v2.json") = Encoding.UTF8.GetBytes(New JObject From {{"versions", versions}}.ToString())
        For Each label In {"required", "optional", "unsupported"}
            fixture.Server.Files("/" & label) = Encoding.UTF8.GetBytes("fixture " & label & " content")
        Next
    End Sub
    Private Async Function InstallsAsync(root As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(root, "mr-layered-download")
            Configure(fixture)
            Dim source = Pack(root, "layered-download", Manifest(False), New Dictionary(Of String, String) From {{"Overrides/MODS/required.jar", "base replacement"}, {"CLIENT-OVERRIDES/mods/required.jar", "client replacement"}, {"client-overrides/config/other.txt", "second file"}})
            Dim result = Await New ModrinthInstaller(New Policy(fixture.Server.Url)).InstallAsync(ModrinthPackage.Read(source), fixture.Root, "layered", {}, {}, Nothing, CancellationToken.None)
            Check(File.ReadAllText(Path.Combine(result.InstancePath, "mods", "required.jar")) = "client replacement" AndAlso File.ReadAllText(Path.Combine(result.InstancePath, "config", "other.txt")) = "second file", "MR applies case-insensitive client layers over verified index content and copies multiple files", passed)
            Dim ledger = JObject.Parse(File.ReadAllText(Path.Combine(result.InstancePath, "PCL", "ModrinthInstall.json")))
            Check(CBool(ledger("files")(0)("overridden")) AndAlso PclFolders.ReadIniValue(Path.Combine(result.InstancePath, "PCL", "Setup.ini"), "VersionArgumentIndieV2") = "True", "MR records intentional index replacement while retaining isolation without a packaged INI", passed)
        End Using
        Using fixture As New InstallationChecks.InstallFixture(root, "mr-retry")
            Configure(fixture)
            Dim package = ModrinthPackage.Read(Pack(root, "install")), installer As New ModrinthInstaller(New Policy(fixture.Server.Url))
            Dim userShared = Path.Combine(fixture.Root, "mods", "user.jar"), settings = Path.Combine(fixture.Root, "PCL", "Setup.ini")
            Directory.CreateDirectory(Path.GetDirectoryName(userShared)) : File.WriteAllText(userShared, "user mod")
            Directory.CreateDirectory(Path.GetDirectoryName(settings)) : File.WriteAllText(settings, "LaunchArgumentIndieV2:0")
            Dim cancelled = False
            Using cancel As New CancellationTokenSource()
                Try
                    Await installer.InstallAsync(package, fixture.Root, "pack", {}, {"mods/optional.jar"}, New Progress(Sub(value)
                        If value.Stage = "完成安装" Then cancel.Cancel()
                    End Sub), cancel.Token, connections:=4)
                Catch ex As OperationCanceledException
                    cancelled = True
                End Try
            End Using
            Check(cancelled AndAlso Not Directory.Exists(Path.Combine(fixture.Root, "versions", "pack")), "MR cancellation before atomic publication exposes no instance", passed)
            Check(File.ReadAllText(userShared) = "user mod" AndAlso File.ReadAllText(settings) = "LaunchArgumentIndieV2:0", "MR cancellation preserves shared Mods and global isolation settings", passed)
            Dim result = Await installer.InstallAsync(package, fixture.Root, "pack", {}, {"mods/optional.jar"}, Nothing, CancellationToken.None, connections:=4)
            Check(result.NetworkBytes = 0, "MR retry reuses all verified game and pack payloads", passed)
            Check(File.ReadAllText(Path.Combine(result.InstancePath, "config", "fixture.txt")) = "client" AndAlso Not File.Exists(Path.Combine(result.InstancePath, "config", "server.txt")), "MR client overrides replace base and server overrides are not installed", passed)
            Check(File.Exists(Path.Combine(result.InstancePath, "mods", "required.jar")) AndAlso File.Exists(Path.Combine(result.InstancePath, "mods", "optional.jar")) AndAlso Not File.Exists(Path.Combine(result.InstancePath, "mods", "unsupported.jar")), "MR installs required and selected client files only", passed)
            Check(PclFolders.ReadIniValue(Path.Combine(result.InstancePath, "PCL", "Setup.ini"), "VersionArgumentIndieV2") = "True" AndAlso PclFolders.ReadIniValue(Path.Combine(result.InstancePath, "PCL", "Setup.ini"), "UserSetting") = "client", "MR forces instance isolation while preserving winning unrelated packaged preferences", passed)
            Dim ledger = JObject.Parse(File.ReadAllText(Path.Combine(result.InstancePath, "PCL", "ModrinthInstall.json")))
            Check(CStr(ledger("archiveSha256")) = package.Sha256 AndAlso DirectCast(ledger("files"), JArray).Count = 2 AndAlso CStr(JObject.Parse(File.ReadAllText(Path.Combine(result.InstancePath, "pack.json")))("mainClass")) = "fixture.FabricLoader", "MR records provenance and the exact loader profile with publication", passed)
            Dim rejected = False, requests = fixture.Server.Requests.Values.Sum()
            Try
                Await installer.InstallAsync(package, fixture.Root, "pack", {}, {}, Nothing, CancellationToken.None)
            Catch ex As IOException
                rejected = True
            End Try
            Check(rejected AndAlso requests = fixture.Server.Requests.Values.Sum(), "MR refuses existing instances before network requests", passed)
        End Using
        For Each issue In {"changed-archive", "reserved", "missing-loader", "corrupt", "late-file", "late-config", "late-instance", "unknown-optional"}
            Using fixture As New InstallationChecks.InstallFixture(root, "mr-error-" & issue)
                Configure(fixture)
                Dim value = Manifest(), entries As Dictionary(Of String, String) = Nothing
                If issue = "reserved" Then entries = New Dictionary(Of String, String) From {{"overrides/pack.json", "bad"}}
                If issue = "missing-loader" Then value("dependencies")("fabric-loader") = "0.99.0"
                Dim source = Pack(root, "install-" & issue, value, entries), package = ModrinthPackage.Read(source)
                If issue = "changed-archive" Then File.AppendAllText(source, "changed")
                If issue = "corrupt" Then fixture.Server.Files("/required") = Encoding.UTF8.GetBytes("invalid required bytes")
                Dim rejected = False
                Try
                    Await New ModrinthInstaller(New Policy(fixture.Server.Url)).InstallAsync(package, fixture.Root, "pack", {}, If(issue = "unknown-optional", {"mods/absent.jar"}, New String() {}), New Progress(Sub(progress)
                        If progress.Stage <> "完成安装" Then Return
                        If issue = "late-instance" Then
                            Directory.CreateDirectory(Path.Combine(fixture.Root, "versions", "pack")) : File.WriteAllText(Path.Combine(fixture.Root, "versions", "pack", "external.txt"), "external")
                        ElseIf issue = "late-file" OrElse issue = "late-config" Then
                            Dim stage = Directory.EnumerateDirectories(Path.Combine(fixture.Root, ".vanilla-downloader", "staging")).Single()
                            File.WriteAllText(Path.Combine(stage, "payload", If(issue = "late-file", "mods/required.jar", "config/fixture.txt")), "mutated")
                        End If
                    End Sub), CancellationToken.None, connections:=4)
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is ArgumentException
                    rejected = True
                End Try
                Check(rejected, "MR rejects incomplete/conflicting installation " & issue, passed)
                Check(If(issue = "late-instance", File.ReadAllText(Path.Combine(fixture.Root, "versions", "pack", "external.txt")) = "external", Not Directory.Exists(Path.Combine(fixture.Root, "versions", "pack"))), "MR preserves existing data and publishes no partial instance " & issue, passed)
                Dim staging = Path.Combine(fixture.Root, ".vanilla-downloader", "staging")
                Check(Not Directory.Exists(staging) OrElse Not Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Any(), "MR releases owned staging data " & issue, passed)
                Using held As New FileStream(source, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)
                    Check(held.CanWrite, "MR failure releases original archive " & issue, passed)
                End Using
            End Using
        Next
    End Function
End Module
