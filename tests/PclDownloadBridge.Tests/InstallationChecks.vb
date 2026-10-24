Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module InstallationChecks
    Friend Async Function RunAsync(testRoot As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "install-repair" Then Await RepairAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-library-scope" Then Await LibraryScopeAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-library-in-use" Then Await LibraryInUseAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-library-locked" Then Await LockedLibraryAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-publish" Then Await PublishAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase.StartsWith("install-repair-metadata-", StringComparison.Ordinal) Then passed.AddRange(Await RepairMetadataChecks.RunAsync(testRoot, onlyCase))
        If onlyCase Is Nothing OrElse onlyCase.StartsWith("install-isolation-", StringComparison.Ordinal) Then passed.AddRange(Await IsolationResourceChecks.RunAsync(testRoot, onlyCase))
        If onlyCase Is Nothing OrElse onlyCase.StartsWith("install-mode-", StringComparison.Ordinal) Then passed.AddRange(Await IsolationModeChecks.RunAsync(testRoot, onlyCase))
        If onlyCase Is Nothing OrElse onlyCase.StartsWith("install-boundary-", StringComparison.Ordinal) Then passed.AddRange(Await IsolationBoundaryChecks.RunAsync(testRoot, onlyCase))
        If onlyCase Is Nothing OrElse onlyCase.StartsWith("install-input-", StringComparison.Ordinal) Then passed.AddRange(Await InstallInputChecks.RunAsync(testRoot, onlyCase))
        If onlyCase Is Nothing OrElse onlyCase.StartsWith("install-runtime-", StringComparison.Ordinal) Then passed.AddRange(Await RuntimeLibraryChecks.RunAsync(testRoot, onlyCase))
        If passed.Count = 0 Then Throw New ArgumentException("Unknown installation regression: " & onlyCase)
        Return passed
    End Function

    Private Sub Assert(condition As Boolean, label As String, passed As List(Of String))
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

    Private Function Artifact(url As String, bytes As Byte(), Optional relative As String = Nothing) As JObject
        Dim result As New JObject From {{"url", url}, {"size", bytes.Length}, {"sha1", Hash(bytes)}}
        If relative IsNot Nothing Then result("path") = relative
        Return result
    End Function

    Private Async Function RepairAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New InstallFixture(testRoot, "repair")
            Dim result = Await fixture.InstallAsync("existing")
            Dim jsonPath = Path.Combine(result.InstancePath, "existing.json")
            Dim original = File.ReadAllBytes(jsonPath)
            Dim changes As New Dictionary(Of String, Action(Of JObject)) From {
                {"asset index", Sub(json) json("assetIndex")("id") = "other-index"},
                {"logging file", Sub(json) json("logging")("client")("file")("id") = "other-log.xml"},
                {"legacy assets name", Sub(json) json("assets") = "other-assets"},
                {"PCL client version", Sub(json) json("clientVersion") = "other-minecraft"}}
            Dim beforeRequests = fixture.NonVersionRequests()
            For Each change In changes
                Dim json = JObject.Parse(Encoding.UTF8.GetString(original))
                change.Value.Invoke(json)
                Dim before = Utf8(json.ToString())
                File.WriteAllBytes(jsonPath, before)
                Dim rejected = False
                Try
                    Await fixture.InstallAsync("existing", repair:=True)
                Catch ex As InvalidDataException
                    rejected = True
                End Try
                Assert(rejected AndAlso before.SequenceEqual(File.ReadAllBytes(jsonPath)), "repair refuses a changed " & change.Key & " and preserves existing JSON bytes", passed)
            Next
            Assert(fixture.NonVersionRequests() = beforeRequests, "incompatible repair fails before index or content requests", passed)
            File.WriteAllBytes(jsonPath, original)
            Dim repaired = Await fixture.InstallAsync("existing", repair:=True)
            Assert(repaired.NetworkBytes = 0 AndAlso original.SequenceEqual(File.ReadAllBytes(jsonPath)), "compatible repair still reuses content and keeps the original JSON bytes", passed)
            Dim legacy = JObject.Parse(Encoding.UTF8.GetString(original))
            legacy.Remove("clientVersion")
            Dim legacyBytes = Utf8(legacy.ToString())
            File.WriteAllBytes(jsonPath, legacyBytes)
            Await fixture.InstallAsync("existing", repair:=True)
            Assert(legacyBytes.SequenceEqual(File.ReadAllBytes(jsonPath)), "compatible vanilla metadata without a PCL clientVersion is still repairable", passed)
        End Using
    End Function

    Private Async Function LibraryScopeAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New InstallFixture(testRoot, "library-scope", "1.12.2")
            Dim unrelated = SafeFiles.UnderRoot(fixture.Root, "libraries/unrelated/kept/1/kept-1.jar")
            Directory.CreateDirectory(Path.GetDirectoryName(unrelated))
            Dim original = Utf8("existing unrelated library")
            File.WriteAllBytes(unrelated, original)
            Dim runtime = Utf8("fixture legacy universal")
            Dim version As New JObject From {{"mainClass", "fixture.LegacyLoader"}, {"inheritsFrom", fixture.Entry.Id},
                {"libraries", New JArray(New JObject From {{"name", "fixture:legacy:1"}, {"downloads", New JObject From {{"artifact", Artifact("", runtime, "fixture/legacy/1/legacy-1.jar")}}}})}}
            Dim profile As New JObject From {{"minecraft", fixture.Entry.Id}, {"versionInfo", version},
                {"install", New JObject From {{"filePath", "universal.jar"}, {"path", "fixture:legacy:1"}}}}
            Using buffer As New MemoryStream()
                Using zip As New ZipArchive(buffer, ZipArchiveMode.Create, True)
                    WriteEntry(zip, "install_profile.json", Utf8(profile.ToString()))
                    WriteEntry(zip, "universal.jar", runtime)
                    WriteEntry(zip, "maven/unrelated/kept/1/kept-1.jar", Utf8("unreferenced installer copy"))
                    WriteEntry(zip, "maven/fixture/processor/1/processor-1.jar", Utf8("private install-only dependency"))
                End Using
                fixture.Server.Files("/legacy-installer") = buffer.ToArray()
            End Using
            fixture.Server.Files("/legacy-installer.sha1") = Utf8(Hash(fixture.Server.Files("/legacy-installer")))
            Dim selection As New ComponentSelection()
            selection.Entries(ComponentKind.Forge) = New ComponentEntry With {.Kind = ComponentKind.Forge, .Minecraft = fixture.Entry.Id, .Version = "14.23.5.2859", .Url = fixture.Server.Url & "/legacy-installer"}
            Dim result = Await fixture.InstallAsync("scoped-loader", selection)
            Assert(original.SequenceEqual(File.ReadAllBytes(unrelated)), "component installation cannot overwrite an unrelated shared library with an unreferenced embedded file", passed)
            Assert(Not File.Exists(SafeFiles.UnderRoot(fixture.Root, "libraries/fixture/processor/1/processor-1.jar")), "install-only libraries stay out of the shared runtime store", passed)
            Assert(File.ReadAllBytes(SafeFiles.UnderRoot(fixture.Root, "libraries/fixture/legacy/1/legacy-1.jar")).SequenceEqual(runtime) AndAlso File.Exists(Path.Combine(result.InstancePath, "scoped-loader.json")), "referenced legacy Forge runtime and instance still publish successfully", passed)
        End Using
    End Function

    Private Async Function LibraryInUseAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New InstallFixture(testRoot, "library-in-use")
            Dim selection = fixture.Fabric()
            Dim library = fixture.SharedPath()
            Directory.CreateDirectory(Path.GetDirectoryName(library))
            Dim expected = fixture.Server.Files("/shared")
            File.WriteAllBytes(library, expected)
            Using held As New FileStream(library, FileMode.Open, FileAccess.Read, FileShare.Read)
                Dim result = Await fixture.InstallAsync("while-library-open", selection)
                Assert(File.Exists(Path.Combine(result.InstancePath, "while-library-open.json")), "a verified runtime library open for reading does not block a new component instance", passed)
                Assert(expected.SequenceEqual(File.ReadAllBytes(library)), "an already matching shared runtime is reused without replacement", passed)
            End Using
        End Using
    End Function

    Private Async Function LockedLibraryAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New InstallFixture(testRoot, "library-locked")
            Dim selection = fixture.Fabric()
            Dim library = fixture.SharedPath()
            Directory.CreateDirectory(Path.GetDirectoryName(library))
            Dim original = Utf8("damaged library held by another process")
            File.WriteAllBytes(library, original)
            Dim failed = False
            Using held As New FileStream(library, FileMode.Open, FileAccess.Read, FileShare.Read)
                Try
                    Await fixture.InstallAsync("retry-after-unlock", selection)
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                    failed = True
                End Try
                Assert(failed AndAlso original.SequenceEqual(File.ReadAllBytes(library)) AndAlso Not Directory.Exists(Path.Combine(fixture.Root, "versions", "retry-after-unlock")), "a locked mismatching library fails without replacing bytes or publishing the instance", passed)
                Assert(Not Directory.EnumerateFiles(Path.Combine(fixture.Root, "libraries"), "*.component-*", SearchOption.AllDirectories).Any(), "failed shared-library publication removes its own temporary copy", passed)
            End Using
            Dim result = Await fixture.InstallAsync("retry-after-unlock", selection)
            Assert(fixture.Server.Files("/shared").SequenceEqual(File.ReadAllBytes(library)) AndAlso File.Exists(Path.Combine(result.InstancePath, "retry-after-unlock.json")), "component installation succeeds after the mismatching library is unlocked", passed)
        End Using
    End Function

    Private Async Function PublishAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New InstallFixture(testRoot, "publish")
            Dim instance = Path.Combine(fixture.Root, "versions", "collision")
            Dim sentinel = Path.Combine(instance, "keep-user-file.txt")
            Dim collided = False
            Dim progress As New CallbackProgress(Sub(value)
                                                     If value.Phase <> "commit" Then Return
                                                     Directory.CreateDirectory(instance)
                                                     File.WriteAllText(sentinel, "preserve")
                                                     collided = True
                                                 End Sub)
            Dim failed = False
            Try
                Await fixture.InstallAsync("collision", progress:=progress)
            Catch ex As IOException
                failed = True
            End Try
            Assert(collided AndAlso failed AndAlso File.ReadAllText(sentinel) = "preserve" AndAlso Not File.Exists(Path.Combine(instance, "collision.json")), "a directory created at final commit is preserved without publishing over it", passed)
            Dim cache = Path.Combine(fixture.Root, ".vanilla-downloader")
            Assert(Not Directory.EnumerateFiles(cache, "collision.*", SearchOption.AllDirectories).Any(), "failed instance publication leaves no orphan client or JSON in private staging/publish folders", passed)
            File.Delete(sentinel)
            Directory.Delete(instance)
            Dim result = Await fixture.InstallAsync("collision")
            Assert(File.Exists(Path.Combine(result.InstancePath, "collision.json")) AndAlso File.Exists(Path.Combine(result.InstancePath, "collision.jar")), "retry after a publication collision commits a complete instance", passed)
        End Using
    End Function

    Private Sub WriteEntry(zip As ZipArchive, name As String, bytes As Byte())
        Using output = zip.CreateEntry(name).Open()
            output.Write(bytes, 0, bytes.Length)
        End Using
    End Sub

    Private Class CallbackProgress
        Implements IProgress(Of TransferProgress)
        Private ReadOnly _action As Action(Of TransferProgress)
        Friend Sub New(action As Action(Of TransferProgress))
            _action = action
        End Sub
        Public Sub Report(value As TransferProgress) Implements IProgress(Of TransferProgress).Report
            _action(value)
        End Sub
    End Class

    Friend Class InstallFixture
        Implements IDisposable
        Friend ReadOnly Server As New TestServer()
        Friend ReadOnly Root As String
        Friend ReadOnly Entry As VersionEntry
        Private ReadOnly _installer As MinecraftInstaller
        Friend Sub New(testRoot As String, name As String, Optional mc As String = "1.20.1")
            Root = Path.Combine(testRoot, "install-" & name, ".minecraft")
            Dim client = Utf8("fixture vanilla client")
            Dim index = Utf8("{""objects"":{}}")
            Dim logging = Utf8("fixture logging")
            Server.Files("/client") = client : Server.Files("/index") = index : Server.Files("/log") = logging
            Dim indexInfo = Artifact(Server.Url & "/index", index)
            indexInfo("id") = "fixture"
            Dim logInfo = Artifact(Server.Url & "/log", logging)
            logInfo("id") = "client.xml"
            Dim metadata As New JObject From {{"id", mc}, {"type", "release"}, {"mainClass", "vanilla.Main"},
                {"downloads", New JObject From {{"client", Artifact(Server.Url & "/client", client)}}},
                {"libraries", New JArray()}, {"assets", "fixture"}, {"assetIndex", indexInfo},
                {"logging", New JObject From {{"client", New JObject From {{"file", logInfo}}}}}}
            Server.Files("/version") = Utf8(metadata.ToString())
            Entry = New VersionEntry With {.Id = mc, .Url = Server.Url & "/version", .Sha1 = Hash(Server.Files("/version"))}
            _installer = New MinecraftInstaller(New FixturePolicy(Server.Url))
        End Sub
        Friend Function InstallAsync(name As String, Optional selection As ComponentSelection = Nothing, Optional repair As Boolean = False, Optional progress As IProgress(Of TransferProgress) = Nothing, Optional token As CancellationToken = Nothing, Optional directoryOnly As Boolean = False) As Task(Of InstallResult)
            Dim installer = If(directoryOnly, New MinecraftInstaller(New FixturePolicy(Server.Url), directoryOnly:=True), _installer)
            Return installer.InstallAsync(Entry, Root, name, {}, progress, token, repair, components:=selection)
        End Function
        Friend Function NonVersionRequests() As Integer
            Return Server.Requests.Where(Function(p) p.Key <> "/version").Sum(Function(p) p.Value)
        End Function
        Friend Function SharedPath() As String
            Return SafeFiles.UnderRoot(Root, "libraries/fixture/shared/1/shared-1.jar")
        End Function
        Friend Function Fabric() As ComponentSelection
            Dim library = Utf8("fixture shared runtime")
            Server.Files("/shared") = library
            Dim profile As New JObject From {{"inheritsFrom", Entry.Id}, {"mainClass", "fixture.FabricLoader"},
                {"libraries", New JArray(New JObject From {{"name", "fixture:shared:1"}, {"downloads", New JObject From {{"artifact", Artifact(Server.Url & "/shared", library, "fixture/shared/1/shared-1.jar")}}}})}}
            Server.Files("/v2/versions/loader/" & Entry.Id & "/fixture/profile/json") = Utf8(profile.ToString())
            Dim selection As New ComponentSelection()
            selection.Entries(ComponentKind.Fabric) = New ComponentEntry With {.Kind = ComponentKind.Fabric, .Minecraft = Entry.Id, .Version = "fixture"}
            Return selection
        End Function
        Public Sub Dispose() Implements IDisposable.Dispose
            Server.Dispose()
        End Sub
    End Class

    Private Class FixturePolicy
        Inherits SourcePolicy
        Private ReadOnly _server As String
        Friend Sub New(server As String)
            _server = server : AllowLoopback = True
        End Sub
        Public Overrides Function Candidates(url As String, category As String) As List(Of String)
            Return New List(Of String) From {_server & New Uri(url).AbsolutePath}
        End Function
    End Class
End Module
