Imports System.IO
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module SupplementalVersionChecks
    Private Const BlueId As String = "2point0_blue"
    Private Const RedId As String = "2point0_red"
    Private ReadOnly HistoricalIds As String() = {"c0.0.12a_03", "a1.1.1", "b1.9-pre6", "13w12~"}

    Private Function HistoryKind(id As String) As String
        If id = "c0.0.12a_03" OrElse id = "a1.1.1" Then Return "old_alpha"
        Return If(id = "b1.9-pre6", "old_beta", "snapshot")
    End Function

    Private Function EntryJson(Optional id As String = BlueId) As JObject
        Dim canonical = id.Replace("2point0_", "2.0_")
        Dim slug = canonical.Replace("2.0_", "2point0_")
        If id = "b1.9-pre6" Then slug = "b1_9-pre6"
        If id = "13w12~" Then slug = "13w12-"
        Return New JObject From {{"id", id}, {"type", HistoryKind(id)}, {"url", "https://zkitefly.github.io/unlisted-versions-of-minecraft/files/" & slug & "/" & slug & ".json"}, {"releaseTime", "2021-04-01T12:00:00Z"}}
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
            If source = SupplementalVersions.LibraryReferenceUrl Then Return _server & "/legacy-reference"
            If source = SupplementalVersions.ManifestUrl Then Return _server & "/supplement"
            If source.Contains("/mc/game/version_manifest_v2.json") Then Return _server & "/normal"
            If source.Contains("/unlisted-versions-of-minecraft/files/") Then Return _server & "/version"
            Return source
        End Function
        Public Overrides Function Candidates(url As String, category As String) As List(Of String)
            If url = SupplementalVersions.LibraryReferenceUrl Then Return New List(Of String) From {_server & "/legacy-reference"}
            Return MyBase.Candidates(url, category).Select(Function(source) MetadataUrl(source)).Distinct().ToList()
        End Function
    End Class

    Friend Async Function RunAsync(root As String) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        HistoricalParseChecks(passed)
        Await HistoricalProfilesAsync(passed)
        ParseChecks(passed)
        Await ResponseRecoveryAsync(passed)
        Await OptionalSourceAsync(passed)
        Await InstallationAsync(root, passed)
        Await HistoricalInstallationAsync(root, passed)
        Await LibraryCompletionAsync(root, passed)
        passed.AddRange(Await SupplementalAssetIndexChecks.RunAsync(root))
        Return passed
    End Function

    Private Sub HistoricalParseChecks(passed As List(Of String))
        For Each id In HistoricalIds
            Dim entries = SupplementalVersions.Parse(Manifest(EntryJson(id)))
            Check(entries.Count = 1 AndAlso entries(0).Id = id AndAlso entries(0).Kind = HistoryKind(id), "historical supplement keeps verified identity and classification: " & id, passed)
            Dim entry = entries.Single()
            Check(entry.Subtitle.Contains(If(id = "13w12~", "旧快照", "远古版")) AndAlso Not entry.Subtitle.Contains("愚人节"), "historical supplement is not mislabeled as April Fools: " & id, passed)
            Dim wrong = EntryJson(id) : wrong("type") = If(entry.Kind = "snapshot", "old_alpha", "snapshot")
            Check(SupplementalVersions.Parse(Manifest(wrong)).Count = 0, "historical supplement rejects wrong declared classification: " & id, passed)
            wrong = EntryJson(id) : wrong("url") = CStr(wrong("url")).Replace("/files/", "/files/../files/")
            Check(SupplementalVersions.Parse(Manifest(wrong)).Count = 0, "historical supplement requires its exact mapped URL: " & id, passed)
            Dim official As New VersionEntry With {.Id = id.ToUpperInvariant(), .Kind = HistoryKind(id)}
            Check(SupplementalVersions.Merge({official}, entries).Single() Is official, "official historical declaration wins case-insensitive collision: " & id, passed)
        Next
        Check(SupplementalVersions.Parse(Manifest(EntryJson("13w12-"), EntryJson("b1_9-pre6"), EntryJson("in-20100223"), EntryJson("inf-20100630-1"))).Count = 0, "unverified URL slugs and incomplete history stay outside the allowlist", passed)
    End Sub

    Private Async Function HistoricalProfilesAsync(passed As List(Of String)) As Task
        Using server As New TestServer()
            For Each id In HistoricalIds
                Dim metadata As JObject
                Using input = Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("Historical." & id & ".json"), reader As New StreamReader(input)
                    metadata = JObject.Parse(reader.ReadToEnd())
                End Using
                Dim entry = SupplementalVersions.Parse(Manifest(EntryJson(id))).Single(), original = metadata.ToString()
                Dim completed = Await SupplementalVersions.PrepareLibrariesAsync(entry, metadata, New FixturePolicy(server.Url), CancellationToken.None)
                Dim plan = MinecraftInstaller.BuildBasePlan(completed, "history")
                SupplementalVersions.ValidatePlan(plan)
                Check(metadata.ToString() = original AndAlso JToken.DeepEquals(completed, metadata) AndAlso plan.Count > 1 AndAlso server.Requests.Count = 0, "archived real historical profile produces a hashed Windows plan without reference fallback: " & id, passed)
                Using cancel As New CancellationTokenSource()
                    cancel.Cancel()
                    Dim cancelled = False
                    Try
                        Await SupplementalVersions.PrepareLibrariesAsync(entry, metadata, New FixturePolicy(server.Url), cancel.Token)
                    Catch ex As OperationCanceledException
                        cancelled = True
                    End Try
                    Check(cancelled, "complete historical profiles respect cancellation before cloning or fallback: " & id, passed)
                End Using
            Next
        End Using
    End Function

    Private Class CancelAtPublish
        Implements IProgress(Of TransferProgress)
        Private ReadOnly _cancel As CancellationTokenSource
        Friend Sub New(cancel As CancellationTokenSource)
            _cancel = cancel
        End Sub
        Public Sub Report(value As TransferProgress) Implements IProgress(Of TransferProgress).Report
            If value.Phase = "commit" Then _cancel.Cancel()
        End Sub
    End Class

    Private Async Function HistoricalInstallationAsync(root As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(root, "history-shared-root")
            Dim source = JObject.Parse(Encoding.UTF8.GetString(fixture.Server.Files("/version")))
            Dim normal = Await fixture.InstallAsync("ordinary")
            Dim ordinaryJson = Path.Combine(normal.InstancePath, "ordinary.json"), normalBytes = File.ReadAllBytes(ordinaryJson)
            Dim sharedIndex = Path.Combine(fixture.Root, "assets", "indexes", "fixture.json"), indexBytes = File.ReadAllBytes(sharedIndex)
            Dim installer As New MinecraftInstaller(New FixturePolicy(fixture.Server.Url))
            For Each id In HistoricalIds
                Dim entry = SupplementalVersions.Parse(Manifest(EntryJson(id))).Single()
                Dim metadata = DirectCast(source.DeepClone(), JObject)
                metadata("id") = id : metadata("type") = entry.Kind
                fixture.Server.Files("/version") = Encoding.UTF8.GetBytes(metadata.ToString())
                Using cancel As New CancellationTokenSource()
                    Dim cancelled = False
                    Try
                        Await installer.InstallAsync(entry, fixture.Root, id, {}, New CancelAtPublish(cancel), cancel.Token, isolationMode:=InstanceIsolationMode.Enabled)
                    Catch ex As OperationCanceledException
                        cancelled = True
                    End Try
                    Check(cancelled AndAlso Not Directory.Exists(Path.Combine(fixture.Root, "versions", id)) AndAlso File.ReadAllBytes(sharedIndex).SequenceEqual(indexBytes), "historical cancellation leaves no instance or shared-index damage: " & id, passed)
                End Using
                Dim installed = Await installer.InstallAsync(entry, fixture.Root, id, {}, Nothing, CancellationToken.None, isolationMode:=InstanceIsolationMode.Enabled)
                Dim jsonPath = Path.Combine(installed.InstancePath, id & ".json"), jsonBytes = File.ReadAllBytes(jsonPath)
                Dim iniPath = Path.Combine(installed.InstancePath, "PCL", "Setup.ini")
                Check(File.Exists(iniPath) AndAlso File.ReadAllText(iniPath).Contains("VersionArgumentIndie:1"), "historical retry publishes explicit isolation: " & id, passed)
                File.AppendAllText(iniPath, Environment.NewLine & "FixtureUserSetting:keep")
                Dim settingsBytes = File.ReadAllBytes(iniPath)
                Await installer.InstallAsync(entry, fixture.Root, id, {}, Nothing, CancellationToken.None, repairExisting:=True, isolationMode:=InstanceIsolationMode.Disabled)
                Check(File.ReadAllBytes(iniPath).SequenceEqual(settingsBytes) AndAlso File.ReadAllBytes(jsonPath).SequenceEqual(jsonBytes), "historical repair preserves existing isolation and JSON bytes: " & id, passed)
                Check(File.ReadAllBytes(sharedIndex).SequenceEqual(indexBytes) AndAlso File.ReadAllBytes(ordinaryJson).SequenceEqual(normalBytes), "historical shared-root install leaves ordinary index and instance intact: " & id, passed)
                For Each mutation In New Action(Of JObject)() {
                    Sub(meta) meta("type") = "release",
                    Sub(meta) DirectCast(meta("libraries"), JArray).Add(JValue.CreateNull()),
                    Sub(meta) meta("libraries") = New JArray(New JObject From {{"name", "fixture:missing:1"}})}
                    Dim bad = DirectCast(metadata.DeepClone(), JObject), rejected = False
                    mutation(bad)
                    Try
                        Await SupplementalVersions.PrepareLibrariesAsync(entry, bad, New FixturePolicy(fixture.Server.Url), CancellationToken.None)
                    Catch ex As InvalidDataException
                        rejected = True
                    End Try
                    Check(rejected AndAlso Not fixture.Server.Requests.ContainsKey("/legacy-reference"), "historical malformed profile cannot use 2.0 reference fallback: " & id & " #" & passed.Count, passed)
                Next
            Next
        End Using
    End Function

    Private Sub ParseChecks(passed As List(Of String))
        Dim source = Manifest(EntryJson(), EntryJson(RedId), EntryJson("2point0_purple"))
        Dim before = source.ToString()
        Dim parsed = SupplementalVersions.Parse(source)
        Check(parsed.Count = 3 AndAlso parsed(0).Id = "2.0_blue" AndAlso parsed(0).SupplementalId = BlueId, "supplement aliases retain the catalogue identity and use the verified metadata ID", passed)
        Check(parsed.All(Function(entry) entry.IsSupplemental AndAlso entry.Sha1 Is Nothing), "supplement metadata without a published digest does not invent a SHA-1", passed)
        Check(parsed.All(Function(entry) entry.Subtitle.Contains("愚人节版") AndAlso entry.Subtitle.Contains("UVMC")), "supplement summaries describe category and public provenance", passed)
        Check(source.ToString() = before, "supplement parsing leaves upstream JSON unchanged", passed)
        For Each color In {"blue", "red", "purple"}
            Dim entries = SupplementalVersions.Parse(Manifest(EntryJson("2point0_" & color), EntryJson("2.0_" & color)))
            Check(entries.Count = 1 AndAlso entries(0).Id = "2.0_" & color, "supplement explicit 2.0 color alias " & color, passed)
        Next
        For Each suffix In {"16_combat-0", "16_combat-1", "16_combat-2", "16_combat-3", "16_combat-4", "16_combat-5", "16_combat-6", "15_combat-1", "15_combat-6", "14_combat-0", "14_combat-3", "14_combat-212796"}
            Check(SupplementalVersions.Parse(Manifest(EntryJson("1_" & suffix), EntryJson("1." & suffix))).Count = 0, "removed combat versions stay outside the supplemental catalogue: " & suffix, passed)
        Next
        For number = 1 To 7
            Check(SupplementalVersions.Parse(Manifest(EntryJson("1.18_experimental-snapshot-" & number))).Count = 0, "removed experimental snapshot " & number, passed)
        Next
        Check(SupplementalVersions.Parse(Manifest(EntryJson("1.19_deep_dark_experimental_snapshot-1"), EntryJson("rd-132211"))).Count = 0, "removed deep dark snapshot and official rd version are not synthesized as supplemental entries", passed)
        Dim mutations As New Dictionary(Of String, Action(Of JObject)) From {
            {"unknown alias", Sub(item) item("id") = "1_99_combat-6"},
            {"history out of scope", Sub(item) item("id") = "in-20100223"},
            {"numeric identity", Sub(item) item("id") = 123},
            {"official release type", Sub(item) item("type") = "release"},
            {"insecure metadata URL", Sub(item) item("url") = CStr(item("url")).Replace("https:", "http:")},
            {"foreign metadata host", Sub(item) item("url") = "https://example.org/version.json"},
            {"cross-version metadata URL", Sub(item) item("url") = EntryJson(RedId)("url")},
            {"malformed metadata digest", Sub(item) item("sha1") = "incorrect"},
            {"explicit null digest", Sub(item) item("sha1") = JValue.CreateNull()},
            {"numeric metadata digest", Sub(item) item("sha1") = 123},
            {"invalid release date", Sub(item) item("releaseTime") = "yesterday"},
            {"object release date", Sub(item) item("releaseTime") = New JObject()}}
        For Each mutation In mutations
            Dim bad = EntryJson() : mutation.Value.Invoke(bad)
            Dim entries = SupplementalVersions.Parse(Manifest(bad, EntryJson(RedId)))
            Check(entries.Count = 1 AndAlso entries(0).Id = "2.0_red", "supplement rejects " & mutation.Key & " without discarding a valid neighbor", passed)
        Next
        Dim withHash = EntryJson() : withHash("sha1") = New String("A"c, 40)
        Check(SupplementalVersions.Parse(Manifest(withHash)).Single().Sha1 = New String("A"c, 40), "supplement preserves an upstream metadata checksum when supplied", passed)
        Dim conflict = EntryJson("2.0_blue") : conflict("sha1") = New String("b"c, 40)
        Check(SupplementalVersions.Parse(Manifest(EntryJson(), conflict, EntryJson(RedId))).Single().Id = "2.0_red", "supplement conflicting aliases cannot pick a checksum by list order", passed)
        Check(SupplementalVersions.Parse(Manifest(conflict, EntryJson())).Count = 0, "supplement conflict rejection is order independent", passed)
        For Each officialId In {"2.0_blue", BlueId, "2.0_BLUE", "2point0_BLUE"}
            Dim official As New VersionEntry With {.Id = officialId, .Sha1 = New String("c"c, 40), .Kind = "snapshot"}
            Dim merged = SupplementalVersions.Merge({official}, parsed)
            Check(merged.Count = 3 AndAlso merged(0) Is official AndAlso merged.Where(Function(entry) entry.Id.EndsWith("_blue", StringComparison.OrdinalIgnoreCase)).Count() = 1, "official declaration wins supplemental alias collision " & officialId, passed)
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
                Check(recovered.Count = 1 AndAlso recovered(0).Id = "2.0_blue", "supplement a valid refresh recovers after a response decoding failure " & preference.ToString(), passed)
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
        For Each rawId In {BlueId, RedId}.Concat(HistoricalIds)
            Dim entry = SupplementalVersions.Parse(Manifest(EntryJson(rawId))).Single()
            Using fixture As New InstallationChecks.InstallFixture(root, "supplement-" & rawId, entry.Id)
                Dim metadata = JObject.Parse(Encoding.UTF8.GetString(fixture.Server.Files("/version")))
                metadata("type") = HistoryKind(rawId)
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
            {"unrelated metadata ID", Sub(meta) meta("id") = "2.0_red"},
            {"unmapped underscore metadata ID", Sub(meta) meta("id") = BlueId},
            {"inherited metadata", Sub(meta) meta("inheritsFrom") = "1.16"},
            {"missing main class", Sub(meta) meta.Remove("mainClass")},
            {"wrong libraries shape", Sub(meta) meta("libraries") = New JObject()},
            {"missing client hash", Sub(meta) DirectCast(meta.SelectToken("downloads.client"), JObject).Remove("sha1")},
            {"missing index hash", Sub(meta) DirectCast(meta("assetIndex"), JObject).Remove("sha1")},
            {"missing client size", Sub(meta) DirectCast(meta.SelectToken("downloads.client"), JObject).Remove("size")},
            {"release masquerade", Sub(meta) meta("type") = "release"},
            {"redirected client jar", Sub(meta) meta("jar") = "other"}}
        For Each mutation In mutations
            Using fixture As New InstallationChecks.InstallFixture(root, "supplement-reject-" & passed.Count, "2.0_blue")
                Dim meta = JObject.Parse(Encoding.UTF8.GetString(fixture.Server.Files("/version")))
                meta("type") = "snapshot" : mutation.Value.Invoke(meta)
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
        Using fixture As New InstallationChecks.InstallFixture(root, "supplement-digests", "2.0_blue")
            Dim meta = JObject.Parse(Encoding.UTF8.GetString(fixture.Server.Files("/version"))) : meta("type") = "snapshot"
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

    Private Async Function LibraryCompletionAsync(root As String, passed As List(Of String)) As Task
        Dim bytes As Byte()
        Using input = Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("SupplementalLibraryReference.json"), output As New MemoryStream()
            input.CopyTo(output) : bytes = output.ToArray()
        End Using
        Dim reference = JObject.Parse(Encoding.UTF8.GetString(bytes))
        Using fixture As New InstallationChecks.InstallFixture(root, "supplement-legacy-library-reference", "2.0_blue")
            fixture.Server.Files("/legacy-reference") = bytes
            Dim metadata = JObject.Parse(Encoding.UTF8.GetString(fixture.Server.Files("/version")))
            metadata("type") = "snapshot" : metadata("libraries") = reference("libraries").DeepClone()
            For Each library In DirectCast(metadata("libraries"), JArray).OfType(Of JObject)()
                library.Remove("downloads")
            Next
            Dim original = metadata.ToString(), entry = SupplementalVersions.Parse(Manifest(EntryJson())).Single()
            Dim policy As New FixturePolicy(fixture.Server.Url)
            Dim completed = Await SupplementalVersions.PrepareLibrariesAsync(entry, metadata, policy, CancellationToken.None)
            Check(metadata.ToString() = original AndAlso JToken.DeepEquals(completed("downloads"), metadata("downloads")) AndAlso JToken.DeepEquals(completed("assetIndex"), metadata("assetIndex")), "2.0 library enrichment preserves source JSON client and asset index", passed)
            Check(DirectCast(completed("libraries"), JArray).All(Function(library) library("downloads") IsNot Nothing), "2.0 legacy Maven rules and natives receive official reference download declarations", passed)
            SupplementalVersions.ValidatePlan(MinecraftInstaller.BuildBasePlan(completed, "legacy"))
            Check(True, "2.0 completed Windows library plan retains mandatory SHA-1 and sizes", passed)
            For Each mutation In New Action(Of JObject)() {
                Sub(meta) meta("libraries")(0)("name") = "net.minecraft:launchwrapper:999",
                Sub(meta) meta("libraries")(0)("url") = "https://example.org/",
                Sub(meta) meta("libraries")(0)("rules") = New JArray(),
                Sub(meta) meta("libraries")(7)("natives")("windows") = "unexpected"}
                Dim bad = DirectCast(metadata.DeepClone(), JObject), rejected = False
                mutation(bad)
                Try
                    Await SupplementalVersions.PrepareLibrariesAsync(entry, bad, policy, CancellationToken.None)
                Catch ex As InvalidDataException
                    rejected = True
                End Try
                Check(rejected, "2.0 library reference cannot overwrite a changed Maven repository rule or native declaration: " & passed.Count, passed)
            Next
            Dim corrupt = DirectCast(bytes.Clone(), Byte()) : corrupt(corrupt.Length - 1) = corrupt(corrupt.Length - 1) Xor CByte(1)
            fixture.Server.Files("/legacy-reference") = corrupt
            Dim failed = False
            Try
                Await SupplementalVersions.PrepareLibrariesAsync(entry, metadata, policy, CancellationToken.None)
            Catch ex As AggregateException
                failed = True
            End Try
            Check(failed, "2.0 rejects a changed official library reference through its pinned SHA-1", passed)
            fixture.Server.Files("/legacy-reference") = bytes
            Dim recovered = Await SupplementalVersions.PrepareLibrariesAsync(entry, metadata, policy, CancellationToken.None)
            Check(JToken.DeepEquals(completed, recovered), "2.0 library reference can retry after checksum failure", passed)
            Using cancel As New CancellationTokenSource()
                cancel.Cancel() : failed = False
                Try
                    Await SupplementalVersions.PrepareLibrariesAsync(entry, metadata, policy, cancel.Token)
                Catch ex As OperationCanceledException
                    failed = True
                End Try
                Check(failed, "2.0 library reference respects installer cancellation", passed)
            End Using
        End Using
    End Function

    Friend Async Function LiveAsync(directory As String) As Task
        IO.Directory.CreateDirectory(directory)
        Dim policy As New SourcePolicy()
        Dim entries = Await SupplementalVersions.LoadAsync(policy, CancellationToken.None)
        If entries.Count <> 7 Then Throw New InvalidDataException("Live UVMC catalogue changed or is unavailable: " & entries.Count)
        Dim records As New JArray()
        Dim ordinary = Await New MinecraftInstaller(policy).VersionsAsync(CancellationToken.None)
        Dim rd = ordinary.Single(Function(entry) entry.Id = "rd-132211")
        If rd.IsSupplemental OrElse rd.Kind <> "old_alpha" Then Throw New InvalidDataException("Official rd-132211 identity changed.")
        records.Add(New JObject From {{"id", rd.Id}, {"source", "official"}, {"type", rd.Kind}})
        For Each entry In entries
            Dim meta = Await policy.GetJsonAsync(entry.Url, CancellationToken.None, entry.Sha1)
            SupplementalVersions.ValidateMetadata(entry, meta)
            meta = Await SupplementalVersions.PrepareLibrariesAsync(entry, meta, policy, CancellationToken.None)
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
