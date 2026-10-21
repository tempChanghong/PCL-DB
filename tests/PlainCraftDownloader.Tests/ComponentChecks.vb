Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PlainCraftDownloader.Core

Module ComponentChecks
    Public Async Function RunAsync(testRoot As String) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        Dim verify As Action(Of Boolean, String) = Sub(condition, label)
                                                       If Not condition Then Throw New Exception("FAIL " & label)
                                                       passed.Add(label)
                                                   End Sub
        Dim forge As New ComponentEntry With {.Kind = ComponentKind.Forge, .Minecraft = "1.20.1", .Version = "47.2.18"}
        Dim opt As New ComponentEntry With {.Kind = ComponentKind.OptiFine, .Minecraft = "1.20.1", .Version = "I6", .RequiredForge = "47.2.18"}
        verify(ComponentSelection.OptiFineFits(opt, forge, "1.20.1"), "PCL OptiFine exact Forge requirement")
        forge.Version = "47.2.19"
        verify(Not ComponentSelection.OptiFineFits(opt, forge, "1.20.1"), "PCL incompatible Forge build rejected")
        opt.RequiredForge = "19"
        verify(ComponentSelection.OptiFineFits(opt, forge, "1.20.1"), "PCL legacy Forge build-number requirement")
        opt.RequiredForge = Nothing
        verify(Not ComponentSelection.OptiFineFits(opt, forge, "1.20.1"), "PCL N/A OptiFine Forge support rejected")
        Dim selected As New ComponentSelection
        Dim invalidRoot = Path.Combine(testRoot, "PCL-incompatible;root")
        Dim incompatibleRoot As Boolean
        Try : InstallPreflight.ValidatePaths(invalidRoot, "vanilla") : Catch ex As ArgumentException : incompatibleRoot = True : End Try
        verify(incompatibleRoot AndAlso Not Directory.Exists(invalidRoot), "PCL-incompatible Java paths rejected before creating directories")
        selected.Entries(ComponentKind.NeoForge) = New ComponentEntry With {.Kind = ComponentKind.NeoForge, .Minecraft = "1.21.1", .Version = "21.1.77"}
        verify(selected.Unavailable(ComponentKind.OptiFine, "1.21.1") IsNot Nothing AndAlso selected.Unavailable(ComponentKind.Forge, "1.21.1") IsNot Nothing AndAlso selected.Unavailable(ComponentKind.Fabric, "1.21.1") IsNot Nothing, "PCL NeoForge mutual exclusions")
        selected.Entries.Clear()
        selected.Entries(ComponentKind.Fabric) = New ComponentEntry With {.Kind = ComponentKind.Fabric, .Minecraft = "1.20.1", .Version = "0.16.9"}
        verify(selected.Unavailable(ComponentKind.OptiFine, "1.21.1") IsNot Nothing, "PCL Fabric OptiFine blocked after 1.20.4")
        selected.Entries(ComponentKind.OptiFine) = opt
        Dim rejected = False
        Try : selected.Validate("1.20.1") : Catch ex As InvalidDataException : rejected = True : End Try
        verify(rejected, "Fabric plus OptiFine requires OptiFabric before writes")
        selected.Entries(ComponentKind.OptiFabric) = New ComponentEntry With {.Kind = ComponentKind.OptiFabric, .Minecraft = "1.20.1", .Version = "1.14.3"}
        selected.Validate("1.20.1")
        verify(True, "compatible Fabric OptiFine OptiFabric combination accepted")
        Dim basis As New JObject From {{"mainClass", "vanilla.Main"}, {"minecraftArguments", "--username ${auth_player_name}"}, {"libraries", New JArray(New JObject From {{"name", "example:library:1"}})}}
        Dim extra As New JObject From {{"mainClass", "loader.Main"}, {"inheritsFrom", "1.20.1"}, {"libraries", New JArray(New JObject From {{"name", "example:library:2"}})}, {"arguments", New JObject From {{"game", New JArray("--tweakClass", "loader.Tweaker")}}}}
        Dim merged = ComponentInstaller.MergeProfile(basis, extra)
        verify(merged("inheritsFrom") Is Nothing AndAlso CStr(merged("mainClass")) = "loader.Main" AndAlso CStr(merged("minecraftArguments")) = CStr(basis("minecraftArguments")) AndAlso JToken.DeepEquals(merged.SelectToken("arguments.game"), extra.SelectToken("arguments.game")), "PCL mixed argument formats preserve old vanilla arguments and the structured loader tweak")
        verify(merged("libraries").Count() = 1 AndAlso CStr(merged("libraries")(0)("name")) = "example:library:2" AndAlso CStr(basis("libraries")(0)("name")) = "example:library:1", "loader library version replaces old coordinate without mutating input")
        verify(JavaRuntime.Quote("C:\中文 path\").EndsWith("\\""", StringComparison.Ordinal) AndAlso JavaRuntime.Quote("a""b") = """a\""b""", "Windows Java argument quoting handles quotes and trailing slashes")
        Dim variables = ComponentInstaller.ProcessorVariables(JObject.Parse("{""minecraft"":""1.21.1"",""data"":{""BINPATCH"":{""client"":""/data/client.lzma""},""MAPPINGS"":{""client"":""[a:b:1:mappings@txt]""}}}"), testRoot, "installer.jar", "client.jar")
        verify(variables("BINPATCH").EndsWith("data\data\client.lzma", StringComparison.Ordinal) AndAlso ComponentInstaller.Resolve("{SIDE}", variables, testRoot) = "client" AndAlso variables("MAPPINGS").EndsWith("b-1-mappings.txt", StringComparison.Ordinal), "Forge processor variables resolve client data and Maven mappings")
        Dim unsafeRejected = False
        Try : ComponentInstaller.Resolve("[a:../../escape:1]", variables, testRoot) : Catch ex As InvalidDataException : unsafeRejected = True : End Try
        verify(unsafeRejected, "unsafe processor Maven destinations rejected")
        Dim mirror As New SourcePolicy
        verify(mirror.Candidates("https://dl.liteloader.com/versions/com/mumfrey/liteloader/1/loader.jar", "library")(0) = "https://bmclapi2.bangbang93.com/maven/com/mumfrey/liteloader/1/loader.jar", "PCL LiteLoader library mirror does not duplicate Maven group")
        verify(mirror.Candidates("http://libraries.minecraft.net/example/a.jar", "library").All(Function(u) u.StartsWith("https://", StringComparison.Ordinal)), "legacy Maven HTTP links are upgraded to HTTPS")
        Dim optPage = ComponentCatalog.ParseOptiFinePage("<td class='colForge'>Forge 47.2.18</td><a href=""adloadx?f=OptiFine_1.20.1_HD_U_I6.jar"">Mirror</a>")
        verify(CStr(optPage(0)("filename")) = "OptiFine_1.20.1_HD_U_I6.jar" AndAlso CStr(optPage(0)("forge")) = "Forge 47.2.18", "PCL official OptiFine HTML fallback retains compatibility requirement")
        Dim checksumFile = Path.Combine(testRoot, "md5-fixture.jar")
        File.WriteAllText(checksumFile, "abc", New UTF8Encoding(False))
        verify(SafeFiles.Matches(checksumFile, New DownloadFile With {.Md5 = "900150983cd24fb0d6963f7d28e17f72"}) AndAlso Not SafeFiles.Matches(checksumFile, New DownloadFile With {.Md5 = New String("0"c, 32)}), "legacy Forge MD5 metadata is checked before accepting a file")
        Dim invalidMod = False
        Try : ComponentInstaller.ReadFabricMod(checksumFile) : Catch ex As InvalidDataException : invalidMod = True : End Try
        verify(invalidMod, "public CurseForge file cannot be accepted as OptiFabric without JAR metadata")
        Using server As New TestServer()
            Dim router As New ComponentFixturePolicy(server.Url)
            Dim mc = "1.21.1"
            server.Files("/v2/versions/loader/1.21.1") = Utf8("[{""loader"":{""version"":""0.16.9"",""stable"":true}}]")
            server.Files("/forge/minecraft/1.21.1") = Utf8("[{""version"":""52.0.28"",""files"":[{""category"":""installer"",""format"":""jar""}]}]")
            server.Files("/api/maven/versions/releases/net/neoforged/neoforge") = Utf8("{""versions"":[""21.1.77"",""21.2.1"",""20.4.1-beta""]}")
            server.Files("/optifine/versionList") = Utf8("[{""mcversion"":""1.21.1"",""type"":""HD_U"",""patch"":""J2"",""filename"":""OptiFine.jar"",""forge"":""N/A""}]")
            server.Files("/versions/versions.json") = Utf8("{""versions"":{""1.12.2"":{""snapshots"":{""com.mumfrey:liteloader"":{""latest"":{""version"":""1.12.2-SNAPSHOT"",""stream"":""SNAPSHOT"",""tweakClass"":""com.mumfrey.liteloader.launch.LiteLoaderTweaker"",""libraries"":[]}}}}}}")
            Dim modList As New JArray(New JObject From {{"name", "Fixture mod"}, {"version_number", "1.0"}, {"version_type", "release"}, {"files", New JArray(New JObject From {{"url", server.Url & "/mod"}, {"filename", "fixture.jar"}, {"primary", True}, {"hashes", New JObject From {{"sha1", Hash(Utf8("mod"))}}}, {"size", 3}})}})
            server.Files("/v2/project/P7dR8mSH/version") = Utf8(modList.ToString())
            Dim curse As New JObject From {{"data", New JArray(New JObject From {{"id", 5025647}, {"projectId", 322385}, {"gameVersions", New JArray("1.20.1")}, {"fileName", "optifabric-1.14.3.jar"}, {"displayName", "OptiFabric-v1.14.3"}, {"fileLength", 3}, {"releaseType", 2}, {"hashes", New JArray(New JObject From {{"algo", 1}, {"value", Hash(Utf8("mod"))}})}})}}
            server.Files("/api/v1/mods/322385/files") = Utf8(curse.ToString())
            server.Files("/curseforge/v1/mods/322385/files") = Utf8(curse.ToString())
            Dim catalog As New ComponentCatalog(router)
            For Each kind As ComponentKind In [Enum].GetValues(GetType(ComponentKind))
                Dim game = If(kind = ComponentKind.LiteLoader, "1.12.2", If(kind = ComponentKind.OptiFabric, "1.20.1", mc))
                Dim choices = Await catalog.LoadAsync(kind, game, CancellationToken.None)
                verify(choices.Count = 1 AndAlso choices(0).Kind = kind, "catalogue parses " & kind.ToString() & " using PCL endpoints")
            Next
            Dim client = Utf8("fixture client")
            Dim index = Utf8("{""objects"":{}}")
            server.Files("/client") = client : server.Files("/index") = index : server.Files("/mod") = Utf8("mod")
            Dim metadata As New JObject From {{"id", mc}, {"type", "release"}, {"mainClass", "vanilla.Main"}, {"downloads", New JObject From {{"client", Artifact(server.Url & "/client", client)}}}, {"libraries", New JArray()}, {"assetIndex", New JObject From {{"id", "fixture"}, {"url", server.Url & "/index"}, {"sha1", Hash(index)}, {"size", index.Length}}}}
            server.Files("/version") = Utf8(metadata.ToString())
            Dim entry As New VersionEntry With {.Id = mc, .Url = server.Url & "/version", .Sha1 = Hash(server.Files("/version"))}
            Dim root = Path.Combine(testRoot, "component 中文 path", ".minecraft")
            server.Files("/v2/versions/loader/1.21.1/0.16.9/profile/json") = Utf8("{""inheritsFrom"":""1.21.1"",""mainClass"":""net.fabricmc.loader.impl.launch.knot.KnotClient"",""arguments"":{""game"":[""--fabric-test""]},""libraries"":[]}")
            Dim fabric = (Await catalog.LoadAsync(ComponentKind.Fabric, mc, CancellationToken.None))(0)
            Dim api = (Await catalog.LoadAsync(ComponentKind.FabricApi, mc, CancellationToken.None))(0)
            Dim selection As New ComponentSelection
            selection.Entries(ComponentKind.Fabric) = fabric : selection.Entries(ComponentKind.FabricApi) = api
            Dim installer As New MinecraftInstaller(router)
            Dim invalidJava As New ComponentSelection With {.JavaPath = Path.Combine(testRoot, "missing-java.exe")}
            invalidJava.Entries(ComponentKind.NeoForge) = New ComponentEntry With {.Kind = ComponentKind.NeoForge, .Minecraft = mc, .Version = "fixture", .Url = server.Url & "/forge-installer"}
            Dim refusedJava As Boolean
            Try
                Await installer.InstallAsync(entry, root, "missing-java", {}, Nothing, CancellationToken.None, components:=invalidJava)
            Catch ex As FileNotFoundException
                refusedJava = True
            End Try
            verify(refusedJava AndAlso Not server.Requests.ContainsKey("/client") AndAlso Not server.Requests.ContainsKey("/index") AndAlso Not Directory.Exists(Path.Combine(root, "versions", "missing-java")), "missing Java fails before asset index or game content download")
            selection.JavaPath = invalidJava.JavaPath
            Dim result = Await installer.InstallAsync(entry, root, "Fabric 测试", {}, Nothing, CancellationToken.None, components:=selection)
            Dim output = JObject.Parse(File.ReadAllText(Path.Combine(result.InstancePath, "Fabric 测试.json")))
            verify(CStr(output("mainClass")).Contains("KnotClient") AndAlso output("inheritsFrom") Is Nothing AndAlso File.Exists(Path.Combine(result.InstancePath, "mods", "fixture.jar")), "Fabric and hash-checked API publish together in isolated instance")
            verify(File.ReadAllText(Path.Combine(result.InstancePath, "PCL", "Setup.ini")).Contains("VersionArgumentIndieV2:True") AndAlso Not File.Exists(Path.Combine(root, "launcher_profiles.json")), "PCL isolation configured only inside new instance")
            verify(File.Exists(Path.Combine(result.InstancePath, "Fabric 测试.jar")), "Fabric install needs no Java even when saved runtime path is unavailable")
            Dim processor = ProcessorJar()
            server.Files("/forge-installer") = InstallerJar(mc, processor, False)
            Dim neo As New ComponentSelection
            neo.Entries(ComponentKind.NeoForge) = New ComponentEntry With {.Kind = ComponentKind.NeoForge, .Minecraft = mc, .Version = "fixture", .Url = server.Url & "/forge-installer"}
            Dim neoResult = Await installer.InstallAsync(entry, root, "NeoForge 处理器测试", {}, Nothing, CancellationToken.None, components:=neo)
            verify(File.ReadAllText(Path.Combine(root, "libraries", "fixture", "patched", "1", "patched-1.jar")) = "patched" AndAlso File.Exists(Path.Combine(neoResult.InstancePath, "NeoForge 处理器测试.json")), "Forge client processors execute with embedded JAR and validate outputs")
            server.Files("/forge-failed") = InstallerJar(mc, processor, True)
            neo.Entries(ComponentKind.NeoForge).Url = server.Url & "/forge-failed"
            Dim failed As Boolean
            Try : Await installer.InstallAsync(entry, root, "failed-loader", {}, Nothing, CancellationToken.None, components:=neo) : Catch ex As IOException : failed = True : End Try
            verify(failed AndAlso Not Directory.Exists(Path.Combine(root, "versions", "failed-loader")), "failed loader processor never publishes an instance")
            Dim java = JavaRuntime.Find(Nothing, 8)
            Dim javaWork = Path.Combine(testRoot, "cancel java") : Directory.CreateDirectory(javaWork)
            File.WriteAllBytes(Path.Combine(javaWork, "processor.jar"), processor)
            Using cancel As New CancellationTokenSource(500)
                Dim stopped As Boolean, watch = Diagnostics.Stopwatch.StartNew()
                Try : Await JavaRuntime.RunAsync(java, {"-cp", Path.Combine(javaWork, "processor.jar"), "FixtureProcessor", "--wait"}, javaWork, Nothing, cancel.Token) : Catch ex As OperationCanceledException : stopped = True : End Try
                verify(stopped AndAlso watch.ElapsedMilliseconds < 5000, "cancel terminates only owned Java processor and drains its streams")
            End Using
        End Using
        Return passed
    End Function
    Private Function Utf8(value As String) As Byte()
        Return Encoding.UTF8.GetBytes(value)
    End Function
    Private Function Hash(bytes As Byte()) As String
        Using sha = Security.Cryptography.SHA1.Create()
            Return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function
    Private Function Artifact(url As String, bytes As Byte()) As JObject
        Return New JObject From {{"url", url}, {"size", bytes.Length}, {"sha1", Hash(bytes)}}
    End Function
    Friend Function ProcessorJar() As Byte()
        Using buffer As New MemoryStream
            Using zip As New ZipArchive(buffer, ZipArchiveMode.Create, True)
                WriteEntry(zip, "META-INF/MANIFEST.MF", Utf8("Manifest-Version: 1.0" & vbCrLf & "Main-Class: FixtureProcessor" & vbCrLf & vbCrLf))
                Using input = GetType(ComponentFixturePolicy).Assembly.GetManifestResourceStream("FixtureProcessor.class"), output As New MemoryStream
                    input.CopyTo(output) : WriteEntry(zip, "FixtureProcessor.class", output.ToArray())
                End Using
            End Using
            Return buffer.ToArray()
        End Using
    End Function
    Private Function InstallerJar(mc As String, processor As Byte(), fail As Boolean) As Byte()
        Dim output = "{PATCHED}"
        Dim profile As New JObject From {{"minecraft", mc}, {"libraries", New JArray()}, {"data", New JObject From {{"PATCHED", New JObject From {{"client", "[fixture:patched:1]"}}}}}, {"processors", New JArray(New JObject From {{"sides", New JArray("server")}, {"jar", "fixture:processor:1"}, {"classpath", New JArray()}, {"args", New JArray("--fail")}}, New JObject From {{"sides", New JArray("client")}, {"jar", "fixture:processor:1"}, {"classpath", New JArray()}, {"args", If(fail, New JArray("--fail"), New JArray(output, "patched"))}, {"outputs", New JObject From {{output, Hash(Utf8("patched"))}}}})}}
        ' Force a cache miss so the failure check actually exercises Java's exit code.
        If fail Then profile("processors")(1)("outputs")(output) = Hash(Utf8("not-produced"))
        Dim version As New JObject From {{"mainClass", "fixture.Loader"}, {"inheritsFrom", mc}, {"libraries", New JArray(New JObject From {{"name", "fixture:patched:1"}, {"downloads", New JObject From {{"artifact", New JObject From {{"path", "fixture/patched/1/patched-1.jar"}, {"url", ""}, {"sha1", Hash(Utf8("patched"))}, {"size", 7}}}}}})}}
        Using buffer As New MemoryStream
            Using zip As New ZipArchive(buffer, ZipArchiveMode.Create, True)
                WriteEntry(zip, "install_profile.json", Utf8(profile.ToString()))
                WriteEntry(zip, "version.json", Utf8(version.ToString()))
                WriteEntry(zip, "maven/fixture/processor/1/processor-1.jar", processor)
            End Using
            Return buffer.ToArray()
        End Using
    End Function
    Private Sub WriteEntry(zip As ZipArchive, name As String, bytes As Byte())
        Using output = zip.CreateEntry(name).Open()
            output.Write(bytes, 0, bytes.Length)
        End Using
    End Sub
    Private Class ComponentFixturePolicy
        Inherits SourcePolicy
        Private ReadOnly _server As String
        Public Sub New(server As String)
            _server = server : AllowLoopback = True
        End Sub
        Protected Overrides Function MetadataUrl(source As String) As String
            Return _server & New Uri(source).AbsolutePath
        End Function
        Public Overrides Function Candidates(url As String, category As String) As List(Of String)
            Return New List(Of String) From {_server & New Uri(url).AbsolutePath}
        End Function
    End Class
End Module
