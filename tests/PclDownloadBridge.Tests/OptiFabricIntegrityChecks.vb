Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module OptiFabricIntegrityChecks
    Private ReadOnly Good As Byte() = ModJar("good")
    Private ReadOnly Wrong As Byte() = ModJar("evil")

    Friend Async Function RunAsync(Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "component-catalogue-integrity-metadata" Then Await MetadataAsync(passed)
        If onlyCase Is Nothing OrElse onlyCase = "component-catalogue-integrity-install" Then Await RejectionAsync(passed)
        If onlyCase Is Nothing OrElse onlyCase = "component-catalogue-integrity-cache" Then Await CacheAsync(passed)
        If onlyCase Is Nothing OrElse onlyCase = "component-catalogue-integrity-cancel" Then Await CancelAsync(passed)
        If passed.Count = 0 Then Throw New ArgumentException(onlyCase)
        Return passed
    End Function

    Private Async Function MetadataAsync(passed As List(Of String)) As Task
        Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(15))
            Dim md5 = Digest(Good, False), sha1 = Digest(Good, True)
            Dim row = ModRow(server.Url, New JArray(HashRow(2, md5.ToUpperInvariant())))
            Dim entry = Await LoadAsync(server, row, timeout.Token)
            Console.WriteLine("OPTIFABRIC_MD5 extracted=" & If(entry.Md5, "none"))
            Check(entry.Md5 = md5 AndAlso String.IsNullOrEmpty(entry.Sha1), "MD5-only OptiFabric metadata supplies a canonical checksum", passed)
            Check(CStr(entry.Data("hashes")(0)("value")) = md5.ToUpperInvariant(), "checksum normalization preserves the upstream metadata", passed)
            row("hashes") = New JArray(HashRow(2, New String("0"c, 32)), HashRow(1, sha1.ToUpperInvariant()))
            entry = Await LoadAsync(server, row, timeout.Token)
            Check(entry.Sha1 = sha1 AndAlso String.IsNullOrEmpty(entry.Md5), "SHA-1 takes precedence over a different declared MD5 regardless of order", passed)
            row("hashes") = New JArray(HashRow(2, JValue.CreateNull()), HashRow(1, sha1))
            entry = Await LoadAsync(server, row, timeout.Token)
            Check(entry.Sha1 = sha1 AndAlso String.IsNullOrEmpty(entry.Md5), "an unused MD5 value does not invalidate the preferred SHA-1", passed)
            For Each hashes As JToken In {CType(Nothing, JToken), JValue.CreateNull(), New JArray(), New JArray(HashRow(99, "unknown"))}
                If hashes Is Nothing Then
                    row.Remove("hashes")
                Else
                    row("hashes") = hashes
                End If
                entry = Await LoadAsync(server, row, timeout.Token)
                Check(String.IsNullOrEmpty(entry.Sha1) AndAlso String.IsNullOrEmpty(entry.Md5), "legacy metadata without a supported checksum remains usable: " & If(hashes?.ToString(), "missing"), passed)
            Next
        End Using
        For Each value As JToken In {New JValue("short"), New JValue(New String("z"c, 32)), JValue.CreateNull(), New JValue(123), New JValue(" " & Digest(Good, False))}
            For Each preference In {SourcePreference.Automatic, SourcePreference.OfficialFirst, SourcePreference.MirrorFirst}
                Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(8))
                    Dim preferred = If(preference = SourcePreference.MirrorFirst, "/mirror", "/official")
                    Dim alternate = If(preferred = "/mirror", "/official", "/mirror")
                    server.Files(preferred) = Page(ModRow(server.Url, New JArray(HashRow(2, value))))
                    server.Files(alternate) = Page(ModRow(server.Url, New JArray(HashRow(2, Digest(Good, False)))))
                    Dim entry = (Await New ComponentCatalog(New RoutedPolicy(server.Url, preference)).LoadAsync(ComponentKind.OptiFabric, "1.20.1", timeout.Token)).Single()
                    Check(entry.Md5 = Digest(Good, False) AndAlso server.Requests.ContainsKey(alternate) AndAlso server.Requests(preferred) = 1,
                          "invalid selected MD5 falls back before source acceptance: " & preference.ToString() & " " & value.ToString(), passed)
                End Using
            Next
        Next
        Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(8))
            server.Files("/mirror") = Page(ModRow(server.Url, New JArray(HashRow(1, "invalid"), HashRow(2, Digest(Good, False)))))
            server.Files("/official") = Page(ModRow(server.Url, New JArray(HashRow(2, Digest(Good, False)))))
            Dim entry = (Await New ComponentCatalog(New RoutedPolicy(server.Url, SourcePreference.MirrorFirst)).LoadAsync(ComponentKind.OptiFabric, "1.20.1", timeout.Token)).Single()
            Check(entry.Md5 = Digest(Good, False) AndAlso server.Requests.ContainsKey("/official"), "a malformed declared SHA-1 cannot silently downgrade to MD5 within the same source", passed)
            server.Files("/official") = server.Files("/mirror")
            Dim failure As AggregateException = Nothing
            Try
                Await New ComponentCatalog(New RoutedPolicy(server.Url, SourcePreference.MirrorFirst)).LoadAsync(ComponentKind.OptiFabric, "1.20.1", timeout.Token)
            Catch ex As AggregateException
                failure = ex
            End Try
            Check(failure IsNot Nothing AndAlso failure.InnerExceptions.Count = 2, "both invalid checksum sources retain aggregate failure diagnostics", passed)
        End Using
    End Function

    Private Async Function RejectionAsync(passed As List(Of String)) As Task
        Dim root = FixtureRoot()
        Using fixture As New InstallationChecks.InstallFixture(root, "reject")
            Check(Good.Length = Wrong.Length AndAlso Not Good.SequenceEqual(Wrong) AndAlso ReadModId(Wrong) = "optifabric", "wrong fixture is a different same-size readable OptiFabric JAR", passed)
            fixture.Server.Files("/mod") = Wrong
            Dim selection = PrepareSelection(fixture)
            ' Isolate transport propagation from catalogue extraction, which has its own checks.
            selection.Entries(ComponentKind.OptiFabric) = New ComponentEntry With {.Kind = ComponentKind.OptiFabric, .Minecraft = "1.20.1", .Version = "fixture", .FileName = "optifabric.jar", .Url = fixture.Server.Url & "/mod", .Md5 = Digest(Good, False), .Size = Good.Length}
            Dim failed As Boolean
            Try
                Await fixture.InstallAsync("retry", selection)
            Catch ex As IOException
                failed = True
            End Try
            Dim published = Path.Combine(fixture.Root, "versions", "retry")
            Console.WriteLine("OPTIFABRIC_MD5 rejected=" & failed.ToString() & " published=" & Directory.Exists(published).ToString())
            Check(failed AndAlso Not Directory.Exists(published), "declared MD5 rejects a same-size readable wrong JAR before instance publication", passed)
            Check(Not File.Exists(CachePath(fixture.Root, Good)), "rejected content never enters the verified MD5 component cache", passed)
            fixture.Server.Files("/mod") = Good
            Dim result = Await fixture.InstallAsync("retry", selection)
            Check(File.ReadAllBytes(Path.Combine(result.InstancePath, "mods", "optifabric.jar")).SequenceEqual(Good), "retrying the same instance name succeeds after valid content becomes available", passed)
        End Using
    End Function

    Private Async Function CacheAsync(passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(FixtureRoot(), "cache"), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(25))
            Dim selection = PrepareSelection(fixture)
            fixture.Server.Files("/mod") = Good
            Dim entry = Await LoadAsync(fixture.Server, ModRow(fixture.Server.Url, New JArray(HashRow(2, Digest(Good, False)))), timeout.Token)
            selection.Entries(ComponentKind.OptiFabric) = entry
            Dim stale = Path.Combine(fixture.Root, "mods", "optifabric.jar")
            Directory.CreateDirectory(Path.GetDirectoryName(stale)) : File.WriteAllBytes(stale, Wrong)
            Dim result = Await fixture.InstallAsync("first", selection, token:=timeout.Token)
            Dim installed = Path.Combine(result.InstancePath, "mods", "optifabric.jar")
            Check(File.ReadAllBytes(installed).SequenceEqual(Good) AndAlso fixture.Server.Requests("/mod") = 1, "catalogue MD5 flows through actual installation and rejects a stale same-size reusable mod", passed)
            Check(File.ReadAllBytes(stale).SequenceEqual(Wrong), "failed reuse leaves the source file untouched", passed)
            Dim cached = CachePath(fixture.Root, Good)
            Check(File.Exists(cached) AndAlso File.ReadAllBytes(cached).SequenceEqual(Good), "actual installation saves the JAR in the private MD5-addressed component cache", passed)
            entry.Url = fixture.Server.Url & "/renamed-mod"
            fixture.Server.Files("/renamed-mod") = Wrong
            Dim repeated = Await fixture.InstallAsync("second", selection, token:=timeout.Token)
            Check(File.ReadAllBytes(Path.Combine(repeated.InstancePath, "mods", "optifabric.jar")).SequenceEqual(Good) AndAlso Not fixture.Server.Requests.ContainsKey("/renamed-mod"), "a changed URL reuses verified MD5 content without a new component request", passed)
            File.WriteAllBytes(cached, Wrong)
            fixture.Server.Files("/renamed-mod") = Good
            Dim repaired = Await fixture.InstallAsync("third", selection, token:=timeout.Token)
            Check(fixture.Server.Requests("/renamed-mod") = 1 AndAlso File.ReadAllBytes(Path.Combine(repaired.InstancePath, "mods", "optifabric.jar")).SequenceEqual(Good), "a readable same-size corrupt MD5 cache is rejected and downloaded again", passed)
            Check(File.ReadAllBytes(cached).SequenceEqual(Good) AndAlso File.ReadAllBytes(installed).SequenceEqual(Good), "cache repair preserves an already published instance", passed)
            Dim updated = ModJar("next")
            entry.Md5 = Digest(updated, False)
            fixture.Server.Files("/changed-mod") = updated : entry.Url = fixture.Server.Url & "/changed-mod"
            Dim changed = Await fixture.InstallAsync("fourth", selection, token:=timeout.Token)
            Check(fixture.Server.Requests("/changed-mod") = 1 AndAlso File.ReadAllBytes(Path.Combine(changed.InstancePath, "mods", "optifabric.jar")).SequenceEqual(updated), "a new declared MD5 uses its own content rather than an old same-name cache", passed)
            Check(File.ReadAllBytes(cached).SequenceEqual(Good) AndAlso File.ReadAllBytes(CachePath(fixture.Root, updated)).SequenceEqual(updated), "different MD5 versions coexist without overwriting the previous verified content", passed)
        End Using
    End Function

    Private Async Function CancelAsync(passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(FixtureRoot(), "cancel"), gate As New ManualResetEventSlim(), cancel As New CancellationTokenSource(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(15))
            Dim selection = PrepareSelection(fixture)
            fixture.Server.Files("/mod") = Good : fixture.Server.BodyGates("/mod") = gate
            selection.Entries(ComponentKind.OptiFabric) = Await LoadAsync(fixture.Server, ModRow(fixture.Server.Url, New JArray(HashRow(2, Digest(Good, False)))), timeout.Token)
            Dim install = fixture.InstallAsync("cancel-retry", selection, token:=cancel.Token)
            While Not fixture.Server.Requests.ContainsKey("/mod") AndAlso Not install.IsCompleted
                Await Task.Delay(10, timeout.Token)
            End While
            Check(fixture.Server.Requests.ContainsKey("/mod"), "cancellation sample reaches the actual MD5 component response before cancelling", passed)
            cancel.Cancel()
            Dim cancelled As Boolean
            Try
                Await install
            Catch ex As OperationCanceledException
                cancelled = True
            Finally
                gate.Set()
            End Try
            Check(cancelled AndAlso Not Directory.Exists(Path.Combine(fixture.Root, "versions", "cancel-retry")) AndAlso Not File.Exists(CachePath(fixture.Root, Good)), "cancelling an incomplete MD5 component publishes neither an instance nor verified cache", passed)
            Dim result = Await fixture.InstallAsync("cancel-retry", selection, token:=timeout.Token)
            Check(File.ReadAllBytes(Path.Combine(result.InstancePath, "mods", "optifabric.jar")).SequenceEqual(Good) AndAlso File.ReadAllBytes(CachePath(fixture.Root, Good)).SequenceEqual(Good), "retry after cancellation completes with verified MD5 content", passed)
        End Using
    End Function

    Private Function PrepareSelection(fixture As InstallationChecks.InstallFixture) As ComponentSelection
        Dim selection = fixture.Fabric()
        fixture.Server.Files("/optifine") = OptiFinePayloadChecks.Jar(64 * 1024)
        selection.Entries(ComponentKind.OptiFine) = New ComponentEntry With {.Kind = ComponentKind.OptiFine, .Minecraft = "1.20.1", .Version = "fixture", .FileName = "optifine.jar", .Url = fixture.Server.Url & "/optifine"}
        Return selection
    End Function
    Private Async Function LoadAsync(server As TestServer, row As JObject, token As CancellationToken) As Task(Of ComponentEntry)
        server.Files("/mirror") = Page(row) : server.Files("/official") = Page(row)
        Return (Await New ComponentCatalog(New RoutedPolicy(server.Url, SourcePreference.MirrorFirst)).LoadAsync(ComponentKind.OptiFabric, "1.20.1", token)).Single()
    End Function
    Private Function ModRow(server As String, hashes As JArray) As JObject
        Return New JObject From {{"id", 5025647}, {"projectId", 322385}, {"gameVersions", New JArray("1.20.1")}, {"fileName", "optifabric.jar"}, {"displayName", "OptiFabric fixture"}, {"fileLength", Good.Length}, {"releaseType", 2}, {"downloadUrl", server & "/mod"}, {"hashes", hashes}}
    End Function
    Private Function HashRow(algorithm As Integer, value As JToken) As JObject
        Return New JObject From {{"algo", algorithm}, {"value", value.DeepClone()}}
    End Function
    Private Function Page(row As JObject) As Byte()
        Return Encoding.UTF8.GetBytes(New JObject From {{"data", New JArray(row)}}.ToString())
    End Function
    Private Function ModJar(marker As String) As Byte()
        Using buffer As New MemoryStream()
            Using archive As New ZipArchive(buffer, ZipArchiveMode.Create, True)
                Dim entry = archive.CreateEntry("fabric.mod.json", CompressionLevel.NoCompression)
                entry.LastWriteTime = New DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)
                Using output = entry.Open()
                    Dim bytes = Encoding.UTF8.GetBytes("{""schemaVersion"":1,""id"":""optifabric"",""version"":""fixture"",""description"":""" & marker & """}")
                    output.Write(bytes, 0, bytes.Length)
                End Using
            End Using
            Return buffer.ToArray()
        End Using
    End Function
    Private Function ReadModId(bytes As Byte()) As String
        Using buffer As New MemoryStream(bytes), archive As New ZipArchive(buffer, ZipArchiveMode.Read), reader As New StreamReader(archive.GetEntry("fabric.mod.json").Open())
            Return CStr(JObject.Parse(reader.ReadToEnd())("id"))
        End Using
    End Function
    Private Function Digest(bytes As Byte(), sha1 As Boolean) As String
        Using algorithm As Security.Cryptography.HashAlgorithm = If(sha1, CType(Security.Cryptography.SHA1.Create(), Security.Cryptography.HashAlgorithm), Security.Cryptography.MD5.Create())
            Return BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function
    Private Function CachePath(root As String, bytes As Byte()) As String
        Dim md5 = Digest(bytes, False)
        Return SafeFiles.UnderRoot(root, ".vanilla-downloader/component-cache/md5/" & md5.Substring(0, 2) & "/" & md5 & ".bin")
    End Function
    Private Function FixtureRoot() As String
        Return Path.Combine(Path.GetTempPath(), "PclDbOptiHash-" & Guid.NewGuid().ToString("N"))
    End Function
    Private Sub Check(condition As Boolean, label As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub
    Private Class RoutedPolicy
        Inherits SourcePolicy
        Private ReadOnly _server As String
        Friend Sub New(server As String, preference As SourcePreference)
            _server = server : AllowLoopback = True : VersionPreference = preference
        End Sub
        Protected Overrides Function MetadataUrl(source As String) As String
            Return _server & If(New Uri(source).Host.Contains("mcimirror"), "/mirror", "/official")
        End Function
    End Class
End Module
