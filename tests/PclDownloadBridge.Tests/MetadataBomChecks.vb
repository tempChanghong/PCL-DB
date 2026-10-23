Imports System.IO
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module MetadataBomChecks
    Private Function Bom(body As String) As Byte()
        Return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(body)).ToArray()
    End Function
    Private Function Hash(bytes As Byte()) As String
        Using sha = Security.Cryptography.SHA1.Create()
            Return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function
    Private Sub Check(value As Boolean, name As String, passed As List(Of String))
        If Not value Then Throw New InvalidOperationException(name)
        passed.Add(name)
    End Sub

    Friend Async Function RunAsync(root As String) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(8))
            Dim policy As New SourcePolicy With {.AllowLoopback = True}
            server.Files("/bom") = Bom("{""name"":""版本 ✓""}")
            Dim result = Await policy.RaceJsonAsync({server.Url & "/bom", server.Url & "/unused"}, timeout.Token, Hash(server.Files("/bom")), fallbackDelay:=60000)
            Check(CStr(result("name")) = "版本 ✓", "metadata with a valid raw SHA-1 and a UTF-8 BOM parses without corrupting Unicode", passed)
            Check(server.Requests("/bom") = 1 AndAlso Not server.Requests.ContainsKey("/unused"), "valid BOM metadata wins without starting the deferred fallback", passed)
            server.Files("/array") = Bom("[1,2]")
            Check(TypeOf Await policy.GetTokenAsync(server.Url & "/array", timeout.Token) Is JArray, "generic metadata arrays accept a single leading UTF-8 BOM", passed)
            server.Files("/embedded") = Bom(New JObject From {{"name", "keep" & ChrW(&HFEFF) & "inside"}}.ToString())
            Check(CStr((Await policy.GetJsonAsync(server.Url & "/embedded", timeout.Token))("name")) = "keep" & ChrW(&HFEFF) & "inside", "metadata decoding preserves a BOM character inside a JSON string", passed)
            server.Files("/xml") = Bom("<versions><version>版本</version></versions>")
            Dim custom = Await policy.RaceVersionListTokenAsync({server.Url & "/xml"}, timeout.Token,
                Function(body) New JValue(Xml.Linq.XDocument.Parse(body).Root.Element("version").Value))
            Check(CStr(custom) = "版本", "catalogue-specific parsers also receive correctly decoded BOM metadata", passed)
        End Using

        Dim json = Encoding.UTF8.GetBytes("{""ok"":true}")
        Dim prefix = Encoding.UTF8.GetPreamble()
        Dim malformed As New Dictionary(Of String, Byte()) From {
            {"one-byte prefix", {CByte(&HEF)}.Concat(json).ToArray()},
            {"two-byte prefix", {CByte(&HEF), CByte(&HBB)}.Concat(json).ToArray()},
            {"double BOM", prefix.Concat(prefix).Concat(json).ToArray()},
            {"BOM after whitespace", Encoding.UTF8.GetBytes(" ").Concat(prefix).Concat(json).ToArray()}}
        For Each item In malformed
            Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(8))
                server.Files("/bad") = item.Value : server.Files("/good") = json
                Dim result = Await New SourcePolicy With {.AllowLoopback = True}.RaceJsonAsync({server.Url & "/bad", server.Url & "/good"}, timeout.Token, fallbackDelay:=60000)
                Check(CBool(result("ok")) AndAlso server.Requests("/bad") = 1 AndAlso server.Requests("/good") = 1, "malformed metadata prefix still triggers a validated fallback: " & item.Key, passed)
            End Using
        Next
        Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(8))
            Dim policy As New SourcePolicy With {.AllowLoopback = True}
            server.Files("/bom") = prefix.Concat(json).ToArray() : server.Files("/plain") = json
            Dim result = Await policy.RaceJsonAsync({server.Url & "/bom", server.Url & "/plain"}, timeout.Token, Hash(json), fallbackDelay:=60000)
            Check(CBool(result("ok")) AndAlso server.Requests.ContainsKey("/plain"), "metadata SHA-1 still includes the BOM bytes and rejects a digest of only the JSON text", passed)
            Dim rejected As AggregateException = Nothing
            Try
                Await policy.RaceJsonAsync({server.Url & "/bom"}, timeout.Token, Hash(json))
            Catch ex As AggregateException
                rejected = ex
            End Try
            Check(rejected IsNot Nothing AndAlso rejected.Flatten().InnerExceptions.All(Function(issue) TypeOf issue Is InvalidDataException AndAlso issue.Message.Contains("SHA-1")), "mismatched BOM metadata reports checksum failure before parsing", passed)
            server.Files("/array") = Bom("[]")
            result = Await policy.RaceJsonAsync({server.Url & "/array", server.Url & "/plain"}, timeout.Token, fallbackDelay:=60000)
            Check(CBool(result("ok")) AndAlso server.Requests("/array") = 1, "a BOM cannot bypass the JSON-object requirement for version metadata", passed)
        End Using
        Await ManifestsAsync(passed)
        Await InstallAsync(root, False, passed)
        Await InstallAsync(root, True, passed)
        Return passed
    End Function

    Private Class ManifestPolicy
        Inherits SourcePolicy
        Private ReadOnly _server As String
        Friend Sub New(server As String)
            _server = server : AllowLoopback = True
        End Sub
        Protected Overrides Function MetadataUrl(source As String) As String
            Return _server & If(New Uri(source).Host.Contains("bmclapi"), "/mirror", "/official")
        End Function
    End Class
    Private Async Function ManifestsAsync(passed As List(Of String)) As Task
        Dim versions As New JArray()
        For number = 1 To 200
            versions.Add(New JObject From {{"id", "fixture-" & number}, {"type", "release"}, {"url", "https://example.org/version.json"}, {"sha1", New String("a"c, 40)}, {"releaseTime", "2026-01-01T00:00:00Z"}})
        Next
        Dim body = New JObject From {{"versions", versions}}.ToString()
        For Each preference As SourcePreference In [Enum].GetValues(GetType(SourcePreference))
            Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(8))
                Dim preferred = If(preference = SourcePreference.MirrorFirst, "/mirror", "/official")
                server.Files("/official") = Encoding.UTF8.GetBytes(body) : server.Files("/mirror") = Encoding.UTF8.GetBytes(body)
                server.Files(preferred) = Bom(body)
                Dim entries = Await New MinecraftInstaller(New ManifestPolicy(server.Url) With {.VersionPreference = preference}).VersionsAsync(timeout.Token)
                Check(entries.Count = 200 AndAlso server.Requests.Count = 1 AndAlso server.Requests.ContainsKey(preferred), "BOM version manifests preserve the preferred source and ordinary entry checks: " & preference.ToString(), passed)
            End Using
        Next
    End Function

    Private Async Function InstallAsync(root As String, supplemental As Boolean, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(root, "metadata-bom-" & supplemental.ToString(), If(supplemental, "2.0_blue", "1.20.1"))
            Dim index = Encoding.UTF8.GetPreamble().Concat(fixture.Server.Files("/index")).ToArray()
            Dim meta = If(supplemental, SupplementalInstallFixture.Profile(fixture, "2.0_blue"), JObject.Parse(Encoding.UTF8.GetString(fixture.Server.Files("/version"))))
            meta("assetIndex")("sha1") = Hash(index) : meta("assetIndex")("size") = index.Length
            If supplemental Then
                meta("type") = "snapshot"
                fixture.Entry.SupplementalId = "2point0_blue"
                fixture.Entry.Kind = "snapshot"
                fixture.Entry.Url = "https://zkitefly.github.io/unlisted-versions-of-minecraft/files/2point0_blue/2point0_blue.json"
            End If
            fixture.Server.Files(New Uri(fixture.Entry.Url).AbsolutePath) = Bom(meta.ToString())
            fixture.Entry.Sha1 = Hash(Bom(meta.ToString()))
            fixture.Server.Files("/index") = index
            Dim result = Await fixture.InstallAsync("with-bom")
            Dim jsonPath = Path.Combine(result.InstancePath, "with-bom.json"), jsonBytes = File.ReadAllBytes(jsonPath)
            Dim installed = JObject.Parse(Encoding.UTF8.GetString(jsonBytes)), indexName = CStr(installed.SelectToken("assetIndex.id"))
            Dim indexPath = Path.Combine(fixture.Root, "assets", "indexes", indexName & ".json")
            Check(File.Exists(Path.Combine(result.InstancePath, "with-bom.jar")) AndAlso File.ReadAllBytes(indexPath).SequenceEqual(index) AndAlso
                indexName = If(supplemental, "pcldb-" & Hash(index), "fixture"), "actual install preserves BOM index bytes and correct index identity; supplemental=" & supplemental.ToString(), passed)
            File.WriteAllBytes(indexPath, Encoding.UTF8.GetBytes("broken index"))
            Await fixture.InstallAsync("with-bom", repair:=True)
            Check(File.ReadAllBytes(jsonPath).SequenceEqual(jsonBytes) AndAlso File.ReadAllBytes(indexPath).SequenceEqual(index), "BOM metadata repair restores the index and preserves instance JSON; supplemental=" & supplemental.ToString(), passed)
        End Using
    End Function
End Module
