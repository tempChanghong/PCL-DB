Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module ForgeChecksumChecks
    Friend Async Function RunAsync(testRoot As String, Optional kind As ComponentKind = ComponentKind.Forge) As Task(Of List(Of String))
        If kind <> ComponentKind.Forge AndAlso kind <> ComponentKind.NeoForge Then Throw New ArgumentOutOfRangeException(NameOf(kind))
        Dim passed As New List(Of String)
        Dim check As Action(Of Boolean, String) = Sub(condition, label)
                                                     If kind = ComponentKind.NeoForge AndAlso Not label.Contains("NeoForge") Then label = label.Replace("Forge", "NeoForge")
                                                     If Not condition Then Throw New InvalidOperationException(label)
                                                     passed.Add(label)
                                                 End Sub
        For Each algorithm In {"sha1", "md5"}
            Using f As New Fixture(testRoot, SourcePreference.OfficialFirst, kind)
                If algorithm = "sha1" Then f.Entry.Sha1 = Digest(f.Jar, False) Else f.Entry.Md5 = Digest(f.Jar, True)
                Await f.ApplyAsync(CancellationToken.None)
                check(f.Published AndAlso Not f.HashRequested, "a supplied valid Forge " & algorithm & " verifies the installer without requesting sidecars")
            End Using
        Next
        For Each preference In {SourcePreference.OfficialFirst, SourcePreference.MirrorFirst}
            Using f As New Fixture(testRoot, preference, kind)
                f.Server.Files(f.First & ".sha1") = Utf8(Digest(f.Jar, False).ToUpperInvariant() & vbCrLf)
                Await f.ApplyAsync(CancellationToken.None)
                check(f.Published AndAlso f.Requested(f.First & ".sha1") AndAlso Not f.Requested(f.Second & ".sha1") AndAlso
                      f.Entry.Sha1 Is Nothing AndAlso f.Entry.Md5 Is Nothing,
                      "hashless Forge resolves the selected file source before downloading without mutating catalogue metadata: " & preference.ToString())
            End Using
        Next
        For Each bad In {"<html>" & New String("a"c, 40) & "</html>", New String("g"c, 40), New String("a"c, 39)}
            Using f As New Fixture(testRoot, SourcePreference.OfficialFirst, kind)
                f.Server.Files(f.First & ".sha1") = Utf8(bad)
                f.Server.Files(f.Second & ".sha1") = Utf8(Digest(f.Jar, False))
                Await f.ApplyAsync(CancellationToken.None)
                check(f.Published AndAlso f.Requested(f.Second & ".sha1") AndAlso Not f.AnyRequested(".md5"),
                      "invalid preferred Forge SHA1 content falls back to a verified alternative: " & bad.Length.ToString())
            End Using
        Next
        Using f As New Fixture(testRoot, SourcePreference.OfficialFirst, kind)
            f.Server.Files(f.First & ".md5") = Utf8(Digest(f.Jar, True))
            Await f.ApplyAsync(CancellationToken.None)
            check(f.Published AndAlso f.Requested(f.First & ".sha1") AndAlso f.Requested(f.Second & ".sha1") AndAlso f.Requested(f.First & ".md5"),
                  "unavailable SHA1 sidecars fall back to the PCL-supported MD5 format")
        End Using
        Using f As New Fixture(testRoot, SourcePreference.OfficialFirst, kind)
            For Each path In {f.First, f.Second}
                f.Server.Files(path & ".sha1") = Utf8("checksum error")
                f.Server.Files(path & ".md5") = Utf8("checksum error")
            Next
            check(Await f.RejectedAsync() AndAlso Not f.ArchiveRequested AndAlso Not f.Published,
                  "invalid checksum sources stop Forge before any installer download or runtime publication")
        End Using
        For Each item In {Tuple.Create("bad-sha1", New String("z"c, 40), CType(Nothing, String)),
                          Tuple.Create("bad-md5", CType(Nothing, String), New String("a"c, 31)),
                          Tuple.Create("bad-secondary", New String("a"c, 40), "bad")}
            Using f As New Fixture(testRoot, SourcePreference.OfficialFirst, kind)
                f.Entry.Sha1 = item.Item2 : f.Entry.Md5 = item.Item3
                check(Await f.RejectedAsync() AndAlso Not f.HashRequested AndAlso Not f.ArchiveRequested,
                      "malformed declared Forge checksums cannot be silently replaced or ignored: " & item.Item1)
            End Using
        Next
        Using f As New Fixture(testRoot, SourcePreference.OfficialFirst, kind), cancel As New CancellationTokenSource(), gate As New ManualResetEventSlim(False)
            f.Server.Files(f.First & ".sha1") = Utf8(Digest(f.Jar, False))
            f.Server.Gates(f.First & ".sha1") = gate
            Dim work = f.ApplyAsync(cancel.Token)
            Dim failure As Exception = Nothing
            Try
                Dim wait = Diagnostics.Stopwatch.StartNew()
                While Not f.Requested(f.First & ".sha1") AndAlso Not work.IsCompleted AndAlso wait.ElapsedMilliseconds < 3000
                    Await Task.Delay(10)
                End While
                check(f.Requested(f.First & ".sha1") AndAlso Not work.IsCompleted AndAlso Not f.ArchiveRequested,
                      "a pending Forge checksum gates installer download instead of racing it unverified")
                cancel.Cancel()
                Dim cancelled As Boolean
                Try
                    Await work
                Catch ex As OperationCanceledException
                    cancelled = True
                End Try
                check(cancelled AndAlso Not f.AnyRequested(".md5") AndAlso Not f.ArchiveRequested AndAlso Not f.Published,
                      "cancelling Forge checksum retrieval stays cancelled without falling back to an unverified installer")
            Catch ex As Exception
                failure = ex
            Finally
                cancel.Cancel() : gate.Set()
            End Try
            Try
                Await work
            Catch
            End Try
            If failure IsNot Nothing Then Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw()
        End Using
        Using f As New Fixture(testRoot, SourcePreference.OfficialFirst, kind)
            f.Server.Files(f.First & ".sha1") = Utf8(Digest(f.Jar, False))
            f.Server.Files(f.First) = Utf8("wrong archive") : f.Server.Files(f.Second) = Utf8("wrong archive")
            check(Await f.RejectedAsync() AndAlso f.ArchiveRequested AndAlso Not f.AnyRequested(".md5") AndAlso Not f.Published,
                  "an installer mismatch cannot downgrade a valid resolved SHA1 to MD5 or publish runtime files")
        End Using
        Using f As New Fixture(testRoot, SourcePreference.OfficialFirst, kind)
            check(Await f.RejectedAsync() AndAlso Not f.ArchiveRequested AndAlso Not f.Published,
                  "missing checksum files produce an explicit failure before Forge installation")
        End Using
        If kind = ComponentKind.NeoForge Then
            Using f As New Fixture(testRoot, SourcePreference.OfficialFirst, kind, "1.20.1")
                f.Server.Files(f.First & ".sha1") = Utf8(Digest(f.Jar, False))
                Await f.ApplyAsync(CancellationToken.None)
                check(f.Published AndAlso f.Entry.Kind = ComponentKind.NeoForge AndAlso f.Entry.Url.Contains("/net/neoforged/forge/1.20.1-") AndAlso f.Requested(f.First & ".sha1"),
                      "the legacy NeoForge forge-package coordinate resolves its exact installer checksum")
            End Using
            Return passed
        End If
        For Each bad As JToken In New JToken() {New JValue(New String("a"c, 31)), New JValue(New String("z"c, 40)), New JValue(123), New JObject From {{"error", "busy"}}}
            Using f As New Fixture(testRoot, SourcePreference.MirrorFirst)
                f.Policy.VersionPreference = SourcePreference.MirrorFirst
                Dim body As JToken = If(TypeOf bad Is JObject, bad, MirrorBody(bad))
                f.Server.Files("/mirror/forge/minecraft/1.8.9") = Utf8(body.ToString())
                f.Server.Files("/official/net/minecraftforge/forge/maven-metadata.xml") = Utf8("<metadata><versioning><versions><version>1.8.9-11.15.1.2318-1.8.9</version></versions></versioning></metadata>")
                Using deadline As New CancellationTokenSource(TimeSpan.FromSeconds(4))
                    Dim entries = Await New ComponentCatalog(f.Policy).LoadAsync(ComponentKind.Forge, "1.8.9", deadline.Token)
                    check(entries.Count = 1 AndAlso entries(0).Sha1 Is Nothing AndAlso f.Requested("/official/net/minecraftforge/forge/maven-metadata.xml"),
                          "invalid BMCLAPI Forge checksum metadata cannot win the source race: " & bad.Type.ToString() & "-" & bad.ToString().Length)
                End Using
            End Using
        Next
        Using f As New Fixture(testRoot, SourcePreference.MirrorFirst)
            f.Policy.VersionPreference = SourcePreference.MirrorFirst
            f.Server.Files("/mirror/forge/minecraft/1.8.9") = Utf8(MirrorBody(New JValue(" " & Digest(f.Jar, True).ToUpperInvariant() & vbCrLf)).ToString())
            Dim entries = Await New ComponentCatalog(f.Policy).LoadAsync(ComponentKind.Forge, "1.8.9", CancellationToken.None)
            check(entries.Count = 1 AndAlso String.Equals(entries(0).Md5, Digest(f.Jar, True), StringComparison.OrdinalIgnoreCase) AndAlso
                  Not f.Requested("/official/net/minecraftforge/forge/maven-metadata.xml"), "valid BMCLAPI checksum whitespace is normalized without unnecessary source fallback")
        End Using
        Using f As New Fixture(testRoot, SourcePreference.MirrorFirst)
            f.Policy.VersionPreference = SourcePreference.MirrorFirst
            f.Server.Files("/mirror/forge/minecraft/1.8.9") = Utf8("[]")
            Dim entries = Await New ComponentCatalog(f.Policy).LoadAsync(ComponentKind.Forge, "1.8.9", CancellationToken.None)
            check(entries.Count = 0 AndAlso Not f.Requested("/official/net/minecraftforge/forge/maven-metadata.xml"),
                  "a genuine empty Forge catalogue remains a valid empty result")
        End Using
        Return passed
    End Function

    Private Function MirrorBody(hash As JToken) As JArray
        Return New JArray(New JObject From {{"version", "11.15.1.2318"}, {"files", New JArray(New JObject From {{"category", "installer"}, {"format", "jar"}, {"hash", hash}})}})
    End Function
    Private Function Utf8(value As String) As Byte()
        Return Encoding.UTF8.GetBytes(value)
    End Function
    Private Function Digest(bytes As Byte(), md5 As Boolean) As String
        Using algorithm As Security.Cryptography.HashAlgorithm = If(md5, CType(Security.Cryptography.MD5.Create(), Security.Cryptography.HashAlgorithm), Security.Cryptography.SHA1.Create())
            Return BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function
    Private Class Fixture
        Implements IDisposable
        Public ReadOnly Server As New TestServer()
        Public ReadOnly Policy As FixturePolicy
        Public ReadOnly Entry As ComponentEntry
        Public ReadOnly Jar As Byte()
        Public ReadOnly First As String, Second As String
        Private ReadOnly _root As String, _stage As String, _runtime As String
        Public Sub New(testRoot As String, preference As SourcePreference, Optional kind As ComponentKind = ComponentKind.Forge, Optional mc As String = Nothing)
            mc = If(mc, If(kind = ComponentKind.Forge, "1.8.9", "1.21.1"))
            Entry = New ComponentEntry With {.Kind = kind, .Minecraft = mc, .Version = If(kind = ComponentKind.Forge, "11.15.1.2318", If(mc = "1.20.1", "47.1.106", "21.1.77"))}
            If kind = ComponentKind.Forge Then
                Entry.Url = "https://maven.minecraftforge.net/net/minecraftforge/forge/1.8.9-11.15.1.2318-1.8.9/forge-1.8.9-11.15.1.2318-1.8.9-installer.jar"
                Jar = ForgeVersionChecks.LegacyInstaller(mc)
                _runtime = "libraries/fixture/forge/1/forge-1.jar"
            Else
                Dim package = If(mc = "1.20.1", "forge", "neoforge"), version = If(mc = "1.20.1", mc & "-", "") & Entry.Version
                Entry.Url = "https://maven.neoforged.net/releases/net/neoforged/" & package & "/" & version & "/" & package & "-" & version & "-installer.jar"
                _runtime = "libraries/fixture/neoforge/1/neoforge-1.jar"
                Jar = NeoForgeInstaller(mc)
            End If
            Policy = New FixturePolicy(Server.Url, preference)
            Dim candidates = Policy.Candidates(Entry.Url, "component")
            First = New Uri(candidates(0)).AbsolutePath : Second = New Uri(candidates(1)).AbsolutePath
            Server.Files(First) = Jar : Server.Files(Second) = Jar
            _root = Path.Combine(testRoot, kind.ToString() & " checksum", Guid.NewGuid().ToString("N"))
            _stage = Path.Combine(_root, "stage") : Directory.CreateDirectory(_stage)
            File.WriteAllText(Path.Combine(_stage, "fixture.jar"), "vanilla")
        End Sub
        Public Async Function ApplyAsync(token As CancellationToken) As Task
            Dim selection As New ComponentSelection : selection.Entries(Entry.Kind) = Entry
            Dim installer As New ComponentInstaller(Policy, selection, _root, {}, Nothing, 4, 0)
            Dim metadata As New JObject From {{"id", Entry.Minecraft}, {"mainClass", "vanilla.Main"}, {"libraries", New JArray()},
                {"downloads", New JObject From {{"client", New JObject From {{"url", Server.Url & "/unused-client"}, {"sha1", Digest(Utf8("vanilla"), False)}, {"size", 7}}}}}}
            Await installer.ApplyAsync(metadata, Entry.Minecraft, _stage, "fixture", token)
        End Function
        Public Async Function RejectedAsync() As Task(Of Boolean)
            Try
                Await ApplyAsync(CancellationToken.None)
                Return False
            Catch ex As InvalidDataException
                Return True
            Catch ex As IOException
                Return True
            Catch ex As AggregateException
                Return True
            End Try
        End Function
        Public Function Requested(path As String) As Boolean
            Return Server.Requests.ContainsKey(path)
        End Function
        Public Function AnyRequested(suffix As String) As Boolean
            Return Server.Requests.Keys.Any(Function(path) path.EndsWith(suffix, StringComparison.Ordinal))
        End Function
        Public ReadOnly Property HashRequested As Boolean
            Get
                Return AnyRequested(".sha1") OrElse AnyRequested(".md5")
            End Get
        End Property
        Public ReadOnly Property ArchiveRequested As Boolean
            Get
                Return Requested(First) OrElse Requested(Second)
            End Get
        End Property
        Public ReadOnly Property Published As Boolean
            Get
                Return File.Exists(Path.Combine(_stage, "PCL", "Setup.ini")) OrElse File.Exists(Path.Combine(_root, _runtime))
            End Get
        End Property
        Public Sub Dispose() Implements IDisposable.Dispose
            Server.Dispose()
        End Sub
    End Class
    Private Function NeoForgeInstaller(mc As String) As Byte()
        Dim runtime = Utf8("NeoForge runtime fixture")
        Dim profile As New JObject From {{"minecraft", mc}, {"processors", New JArray()}, {"libraries", New JArray()}}
        Dim version As New JObject From {{"mainClass", "fixture.NeoForge"}, {"libraries", New JArray(New JObject From {
            {"name", "fixture:neoforge:1"}, {"downloads", New JObject From {{"artifact", New JObject From {
                {"path", "fixture/neoforge/1/neoforge-1.jar"}, {"url", ""}, {"sha1", Digest(runtime, False)}, {"size", runtime.Length}}}}}})}}
        Using output As New MemoryStream
            Using zip As New ZipArchive(output, ZipArchiveMode.Create, True)
                For Each item In New KeyValuePair(Of String, Byte())() {
                    New KeyValuePair(Of String, Byte())("install_profile.json", Utf8(profile.ToString())),
                    New KeyValuePair(Of String, Byte())("version.json", Utf8(version.ToString())),
                    New KeyValuePair(Of String, Byte())("maven/fixture/neoforge/1/neoforge-1.jar", runtime)}
                    Using stream = zip.CreateEntry(item.Key).Open()
                        stream.Write(item.Value, 0, item.Value.Length)
                    End Using
                Next
            End Using
            Return output.ToArray()
        End Using
    End Function
    Private Class FixturePolicy
        Inherits SourcePolicy
        Private ReadOnly _root As String
        Public Sub New(root As String, preference As SourcePreference)
            _root = root : Me.Preference = preference : VersionPreference = SourcePreference.OfficialFirst : AllowLoopback = True
        End Sub
        Protected Overrides Function MetadataUrl(source As String) As String
            Dim uri As New Uri(source)
            If uri.IsLoopback Then Return source
            Return _root & If(uri.Host.Contains("bmclapi"), "/mirror", "/official") & uri.PathAndQuery
        End Function
        Public Overrides Function Candidates(url As String, category As String) As List(Of String)
            Return MyBase.Candidates(url, category).Select(Function(source) MetadataUrl(source)).ToList()
        End Function
    End Class
End Module
