Imports System.Diagnostics
Imports System.IO
Imports System.Security.Cryptography
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module SharedModsHashCancellationChecks
    Private ReadOnly Payload As Byte() = Enumerable.Range(0, 512 * 1024 + 19).Select(Function(index) CByte(index Mod 251)).ToArray()

    Private Sub Check(value As Boolean, label As String, passed As List(Of String))
        If Not value Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub
    Private Function Hash(bytes As Byte(), Optional md5 As Boolean = False) As String
        Using algorithm As HashAlgorithm = If(md5, DirectCast(New MD5CryptoServiceProvider(), HashAlgorithm), New SHA1CryptoServiceProvider())
            Return BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function
    Private Function Mappings() As Dictionary(Of String, Type)
        Dim result As New Dictionary(Of String, Type)
        For Each name In {"SHA1", "System.Security.Cryptography.SHA1", "MD5", "System.Security.Cryptography.MD5"}
            Using algorithm = DirectCast(CryptoConfig.CreateFromName(name), HashAlgorithm)
                result.Add(name, algorithm.GetType())
            End Using
        Next
        Return result
    End Function
    Private Class Probe
        Implements IDisposable
        Private ReadOnly _previous As Dictionary(Of String, Type) = Mappings()
        Private ReadOnly _callback As Action(Of String, Integer) = FileMatchHashProbe.OnBlock
        Private ReadOnly _creating As Action = SharedModsHashProbeState.OnCreate
        Friend Sub New(block As Action(Of String, Integer), Optional creating As Action = Nothing)
            FileMatchHashProbe.OnBlock = block : SharedModsHashProbeState.OnCreate = creating
            CryptoConfig.AddAlgorithm(GetType(SharedModsProbeSha1), {"SHA1", "System.Security.Cryptography.SHA1"})
            CryptoConfig.AddAlgorithm(GetType(FileMatchProbeMd5), {"MD5", "System.Security.Cryptography.MD5"})
        End Sub
        Public Sub Dispose() Implements IDisposable.Dispose
            FileMatchHashProbe.OnBlock = _callback : SharedModsHashProbeState.OnCreate = _creating
            For Each pair In _previous
                CryptoConfig.AddAlgorithm(pair.Value, {pair.Key})
            Next
        End Sub
    End Class
    Private Function SnapshotStack() As Boolean
        Return New StackTrace().GetFrames().Any(Function(frame) frame.GetMethod()?.DeclaringType Is GetType(SharedModsPublication) AndAlso frame.GetMethod().Name = "CheckedFiles")
    End Function
    Private Function IsProtected(path As String) As Boolean
        Try
            Using writer As New FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            End Using
        Catch ex As IOException
            Return True
        End Try
        Return False
    End Function
    Private Function Released(path As String) As Boolean
        Using writer As New FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            Return True
        End Using
    End Function
    Private Function BoundaryProtection(path As String) As Integer
        Dim blocked As Integer
        ' SHA1 is created only after the MD5 matcher has returned. Each attempt must
        ' still be blocked by CheckedFiles' original outer lease, between the hashes.
        Try
            Using writer As New FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)
            End Using
        Catch ex As IOException
            blocked += 1
        End Try
        Try
            File.Delete(path)
        Catch ex As IOException
            blocked += 1
        End Try
        Try
            File.Move(path, path & ".external")
        Catch ex As IOException
            blocked += 1
        End Try
        Return blocked
    End Function
    Private Function Expected(bytes As Byte(), hashed As Boolean) As DownloadFile
        Return New DownloadFile With {.RelativePath = "mods/fixture.jar", .Size = bytes.Length, .Md5 = If(hashed, Hash(bytes, True), Nothing), .Category = "component"}
    End Function
    Private Function FreezeCorrect(result As List(Of DownloadFile), item As DownloadFile, bytes As Byte()) As Boolean
        Return result.Count = 1 AndAlso result(0) IsNot item AndAlso result(0).RelativePath = item.RelativePath AndAlso
            result(0).Size = bytes.Length AndAlso result(0).Sha1 = Hash(bytes) AndAlso result(0).Category = "component" AndAlso result(0).Md5 Is Nothing
    End Function

    Friend Async Function RunAsync(root As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        If onlyCase IsNot Nothing AndAlso onlyCase <> "shared-mods-hash-all" Then Throw New ArgumentException(onlyCase)
        root = IO.Path.Combine(root, "shared-mods-hash-cancellation") : Directory.CreateDirectory(root)
        Dim passed As New List(Of String), failures As New List(Of Exception), cases As New JObject, before = Mappings()
        Dim callback = FileMatchHashProbe.OnBlock, creating = SharedModsHashProbeState.OnCreate
        For Each scope In {"remote-md5", "hashed-freeze", "hashless-optifine-freeze"}
            Try
                DirectCase(root, scope, passed, cases)
            Catch ex As Exception
                failures.Add(New InvalidOperationException("Shared Mods hash cancellation: " & scope, ex))
            End Try
        Next
        Try
            Await InstallationAsync(root, passed, cases)
        Catch ex As Exception
            failures.Add(New InvalidOperationException("Shared Mods installation cancellation", ex))
        End Try
        Try
            Controls(root, passed)
        Catch ex As Exception
            failures.Add(New InvalidOperationException("Shared Mods hash controls", ex))
        End Try
        For Each pair In before
            Using algorithm = DirectCast(CryptoConfig.CreateFromName(pair.Key), HashAlgorithm)
                Check(algorithm.GetType() Is pair.Value, "shared Mods hashing restores its original algorithm mapping: " & pair.Key, passed)
            End Using
        Next
        Check(Object.ReferenceEquals(FileMatchHashProbe.OnBlock, callback), "shared Mods hashing restores the process-local block callback", passed)
        Check(Object.ReferenceEquals(SharedModsHashProbeState.OnCreate, creating), "shared Mods hashing restores the provider-construction callback", passed)
        Dim report = IO.Path.Combine(root, "result.json")
        File.WriteAllText(report, New JObject From {{"Succeeded", failures.Count = 0}, {"Count", passed.Count}, {"Passed", New JArray(passed)},
            {"Cases", cases}, {"Failures", New JArray(failures.Select(Function(ex) ex.ToString()))}}.ToString())
        Console.WriteLine("SHARED_MODS_HASH_RESULT=" & report)
        If failures.Count > 0 Then Throw New AggregateException("Shared Mods hash cancellation regressions", failures)
        Return passed
    End Function

    Private Sub DirectCase(root As String, scope As String, passed As List(Of String), cases As JObject)
        Dim stage = IO.Path.Combine(root, scope), path = IO.Path.Combine(stage, "mods", "fixture.jar"), hashless = scope = "hashless-optifine-freeze"
        Dim bytes = If(hashless, OptiFinePayloadChecks.Jar(Payload.Length), Payload), item = Expected(bytes, Not hashless)
        Directory.CreateDirectory(IO.Path.GetDirectoryName(path)) : File.WriteAllBytes(path, bytes)
        Dim stamp As New DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc) : File.SetLastWriteTimeUtc(path, stamp)
        Dim itemBefore = JObject.FromObject(item).ToString(), remoteBytes As Long, freezeBytes As Long, targetBlocks As Integer, blocked As Boolean, boundaryBlocked As Integer
        Dim failure As Exception = Nothing, result As List(Of DownloadFile) = Nothing, originalToken As Boolean
        Using cancel As New CancellationTokenSource()
            Using probe As New Probe(Sub(algorithm, count)
                                         If count <= 0 OrElse Not SnapshotStack() Then Return
                                         If algorithm = "md5" Then remoteBytes += count Else If algorithm = "sha1" Then freezeBytes += count
                                         If algorithm <> If(scope = "remote-md5", "md5", "sha1") Then Return
                                         targetBlocks += 1
                                         If targetBlocks <> 1 Then Return
                                         blocked = IsProtected(path) : cancel.Cancel()
                                     End Sub, Sub()
                                                  If SnapshotStack() Then boundaryBlocked = BoundaryProtection(path)
                                              End Sub)
                Try
                    result = SharedModsPublication.CheckedFiles(stage, New List(Of DownloadFile) From {item}, cancel.Token)
                Catch ex As Exception
                    failure = ex
                End Try
                originalToken = TypeOf failure Is OperationCanceledException AndAlso DirectCast(failure, OperationCanceledException).CancellationToken = cancel.Token
            End Using
            Dim sourcePreserved = File.ReadAllBytes(path).SequenceEqual(bytes) AndAlso File.GetLastWriteTimeUtc(path) = stamp
            Dim releasedAfterCancel = Released(path), expectedPreserved = JObject.FromObject(item).ToString() = itemBefore
            Dim observation As New JObject From {{"TargetAlgorithm", If(scope = "remote-md5", "md5", "sha1")}, {"RemoteBytes", remoteBytes}, {"FreezeBytes", freezeBytes},
                {"Length", bytes.Length}, {"TargetBlocks", targetBlocks}, {"OriginalToken", originalToken}, {"ReturnedResult", result IsNot Nothing}, {"Exception", failure?.GetType().FullName},
                {"SourceWriteBlocked", blocked}, {"BoundaryWriteDeleteRenameBlocked", boundaryBlocked}, {"SourcePreserved", sourcePreserved}, {"ExpectedUnchanged", expectedPreserved}, {"LeaseReleased", releasedAfterCancel}}
            cases(scope) = observation
            Using fresh As New CancellationTokenSource()
                observation("RetrySuccess") = FreezeCorrect(SharedModsPublication.CheckedFiles(stage, New List(Of DownloadFile) From {item}, fresh.Token), item, bytes)
            End Using
            Console.WriteLine("SHARED_MODS_HASH_CANCEL scope=" & scope & " remote=" & remoteBytes & " freeze=" & freezeBytes & " length=" & bytes.Length & " blocks=" & targetBlocks & " originalToken=" & originalToken)
            Check(targetBlocks > 0 AndAlso blocked, "shared Mods cancellation reaches and protects the intended staged hash: " & scope, passed)
            Check(originalToken AndAlso result Is Nothing, "shared Mods cancellation preserves the caller token and exports no partial frozen list: " & scope, passed)
            Check(sourcePreserved AndAlso expectedPreserved, "cancelled shared Mods hashing preserves staged content, timestamp and upstream expectations: " & scope, passed)
            If scope <> "remote-md5" Then Check(boundaryBlocked = 3, "the original shared Mods read lease blocks write/delete/rename between validation and freezing: " & scope, passed)
            Check(releasedAfterCancel, "cancelled shared Mods hashing releases its original read lease: " & scope, passed)
            Check(CBool(observation("RetrySuccess")) AndAlso Released(path), "shared Mods hashing retries with a fresh token and a complete local SHA1 snapshot: " & scope, passed)
            Dim targetBytes = If(scope = "remote-md5", remoteBytes, freezeBytes)
            Check(targetBytes > 0 AndAlso targetBytes < bytes.Length AndAlso (scope <> "remote-md5" OrElse freezeBytes = 0), "shared Mods cancellation stops its hash early and prevents a subsequent freeze: " & scope, passed)
        End Using
    End Sub

    Private Sub Controls(root As String, passed As List(Of String))
        For Each hashed In {True, False}
            Dim stage = IO.Path.Combine(root, "success-" & hashed), path = IO.Path.Combine(stage, "mods", "fixture.jar"), item = Expected(Payload, hashed)
            Directory.CreateDirectory(IO.Path.GetDirectoryName(path)) : File.WriteAllBytes(path, Payload)
            Dim first As List(Of DownloadFile) = Nothing, second As List(Of DownloadFile) = Nothing, protectedBoundaries As Integer = 0
            Using probe As New Probe(Sub(algorithm, count)
                                     End Sub, Sub()
                                                  If SnapshotStack() Then protectedBoundaries += BoundaryProtection(path)
                                              End Sub)
                first = SharedModsPublication.CheckedFiles(stage, New List(Of DownloadFile) From {item}, CancellationToken.None)
                Using token As New CancellationTokenSource()
                    second = SharedModsPublication.CheckedFiles(stage, New List(Of DownloadFile) From {item}, token.Token)
                End Using
            End Using
            Check(FreezeCorrect(first, item, Payload) AndAlso FreezeCorrect(second, item, Payload), "uncancelled shared Mods freeze produces the correct local digest without copying remote MD5: hashed=" & hashed, passed)
            Check(JArray.FromObject(first).ToString() = JArray.FromObject(second).ToString(), "None and cancellable shared Mods snapshots have identical metadata: hashed=" & hashed, passed)
            Console.WriteLine("SHARED_MODS_CONTROL hashed=" & hashed & " boundaryBlocked=" & protectedBoundaries & " unchanged=" & File.ReadAllBytes(path).SequenceEqual(Payload) & " released=" & Released(path))
            Check(protectedBoundaries = 6 AndAlso File.ReadAllBytes(path).SequenceEqual(Payload) AndAlso Released(path), "None and cancellable snapshots protect the original lease across both steps and then release it: hashed=" & hashed, passed)
        Next
        Dim area = IO.Path.Combine(root, "controls"), empty = IO.Path.Combine(area, "mods", "fixture.jar")
        Directory.CreateDirectory(IO.Path.GetDirectoryName(empty)) : File.WriteAllBytes(empty, {})
        Dim itemEmpty = Expected({}, False)
        Using token As New CancellationTokenSource()
            Check(FreezeCorrect(SharedModsPublication.CheckedFiles(area, New List(Of DownloadFile) From {itemEmpty}, token.Token), itemEmpty, {}), "a declared empty shared Mod freezes the standard empty SHA1", passed)
        End Using
        Dim seen As Long, result As List(Of DownloadFile) = Nothing, failure As Exception = Nothing
        Using cancel As New CancellationTokenSource(), probe As New Probe(Sub(algorithm, count) seen += count)
            cancel.Cancel()
            Try
                result = SharedModsPublication.CheckedFiles(area, New List(Of DownloadFile) From {itemEmpty}, cancel.Token)
            Catch ex As Exception
                failure = ex
            End Try
            Check(TypeOf failure Is OperationCanceledException AndAlso DirectCast(failure, OperationCanceledException).CancellationToken = cancel.Token AndAlso result Is Nothing AndAlso seen = 0 AndAlso Released(empty), "pre-cancelled shared Mods validation reads zero bytes and preserves the caller token", passed)
        End Using
        File.WriteAllBytes(empty, Payload)
        Dim wrong = Expected(Payload, True), freezes As Integer
        wrong.Md5 = New String("0"c, 32) : failure = Nothing : result = Nothing
        Using probe As New Probe(Sub(algorithm, count)
                                 End Sub, Sub() freezes += 1)
            Try
                result = SharedModsPublication.CheckedFiles(area, New List(Of DownloadFile) From {wrong}, CancellationToken.None)
            Catch ex As Exception
                failure = ex
            End Try
        End Using
        Check(TypeOf failure Is InvalidDataException AndAlso result Is Nothing AndAlso freezes = 0 AndAlso File.ReadAllBytes(empty).SequenceEqual(Payload) AndAlso Released(empty), "a wrong remote Mod digest is rejected before any local freeze and releases its lease", passed)
    End Sub

    Private Async Function InstallationAsync(root As String, passed As List(Of String), cases As JObject) As Task
        Using session As New IsolationResourceChecks.Session(root, "shared-mods-hash")
            session.SetGlobal("0")
            Dim selected = IsolationModeChecks.Components(session, all:=True), payloadOpt = OptiFinePayloadChecks.Jar(Payload.Length)
            session.Fixture.Server.Files("/opt") = payloadOpt
            Dim originalOpt = IO.Path.Combine(session.Root, "mods", "optifine.jar"), userMod = IO.Path.Combine(session.Root, "mods", "user.jar")
            session.Put(originalOpt, payloadOpt) : session.Put(userMod, session.Bad)
            Dim stamp As New DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc) : File.SetLastWriteTimeUtc(originalOpt, stamp)
            Dim existing = IO.Path.Combine(session.Root, "versions", "kept", "kept.json"), settings = IO.Path.Combine(session.Root, "versions", "kept", "PCL", "Setup.ini")
            Dim jsonBytes = Text.Encoding.UTF8.GetBytes("{""id"":""kept"",""setting"":true}"), settingsBytes = Text.Encoding.UTF8.GetBytes("VersionArgumentIndieV2:False" & vbCrLf & "keep:用户设置")
            session.Put(existing, jsonBytes) : session.Put(settings, settingsBytes)
            Dim globalBytes = File.ReadAllBytes(session.GlobalPath), bytes As Long, blocks As Integer, blocked As Boolean, failure As Exception = Nothing, returned As Boolean
            Dim privatePath As String = Nothing, originalToken As Boolean
            Using cancel As New CancellationTokenSource()
                Using probe As New Probe(Sub(algorithm, count)
                                             If count <= 0 OrElse algorithm <> "sha1" OrElse Not SnapshotStack() Then Return
                                             bytes += count : blocks += 1
                                             If blocks <> 1 Then Return
                                             privatePath = Directory.EnumerateFiles(IO.Path.Combine(session.Root, ".vanilla-downloader", "staging"), "optifine.jar", SearchOption.AllDirectories).Single()
                                             blocked = IsProtected(privatePath) : cancel.Cancel()
                                         End Sub)
                    Try
                        Await session.StartAsync(components:=selected, isolation:=InstanceIsolationMode.Disabled, token:=cancel.Token)
                        returned = True
                    Catch ex As Exception
                        failure = ex
                    End Try
                    originalToken = TypeOf failure Is OperationCanceledException AndAlso DirectCast(failure, OperationCanceledException).CancellationToken = cancel.Token
                End Using
                Dim oldSettings = File.ReadAllBytes(session.GlobalPath).SequenceEqual(globalBytes) AndAlso File.ReadAllBytes(existing).SequenceEqual(jsonBytes) AndAlso File.ReadAllBytes(settings).SequenceEqual(settingsBytes)
                Dim sharedKept = File.ReadAllBytes(originalOpt).SequenceEqual(payloadOpt) AndAlso File.GetLastWriteTimeUtc(originalOpt) = stamp AndAlso File.ReadAllBytes(userMod).SequenceEqual(session.Bad)
                Dim noPublication = Not File.Exists(session.Json) AndAlso Not File.Exists(session.Ini) AndAlso Not File.Exists(session.Resource(False)) AndAlso
                    Not File.Exists(IO.Path.Combine(session.Root, "mods", "fabric-api.jar")) AndAlso Not File.Exists(IO.Path.Combine(session.Root, "mods", "optifabric.jar"))
                Dim clean = session.Released() AndAlso Not Directory.EnumerateFiles(IO.Path.Combine(session.Root, ".vanilla-downloader", "components"), "*", SearchOption.AllDirectories).Any()
                Dim leases = Released(originalOpt) AndAlso Released(userMod) AndAlso Released(existing) AndAlso Released(settings)
                Dim requests = session.Fixture.Server.Requests("/opt")
                Dim observation As New JObject From {{"Algorithm", "sha1"}, {"Bytes", bytes}, {"Length", payloadOpt.Length}, {"Blocks", blocks}, {"OriginalToken", originalToken},
                    {"Returned", returned}, {"Exception", failure?.GetType().FullName}, {"PrivateSource", privatePath}, {"PrivateSourceRemoved", privatePath IsNot Nothing AndAlso Not File.Exists(privatePath)},
                    {"WriteBlocked", blocked}, {"SettingsPreserved", oldSettings}, {"SharedModsPreserved", sharedKept}, {"NoPublication", noPublication}, {"PrivateTreeClean", clean}, {"LeasesReleased", leases}}
                cases("installation-hashless-optifine") = observation
                Using fresh As New CancellationTokenSource()
                    Dim retry = Await session.StartAsync(components:=selected, isolation:=InstanceIsolationMode.Disabled, token:=fresh.Token)
                    observation("RetrySuccess") = File.Exists(session.Json) AndAlso File.Exists(IO.Path.Combine(retry.InstancePath, "legacy.jar")) AndAlso
                        File.ReadAllBytes(originalOpt).SequenceEqual(payloadOpt) AndAlso File.Exists(IO.Path.Combine(session.Root, "mods", "fabric-api.jar")) AndAlso File.Exists(IO.Path.Combine(session.Root, "mods", "optifabric.jar")) AndAlso
                        PclFolders.ReadIniValue(session.Ini, "VersionArgumentIndieV2") = "False" AndAlso Not Directory.Exists(IO.Path.Combine(retry.InstancePath, "mods"))
                    observation("RetryNetworkBytes") = retry.NetworkBytes
                End Using
                observation("OptiFineRequestsBeforeRetry") = requests : observation("OptiFineRequestsAfterRetry") = session.Fixture.Server.Requests("/opt")
                observation("RetrySettingsPreservedAndClean") = File.ReadAllBytes(session.GlobalPath).SequenceEqual(globalBytes) AndAlso File.ReadAllBytes(existing).SequenceEqual(jsonBytes) AndAlso
                    File.ReadAllBytes(settings).SequenceEqual(settingsBytes) AndAlso session.Released() AndAlso File.GetLastWriteTimeUtc(originalOpt) = stamp
                Console.WriteLine("SHARED_MODS_HASH_CANCEL scope=installation bytes=" & bytes & " length=" & payloadOpt.Length & " blocks=" & blocks & " originalToken=" & originalToken)
                Check(blocks > 0 AndAlso blocked AndAlso privatePath IsNot Nothing, "real shared OptiFine install cancels inside its protected hashless snapshot", passed)
                Check(originalToken AndAlso Not returned, "shared Mod installation snapshot cancellation preserves the original token without success", passed)
                Check(oldSettings, "cancelled shared Mod installation preserves global and existing instance/isolation settings", passed)
                Check(sharedKept, "cancelled shared Mod installation preserves existing shared OptiFine, timestamps and user Mods", passed)
                Check(noPublication, "cancelled shared Mod snapshot publishes no instance, settings, resource aliases or new selected Mods", passed)
                Check(clean AndAlso leases, "cancelled shared Mod installation drains workers, cleans private trees and releases root/file leases", passed)
                Check(CBool(observation("RetrySuccess")), "shared OptiFine snapshot cancellation retries the same name with correct shared isolation and all selected Mods", passed)
                Check(CLng(observation("RetryNetworkBytes")) = payloadOpt.Length AndAlso requests = 1 AndAlso CInt(observation("OptiFineRequestsAfterRetry")) = 2 AndAlso CBool(observation("RetrySettingsPreservedAndClean")),
                    "shared Mod retry reuses declared caches, refetches hashless OptiFine and preserves old configuration", passed)
                Check(bytes > 0 AndAlso bytes < payloadOpt.Length, "real shared Mod install stops the local frozen hash before reading the complete file", passed)
            End Using
        End Using
    End Function
End Module

Friend Module SharedModsHashProbeState
    Friend OnCreate As Action
End Module

Public Class SharedModsProbeSha1
    Inherits FileMatchProbeSha1
    Public Sub New()
        SharedModsHashProbeState.OnCreate?.Invoke()
    End Sub
End Class
