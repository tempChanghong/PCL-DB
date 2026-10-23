Imports System.IO
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module SupplementalCandidateVerificationChecks
    Private Sub Check(ok As Boolean, message As String, passed As List(Of String))
        If Not ok Then Throw New InvalidOperationException(message)
        passed.Add(message)
    End Sub

    Private Class Policy
        Inherits SourcePolicy
        Private ReadOnly _server As String
        Friend Sub New(server As String)
            _server = server : AllowLoopback = True
        End Sub
        Public Overrides Function Candidates(url As String, category As String) As List(Of String)
            If url.StartsWith("https://zkitefly.github.io/", StringComparison.Ordinal) Then Return New List(Of String) From {_server & "/version"}
            Return New List(Of String) From {_server & New Uri(url).AbsolutePath}
        End Function
    End Class

    Private Function Hash(bytes As Byte()) As String
        Using sha = Security.Cryptography.SHA1.Create()
            Return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function

    Private Sub ApplyMappedProfile(fixture As InstallationChecks.InstallFixture, id As String)
        Dim source = SupplementalInstallFixture.Profile(fixture, id), bytes = Encoding.UTF8.GetBytes("synthetic snapshot mapped resource"), digest = Hash(bytes)
        fixture.Server.Files("/" & digest.Substring(0, 2) & "/" & digest) = bytes
        Dim index = Encoding.UTF8.GetBytes(New JObject From {{"map_to_resources", True}, {"objects", New JObject From {{"sound/check.ogg", New JObject From {{"hash", digest}, {"size", bytes.Length}}}}}}.ToString())
        fixture.Server.Files("/index") = index
        source("assetIndex")("sha1") = Hash(index) : source("assetIndex")("size") = index.Length
        fixture.Server.Files("/version") = Encoding.UTF8.GetBytes(source.ToString())
    End Sub

    Friend Async Function RunAsync(root As String) As Task(Of List(Of String))
        Dim passed As New List(Of String), selected = SupplementalInstallFixture.Entry("13w10b")
        For Each ids In New List(Of String()) From {New String() {"13w10b", "13W10B"}, New String() {"unregistered"}, New String() {"13w10b"}}
            Dim rejected = False
            Try
                SupplementalCandidateVerification.SelectEntries(ids, {New VersionEntry With {.Id = "13w10b"}}, {selected})
            Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is InvalidDataException
                rejected = True
            End Try
            Check(rejected, "candidate verification rejects duplicate/unknown IDs and keeps official precedence: " & String.Join(",", ids), passed)
        Next
        Using fixture As New InstallationChecks.InstallFixture(root, "candidate-verification-tool")
            ApplyMappedProfile(fixture, selected.Id)
            Dim policy As New Policy(fixture.Server.Url), output = Path.Combine(root, "candidate-tool-output")
            Dim result = Await SupplementalCandidateVerification.VerifyAsync(output, {selected}, policy, True, CancellationToken.None)
            Dim row = DirectCast(result("records"), JArray).OfType(Of JObject)().Single()
            Check(CBool(result("succeeded")) AndAlso CBool(row("cancelThenRetry")) AndAlso CBool(row("utf16AndIsolationPreserved")) AndAlso CBool(row("corruptClientRepaired")) AndAlso CLng(row("initialNetworkBytes")) > 0, "candidate tool exercises real production install/cancel/retry/repair with controlled network bytes", passed)
            Check(CInt(row("mappedResources")) = 1 AndAlso File.Exists(Path.Combine(output, "game/.minecraft/versions/13w10b/resources/sound/check.ogg")) AndAlso
                Not File.Exists(Path.Combine(output, "game/.minecraft/resources/sound/check.ogg")), "candidate tool verifies PCL isolated pre-1.6 resource mapping", passed)
            Check(Not CBool(result("gameLaunched")) AndAlso Not CBool(result("pclGuiVerified")) AndAlso DirectCast(result("externalReuseRoots"), JArray).Count = 0, "candidate report distinguishes installation evidence from game acceptance and external reuse", passed)
            Dim reportPath = Path.Combine(output, "report.json"), original = File.ReadAllBytes(reportPath), rejected = False
            Try
                Await SupplementalCandidateVerification.VerifyAsync(output, {selected}, policy, False, CancellationToken.None)
            Catch ex As IOException
                rejected = True
            End Try
            Check(rejected AndAlso File.ReadAllBytes(reportPath).SequenceEqual(original), "candidate verification cannot overwrite a previous receipt or game root", passed)
            Dim invalid = SupplementalInstallFixture.Profile(fixture, selected.Id) : invalid("minecraftArguments") = "invalid"
            fixture.Server.Files("/version") = Encoding.UTF8.GetBytes(invalid.ToString())
            Dim failedOutput = Path.Combine(root, "candidate-tool-failure"), failed = False
            Try
                Await SupplementalCandidateVerification.VerifyAsync(failedOutput, {selected}, policy, True, CancellationToken.None)
            Catch ex As InvalidDataException
                failed = True
            End Try
            Dim failure = JObject.Parse(File.ReadAllText(Path.Combine(failedOutput, "report.json")))
            Check(failed AndAlso Not CBool(failure("succeeded")) AndAlso Not CBool(failure("records")(0)("succeeded")) AndAlso failure("failure") IsNot Nothing AndAlso
                Not Directory.Exists(Path.Combine(failedOutput, "game")), "candidate tool persists an explicit failure and does not install invalid metadata", passed)
        End Using
        For Each id In SupplementalVersions.HistoricalSnapshotIds.Concat(SupplementalVersions.HistoricalBetaIds).Concat(SupplementalVersions.HistoricalAlphaIds)
            Using fixture As New InstallationChecks.InstallFixture(root, "historical-map-" & id)
                ApplyMappedProfile(fixture, id)
                Dim installer As New MinecraftInstaller(New Policy(fixture.Server.Url)), entry = SupplementalInstallFixture.Entry(id)
                Dim sharedResource = Path.Combine(fixture.Root, "resources/sound/check.ogg"), userBytes = Encoding.UTF8.GetBytes("existing user resource")
                Directory.CreateDirectory(Path.GetDirectoryName(sharedResource)) : File.WriteAllBytes(sharedResource, userBytes)
                Dim isolated = Await installer.InstallAsync(entry, fixture.Root, "isolated", {}, Nothing, CancellationToken.None, isolationMode:=InstanceIsolationMode.Enabled)
                Check(File.Exists(Path.Combine(isolated.InstancePath, "resources/sound/check.ogg")) AndAlso File.ReadAllBytes(sharedResource).SequenceEqual(userBytes), "historical isolation preserves differing shared resources: " & id, passed)
                Dim refused = False
                Try
                    Await installer.InstallAsync(entry, fixture.Root, "shared", {}, Nothing, CancellationToken.None, isolationMode:=InstanceIsolationMode.Disabled)
                Catch ex As IOException
                    refused = True
                End Try
                Check(refused AndAlso Not Directory.Exists(Path.Combine(fixture.Root, "versions/shared")) AndAlso File.ReadAllBytes(sharedResource).SequenceEqual(userBytes), "historical shared-resource conflict refuses publication without overwriting user bytes: " & id, passed)
                ' Explicit test correction, never an automatic installer migration.
                File.WriteAllBytes(sharedResource, File.ReadAllBytes(Path.Combine(isolated.InstancePath, "resources/sound/check.ogg")))
                Dim accepted = Await installer.InstallAsync(entry, fixture.Root, "shared", {}, Nothing, CancellationToken.None, isolationMode:=InstanceIsolationMode.Disabled)
                Check(File.Exists(Path.Combine(accepted.InstancePath, "shared.json")) AndAlso PclFolders.ReadIniValue(Path.Combine(accepted.InstancePath, "PCL/Setup.ini"), "VersionArgumentIndieV2") = "False", "historical shared install retries after explicit conflict correction: " & id, passed)
            End Using
        Next
        Await VirtualResourcesAsync(root, passed)
        Return passed
    End Function

    Private Async Function VirtualResourcesAsync(root As String, passed As List(Of String)) As Task
        For Each id In {"b1.1-1", "b1.1-2", "a1.1.0-2"}
            Using fixture As New InstallationChecks.InstallFixture(root, "legacy-virtual-" & id)
                Dim source = SupplementalInstallFixture.Profile(fixture, id), bytes = Encoding.UTF8.GetBytes("synthetic legacy virtual resource"), digest = Hash(bytes)
                fixture.Server.Files("/" & digest.Substring(0, 2) & "/" & digest) = bytes
                Dim index = Encoding.UTF8.GetBytes(New JObject From {{"virtual", True}, {"objects", New JObject From {{"sound/check.ogg", New JObject From {{"hash", digest}, {"size", bytes.Length}}}}}}.ToString())
                fixture.Server.Files("/index") = index
                source("assets") = "legacy" : source("assetIndex")("id") = "legacy"
                source("assetIndex")("sha1") = Hash(index) : source("assetIndex")("size") = index.Length
                fixture.Server.Files("/version") = Encoding.UTF8.GetBytes(source.ToString())
                Dim policy As New Policy(fixture.Server.Url), output = Path.Combine(root, "virtual-tool-" & id)
                Dim report = Await SupplementalCandidateVerification.VerifyAsync(output, {SupplementalInstallFixture.Entry(id)}, policy, True, CancellationToken.None)
                Dim gameRoot = Path.Combine(output, "game/.minecraft"), virtualPath = Path.Combine(gameRoot, "assets/virtual/legacy/sound/check.ogg"), row = report("records")(0)
                Check(CBool(report("succeeded")) AndAlso CInt(row("virtualResources")) = 1 AndAlso CInt(row("mappedResources")) = 0 AndAlso File.ReadAllBytes(virtualPath).SequenceEqual(bytes) AndAlso
                    Not Directory.Exists(Path.Combine(gameRoot, "versions", id, "assets/virtual")) AndAlso Not Directory.Exists(Path.Combine(gameRoot, "resources")), "legacy virtual resources remain in the root cache through isolated install, cancellation, retry and repair: " & id, passed)
                Dim userResource = Path.Combine(gameRoot, "resources/user.ogg"), instanceResource = Path.Combine(gameRoot, "versions", id, "resources/user.ogg"), userBytes = Encoding.UTF8.GetBytes("preserve user resource")
                For Each resourcePath In {userResource, instanceResource}
                    Directory.CreateDirectory(Path.GetDirectoryName(resourcePath)) : File.WriteAllBytes(resourcePath, userBytes)
                Next
                Dim ordinaryIndex = Path.Combine(gameRoot, "assets/indexes/legacy.json"), ordinaryBytes = Encoding.UTF8.GetBytes("unrelated ordinary legacy index")
                File.WriteAllBytes(ordinaryIndex, ordinaryBytes)
                Dim installer As New MinecraftInstaller(policy), sharedName = "shared-" & id
                Dim result = Await installer.InstallAsync(SupplementalInstallFixture.Entry(id), gameRoot, sharedName, {gameRoot}, Nothing, CancellationToken.None, isolationMode:=InstanceIsolationMode.Disabled)
                Check(File.ReadAllBytes(virtualPath).SequenceEqual(bytes) AndAlso File.ReadAllBytes(userResource).SequenceEqual(userBytes) AndAlso File.ReadAllBytes(instanceResource).SequenceEqual(userBytes) AndAlso
                    PclFolders.ReadIniValue(Path.Combine(result.InstancePath, "PCL/Setup.ini"), "VersionArgumentIndieV2") = "False", "legacy virtual cache is shared without changing either user's resources directory: " & id, passed)
                Dim jsonPath = Path.Combine(gameRoot, "versions", id, id & ".json"), iniPath = Path.Combine(gameRoot, "versions", id, "PCL/Setup.ini")
                Dim installed = JObject.Parse(File.ReadAllText(jsonPath))
                Check(CStr(installed("assets")) = "legacy" AndAlso CStr(installed("assetIndex")("id")) = "pcldb-" & Hash(index) AndAlso File.ReadAllBytes(ordinaryIndex).SequenceEqual(ordinaryBytes),
                    "private legacy index naming keeps the virtual directory and ordinary index independent: " & id, passed)
                Dim json = File.ReadAllBytes(jsonPath), ini = File.ReadAllBytes(iniPath)
                File.WriteAllText(virtualPath, "damaged virtual cache")
                Dim repaired = Await installer.InstallAsync(SupplementalInstallFixture.Entry(id), gameRoot, id, {gameRoot}, Nothing, CancellationToken.None, repairExisting:=True, isolationMode:=InstanceIsolationMode.Disabled)
                Check(repaired.NetworkBytes = 0 AndAlso File.ReadAllBytes(virtualPath).SequenceEqual(bytes) AndAlso File.ReadAllBytes(jsonPath).SequenceEqual(json) AndAlso File.ReadAllBytes(iniPath).SequenceEqual(ini) AndAlso
                    File.ReadAllBytes(ordinaryIndex).SequenceEqual(ordinaryBytes) AndAlso File.ReadAllBytes(userResource).SequenceEqual(userBytes) AndAlso File.ReadAllBytes(instanceResource).SequenceEqual(userBytes),
                    "legacy virtual cache repair reuses the checked object and preserves existing isolated settings: " & id, passed)
                Dim blockedRoot = Path.Combine(root, "virtual-blocked-" & id), target = Path.Combine(blockedRoot, "assets/virtual/legacy/sound/check.ogg"), rejected = False
                Directory.CreateDirectory(target)
                Try
                    Await installer.InstallAsync(SupplementalInstallFixture.Entry(id), blockedRoot, "blocked", {}, Nothing, CancellationToken.None, isolationMode:=InstanceIsolationMode.Enabled)
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException
                    rejected = True
                End Try
                Check(rejected AndAlso Directory.Exists(target) AndAlso Not Directory.Exists(Path.Combine(blockedRoot, "versions/blocked")), "legacy virtual file-directory conflict preserves the occupied path and refuses instance publication: " & id, passed)
                Directory.Delete(target) ' Explicit correction of the empty fixture directory.
                Dim retried = Await installer.InstallAsync(SupplementalInstallFixture.Entry(id), blockedRoot, "blocked", {}, Nothing, CancellationToken.None, isolationMode:=InstanceIsolationMode.Enabled)
                Check(File.ReadAllBytes(target).SequenceEqual(bytes) AndAlso File.Exists(Path.Combine(retried.InstancePath, "blocked.json")), "legacy virtual conflict can retry the same name after explicit fixture correction: " & id, passed)
            End Using
        Next
    End Function
End Module
