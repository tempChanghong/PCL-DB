Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module ForgeVersionChecks
    Friend Async Function RunAsync(testRoot As String) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(25))
            Dim mc = "1.8.9", jar = LegacyInstaller(mc), checksum = Hash(jar)
            server.Files("/net/minecraftforge/forge/maven-metadata.xml") = Utf8("<metadata><versioning><versions>" &
                "<version>1.8.9-11.15.1.2318-1.8.9</version><version>1.8.9-11.15.1.2318</version>" &
                "<version>1.8.9-11.15.1.1902-1.8.9</version><version>1.8.9-not-a-version</version>" &
                "<version>1.8.9-11.15.1.2200-../bad</version><version>1.7.10-10.13.4.1614-1.7.10</version>" &
                "</versions></versioning></metadata>")
            Dim officialPolicy As New FixturePolicy(server.Url, SourcePreference.OfficialFirst)
            Dim official = Await New ComponentCatalog(officialPolicy).LoadAsync(ComponentKind.Forge, mc, timeout.Token)
            Dim chosen = official.FirstOrDefault(Function(v) v.Version = "11.15.1.2318")
            Check(chosen IsNot Nothing, "official Forge display version excludes its Maven branch suffix", passed)
            Check(chosen.Url = InstallerUrl(mc, "11.15.1.2318-1.8.9"), "official Forge installer retains the branch in both Maven path and filename", passed)
            Dim opt As New ComponentEntry With {.Kind = ComponentKind.OptiFine, .Minecraft = mc, .RequiredForge = "11.15.1.2318"}
            Check(ComponentSelection.OptiFineFits(opt, chosen, mc), "a branched official Forge release matches the numeric OptiFine requirement", passed)
            Check(official.Select(Function(v) v.Version).SequenceEqual({"11.15.1.2318", "11.15.1.1902"}), "official Forge coordinates normalize, deduplicate and sort without admitting malformed or unrelated versions", passed)
            Dim historical = New JArray(MirrorEntry("11.15.1.2318", Nothing, checksum), MirrorEntry("11.15.1.1902", "wrong", checksum), MirrorEntry("11.15.1.1890", Nothing, checksum), MirrorEntry("11.15.1.2200", "../../bad", checksum))
            server.Files("/forge/minecraft/1.8.9") = Utf8(historical.ToString())
            Dim mirrorPolicy As New FixturePolicy(server.Url, SourcePreference.MirrorFirst)
            Dim mirror = Await New ComponentCatalog(mirrorPolicy).LoadAsync(ComponentKind.Forge, mc, timeout.Token)
            For Each version In {"11.15.1.2318", "11.15.1.1902", "11.15.1.1890"}
                Dim item = mirror.Single(Function(v) v.Version = version)
                Check(item.Url = InstallerUrl(mc, version & "-1.8.9") AndAlso item.Sha1 = checksum,
                      "PCL's historical Forge branch correction applies to " & version, passed)
            Next
            Check(official.All(Function(v) mirror.Any(Function(m) m.Version = v.Version AndAlso m.Url = v.Url)), "official and BMCLAPI Forge entries agree on display names and installer identity", passed)
            Dim old = New JArray(MirrorEntry("10.13.0.1299", Nothing), MirrorEntry("10.13.0.1300", Nothing), MirrorEntry("10.13.4.1614", "custom"), MirrorEntry("10.13.4.1558", ""))
            server.Files("/forge/minecraft/1.7.10") = Utf8(old.ToString())
            Dim oldList = Await New ComponentCatalog(mirrorPolicy).LoadAsync(ComponentKind.Forge, "1.7.10", timeout.Token)
            Check(oldList.Single(Function(v) v.Version = "10.13.0.1299").Url = InstallerUrl("1.7.10", "10.13.0.1299"), "Forge builds below 1300 keep their branchless Minecraft 1.7.10 coordinate", passed)
            Check(oldList.Single(Function(v) v.Version = "10.13.0.1300").Url = InstallerUrl("1.7.10", "10.13.0.1300-1.7.10"), "Forge build 1300 acquires PCL's missing Minecraft 1.7.10 branch", passed)
            Check(oldList.Single(Function(v) v.Version = "10.13.4.1614").Url = InstallerUrl("1.7.10", "10.13.4.1614-custom"), "an explicit non-special Forge branch remains part of its installer identity", passed)
            Check(oldList.Single(Function(v) v.Version = "10.13.4.1558").Url = InstallerUrl("1.7.10", "10.13.4.1558-1.7.10"), "an empty Forge branch follows the missing-branch rule instead of emitting a trailing dash", passed)
            server.Files("/forge/minecraft/1.21.1") = Utf8(New JArray(MirrorEntry("52.0.28", Nothing, New String("a"c, 32))).ToString())
            Dim modern = (Await New ComponentCatalog(mirrorPolicy).LoadAsync(ComponentKind.Forge, "1.21.1", timeout.Token)).Single()
            Check(modern.Url = InstallerUrl("1.21.1", "52.0.28") AndAlso modern.Md5 = New String("a"c, 32) AndAlso modern.Sha1 Is Nothing, "modern branchless Forge coordinates preserve MD5 metadata without fabricating a SHA1", passed)
            server.Files("/forge/minecraft/1.7.10_pre4") = Utf8(New JArray(MirrorEntry("10.13.0.1199", Nothing)).ToString())
            Dim pre = (Await New ComponentCatalog(mirrorPolicy).LoadAsync(ComponentKind.Forge, "1.7.10-pre4", timeout.Token)).Single()
            Check(pre.Minecraft = "1.7.10-pre4" AndAlso pre.Url = InstallerUrl("1.7.10_pre4", "10.13.0.1199"), "Forge pre-release metadata keeps the Minecraft identity while using PCL's underscore coordinate", passed)
            Check(mirror.Count = 3, "invalid Forge branches cannot enter the catalogue or fabricate a Maven path", passed)
            Dim path = New Uri(chosen.Url).AbsolutePath
            server.Files(path) = jar
            Dim client = Utf8("fixture vanilla"), index = Utf8("{""objects"":{}}")
            server.Files("/client") = client : server.Files("/index") = index
            Dim metadata As New JObject From {{"id", mc}, {"type", "release"}, {"mainClass", "vanilla.Main"}, {"libraries", New JArray()},
                {"downloads", New JObject From {{"client", Artifact(server.Url & "/client", client)}}},
                {"assetIndex", New JObject From {{"id", "fixture"}, {"url", server.Url & "/index"}, {"sha1", Hash(index)}, {"size", index.Length}}}}
            server.Files("/version") = Utf8(metadata.ToString())
            Dim entry As New VersionEntry With {.Id = mc, .Url = server.Url & "/version", .Sha1 = Hash(server.Files("/version"))}
            Dim selection As New ComponentSelection
            selection.Entries(ComponentKind.Forge) = mirror.Single(Function(v) v.Version = chosen.Version)
            Dim root = System.IO.Path.Combine(testRoot, "Forge branch 中文", ".minecraft")
            Dim result = Await New MinecraftInstaller(mirrorPolicy).InstallAsync(entry, root, "Forge branch", {}, Nothing, timeout.Token, components:=selection)
            Dim output = JObject.Parse(File.ReadAllText(System.IO.Path.Combine(result.InstancePath, "Forge branch.json")))
            Check(server.Requests.ContainsKey(path) AndAlso Not server.Requests.ContainsKey(New Uri(InstallerUrl(mc, chosen.Version)).AbsolutePath) AndAlso CStr(output("mainClass")) = "fixture.Forge",
                  "the actual installer downloads the corrected branch URL and publishes the merged legacy profile", passed)
            Check(File.ReadAllText(System.IO.Path.Combine(root, "libraries", "fixture", "forge", "1", "forge-1.jar")) = "bundled Forge fixture" AndAlso
                  File.ReadAllText(System.IO.Path.Combine(result.InstancePath, "PCL", "Setup.ini")).Contains("VersionArgumentIndieV2:True"),
                  "the branch-selected legacy installer extracts its bundled runtime and enables instance isolation", passed)
            server.Files(path) = Utf8("corrupt installer")
            Dim failed As Boolean, badRoot = System.IO.Path.Combine(testRoot, "Forge bad hash", ".minecraft")
            Try
                Await New MinecraftInstaller(mirrorPolicy).InstallAsync(entry, badRoot, "bad hash", {}, Nothing, timeout.Token, components:=selection)
            Catch ex As IOException
                failed = True
            End Try
            Check(failed AndAlso Not Directory.Exists(System.IO.Path.Combine(badRoot, "versions", "bad hash")), "a corrected Forge coordinate still requires the declared installer checksum before publication", passed)
        End Using
        Return passed
    End Function

    Private Sub Check(condition As Boolean, label As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub
    Private Function Utf8(value As String) As Byte()
        Return Encoding.UTF8.GetBytes(value)
    End Function
    Private Function Hash(value As Byte()) As String
        Using sha = Security.Cryptography.SHA1.Create()
            Return BitConverter.ToString(sha.ComputeHash(value)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function
    Private Function Artifact(url As String, value As Byte()) As JObject
        Return New JObject From {{"url", url}, {"sha1", Hash(value)}, {"size", value.Length}}
    End Function
    Private Function InstallerUrl(mc As String, fileVersion As String) As String
        Dim coordinate = mc & "-" & fileVersion
        Return "https://maven.minecraftforge.net/net/minecraftforge/forge/" & coordinate & "/forge-" & coordinate & "-installer.jar"
    End Function
    Private Function MirrorEntry(version As String, branch As String, Optional checksum As String = Nothing) As JObject
        Return New JObject From {{"version", version}, {"branch", branch}, {"files", New JArray(New JObject From {{"category", "installer"}, {"format", "jar"}, {"hash", checksum}})}}
    End Function
    Friend Function LegacyInstaller(mc As String) As Byte()
        Dim runtime = Utf8("bundled Forge fixture")
        Dim library As New JObject From {{"name", "fixture:forge:1"}, {"downloads", New JObject From {{"artifact", New JObject From {
            {"path", "fixture/forge/1/forge-1.jar"}, {"url", ""}, {"sha1", Hash(runtime)}, {"size", runtime.Length}}}}}}
        Dim profile As New JObject From {{"minecraft", mc}, {"versionInfo", New JObject From {{"mainClass", "fixture.Forge"}, {"libraries", New JArray(library)}}},
            {"install", New JObject From {{"path", "fixture:forge:1"}, {"filePath", "forge-universal.jar"}}}}
        Using memory As New MemoryStream
            Using zip As New ZipArchive(memory, ZipArchiveMode.Create, True)
                Using output = zip.CreateEntry("install_profile.json").Open()
                    Dim bytes = Utf8(profile.ToString()) : output.Write(bytes, 0, bytes.Length)
                End Using
                Using output = zip.CreateEntry("forge-universal.jar").Open()
                    output.Write(runtime, 0, runtime.Length)
                End Using
            End Using
            Return memory.ToArray()
        End Using
    End Function
    Private Class FixturePolicy
        Inherits SourcePolicy
        Private ReadOnly _root As String
        Public Sub New(root As String, preference As SourcePreference)
            _root = root : Me.Preference = preference : VersionPreference = preference : AllowLoopback = True
        End Sub
        Protected Overrides Function MetadataUrl(source As String) As String
            Return _root & New Uri(source).PathAndQuery
        End Function
        Public Overrides Function Candidates(url As String, category As String) As List(Of String)
            Return New List(Of String) From {_root & New Uri(url).PathAndQuery}
        End Function
    End Class
End Module
