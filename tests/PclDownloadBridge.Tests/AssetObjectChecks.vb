Imports System.IO
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module AssetObjectChecks
    Private ReadOnly Content As Byte() = Encoding.UTF8.GetBytes("verified resource object")
    Private Function Hash(bytes As Byte()) As String
        Using sha = Security.Cryptography.SHA1.Create()
            Return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function
    Private Sub Check(value As Boolean, name As String, passed As List(Of String))
        If Not value Then Throw New InvalidOperationException(name)
        passed.Add(name)
    End Sub
    Private Function Index(Optional legacy As Boolean = False) As JObject
        Return New JObject From {{"virtual", legacy}, {"map_to_resources", legacy}, {"objects", New JObject From {
            {"sounds/微风.ogg", New JObject From {{"hash", Hash(Content).ToUpperInvariant()}, {"size", Content.Length}}}}}}
    End Function
    Private Sub Serve(fixture As InstallationChecks.InstallFixture, index As JObject)
        Dim bytes = Encoding.UTF8.GetBytes(index.ToString())
        fixture.Server.Files("/index") = bytes
        Dim meta = JObject.Parse(Encoding.UTF8.GetString(fixture.Server.Files("/version")))
        meta("assetIndex")("sha1") = Hash(bytes) : meta("assetIndex")("size") = bytes.Length
        fixture.Server.Files("/version") = Encoding.UTF8.GetBytes(meta.ToString())
        fixture.Entry.Sha1 = Hash(fixture.Server.Files("/version"))
    End Sub
    Private Class FixturePolicy
        Inherits SourcePolicy
        Private ReadOnly _server As String
        Friend Sub New(server As String, preference As SourcePreference)
            _server = server : AllowLoopback = True : Me.Preference = preference
        End Sub
        Public Overrides Function Candidates(url As String, category As String) As List(Of String)
            Return MyBase.Candidates(url, category).Select(Function(source)
                Dim uri As New Uri(source)
                If uri.Host = "resources.download.minecraft.net" Then Return _server & "/official" & uri.AbsolutePath
                If uri.Host.Contains("bmclapi") AndAlso uri.AbsolutePath.StartsWith("/assets/", StringComparison.Ordinal) Then Return _server & "/mirror" & uri.AbsolutePath.Substring(7)
                Return _server & uri.AbsolutePath
            End Function).ToList()
        End Function
    End Class

    Friend Async Function RunAsync(root As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        If onlyCase = "plan-asset-objects" Then onlyCase = Nothing
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "plan-asset-address" Then
            Await AddressAsync(root, passed)
            Await ReuseAsync(root, passed)
        End If
        If onlyCase Is Nothing OrElse onlyCase = "plan-asset-schema" Then
            SchemaChecks(passed)
            Await InvalidInstallAsync(root, passed)
        End If
        If passed.Count = 0 Then Throw New ArgumentException(onlyCase)
        Return passed
    End Function

    Private Async Function AddressAsync(root As String, passed As List(Of String)) As Task
        Dim digest = Hash(Content), route = "/" & digest.Substring(0, 2) & "/" & digest
        Dim corrupt = DirectCast(Content.Clone(), Byte()) : corrupt(0) = corrupt(0) Xor CByte(1)
        For Each preference As SourcePreference In [Enum].GetValues(GetType(SourcePreference))
            Using fixture As New InstallationChecks.InstallFixture(root, "asset-case-" & preference.ToString())
                Serve(fixture, Index())
                Dim preferred = If(preference = SourcePreference.OfficialFirst, "/official", "/mirror") & route
                Dim alternate = If(preference = SourcePreference.OfficialFirst, "/mirror", "/official") & route
                fixture.Server.Files(preferred) = corrupt : fixture.Server.Files(alternate) = Content
                Dim installer As New MinecraftInstaller(New FixturePolicy(fixture.Server.Url, preference))
                Dim result As InstallResult
                Try
                    result = Await installer.InstallAsync(fixture.Entry, fixture.Root, "case", {}, Nothing, CancellationToken.None)
                Catch
                    Console.WriteLine("ASSET_CASE preference=" & preference.ToString() & " requests=" & String.Join("|", fixture.Server.Requests.Keys.Where(Function(path) path.StartsWith("/official/", StringComparison.Ordinal) OrElse path.StartsWith("/mirror/", StringComparison.Ordinal))))
                    Throw
                End Try
                Dim target = Path.Combine(fixture.Root, "assets", "objects", digest.Substring(0, 2), digest)
                Check(File.ReadAllBytes(target).SequenceEqual(Content) AndAlso File.Exists(Path.Combine(result.InstancePath, "case.jar")), "uppercase resource digest installs through canonical object URLs: " & preference.ToString(), passed)
                Check(fixture.Server.Requests(preferred) = 2 AndAlso fixture.Server.Requests(alternate) = 1, "resource checksum failure follows the selected source order and recovers: " & preference.ToString(), passed)
                Dim jsonPath = Path.Combine(result.InstancePath, "case.json"), jsonBytes = File.ReadAllBytes(jsonPath)
                Dim goodRequests = fixture.Server.Requests(alternate)
                File.WriteAllBytes(target, corrupt)
                Await installer.InstallAsync(fixture.Entry, fixture.Root, "case", {}, Nothing, CancellationToken.None, repairExisting:=True)
                Check(File.ReadAllBytes(target).SequenceEqual(Content) AndAlso fixture.Server.Requests(alternate) = goodRequests + 1 AndAlso File.ReadAllBytes(jsonPath).SequenceEqual(jsonBytes), "same-size damaged resource is repaired without rewriting instance JSON: " & preference.ToString(), passed)
            End Using
        Next
    End Function

    Private Async Function ReuseAsync(root As String, passed As List(Of String)) As Task
        Dim assetIndex = Index(True), before = assetIndex.ToString(), digest = Hash(Content)
        Dim plan = MinecraftInstaller.BuildAssetsPlan(assetIndex)
        Check(plan.Count = 3 AndAlso plan.All(Function(item) item.Sha1 = digest AndAlso item.Url.EndsWith("/" & digest.Substring(0, 2) & "/" & digest, StringComparison.Ordinal)) AndAlso
            plan.Any(Function(item) item.RelativePath = "assets/virtual/legacy/sounds/微风.ogg") AndAlso plan.Any(Function(item) item.RelativePath = "resources/sounds/微风.ogg"), "resource object and legacy aliases share canonical hashes while retaining logical names", passed)
        Check(assetIndex.ToString() = before, "resource normalization leaves verified index JSON unchanged", passed)
        Dim donor = Path.Combine(root, "asset-donor"), target = Path.Combine(root, "asset-reuse")
        Dim objectPath = SafeFiles.UnderRoot(donor, "assets/objects/" & digest.Substring(0, 2) & "/" & digest)
        Directory.CreateDirectory(Path.GetDirectoryName(objectPath)) : File.WriteAllBytes(objectPath, Content)
        Using server As New TestServer(), engine As New DownloadEngine(New FixturePolicy(server.Url, SourcePreference.Automatic), Nothing, {donor}, cacheRoot:=target)
            Await engine.DownloadAsync(target, plan, CancellationToken.None)
            Check(engine.Reused = 3 AndAlso server.Requests.Count = 0 AndAlso plan.All(Function(item) File.ReadAllBytes(SafeFiles.UnderRoot(target, item.RelativePath)).SequenceEqual(Content)), "uppercase resource declarations reuse the existing object and every alias without HTTP", passed)
        End Using
        Dim corrupt = DirectCast(Content.Clone(), Byte()) : corrupt(0) = corrupt(0) Xor CByte(1)
        File.WriteAllBytes(objectPath, corrupt)
        target = Path.Combine(root, "asset-donor-corrupt")
        Using server As New TestServer(), engine As New DownloadEngine(New FixturePolicy(server.Url, SourcePreference.Automatic), Nothing, {donor}, cacheRoot:=target)
            Dim route = "/mirror/" & digest.Substring(0, 2) & "/" & digest
            server.Files(route) = Content
            Dim objectPlan = MinecraftInstaller.BuildAssetsPlan(Index())
            Await engine.DownloadAsync(target, objectPlan, CancellationToken.None)
            Check(engine.Reused = 0 AndAlso server.Requests(route) = 1 AndAlso File.ReadAllBytes(SafeFiles.UnderRoot(target, objectPlan(0).RelativePath)).SequenceEqual(Content), "same-size corrupt donor assets fall back to a verified network object", passed)
            Check(File.ReadAllBytes(objectPath).SequenceEqual(corrupt), "resource fallback does not alter the donor game folder", passed)
        End Using
    End Function

    Private Sub SchemaChecks(passed As List(Of String))
        Dim invalid As New Dictionary(Of String, JObject) From {
            {"null index", Nothing},
            {"missing objects", New JObject()}, {"array objects", New JObject From {{"objects", New JArray()}}},
            {"scalar object", New JObject From {{"objects", New JObject From {{"bad.ogg", True}}}}}}
        For Each size As JToken In {JValue.CreateNull(), New JValue("24"), New JValue(1.5), New JValue(-1), New JValue(True), JToken.Parse("9223372036854775808")}
            Dim item = Index() : item("objects")("sounds/微风.ogg")("size") = size
            invalid.Add("invalid size " & size.Type.ToString() & " " & size.ToString(), item)
        Next
        Dim missingSize = Index() : DirectCast(missingSize("objects")("sounds/微风.ogg"), JObject).Remove("size") : invalid.Add("missing size", missingSize)
        Dim badHash = Index() : badHash("objects")("sounds/微风.ogg")("hash") = New JArray() : invalid.Add("non-string hash", badHash)
        For Each pair In invalid
            Dim rejected = False
            Try
                MinecraftInstaller.BuildAssetsPlan(pair.Value)
            Catch ex As InvalidDataException
                rejected = True
            End Try
            Check(rejected, "resource metadata rejects malformed declarations before transfer: " & pair.Key, passed)
        Next
        Dim valid = Index() : valid("objects")("sounds/微风.ogg")("size") = 0
        Check(MinecraftInstaller.BuildAssetsPlan(valid).Single().Size = 0, "resource validation retains legitimate zero-byte declarations", passed)
        valid("objects")("sounds/微风.ogg")("size") = Long.MaxValue
        Check(MinecraftInstaller.BuildAssetsPlan(valid).Single().Size = Long.MaxValue, "resource validation preserves full Int64 file sizes", passed)
    End Sub

    Private Async Function InvalidInstallAsync(root As String, passed As List(Of String)) As Task
        For Each size As JToken In {New JValue(-1), New JValue(Content.Length + 0.1), New JValue(Content.Length.ToString())}
            Using fixture As New InstallationChecks.InstallFixture(root, "asset-invalid-install-" & passed.Count)
                Dim assetIndex = Index() : assetIndex("objects")("sounds/微风.ogg")("hash") = Hash(Content)
                assetIndex("objects")("sounds/微风.ogg")("size") = size : Serve(fixture, assetIndex)
                fixture.Server.Files("/" & Hash(Content).Substring(0, 2) & "/" & Hash(Content)) = Content
                Dim rejected = False
                Try
                    Await fixture.InstallAsync("invalid")
                Catch ex As InvalidDataException
                    rejected = True
                End Try
                Check(rejected AndAlso fixture.Server.Requests.Keys.All(Function(path) path = "/version" OrElse path = "/index") AndAlso Not Directory.Exists(Path.Combine(fixture.Root, "versions", "invalid")), "invalid resource sizes prevent all payload transfers and instance publication: " & size.Type.ToString(), passed)
            End Using
        Next
    End Function
End Module
