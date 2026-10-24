Imports System.IO
Imports System.IO.Compression
Imports System.Security.Cryptography
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module LargeModpackChecks
    Private Sub Check(value As Boolean, label As String, passed As List(Of String))
        If Not value Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub
    Private Function CreateArchive(root As String, name As String, size As Long, count As Integer) As String
        Dim path = IO.Path.Combine(root, name & ".zip")
        Using zip = ZipFile.Open(path, ZipArchiveMode.Create)
            Dim cf = CurseForgeChecks.Manifest(Nothing) : cf("files") = New JArray()
            Dim mr As New JObject From {{"formatVersion", 1}, {"game", "minecraft"}, {"versionId", "1"}, {"name", "Large pack fixture"},
                {"dependencies", New JObject From {{"minecraft", "1.20.1"}, {"fabric-loader", "0.16.9"}}}, {"files", New JArray()}}
            For Each pair In New Dictionary(Of String, JObject) From {{"manifest.json", cf}, {"modrinth.index.json", mr}}
                Using writer As New StreamWriter(zip.CreateEntry(pair.Key).Open()) : writer.Write(pair.Value.ToString()) : End Using
            Next
            Dim buffer(65535) As Byte
            For index = 0 To count - 1
                Using output = zip.CreateEntry("overrides/resourcepacks/large-" & index & ".zip", CompressionLevel.Fastest).Open()
                    Dim remaining = size
                    While remaining > 0
                        Dim length = CInt(Math.Min(remaining, buffer.Length))
                        output.Write(buffer, 0, length) : remaining -= length
                    End While
                End Using
            Next
        End Using
        Return path
    End Function
    Private Function Digest(input As Stream) As String
        Using sha = SHA256.Create() : Return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "") : End Using
    End Function
    Friend Async Function RunAsync(root As String) As Task(Of List(Of String))
        root = IO.Path.Combine(root, "large-modpacks") : Directory.CreateDirectory(root)
        Dim passed As New List(Of String), size = 128L * 1024 * 1024 + 65536
        Dim archive = CreateArchive(root, "large-entry", size, 1), cf = CurseForgePackage.Read(archive), mr = ModrinthPackage.Read(archive)
        Check(cf.OverrideFiles.Count = 1 AndAlso mr.OverrideFiles.Count = 1, "both formats accept a real compressed override larger than 128 MiB", passed)
        Using zip = ZipFile.OpenRead(archive), source = zip.GetEntry("overrides/resourcepacks/large-0.zip").Open()
            Dim expected = Digest(source)
            For Each packKind In {"cf", "mr"}
                Dim stage = IO.Path.Combine(root, "streamed-" & packKind) : Directory.CreateDirectory(stage)
                Using plan As ModpackInstallPlan = If(packKind = "cf", DirectCast(New CurseForgeInstallPlan(cf, CancellationToken.None), ModpackInstallPlan), New ModrinthInstallPlan(mr, {}, CancellationToken.None))
                    plan.ValidateTargets("large-fixture") : plan.CopyOverrides(stage, CancellationToken.None)
                End Using
                Dim target = IO.Path.Combine(stage, "resourcepacks/large-0.zip")
                Using input = File.OpenRead(target)
                    Check(input.Length = size AndAlso Digest(input) = expected, "large override extraction streams the exact bytes and digest: " & packKind, passed)
                End Using
                File.Delete(target)
            Next
        End Using
        Dim totalArchive = CreateArchive(root, "large-total", 128L * 1024 * 1024, 17)
        Check(CurseForgePackage.Read(totalArchive).OverrideFiles.Count = 17, "CF accepts a real archive with more than 2 GiB of expanded content", passed)
        Check(ModrinthPackage.Read(totalArchive).OverrideFiles.Count = 17, "MR accepts a real archive with more than 2 GiB of expanded content", passed)
        Dim project As New ModpackProject With {.Platform = ModpackPlatform.Modrinth, .Id = "pack1"}
        For Each declared In {2L * 1024 * 1024 * 1024 + 1, 16L * 1024 * 1024 * 1024 + 1}
            Dim json = ModpackCatalogChecks.MrRelease() : json("files")(1)("size") = declared
            Dim release = ModpackCatalogClient.ParseRelease(project, json)
            Check(release IsNot Nothing AndAlso release.Size = declared, "online MR archives retain their full Int64 size: " & declared, passed)
            Dim cfProject As New ModpackProject With {.Platform = ModpackPlatform.CurseForge, .Id = "12"}
            Dim cfJson As New JObject From {{"id", 1234567}, {"modId", 12}, {"displayName", "Large pack"}, {"fileName", "client.zip"}, {"downloadUrl", "https://edge.forgecdn.net/files/1234/567/client.zip"},
                {"fileLength", declared}, {"isServerPack", False}, {"isAvailable", True}, {"releaseType", 1}, {"fileDate", "2026-10-01T00:00:00Z"}, {"gameVersions", New JArray("1.20.1", "Forge")},
                {"hashes", New JArray(New JObject From {{"algo", 1}, {"value", New String("a"c, 40)}})}}
            Check(ModpackCatalogClient.ParseRelease(cfProject, cfJson).Size = declared, "online CF archives retain their full Int64 size: " & declared, passed)
            Using cancel As New CancellationTokenSource()
                cancel.Cancel() : Dim cancelled = False
                Try
                    Await ModpackArchiveInstaller.AcquireAsync(release, IO.Path.Combine(root, "cancelled-" & declared), New SourcePolicy(), Nothing, cancel.Token)
                Catch ex As OperationCanceledException : cancelled = True
                End Try
                Check(cancelled, "large online archives reach cancellable acquisition without an artificial size rejection: " & declared, passed)
            End Using
        Next
        Dim reference As New ModrinthReference(New JObject From {{"path", "mods/large.jar"}, {"fileSize", 16L * 1024 * 1024 * 1024 + 1},
            {"hashes", New JObject From {{"sha1", New String("a"c, 40)}, {"sha512", New String("b"c, 128)}}}, {"downloads", New JArray("https://cdn.modrinth.com/large.jar")}})
        Check(reference.EstimatedSize = 16L * 1024 * 1024 * 1024 + 1, "MR downloadable files are not rejected at an arbitrary 16 GiB boundary", passed)
        Return passed
    End Function

    Friend Async Function SamplesAsync(cfPath As String, mrPath As String, output As String) As Task
        output = IO.Path.GetFullPath(output) : Directory.CreateDirectory(output)
        Dim records As New JArray(), succeeded = True
        For Each packKind In {"cf", "mr"}
            Dim path = If(packKind = "cf", cfPath, mrPath), record As New JObject From {{"format", packKind}, {"source", path}, {"succeeded", False}}
            Try
                Dim makePlan As Func(Of ModpackInstallPlan), entries As Dictionary(Of String, String), name As String, minecraft As String, loader As String, references As Integer
                If packKind = "cf" Then
                    Dim package = CurseForgePackage.Read(path)
                    makePlan = Function() New CurseForgeInstallPlan(package, CancellationToken.None)
                    entries = package.OverrideFiles.ToDictionary(Function(relative) relative, Function(relative) package.BaseFolder & package.OverridesFolder & "/" & relative)
                    name = package.Name : minecraft = package.Minecraft : loader = package.LoaderVersion : references = package.References.Count
                    record("archiveSha256") = package.Sha256
                Else
                    Dim package = ModrinthPackage.Read(path)
                    makePlan = Function() New ModrinthInstallPlan(package, {}, CancellationToken.None)
                    entries = package.OverrideSources.ToDictionary(Function(pair) pair.Key, Function(pair) pair.Value)
                    name = package.Name : minecraft = package.Minecraft : loader = package.LoaderVersion : references = package.References.Count
                    record("archiveSha256") = package.Sha256
                End If
                record("name") = name : record("minecraft") = minecraft : record("loaderVersion") = loader : record("references") = references : record("overrides") = entries.Count
                Console.WriteLine("READ " & packKind & ": " & name & "; " & entries.Count & " overrides")
                Dim sampleRoot = IO.Path.Combine(output, packKind) : Directory.CreateDirectory(sampleRoot)
                Dim cancelStage = IO.Path.Combine(sampleRoot, "cancelled"), stage = IO.Path.Combine(sampleRoot, "payload")
                If Directory.Exists(cancelStage) OrElse Directory.Exists(stage) Then Throw New IOException("Sample stages must be fresh.")
                Dim largest As KeyValuePair(Of String, String)
                Using zip = ZipFile.OpenRead(path)
                    largest = entries.OrderByDescending(Function(pair) zip.Entries.Single(Function(entry) String.Equals(entry.FullName.Replace("\"c, "/"c), pair.Value, StringComparison.OrdinalIgnoreCase)).Length).First()
                End Using
                Directory.CreateDirectory(cancelStage)
                Using plan = makePlan(), cancel As New CancellationTokenSource()
                    plan.ValidateTargets(name)
                    Dim work = Task.Run(Sub() plan.CopyOverrides(cancelStage, cancel.Token)), target = SafeFiles.UnderRoot(cancelStage, largest.Key), cancelled = False
                    While Not work.IsCompleted
                        If File.Exists(target) AndAlso New FileInfo(target).Length >= 65536 Then cancel.Cancel()
                        Await Task.Delay(1).ConfigureAwait(False)
                    End While
                    Try : Await work.ConfigureAwait(False)
                    Catch ex As OperationCanceledException When cancel.IsCancellationRequested : cancelled = True
                    End Try
                    If Not cancelled Then Throw New InvalidOperationException("The sample did not exercise cancellation during the large override.")
                    record("cancelledDuringLargeOverride") = True : record("cancelledBytes") = New FileInfo(target).Length
                End Using
                Directory.CreateDirectory(stage)
                Using plan = makePlan()
                    plan.ValidateTargets(name)
                    Await Task.Run(Sub() plan.CopyOverrides(stage, CancellationToken.None)).ConfigureAwait(False)
                End Using
                Dim verified As Integer = 0, bytes As Long = 0
                Using zip = ZipFile.OpenRead(path)
                    For Each pair In entries
                        Dim target = SafeFiles.UnderRoot(stage, pair.Key)
                        If String.Equals(pair.Key, "PCL/Setup.ini", StringComparison.OrdinalIgnoreCase) Then
                            Dim text = File.ReadAllText(target)
                            If Not text.Contains("VersionArgumentIndie:1") OrElse Not text.Contains("VersionArgumentIndieV2:True") Then Throw New InvalidDataException("Pack isolation was not retained.")
                            record("isolationIniMerged") = True
                        Else
                            Dim entry = zip.Entries.Single(Function(item) String.Equals(item.FullName.Replace("\"c, "/"c), pair.Value, StringComparison.OrdinalIgnoreCase))
                            Using input = entry.Open(), actual = File.OpenRead(target)
                                If actual.Length <> entry.Length OrElse Digest(input) <> Digest(actual) Then Throw New InvalidDataException("Sample override mismatch: " & pair.Key)
                                bytes += actual.Length
                            End Using
                        End If
                        verified += 1
                    Next
                End Using
                record("verifiedOverrides") = verified : record("verifiedBytes") = bytes : record("succeeded") = True
                Console.WriteLine("VERIFIED " & packKind & ": " & verified & " streamed overrides; " & bytes & " bytes")
            Catch ex As Exception
                record("error") = ex.ToString() : succeeded = False
                Console.WriteLine("FAILED " & packKind & ": " & ex.Message)
            End Try
            records.Add(record)
            File.WriteAllText(IO.Path.Combine(output, "samples.json"), New JObject From {{"succeeded", succeeded}, {"samples", records}, {"networkInstallPerformed", False}, {"gameLaunched", False}}.ToString())
        Next
        If Not succeeded Then Throw New InvalidOperationException("Large modpack sample verification failed.")
    End Function
End Module
