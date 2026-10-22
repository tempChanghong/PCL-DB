Imports System.Diagnostics
Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

' Explicit live opt-in. Uses a separate game root; never a user's live instance.
Friend Module ModrinthLiveChecks
    Friend Async Function RunAsync(path As String, output As String, reuse As IEnumerable(Of String)) As Task
        output = IO.Path.GetFullPath(output) : Directory.CreateDirectory(output)
        Dim package = ModrinthPackage.Read(path), root = IO.Path.Combine(output, "game", ".minecraft"), name = "Zombie-Invade-validation"
        Dim report As New JObject From {{"source", package.SourcePath}, {"archiveSha256", package.Sha256}, {"name", package.Name}, {"version", package.Version},
            {"minecraft", package.Minecraft}, {"loader", package.LoaderKind.ToString()}, {"loaderVersion", package.LoaderVersion},
            {"declaredFiles", package.References.Count}, {"overrideFiles", package.OverrideFiles.Count}, {"gameLaunched", False}, {"succeeded", False}}
        Dim clock = Stopwatch.StartNew(), lastReport As Long = -5000, latest As String = Nothing, cancelled = False
        Dim policy As New SourcePolicy With {.Preference = SourcePreference.OfficialFirst, .VersionPreference = SourcePreference.OfficialFirst}
        Dim installer As New ModrinthInstaller(policy)
        Dim userMod = IO.Path.Combine(root, "mods", "user-existing.jar"), userIni = IO.Path.Combine(root, "PCL", "Setup.ini")
        Directory.CreateDirectory(IO.Path.GetDirectoryName(userMod)) : File.WriteAllText(userMod, "preserve existing shared Mods")
        Directory.CreateDirectory(IO.Path.GetDirectoryName(userIni)) : File.WriteAllText(userIni, "LaunchArgumentIndieV2:0" & vbCrLf & "UserSetting:preserve")
        Try
            Using timeout As New CancellationTokenSource(TimeSpan.FromMinutes(25)), cancel As New CancellationTokenSource(), linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancel.Token)
                Dim progress As New ModrinthChecks.Progress(Sub(value)
                    SyncLock clock
                        latest = value.Stage & " " & value.Detail
                        If clock.ElapsedMilliseconds - lastReport >= 5000 Then
                            lastReport = clock.ElapsedMilliseconds
                            Console.WriteLine($"LIVE MR {clock.Elapsed.TotalSeconds:F0}s {latest}; {value.Completed}/{value.Total}; bytes={value.NetworkBytes}; reused={value.Reused}")
                        End If
                    End SyncLock
                    If value.Stage = "完成安装" Then cancel.Cancel()
                End Sub)
                Try
                    Await installer.InstallAsync(package, root, name, reuse, {}, progress, linked.Token, connections:=12).ConfigureAwait(False)
                Catch ex As OperationCanceledException When cancel.IsCancellationRequested AndAlso Not timeout.IsCancellationRequested
                    cancelled = True
                End Try
            End Using
            If Not cancelled OrElse Directory.Exists(IO.Path.Combine(root, "versions", name)) Then Throw New InvalidOperationException("Live MR cancelled import exposed a partial instance.")
            report("cancelledBeforePublication") = True
            Using timeout As New CancellationTokenSource(TimeSpan.FromMinutes(25))
                Dim result = Await installer.InstallAsync(package, root, name, reuse, {}, New ModrinthChecks.Progress(Sub(value)
                    SyncLock clock
                        latest = value.Stage & " " & value.Detail
                        If clock.ElapsedMilliseconds - lastReport >= 5000 Then
                            lastReport = clock.ElapsedMilliseconds
                            Console.WriteLine($"LIVE MR retry {clock.Elapsed.TotalSeconds:F0}s {latest}; {value.Completed}/{value.Total}; bytes={value.NetworkBytes}; reused={value.Reused}")
                        End If
                    End SyncLock
                End Sub), timeout.Token, connections:=12).ConfigureAwait(False)
                Dim checked As New JArray()
                For Each reference In package.References.Where(Function(item) item.Client = "required")
                    If Not SafeFiles.Matches(SafeFiles.UnderRoot(result.InstancePath, reference.Path), ModrinthChecks.Download(reference)) Then Throw New InvalidDataException("Live MR dual-hash mismatch: " & reference.Path)
                    checked.Add(New JObject From {{"path", reference.Path}, {"sha1", reference.Sha1}, {"sha512", reference.Sha512}})
                Next
                Dim overrideCount = 0
                Using archive = ZipFile.OpenRead(path)
                    For Each relative In package.OverrideFiles
                        Dim entry = archive.GetEntry("client-overrides/" & relative)
                        If entry Is Nothing Then entry = archive.GetEntry("overrides/" & relative)
                        If entry Is Nothing Then Throw New InvalidDataException("Live fixture override missing: " & relative)
                        Using input = entry.Open(), bytes As New MemoryStream()
                            input.CopyTo(bytes)
                            If ModrinthChecks.Hash(bytes.ToArray(), True) <> ModrinthChecks.Hash(File.ReadAllBytes(SafeFiles.UnderRoot(result.InstancePath, relative)), True) Then Throw New InvalidDataException("Live override mismatch: " & relative)
                        End Using
                        overrideCount += 1
                    Next
                End Using
                Dim ini = IO.Path.Combine(result.InstancePath, "PCL", "Setup.ini"), profile = JObject.Parse(File.ReadAllText(IO.Path.Combine(result.InstancePath, name & ".json")))
                If PclFolders.ReadIniValue(ini, "VersionArgumentIndieV2") <> "True" Then Throw New InvalidDataException("Live MR instance is not isolated.")
                If File.ReadAllText(userMod) <> "preserve existing shared Mods" OrElse File.ReadAllText(userIni) <> "LaunchArgumentIndieV2:0" & vbCrLf & "UserSetting:preserve" Then Throw New InvalidDataException("Live MR changed existing shared files/settings.")
                If result.NetworkBytes <> 0 Then Throw New InvalidOperationException("Live MR retry unexpectedly redownloaded verified payloads.")
                report("verifiedDownloads") = checked : report("verifiedOverrides") = overrideCount : report("retryNetworkBytes") = result.NetworkBytes
                report("reusedFiles") = result.Reused : report("instance") = result.InstancePath : report("mainClass") = CStr(profile("mainClass"))
                report("isolated") = True : report("sharedFilesPreserved") = True : report("succeeded") = True
            End Using
        Catch ex As Exception
            report("failure") = ex.ToString() : report("lastProgress") = latest
            Throw
        Finally
            report("elapsedSeconds") = clock.Elapsed.TotalSeconds
            File.WriteAllText(IO.Path.Combine(output, "report.json"), report.ToString(), New UTF8Encoding(False))
            Console.WriteLine("LIVE MR report: " & IO.Path.Combine(output, "report.json"))
        End Try
    End Function
End Module
