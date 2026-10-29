Imports System.IO
Imports System.IO.Compression
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

' A verified legacy installer gives us known private runtime bytes without Java.
' These cases also run against the old Core for before/after evidence.
Friend Module RuntimeLibraryChecks
    Private Const Relative As String = "libraries/fixture/runtime/1/runtime-1.jar"
    Private Const SecondRelative As String = "libraries/fixture/runtime-extra/2/runtime-extra-2.jar"
    Private ReadOnly Correct As Byte() = Encoding.UTF8.GetBytes("correct")
    Private ReadOnly Wrong As Byte() = Encoding.UTF8.GetBytes("corrupt")
    Private Sub Check(value As Boolean, label As String, passed As List(Of String))
        If Not value Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub
    Private Function Hash(value As Byte()) As String
        Using sha As New SHA1CryptoServiceProvider()
            Return BitConverter.ToString(sha.ComputeHash(value)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function
    Private Sub Entry(zip As ZipArchive, name As String, bytes As Byte())
        Using output = zip.CreateEntry(name).Open()
            output.Write(bytes, 0, bytes.Length)
        End Using
    End Sub
    Private Function CreateSelection(fixture As InstallationChecks.InstallFixture, sized As Boolean, hashed As Boolean, Optional remote As Boolean = False, Optional two As Boolean = False) As ComponentSelection
        Dim libraries As New JArray(), artifact As New JObject From {{"path", Relative.Substring(10)}, {"url", If(remote, fixture.Server.Url & "/runtime", "")}}
        If sized Then artifact("size") = Correct.Length
        If hashed Then artifact("sha1") = Hash(Correct)
        libraries.Add(New JObject From {{"name", "fixture:runtime:1"}, {"downloads", New JObject From {{"artifact", artifact}}}})
        If two Then libraries.Add(New JObject From {{"name", "fixture:runtime-extra:2"}, {"downloads", New JObject From {{"artifact", New JObject From {{"path", SecondRelative.Substring(10)}, {"url", ""}, {"size", Correct.Length}}}}}})
        Dim version As New JObject From {{"mainClass", "fixture.LegacyLoader"}, {"inheritsFrom", fixture.Entry.Id}, {"libraries", libraries}}
        Dim profile As New JObject From {{"minecraft", fixture.Entry.Id}, {"versionInfo", version}}
        If Not remote Then profile("install") = New JObject From {{"filePath", "universal.jar"}, {"path", "fixture:runtime:1"}}
        Using buffer As New MemoryStream()
            Using zip As New ZipArchive(buffer, ZipArchiveMode.Create, True)
                Entry(zip, "install_profile.json", Encoding.UTF8.GetBytes(profile.ToString()))
                If Not remote Then Entry(zip, "universal.jar", Correct)
                If two Then Entry(zip, "maven/" & SecondRelative.Substring(10), Correct)
            End Using
            fixture.Server.Files("/runtime-installer") = buffer.ToArray()
        End Using
        fixture.Server.Files("/runtime") = Correct
        Dim result As New ComponentSelection()
        result.Entries.Add(ComponentKind.Forge, New ComponentEntry With {.Kind = ComponentKind.Forge, .Minecraft = fixture.Entry.Id, .Version = "14.23.5.2859", .Url = fixture.Server.Url & "/runtime-installer", .Sha1 = Hash(fixture.Server.Files("/runtime-installer"))})
        Return result
    End Function
    Private Function Target(fixture As InstallationChecks.InstallFixture, Optional relativePath As String = Relative) As String
        Return SafeFiles.UnderRoot(fixture.Root, relativePath)
    End Function
    Private Sub Seed(path As String, bytes As Byte())
        Directory.CreateDirectory(IO.Path.GetDirectoryName(path)) : File.WriteAllBytes(path, bytes)
    End Sub
    Private Function Clean(fixture As InstallationChecks.InstallFixture) As Boolean
        Return Not Directory.EnumerateFiles(fixture.Root, "*.component-*", SearchOption.AllDirectories).Any() AndAlso
            {"components", "staging"}.All(Function(branch)
                                               Dim folder = IO.Path.Combine(fixture.Root, ".vanilla-downloader", branch)
                                               Return Not Directory.Exists(folder) OrElse Not Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any()
                                           End Function)
    End Function
    Friend Async Function RunAsync(root As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        If onlyCase = "install-runtime-all" Then onlyCase = Nothing
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "install-runtime-conflict" Then Await ConflictAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-runtime-reuse" Then Await ReuseAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-runtime-create" Then Await CreateAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-runtime-hashed" Then Await HashedAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-runtime-race" Then Await RaceAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-runtime-cancel" Then Await CancelAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-runtime-lease" Then Await LeaseAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-runtime-late-conflict" Then Await LateConflictAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-runtime-final-change" Then Await FinalChangeAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-runtime-final-lease" Then Await FinalLeaseAsync(root, passed, False)
        If onlyCase Is Nothing OrElse onlyCase = "install-runtime-final-cancel" Then Await FinalLeaseAsync(root, passed, True)
        If onlyCase Is Nothing OrElse onlyCase = "install-runtime-source-lease" Then Await SourceLeaseAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-runtime-hashed-final-all" Then Await HashedFinalAsync(root, passed)
        If passed.Count = 0 Then Throw New ArgumentException(onlyCase)
        Return passed
    End Function
    Private Async Function ConflictAsync(root As String, passed As List(Of String)) As Task
        For Each sized In {True, False}
            For Each remote In {False, True}
                Using fixture As New InstallationChecks.InstallFixture(root, "runtime-conflict-" & sized & remote, "1.12.2")
                    Dim selection = CreateSelection(fixture, sized, False, remote), path = Target(fixture)
                    Seed(path, Wrong)
                    Dim preferences = SafeFiles.UnderRoot(fixture.Root, "versions/kept/PCL/Setup.ini")
                    Seed(preferences, Encoding.UTF8.GetBytes("VersionArgumentIndieV2:False" & vbCrLf & "keep:用户设置"))
                    Dim existingJson = SafeFiles.UnderRoot(fixture.Root, "versions/kept/kept.json")
                    Dim existingBytes = Encoding.UTF8.GetBytes("{""id"":""kept"",""userSetting"":true}")
                    Seed(existingJson, existingBytes)
                    Dim oldPreferences = File.ReadAllBytes(preferences), failure As Exception = Nothing
                    Try
                        Await fixture.InstallAsync("conflict", selection)
                    Catch ex As Exception
                        failure = ex
                    End Try
                    Check(TypeOf failure Is IOException AndAlso failure.Message.Contains(Relative), "hashless runtime content conflict is explicit; sized=" & sized & ", remote=" & remote, passed)
                    Check(File.ReadAllBytes(path).SequenceEqual(Wrong) AndAlso Not Directory.Exists(IO.Path.Combine(fixture.Root, "versions", "conflict")), "hashless runtime conflict preserves existing bytes and publishes no instance; sized=" & sized & ", remote=" & remote, passed)
                    Check(File.ReadAllBytes(preferences).SequenceEqual(oldPreferences) AndAlso File.ReadAllBytes(existingJson).SequenceEqual(existingBytes) AndAlso Clean(fixture), "runtime conflict preserves unrelated instance metadata/PCL settings and cleans only private files; sized=" & sized & ", remote=" & remote, passed)
                    If remote Then Check(fixture.Server.Requests.ContainsKey("/runtime"), "hashless remote runtime is fetched from its source instead of trusting the shared donor; sized=" & sized, passed)
                End Using
            Next
        Next
    End Function
    Private Async Function ReuseAsync(root As String, passed As List(Of String)) As Task
        For Each sized In {True, False}
            Using fixture As New InstallationChecks.InstallFixture(root, "runtime-reuse-" & sized, "1.12.2")
                Dim selection = CreateSelection(fixture, sized, False), path = Target(fixture)
                Seed(path, Correct)
                Dim before = File.GetLastWriteTimeUtc(path)
                Using held As New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
                    Dim result = Await fixture.InstallAsync("same", selection)
                    Check(File.Exists(IO.Path.Combine(result.InstancePath, "same.json")) AndAlso File.GetLastWriteTimeUtc(path) = before, "matching hashless runtime is reused even while open for reading; sized=" & sized, passed)
                End Using
                Check(File.ReadAllBytes(path).SequenceEqual(Correct) AndAlso Clean(fixture), "matching hashless runtime retains its bytes and leaves no component temporary copy; sized=" & sized, passed)
            End Using
        Next
    End Function
    Private Async Function CreateAsync(root As String, passed As List(Of String)) As Task
        For Each sized In {True, False}
            Using fixture As New InstallationChecks.InstallFixture(root, "runtime-create-" & sized, "1.12.2")
                Dim result = Await fixture.InstallAsync("created", CreateSelection(fixture, sized, False))
                Check(File.ReadAllBytes(Target(fixture)).SequenceEqual(Correct), "missing hashless runtime is created from the private installer payload; sized=" & sized, passed)
                Dim profile = JObject.Parse(File.ReadAllText(IO.Path.Combine(result.InstancePath, "created.json")))
                Check(profile.SelectToken("libraries[0].downloads.artifact.sha1") Is Nothing AndAlso profile.SelectToken("libraries[0].downloads.artifact.sha512") Is Nothing, "local publication checksum is not forged into upstream launch metadata; sized=" & sized, passed)
                Check(File.ReadAllText(IO.Path.Combine(result.InstancePath, "PCL", "Setup.ini")).Contains("VersionArgumentIndieV2:True") AndAlso Clean(fixture), "runtime creation retains component isolation and private cleanup; sized=" & sized, passed)
            End Using
        Next
    End Function
    Private Async Function HashedAsync(root As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(root, "runtime-hashed", "1.12.2")
            Dim selection = CreateSelection(fixture, True, True), path = Target(fixture)
            Seed(path, Wrong)
            Dim result = Await fixture.InstallAsync("hashed", selection)
            Check(File.ReadAllBytes(path).SequenceEqual(Correct) AndAlso File.Exists(IO.Path.Combine(result.InstancePath, "hashed.json")), "upstream-hashed runtime retains the existing verified repair behavior", passed)
            Check(Clean(fixture), "hashed runtime repair removes its temporary copies", passed)
        End Using
    End Function
    Private Function CopyPresent(fixture As InstallationChecks.InstallFixture) As Boolean
        Dim parent = IO.Path.GetDirectoryName(Target(fixture))
        Return Directory.Exists(parent) AndAlso Directory.EnumerateFiles(parent, "runtime-1.jar.component-*").Any()
    End Function
    Private Async Function RaceAsync(root As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(root, "runtime-race", "1.12.2")
            Dim selection = CreateSelection(fixture, True, False), reached As Boolean = False, failure As Exception = Nothing
            Using probe As New RuntimeHashProbe(Sub()
                                                    If reached OrElse Not CopyPresent(fixture) Then Return
                                                    reached = True : Seed(Target(fixture), Wrong)
                                                End Sub)
                Try
                    Await fixture.InstallAsync("race", selection)
                Catch ex As Exception
                    failure = ex
                End Try
            End Using
            Check(reached AndAlso TypeOf failure Is IOException, "hashless publication notices a target created after its initial absence check", passed)
            Check(File.ReadAllBytes(Target(fixture)).SequenceEqual(Wrong) AndAlso Not Directory.Exists(IO.Path.Combine(fixture.Root, "versions", "race")) AndAlso Clean(fixture), "atomic runtime creation cannot replace a competing writer or leave a partial instance", passed)
            File.Delete(Target(fixture))
            Dim retry = Await fixture.InstallAsync("race", selection)
            Check(retry.NetworkBytes = 0 AndAlso File.ReadAllBytes(Target(fixture)).SequenceEqual(Correct), "retry after a runtime publication race reuses the checked installer", passed)
        End Using
    End Function
    Private Async Function CancelAsync(root As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(root, "runtime-cancel", "1.12.2"), cancel As New CancellationTokenSource()
            Dim selection = CreateSelection(fixture, False, False), reached As Boolean = False, cancelled As Boolean = False
            Using probe As New RuntimeHashProbe(Sub()
                                                    If reached OrElse Not CopyPresent(fixture) Then Return
                                                    reached = True : cancel.Cancel()
                                                End Sub)
                Try
                    Await fixture.InstallAsync("cancelled", selection, token:=cancel.Token)
                Catch ex As OperationCanceledException
                    cancelled = True
                End Try
            End Using
            Check(reached AndAlso cancelled AndAlso Not File.Exists(Target(fixture)), "cancel after private runtime copy verification does not publish that file", passed)
            Check(Not Directory.Exists(IO.Path.Combine(fixture.Root, "versions", "cancelled")) AndAlso Clean(fixture), "runtime publication cancellation leaves no instance or private copy", passed)
            Dim retry = Await fixture.InstallAsync("cancelled", selection)
            Check(retry.NetworkBytes = 0 AndAlso File.ReadAllBytes(Target(fixture)).SequenceEqual(Correct), "runtime publication cancellation can retry from the checked installer cache", passed)
        End Using
    End Function
    Private Async Function LeaseAsync(root As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(root, "runtime-lease", "1.12.2")
            Dim selection = CreateSelection(fixture, True, False, two:=True), reached As Boolean = False, blocked As Boolean = False
            Seed(Target(fixture), Correct)
            Using probe As New RuntimeHashProbe(Sub()
                                                    Dim parent = IO.Path.GetDirectoryName(Target(fixture, SecondRelative))
                                                    If reached OrElse Not Directory.Exists(parent) OrElse Not Directory.EnumerateFiles(parent, "runtime-extra-2.jar.component-*").Any() Then Return
                                                    reached = True
                                                    Try
                                                        File.WriteAllBytes(Target(fixture), Wrong)
                                                    Catch ex As IOException
                                                        blocked = True
                                                    End Try
                                                End Sub)
                Dim result = Await fixture.InstallAsync("leased", selection)
                Check(reached AndAlso blocked AndAlso File.Exists(IO.Path.Combine(result.InstancePath, "leased.json")), "checked earlier runtime is held for reading through the short multi-library publication phase", passed)
            End Using
            Check(File.ReadAllBytes(Target(fixture)).SequenceEqual(Correct) AndAlso File.ReadAllBytes(Target(fixture, SecondRelative)).SequenceEqual(Correct) AndAlso Clean(fixture), "runtime publication read leases preserve checked bytes and release after the phase", passed)
        End Using
    End Function
    Private Async Function LateConflictAsync(root As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(root, "runtime-late-conflict", "1.12.2")
            Dim selection = CreateSelection(fixture, True, False, two:=True), failure As Exception = Nothing
            Seed(Target(fixture, SecondRelative), Wrong)
            Try
                Await fixture.InstallAsync("late-conflict", selection)
            Catch ex As Exception
                failure = ex
            End Try
            Check(TypeOf failure Is IOException AndAlso failure.Message.Contains(SecondRelative), "a later hashless runtime conflict fails explicitly after an earlier runtime was published", passed)
            Check(File.ReadAllBytes(Target(fixture)).SequenceEqual(Correct) AndAlso File.ReadAllBytes(Target(fixture, SecondRelative)).SequenceEqual(Wrong), "late conflict retains completed correct shared content and the existing conflicting file", passed)
            Check(Not Directory.Exists(IO.Path.Combine(fixture.Root, "versions", "late-conflict")) AndAlso Clean(fixture), "late runtime conflict releases leases and cleans its unpublished instance", passed)
        End Using
    End Function
    Private Sub GateAsset(fixture As InstallationChecks.InstallFixture, gate As ManualResetEventSlim)
        Dim content = Encoding.UTF8.GetBytes("wait for runtime publication"), digest = Hash(content)
        Dim assetPath = "/" & digest.Substring(0, 2) & "/" & digest
        fixture.Server.Files(assetPath) = content : fixture.Server.Gates(assetPath) = gate
        Dim index = Encoding.UTF8.GetBytes(New JObject From {{"objects", New JObject From {{"fixture.dat", New JObject From {{"hash", digest}, {"size", content.Length}}}}}}.ToString())
        fixture.Server.Files("/index") = index
        Dim metadata = JObject.Parse(Encoding.UTF8.GetString(fixture.Server.Files("/version")))
        metadata("assetIndex")("size") = index.Length : metadata("assetIndex")("sha1") = Hash(index)
        fixture.Server.Files("/version") = Encoding.UTF8.GetBytes(metadata.ToString())
        fixture.Entry.Sha1 = Hash(fixture.Server.Files("/version"))
    End Sub
    Private Async Function FinalChangeAsync(root As String, passed As List(Of String)) As Task
        For Each afterComponents In {True, False}
            Using fixture As New InstallationChecks.InstallFixture(root, "runtime-final-change-" & afterComponents, "1.12.2"), gate As New ManualResetEventSlim(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(20))
                Dim selection = CreateSelection(fixture, True, False), reached As Boolean = False, failure As Exception = Nothing
                If afterComponents Then GateAsset(fixture, gate)
                Dim progress As New InlineProgress(Sub(value)
                                                        If reached OrElse If(afterComponents, value.Stage <> "组件安装完成", value.Phase <> "commit") Then Return
                                                        reached = True : File.WriteAllBytes(Target(fixture), Wrong) : gate.Set()
                                                    End Sub)
                Try
                    Await fixture.InstallAsync("changed", selection, progress:=progress, token:=timeout.Token)
                Catch ex As Exception
                    failure = ex
                Finally
                    gate.Set()
                End Try
                Check(reached AndAlso TypeOf failure Is IOException AndAlso Not timeout.IsCancellationRequested, "final instance commit detects changed runtime after components or final progress; components=" & afterComponents & "; reached=" & reached & "; timeout=" & timeout.IsCancellationRequested & "; failure=" & If(failure Is Nothing, "none", failure.ToString()), passed)
                Check(File.ReadAllBytes(Target(fixture)).SequenceEqual(Wrong) AndAlso Not Directory.Exists(IO.Path.Combine(fixture.Root, "versions", "changed")) AndAlso Clean(fixture), "late external runtime change is preserved and cannot be blessed as the expected digest; components=" & afterComponents, passed)
                File.WriteAllBytes(Target(fixture), Correct)
                Dim retry = Await fixture.InstallAsync("changed", selection)
                Check(retry.NetworkBytes = 0 AndAlso File.Exists(IO.Path.Combine(retry.InstancePath, "changed.json")), "retry after restoring the expected runtime uses the checked install cache; components=" & afterComponents, passed)
            End Using
        Next
    End Function
    Private Async Function FinalLeaseAsync(root As String, passed As List(Of String), cancelAtLease As Boolean) As Task
        Using fixture As New InstallationChecks.InstallFixture(root, "runtime-final-lease-" & cancelAtLease, "1.12.2"), cancel As New CancellationTokenSource()
            Dim selection = CreateSelection(fixture, True, False), finalPhase As Boolean = False, reached As Boolean = False, blocked As Boolean = False, cancelled As Boolean = False
            Dim progress As New InlineProgress(Sub(value)
                                                    If value.Phase = "commit" Then finalPhase = True
                                                End Sub)
            Using probe As New RuntimeHashProbe(Sub()
                                                    If Not finalPhase OrElse reached Then Return
                                                    reached = True
                                                    Try
                                                        File.WriteAllBytes(Target(fixture), Wrong)
                                                    Catch ex As IOException
                                                        blocked = True
                                                    End Try
                                                    If cancelAtLease Then cancel.Cancel()
                                                End Sub)
                Try
                    Await fixture.InstallAsync("guarded", selection, progress:=progress, token:=cancel.Token)
                Catch ex As OperationCanceledException
                    cancelled = True
                End Try
            End Using
            Check(reached AndAlso blocked AndAlso cancelled = cancelAtLease, "final runtime read lease blocks replacement and observes cancellation; cancel=" & cancelAtLease, passed)
            Check(File.ReadAllBytes(Target(fixture)).SequenceEqual(Correct) AndAlso Directory.Exists(IO.Path.Combine(fixture.Root, "versions", "guarded")) <> cancelAtLease AndAlso Clean(fixture), "final runtime lease preserves correct bytes and atomic instance result; cancel=" & cancelAtLease, passed)
            Using released As New FileStream(Target(fixture), FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                Check(released.Length = Correct.Length, "final runtime lease is released on success or cancellation; cancel=" & cancelAtLease, passed)
            End Using
            If cancelAtLease Then
                Dim retry = Await fixture.InstallAsync("guarded", selection)
                Check(retry.NetworkBytes = 0 AndAlso File.Exists(IO.Path.Combine(retry.InstancePath, "guarded.json")), "final runtime-lease cancellation retries without fetching checked payloads", passed)
            End If
        End Using
    End Function
    Private Async Function HashedFinalAsync(root As String, passed As List(Of String)) As Task
        Dim failures As New List(Of Exception)
        Dim mappings As New Dictionary(Of String, Type), blockBefore = FileMatchHashProbe.OnBlock, finalBefore = RuntimePublicationSha512.OnHash
        For Each name In {"SHA1", "System.Security.Cryptography.SHA1", "SHA512", "System.Security.Cryptography.SHA512", "MD5", "System.Security.Cryptography.MD5"}
            Using hash = DirectCast(CryptoConfig.CreateFromName(name), HashAlgorithm)
                mappings.Add(name, hash.GetType())
            End Using
        Next
        For Each existed In {False, True}
            For Each afterComponents In {True, False}
                For Each mutation In {"changed", "missing", "directory"}
                    Dim label = "existing=" & existed & ";components=" & afterComponents & ";mutation=" & mutation
                    Try
                        Await HashedFinalChangeAsync(root, existed, afterComponents, mutation, passed)
                    Catch ex As Exception
                        failures.Add(New InvalidOperationException("Hashed runtime final conflict: " & label, ex))
                    End Try
                Next
            Next
        Next
        For Each digest In {"sha1", "sha512", "md5", "etag"}
            For Each existed In {False, True}
                Try
                    HashedSnapshot(root, digest, existed, passed)
                Catch ex As Exception
                    failures.Add(New InvalidOperationException("Hashed runtime snapshot: " & digest & ";existing=" & existed, ex))
                End Try
            Next
        Next
        For Each boundary In {"incoming-new", "incoming-existing", "getter", "lease"}
            Try
                HashedOwnership(root, boundary, passed)
            Catch ex As Exception
                failures.Add(New InvalidOperationException("Runtime metadata ownership: " & boundary, ex))
            End Try
        Next
        For Each phase In {"constructor", "verify"}
            Try
                RuntimeLeaseCancellation(root, phase, passed)
            Catch ex As Exception
                failures.Add(New InvalidOperationException("Runtime final lease cancellation: " & phase, ex))
            End Try
        Next
        For Each pair In mappings
            Using hash = DirectCast(CryptoConfig.CreateFromName(pair.Key), HashAlgorithm)
                Check(hash.GetType() Is pair.Value, "runtime publication probes restore the original algorithm mapping: " & pair.Key, passed)
            End Using
        Next
        Check(blockBefore Is Nothing AndAlso finalBefore Is Nothing AndAlso Object.ReferenceEquals(FileMatchHashProbe.OnBlock, blockBefore) AndAlso Object.ReferenceEquals(RuntimePublicationSha512.OnHash, finalBefore),
            "runtime publication probes restore both process-local callbacks to their empty original state", passed)
        If failures.Count > 0 Then Throw New AggregateException("Hashed runtime final publication regressions", failures)
    End Function
    Private Async Function HashedFinalChangeAsync(root As String, existed As Boolean, afterComponents As Boolean, mutation As String, passed As List(Of String)) As Task
        Dim label = "existing=" & existed & ";components=" & afterComponents & ";mutation=" & mutation
        Using fixture As New InstallationChecks.InstallFixture(root, "runtime-hashed-final-" & existed & afterComponents & mutation, "1.12.2"), gate As New ManualResetEventSlim(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(20))
            Dim selection = CreateSelection(fixture, True, True), path = Target(fixture), reached = False, failure As Exception = Nothing
            If existed Then Seed(path, Correct)
            Dim existingJson = SafeFiles.UnderRoot(fixture.Root, "versions/kept/kept.json"), existingSettings = SafeFiles.UnderRoot(fixture.Root, "versions/kept/PCL/Setup.ini")
            Seed(existingJson, Encoding.UTF8.GetBytes("{""id"":""kept"",""userSetting"":true}"))
            Seed(existingSettings, Encoding.UTF8.GetBytes("VersionArgumentIndieV2:False" & vbCrLf & "keep:用户设置"))
            Dim jsonBytes = File.ReadAllBytes(existingJson), settingsBytes = File.ReadAllBytes(existingSettings)
            If afterComponents Then GateAsset(fixture, gate)
            Dim progress As New InlineProgress(Sub(value)
                                                    If reached OrElse If(afterComponents, value.Stage <> "组件安装完成", value.Phase <> "commit") Then Return
                                                    reached = True
                                                    If mutation = "changed" Then
                                                        File.WriteAllBytes(path, Wrong)
                                                    Else
                                                        File.Delete(path)
                                                        If mutation = "directory" Then
                                                            Directory.CreateDirectory(path) : File.WriteAllText(IO.Path.Combine(path, "keep.txt"), "external folder")
                                                        End If
                                                    End If
                                                    gate.Set()
                                                End Sub)
            Try
                Await fixture.InstallAsync("changed", selection, progress:=progress, token:=timeout.Token)
            Catch ex As Exception
                failure = ex
            Finally
                gate.Set()
            End Try
            Console.WriteLine("RUNTIME_HASHED_FINAL " & label & ";reached=" & reached & ";failure=" & If(failure?.GetType().FullName, "none") & ";published=" & Directory.Exists(IO.Path.Combine(fixture.Root, "versions", "changed")))
            Check(reached AndAlso (TypeOf failure Is IOException OrElse TypeOf failure Is UnauthorizedAccessException) AndAlso Not timeout.IsCancellationRequested,
                "declared runtime digest rejects external final-stage mutation: " & label, passed)
            Dim preserved = If(mutation = "changed", File.Exists(path) AndAlso File.ReadAllBytes(path).SequenceEqual(Wrong),
                If(mutation = "missing", Not File.Exists(path) AndAlso Not Directory.Exists(path), Directory.Exists(path) AndAlso File.ReadAllText(IO.Path.Combine(path, "keep.txt")) = "external folder"))
            Check(preserved AndAlso Not Directory.Exists(IO.Path.Combine(fixture.Root, "versions", "changed")) AndAlso Clean(fixture),
                "late declared-digest conflict preserves external state and cleans the unpublished instance: " & label, passed)
            Check(File.ReadAllBytes(existingJson).SequenceEqual(jsonBytes) AndAlso File.ReadAllBytes(existingSettings).SequenceEqual(settingsBytes),
                "declared-digest conflict retains existing instance metadata and isolation settings: " & label, passed)
            If mutation = "directory" Then
                File.Delete(IO.Path.Combine(path, "keep.txt")) : Directory.Delete(path)
            End If
            Seed(path, Correct)
            Dim installerRequests = fixture.Server.Requests("/runtime-installer"), retry = Await fixture.InstallAsync("changed", selection)
            Check(retry.NetworkBytes = 0 AndAlso fixture.Server.Requests("/runtime-installer") = installerRequests AndAlso
                File.ReadAllBytes(path).SequenceEqual(Correct) AndAlso File.Exists(IO.Path.Combine(retry.InstancePath, "changed.json")) AndAlso Clean(fixture),
                "retry after declared-digest conflict reuses checked installer content: " & label, passed)
        End Using
    End Function
    Private Sub HashedSnapshot(root As String, digest As String, existed As Boolean, passed As List(Of String))
        Dim area = IO.Path.Combine(root, "runtime-hashed-snapshot-" & digest & existed), work = IO.Path.Combine(area, "work"), destination = IO.Path.Combine(area, "root")
        Dim source = SafeFiles.UnderRoot(work, Relative), targetPath = SafeFiles.UnderRoot(destination, Relative), item As New DownloadFile With {.RelativePath = Relative, .Size = Correct.Length, .Category = "library"}
        Using hash As HashAlgorithm = If(digest = "sha512", DirectCast(New SHA512CryptoServiceProvider(), HashAlgorithm), If(digest = "md5" OrElse digest = "etag", DirectCast(New MD5CryptoServiceProvider(), HashAlgorithm), New SHA1CryptoServiceProvider()))
            Dim bytes = hash.ComputeHash(Correct)
            If digest = "etag" Then bytes = hash.ComputeHash(bytes)
            Dim text = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant()
            Select Case digest
                Case "sha512" : item.Sha512 = text
                Case "md5" : item.Md5 = text
                Case "etag" : item.S3SinglePartETag = text & "-1"
                Case Else : item.Sha1 = text
            End Select
        End Using
        Seed(source, Correct)
        If existed Then Seed(targetPath, Correct)
        Dim files As List(Of DownloadFile)
        Using publication As New ComponentRuntimePublication(destination)
            publication.Publish(work, {item}, CancellationToken.None)
            files = publication.FrozenFiles
        End Using
        Console.WriteLine("RUNTIME_HASHED_SNAPSHOT digest=" & digest & ";existing=" & existed & ";count=" & files.Count)
        Dim label = digest & ";existing=" & existed
        Check(files.Count = 1 AndAlso files(0).RelativePath = Relative, "declared runtime expectations survive work publication in either target branch: " & label, passed)
        Dim snapshot = files(0), originalSha1 = snapshot.Sha1, originalSha512 = snapshot.Sha512, originalMd5 = snapshot.Md5, originalETag = snapshot.S3SinglePartETag
        item.Size = Correct.Length + 1 : item.Sha1 = New String("0"c, 40) : item.Sha512 = Nothing : item.Md5 = Nothing : item.S3SinglePartETag = Nothing
        Check(snapshot.Size = Correct.Length AndAlso snapshot.Sha1 = originalSha1 AndAlso snapshot.Sha512 = originalSha512 AndAlso snapshot.Md5 = originalMd5 AndAlso snapshot.S3SinglePartETag = originalETag,
            "final runtime snapshot is independent of the caller's mutable metadata: " & label, passed)
        Dim reached = False, blocked = False, failure As Exception = Nothing, cancelAtLease = digest = "md5" OrElse digest = "etag"
        Using cancel As New CancellationTokenSource(), probe As New RuntimeFinalDigestProbe(Sub()
            If reached Then Return
            reached = True
            Try
                File.WriteAllBytes(targetPath, Wrong)
            Catch ex As IOException
                blocked = True
            End Try
            If cancelAtLease Then cancel.Cancel()
        End Sub)
            Try
                Using guard As New ComponentRuntimePublication.PublishedLease(destination, files, cancel.Token)
                    guard.Verify(cancel.Token)
                End Using
            Catch ex As Exception
                failure = ex
            End Try
            Check(reached AndAlso blocked AndAlso If(cancelAtLease, TypeOf failure Is OperationCanceledException AndAlso DirectCast(failure, OperationCanceledException).CancellationToken = cancel.Token, failure Is Nothing),
                "declared runtime final read lease blocks a competing writer and preserves cancellation identity: " & label, passed)
        End Using
        Check(File.ReadAllBytes(source).SequenceEqual(Correct) AndAlso File.ReadAllBytes(targetPath).SequenceEqual(Correct) AndAlso Not Directory.EnumerateFiles(area, "*.component-*", SearchOption.AllDirectories).Any(),
            "declared runtime read guard preserves private source and shared bytes without temporary copies: " & label, passed)
        Using lease As New FileStream(targetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            Check(lease.Length = Correct.Length, "declared runtime final read lease releases on success or cancellation: " & label, passed)
        End Using
        Using retry As New ComponentRuntimePublication.PublishedLease(destination, files, CancellationToken.None)
            retry.Verify(CancellationToken.None)
            Check(True, "declared runtime final guard retries with the original checked snapshot: " & label, passed)
        End Using
    End Sub
    Private Class RuntimeFinalDigestProbe
        Implements IDisposable
        Private ReadOnly _previous As New Dictionary(Of String, Type)
        Friend Sub New(callback As Action, Optional block As Action(Of String, Integer) = Nothing)
            For Each name In {"SHA1", "System.Security.Cryptography.SHA1", "SHA512", "System.Security.Cryptography.SHA512", "MD5", "System.Security.Cryptography.MD5"}
                Using hash = DirectCast(CryptoConfig.CreateFromName(name), HashAlgorithm)
                    _previous.Add(name, hash.GetType())
                End Using
            Next
            FileMatchHashProbe.OnBlock = Sub(algorithm, count)
                                             If count > 0 Then block?.Invoke(algorithm, count) : callback?.Invoke()
                                         End Sub
            CryptoConfig.AddAlgorithm(GetType(FileMatchProbeSha1), {"SHA1", "System.Security.Cryptography.SHA1"})
            CryptoConfig.AddAlgorithm(GetType(FileMatchProbeSha512), {"SHA512", "System.Security.Cryptography.SHA512"})
            CryptoConfig.AddAlgorithm(GetType(FileMatchProbeMd5), {"MD5", "System.Security.Cryptography.MD5"})
        End Sub
        Public Sub Dispose() Implements IDisposable.Dispose
            FileMatchHashProbe.OnBlock = Nothing
            For Each pair In _previous
                CryptoConfig.AddAlgorithm(pair.Value, {pair.Key})
            Next
        End Sub
    End Class
    Private Sub HashedOwnership(root As String, boundary As String, passed As List(Of String))
        Dim area = IO.Path.Combine(root, "runtime-ownership-" & boundary), work = IO.Path.Combine(area, "work"), destination = IO.Path.Combine(area, "root")
        Dim source = SafeFiles.UnderRoot(work, Relative), targetPath = SafeFiles.UnderRoot(destination, Relative), item As New DownloadFile With {.RelativePath = Relative, .Size = Correct.Length, .Category = "library"}
        Seed(source, Correct)
        If boundary = "incoming-existing" Then Seed(targetPath, Correct)
        Dim files As List(Of DownloadFile), reached = False, failure As Exception = Nothing
        Using publication As New ComponentRuntimePublication(destination)
            If boundary.StartsWith("incoming-", StringComparison.Ordinal) Then
                item.Sha1 = Hash(Correct)
                Using probe As New RuntimeFinalDigestProbe(Sub()
                    If reached Then Return
                    reached = True
                    item.RelativePath = SecondRelative : item.Size = Correct.Length + 1 : item.Sha1 = Hash(Wrong)
                    item.Sha512 = New String("a"c, 128) : item.Md5 = New String("b"c, 32) : item.S3SinglePartETag = New String("c"c, 32) & "-1" : item.Category = "changed"
                End Sub)
                    Try
                        publication.Publish(work, {item}, CancellationToken.None)
                    Catch ex As Exception
                        failure = ex
                    End Try
                End Using
                files = publication.FrozenFiles
                Console.WriteLine("RUNTIME_OWNERSHIP boundary=" & boundary & ";reached=" & reached & ";failure=" & If(failure?.GetType().FullName, "none") & ";count=" & files.Count)
                Check(reached AndAlso failure Is Nothing AndAlso files.Count = 1 AndAlso File.ReadAllBytes(targetPath).SequenceEqual(Correct),
                    "publication snapshots incoming declared metadata before any hash callback: " & boundary, passed)
                Dim snapshot = files(0)
                Check(snapshot.RelativePath = Relative AndAlso snapshot.Size = Correct.Length AndAlso snapshot.Sha1 = Hash(Correct) AndAlso snapshot.Sha512 Is Nothing AndAlso
                    snapshot.Md5 Is Nothing AndAlso snapshot.S3SinglePartETag Is Nothing AndAlso snapshot.Category = "library",
                    "incoming mutation cannot change any published integrity field: " & boundary, passed)
            Else
                publication.Publish(work, {item}, CancellationToken.None)
                files = publication.FrozenFiles
                If boundary = "getter" Then
                    Dim old = files(0).Sha512
                    files(0).Sha512 = New String("0"c, 128) : files(0).Size += 1 : files(0).RelativePath = SecondRelative
                    Dim independent = publication.FrozenFiles
                    Console.WriteLine("RUNTIME_OWNERSHIP boundary=getter;independent=" & (independent(0).Sha512 = old))
                    Check(independent.Count = 1 AndAlso independent(0).Sha512 = old AndAlso independent(0).Size = Correct.Length AndAlso independent(0).RelativePath = Relative,
                        "editing a returned frozen file cannot mutate the publisher's owned integrity metadata", passed)
                Else
                    Using guard As New ComponentRuntimePublication.PublishedLease(destination, files, CancellationToken.None)
                        files(0).Sha512 = New String("0"c, 128) : files(0).Size += 1 : files(0).RelativePath = SecondRelative
                        Try
                            guard.Verify(CancellationToken.None)
                        Catch ex As Exception
                            failure = ex
                        End Try
                    End Using
                    Console.WriteLine("RUNTIME_OWNERSHIP boundary=lease;failure=" & If(failure?.GetType().FullName, "none"))
                    Check(failure Is Nothing, "final lease verification owns a copy independent of later constructor-input edits", passed)
                End If
            End If
        End Using
        Check(File.ReadAllBytes(source).SequenceEqual(Correct) AndAlso File.ReadAllBytes(targetPath).SequenceEqual(Correct) AndAlso
            Not Directory.EnumerateFiles(area, "*.component-*", SearchOption.AllDirectories).Any(), "metadata ownership checks preserve source shared bytes and private cleanup: " & boundary, passed)
        Using lease As New FileStream(targetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            Check(lease.Length = Correct.Length, "metadata ownership checks release every published read handle: " & boundary, passed)
        End Using
    End Sub
    Private Sub RuntimeLeaseCancellation(root As String, phase As String, passed As List(Of String))
        Dim area = IO.Path.Combine(root, "runtime-final-lease-cancellation-" & phase), bytes = Enumerable.Range(0, 512 * 1024 + 19).Select(Function(index) CByte(index Mod 251)).ToArray()
        Dim path = SafeFiles.UnderRoot(area, Relative), digest As String
        Using hash As New SHA512CryptoServiceProvider()
            digest = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
        End Using
        Seed(path, bytes)
        Dim item As New DownloadFile With {.RelativePath = Relative, .Size = bytes.Length, .Sha512 = digest, .Category = "library"}
        Dim guard As ComponentRuntimePublication.PublishedLease = Nothing, failure As Exception = Nothing, blocked = False, hashed As Long = 0, blocks = 0
        Using cancel As New CancellationTokenSource()
            Try
                If phase = "verify" Then guard = New ComponentRuntimePublication.PublishedLease(area, {item}, CancellationToken.None)
                Using probe As New RuntimeFinalDigestProbe(Nothing, Sub(algorithm, count)
                    hashed += count : blocks += 1
                    If blocks <> 1 Then Return
                    Try
                        File.WriteAllBytes(path, Wrong)
                    Catch ex As IOException
                        blocked = True
                    End Try
                    cancel.Cancel()
                End Sub)
                    Try
                        If phase = "constructor" Then
                            guard = New ComponentRuntimePublication.PublishedLease(area, {item}, cancel.Token)
                        Else
                            guard.Verify(cancel.Token)
                        End If
                    Catch ex As Exception
                        failure = ex
                    End Try
                End Using
            Finally
                guard?.Dispose()
            End Try
            Console.WriteLine("RUNTIME_LEASE_CANCEL phase=" & phase & ";bytes=" & hashed & ";length=" & bytes.Length & ";blocks=" & blocks & ";blocked=" & blocked & ";failure=" & If(failure?.GetType().FullName, "none"))
            Check(TypeOf failure Is OperationCanceledException AndAlso DirectCast(failure, OperationCanceledException).CancellationToken = cancel.Token,
                "final runtime guard cancellation retains the original caller token: " & phase, passed)
            Check(hashed > 0 AndAlso hashed < bytes.Length AndAlso blocks > 0 AndAlso blocked,
                "final runtime guard stops hashing promptly while blocking shared-file replacement: " & phase, passed)
        End Using
        Check(File.ReadAllBytes(path).SequenceEqual(bytes), "cancelled final runtime guard preserves the original shared bytes: " & phase, passed)
        Using released As New FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            Check(released.Length = bytes.Length, "cancelled final runtime guard releases its read lease: " & phase, passed)
        End Using
        Using retry As New ComponentRuntimePublication.PublishedLease(area, {item}, CancellationToken.None)
            retry.Verify(CancellationToken.None)
            Check(True, "cancelled final runtime guard retries with an unchanged checked file: " & phase, passed)
        End Using
    End Sub
    Private Async Function SourceLeaseAsync(root As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(root, "runtime-source-lease", "1.12.2")
            Dim selection = CreateSelection(fixture, False, False), reached As Boolean = False, blocked As Boolean = False
            Using probe As New RuntimeHashProbe(Sub()
                                                    If reached Then Return
                                                    Dim work = IO.Path.Combine(fixture.Root, ".vanilla-downloader", "components")
                                                    If Not Directory.Exists(work) Then Return
                                                    Dim source = Directory.EnumerateFiles(work, "runtime-1.jar", SearchOption.AllDirectories).SingleOrDefault()
                                                    If source Is Nothing Then Return
                                                    reached = True
                                                    Try
                                                        File.WriteAllBytes(source, Wrong)
                                                    Catch ex As IOException
                                                        blocked = True
                                                    End Try
                                                End Sub)
                Dim result = Await fixture.InstallAsync("source-leased", selection)
                Check(reached AndAlso blocked AndAlso File.ReadAllBytes(Target(fixture)).SequenceEqual(Correct) AndAlso File.Exists(IO.Path.Combine(result.InstancePath, "source-leased.json")), "private runtime read lease binds local digest computation and publication to unchanged source bytes", passed)
            End Using
            Check(Clean(fixture), "private runtime read lease releases before work cleanup", passed)
        End Using
    End Function
    Private Class RuntimeHashProbe
        Implements IDisposable
        Private ReadOnly _previous As New Dictionary(Of String, Type)
        Friend Sub New(callback As Action)
            For Each aliasName In {"SHA512", "System.Security.Cryptography.SHA512"}
                Using algorithm = DirectCast(CryptoConfig.CreateFromName(aliasName), SHA512)
                    _previous.Add(aliasName, algorithm.GetType())
                End Using
            Next
            RuntimePublicationSha512.OnHash = callback
            CryptoConfig.AddAlgorithm(GetType(RuntimePublicationSha512), _previous.Keys.ToArray())
        End Sub
        Public Sub Dispose() Implements IDisposable.Dispose
            RuntimePublicationSha512.OnHash = Nothing
            For Each previous In _previous
                CryptoConfig.AddAlgorithm(previous.Value, {previous.Key})
            Next
        End Sub
    End Class
End Module

Public Class RuntimePublicationSha512
    Inherits SHA512
    Friend Shared OnHash As Action
    Private ReadOnly _inner As New SHA512Managed()
    Public Sub New()
        HashSizeValue = 512
    End Sub
    Public Overrides Sub Initialize()
        _inner.Initialize()
    End Sub
    Protected Overrides Sub HashCore(array As Byte(), ibStart As Integer, cbSize As Integer)
        _inner.TransformBlock(array, ibStart, cbSize, array, ibStart)
    End Sub
    Protected Overrides Function HashFinal() As Byte()
        _inner.TransformFinalBlock(New Byte() {}, 0, 0)
        Dim result = _inner.Hash
        OnHash?.Invoke()
        Return result
    End Function
    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then _inner.Dispose()
        MyBase.Dispose(disposing)
    End Sub
End Class
