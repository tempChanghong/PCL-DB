Imports System.IO
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PlainCraftDownloader.Core

Friend Module DownloadPlanChecks
    Friend Async Function RunAsync(testRoot As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "plan-strength" Then Await StrengthAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "plan-alias" Then Await AliasAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "plan-conflict" Then Await ConflictsAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "plan-md5" Then Await Md5Async(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "plan-install" Then Await InstallAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "plan-assets" Then Assets(passed)
        If passed.Count = 0 Then Throw New ArgumentException("Unknown download-plan regression: " & onlyCase)
        Return passed
    End Function

    Private Sub Check(condition As Boolean, label As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub

    Private Function Hash(bytes As Byte(), Optional md5 As Boolean = False) As String
        Using algorithm As HashAlgorithm = If(md5, DirectCast(Security.Cryptography.MD5.Create(), HashAlgorithm), SHA1.Create())
            Return BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function

    Private Async Function StrengthAsync(testRoot As String, passed As List(Of String)) As Task
        Using server As New TestServer()
            Dim root = Path.Combine(testRoot, "plan-strength")
            Dim bytes = Encoding.UTF8.GetBytes("verified content")
            Dim corrupt = Encoding.UTF8.GetBytes("incorrect bytes!")
            Check(bytes.Length = corrupt.Length, "duplicate-plan fixture has equal-size corrupt content", passed)
            server.Files("/strong") = bytes
            Dim target = SafeFiles.UnderRoot(root, "libraries/strong.bin")
            Directory.CreateDirectory(Path.GetDirectoryName(target))
            File.WriteAllBytes(target, corrupt)
            Dim weak As New DownloadFile With {.RelativePath = "libraries/strong.bin", .Url = server.Url & "/strong", .Size = bytes.Length, .Category = "library"}
            Dim strong As New DownloadFile With {.RelativePath = weak.RelativePath, .Url = weak.Url, .Size = bytes.Length, .Sha1 = Hash(bytes), .Category = "library"}
            Using engine As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, Nothing, {}, 2)
                Await engine.DownloadAsync(root, {weak, strong}, CancellationToken.None)
                Check(File.ReadAllBytes(target).SequenceEqual(bytes) AndAlso engine.NetworkBytes = bytes.Length,
                      "a later duplicate SHA-1 repairs an existing same-size corrupt file", passed)
                Check(server.Requests("/strong") = 1 AndAlso engine.Reused = 0, "strong duplicate validation prevents false reuse and downloads once", passed)
            End Using
            Check(weak.Sha1 Is Nothing AndAlso strong.Sha1 = Hash(bytes), "download planning leaves caller-owned validation records unchanged", passed)
        End Using
    End Function

    Private Async Function AliasAsync(testRoot As String, passed As List(Of String)) As Task
        Using server As New TestServer()
            Dim root = Path.Combine(testRoot, "plan-alias")
            Dim bytes = Encoding.UTF8.GetBytes("one physical destination")
            server.Files("/first") = bytes
            server.Files("/second") = bytes
            server.PathDelayMs("/first") = 120
            server.PathDelayMs("/second") = 120
            Dim first As New DownloadFile With {.RelativePath = "libraries/sample.bin", .Url = server.Url & "/first", .Size = bytes.Length, .Sha1 = Hash(bytes).ToUpperInvariant(), .Category = "library"}
            Dim second As New DownloadFile With {.RelativePath = "LIBRARIES\.\sample.bin", .Url = server.Url & "/second", .Size = bytes.Length, .Sha1 = Hash(bytes), .Category = "meta"}
            Dim progress As New CaptureProgress()
            Using engine As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, progress, {}, 2)
                Await engine.DownloadAsync(root, {first, second}, CancellationToken.None)
                Check(server.Requests.Values.Sum() = 1 AndAlso server.Requests.ContainsKey("/first") AndAlso Not server.Requests.ContainsKey("/second"),
                      "case, slash and dot aliases download once using the first declared source", passed)
                Check(engine.NetworkBytes = bytes.Length AndAlso File.ReadAllBytes(SafeFiles.UnderRoot(root, first.RelativePath)).SequenceEqual(bytes),
                      "canonical duplicate destinations publish one verified file", passed)
            End Using
            Check(progress.Last IsNot Nothing AndAlso progress.Last.Total = 1 AndAlso progress.Last.Completed = 1,
                  "download progress counts unique physical destinations", passed)
            Check(first.Sha1 = Hash(bytes).ToUpperInvariant() AndAlso second.RelativePath = "LIBRARIES\.\sample.bin",
                  "canonical planning preserves original hash casing and input paths", passed)
        End Using
    End Function

    Private Async Function ConflictsAsync(testRoot As String, passed As List(Of String)) As Task
        For Each field In {"size", "SHA-1", "MD5"}
            Using server As New TestServer()
                Dim root = Path.Combine(testRoot, "plan-conflict-" & field)
                Dim bytes = Encoding.UTF8.GetBytes("existing verified content")
                server.Files("/file") = bytes
                Dim target = SafeFiles.UnderRoot(root, "libraries/shared.bin")
                Directory.CreateDirectory(Path.GetDirectoryName(target))
                File.WriteAllBytes(target, bytes)
                Dim first As New DownloadFile With {.RelativePath = "libraries/shared.bin", .Url = server.Url & "/file", .Size = bytes.Length, .Sha1 = Hash(bytes), .Md5 = Hash(bytes, True)}
                Dim second As New DownloadFile With {.RelativePath = "LIBRARIES\sub\..\shared.bin", .Url = first.Url, .Size = first.Size, .Sha1 = first.Sha1, .Md5 = first.Md5}
                Select Case field
                    Case "size" : second.Size += 1
                    Case "SHA-1" : second.Sha1 = New String("0"c, 40)
                    Case "MD5" : second.Md5 = New String("0"c, 32)
                End Select
                Dim other As New DownloadFile With {.RelativePath = "libraries/new.bin", .Url = first.Url, .Size = bytes.Length, .Sha1 = first.Sha1}
                Dim failure As InvalidDataException = Nothing
                Using engine As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, Nothing, {}, 3)
                    Try
                        Await engine.DownloadAsync(root, {other, first, second}, CancellationToken.None)
                    Catch ex As InvalidDataException
                        failure = ex
                    End Try
                    Check(failure IsNot Nothing AndAlso failure.Message.Contains(second.RelativePath), "conflicting " & field & " declarations identify the duplicate destination", passed)
                    Check(server.Requests.Count = 0 AndAlso engine.NetworkBytes = 0, "conflicting " & field & " blocks the entire batch before HTTP transfer", passed)
                End Using
                Check(File.ReadAllBytes(target).SequenceEqual(bytes) AndAlso Not File.Exists(SafeFiles.UnderRoot(root, other.RelativePath)),
                      "conflicting " & field & " preserves existing files and publishes no unrelated batch file", passed)
            End Using
        Next
        Using server As New TestServer()
            Dim root = Path.Combine(testRoot, "plan-invalid-path")
            Dim rejected = False
            Using engine As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, Nothing, {}, 2)
                Try
                    Await engine.DownloadAsync(root, {
                        New DownloadFile With {.RelativePath = "valid.bin", .Url = server.Url & "/file"},
                        New DownloadFile With {.RelativePath = "../outside.bin", .Url = server.Url & "/file"}}, CancellationToken.None)
                Catch ex As InvalidDataException
                    rejected = True
                End Try
            End Using
            Check(rejected AndAlso server.Requests.Count = 0 AndAlso Not Directory.Exists(root), "full-batch destination validation rejects escape paths before creating files or connecting", passed)
        End Using
    End Function

    Private Async Function Md5Async(testRoot As String, passed As List(Of String)) As Task
        Using server As New TestServer()
            Dim root = Path.Combine(testRoot, "plan-md5")
            Dim bytes = Encoding.UTF8.GetBytes("both hash algorithms")
            server.Files("/file") = bytes
            Dim target = SafeFiles.UnderRoot(root, "libraries/both.bin")
            Directory.CreateDirectory(Path.GetDirectoryName(target))
            File.WriteAllBytes(target, New Byte(bytes.Length - 1) {})
            Dim first As New DownloadFile With {.RelativePath = "libraries/both.bin", .Url = server.Url & "/file"}
            Dim md5Only As New DownloadFile With {.RelativePath = first.RelativePath, .Url = first.Url, .Size = bytes.Length, .Md5 = Hash(bytes, True)}
            Dim progress As New CaptureProgress()
            Using engine As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, progress, {}, 2)
                Await engine.DownloadAsync(root, {first, md5Only}, CancellationToken.None)
                Check(File.ReadAllBytes(target).SequenceEqual(bytes) AndAlso server.Requests("/file") = 1, "later MD5 validation repairs a corrupt duplicate with unknown initial size", passed)
            End Using
            Check(progress.Last.KnownBytes = bytes.Length AndAlso progress.Last.VerifiedBytes = bytes.Length, "duplicate size promotion counts verified bytes once", passed)
            Check(first.Size = -1 AndAlso first.Md5 Is Nothing, "size and MD5 promotion do not alter the caller's first declaration", passed)

            Dim shaOnly As New DownloadFile With {.RelativePath = first.RelativePath, .Url = first.Url, .Size = bytes.Length, .Sha1 = Hash(bytes)}
            Dim wrongMd5 As New DownloadFile With {.RelativePath = first.RelativePath, .Url = first.Url, .Size = bytes.Length, .Md5 = New String("0"c, 32)}
            Dim failed = False
            Dim beforeRequests = server.Requests("/file")
            Using engine As New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, Nothing, {}, 2)
                Try
                    Await engine.DownloadAsync(root, {shaOnly, wrongMd5}, CancellationToken.None)
                Catch ex As IOException
                    failed = TypeOf ex.InnerException Is InvalidDataException
                End Try
                Check(failed AndAlso server.Requests("/file") - beforeRequests = 2 AndAlso engine.Reused = 0,
                      "a matching SHA-1 cannot bypass a duplicate's failing MD5 during reuse or retries", passed)
            End Using
            Check(File.ReadAllBytes(target).SequenceEqual(bytes), "failed combined hash validation preserves the existing destination bytes", passed)
        End Using
    End Function

    Private Function AddDuplicateLibraries(fixture As InstallationChecks.InstallFixture, Optional conflict As Boolean = False) As Byte()
        Dim bytes = Encoding.UTF8.GetBytes("strong shared library")
        fixture.Server.Files("/shared") = bytes
        Dim metadata = JObject.Parse(Encoding.UTF8.GetString(fixture.Server.Files("/version")))
        Dim weak As New JObject From {{"name", "fixture:shared:1"}, {"downloads", New JObject From {{"artifact", New JObject From {
            {"path", "fixture/shared/1/shared-1.jar"}, {"url", fixture.Server.Url & "/shared"}, {"size", bytes.Length}}}}}}
        Dim strong = DirectCast(weak.DeepClone(), JObject)
        DirectCast(strong.SelectToken("downloads.artifact"), JObject)("sha1") = Hash(bytes)
        If conflict Then DirectCast(weak.SelectToken("downloads.artifact"), JObject)("sha1") = New String("0"c, 40)
        metadata("libraries") = New JArray(weak, strong)
        fixture.Server.Files("/version") = Encoding.UTF8.GetBytes(metadata.ToString())
        fixture.Entry.Sha1 = Hash(fixture.Server.Files("/version"))
        Return bytes
    End Function

    Private Async Function InstallAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(testRoot, "plan-merge")
            Dim bytes = AddDuplicateLibraries(fixture)
            Dim sharedPath = fixture.SharedPath()
            Directory.CreateDirectory(Path.GetDirectoryName(sharedPath))
            File.WriteAllBytes(sharedPath, New Byte(bytes.Length - 1) {})
            Dim result = Await fixture.InstallAsync("merged")
            Check(File.ReadAllBytes(sharedPath).SequenceEqual(bytes) AndAlso fixture.Server.Requests("/shared") = 1,
                  "real installation retains a later library SHA-1 through metadata planning and repairs shared content", passed)
            Check(result.FileCount = 5 AndAlso File.Exists(Path.Combine(result.InstancePath, "merged.json")),
                  "real installation publishes its instance and reports unique client, library, index, logging and JSON files", passed)
            Dim repaired = Await fixture.InstallAsync("merged", repair:=True)
            Check(repaired.NetworkBytes = 0 AndAlso repaired.FileCount = 5, "repair of duplicate-library metadata reuses all verified content", passed)
        End Using
        Using fixture As New InstallationChecks.InstallFixture(testRoot, "plan-conflict")
            AddDuplicateLibraries(fixture, True)
            Dim failed = False
            Try
                Await fixture.InstallAsync("conflict")
            Catch ex As InvalidDataException
                failed = True
            End Try
            Check(failed AndAlso fixture.NonVersionRequests() = 0, "conflicting duplicate libraries fail after version metadata and before index or content HTTP", passed)
            Check(Not Directory.Exists(Path.Combine(fixture.Root, "versions", "conflict")) AndAlso Not File.Exists(fixture.SharedPath()),
                  "conflicting installation metadata publishes no instance or shared library", passed)
        End Using
    End Function

    Private Sub Assets(passed As List(Of String))
        Dim hashValue = New String("a"c, 40)
        Dim index As New JObject From {{"virtual", True}, {"objects", New JObject From {
            {"first.ogg", New JObject From {{"hash", hashValue}, {"size", 2}}},
            {"second.ogg", New JObject From {{"hash", hashValue.ToUpperInvariant()}, {"size", 2}}}}}}
        Dim plan = MinecraftInstaller.BuildAssetsPlan(index)
        Check(plan.Count = 3 AndAlso plan.Where(Function(f) f.RelativePath.StartsWith("assets/objects/", StringComparison.Ordinal)).Count() = 1,
              "shared asset objects deduplicate while both legacy virtual mappings remain", passed)
        index("objects")("second.ogg")("size") = 3
        Dim failed = False
        Try
            MinecraftInstaller.BuildAssetsPlan(index)
        Catch ex As InvalidDataException
            failed = True
        End Try
        Check(failed, "conflicting sizes for one asset object are rejected during metadata planning", passed)
    End Sub

    Private Class CaptureProgress
        Implements IProgress(Of TransferProgress)
        Public Last As TransferProgress
        Public Sub Report(value As TransferProgress) Implements IProgress(Of TransferProgress).Report
            Last = value
        End Sub
    End Class
End Module
