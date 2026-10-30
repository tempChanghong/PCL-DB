Imports System.Diagnostics
Imports System.IO
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module LegacyPublicationHashCancellationChecks
    Private Const Relative As String = "mods/fixture.jar"
    Private ReadOnly Payload As Byte() = Enumerable.Range(0, 512 * 1024 + 19).Select(Function(index) CByte(index Mod 251)).ToArray()
    Private Sub Check(value As Boolean, label As String, passed As List(Of String))
        If Not value Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub
    Private Function Hash(bytes As Byte()) As String
        Using sha As New SHA1CryptoServiceProvider()
            Return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function
    Private Function Item(Optional relativePath As String = Relative) As DownloadFile
        Return New DownloadFile With {.RelativePath = relativePath, .Size = Payload.Length, .Sha1 = Hash(Payload), .Category = "component"}
    End Function
    Private Sub Seed(path As String, bytes As Byte())
        Directory.CreateDirectory(IO.Path.GetDirectoryName(path)) : File.WriteAllBytes(path, bytes)
    End Sub
    Private Function Mappings() As Dictionary(Of String, Type)
        Dim result As New Dictionary(Of String, Type)
        For Each name In {"SHA1", "System.Security.Cryptography.SHA1"}
            Using sha = DirectCast(CryptoConfig.CreateFromName(name), HashAlgorithm)
                result.Add(name, sha.GetType())
            End Using
        Next
        Return result
    End Function
    Private Class Probe
        Implements IDisposable
        Private ReadOnly _previous As Dictionary(Of String, Type) = Mappings()
        Private ReadOnly _callback As Action(Of String, Integer) = FileMatchHashProbe.OnBlock
        Friend Sub New(block As Action(Of String, Integer))
            FileMatchHashProbe.OnBlock = block
            CryptoConfig.AddAlgorithm(GetType(FileMatchProbeSha1), {"SHA1", "System.Security.Cryptography.SHA1"})
        End Sub
        Public Sub Dispose() Implements IDisposable.Dispose
            FileMatchHashProbe.OnBlock = _callback
            For Each pair In _previous
                CryptoConfig.AddAlgorithm(pair.Value, {pair.Key})
            Next
        End Sub
    End Class
    Private Function Owner(Optional requirePublish As Boolean = False) As String
        Dim frames = New StackTrace().GetFrames().Select(Function(frame) frame.GetMethod()).Where(Function(method) method?.DeclaringType Is GetType(LegacyResourcePublication)).ToList()
        If requirePublish AndAlso Not frames.Any(Function(method) method.Name = "Publish") Then Return Nothing
        Return frames.FirstOrDefault()?.Name
    End Function
    Private Function IsProtected(path As String) As Boolean
        If Not File.Exists(path) Then Return False
        Try
            Using lease As New FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            End Using
        Catch ex As IOException
            Return True
        End Try
        Return False
    End Function
    Private Function Released(paths As IEnumerable(Of String)) As Boolean
        For Each path In paths.Where(Function(value) File.Exists(value))
            Using lease As New FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            End Using
        Next
        Return True
    End Function
    Private Function Correct(path As String) As Boolean
        Return File.Exists(path) AndAlso File.ReadAllBytes(path).SequenceEqual(Payload)
    End Function

    Friend Async Function RunAsync(root As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        If onlyCase IsNot Nothing AndAlso onlyCase <> "legacy-publication-hash-all" Then Throw New ArgumentException(onlyCase)
        root = IO.Path.Combine(root, "legacy-publication-hash-cancellation") : Directory.CreateDirectory(root)
        Dim passed As New List(Of String), failures As New List(Of Exception), cases As New JObject, before = Mappings(), callback = FileMatchHashProbe.OnBlock
        For Each scope In {"validation-target", "existing-target", "staged-source", "new-held-target"}
            Try
                DirectCase(root, scope, passed, cases)
            Catch ex As Exception
                failures.Add(New InvalidOperationException("Legacy publication hash cancellation: " & scope, ex))
            End Try
        Next
        Try
            Repair(root, passed, cases)
        Catch ex As Exception
            failures.Add(New InvalidOperationException("Legacy repair hash cancellation", ex))
        End Try
        Try
            Controls(root, passed)
        Catch ex As Exception
            failures.Add(New InvalidOperationException("Legacy publication controls", ex))
        End Try
        Try
            Await InstallationAsync(root, passed, cases)
        Catch ex As Exception
            failures.Add(New InvalidOperationException("Legacy publication installation cancellation", ex))
        End Try
        For Each pair In before
            Using sha = DirectCast(CryptoConfig.CreateFromName(pair.Key), HashAlgorithm)
                Check(sha.GetType() Is pair.Value, "legacy publication restores its original algorithm mapping: " & pair.Key, passed)
            End Using
        Next
        Check(Object.ReferenceEquals(FileMatchHashProbe.OnBlock, callback), "legacy publication restores the process-local callback", passed)
        Dim report = IO.Path.Combine(root, "result.json")
        File.WriteAllText(report, New JObject From {{"Succeeded", failures.Count = 0}, {"Count", passed.Count}, {"Passed", New JArray(passed)},
            {"Cases", cases}, {"Failures", New JArray(failures.Select(Function(ex) ex.ToString()))}}.ToString())
        Console.WriteLine("LEGACY_PUBLICATION_HASH_RESULT=" & report)
        If failures.Count > 0 Then Throw New AggregateException("Legacy publication hash cancellation regressions", failures)
        Return passed
    End Function

    Private Sub DirectCase(root As String, scope As String, passed As List(Of String), cases As JObject)
        Dim area = IO.Path.Combine(root, scope), destination = IO.Path.Combine(area, "shared"), stage = IO.Path.Combine(area, "stage")
        Dim source = SafeFiles.UnderRoot(stage, Relative), target = SafeFiles.UnderRoot(destination, Relative), expected = Item()
        Directory.CreateDirectory(destination) : Seed(source, Payload)
        Dim existing = scope = "validation-target" OrElse scope = "existing-target", heldNew = scope = "new-held-target", repair = scope = "existing-target"
        Dim stamp As New DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc)
        If existing Then Seed(target, Payload) : File.SetLastWriteTimeUtc(target, stamp)
        Dim settings = IO.Path.Combine(destination, "versions", "kept", "PCL", "Setup.ini"), settingsBytes = Encoding.UTF8.GetBytes("VersionArgumentIndieV2:False" & vbCrLf & "keep:用户设置")
        Seed(settings, settingsBytes)
        Dim expectedBefore = JObject.FromObject(expected).ToString(), failure As Exception = Nothing, returned As Boolean, originalToken As Boolean
        Dim bytes As Long, rollbackBytes As Long, blocks As Integer, blocked As Boolean, actualOwner As String = Nothing
        Dim targetBeforeDispose As Boolean, sourceBeforeDispose As Boolean, heldBeforeDispose As Boolean
        Using cancel As New CancellationTokenSource(), publication As New LegacyResourcePublication(destination, stage, "fixture", New List(Of DownloadFile) From {expected}, False, repair, holdTargets:=scope <> "staged-source")
            Using probe As New Probe(Sub(algorithm, count)
                                         If count <= 0 OrElse algorithm <> "sha1" Then Return
                                         Dim ownerName = Owner()
                                         If ownerName = "Dispose" Then rollbackBytes += count : Return
                                         If Owner(requirePublish:=True) Is Nothing Then Return
                                         If scope = "validation-target" Then
                                             If ownerName <> "Validate" AndAlso ownerName <> "ValidateCore" Then Return
                                         ElseIf ownerName <> "Publish" Then
                                             Return
                                         End If
                                         If heldNew AndAlso (File.Exists(source) OrElse Not File.Exists(target)) Then Return
                                         Dim active = If(scope = "staged-source", source, target)
                                         If Not IsProtected(active) Then Return
                                         bytes += count : blocks += 1 : actualOwner = ownerName
                                         If blocks <> 1 Then Return
                                         blocked = True : cancel.Cancel()
                                     End Sub)
                Try
                    publication.Publish(cancel.Token) : returned = True
                Catch ex As Exception
                    failure = ex
                End Try
                originalToken = TypeOf failure Is OperationCanceledException AndAlso DirectCast(failure, OperationCanceledException).CancellationToken = cancel.Token
                targetBeforeDispose = File.Exists(target) : sourceBeforeDispose = File.Exists(source)
                heldBeforeDispose = IsProtected(target)
                ' Keep the probe active through disposal: rollback must use an
                ' uncancelled check after releasing our long-lived target lease.
                publication.Dispose()
            End Using
        End Using
        Dim targetPreserved = If(existing, Correct(target) AndAlso File.GetLastWriteTimeUtc(target) = stamp, Not File.Exists(target))
        Dim sourcePreserved = If(heldNew, Not File.Exists(source), Correct(source)), releasedAfterCancel = Released({source, target, settings})
        Dim settingsPreserved = File.ReadAllBytes(settings).SequenceEqual(settingsBytes) AndAlso JObject.FromObject(expected).ToString() = expectedBefore
        Dim observation As New JObject From {{"Algorithm", "sha1"}, {"Owner", actualOwner}, {"Bytes", bytes}, {"Length", Payload.Length}, {"Blocks", blocks}, {"RollbackBytes", rollbackBytes},
            {"OriginalToken", originalToken}, {"Returned", returned}, {"Exception", failure?.GetType().FullName}, {"WriteBlocked", blocked},
            {"TargetBeforeDispose", targetBeforeDispose}, {"SourceBeforeDispose", sourceBeforeDispose}, {"TargetHeldBeforeDispose", heldBeforeDispose},
            {"TargetPreservedOrRolledBack", targetPreserved}, {"SourceStateCorrect", sourcePreserved}, {"LeasesReleased", releasedAfterCancel}, {"SettingsAndExpectationsPreserved", settingsPreserved}}
        cases(scope) = observation
        If Not File.Exists(source) Then Seed(source, Payload)
        Using fresh As New CancellationTokenSource(), retry As New LegacyResourcePublication(destination, stage, "fixture", New List(Of DownloadFile) From {expected}, False, repair, holdTargets:=True)
            retry.Publish(fresh.Token) : retry.Complete()
        End Using
        observation("RetrySuccess") = Correct(target) AndAlso Not File.Exists(source)
        observation("RetryPreservesSettingsAndReleasesLeases") = File.ReadAllBytes(settings).SequenceEqual(settingsBytes) AndAlso Released({target, settings}) AndAlso
            (Not existing OrElse File.GetLastWriteTimeUtc(target) = stamp)
        Console.WriteLine("LEGACY_PUBLICATION_HASH_CANCEL scope=" & scope & " bytes=" & bytes & " length=" & Payload.Length & " blocks=" & blocks & " rollback=" & rollbackBytes & " originalToken=" & originalToken)
        Check(blocks > 0 AndAlso blocked AndAlso actualOwner IsNot Nothing, "legacy publication cancellation reaches the intended protected hash: " & scope, passed)
        Check(originalToken AndAlso Not returned, "legacy publication cancellation preserves the original caller token without success: " & scope, passed)
        Check(targetBeforeDispose = (existing OrElse heldNew) AndAlso sourceBeforeDispose = Not heldNew AndAlso If(heldNew OrElse repair, heldBeforeDispose, True), "cancelled legacy publication has the expected move and target-lease state before disposal: " & scope, passed)
        Check(targetPreserved AndAlso sourcePreserved AndAlso rollbackBytes = If(heldNew, CLng(Payload.Length), 0L), "legacy disposal retains prior files and completes only its owned rollback despite cancellation: " & scope, passed)
        Check(settingsPreserved AndAlso releasedAfterCancel, "cancelled legacy publication preserves settings/expectations and releases every file lease: " & scope, passed)
        Check(CBool(observation("RetrySuccess")), "legacy publication retries with a fresh token and the complete staged payload: " & scope, passed)
        Check(CBool(observation("RetryPreservesSettingsAndReleasesLeases")), "legacy publication retry preserves user settings and existing timestamps and releases commit leases: " & scope, passed)
        Check(bytes > 0 AndAlso bytes < Payload.Length, "legacy publication stops the cancelled hash before reading the complete file: " & scope, passed)
    End Sub

    Private Sub Repair(root As String, passed As List(Of String), cases As JObject)
        Dim destination = IO.Path.Combine(root, "repair", "shared"), stage = IO.Path.Combine(root, "repair", "stage"), first = Item("resources/first.bin"), second = Item("resources/second.bin")
        Dim sourceA = SafeFiles.UnderRoot(stage, first.RelativePath), sourceB = SafeFiles.UnderRoot(stage, second.RelativePath)
        Dim targetA = SafeFiles.UnderRoot(destination, first.RelativePath), targetB = SafeFiles.UnderRoot(destination, second.RelativePath)
        Seed(sourceA, Payload) : Seed(sourceB, Payload) : Seed(targetA, Encoding.UTF8.GetBytes("old damaged alias"))
        Dim settings = IO.Path.Combine(destination, "PCL", "Setup.ini"), settingsBytes = Encoding.UTF8.GetBytes("LaunchArgumentIndieV2:0" & vbCrLf & "keep:用户设置")
        Seed(settings, settingsBytes)
        Dim bytes As Long, blocks As Integer, firstCommitted As Boolean, blocked As Boolean, originalToken As Boolean, failure As Exception = Nothing
        Using cancel As New CancellationTokenSource(), publication As New LegacyResourcePublication(destination, stage, "fixture", New List(Of DownloadFile) From {first, second}, False, True)
            Using probe As New Probe(Sub(algorithm, count)
                                         If count <= 0 OrElse algorithm <> "sha1" OrElse Owner(requirePublish:=True) <> "Publish" OrElse Not IsProtected(sourceB) Then Return
                                         bytes += count : blocks += 1
                                         If blocks <> 1 Then Return
                                         firstCommitted = Correct(targetA) AndAlso Not File.Exists(sourceA) : blocked = True : cancel.Cancel()
                                     End Sub)
                Try
                    publication.Publish(cancel.Token)
                Catch ex As Exception
                    failure = ex
                End Try
                originalToken = TypeOf failure Is OperationCanceledException AndAlso DirectCast(failure, OperationCanceledException).CancellationToken = cancel.Token
                publication.Dispose()
            End Using
        End Using
        Dim retained = Correct(targetA) AndAlso Not File.Exists(targetB) AndAlso Correct(sourceB), kept = File.ReadAllBytes(settings).SequenceEqual(settingsBytes) AndAlso Released({targetA, sourceB, settings})
        Dim observation As New JObject From {{"Bytes", bytes}, {"Length", Payload.Length}, {"Blocks", blocks}, {"OriginalToken", originalToken}, {"FirstCommittedBeforeCancellation", firstCommitted},
            {"LaterSourceProtected", blocked}, {"CompletedRepairRetained", retained}, {"SettingsPreservedAndLeasesReleased", kept}, {"Exception", failure?.GetType().FullName}}
        cases("repair-retains-first") = observation
        Using fresh As New CancellationTokenSource(), retry As New LegacyResourcePublication(destination, stage, "fixture", New List(Of DownloadFile) From {first, second}, False, True)
            retry.Publish(fresh.Token) : retry.Complete()
        End Using
        observation("RetrySuccess") = Correct(targetA) AndAlso Correct(targetB) AndAlso Not File.Exists(sourceA) AndAlso Not File.Exists(sourceB) AndAlso File.ReadAllBytes(settings).SequenceEqual(settingsBytes) AndAlso Released({targetA, targetB, settings})
        Console.WriteLine("LEGACY_PUBLICATION_HASH_CANCEL scope=repair bytes=" & bytes & " length=" & Payload.Length & " blocks=" & blocks & " originalToken=" & originalToken)
        Check(firstCommitted AndAlso blocked AndAlso blocks > 0, "repair cancellation reaches a protected later source after the first alias has committed", passed)
        Check(originalToken, "later repair hash cancellation preserves the original token", passed)
        Check(retained, "repair disposal keeps the completed first fix and the uncommitted second staged payload", passed)
        Check(kept, "repair cancellation preserves original settings and releases all source/target leases", passed)
        Check(CBool(observation("RetrySuccess")), "partial repair retries without losing the first completed fix or existing settings", passed)
        Check(bytes > 0 AndAlso bytes < Payload.Length, "later repair source hashing stops before reading the complete payload", passed)
    End Sub

    Private Sub Controls(root As String, passed As List(Of String))
        For Each scope In {"completed", "target-conflict", "source-conflict"}
            Dim destination = IO.Path.Combine(root, scope, "shared"), stage = IO.Path.Combine(root, scope, "stage"), expected = Item()
            Dim source = SafeFiles.UnderRoot(stage, Relative), target = SafeFiles.UnderRoot(destination, Relative), failure As Exception = Nothing
            Directory.CreateDirectory(destination) : Seed(source, Payload)
            Dim original = Encoding.UTF8.GetBytes("preserve external conflict")
            If scope = "target-conflict" Then Seed(target, original)
            If scope = "source-conflict" Then expected.Sha1 = New String("0"c, 40)
            Using cancel As New CancellationTokenSource(), publication As New LegacyResourcePublication(destination, stage, "fixture", New List(Of DownloadFile) From {expected}, False, False, holdTargets:=True)
                Try
                    publication.Publish(cancel.Token)
                    publication.Complete() : cancel.Cancel()
                Catch ex As Exception
                    failure = ex
                End Try
            End Using
            If scope = "completed" Then
                Check(failure Is Nothing AndAlso Correct(target) AndAlso Not File.Exists(source), "Complete followed by late cancellation retains the committed shared file", passed)
                Check(Released({target}), "completed publication releases held target leases after late cancellation", passed)
            Else
                Check(If(scope = "target-conflict", TypeOf failure Is IOException AndAlso File.ReadAllBytes(target).SequenceEqual(original), TypeOf failure Is InvalidDataException AndAlso Not File.Exists(target)) AndAlso Correct(source) AndAlso Released({target, source}), "uncancelled legacy publication rejects and preserves the original conflict: " & scope, passed)
                If scope = "target-conflict" Then File.WriteAllBytes(target, Payload) Else expected.Sha1 = Hash(Payload)
                Using fresh As New CancellationTokenSource(), retry As New LegacyResourcePublication(destination, stage, "fixture", New List(Of DownloadFile) From {expected}, False, False, holdTargets:=True)
                    retry.Publish(fresh.Token) : retry.Complete()
                End Using
                Check(Correct(target) AndAlso Not File.Exists(source) AndAlso Released({target}), "legacy publication retries once the original content conflict is corrected: " & scope, passed)
            End If
        Next
    End Sub

    Private Async Function InstallationAsync(root As String, passed As List(Of String), cases As JObject) As Task
        Using session As New IsolationResourceChecks.Session(root, "legacy-publication-hash")
            session.SetGlobal("0")
            Dim name = "sounds/legacy-hash.ogg", digest = Hash(Payload), route = "/" & digest.Substring(0, 2) & "/" & digest
            IsolationResourceChecks.Configure(session.Fixture, New JObject From {{name, New JObject From {{"hash", digest}, {"size", Payload.Length}}}})
            session.Fixture.Server.Files(route) = Payload
            Dim resource = session.Resource(False, name), virtualPath = IO.Path.Combine(session.Root, "assets", "virtual", "legacy", name)
            Dim canonical = IO.Path.Combine(session.Root, "assets", "objects", digest.Substring(0, 2), digest)
            Dim existing = IO.Path.Combine(session.Root, "versions", "kept", "kept.json"), settings = IO.Path.Combine(session.Root, "versions", "kept", "PCL", "Setup.ini")
            Dim jsonBytes = Encoding.UTF8.GetBytes("{""id"":""kept"",""setting"":true}"), settingsBytes = Encoding.UTF8.GetBytes("VersionArgumentIndieV2:False" & vbCrLf & "keep:用户设置")
            session.Put(existing, jsonBytes) : session.Put(settings, settingsBytes)
            Dim userResource = IO.Path.Combine(session.Root, "resources", "user.dat") : session.Put(userResource, session.Bad)
            Dim globalBytes = File.ReadAllBytes(session.GlobalPath), bytes As Long, blocks As Integer, rollbackBytes As Long, privatePath As String = Nothing
            Dim blocked As Boolean, failure As Exception = Nothing, returned As Boolean, originalToken As Boolean
            Using cancel As New CancellationTokenSource()
                Using probe As New Probe(Sub(algorithm, count)
                                             If count <= 0 OrElse algorithm <> "sha1" Then Return
                                             If Owner() = "Dispose" Then rollbackBytes += count : Return
                                             If Owner(requirePublish:=True) <> "Publish" OrElse File.Exists(resource) Then Return
                                             Dim staging = IO.Path.Combine(session.Root, ".vanilla-downloader", "staging")
                                             Dim candidates = Directory.EnumerateFiles(staging, "legacy-hash.ogg", SearchOption.AllDirectories).Where(Function(path) path.EndsWith("\resources\sounds\legacy-hash.ogg", StringComparison.OrdinalIgnoreCase)).ToList()
                                             If candidates.Count <> 1 OrElse Not IsProtected(candidates(0)) Then Return
                                             privatePath = candidates(0) : bytes += count : blocks += 1
                                             If blocks = 1 Then blocked = True : cancel.Cancel()
                                         End Sub)
                    Try
                        Await session.StartAsync(token:=cancel.Token) : returned = True
                    Catch ex As Exception
                        failure = ex
                    End Try
                    originalToken = TypeOf failure Is OperationCanceledException AndAlso DirectCast(failure, OperationCanceledException).CancellationToken = cancel.Token
                End Using
                ' Virtual aliases are completed asset downloads in the shared root.
                ' Only resources/* belongs to this short instance publication phase.
                Dim noPublication = Not File.Exists(session.Json) AndAlso Not File.Exists(session.Ini) AndAlso Not File.Exists(resource)
                Dim kept = File.ReadAllBytes(session.GlobalPath).SequenceEqual(globalBytes) AndAlso File.ReadAllBytes(existing).SequenceEqual(jsonBytes) AndAlso File.ReadAllBytes(settings).SequenceEqual(settingsBytes) AndAlso File.ReadAllBytes(userResource).SequenceEqual(session.Bad)
                Dim canonicalKept = Correct(canonical) AndAlso Correct(virtualPath), clean = session.Released() AndAlso Released({existing, settings, userResource, canonical, virtualPath})
                Dim observation As New JObject From {{"Bytes", bytes}, {"Length", Payload.Length}, {"Blocks", blocks}, {"RollbackBytes", rollbackBytes}, {"OriginalToken", originalToken},
                    {"Returned", returned}, {"Exception", failure?.GetType().FullName}, {"PrivateSource", privatePath}, {"PrivateSourceRemoved", privatePath IsNot Nothing AndAlso Not File.Exists(privatePath)},
                    {"WriteBlocked", blocked}, {"NoPublication", noPublication}, {"ExistingSettingsAndResourcesPreserved", kept}, {"CanonicalObjectAndCompletedVirtualAliasPreserved", canonicalKept}, {"PrivateTreeCleanAndLeasesReleased", clean}}
                cases("installation") = observation
                Using fresh As New CancellationTokenSource()
                    Dim retry = Await session.StartAsync(token:=fresh.Token)
                    observation("RetrySuccess") = File.Exists(session.Json) AndAlso File.Exists(IO.Path.Combine(retry.InstancePath, "legacy.jar")) AndAlso Correct(resource) AndAlso Correct(virtualPath) AndAlso PclFolders.ReadIniValue(session.Ini, "VersionArgumentIndieV2") = "False"
                    observation("RetryNetworkBytes") = retry.NetworkBytes
                End Using
                observation("RetrySettingsPreservedAndClean") = File.ReadAllBytes(session.GlobalPath).SequenceEqual(globalBytes) AndAlso File.ReadAllBytes(existing).SequenceEqual(jsonBytes) AndAlso File.ReadAllBytes(settings).SequenceEqual(settingsBytes) AndAlso session.Released()
                observation("ObjectRequests") = session.Fixture.Server.Requests(route)
                Console.WriteLine("LEGACY_PUBLICATION_HASH_CANCEL scope=installation bytes=" & bytes & " length=" & Payload.Length & " blocks=" & blocks & " rollback=" & rollbackBytes & " originalToken=" & originalToken)
                Check(blocks > 0 AndAlso blocked AndAlso privatePath IsNot Nothing, "real installation cancellation reaches the protected remapped-resource source hash", passed)
                Check(originalToken AndAlso Not returned, "legacy publication installation cancellation preserves the original caller token", passed)
                Check(noPublication, "cancelled legacy publication leaves no owned resource alias or published instance/settings", passed)
                Check(kept, "cancelled legacy publication preserves original global/instance settings and unrelated resources", passed)
                Check(canonicalKept AndAlso clean, "cancelled legacy publication keeps completed canonical/virtual content and releases workers/private files/root leases", passed)
                Check(CBool(observation("RetrySuccess")), "legacy publication installation retries the same instance with correct shared isolation and both aliases", passed)
                Check(CLng(observation("RetryNetworkBytes")) = 0 AndAlso CInt(observation("ObjectRequests")) = 1 AndAlso CBool(observation("RetrySettingsPreservedAndClean")), "legacy publication retry reuses canonical content and preserves old settings with clean worker shutdown", passed)
                Check(bytes > 0 AndAlso bytes < Payload.Length, "real legacy publication stops the cancelled staged-resource hash before EOF", passed)
            End Using
        End Using
    End Function
End Module
