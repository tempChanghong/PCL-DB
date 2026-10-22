Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module OptiFinePayloadChecks
    Private Const Mirror As String = "/mirror/optifine/fixture"
    Private Const Official As String = "/official/downloadx?f=fixture.jar&token=fixture"
    Private Const Page As String = "/official/adloadx?f=fixture.jar"

    Friend Async Function RunAsync(Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "component-catalogue-optifine-payload-fallback" Then Await FallbackAsync(passed)
        If onlyCase Is Nothing OrElse onlyCase = "component-catalogue-optifine-payload-boundary" Then Await BoundaryAsync(passed)
        If onlyCase Is Nothing OrElse onlyCase = "component-catalogue-optifine-payload-cancel" Then Await CancelAsync(passed)
        If passed.Count = 0 Then Throw New ArgumentException(onlyCase)
        Return passed
    End Function

    Private Async Function FallbackAsync(passed As List(Of String)) As Task
        For Each preference In {SourcePreference.Automatic, SourcePreference.OfficialFirst, SourcePreference.MirrorFirst}
            For Each bad In {Jar(1024), Jar(64 * 1024 - 1), Encoding.UTF8.GetBytes(New String("x"c, 64 * 1024)), Jar(64 * 1024, False)}
                Using fixture As New Fixture(preference), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(15))
                    fixture.Server.Files(fixture.First) = bad
                    Dim result = Await fixture.InstallAsync(timeout.Token)
                    Dim target = Path.Combine(result.InstancePath, "mods", "1.12.2", "fixture.jar")
                    Console.WriteLine("OPTIFINE_PAYLOAD preference=" & preference.ToString() & " badSize=" & bad.Length & " fallback=" & fixture.Server.Requests.ContainsKey(fixture.Second).ToString())
                    Check(File.ReadAllBytes(target).SequenceEqual(fixture.Good) AndAlso fixture.Server.Requests.ContainsKey(fixture.Second), "OptiFine rejects the preferred incomplete or invalid payload and publishes the alternate JAR: " & preference.ToString() & " " & bad.Length & " " & (bad(0) = &H50).ToString(), passed)
                    Check(fixture.Server.Requests(fixture.First) = 1 AndAlso fixture.Server.Requests(fixture.Second) = 1 AndAlso fixture.Server.Requests(Page) = 1,
                          "OptiFine fallback keeps source ordering and decodes the official HTML link without extra file requests", passed)
                End Using
            Next
            Using fixture As New Fixture(preference), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(15))
                Dim result = Await fixture.InstallAsync(timeout.Token)
                Check(File.ReadAllBytes(Path.Combine(result.InstancePath, "mods", "1.12.2", "fixture.jar")).SequenceEqual(fixture.Good) AndAlso Not fixture.Server.Requests.ContainsKey(fixture.Second), "a valid OptiFine payload at exactly 64 KiB avoids fallback: " & preference.ToString(), passed)
                Check(fixture.Server.Requests.ContainsKey(Page) = (preference = SourcePreference.OfficialFirst), "a successful mirror does not request the official download page: " & preference.ToString(), passed)
                Await fixture.InstallAsync(timeout.Token, "second")
                Check(fixture.Server.Requests(fixture.First) = 2, "unhashed OptiFine downloads are not silently promoted into verified content reuse: " & preference.ToString(), passed)
            End Using
        Next
    End Function

    Private Async Function BoundaryAsync(passed As List(Of String)) As Task
        For Each preference In {SourcePreference.Automatic, SourcePreference.OfficialFirst, SourcePreference.MirrorFirst}
            Using fixture As New Fixture(preference), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(15))
                fixture.Server.Files(Mirror) = Jar(64 * 1024 - 1) : fixture.Server.Files(Official) = Jar(64 * 1024 - 1)
                Dim failure As Exception = Nothing
                Try
                    Await fixture.InstallAsync(timeout.Token)
                Catch ex As Exception
                    failure = ex
                End Try
                Console.WriteLine("OPTIFINE_MINIMUM rejected=" & (failure IsNot Nothing).ToString() & " published=" & Directory.Exists(fixture.InstancePath).ToString())
                Check(TypeOf failure Is InvalidDataException AndAlso failure.Message.Contains("64 KiB") AndAlso Not Directory.Exists(fixture.InstancePath), "two below-minimum OptiFine sources fail without publishing an instance: " & preference.ToString(), passed)
                Check(fixture.Server.Requests.ContainsKey(fixture.First) AndAlso fixture.Server.Requests.ContainsKey(fixture.Second) AndAlso File.ReadAllText(fixture.Sentinel) = "keep user data", "all-source payload failure preserves existing files and tries both candidates", passed)
                fixture.Server.Files(fixture.First) = fixture.Good
                Dim result = Await fixture.InstallAsync(timeout.Token)
                Check(File.Exists(Path.Combine(result.InstancePath, "PCL", "Setup.ini")), "the same instance name can be retried after payload validation fails", passed)
            End Using
            Using fixture As New Fixture(preference, standalone:=True), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(15))
                fixture.Server.Files(Mirror) = Jar(300 * 1024 - 1) : fixture.Server.Files(Official) = Jar(300 * 1024 - 1)
                Dim failure As Exception = Nothing
                Try
                    Await fixture.InstallAsync(timeout.Token)
                Catch ex As Exception
                    failure = ex
                End Try
                Check(TypeOf failure Is InvalidDataException AndAlso failure.Message.Contains("300 KiB") AndAlso fixture.Server.Requests.ContainsKey(fixture.Second) AndAlso Not Directory.Exists(fixture.InstancePath), "standalone OptiFine uses the PCL 300 KiB minimum instead of the mod threshold: " & preference.ToString(), passed)
                fixture.Server.Files(fixture.First) = Jar(300 * 1024)
                failure = Nothing
                Try
                    Await fixture.InstallAsync(timeout.Token)
                Catch ex As Exception
                    failure = ex
                End Try
                ' Deliberately invalid class bytes stop before Java execution, after download validation.
                Check(TypeOf failure Is InvalidDataException AndAlso failure.Message.Contains("class 文件头无效") AndAlso Not Directory.Exists(fixture.InstancePath), "exactly 300 KiB passes standalone payload validation and reaches the independent class-header check", passed)
            End Using
        Next
    End Function

    Private Async Function CancelAsync(passed As List(Of String)) As Task
        For Each preference In {SourcePreference.MirrorFirst, SourcePreference.OfficialFirst}
            Using fixture As New Fixture(preference), gate As New ManualResetEventSlim(), cancel As New CancellationTokenSource(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(15))
                fixture.Server.BodyGates(fixture.First) = gate
                Dim install = fixture.InstallAsync(cancel.Token)
                While Not fixture.Server.Requests.ContainsKey(fixture.First) AndAlso Not install.IsCompleted
                    Await Task.Delay(10, timeout.Token)
                End While
                Check(fixture.Server.Requests.ContainsKey(fixture.First), "OptiFine cancellation reaches the selected file body: " & preference.ToString(), passed)
                cancel.Cancel()
                Dim cancelled As Boolean
                Try
                    Await install
                Catch ex As OperationCanceledException
                    cancelled = True
                Finally
                    gate.Set()
                End Try
                Check(cancelled AndAlso Not fixture.Server.Requests.ContainsKey(fixture.Second) AndAlso Not Directory.Exists(fixture.InstancePath), "cancelling an OptiFine payload does not start fallback or publish an instance: " & preference.ToString(), passed)
                Dim result = Await fixture.InstallAsync(timeout.Token)
                Check(File.ReadAllBytes(Path.Combine(result.InstancePath, "mods", "1.12.2", "fixture.jar")).SequenceEqual(fixture.Good) AndAlso File.ReadAllText(fixture.Sentinel) = "keep user data", "OptiFine cancellation can retry safely with the original user files intact", passed)
            End Using
        Next
    End Function

    Friend Function Jar(length As Integer, Optional withInstaller As Boolean = True) As Byte()
        Dim overhead = BuildJar(0, withInstaller).Length
        If length < overhead Then Throw New ArgumentOutOfRangeException(NameOf(length))
        Dim padding = length - overhead
        For attempt = 0 To 3
            Dim bytes = BuildJar(padding, withInstaller)
            If bytes.Length = length Then Return bytes
            padding += length - bytes.Length ' Deflate block framing also consumes bytes.
        Next
        Throw New InvalidOperationException("Cannot construct the exact-size OptiFine fixture.")
    End Function
    Private Function BuildJar(padding As Integer, withInstaller As Boolean) As Byte()
        Using buffer As New MemoryStream()
            Using archive As New ZipArchive(buffer, ZipArchiveMode.Create, True)
                If withInstaller Then
                    Using output = archive.CreateEntry("optifine/Installer.class", CompressionLevel.NoCompression).Open()
                        output.WriteByte(1) ' Installed as a mod, or rejected before Java execution.
                    End Using
                End If
                Using output = archive.CreateEntry("fixture-padding.bin", CompressionLevel.NoCompression).Open()
                    Dim bytes(padding - 1) As Byte
                    output.Write(bytes, 0, bytes.Length)
                End Using
            End Using
            Return buffer.ToArray()
        End Using
    End Function
    Private Function Utf8(value As String) As Byte()
        Return Encoding.UTF8.GetBytes(value)
    End Function
    Private Function Hash(bytes As Byte()) As String
        Using digest = Security.Cryptography.SHA1.Create()
            Return BitConverter.ToString(digest.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function
    Private Function Artifact(url As String, bytes As Byte()) As JObject
        Return New JObject From {{"url", url}, {"size", bytes.Length}, {"sha1", Hash(bytes)}}
    End Function
    Private Sub Check(condition As Boolean, label As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub
    Private Class Fixture
        Implements IDisposable
        Friend ReadOnly Server As New TestServer()
        Friend ReadOnly Root As String = Path.Combine(Path.GetTempPath(), "PclDbOptiPayload-" & Guid.NewGuid().ToString("N"), ".minecraft")
        Friend ReadOnly Good As Byte() = Jar(64 * 1024)
        Friend ReadOnly First As String
        Friend ReadOnly Second As String
        Friend ReadOnly Sentinel As String
        Friend ReadOnly InstancePath As String
        Private ReadOnly _selection As New ComponentSelection()
        Private ReadOnly _entry As VersionEntry
        Private ReadOnly _installer As MinecraftInstaller
        Friend Sub New(preference As SourcePreference, Optional standalone As Boolean = False)
            Directory.CreateDirectory(Root)
            Sentinel = Path.Combine(Root, "keep.txt") : File.WriteAllText(Sentinel, "keep user data")
            InstancePath = Path.Combine(Root, "versions", "fixture")
            First = If(preference = SourcePreference.OfficialFirst, Official, Mirror) : Second = If(First = Mirror, Official, Mirror)
            Server.Files(Page) = Utf8("<a href='downloadx?f=fixture.jar&amp;token=fixture'>Download</a>")
            Server.Files(Mirror) = Good : Server.Files(Official) = Good
            Server.Files("/client") = Utf8("fixture vanilla client") : Server.Files("/index") = Utf8("{""objects"":{}}")
            Server.Files("/versions/com/mumfrey/liteloader/1.12.2/liteloader-1.12.2.jar") = Utf8("fixture runtime")
            Dim index = Artifact(Server.Url & "/index", Server.Files("/index")) : index("id") = "fixture"
            Dim metadata As New JObject From {{"id", "1.12.2"}, {"type", "release"}, {"mainClass", "vanilla.Main"}, {"libraries", New JArray()}, {"assets", "fixture"}, {"assetIndex", index}, {"downloads", New JObject From {{"client", Artifact(Server.Url & "/client", Server.Files("/client"))}}}}
            Server.Files("/version") = Utf8(metadata.ToString())
            _entry = New VersionEntry With {.Id = "1.12.2", .Url = Server.Url & "/version", .Sha1 = Hash(Server.Files("/version"))}
            _selection.Entries(ComponentKind.OptiFine) = New ComponentEntry With {.Kind = ComponentKind.OptiFine, .Minecraft = "1.12.2", .Version = "fixture", .FileName = "fixture.jar", .Url = "https://bmclapi2.bangbang93.com/optifine/fixture"}
            If Not standalone Then _selection.Entries(ComponentKind.LiteLoader) = New ComponentEntry With {.Kind = ComponentKind.LiteLoader, .Minecraft = "1.12.2", .Version = "1.12.2", .Data = New JObject From {{"libraries", New JArray()}, {"tweakClass", "fixture.Tweaker"}}}
            _installer = New MinecraftInstaller(New FixturePolicy(Server.Url) With {.Preference = preference})
        End Sub
        Friend Function InstallAsync(token As CancellationToken, Optional name As String = "fixture") As Task(Of InstallResult)
            Return _installer.InstallAsync(_entry, Root, name, {}, Nothing, token, components:=_selection)
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
        Protected Overrides Function MetadataUrl(source As String) As String
            Dim address As New Uri(source)
            Return If(address.IsLoopback, source, _server & "/official" & address.PathAndQuery)
        End Function
        Public Overrides Function Candidates(url As String, category As String) As List(Of String)
            Dim address As New Uri(url)
            Return New List(Of String) From {_server & If(address.Host = "optifine.net", "/official", If(address.Host.Contains("bmclapi"), "/mirror", "")) & address.PathAndQuery}
        End Function
    End Class
End Module
