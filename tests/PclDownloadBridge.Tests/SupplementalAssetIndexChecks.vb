Imports System.IO
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module SupplementalAssetIndexChecks
    Private Class FixturePolicy
        Inherits SourcePolicy
        Private ReadOnly _server As String
        Friend Sub New(server As String)
            _server = server : AllowLoopback = True
        End Sub
        Public Overrides Function Candidates(url As String, category As String) As List(Of String)
            Dim uri As New Uri(url)
            Return New List(Of String) From {_server & If(uri.Host = "zkitefly.github.io", "/version", uri.AbsolutePath)}
        End Function
    End Class

    Private Function Entry(suffix As String) As VersionEntry
        Dim raw = "1_16_combat-" & suffix
        Return New VersionEntry With {.Id = "1.16_combat-" & suffix, .SupplementalId = raw, .Kind = "pending",
            .Url = "https://zkitefly.github.io/unlisted-versions-of-minecraft/files/" & raw & "/" & raw & ".json"}
    End Function

    Private Function Hash(bytes As Byte()) As String
        Using sha = Security.Cryptography.SHA1.Create()
            Return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function

    Private Function Serve(fixture As InstallationChecks.InstallFixture, selected As VersionEntry, bytes As Byte()) As JObject
        Dim meta = JObject.Parse(Encoding.UTF8.GetString(fixture.Server.Files("/version")))
        meta("id") = selected.Id : meta("type") = "pending"
        meta("assetIndex")("sha1") = Hash(bytes) : meta("assetIndex")("size") = bytes.Length
        fixture.Server.Files("/index") = bytes
        fixture.Server.Files("/version") = Encoding.UTF8.GetBytes(meta.ToString())
        Return meta
    End Function

    Private Sub Check(value As Boolean, name As String, passed As List(Of String))
        If Not value Then Throw New InvalidOperationException(name)
        passed.Add(name)
    End Sub

    Private Class CancelAtCommit
        Implements IProgress(Of TransferProgress)
        Private ReadOnly _cancel As CancellationTokenSource
        Friend Sub New(cancel As CancellationTokenSource)
            _cancel = cancel
        End Sub
        Public Sub Report(value As TransferProgress) Implements IProgress(Of TransferProgress).Report
            If value.Stage = "完成安装" Then _cancel.Cancel()
        End Sub
    End Class

    Friend Async Function RunAsync(root As String) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        Using fixture As New InstallationChecks.InstallFixture(root, "supplement-indexes")
            Dim installer As New MinecraftInstaller(New FixturePolicy(fixture.Server.Url))
            Dim normalSource = DirectCast(fixture.Server.Files("/version").Clone(), Byte())
            Dim normal = Await fixture.InstallAsync("normal")
            Dim normalJson = Path.Combine(normal.InstancePath, "normal.json"), normalBytes = File.ReadAllBytes(normalJson)
            Dim originalIndex = Path.Combine(fixture.Root, "assets", "indexes", "fixture.json")
            Dim originalBytes = File.ReadAllBytes(originalIndex), originalTime = File.GetLastWriteTimeUtc(originalIndex)
            Dim first = Entry("6"), bytes6 = Encoding.UTF8.GetBytes("{""objects"":{},""fixture"":""combat-six""}")
            Dim source6 = Serve(fixture, first, bytes6), sourceJson = source6.ToString()
            Dim result6 = Await installer.InstallAsync(first, fixture.Root, "combat-six", {}, Nothing, CancellationToken.None)
            Dim installed6 = JObject.Parse(File.ReadAllText(Path.Combine(result6.InstancePath, "combat-six.json")))
            Dim name6 = CStr(installed6.SelectToken("assetIndex.id")), expected6 = "pcldb-" & Hash(bytes6)
            Console.WriteLine("SUPPLEMENT_INDEX_CONFLICT originalPreserved=" & File.ReadAllBytes(originalIndex).SequenceEqual(originalBytes) & "; installedIndex=" & name6)
            Check(File.ReadAllBytes(originalIndex).SequenceEqual(originalBytes) AndAlso File.GetLastWriteTimeUtc(originalIndex) = originalTime, "supplement conflicting index leaves the ordinary index bytes and timestamp intact", passed)
            Check(name6 = expected6 AndAlso File.ReadAllBytes(Path.Combine(fixture.Root, "assets", "indexes", name6 & ".json")).SequenceEqual(bytes6), "supplement index is stored under its complete SHA-1 identity", passed)
            Check(File.ReadAllBytes(normalJson).SequenceEqual(normalBytes), "supplement install preserves the ordinary instance metadata", passed)
            Check(CStr(installed6("clientVersion")) = first.Id AndAlso CStr(installed6("assets")) = CStr(source6("assets")) AndAlso
                CStr(installed6.SelectToken("assetIndex.url")) = CStr(source6.SelectToken("assetIndex.url")) AndAlso CStr(installed6.SelectToken("assetIndex.sha1")) = Hash(bytes6), "supplement changes only the index name while keeping PCL identity and upstream checksum/source", passed)
            Check(source6.ToString() = sourceJson AndAlso CStr(JObject.Parse(Encoding.UTF8.GetString(fixture.Server.Files("/version"))).SelectToken("assetIndex.id")) = "fixture", "supplement index preparation cannot mutate source metadata", passed)

            Dim second = Entry("0"), bytes0 = Encoding.UTF8.GetBytes("{""objects"":{},""fixture"":""combat-zero""}")
            Serve(fixture, second, bytes0)
            Dim result0 = Await installer.InstallAsync(second, fixture.Root, "combat-zero", {}, Nothing, CancellationToken.None)
            Dim installed0 = JObject.Parse(File.ReadAllText(Path.Combine(result0.InstancePath, "combat-zero.json")))
            Dim name0 = CStr(installed0.SelectToken("assetIndex.id")), index6 = Path.Combine(fixture.Root, "assets", "indexes", name6 & ".json")
            Check(name0 <> name6 AndAlso File.ReadAllBytes(index6).SequenceEqual(bytes6) AndAlso File.ReadAllBytes(originalIndex).SequenceEqual(originalBytes), "supplement versions sharing an upstream index ID retain both different contents", passed)

            Serve(fixture, first, bytes6)
            Dim json6 = Path.Combine(result6.InstancePath, "combat-six.json"), jsonBytes6 = File.ReadAllBytes(json6)
            Dim corrupt = DirectCast(bytes6.Clone(), Byte()) : corrupt(0) = corrupt(0) Xor CByte(1)
            File.WriteAllBytes(index6, corrupt)
            Await installer.InstallAsync(first, fixture.Root, "combat-six", {}, Nothing, CancellationToken.None, repairExisting:=True)
            Check(File.ReadAllBytes(index6).SequenceEqual(bytes6) AndAlso File.ReadAllBytes(json6).SequenceEqual(jsonBytes6), "supplement repair restores its damaged private index without rewriting metadata", passed)
            Check(File.ReadAllBytes(originalIndex).SequenceEqual(originalBytes), "supplement repair cannot replace the ordinary index", passed)

            Dim requests = fixture.Server.Requests("/index"), beforeReuse = File.GetLastWriteTimeUtc(index6)
            Serve(fixture, Entry("5"), bytes6)
            Dim same = Await installer.InstallAsync(Entry("5"), fixture.Root, "same-index", {}, Nothing, CancellationToken.None)
            Check(CStr(JObject.Parse(File.ReadAllText(Path.Combine(same.InstancePath, "same-index.json"))).SelectToken("assetIndex.id")) = name6 AndAlso
                fixture.Server.Requests("/index") = requests + 1 AndAlso File.GetLastWriteTimeUtc(index6) = beforeReuse, "supplement identical indexes share the verified private file without a second transfer", passed)

            Dim upperSource = Serve(fixture, Entry("4"), bytes6)
            upperSource("assetIndex")("sha1") = Hash(bytes6).ToUpperInvariant()
            fixture.Server.Files("/version") = Encoding.UTF8.GetBytes(upperSource.ToString())
            Dim upper = Await installer.InstallAsync(Entry("4"), fixture.Root, "upper-digest", {}, Nothing, CancellationToken.None)
            Check(CStr(JObject.Parse(File.ReadAllText(Path.Combine(upper.InstancePath, "upper-digest.json"))).SelectToken("assetIndex.id")) = name6, "supplement checksum case cannot create a second private index identity", passed)
            fixture.Server.Files("/version") = normalSource : fixture.Server.Files("/index") = originalBytes
            Await fixture.InstallAsync("normal-again")
            Check(File.ReadAllBytes(index6).SequenceEqual(bytes6) AndAlso File.ReadAllBytes(originalIndex).SequenceEqual(originalBytes), "ordinary installation after supplemental versions preserves their private indexes", passed)

            Dim cancelIndex = Encoding.UTF8.GetBytes("{""objects"":{},""fixture"":""cancel-before-publish""}")
            Serve(fixture, first, cancelIndex)
            Using cancel As New CancellationTokenSource()
                Dim cancelled = False
                Try
                    Await installer.InstallAsync(first, fixture.Root, "cancelled", {}, New CancelAtCommit(cancel), cancel.Token)
                Catch ex As OperationCanceledException
                    cancelled = True
                End Try
                Check(cancelled AndAlso Not Directory.Exists(Path.Combine(fixture.Root, "versions", "cancelled")) AndAlso File.ReadAllBytes(originalIndex).SequenceEqual(originalBytes), "supplement cancellation before publish retains ordinary index and leaves no partial instance", passed)
            End Using
            Dim retry = Await installer.InstallAsync(first, fixture.Root, "cancelled", {}, Nothing, CancellationToken.None)
            Check(CStr(JObject.Parse(File.ReadAllText(Path.Combine(retry.InstancePath, "cancelled.json"))).SelectToken("assetIndex.id")) = "pcldb-" & Hash(cancelIndex) AndAlso File.ReadAllBytes(originalIndex).SequenceEqual(originalBytes), "supplement cancelled install retries with its isolated index", passed)
            ' Use a new expected client so legitimate cross-instance reuse cannot
            ' bypass the deliberately corrupt network response in this scenario.
            Dim clientBytes = Encoding.UTF8.GetBytes("fixture changed vanilla client"), badClient = DirectCast(clientBytes.Clone(), Byte())
            Dim changedClient = JObject.Parse(Encoding.UTF8.GetString(fixture.Server.Files("/version")))
            changedClient("downloads")("client")("sha1") = Hash(clientBytes)
            changedClient("downloads")("client")("size") = clientBytes.Length
            fixture.Server.Files("/version") = Encoding.UTF8.GetBytes(changedClient.ToString())
            badClient(0) = badClient(0) Xor CByte(1) : fixture.Server.Files("/client") = badClient
            Dim rejected = False
            Try
                Await installer.InstallAsync(first, fixture.Root, "failed-client", {}, Nothing, CancellationToken.None)
            Catch ex As Exception
                rejected = ex.ToString().Contains("哈希校验失败")
            End Try
            Check(rejected AndAlso Not Directory.Exists(Path.Combine(fixture.Root, "versions", "failed-client")) AndAlso File.ReadAllBytes(originalIndex).SequenceEqual(originalBytes) AndAlso File.ReadAllBytes(index6).SequenceEqual(bytes6), "supplement client hash failure cannot damage existing ordinary or supplemental indexes", passed)
            fixture.Server.Files("/client") = clientBytes
            Dim recovered = Await installer.InstallAsync(first, fixture.Root, "failed-client", {}, Nothing, CancellationToken.None)
            Check(File.Exists(Path.Combine(recovered.InstancePath, "failed-client.jar")) AndAlso File.ReadAllBytes(normalJson).SequenceEqual(normalBytes), "supplement failed install recovers with existing ordinary metadata intact", passed)
        End Using
        Await LegacyAsync(root, passed)
        For Each badName In {"../index", "CON", New String("x"c, 101)}
            Using fixture As New InstallationChecks.InstallFixture(root, "supplement-invalid-index-" & passed.Count, "1.16_combat-6")
                Dim meta = Serve(fixture, Entry("6"), fixture.Server.Files("/index"))
                meta("assetIndex")("id") = badName
                fixture.Server.Files("/version") = Encoding.UTF8.GetBytes(meta.ToString())
                Dim rejected = False
                Try
                    Await New MinecraftInstaller(New FixturePolicy(fixture.Server.Url)).InstallAsync(Entry("6"), fixture.Root, "invalid-index", {}, Nothing, CancellationToken.None)
                Catch ex As ArgumentException
                    rejected = True
                End Try
                Check(rejected AndAlso Not fixture.Server.Requests.ContainsKey("/index") AndAlso Not Directory.Exists(Path.Combine(fixture.Root, "versions", "invalid-index")), "supplement private naming still rejects unsafe original index " & badName, passed)
            End Using
        Next
        Return passed
    End Function

    Private Async Function LegacyAsync(root As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(root, "supplement-legacy-index", "1.16_combat-6")
            Dim original = fixture.Server.Files("/index")
            Serve(fixture, Entry("6"), original)
            fixture.Entry.Sha1 = Hash(fixture.Server.Files("/version"))
            ' An ordinary call deliberately produces the pre-isolation metadata layout.
            Dim legacy = Await fixture.InstallAsync("legacy")
            Dim jsonPath = Path.Combine(legacy.InstancePath, "legacy.json"), bytes = File.ReadAllBytes(jsonPath)
            Dim indexPath = Path.Combine(fixture.Root, "assets", "indexes", "fixture.json")
            Dim installer As New MinecraftInstaller(New FixturePolicy(fixture.Server.Url))
            Await installer.InstallAsync(Entry("6"), fixture.Root, "legacy", {}, Nothing, CancellationToken.None, repairExisting:=True)
            Check(File.ReadAllBytes(jsonPath).SequenceEqual(bytes) AndAlso File.ReadAllBytes(indexPath).SequenceEqual(original), "supplement legacy repair keeps matching original index names and metadata bytes", passed)
            Dim conflicting = Encoding.UTF8.GetBytes("{""objects"":{},""fixture"":""another-installed-version""}")
            File.WriteAllBytes(indexPath, conflicting)
            Dim requests = fixture.Server.Requests("/index"), rejected = False
            Try
                Await installer.InstallAsync(Entry("6"), fixture.Root, "legacy", {}, Nothing, CancellationToken.None, repairExisting:=True)
            Catch ex As InvalidDataException
                rejected = ex.Message.Contains("共享资源索引")
            End Try
            Check(rejected AndAlso fixture.Server.Requests("/index") = requests AndAlso File.ReadAllBytes(indexPath).SequenceEqual(conflicting) AndAlso File.ReadAllBytes(jsonPath).SequenceEqual(bytes), "supplement legacy repair refuses to overwrite a conflicting shared index before transfers", passed)
            File.Delete(indexPath)
            Await installer.InstallAsync(Entry("6"), fixture.Root, "legacy", {}, Nothing, CancellationToken.None, repairExisting:=True)
            Check(File.ReadAllBytes(indexPath).SequenceEqual(original) AndAlso File.ReadAllBytes(jsonPath).SequenceEqual(bytes), "supplement legacy repair can restore a missing index while preserving metadata", passed)
        End Using
    End Function
End Module
