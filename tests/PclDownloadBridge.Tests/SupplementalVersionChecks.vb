Imports System.IO
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module SupplementalVersionChecks
    Private Const CombatId As String = "1_16_combat-6"
    Private Const ExperimentId As String = "1.18_experimental-snapshot-7"

    Private Function EntryJson(Optional id As String = CombatId) As JObject
        Dim canonical = If(id.StartsWith("1_", StringComparison.Ordinal), "1." & id.Substring(2), id)
        Dim slug = canonical.Replace("."c, "_"c)
        Return New JObject From {{"id", id}, {"type", "pending"}, {"url", "https://zkitefly.github.io/unlisted-versions-of-minecraft/files/" & slug & "/" & slug & ".json"}, {"releaseTime", "2021-04-01T12:00:00Z"}}
    End Function

    Private Function Manifest(ParamArray entries As JObject()) As JObject
        Return New JObject From {{"versions", New JArray(entries)}}
    End Function

    Private Sub Check(condition As Boolean, name As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(name)
        passed.Add(name)
    End Sub

    Private Class FixturePolicy
        Inherits SourcePolicy
        Private ReadOnly _server As String
        Friend Sub New(server As String)
            _server = server : AllowLoopback = True
        End Sub
        Protected Overrides Function MetadataUrl(source As String) As String
            If source = SupplementalVersions.ManifestUrl Then Return _server & "/supplement"
            If source.Contains("/mc/game/version_manifest_v2.json") Then Return _server & "/normal"
            If source.Contains("/unlisted-versions-of-minecraft/files/") Then Return _server & "/version"
            Return source
        End Function
        Public Overrides Function Candidates(url As String, category As String) As List(Of String)
            Return MyBase.Candidates(url, category).Select(Function(source) MetadataUrl(source)).Distinct().ToList()
        End Function
    End Class

    Friend Async Function RunAsync(root As String) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        ParseChecks(passed)
        Await ResponseRecoveryAsync(passed)
        Await OptionalSourceAsync(passed)
        Await InstallationAsync(root, passed)
        passed.AddRange(Await SupplementalAssetIndexChecks.RunAsync(root))
        Return passed
    End Function

    Private Sub ParseChecks(passed As List(Of String))
        Dim source = Manifest(EntryJson(), EntryJson(ExperimentId), EntryJson("1.19_deep_dark_experimental_snapshot-1"))
        Dim before = source.ToString()
        Dim parsed = SupplementalVersions.Parse(source)
        Check(parsed.Count = 3 AndAlso parsed(0).Id = "1.16_combat-6" AndAlso parsed(0).SupplementalId = CombatId, "supplement aliases retain the catalogue identity and use the verified metadata ID", passed)
        Check(parsed.All(Function(entry) entry.IsSupplemental AndAlso entry.Sha1 Is Nothing), "supplement metadata without a published digest does not invent a SHA-1", passed)
        Check(parsed(0).Subtitle.Contains("战斗测试版") AndAlso parsed(1).Subtitle.Contains("实验快照") AndAlso parsed.All(Function(entry) entry.Subtitle.Contains("UVMC")), "supplement summaries describe category and public provenance", passed)
        Check(source.ToString() = before, "supplement parsing leaves upstream JSON unchanged", passed)
        For Each suffix In {"16_combat-0", "16_combat-1", "16_combat-2", "16_combat-3", "16_combat-4", "16_combat-5", "16_combat-6", "15_combat-1", "15_combat-6", "14_combat-0", "14_combat-3", "14_combat-212796"}
            Dim entries = SupplementalVersions.Parse(Manifest(EntryJson("1_" & suffix), EntryJson("1." & suffix)))
            Check(entries.Count = 1 AndAlso entries(0).Id = "1." & suffix, "supplement explicit combat alias " & suffix, passed)
        Next
        For number = 1 To 7
            Check(SupplementalVersions.Parse(Manifest(EntryJson("1.18_experimental-snapshot-" & number))).Count = 1, "supplement experimental snapshot " & number, passed)
        Next
        Dim mutations As New Dictionary(Of String, Action(Of JObject)) From {
            {"unknown alias", Sub(item) item("id") = "1_99_combat-6"},
            {"history out of scope", Sub(item) item("id") = "c0.0.12a_03"},
            {"numeric identity", Sub(item) item("id") = 123},
            {"official release type", Sub(item) item("type") = "release"},
            {"insecure metadata URL", Sub(item) item("url") = CStr(item("url")).Replace("https:", "http:")},
            {"foreign metadata host", Sub(item) item("url") = "https://example.org/version.json"},
            {"cross-version metadata URL", Sub(item) item("url") = EntryJson(ExperimentId)("url")},
            {"malformed metadata digest", Sub(item) item("sha1") = "incorrect"},
            {"explicit null digest", Sub(item) item("sha1") = JValue.CreateNull()},
            {"numeric metadata digest", Sub(item) item("sha1") = 123},
            {"invalid release date", Sub(item) item("releaseTime") = "yesterday"},
            {"object release date", Sub(item) item("releaseTime") = New JObject()}}
        For Each mutation In mutations
            Dim bad = EntryJson() : mutation.Value.Invoke(bad)
            Dim entries = SupplementalVersions.Parse(Manifest(bad, EntryJson(ExperimentId)))
            Check(entries.Count = 1 AndAlso entries(0).Id = ExperimentId, "supplement rejects " & mutation.Key & " without discarding a valid neighbor", passed)
        Next
        Dim withHash = EntryJson() : withHash("sha1") = New String("A"c, 40)
        Check(SupplementalVersions.Parse(Manifest(withHash)).Single().Sha1 = New String("A"c, 40), "supplement preserves an upstream metadata checksum when supplied", passed)
        Dim conflict = EntryJson("1.16_combat-6") : conflict("sha1") = New String("b"c, 40)
        Check(SupplementalVersions.Parse(Manifest(EntryJson(), conflict, EntryJson(ExperimentId))).Single().Id = ExperimentId, "supplement conflicting aliases cannot pick a checksum by list order", passed)
        Check(SupplementalVersions.Parse(Manifest(conflict, EntryJson())).Count = 0, "supplement conflict rejection is order independent", passed)
        For Each officialId In {"1.16_combat-6", CombatId, "1.16_COMBAT-6", "1_16_COMBAT-6"}
            Dim official As New VersionEntry With {.Id = officialId, .Sha1 = New String("c"c, 40), .Kind = "snapshot"}
            Dim merged = SupplementalVersions.Merge({official}, parsed)
            Check(merged.Count = 3 AndAlso merged(0) Is official AndAlso merged.Where(Function(entry) entry.Id.IndexOf("combat", StringComparison.OrdinalIgnoreCase) >= 0).Count() = 1, "official declaration wins supplemental alias collision " & officialId, passed)
        Next
        Check(SupplementalVersions.Merge(parsed, parsed).Count = parsed.Count, "supplement repeated merge is idempotent", passed)
    End Sub

    Private Async Function ResponseRecoveryAsync(passed As List(Of String)) As Task
        Using server As New TestServer()
            Dim body = Manifest(EntryJson()).ToString()
            server.Files("/supplement") = Encoding.UTF8.GetBytes(body)
            For Each preference As SourcePreference In [Enum].GetValues(GetType(SourcePreference))
                Dim policy As New FixturePolicy(server.Url) With {.Preference = preference, .VersionPreference = preference}
                server.ContentTypes("/supplement") = "application/json; charset=invalid-pcl-db-charset"
                Dim failed = Await SupplementalVersions.LoadAsync(policy, CancellationToken.None)
                Check(failed.Count = 0, "supplement unsupported response charset is isolated " & preference.ToString(), passed)
                server.ContentTypes("/supplement") = "application/json; charset=utf-8"
                Dim recovered = Await SupplementalVersions.LoadAsync(policy, CancellationToken.None)
                Check(recovered.Count = 1 AndAlso recovered(0).Id = "1.16_combat-6", "supplement a valid refresh recovers after a response decoding failure " & preference.ToString(), passed)
            Next
            server.Files("/supplement") = Encoding.Unicode.GetBytes(body)
            server.ContentTypes("/supplement") = "application/json; charset=utf-16"
            Check((Await SupplementalVersions.LoadAsync(New FixturePolicy(server.Url), CancellationToken.None)).Count = 1, "supplement declared UTF-16 responses keep normal decoding support", passed)
        End Using
    End Function

    Private Async Function OptionalSourceAsync(passed As List(Of String)) As Task
        Using server As New TestServer()
            Dim normal As New JArray()
            For number = 1 To 200
                normal.Add(New JObject From {{"id", "normal-" & number}, {"type", "release"}, {"url", "https://example.org/version.json"}, {"sha1", New String("a"c, 40)}, {"releaseTime", "2026-01-01T00:00:00Z"}})
            Next
            server.Files("/normal") = Encoding.UTF8.GetBytes(New JObject From {{"versions", normal}}.ToString())
            For Each preference As SourcePreference In [Enum].GetValues(GetType(SourcePreference))
                Dim policy As New FixturePolicy(server.Url) With {.VersionPreference = preference}
                server.Files("/supplement") = Encoding.UTF8.GetBytes(Manifest(EntryJson()).ToString())
                Dim extra = Await SupplementalVersions.LoadAsync(policy, CancellationToken.None)
                Dim main = Await New MinecraftInstaller(policy).VersionsAsync(CancellationToken.None)
                Check(main.Count = 200 AndAlso SupplementalVersions.Merge(main, extra).Count = 201, "supplement independent merge with source preference " & preference.ToString(), passed)
                For Each bad In {"<html>unavailable</html>", "{}", "[]", "{""versions"":null}"}
                    server.Files("/supplement") = Encoding.UTF8.GetBytes(bad)
                    extra = Await SupplementalVersions.LoadAsync(policy, CancellationToken.None)
                    Check(extra.Count = 0 AndAlso (Await New MinecraftInstaller(policy).VersionsAsync(CancellationToken.None)).Count = 200, "supplement invalid response preserves normal downloads " & preference.ToString() & " " & bad, passed)
                Next
            Next
            Dim optionalPolicy As New FixturePolicy(server.Url)
            server.Files("/supplement") = Encoding.UTF8.GetBytes(Manifest(EntryJson()).ToString())
            server.StatusCodes("/supplement") = 503
            Check((Await SupplementalVersions.LoadAsync(optionalPolicy, CancellationToken.None)).Count = 0, "supplement HTTP failure returns an empty optional list", passed)
            server.StatusCodes("/supplement") = 200
            server.Files("/supplement") = Encoding.UTF8.GetBytes(New String(" "c, 1024 * 1024 + 1))
            Check((Await SupplementalVersions.LoadAsync(optionalPolicy, CancellationToken.None)).Count = 0, "supplement oversized catalogue remains an optional failure", passed)
            server.Files("/supplement") = Encoding.UTF8.GetBytes(Manifest(EntryJson()).ToString())
            Using gate As New ManualResetEventSlim(False)
                server.Gates("/supplement") = gate
                Dim watch = Diagnostics.Stopwatch.StartNew()
                Dim optionalWork = SupplementalVersions.LoadAsync(optionalPolicy, CancellationToken.None)
                Dim main = Await New MinecraftInstaller(optionalPolicy).VersionsAsync(CancellationToken.None)
                Check(main.Count = 200 AndAlso Not optionalWork.IsCompleted, "normal catalogue completes while supplemental headers are stalled", passed)
                Check((Await optionalWork).Count = 0 AndAlso watch.ElapsedMilliseconds < 8000, "supplement stalled headers terminate within their independent three second budget", passed)
                gate.Set()
            End Using
            DirectCast(normal(0), JObject).Remove("sha1")
            server.Files("/normal") = Encoding.UTF8.GetBytes(New JObject From {{"versions", normal}}.ToString())
            Dim rejectedNormal = False
            Try
                Await New MinecraftInstaller(optionalPolicy).VersionsAsync(CancellationToken.None)
            Catch ex As AggregateException
                rejectedNormal = True
            End Try
            Check(rejectedNormal, "normal manifest still requires its published SHA-1 on every entry", passed)
            Dim removed As ManualResetEventSlim = Nothing : server.Gates.TryRemove("/supplement", removed)
            Using gate As New ManualResetEventSlim(False), cancellation As New CancellationTokenSource()
                server.Files("/supplement") = Encoding.UTF8.GetBytes(Manifest(EntryJson()).ToString() & New String(" "c, 128 * 1024))
                server.BodyGates("/supplement") = gate : server.BodyPauseAt("/supplement") = 1
                Dim work = SupplementalVersions.LoadAsync(optionalPolicy, cancellation.Token)
                cancellation.CancelAfter(100)
                Dim cancelled = False
                Try
                    Await work
                Catch ex As OperationCanceledException
                    cancelled = True
                End Try
                Check(cancelled, "supplement caller cancellation propagates instead of becoming a successful empty refresh", passed)
                gate.Set()
            End Using
        End Using
    End Function

    Private Async Function InstallationAsync(root As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(root, "normal-after-supplement-failure")
            Dim policy As New FixturePolicy(fixture.Server.Url)
            Dim extra = Await SupplementalVersions.LoadAsync(policy, CancellationToken.None)
            Dim result = Await New MinecraftInstaller(policy).InstallAsync(fixture.Entry, fixture.Root, "normal", {}, Nothing, CancellationToken.None)
            Check(extra.Count = 0 AndAlso File.Exists(Path.Combine(result.InstancePath, "normal.jar")), "normal install succeeds after a supplemental HTTP failure", passed)
        End Using
        For Each rawId In {CombatId, ExperimentId}
            Dim entry = SupplementalVersions.Parse(Manifest(EntryJson(rawId))).Single()
            Using fixture As New InstallationChecks.InstallFixture(root, "supplement-" & rawId, entry.Id)
                Dim metadata = JObject.Parse(Encoding.UTF8.GetString(fixture.Server.Files("/version")))
                metadata("type") = "pending"
                fixture.Server.Files("/version") = Encoding.UTF8.GetBytes(metadata.ToString())
                Dim installer As New MinecraftInstaller(New FixturePolicy(fixture.Server.Url))
                Dim result = Await installer.InstallAsync(entry, fixture.Root, "alias-install", {}, Nothing, CancellationToken.None)
                Dim jsonPath = Path.Combine(result.InstancePath, "alias-install.json")
                Dim installed = JObject.Parse(File.ReadAllText(jsonPath))
                Check(CStr(installed("id")) = "alias-install" AndAlso CStr(installed("clientVersion")) = entry.Id AndAlso File.Exists(Path.Combine(result.InstancePath, "alias-install.jar")), "supplement installs canonical PCL clientVersion " & rawId, passed)
                Dim bytes = File.ReadAllBytes(jsonPath)
                Await installer.InstallAsync(entry, fixture.Root, "alias-install", {}, Nothing, CancellationToken.None, repairExisting:=True)
                Check(File.ReadAllBytes(jsonPath).SequenceEqual(bytes), "supplement repair preserves instance metadata " & rawId, passed)
                Dim selection = fixture.Fabric(), rejected = False
                Try
                    Await installer.InstallAsync(entry, fixture.Root & "-components", "loader", {}, Nothing, CancellationToken.None, components:=selection)
                Catch ex As InvalidOperationException
                    rejected = True
                End Try
                Check(rejected AndAlso Not Directory.Exists(fixture.Root & "-components"), "supplement rejects unverified component selection before creating folders " & rawId, passed)
            End Using
        Next
        Dim mutations As New Dictionary(Of String, Action(Of JObject)) From {
            {"unrelated metadata ID", Sub(meta) meta("id") = "1.16_combat-5"},
            {"unmapped underscore metadata ID", Sub(meta) meta("id") = CombatId},
            {"inherited metadata", Sub(meta) meta("inheritsFrom") = "1.16"},
            {"missing main class", Sub(meta) meta.Remove("mainClass")},
            {"wrong libraries shape", Sub(meta) meta("libraries") = New JObject()},
            {"missing client hash", Sub(meta) DirectCast(meta.SelectToken("downloads.client"), JObject).Remove("sha1")},
            {"missing index hash", Sub(meta) DirectCast(meta("assetIndex"), JObject).Remove("sha1")},
            {"missing client size", Sub(meta) DirectCast(meta.SelectToken("downloads.client"), JObject).Remove("size")},
            {"release masquerade", Sub(meta) meta("type") = "release"},
            {"redirected client jar", Sub(meta) meta("jar") = "other"}}
        For Each mutation In mutations
            Using fixture As New InstallationChecks.InstallFixture(root, "supplement-reject-" & passed.Count, "1.16_combat-6")
                Dim meta = JObject.Parse(Encoding.UTF8.GetString(fixture.Server.Files("/version")))
                meta("type") = "pending" : mutation.Value.Invoke(meta)
                fixture.Server.Files("/version") = Encoding.UTF8.GetBytes(meta.ToString())
                Dim rejected = False
                Try
                    Await New MinecraftInstaller(New FixturePolicy(fixture.Server.Url)).InstallAsync(SupplementalVersions.Parse(Manifest(EntryJson())).Single(), fixture.Root, "invalid", {}, Nothing, CancellationToken.None)
                Catch ex As InvalidDataException
                    rejected = True
                End Try
                Check(rejected AndAlso fixture.NonVersionRequests() = 0 AndAlso Not Directory.Exists(Path.Combine(fixture.Root, "versions", "invalid")), "supplement rejects " & mutation.Key & " before content downloads", passed)
            End Using
        Next
        Using fixture As New InstallationChecks.InstallFixture(root, "supplement-digests", "1.16_combat-6")
            Dim meta = JObject.Parse(Encoding.UTF8.GetString(fixture.Server.Files("/version"))) : meta("type") = "pending"
            fixture.Server.Files("/version") = Encoding.UTF8.GetBytes(meta.ToString())
            Dim entry = SupplementalVersions.Parse(Manifest(EntryJson())).Single()
            Dim installer As New MinecraftInstaller(New FixturePolicy(fixture.Server.Url))
            entry.Sha1 = New String("0"c, 40)
            Dim rejected = False
            Try
                Await installer.InstallAsync(entry, fixture.Root, "digest", {}, Nothing, CancellationToken.None)
            Catch ex As AggregateException
                rejected = True
            End Try
            Check(rejected AndAlso fixture.NonVersionRequests() = 0, "supplement declared metadata SHA-1 is enforced", passed)
            entry.Sha1 = Nothing
            Dim corrupt = DirectCast(fixture.Server.Files("/client").Clone(), Byte())
            corrupt(0) = corrupt(0) Xor CByte(1)
            fixture.Server.Files("/client") = corrupt
            rejected = False
            Try
                Await installer.InstallAsync(entry, fixture.Root, "digest", {}, Nothing, CancellationToken.None)
            Catch ex As Exception
                rejected = ex.ToString().Contains("哈希校验失败")
            End Try
            Check(rejected AndAlso fixture.Server.Requests.ContainsKey("/client") AndAlso Not Directory.Exists(Path.Combine(fixture.Root, "versions", "digest")), "supplement same-size corrupted client fails SHA-1 and cannot publish an instance", passed)
        End Using
    End Function

    Friend Async Function LiveAsync(directory As String) As Task
        IO.Directory.CreateDirectory(directory)
        Dim policy As New SourcePolicy()
        Dim entries = Await SupplementalVersions.LoadAsync(policy, CancellationToken.None)
        If entries.Count <> 20 Then Throw New InvalidDataException("Live UVMC catalogue changed or is unavailable: " & entries.Count)
        Dim records As New JArray()
        For Each entry In entries
            Dim meta = Await policy.GetJsonAsync(entry.Url, CancellationToken.None, entry.Sha1)
            SupplementalVersions.ValidateMetadata(entry, meta)
            Dim plan = MinecraftInstaller.BuildBasePlan(meta, "smoke")
            SupplementalVersions.ValidatePlan(plan)
            Dim index = DirectCast(meta("assetIndex"), JObject)
            Dim assets = Await policy.GetJsonAsync(CStr(index("url")), CancellationToken.None, CStr(index("sha1")))
            Dim assetPlan = MinecraftInstaller.BuildAssetsPlan(assets)
            SupplementalVersions.ValidatePlan(assetPlan)
            records.Add(New JObject From {{"catalogueId", entry.SupplementalId}, {"id", entry.Id}, {"files", plan.Count}, {"assets", assetPlan.Count}, {"metadataSha1Published", Not String.IsNullOrEmpty(entry.Sha1)}})
        Next
        File.WriteAllText(Path.Combine(directory, "live-core-smoke.json"), records.ToString())
        Console.WriteLine("PASS live supplemental metadata and hashed asset indexes: " & entries.Count & "; no game payload downloaded")
    End Function
End Module
