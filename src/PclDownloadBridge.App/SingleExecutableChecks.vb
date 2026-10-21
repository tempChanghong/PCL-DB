Imports System.IO
Imports System.Net
Imports System.Net.Sockets
Imports System.Reflection
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json
Imports PclDownloadBridge.Core

Friend NotInheritable Class SingleExecutableChecks
    Friend Shared Function Run(reportPath As String) As Integer
        Return UiCheckRunner.Run(reportPath, "standalone", Sub(directory, passed) VerifyAsync(directory, passed).GetAwaiter().GetResult())
    End Function

    Private Shared Sub Check(condition As Boolean, label As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub

    Private Shared Function ResourceBytes(name As String) As Byte()
        Using source = GetType(SingleExecutableChecks).Assembly.GetManifestResourceStream(name), buffer As New MemoryStream()
            If source Is Nothing Then Throw New FileNotFoundException(name)
            source.CopyTo(buffer)
            Return buffer.ToArray()
        End Using
    End Function

    Private Shared Function Hash(bytes As Byte()) As String
        Using digest = SHA256.Create()
            Return BitConverter.ToString(digest.ComputeHash(bytes)).Replace("-", "")
        End Using
    End Function

    Private Shared Async Function VerifyAsync(directory As String, passed As List(Of String)) As Task
        Check(RuntimeBootstrap.IsStandalone, "standalone build flag is active", passed)
        Dim executableFiles = IO.Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory)
        Check(executableFiles.Length = 1 AndAlso IO.Path.GetExtension(executableFiles(0)) = ".exe", "the cold-start directory contains only one executable and no config or DLLs", passed)
        Dim assemblies = {GetType(MinecraftInstaller).Assembly, GetType(JsonConvert).Assembly}
        For Each assembly In assemblies
            Check(String.IsNullOrEmpty(assembly.Location), "dependency was loaded from embedded bytes: " & assembly.GetName().Name, passed)
            Dim requests = Enumerable.Range(0, 16).Select(Function(index) Task.Run(Function() RuntimeBootstrap.Resolve(assembly.FullName))).ToArray()
            Dim values = Await Task.WhenAll(requests).ConfigureAwait(False)
            Check(values.All(Function(value) Object.ReferenceEquals(value, assembly)), "concurrent dependency requests reuse the same assembly identity: " & assembly.GetName().Name, passed)
        Next
        Check(RuntimeBootstrap.Resolve("Newtonsoft.Json.resources") Is Nothing AndAlso RuntimeBootstrap.Resolve("Unrelated.Assembly") Is Nothing, "the resolver ignores unknown and satellite assemblies", passed)
        Dim wrongVersion As New AssemblyName(assemblies(0).FullName) With {.Version = New Version(99, 0, 0, 0)}
        Check(RuntimeBootstrap.Resolve(wrongVersion.FullName) Is Nothing, "an incompatible requested assembly version is rejected", passed)
        Dim wrongToken As New AssemblyName(assemblies(1).FullName)
        wrongToken.SetPublicKeyToken(New Byte(7) {})
        Check(RuntimeBootstrap.Resolve(wrongToken.FullName) Is Nothing, "an incompatible public-key token is rejected", passed)
        Dim legacy As Boolean, blocked As Boolean
        Check(AppContext.TryGetSwitch("Switch.System.IO.UseLegacyPathHandling", legacy) AndAlso Not legacy, "long-path handling does not rely on an adjacent config", passed)
        Check(AppContext.TryGetSwitch("Switch.System.IO.BlockLongPaths", blocked) AndAlso Not blocked, "long paths remain enabled without an adjacent config", passed)
        Dim longPath = Path.Combine(directory, "long-path", New String("a"c, 90), New String("b"c, 90), New String("c"c, 90), "样本.txt")
        IO.Directory.CreateDirectory(Path.GetDirectoryName(longPath))
        File.WriteAllText(longPath, "long path fixture")
        Check(longPath.Length > 260 AndAlso File.ReadAllText(longPath) = "long path fixture", "the isolated executable can read and write a real path longer than 260 characters", passed)
        For Each name In BundledDocuments.Names
            Check(Not String.IsNullOrWhiteSpace(BundledDocuments.Read(name)), "bundled documentation remains available: " & name, passed)
        Next
        Dim cache = Path.Combine(directory, "document-cache")
        Dim notices = BundledDocuments.Materialize("THIRD_PARTY_NOTICES.md", cache)
        Check(File.ReadAllText(notices).Contains(BundledDocuments.Read("LICENCE")), "standalone third-party notices include the complete upstream licence", passed)
        Dim readme = BundledDocuments.Materialize("README.md", cache)
        File.WriteAllText(readme, "damaged cached document")
        Check(File.ReadAllText(BundledDocuments.Materialize("README.md", cache)) = BundledDocuments.Read("README.md"), "a modified documentation cache is restored from the embedded source", passed)
        Dim stamp As New DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc)
        File.SetLastWriteTimeUtc(readme, stamp)
        Check(BundledDocuments.Materialize("README.md", cache) = readme AndAlso File.GetLastWriteTimeUtc(readme) = stamp, "unchanged documentation is reused without rewriting its timestamp", passed)
        Dim rejected As Boolean
        Try
            BundledDocuments.Materialize("../settings.json", cache)
        Catch ex As ArgumentException
            rejected = True
        End Try
        Check(rejected, "unsupported documentation names cannot create arbitrary cache files", passed)
        Using bridge = assemblies(0).GetManifestResourceStream("OptiFineBridge.class")
            Check(bridge IsNot Nothing AndAlso bridge.ReadByte() = &HCA AndAlso bridge.ReadByte() = &HFE, "the embedded Core retains its Java bridge resource", passed)
        End Using
        Await VerifyDownloadAsync(directory, passed).ConfigureAwait(False)
        Dim embedded = assemblies.Select(Function(assembly) New With {
            .Name = assembly.GetName().Name, .Identity = assembly.FullName,
            .SHA256 = Hash(ResourceBytes("PclDownloadBridge.Dependencies." & assembly.GetName().Name & ".dll"))}).ToArray()
        Dim documents = BundledDocuments.Names.Select(Function(name) New With {.Name = name, .SHA256 = Hash(ResourceBytes("PclDownloadBridge.Documents." & name))}).ToArray()
        File.WriteAllText(Path.Combine(directory, "embedded-content.json"), JsonConvert.SerializeObject(New With {.Assemblies = embedded, .Documents = documents}, Formatting.Indented))
    End Function

    Private Shared Async Function VerifyDownloadAsync(directory As String, passed As List(Of String)) As Task
        Dim bytes = Encoding.UTF8.GetBytes("isolated Core HTTP download")
        Dim listener As New TcpListener(IPAddress.Loopback, 0)
        listener.Start()
        Dim port = DirectCast(listener.LocalEndpoint, IPEndPoint).Port
        Dim server = Task.Run(Async Function()
                                  Using client = Await listener.AcceptTcpClientAsync().ConfigureAwait(False), stream = client.GetStream()
                                      Using reader As New StreamReader(stream, Encoding.ASCII, False, 1024, True)
                                          Dim line As String
                                          Do
                                              line = Await reader.ReadLineAsync().ConfigureAwait(False)
                                          Loop While Not String.IsNullOrEmpty(line)
                                      End Using
                                      Dim header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK" & vbCrLf & "Content-Length: " & bytes.Length & vbCrLf & "Connection: close" & vbCrLf & vbCrLf)
                                      Await stream.WriteAsync(header, 0, header.Length).ConfigureAwait(False)
                                      Await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(False)
                                  End Using
                              End Function)
        Dim failure As Exception = Nothing
        Try
            Dim digest As String
            Using algorithm = SHA1.Create()
                digest = BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-", "")
            End Using
            Dim target = Path.Combine(directory, "download-fixture")
            Using cancel As New CancellationTokenSource(10000), engine As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, Nothing, {}, 2)
                Await engine.DownloadAsync(target, {New DownloadFile With {.RelativePath = "fixture.bin", .Url = "http://127.0.0.1:" & port & "/fixture", .Size = bytes.Length, .Sha1 = digest}}, cancel.Token).ConfigureAwait(False)
                Check(File.ReadAllBytes(Path.Combine(target, "fixture.bin")).SequenceEqual(bytes) AndAlso engine.NetworkBytes = bytes.Length AndAlso engine.Reused = 0, "the embedded download engine completes a real loopback transfer with SHA1 verification", passed)
            End Using
        Catch ex As Exception
            failure = ex
        Finally
            listener.Stop()
        End Try
        Try
            Await server.ConfigureAwait(False)
        Catch ex As Exception When failure IsNot Nothing
        End Try
        If failure IsNot Nothing Then Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw()
    End Function
End Class
