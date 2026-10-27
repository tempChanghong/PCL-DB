Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module FabricApiCatalogueChecks
    Private Const Endpoint As String = "/v2/project/P7dR8mSH/version"
    Private ReadOnly Bytes As Byte() = Encoding.UTF8.GetBytes("fixture API content")

    Friend Async Function RunAsync(Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "component-catalogue-api-filter" Then Await FilterAsync(passed)
        If onlyCase Is Nothing OrElse onlyCase = "component-catalogue-api-files" Then Await FilesAsync(passed)
        If onlyCase Is Nothing OrElse onlyCase = "component-catalogue-api-invalid" Then Await InvalidAsync(passed)
        If onlyCase Is Nothing OrElse onlyCase = "component-catalogue-api-behavior" Then Await BehaviorAsync(passed)
        If onlyCase Is Nothing OrElse onlyCase = "component-catalogue-api-install" Then Await InstallAsync(passed)
        If passed.Count = 0 Then Throw New ArgumentException(onlyCase)
        Return passed
    End Function

    Friend Function ApiFile(Optional filename As String = "fabric-api.jar", Optional primary As Boolean? = Nothing, Optional content As Byte() = Nothing) As JObject
        Dim payload = If(content, Bytes)
        Dim result As New JObject From {{"url", "https://example.invalid/" & filename}, {"filename", filename},
            {"hashes", New JObject From {{"sha1", Hash(payload)}}}, {"size", payload.Length}}
        If primary.HasValue Then result("primary") = primary.Value
        Return result
    End Function
    Friend Function Row(Optional version As String = "matching") As JObject
        Return New JObject From {{"name", "Fabric API " & version}, {"version_number", version}, {"version_type", "release"},
            {"project_id", "P7dR8mSH"}, {"game_versions", New JArray("1.20.1")}, {"loaders", New JArray("fabric")},
            {"date_published", "2026-01-01T00:00:00Z"}, {"files", New JArray(ApiFile(primary:=True))}}
    End Function
    Friend Async Function LoadAsync(server As TestServer, rows As JToken, Optional mc As String = "1.20.1") As Task(Of List(Of ComponentEntry))
        server.Files(Endpoint) = Utf8(rows.ToString(Newtonsoft.Json.Formatting.None))
        Using timeout As New CancellationTokenSource(TimeSpan.FromSeconds(8))
            Return Await New ComponentCatalog(New FixturePolicy(server.Url)).LoadAsync(ComponentKind.FabricApi, mc, timeout.Token)
        End Using
    End Function

    Private Async Function FilterAsync(passed As List(Of String)) As Task
        Using server As New TestServer()
            For Each mismatch In {"game", "loader"}
                Dim wrong = Row("wrong-" & mismatch)
                If mismatch = "game" Then wrong("game_versions") = New JArray("1.21.1") Else wrong("loaders") = New JArray("forge")
                Dim entries = Await LoadAsync(server, New JArray(wrong, Row()))
                Console.WriteLine("FABRIC_API_FILTER mismatch=" & mismatch & " versions=" & String.Join(",", entries.Select(Function(entry) entry.Version)))
                Check(entries.Count = 1 AndAlso entries(0).Version = "matching", "Fabric API verifies the returned " & mismatch & " instead of trusting query filtering", passed)
            Next
            Dim multi = Row()
            multi("game_versions") = New JArray("1.19.4", "1.20.1") : multi("loaders") = New JArray("quilt", "fabric")
            Dim accepted = Await LoadAsync(server, New JArray(multi))
            Check(accepted.Count = 1 AndAlso accepted(0).Minecraft = "1.20.1", "Fabric API accepts an explicit match within multiple games and loaders", passed)
            Dim old = Row("old"), latest = Row("latest")
            old("date_published") = "2026-10-20T09:00:00+08:00" : latest("date_published") = "2026-10-20T02:00:00Z"
            latest("version_type") = "beta"
            accepted = Await LoadAsync(server, New JArray(old, latest))
            Check(accepted.Select(Function(entry) entry.Version).SequenceEqual({"latest", "old"}) AndAlso Not accepted(0).Stable,
                  "Fabric API sorts actual publication instants newest first while retaining beta status", passed)
        End Using
    End Function

    Private Async Function FilesAsync(passed As List(Of String)) As Task
        Using server As New TestServer()
            For Each attachment In {"fabric-api-sources.jar", "fabric-api-javadoc.jar", "readme.txt"}
                Dim rowData = Row()
                rowData("files") = New JArray(ApiFile(attachment), ApiFile("runtime.jar"))
                Dim entry = (Await LoadAsync(server, New JArray(rowData))).Single()
                Check(entry.FileName = "runtime.jar", "Fabric API fallback skips a non-runtime attachment: " & attachment, passed)
            Next
            Dim primary = Row()
            primary("files") = New JArray(ApiFile("first.jar", False), ApiFile("preferred.jar", True))
            Dim selected = (Await LoadAsync(server, New JArray(primary))).Single()
            Check(selected.FileName = "preferred.jar", "Fabric API preserves an explicit primary runtime JAR over the first JAR", passed)
            primary("files") = New JArray(ApiFile("UPPER.JAR"))
            selected = (Await LoadAsync(server, New JArray(primary))).Single()
            Check(selected.FileName = "UPPER.JAR", "Fabric API runtime extension matching is case insensitive", passed)
        End Using
    End Function

    Private Async Function InvalidAsync(passed As List(Of String)) As Task
        Dim invalid As New List(Of JToken) From {New JObject(), JValue.CreateNull(), New JArray(JValue.CreateNull())}
        For Each field In {"project_id", "game_versions", "loaders", "name", "version_number", "date_published", "files"}
            Dim item = Row()
            Select Case field
                Case "project_id" : item(field) = "other-project"
                Case "game_versions", "loaders" : item(field) = "wrong shape"
                Case "name", "version_number" : item(field) = JValue.CreateNull()
                Case "date_published" : item(field) = "not a date"
                Case "files" : item(field) = New JObject()
            End Select
            invalid.Add(New JArray(item))
        Next
        For Each field In {"url", "filename", "size", "sha1", "primary"}
            Dim item = Row(), file = DirectCast(item("files")(0), JObject)
            Select Case field
                Case "url" : file(field) = "relative/file.jar"
                Case "filename" : file(field) = "../outside.jar"
                Case "size" : file(field) = -3
                Case "sha1" : file("hashes")(field) = "not-a-hash"
                Case "primary" : file(field) = "true"
            End Select
            invalid.Add(New JArray(item))
        Next
        Dim ambiguous = Row() : ambiguous("files") = New JArray(ApiFile("a.jar", True), ApiFile("b.jar", True))
        invalid.Add(New JArray(ambiguous))
        Dim wrongPrimary = Row() : wrongPrimary("files") = New JArray(ApiFile("readme.txt", True), ApiFile("runtime.jar"))
        invalid.Add(New JArray(wrongPrimary))
        For i = 0 To invalid.Count - 1
            Using server As New TestServer()
                Dim failure As AggregateException = Nothing
                Try : Await LoadAsync(server, invalid(i)) : Catch ex As AggregateException : failure = ex : End Try
                Console.WriteLine("FABRIC_API_INVALID case=" & i & " rejected=" & (failure IsNot Nothing).ToString())
                Check(failure IsNot Nothing AndAlso server.Requests.Count = 1,
                      "malformed Fabric API metadata is rejected before becoming an installable option; case=" & i, passed)
            End Using
        Next
        Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(8))
            server.Files(Endpoint) = Utf8(New JArray(Row()).ToString(Newtonsoft.Json.Formatting.None) & "{}")
            Dim failure As AggregateException = Nothing
            Try
                Await New ComponentCatalog(New FixturePolicy(server.Url)).LoadAsync(ComponentKind.FabricApi, "1.20.1", timeout.Token)
            Catch ex As AggregateException
                failure = ex
            End Try
            Check(failure IsNot Nothing, "Fabric API parser rejects extra JSON after the root array", passed)
        End Using
    End Function

    Private Async Function BehaviorAsync(passed As List(Of String)) As Task
        Using server As New TestServer()
            Check((Await LoadAsync(server, New JArray())).Count = 0, "Fabric API preserves a legitimate empty version response", passed)
            Dim empty = Row() : empty("files") = New JArray()
            Check((Await LoadAsync(server, New JArray(empty))).Count = 0, "Fabric API omits versions without downloadable files as PCL does", passed)
            empty("files") = New JArray(ApiFile("fabric-api-sources.jar"))
            Check((Await LoadAsync(server, New JArray(empty))).Count = 0, "Fabric API omits a version containing only source attachments", passed)
            Dim irrelevant = Row() : irrelevant("game_versions") = New JArray("1.21.1") : irrelevant("files") = New JObject()
            Check((Await LoadAsync(server, New JArray(irrelevant, Row()))).Count = 1,
                  "Fabric API does not consume file metadata belonging to another game", passed)
            Dim optionalFields = Row()
            optionalFields("files")(0)("hashes")("sha1") = Hash(Bytes).ToUpperInvariant()
            Dim entry = (Await LoadAsync(server, New JArray(optionalFields))).Single()
            Check(entry.Sha1 = Hash(Bytes).ToUpperInvariant() AndAlso entry.Data("date_published").Type = JTokenType.String,
                  "Fabric API retains valid uppercase digests and original publication text", passed)
            DirectCast(optionalFields("files")(0)("hashes"), JObject).Remove("sha1")
            optionalFields("files")(0)("primary") = JValue.CreateNull()
            entry = (Await LoadAsync(server, New JArray(optionalFields))).Single()
            Check(entry.Sha1 Is Nothing AndAlso entry.FileName = "fabric-api.jar",
                  "Fabric API preserves optional SHA-1 and primary metadata without rejecting a sole runtime JAR", passed)
        End Using
        Using server As New TestServer(), gate As New ManualResetEventSlim(), cancel As New CancellationTokenSource()
            server.Files(Endpoint) = Utf8(New JArray(Row()).ToString()) : server.BodyGates(Endpoint) = gate
            Dim work = New ComponentCatalog(New FixturePolicy(server.Url)).LoadAsync(ComponentKind.FabricApi, "1.20.1", cancel.Token)
            cancel.CancelAfter(TimeSpan.FromMilliseconds(200))
            Dim cancelled = False
            Try : Await work : Catch ex As OperationCanceledException : cancelled = True : End Try
            Check(cancelled AndAlso Not gate.IsSet, "Fabric API catalogue cancellation joins a stalled response", passed)
        End Using
    End Function

    Private Async Function InstallAsync(passed As List(Of String)) As Task
        Dim root = Path.Combine(Path.GetTempPath(), "PclDbApiFixture-" & Guid.NewGuid().ToString("N"))
        Using fixture As New InstallationChecks.InstallFixture(root, "file-selection")
            Dim jar As Byte()
            Using buffer As New MemoryStream()
                Using archive As New ZipArchive(buffer, ZipArchiveMode.Create, True), output = archive.CreateEntry("fabric.mod.json").Open()
                    Dim metadata = Utf8("{""schemaVersion"":1,""id"":""fabric-api"",""version"":""fixture""}")
                    output.Write(metadata, 0, metadata.Length)
                End Using
                jar = buffer.ToArray()
            End Using
            fixture.Server.Files("/runtime.jar") = jar : fixture.Server.Files("/sources.jar") = Bytes : fixture.Server.Files("/wrong.jar") = Bytes
            Dim wrong = Row("wrong-game") : wrong("game_versions") = New JArray("1.21.1")
            Dim wrongFile = ApiFile("wrong.jar", True) : wrongFile("url") = fixture.Server.Url & "/wrong.jar" : wrong("files") = New JArray(wrongFile)
            Dim matching = Row(), source = ApiFile("fabric-api-sources.jar"), runtime = ApiFile("runtime.jar", content:=jar)
            source("url") = fixture.Server.Url & "/sources.jar" : runtime("url") = fixture.Server.Url & "/runtime.jar"
            matching("files") = New JArray(source, runtime)
            Dim entries = Await LoadAsync(fixture.Server, New JArray(wrong, matching))
            Dim selection = fixture.Fabric() : selection.Entries(ComponentKind.FabricApi) = entries(0)
            Directory.CreateDirectory(fixture.Root)
            Dim sentinel = Path.Combine(fixture.Root, "keep.txt") : File.WriteAllText(sentinel, "keep existing files")
            Dim result = Await fixture.InstallAsync("api-install", selection)
            Dim target = Path.Combine(result.InstancePath, "mods", "runtime.jar")
            Console.WriteLine("FABRIC_API_INSTALL runtime=" & File.Exists(target).ToString() & " wrong_requested=" & fixture.Server.Requests.ContainsKey("/wrong.jar").ToString())
            Check(File.Exists(target) AndAlso File.ReadAllBytes(target).SequenceEqual(jar) AndAlso
                  Not fixture.Server.Requests.ContainsKey("/sources.jar") AndAlso Not fixture.Server.Requests.ContainsKey("/wrong.jar"),
                  "actual Fabric installation publishes only the matching runtime API JAR with its verified bytes", passed)
            Check(File.ReadAllText(sentinel) = "keep existing files" AndAlso
                  File.ReadAllText(Path.Combine(result.InstancePath, "PCL", "Setup.ini")).Contains("VersionArgumentIndieV2:True"),
                  "Fabric API selection keeps existing files and the actual PCL isolation layout", passed)
            Await fixture.InstallAsync("api-repeat", selection)
            Check(fixture.Server.Requests("/runtime.jar") = 1,
                  "a second instance reuses the validated API JAR without another network request", passed)
        End Using
    End Function

    Private Sub Check(condition As Boolean, label As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub
    Private Function Hash(payload As Byte()) As String
        Using digest = Security.Cryptography.SHA1.Create()
            Return BitConverter.ToString(digest.ComputeHash(payload)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function
    Private Function Utf8(value As String) As Byte()
        Return Encoding.UTF8.GetBytes(value)
    End Function
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
