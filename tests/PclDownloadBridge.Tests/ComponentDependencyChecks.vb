Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module ComponentDependencyChecks
    Private Const DependencyPath As String = "fixture/processor/1/processor-1.jar"
    Private Const RuntimePath As String = "libraries/fixture/generated/1/generated-1.jar"

    Friend Async Function RunAsync(testRoot As String, Optional onlyCase As String = "component-plan-all") As Task(Of List(Of String))
        Dim passed As New List(Of String)
        If onlyCase = "component-plan-all" OrElse onlyCase = "component-plan-strength" Then Await StrengthAsync(testRoot, passed)
        If onlyCase = "component-plan-all" OrElse onlyCase = "component-plan-mismatch" Then Await MismatchAsync(testRoot, passed)
        If onlyCase = "component-plan-all" OrElse onlyCase = "component-plan-conflict" Then Await ConflictsAsync(testRoot, passed)
        If onlyCase = "component-plan-all" OrElse onlyCase = "component-plan-embedded" Then Await EmbeddedAsync(testRoot, passed)
        If passed.Count = 0 Then Throw New ArgumentException("Unknown component dependency case: " & onlyCase)
        Return passed
    End Function

    Private Sub Check(condition As Boolean, label As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub

    Private Function Hash(bytes As Byte()) As String
        Using digest = Security.Cryptography.SHA1.Create()
            Return BitConverter.ToString(digest.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function

    Private Function ProcessorJar(marker As String) As Byte()
        Using buffer As New MemoryStream()
            Dim original = ComponentChecks.ProcessorJar()
            buffer.Write(original, 0, original.Length)
            Using zip As New ZipArchive(buffer, ZipArchiveMode.Update, True)
                WriteEntry(zip, "fixture-marker.txt", Encoding.UTF8.GetBytes(marker))
            End Using
            Return buffer.ToArray()
        End Using
    End Function

    Private Async Function StrengthAsync(testRoot As String, passed As List(Of String)) As Task
        For Each kind In {ComponentKind.Forge, ComponentKind.NeoForge}
            Using f As New Fixture(testRoot, kind, "strength")
                f.Publish(New JArray(f.Library(False), f.Library(True)))
                Dim first = Await f.InstallAsync("first")
                Check(File.Exists(Path.Combine(first.InstancePath, "first.json")) AndAlso f.ProcessorRan AndAlso f.Requested("/dependency") = 1,
                      kind.ToString() & " merges duplicate processor dependencies and executes the real Java fixture", passed)
                Dim cache = Path.Combine(f.Root, ".vanilla-downloader", "component-cache")
                Check(Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories).Any(Function(path) File.ReadAllBytes(path).SequenceEqual(f.GoodJar)),
                      kind.ToString() & " retains a later duplicate SHA1 for checked private dependency caching", passed)
                Dim second = Await f.InstallAsync("second")
                Check(second.NetworkBytes = 0 AndAlso f.Requested("/dependency") = 1 AndAlso File.Exists(Path.Combine(second.InstancePath, "second.json")),
                      kind.ToString() & " reuses the checked duplicate dependency on a subsequent installation", passed)
            End Using
        Next
    End Function

    Private Async Function MismatchAsync(testRoot As String, passed As List(Of String)) As Task
        For Each kind In {ComponentKind.Forge, ComponentKind.NeoForge}
            Using f As New Fixture(testRoot, kind, "mismatch")
                Check(f.GoodJar.Length = f.BadJar.Length, kind.ToString() & " processor fixture has equal-size runnable archives with different content", passed)
                f.Server.Files("/dependency") = f.BadJar
                f.Publish(New JArray(f.Library(False), f.Library(True)))
                Dim failure = Await f.RejectedAsync("mismatch")
                Console.WriteLine("COMPONENT_PLAN_MISMATCH kind=" & kind.ToString() & " rejected=" & (failure IsNot Nothing).ToString() & " processorRan=" & f.ProcessorRan.ToString())
                Check(failure IsNot Nothing AndAlso Not f.ProcessorRan AndAlso Not f.Published("mismatch"),
                      kind.ToString() & " rejects a same-size dependency that fails a later duplicate SHA1 before Java or instance publication", passed)
                Check(Not File.Exists(SafeFiles.UnderRoot(f.Root, RuntimePath)),
                      kind.ToString() & " publishes no generated runtime after dependency integrity failure", passed)
            End Using
        Next
    End Function

    Private Async Function ConflictsAsync(testRoot As String, passed As List(Of String)) As Task
        For Each field In {"size", "sha1"}
            For Each embedded In {False, True}
                Using f As New Fixture(testRoot, ComponentKind.NeoForge, "conflict-" & field & "-" & embedded.ToString())
                    Dim first = f.Library(True), second = f.Library(True)
                    Dim artifact = DirectCast(second.SelectToken("downloads.artifact"), JObject)
                    artifact(field) = If(field = "size", CType(New JValue(f.GoodJar.Length + 1), JToken), New JValue(New String("0"c, 40)))
                    f.Server.Files("/unrelated") = Encoding.UTF8.GetBytes("unrelated dependency")
                    Dim unrelated As New JObject From {{"name", "fixture:unrelated:1"}, {"downloads", New JObject From {{"artifact", New JObject From {
                        {"path", "fixture/unrelated/1/unrelated-1.jar"}, {"url", f.Server.Url & "/unrelated"}, {"sha1", Hash(f.Server.Files("/unrelated"))}}}}}}
                    f.Publish(New JArray(unrelated, first, second), If(embedded, f.GoodJar, Nothing))
                    Dim failure = Await f.RejectedAsync("conflict")
                    Dim label = field & "; embedded=" & embedded.ToString()
                    Check(TypeOf failure Is InvalidDataException AndAlso failure.Message.Contains("processor-1.jar"),
                          "conflicting installer dependency declarations identify their destination: " & label, passed)
                    Check(f.Requested("/dependency") = 0 AndAlso f.Requested("/unrelated") = 0 AndAlso Not f.ProcessorRan AndAlso Not f.Published("conflict"),
                          "dependency conflicts stop the complete batch before reuse, transfer or Java: " & label, passed)
                End Using
            Next
        Next
    End Function

    Private Async Function EmbeddedAsync(testRoot As String, passed As List(Of String)) As Task
        For Each damaged In {False, True}
            Using f As New Fixture(testRoot, ComponentKind.NeoForge, "embedded-" & damaged.ToString())
                Dim stronger = f.Library(True)
                stronger("downloads")("artifact")("path") = "FIXTURE\processor\.\1\processor-1.jar"
                stronger("downloads")("artifact")("url") = f.Server.Url & "/alternate"
                f.Server.Files("/alternate") = f.GoodJar
                f.Publish(New JArray(f.Library(False), stronger), If(damaged, f.BadJar, f.GoodJar))
                Dim result = Await f.InstallAsync("embedded")
                Check(f.ProcessorRan AndAlso File.Exists(Path.Combine(result.InstancePath, "embedded.json")),
                      "canonical duplicate dependency checks complete with an embedded archive; damaged=" & damaged.ToString(), passed)
                Check(f.Requested("/dependency") = If(damaged, 1, 0) AndAlso f.Requested("/alternate") = 0,
                      "merged embedded dependency checks retain the first source and skip verified content; damaged=" & damaged.ToString(), passed)
            End Using
        Next
    End Function

    Private Sub WriteEntry(zip As ZipArchive, name As String, bytes As Byte())
        Using output = zip.CreateEntry(name, CompressionLevel.NoCompression).Open()
            output.Write(bytes, 0, bytes.Length)
        End Using
    End Sub

    Private Class Fixture
        Implements IDisposable
        Private ReadOnly _installation As InstallationChecks.InstallFixture
        Private ReadOnly _selected As New ComponentSelection()
        Friend ReadOnly GoodJar As Byte() = ProcessorJar("verified")
        Friend ReadOnly BadJar As Byte() = ProcessorJar("corrupt!")
        Friend Sub New(testRoot As String, kind As ComponentKind, name As String)
            _installation = New InstallationChecks.InstallFixture(testRoot, "component-plan-" & kind.ToString() & "-" & name)
            _selected.Entries(kind) = New ComponentEntry With {.Kind = kind, .Minecraft = _installation.Entry.Id, .Version = "fixture", .Url = Server.Url & "/installer"}
            Server.Files("/dependency") = GoodJar
        End Sub
        Friend ReadOnly Property Server As TestServer
            Get
                Return _installation.Server
            End Get
        End Property
        Friend ReadOnly Property Root As String
            Get
                Return _installation.Root
            End Get
        End Property
        Friend Function Library(strong As Boolean) As JObject
            Dim artifact As New JObject From {{"path", DependencyPath}, {"url", Server.Url & "/dependency"}, {"size", GoodJar.Length}}
            If strong Then artifact("sha1") = Hash(GoodJar)
            Return New JObject From {{"name", "fixture:processor:1"}, {"downloads", New JObject From {{"artifact", artifact}}}}
        End Function
        Friend Sub Publish(libraries As JArray, Optional embedded As Byte() = Nothing)
            Dim runtime = Encoding.UTF8.GetBytes("patched")
            Dim profile As New JObject From {{"minecraft", _installation.Entry.Id}, {"libraries", libraries},
                {"data", New JObject From {{"PATCHED", New JObject From {{"client", "[fixture:generated:1]"}}}}},
                {"processors", New JArray(New JObject From {{"sides", New JArray("client")}, {"jar", "fixture:processor:1"}, {"classpath", New JArray()},
                    {"args", New JArray("{PATCHED}", "patched")}, {"outputs", New JObject From {{"{PATCHED}", Hash(runtime)}}}})}}
            Dim version As New JObject From {{"mainClass", "fixture.Loader"}, {"libraries", New JArray(New JObject From {
                {"name", "fixture:generated:1"}, {"downloads", New JObject From {{"artifact", New JObject From {
                    {"path", RuntimePath.Substring(10)}, {"url", ""}, {"sha1", Hash(runtime)}, {"size", runtime.Length}}}}}})}}
            Using buffer As New MemoryStream()
                Using zip As New ZipArchive(buffer, ZipArchiveMode.Create, True)
                    WriteEntry(zip, "install_profile.json", Encoding.UTF8.GetBytes(profile.ToString()))
                    WriteEntry(zip, "version.json", Encoding.UTF8.GetBytes(version.ToString()))
                    If embedded IsNot Nothing Then WriteEntry(zip, "maven/" & DependencyPath, embedded)
                End Using
                Server.Files("/installer") = buffer.ToArray()
            End Using
            _selected.Entries.Values.Single().Sha1 = Hash(Server.Files("/installer"))
        End Sub
        Friend Function Requested(path As String) As Integer
            Dim count As Integer
            Server.Requests.TryGetValue(path, count)
            Return count
        End Function
        Friend ReadOnly Property ProcessorRan As Boolean
            Get
                Dim log = Path.Combine(Root, ".vanilla-downloader", "installer.log")
                Return File.Exists(log) AndAlso File.ReadAllText(log).Contains("fixture processor complete")
            End Get
        End Property
        Friend Function Published(name As String) As Boolean
            Return Directory.Exists(Path.Combine(Root, "versions", name))
        End Function
        Friend Function InstallAsync(name As String) As Task(Of InstallResult)
            Return _installation.InstallAsync(name, _selected)
        End Function
        Friend Async Function RejectedAsync(name As String) As Task(Of Exception)
            Try
                Await InstallAsync(name)
                Return Nothing
            Catch ex As InvalidDataException
                Return ex
            Catch ex As IOException
                Return ex
            End Try
        End Function
        Public Sub Dispose() Implements IDisposable.Dispose
            _installation.Dispose()
        End Sub
    End Class
End Module
