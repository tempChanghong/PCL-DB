Imports System.IO
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module EarlySupplementalChecks
    Private ReadOnly Ids As String() = {"1.0.0-rc1", "inf-20100630-1"}

    Private Sub Check(ok As Boolean, name As String, passed As List(Of String))
        If Not ok Then Throw New InvalidOperationException(name)
        passed.Add(name)
    End Sub

    Private Function Raw(id As String) As JObject
        Return JObject.Parse(Encoding.UTF8.GetString(SupplementalInstallFixture.Resource("Historical." & id & ".json")))
    End Function

    Private Class Policy
        Inherits SourcePolicy
        Private ReadOnly _server As String
        Friend Sub New(server As String)
            _server = server : AllowLoopback = True
        End Sub
        Public Overrides Function Candidates(url As String, category As String) As List(Of String)
            If url = SupplementalVersions.AlphaLibraryReferenceUrl Then Return New List(Of String) From {_server & "/reference"}
            If url = SupplementalVersions.LegacyAssetIndexUrl Then Return New List(Of String) From {_server & "/legacy-index"}
            If url.StartsWith("https://zkitefly.github.io/", StringComparison.Ordinal) Then Return New List(Of String) From {_server & "/version"}
            If url = "https://libraries.minecraft.net/net/minecraft/launchwrapper/1.6/launchwrapper-1.6.jar" Then Return New List(Of String) From {_server & "/fixture-wrapper/1.6.jar"}
            Return MyBase.Candidates(url, category)
        End Function
    End Class

    Private Class Progress
        Implements IProgress(Of TransferProgress)
        Private ReadOnly _action As Action(Of TransferProgress)
        Friend Sub New(action As Action(Of TransferProgress))
            _action = action
        End Sub
        Public Sub Report(value As TransferProgress) Implements IProgress(Of TransferProgress).Report
            _action(value)
        End Sub
    End Class

    Private Async Function Requested(server As TestServer, route As String) As Task
        Dim untilTime = DateTime.UtcNow.AddSeconds(10)
        Do While Not server.Requests.ContainsKey(route)
            If DateTime.UtcNow > untilTime Then Throw New TimeoutException("Expected request: " & route)
            Await Task.Delay(10)
        Loop
    End Function

    Friend Async Function RunAsync(root As String) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        Await ProfilesAsync(passed)
        For Each id In Ids
            Await LifecycleAsync(root, id, passed)
            Await FallbackFailureAsync(root, id, passed)
        Next
        Return passed
    End Function

    Private Async Function ProfilesAsync(passed As List(Of String)) As Task
        Using server As New TestServer()
            Dim reference = SupplementalInstallFixture.Resource("Historical.AlphaLibraryReference.json")
            server.Files("/reference") = reference
            Dim policy As New Policy(server.Url)
            For Each id In Ids
                Dim entry = SupplementalInstallFixture.Entry(id), source = Raw(id), original = source.ToString()
                Dim declaration As New JObject From {{"id", id}, {"type", entry.Kind}, {"url", entry.Url}, {"releaseTime", source("releaseTime").DeepClone()}}
                Dim parsed = SupplementalVersions.Parse(New JObject From {{"versions", New JArray(declaration)}}).Single()
                Check(parsed.IsSupplemental AndAlso parsed.Id = id AndAlso parsed.Kind = entry.Kind AndAlso parsed.Url = entry.Url, "early catalogue preserves canonical identity, source and category: " & id, passed)
                Dim official As New VersionEntry With {.Id = id.ToUpperInvariant()}
                Check(SupplementalVersions.Merge({official}, {parsed}).Single() Is official, "early supplemental identity cannot replace official entry: " & id, passed)
                For Each field In {"type", "url"}
                    Dim bad = DirectCast(declaration.DeepClone(), JObject) : bad(field) = "wrong"
                    Check(SupplementalVersions.Parse(New JObject From {{"versions", New JArray(bad)}}).Count = 0, "early catalogue rejects wrong " & field & ": " & id, passed)
                Next
                Dim prepared = Await SupplementalVersions.PrepareLibrariesAsync(entry, source, policy, CancellationToken.None)
                Check(source.ToString() = original AndAlso JToken.DeepEquals(source("downloads"), prepared("downloads")) AndAlso
                    JToken.DeepEquals(source("minecraftArguments"), prepared("minecraftArguments")), "early enrichment preserves archived client, arguments and source bytes: " & id, passed)
                Check(CStr(prepared.SelectToken("assetIndex.sha1")) = SupplementalVersions.LegacyAssetIndexSha1 AndAlso
                    CStr(prepared.SelectToken("assetIndex.url")) = SupplementalVersions.LegacyAssetIndexUrl AndAlso CLng(prepared.SelectToken("assetIndex.size")) = 134284 AndAlso
                    CLng(prepared.SelectToken("assetIndex.totalSize")) = 111220701 AndAlso CStr(prepared("assets")) = "legacy", "early missing index uses exact fixed PCL legacy declaration: " & id, passed)
                Dim plan = MinecraftInstaller.BuildBasePlan(prepared, "自定义 RC 实例")
                SupplementalVersions.ValidatePlan(plan)
                Check(plan.Any(Function(file) file.RelativePath.Contains("launchwrapper/1.6/")) AndAlso Not plan.Any(Function(file) file.RelativePath.Contains("launchwrapper/1.5/")), "early Windows plan uses hashed 1.6 libraries and native declarations: " & id, passed)
                For Each mutation In New Dictionary(Of String, Action(Of JObject)) From {
                    {"null index", Sub(meta) meta("assetIndex") = JValue.CreateNull()},
                    {"string index", Sub(meta) meta("assetIndex") = "legacy"},
                    {"empty index", Sub(meta) meta("assetIndex") = New JObject()},
                    {"assets only", Sub(meta) meta("assets") = "legacy"},
                    {"null assets", Sub(meta) meta("assets") = JValue.CreateNull()},
                    {"null downloads", Sub(meta) meta("libraries")(0)("downloads") = JValue.CreateNull()},
                    {"string downloads", Sub(meta) meta("libraries")(0)("downloads") = "missing"},
                    {"wrong main", Sub(meta) meta("mainClass") = "wrong.Main"},
                    {"wrong kind", Sub(meta) meta("type") = "snapshot"},
                    {"inherited", Sub(meta) meta("inheritsFrom") = "base"},
                    {"custom jar", Sub(meta) meta("jar") = "base"}}
                    Dim bad = DirectCast(source.DeepClone(), JObject), rejected = False, requests = server.Requests("/reference")
                    mutation.Value.Invoke(bad)
                    Try
                        Await SupplementalVersions.PrepareLibrariesAsync(entry, bad, policy, CancellationToken.None)
                    Catch ex As InvalidDataException
                        rejected = True
                    End Try
                    Check(rejected AndAlso requests = server.Requests("/reference") AndAlso source.ToString() = original, "early malformed declaration cannot trigger missing-field fallback: " & id & " " & mutation.Key, passed)
                Next
                For Each mutation In New Action(Of JObject)() {
                    Sub(meta) meta("libraries")(0)("name") = "net.minecraft:launchwrapper:1.5",
                    Sub(meta) meta("libraries")(0)("url") = "https://example.invalid/",
                    Sub(meta) meta("libraries")(7)("natives")("windows") = "other",
                    Sub(meta) meta("libraries")(0)("rules") = New JArray()}
                    Dim bad = DirectCast(source.DeepClone(), JObject), rejected = False
                    mutation(bad)
                    Try
                        Await SupplementalVersions.PrepareLibrariesAsync(entry, bad, policy, CancellationToken.None)
                    Catch ex As InvalidDataException
                        rejected = True
                    End Try
                    Check(rejected, "early reference requires full Maven rules/repository/native declaration match: " & id & " #" & passed.Count, passed)
                Next
                Dim supplied = DirectCast(prepared.DeepClone(), JObject)
                supplied("assetIndex")("id") = "pre-1.6" : supplied("assets") = "pre-1.6"
                Dim preserved = Await SupplementalVersions.PrepareLibrariesAsync(entry, supplied, policy, CancellationToken.None)
                Check(JToken.DeepEquals(supplied, preserved), "early valid supplied index never switches to fixed legacy fallback: " & id, passed)
            Next
        End Using
    End Function

    Private Async Function LifecycleAsync(root As String, id As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(root, "early-" & id)
            Dim ordinary = Await fixture.InstallAsync("ordinary"), ordinaryBytes = File.ReadAllBytes(Path.Combine(ordinary.InstancePath, "ordinary.json"))
            Dim indexPath = Path.Combine(fixture.Root, "assets/indexes/fixture.json"), indexBytes = File.ReadAllBytes(indexPath)
            Dim source = SupplementalInstallFixture.Profile(fixture, id)
            ' Distinct from the ordinary fixture's already verified client, so
            ' the transfer-cancellation test cannot silently reuse that JAR.
            Dim client = Encoding.UTF8.GetBytes("early fixture client " & id & New String("x"c, 8192))
            fixture.Server.Files("/client") = client
            source("downloads")("client")("size") = client.Length
            Using sha = Security.Cryptography.SHA1.Create()
                source("downloads")("client")("sha1") = BitConverter.ToString(sha.ComputeHash(client)).Replace("-", "").ToLowerInvariant()
            End Using
            DirectCast(source("libraries")(0), JObject).Remove("downloads")
            fixture.Server.Files("/version") = Encoding.UTF8.GetBytes(source.ToString())
            Dim reference = SupplementalInstallFixture.Resource("Historical.AlphaLibraryReference.json")
            fixture.Server.Files("/reference") = reference
            Dim selected = SupplementalInstallFixture.Entry(id), installer As New MinecraftInstaller(New Policy(fixture.Server.Url))
            Dim name = "自定义 " & id, target = Path.Combine(fixture.Root, "versions", name)
            For Each mode In {"reference-failure", "reference-cancel", "transfer-cancel", "publish-cancel"}
                Using cancel As New CancellationTokenSource(), gate As New ManualResetEventSlim(False)
                    Dim progress As IProgress(Of TransferProgress) = Nothing
                    If mode = "reference-failure" Then
                        Dim corrupt = DirectCast(reference.Clone(), Byte()) : corrupt(0) = corrupt(0) Xor CByte(1)
                        fixture.Server.Files("/reference") = corrupt
                    ElseIf mode = "reference-cancel" Then
                        fixture.Server.Requests.Clear() : fixture.Server.Gates("/reference") = gate
                    ElseIf mode = "transfer-cancel" Then
                        fixture.Server.Requests.Clear() : fixture.Server.BodyGates("/client") = gate : fixture.Server.BodyPauseAt("/client") = 0
                    Else
                        progress = New Progress(Sub(value)
                                                    If value.Phase = "commit" Then cancel.Cancel()
                                                End Sub)
                    End If
                    Dim work = installer.InstallAsync(selected, fixture.Root, name, {}, progress, cancel.Token, isolationMode:=InstanceIsolationMode.Enabled)
                    If mode = "reference-cancel" OrElse mode = "transfer-cancel" Then
                        Await Requested(fixture.Server, If(mode = "reference-cancel", "/reference", "/client")) : cancel.Cancel()
                    End If
                    Dim failure As Exception = Nothing
                    Try
                        Await work
                    Catch ex As Exception
                        failure = ex
                    Finally
                        gate.Set()
                        Dim removed As ManualResetEventSlim = Nothing
                        fixture.Server.Gates.TryRemove("/reference", removed) : fixture.Server.BodyGates.TryRemove("/client", removed)
                        fixture.Server.Files("/reference") = reference
                    End Try
                    Check(If(mode = "reference-failure", TypeOf failure Is AggregateException, TypeOf failure Is OperationCanceledException) AndAlso Not Directory.Exists(target), "early production install fails atomically and releases name: " & id & " " & mode, passed)
                End Using
            Next
            Dim installed = Await installer.InstallAsync(selected, fixture.Root, name, {}, Nothing, CancellationToken.None, isolationMode:=InstanceIsolationMode.Enabled)
            Dim jsonPath = Path.Combine(target, name & ".json"), iniPath = Path.Combine(target, "PCL/Setup.ini")
            Dim profile = JObject.Parse(File.ReadAllText(jsonPath))
            Check(CStr(profile("id")) = name AndAlso CStr(profile("clientVersion")) = id AndAlso CStr(profile.SelectToken("assetIndex.id")).StartsWith("pcldb-", StringComparison.Ordinal) AndAlso
                File.ReadAllText(iniPath).Contains("VersionArgumentIndie:1"), "early same-name retry publishes canonical clientVersion, private index and PCL isolation: " & id, passed)
            profile("minecraftArguments") = CStr(profile("minecraftArguments")) & " --width 1280 --server localhost"
            File.WriteAllText(jsonPath, profile.ToString(), Encoding.Unicode)
            File.AppendAllText(iniPath, Environment.NewLine & "UserSetting:preserve")
            Dim json = File.ReadAllBytes(jsonPath), ini = File.ReadAllBytes(iniPath), clientPath = Path.Combine(target, name & ".jar")
            Dim corruptClient = DirectCast(fixture.Server.Files("/client").Clone(), Byte()) : corruptClient(0) = corruptClient(0) Xor CByte(1)
            File.WriteAllBytes(clientPath, corruptClient)
            Using cancel As New CancellationTokenSource()
                Dim cancelled = False
                Try
                    Await installer.InstallAsync(selected, fixture.Root, name, {}, New Progress(Sub(value)
                                                                                                  If value.Phase = "commit" Then cancel.Cancel()
                                                                                              End Sub), cancel.Token, repairExisting:=True, isolationMode:=InstanceIsolationMode.Disabled)
                Catch ex As OperationCanceledException
                    cancelled = True
                End Try
                Check(cancelled AndAlso File.ReadAllBytes(clientPath).SequenceEqual(corruptClient) AndAlso File.ReadAllBytes(jsonPath).SequenceEqual(json) AndAlso File.ReadAllBytes(iniPath).SequenceEqual(ini), "early repair cancellation preserves client, UTF16 profile and existing settings: " & id, passed)
            End Using
            Await installer.InstallAsync(selected, fixture.Root, name, {}, Nothing, CancellationToken.None, repairExisting:=True, isolationMode:=InstanceIsolationMode.Disabled)
            Check(File.ReadAllBytes(clientPath).SequenceEqual(fixture.Server.Files("/client")) AndAlso File.ReadAllBytes(jsonPath).SequenceEqual(json) AndAlso File.ReadAllBytes(iniPath).SequenceEqual(ini), "early repair retry completes existing instance without changing user parameters or isolation: " & id, passed)
            Dim incomplete = DirectCast(profile.DeepClone(), JObject) : incomplete.Remove("assetIndex")
            DirectCast(incomplete("libraries")(0), JObject).Remove("downloads")
            File.WriteAllText(jsonPath, incomplete.ToString(), Encoding.Unicode)
            Dim before = File.ReadAllBytes(jsonPath), rejected = False
            Try
                Await installer.InstallAsync(selected, fixture.Root, name, {}, Nothing, CancellationToken.None, repairExisting:=True)
            Catch ex As InvalidDataException
                rejected = ex.Message.Contains("缺少完整")
            End Try
            Check(rejected AndAlso File.ReadAllBytes(jsonPath).SequenceEqual(before) AndAlso File.ReadAllBytes(iniPath).SequenceEqual(ini), "early external incomplete profile receives specific refusal without implicit migration: " & id, passed)
            Check(File.ReadAllBytes(indexPath).SequenceEqual(indexBytes) AndAlso File.ReadAllBytes(Path.Combine(ordinary.InstancePath, "ordinary.json")).SequenceEqual(ordinaryBytes), "early installs and repairs preserve ordinary shared index and instance: " & id, passed)
            Dim staging = Path.Combine(fixture.Root, ".vanilla-downloader/staging")
            Check(Not Directory.Exists(staging) OrElse Not Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Any(), "early cancellation and refusal leave no staging payload: " & id, passed)
            Using hold As New FileStream(Path.Combine(fixture.Root, ".vanilla-downloader/install.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                Check(True, "early installation releases shared root lock: " & id, passed)
            End Using
        End Using
    End Function

    Private Async Function FallbackFailureAsync(root As String, id As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(root, "early-index-failure-" & id)
            Dim ordinarySource = fixture.Server.Files("/version")
            fixture.Server.Files("/version") = Encoding.UTF8.GetBytes(Raw(id).ToString())
            fixture.Server.Files("/reference") = SupplementalInstallFixture.Resource("Historical.AlphaLibraryReference.json")
            ' Real fixed declaration must reject bytes from a different index, never
            ' fall back to a pre-1.6 index or an unverified empty resource tree.
            fixture.Server.Files("/legacy-index") = Encoding.UTF8.GetBytes("{""objects"":{}}")
            Dim rejected = False
            Try
                Await New MinecraftInstaller(New Policy(fixture.Server.Url)).InstallAsync(SupplementalInstallFixture.Entry(id), fixture.Root, "unpublished", {}, Nothing, CancellationToken.None)
            Catch ex As AggregateException
                rejected = True
            End Try
            Check(rejected AndAlso fixture.Server.Requests.ContainsKey("/legacy-index") AndAlso Not fixture.Server.Requests.ContainsKey("/client") AndAlso
                Not Directory.Exists(Path.Combine(fixture.Root, "versions/unpublished")), "real archived early profile enforces fixed index checksum before payload: " & id, passed)
            fixture.Server.Files("/version") = ordinarySource
            Dim normal = Await fixture.InstallAsync("ordinary-after-failure")
            Check(File.Exists(Path.Combine(normal.InstancePath, "ordinary-after-failure.jar")), "early supplemental index failure does not block ordinary installer: " & id, passed)
        End Using
    End Function
End Module
