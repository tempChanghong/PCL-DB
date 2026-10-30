Imports System.Diagnostics
Imports System.IO
Imports System.Reflection
Imports System.Runtime.CompilerServices
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module InstallerFinalHashCancellationChecks
    Private ReadOnly Payload As Byte() = Enumerable.Range(0, 512 * 1024 + 19).Select(Function(index) CByte(index Mod 251)).ToArray()
    Private ReadOnly InstallerState As Type = GetType(MinecraftInstaller).GetMethod("InstallCoreAsync", BindingFlags.NonPublic Or BindingFlags.Instance).GetCustomAttribute(Of AsyncStateMachineAttribute)().StateMachineType
    Private Sub Check(value As Boolean, label As String, passed As List(Of String))
        If Not value Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub
    Private Function Hash(bytes As Byte()) As String
        Using sha As New SHA1CryptoServiceProvider()
            Return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function
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
    Private Function DirectInstallerHash() As Boolean
        For Each frame In New StackTrace().GetFrames()
            Dim declaring = frame.GetMethod()?.DeclaringType
            If declaring Is Nothing OrElse declaring.Assembly IsNot GetType(MinecraftInstaller).Assembly OrElse declaring Is GetType(SafeFiles) Then Continue For
            Return declaring Is InstallerState
        Next
        Return False
    End Function
    Private Function IsProtected(path As String) As Boolean
        Try
            Using lease As New FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            End Using
        Catch ex As IOException
            Return True
        End Try
        Return False
    End Function
    Private Function Readable(path As String) As Boolean
        Using reader = File.OpenRead(path)
            Return reader.Length > 0
        End Using
    End Function
    Private Function Released(paths As IEnumerable(Of String)) As Boolean
        For Each path In paths
            Using lease As New FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            End Using
        Next
        Return True
    End Function
    Private Function Clean(root As String) As Boolean
        Dim staging = IO.Path.Combine(root, ".vanilla-downloader", "staging")
        Return Not Directory.Exists(staging) OrElse Not Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Any()
    End Function
    Private Class SupplementalPolicy
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

    Friend Async Function RunAsync(root As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        If onlyCase IsNot Nothing AndAlso onlyCase <> "installer-final-hash-all" Then Throw New ArgumentException(onlyCase)
        root = IO.Path.Combine(root, "installer-final-hash-cancellation") : Directory.CreateDirectory(root)
        Dim passed As New List(Of String), failures As New List(Of Exception), cases As New JObject, before = Mappings(), callback = FileMatchHashProbe.OnBlock
        Try
            Await LegacyIndexAsync(root, passed, cases)
        Catch ex As Exception
            failures.Add(New InvalidOperationException("Legacy supplemental shared index cancellation", ex))
        End Try
        Try
            Await RepairClientAsync(root, passed, cases)
        Catch ex As Exception
            failures.Add(New InvalidOperationException("Final repair client cancellation", ex))
        End Try
        For Each pair In before
            Using sha = DirectCast(CryptoConfig.CreateFromName(pair.Key), HashAlgorithm)
                Check(sha.GetType() Is pair.Value, "installer final hashing restores its original algorithm mapping: " & pair.Key, passed)
            End Using
        Next
        Check(Object.ReferenceEquals(FileMatchHashProbe.OnBlock, callback), "installer final hashing restores the process-local callback", passed)
        Dim report = IO.Path.Combine(root, "result.json")
        File.WriteAllText(report, New JObject From {{"Succeeded", failures.Count = 0}, {"Count", passed.Count}, {"Passed", New JArray(passed)},
            {"Cases", cases}, {"Failures", New JArray(failures.Select(Function(ex) ex.ToString()))}}.ToString())
        Console.WriteLine("INSTALLER_FINAL_HASH_RESULT=" & report)
        If failures.Count > 0 Then Throw New AggregateException("Installer final hash cancellation regressions", failures)
        Return passed
    End Function

    Private Async Function LegacyIndexAsync(root As String, passed As List(Of String), cases As JObject) As Task
        Using fixture As New InstallationChecks.InstallFixture(root, "final-hash-legacy-index", "2.0_blue")
            Dim indexBytes = Encoding.UTF8.GetBytes(New JObject From {{"objects", New JObject()}, {"fixture-padding", New String("x"c, Payload.Length)}}.ToString(Newtonsoft.Json.Formatting.None))
            Dim source = SupplementalInstallFixture.Profile(fixture, "2.0_blue")
            source("assetIndex")("size") = indexBytes.Length : source("assetIndex")("sha1") = Hash(indexBytes)
            fixture.Server.Files("/index") = indexBytes : fixture.Server.Files("/version") = Encoding.UTF8.GetBytes(source.ToString())
            fixture.Entry.Sha1 = Hash(fixture.Server.Files("/version"))
            ' An ordinary entry intentionally seeds the original pre-private-index
            ' metadata; the subsequent supplemental entry must preserve that layout.
            Dim seeded = Await fixture.InstallAsync("legacy"), json = IO.Path.Combine(seeded.InstancePath, "legacy.json"), ini = IO.Path.Combine(seeded.InstancePath, "PCL", "Setup.ini")
            Directory.CreateDirectory(IO.Path.GetDirectoryName(ini)) : File.WriteAllText(ini, "VersionArgumentIndieV2:True" & vbCrLf & "keep:用户设置", New UTF8Encoding(False))
            Dim index = IO.Path.Combine(fixture.Root, "assets", "indexes", "fixture.json"), rootLock = IO.Path.Combine(fixture.Root, ".vanilla-downloader", "install.lock")
            Dim stamp As New DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc) : File.SetLastWriteTimeUtc(index, stamp)
            Dim jsonBytes = File.ReadAllBytes(json), preferences = File.ReadAllBytes(ini), sourceBytes = DirectCast(fixture.Server.Files("/version").Clone(), Byte())
            Dim requests = fixture.Server.Requests("/index"), clients = fixture.Server.Requests("/client"), bytes As Long, blocks As Integer, protectedIndex As Boolean, protectedRoot As Boolean
            Dim returned As Boolean, failure As Exception = Nothing, originalToken As Boolean
            Dim installer As New MinecraftInstaller(New SupplementalPolicy(fixture.Server.Url)), selected = SupplementalInstallFixture.Entry("2.0_blue")
            Using cancel As New CancellationTokenSource()
                Using probe As New Probe(Sub(algorithm, count)
                                             If count <= 0 OrElse algorithm <> "sha1" OrElse Not DirectInstallerHash() OrElse Not IsProtected(index) Then Return
                                             bytes += count : blocks += 1
                                             If blocks <> 1 Then Return
                                             protectedIndex = True : protectedRoot = IsProtected(rootLock) : cancel.Cancel()
                                         End Sub)
                    Try
                        Await installer.InstallAsync(selected, fixture.Root, "legacy", {}, Nothing, cancel.Token, repairExisting:=True) : returned = True
                    Catch ex As Exception
                        failure = ex
                    End Try
                    originalToken = TypeOf failure Is OperationCanceledException AndAlso DirectCast(failure, OperationCanceledException).CancellationToken = cancel.Token
                End Using
            End Using
            Dim kept = File.ReadAllBytes(index).SequenceEqual(indexBytes) AndAlso File.GetLastWriteTimeUtc(index) = stamp AndAlso File.ReadAllBytes(json).SequenceEqual(jsonBytes) AndAlso
                File.ReadAllBytes(ini).SequenceEqual(preferences) AndAlso fixture.Server.Files("/version").SequenceEqual(sourceBytes)
            Dim noTransfer = fixture.Server.Requests("/index") = requests AndAlso fixture.Server.Requests("/client") = clients
            Dim cleanAndReleased = Clean(fixture.Root) AndAlso Released({index, json, ini, rootLock})
            Dim observation As New JObject From {{"Bytes", bytes}, {"Length", indexBytes.Length}, {"Blocks", blocks}, {"OriginalToken", originalToken}, {"Returned", returned},
                {"Exception", failure?.GetType().FullName}, {"SharedIndexWriteBlocked", protectedIndex}, {"RootLockHeld", protectedRoot}, {"IndexMetadataAndSettingsPreserved", kept},
                {"NoIndexOrClientTransfer", noTransfer}, {"PrivateFilesCleanAndLeasesReleased", cleanAndReleased}}
            cases("legacy-shared-index") = observation
            Using fresh As New CancellationTokenSource()
                Dim retry = Await installer.InstallAsync(selected, fixture.Root, "legacy", {}, Nothing, fresh.Token, repairExisting:=True)
                observation("RetrySuccess") = retry.NetworkBytes = 0 AndAlso File.ReadAllBytes(index).SequenceEqual(indexBytes) AndAlso File.GetLastWriteTimeUtc(index) = stamp AndAlso File.ReadAllBytes(json).SequenceEqual(jsonBytes) AndAlso File.ReadAllBytes(ini).SequenceEqual(preferences)
            End Using
            Dim noneBytes As Long
            Using probe As New Probe(Sub(algorithm, count)
                                         If count > 0 AndAlso algorithm = "sha1" AndAlso DirectInstallerHash() AndAlso IsProtected(index) Then noneBytes += count
                                     End Sub)
                Dim normal = Await installer.InstallAsync(selected, fixture.Root, "legacy", {}, Nothing, CancellationToken.None, repairExisting:=True)
                observation("NoneSuccess") = normal.NetworkBytes = 0 AndAlso noneBytes = indexBytes.Length AndAlso File.ReadAllBytes(json).SequenceEqual(jsonBytes) AndAlso File.ReadAllBytes(index).SequenceEqual(indexBytes) AndAlso File.GetLastWriteTimeUtc(index) = stamp
            End Using
            observation("NoneHashBytes") = noneBytes
            Console.WriteLine("INSTALLER_FINAL_HASH_CANCEL scope=legacy-index bytes=" & bytes & " length=" & indexBytes.Length & " blocks=" & blocks & " originalToken=" & originalToken & " none=" & noneBytes)
            Check(blocks > 0 AndAlso protectedIndex AndAlso protectedRoot, "legacy supplemental repair cancels inside the direct installer shared-index check under its read/root leases", passed)
            Check(originalToken AndAlso Not returned, "legacy index cancellation preserves the original token without success", passed)
            Check(kept, "legacy index cancellation preserves the original index layout, bytes, timestamp, metadata and PCL settings", passed)
            Check(noTransfer, "legacy index guard cancellation precedes index-body and client transfers", passed)
            Check(cleanAndReleased, "legacy index cancellation releases file/root locks and leaves no private staged payload", passed)
            Check(CBool(observation("RetrySuccess")), "legacy supplemental index check retries with a fresh token and no content transfer or metadata migration", passed)
            Check(CBool(observation("NoneSuccess")) AndAlso Clean(fixture.Root) AndAlso Released({index, json, ini, rootLock}), "legacy index None-token repair retains a complete provider hash and unchanged shared metadata", passed)
            Check(bytes > 0 AndAlso bytes < indexBytes.Length, "legacy index cancellation stops before reading the complete shared index", passed)
        End Using
    End Function

    Private Async Function RepairClientAsync(root As String, passed As List(Of String), cases As JObject) As Task
        Using session As New IsolationResourceChecks.Session(root, "final-hash-client")
            Dim metadata = JObject.Parse(Encoding.UTF8.GetString(session.Fixture.Server.Files("/version")))
            metadata("downloads")("client")("sha1") = Hash(Payload) : metadata("downloads")("client")("size") = Payload.Length
            session.Fixture.Server.Files("/client") = Payload : session.Fixture.Server.Files("/version") = Encoding.UTF8.GetBytes(metadata.ToString())
            session.Fixture.Entry.Sha1 = Hash(session.Fixture.Server.Files("/version"))
            Await session.SeedAsync() : session.SetIni("VersionArgumentIndieV2:True")
            Dim corrupt = DirectCast(Payload.Clone(), Byte()) : corrupt(0) = corrupt(0) Xor CByte(1)
            File.WriteAllBytes(session.Jar, corrupt) : session.Put(session.Resource(True), session.Bad)
            Dim stamp As New DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc) : File.SetLastWriteTimeUtc(session.Jar, stamp)
            Dim jsonBytes = File.ReadAllBytes(session.Json), preferences = File.ReadAllBytes(session.Ini), globalBytes = File.ReadAllBytes(session.GlobalPath), resourceGood = File.ReadAllBytes(session.Resource(False))
            Dim rootLock = IO.Path.Combine(session.Root, ".vanilla-downloader", "install.lock"), bytes As Long, blocks As Integer, commit As Boolean
            Dim jsonProtected As Boolean, iniProtected As Boolean, rootProtected As Boolean, readableGuards As Boolean, completedResource As Boolean
            Dim returned As Boolean, failure As Exception = Nothing, originalToken As Boolean
            Using cancel As New CancellationTokenSource()
                Dim progress As New InlineProgress(Sub(value)
                                                       If value.Phase = "commit" Then commit = True
                                                   End Sub)
                Using probe As New Probe(Sub(algorithm, count)
                                             If count <= 0 OrElse algorithm <> "sha1" OrElse Not commit OrElse Not DirectInstallerHash() OrElse Not IsProtected(session.Jar) Then Return
                                             bytes += count : blocks += 1
                                             If blocks <> 1 Then Return
                                             jsonProtected = IsProtected(session.Json) : iniProtected = IsProtected(session.Ini) : rootProtected = IsProtected(rootLock)
                                             readableGuards = Readable(session.Json) AndAlso Readable(session.Ini)
                                             completedResource = File.ReadAllBytes(session.Resource(True)).SequenceEqual(resourceGood) : cancel.Cancel()
                                         End Sub)
                    Try
                        Await session.StartAsync(True, progress, cancel.Token) : returned = True
                    Catch ex As Exception
                        failure = ex
                    End Try
                    originalToken = TypeOf failure Is OperationCanceledException AndAlso DirectCast(failure, OperationCanceledException).CancellationToken = cancel.Token
                End Using
            End Using
            Dim clientPreserved = File.ReadAllBytes(session.Jar).SequenceEqual(corrupt) AndAlso File.GetLastWriteTimeUtc(session.Jar) = stamp
            Dim settingsPreserved = File.ReadAllBytes(session.Json).SequenceEqual(jsonBytes) AndAlso File.ReadAllBytes(session.Ini).SequenceEqual(preferences) AndAlso File.ReadAllBytes(session.GlobalPath).SequenceEqual(globalBytes)
            Dim resourceRetained = File.ReadAllBytes(session.Resource(True)).SequenceEqual(resourceGood) AndAlso File.ReadAllBytes(session.Resource(False)).SequenceEqual(resourceGood) AndAlso File.ReadAllBytes(session.ObjectFile()).SequenceEqual(resourceGood)
            Dim releasedAfterCancel = session.Released() AndAlso Released({session.Json, session.Ini, session.GlobalPath, session.Jar, rootLock})
            Dim observation As New JObject From {{"Bytes", bytes}, {"Length", Payload.Length}, {"Blocks", blocks}, {"OriginalToken", originalToken}, {"Returned", returned}, {"Exception", failure?.GetType().FullName},
                {"CommitReached", commit}, {"JsonWriteBlocked", jsonProtected}, {"IniWriteBlocked", iniProtected}, {"RootLockHeld", rootProtected}, {"GuardReadersAllowed", readableGuards},
                {"ResourceCompletedBeforeHash", completedResource}, {"DamagedClientPreserved", clientPreserved}, {"OriginalSettingsPreserved", settingsPreserved}, {"CompletedResourcesRetained", resourceRetained}, {"PrivateFilesCleanAndLeasesReleased", releasedAfterCancel}}
            cases("repair-client-target") = observation
            Using fresh As New CancellationTokenSource()
                Dim retry = Await session.StartAsync(repair:=True, token:=fresh.Token)
                observation("RetrySuccess") = retry.NetworkBytes = 0 AndAlso session.Fixture.Server.Requests("/client") = 2 AndAlso File.ReadAllBytes(session.Jar).SequenceEqual(Payload) AndAlso
                    File.ReadAllBytes(session.Json).SequenceEqual(jsonBytes) AndAlso File.ReadAllBytes(session.Ini).SequenceEqual(preferences) AndAlso File.ReadAllBytes(session.Resource(True)).SequenceEqual(resourceGood)
            End Using
            observation("RetryLeasesReleased") = session.Released() AndAlso Released({session.Json, session.Ini, session.GlobalPath, session.Jar, rootLock})
            Dim noneBytes As Long, noneCommit As Boolean, correctedStamp = File.GetLastWriteTimeUtc(session.Jar)
            Using probe As New Probe(Sub(algorithm, count)
                                         If count > 0 AndAlso algorithm = "sha1" AndAlso noneCommit AndAlso DirectInstallerHash() AndAlso IsProtected(session.Jar) Then noneBytes += count
                                     End Sub)
                Dim normal = Await session.StartAsync(repair:=True, token:=CancellationToken.None, progress:=New InlineProgress(Sub(value)
                                                                                                                                If value.Phase = "commit" Then noneCommit = True
                                                                                                                            End Sub))
                observation("NoneSuccess") = normal.NetworkBytes = 0 AndAlso noneBytes = Payload.Length AndAlso File.GetLastWriteTimeUtc(session.Jar) = correctedStamp AndAlso File.ReadAllBytes(session.Json).SequenceEqual(jsonBytes) AndAlso File.ReadAllBytes(session.Ini).SequenceEqual(preferences)
            End Using
            observation("NoneHashBytes") = noneBytes
            Console.WriteLine("INSTALLER_FINAL_HASH_CANCEL scope=repair-client bytes=" & bytes & " length=" & Payload.Length & " blocks=" & blocks & " originalToken=" & originalToken & " none=" & noneBytes)
            Check(commit AndAlso blocks > 0 AndAlso jsonProtected AndAlso iniProtected AndAlso rootProtected AndAlso readableGuards, "final repair target hashing holds JSON/isolation/root guards while allowing readers", passed)
            Check(originalToken AndAlso Not returned, "final repair client cancellation preserves the original token without success", passed)
            Check(clientPreserved, "cancelled final repair hash leaves the same-sized damaged client and timestamp untouched", passed)
            Check(settingsPreserved, "cancelled final repair hash preserves exact instance metadata and isolation/global settings", passed)
            Check(completedResource AndAlso resourceRetained, "client hash cancellation retains resource fixes completed before final client publication", passed)
            Check(releasedAfterCancel, "cancelled final repair hash releases JSON/isolation/root locks and cleans staged replacement files", passed)
            Check(CBool(observation("RetrySuccess")), "final repair hash cancellation retries with the checked cached client and no further transfer", passed)
            Check(CBool(observation("RetryLeasesReleased")), "successful repair retry releases every metadata/settings/root lease", passed)
            Check(CBool(observation("NoneSuccess")) AndAlso session.Released(), "None-token final repair retains complete provider hashing and leaves an already-correct client timestamp intact", passed)
            Check(bytes > 0 AndAlso bytes < Payload.Length, "final repair cancellation stops target hashing before the complete damaged client is read", passed)
        End Using
    End Function
End Module
