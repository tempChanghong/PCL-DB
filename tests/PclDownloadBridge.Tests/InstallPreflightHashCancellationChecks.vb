Imports System.Diagnostics
Imports System.IO
Imports System.IO.Compression
Imports System.Reflection
Imports System.Runtime.ExceptionServices
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module InstallPreflightHashCancellationChecks
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
    Private Sub Seed(path As String, bytes As Byte())
        Directory.CreateDirectory(IO.Path.GetDirectoryName(path)) : File.WriteAllBytes(path, bytes)
    End Sub
    Private Function Item(relative As String, Optional bytes As Byte() = Nothing) As DownloadFile
        bytes = If(bytes, Payload)
        Return New DownloadFile With {.RelativePath = relative, .Sha1 = Hash(bytes), .Size = bytes.Length}
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
    Private Function InPreflight(mods As Boolean) As Boolean
        Dim methods = New StackTrace().GetFrames().Select(Function(frame) frame.GetMethod()).ToList()
        If mods Then Return methods.Any(Function(method) method?.DeclaringType Is GetType(SharedModsPublication) AndAlso {"Preflight", "PreflightForInstallation"}.Contains(method.Name))
        Return Not methods.Any(Function(method) method?.DeclaringType Is GetType(LegacyResourcePublication) AndAlso method.Name = "Publish") AndAlso
            methods.Any(Function(method) method?.DeclaringType Is GetType(LegacyResourcePublication) AndAlso {"Validate", "ValidateCore", "ValidateForInstallation"}.Contains(method.Name))
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
    Private Function Released(paths As IEnumerable(Of String)) As Boolean
        For Each path In paths
            Using lease As New FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            End Using
        Next
        Return True
    End Function
    Private Function Entry(mods As Boolean, Optional oldOnly As Boolean = False) As MethodInfo
        Dim owner = If(mods, GetType(SharedModsPublication), GetType(LegacyResourcePublication)), name = If(mods, "Preflight", "Validate")
        If Not oldOnly Then
            Dim tokenEntry = owner.GetMethod(If(mods, "PreflightForInstallation", "ValidateForInstallation"), BindingFlags.NonPublic Or BindingFlags.Static)
            If tokenEntry IsNot Nothing Then Return tokenEntry
        End If
        Return owner.GetMethod(name, BindingFlags.NonPublic Or BindingFlags.Static)
    End Function
    Private Function Mode(mods As Boolean) As String
        Return If(Entry(mods).Name.EndsWith("ForInstallation", StringComparison.Ordinal), "new-token-entry", "old-None-fallback")
    End Function
    Private Sub Invoke(root As String, files As List(Of DownloadFile), mods As Boolean, token As CancellationToken, Optional repair As Boolean = False, Optional oldOnly As Boolean = False)
        ' The old locked DLL has no new entry points. Reflection selects and records
        ' its original None API instead of allowing MissingMethod/JIT errors to pass
        ' as regression evidence; actual InstallAsync cases always use the public API.
        Dim method = Entry(mods, oldOnly), arguments As Object()
        If mods Then
            arguments = If(method.Name = "Preflight", New Object() {root, files, True}, New Object() {root, files, True, token})
        Else
            arguments = If(method.Name = "Validate", New Object() {root, files, repair, "旧资源"}, New Object() {root, files, repair, token, "旧资源"})
        End If
        Try
            method.Invoke(Nothing, arguments)
        Catch ex As TargetInvocationException When ex.InnerException IsNot Nothing
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw()
            Throw
        End Try
    End Sub
    Friend Async Function RunAsync(root As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        If onlyCase IsNot Nothing AndAlso onlyCase <> "install-preflight-hash-all" Then Throw New ArgumentException(onlyCase)
        root = IO.Path.Combine(root, "install-preflight-hash-cancellation") : Directory.CreateDirectory(root)
        Dim passed As New List(Of String), failures As New List(Of Exception), cases As New JObject, before = Mappings(), callback = FileMatchHashProbe.OnBlock
        For Each mods In {False, True}
            Try
                DirectCase(root, mods, passed, cases)
            Catch ex As Exception
                failures.Add(New InvalidOperationException("Direct installation preflight: mods=" & mods, ex))
            End Try
            Try
                Await InstallationAsync(root, mods, passed, cases)
            Catch ex As Exception
                failures.Add(New InvalidOperationException("Installation preflight: mods=" & mods, ex))
            End Try
        Next
        Try
            Controls(root, passed)
        Catch ex As Exception
            failures.Add(New InvalidOperationException("Installation preflight controls", ex))
        End Try
        For Each pair In before
            Using sha = DirectCast(CryptoConfig.CreateFromName(pair.Key), HashAlgorithm)
                Check(sha.GetType() Is pair.Value, "installation preflight restores its original algorithm mapping: " & pair.Key, passed)
            End Using
        Next
        Check(Object.ReferenceEquals(FileMatchHashProbe.OnBlock, callback), "installation preflight restores the process-local callback", passed)
        Dim report = IO.Path.Combine(root, "result.json")
        File.WriteAllText(report, New JObject From {{"Succeeded", failures.Count = 0}, {"Count", passed.Count}, {"Passed", New JArray(passed)},
            {"Cases", cases}, {"Failures", New JArray(failures.Select(Function(ex) ex.ToString()))}}.ToString())
        Console.WriteLine("INSTALL_PREFLIGHT_HASH_RESULT=" & report)
        If failures.Count > 0 Then Throw New AggregateException("Installation preflight hash regressions", failures)
        Return passed
    End Function
    Private Sub DirectCase(root As String, mods As Boolean, passed As List(Of String), cases As JObject)
        Dim label = If(mods, "mods", "resources"), area = IO.Path.Combine(root, "direct-" & label), expected = Item(If(mods, "mods/fixture.jar", "resources/fixture.bin"))
        Dim target = SafeFiles.UnderRoot(area, expected.RelativePath) : Seed(target, Payload)
        Dim stamp As New DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc) : File.SetLastWriteTimeUtc(target, stamp)
        Dim before = JObject.FromObject(expected).ToString(), bytes As Long, blocks As Integer, blocked As Boolean, returned As Boolean, failure As Exception = Nothing, originalToken As Boolean
        Using cancel As New CancellationTokenSource()
            Using probe As New Probe(Sub(algorithm, count)
                                         If count <= 0 OrElse algorithm <> "sha1" OrElse Not InPreflight(mods) Then Return
                                         bytes += count : blocks += 1
                                         If blocks = 1 Then blocked = IsProtected(target) : cancel.Cancel()
                                     End Sub)
                Try
                    Invoke(area, New List(Of DownloadFile) From {expected}, mods, cancel.Token) : returned = True
                Catch ex As Exception
                    failure = ex
                End Try
                originalToken = TypeOf failure Is OperationCanceledException AndAlso DirectCast(failure, OperationCanceledException).CancellationToken = cancel.Token
            End Using
        End Using
        Dim preserved = File.ReadAllBytes(target).SequenceEqual(Payload) AndAlso File.GetLastWriteTimeUtc(target) = stamp AndAlso JObject.FromObject(expected).ToString() = before
        Dim releasedAfterCancel = Released({target})
        Dim observation As New JObject From {{"EntryMode", Mode(mods)}, {"Entry", Entry(mods).Name}, {"Bytes", bytes}, {"Length", Payload.Length}, {"Blocks", blocks},
            {"Returned", returned}, {"OriginalToken", originalToken}, {"Exception", failure?.GetType().FullName}, {"WriteBlocked", blocked}, {"OriginalFileAndExpectationsPreserved", preserved}, {"LeaseReleased", releasedAfterCancel}}
        cases("direct-" & label) = observation
        Using fresh As New CancellationTokenSource()
            Invoke(area, New List(Of DownloadFile) From {expected}, mods, fresh.Token)
        End Using
        observation("RetrySuccess") = File.ReadAllBytes(target).SequenceEqual(Payload) AndAlso File.GetLastWriteTimeUtc(target) = stamp AndAlso Released({target})
        Console.WriteLine("INSTALL_PREFLIGHT_HASH_CANCEL scope=direct-" & label & " mode=" & Mode(mods) & " bytes=" & bytes & " length=" & Payload.Length & " blocks=" & blocks & " returned=" & returned & " originalToken=" & originalToken)
        Check(blocks > 0 AndAlso blocked, "direct installation preflight cancellation reaches its protected existing file hash: " & label, passed)
        Check(preserved, "cancelled direct installation preflight preserves original bytes, timestamp and expectations: " & label, passed)
        Check(releasedAfterCancel, "cancelled direct installation preflight releases its read lease: " & label, passed)
        Check(CBool(observation("RetrySuccess")), "direct installation preflight accepts a fresh-token retry without modifying existing content: " & label, passed)
        Check(originalToken AndAlso Not returned, "direct token-aware installation preflight returns original-token cancellation rather than success: " & label, passed)
        Check(bytes > 0 AndAlso bytes < Payload.Length, "direct installation preflight stops hashing before the complete file is read: " & label, passed)
    End Sub
    Private Function ModJar() As Byte()
        Using buffer As New MemoryStream()
            Using zip As New ZipArchive(buffer, ZipArchiveMode.Create, True)
                Using writer As New StreamWriter(zip.CreateEntry("fabric.mod.json", CompressionLevel.NoCompression).Open(), New UTF8Encoding(False))
                    writer.Write("{""schemaVersion"":1,""id"":""fabric-api"",""version"":""fixture""}")
                End Using
                Using output = zip.CreateEntry("padding.bin", CompressionLevel.NoCompression).Open()
                    output.Write(Payload, 0, Payload.Length)
                End Using
            End Using
            Return buffer.ToArray()
        End Using
    End Function
    Private Async Function InstallationAsync(root As String, mods As Boolean, passed As List(Of String), cases As JObject) As Task
        Dim label = If(mods, "mods", "resources")
        Using session As New IsolationResourceChecks.Session(root, "preflight-hash-" & label)
            session.SetGlobal("0")
            Dim selected As ComponentSelection = Nothing, content As Byte() = Payload, target As String
            If mods Then
                selected = IsolationModeChecks.Components(session) : content = ModJar()
                session.Fixture.Server.Files("/api") = content
                selected.Item(ComponentKind.FabricApi).Sha1 = Hash(content) : selected.Item(ComponentKind.FabricApi).Size = content.Length
                target = IO.Path.Combine(session.Root, "mods", "fabric-api.jar")
            Else
                Dim name = "sounds/preflight-hash.ogg", digest = Hash(content)
                IsolationResourceChecks.Configure(session.Fixture, New JObject From {{name, New JObject From {{"hash", digest}, {"size", content.Length}}}})
                session.Fixture.Server.Files("/" & digest.Substring(0, 2) & "/" & digest) = content
                target = session.Resource(False, name)
            End If
            session.Put(target, content)
            Dim stamp As New DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc) : File.SetLastWriteTimeUtc(target, stamp)
            Dim existing = IO.Path.Combine(session.Root, "versions", "kept", "kept.json"), settings = IO.Path.Combine(session.Root, "versions", "kept", "PCL", "Setup.ini")
            Dim jsonBytes = Encoding.UTF8.GetBytes("{""id"":""kept"",""setting"":true}"), settingsBytes = Encoding.UTF8.GetBytes("VersionArgumentIndieV2:False" & vbCrLf & "keep:用户设置")
            session.Put(existing, jsonBytes) : session.Put(settings, settingsBytes)
            Dim globalBytes = File.ReadAllBytes(session.GlobalPath), staging = IO.Path.Combine(session.Root, ".vanilla-downloader", "staging")
            Dim bytes As Long, blocks As Integer, blocked As Boolean, stageAtFirstBlock As Boolean, returned As Boolean, failure As Exception = Nothing, originalToken As Boolean
            Using cancel As New CancellationTokenSource()
                Using probe As New Probe(Sub(algorithm, count)
                                             If count <= 0 OrElse algorithm <> "sha1" OrElse Not InPreflight(mods) Then Return
                                             bytes += count : blocks += 1
                                             If blocks <> 1 Then Return
                                             blocked = IsProtected(target) : stageAtFirstBlock = Directory.Exists(staging) : cancel.Cancel()
                                         End Sub)
                    Try
                        Await session.StartAsync(components:=selected, isolation:=InstanceIsolationMode.Disabled, token:=cancel.Token) : returned = True
                    Catch ex As Exception
                        failure = ex
                    End Try
                    originalToken = TypeOf failure Is OperationCanceledException AndAlso DirectCast(failure, OperationCanceledException).CancellationToken = cancel.Token
                End Using
                Dim noPayload = session.Fixture.Server.Requests.Keys.All(Function(path) path = "/version" OrElse path = "/index")
                Dim noStage = Not Directory.Exists(staging), noInstance = Not File.Exists(session.Json) AndAlso Not File.Exists(session.Ini)
                Dim kept = File.ReadAllBytes(target).SequenceEqual(content) AndAlso File.GetLastWriteTimeUtc(target) = stamp AndAlso File.ReadAllBytes(session.GlobalPath).SequenceEqual(globalBytes) AndAlso
                    File.ReadAllBytes(existing).SequenceEqual(jsonBytes) AndAlso File.ReadAllBytes(settings).SequenceEqual(settingsBytes)
                Dim releasedAfterCancel = session.Released() AndAlso Released({target, existing, settings})
                Dim observation As New JObject From {{"EntryMode", Mode(mods)}, {"Bytes", bytes}, {"Length", content.Length}, {"Blocks", blocks}, {"OriginalToken", originalToken}, {"Returned", returned},
                    {"Exception", failure?.GetType().FullName}, {"WriteBlocked", blocked}, {"StageAtFirstBlock", stageAtFirstBlock}, {"NoStagingLayoutCreated", noStage}, {"NoPayloadRequests", noPayload},
                    {"RequestsBeforeRetry", New JArray(session.Fixture.Server.Requests.Keys.OrderBy(Function(path) path))}, {"NoInstanceOrNewSettings", noInstance}, {"OriginalFilesAndSettingsPreserved", kept}, {"LeasesAndRootLockReleased", releasedAfterCancel}}
                cases("installation-" & label) = observation
                Using fresh As New CancellationTokenSource()
                    Dim retry = Await session.StartAsync(components:=selected, isolation:=InstanceIsolationMode.Disabled, token:=fresh.Token)
                    observation("RetrySuccess") = File.Exists(session.Json) AndAlso File.Exists(IO.Path.Combine(retry.InstancePath, "legacy.jar")) AndAlso File.ReadAllBytes(target).SequenceEqual(content) AndAlso PclFolders.ReadIniValue(session.Ini, "VersionArgumentIndieV2") = "False"
                End Using
                observation("RetryPreservesOldStateAndCleansWork") = File.GetLastWriteTimeUtc(target) = stamp AndAlso File.ReadAllBytes(session.GlobalPath).SequenceEqual(globalBytes) AndAlso File.ReadAllBytes(existing).SequenceEqual(jsonBytes) AndAlso
                    File.ReadAllBytes(settings).SequenceEqual(settingsBytes) AndAlso session.Released() AndAlso session.Fixture.Server.Requests.ContainsKey("/client")
                Console.WriteLine("INSTALL_PREFLIGHT_HASH_CANCEL scope=installation-" & label & " bytes=" & bytes & " length=" & content.Length & " blocks=" & blocks & " noStage=" & noStage & " noPayload=" & noPayload & " originalToken=" & originalToken)
                Check(blocks > 0 AndAlso blocked AndAlso Not stageAtFirstBlock, "real installation cancellation reaches initial preflight before staging exists: " & label, passed)
                Check(originalToken AndAlso Not returned, "initial installation preflight preserves original-token cancellation without success: " & label, passed)
                Check(noInstance AndAlso kept, "initial preflight cancellation preserves original files/configuration and publishes no instance/settings: " & label, passed)
                Check(releasedAfterCancel, "initial preflight cancellation releases existing-file and game-root locks: " & label, passed)
                Check(CBool(observation("RetrySuccess")), "initial preflight cancellation retries the same instance with complete content and shared isolation: " & label, passed)
                Check(CBool(observation("RetryPreservesOldStateAndCleansWork")), "initial preflight retry preserves old timestamps/settings, downloads required payload and cleans private work: " & label, passed)
                Check(noStage AndAlso noPayload, "initial preflight cancellation creates no staging/settings layout and starts no payload download: " & label, passed)
                Check(bytes > 0 AndAlso bytes < content.Length, "initial installation preflight stops hashing before the complete existing file is read: " & label, passed)
            End Using
        End Using
    End Function
    Private Sub Controls(root As String, passed As List(Of String))
        For Each mods In {False, True}
            Dim area = IO.Path.Combine(root, "legacy-None-" & mods), expected = Item(If(mods, "mods/fixture.jar", "resources/fixture.bin")), target = SafeFiles.UnderRoot(area, expected.RelativePath)
            Seed(target, Payload)
            Dim bytes As Long = 0, returned As Boolean = False
            Using cancel As New CancellationTokenSource(), probe As New Probe(Sub(algorithm, count)
                                                                                  If count <= 0 OrElse algorithm <> "sha1" OrElse Not InPreflight(mods) Then Return
                                                                                  bytes += count : cancel.Cancel()
                                                                              End Sub)
                Invoke(area, New List(Of DownloadFile) From {expected}, mods, cancel.Token, oldOnly:=True) : returned = True
            End Using
            Check(returned AndAlso bytes = Payload.Length, "the original None preflight entry retains complete provider behavior when an unrelated token is cancelled: mods=" & mods, passed)
            Check(File.ReadAllBytes(target).SequenceEqual(Payload) AndAlso Released({target}), "the original None preflight preserves existing content and releases its lease: mods=" & mods, passed)
            expected.Sha1 = New String("0"c, 40)
            Dim failure As Exception = Nothing
            Try
                Invoke(area, New List(Of DownloadFile) From {expected}, mods, CancellationToken.None)
            Catch ex As Exception
                failure = ex
            End Try
            Check(TypeOf failure Is IOException AndAlso File.ReadAllBytes(target).SequenceEqual(Payload) AndAlso Released({target}), "uncancelled installation preflight retains the existing-file digest conflict: mods=" & mods, passed)
        Next
        Dim layout = IO.Path.Combine(root, "eager-layout"), first = Item("resources/first.bin"), invalid = Item("../escape.bin"), layoutBytes As Long
        Seed(SafeFiles.UnderRoot(layout, first.RelativePath), Payload)
        Dim structural As Exception = Nothing
        Using cancel As New CancellationTokenSource(), probe As New Probe(Sub(algorithm, count) layoutBytes += count)
            cancel.Cancel()
            Try
                Invoke(layout, New List(Of DownloadFile) From {first, invalid}, False, cancel.Token)
            Catch ex As Exception
                structural = ex
            End Try
        End Using
        Check(TypeOf structural Is InvalidDataException AndAlso layoutBytes = 0, "eager invalid destination validation precedes cancellation and any existing resource hash", passed)
        Dim folders = IO.Path.Combine(root, "folder-priority"), marker = IO.Path.Combine(folders, "mods"), folderBytes As Long
        Seed(marker, Payload) : structural = Nothing
        Using cancel As New CancellationTokenSource(), probe As New Probe(Sub(algorithm, count) folderBytes += count)
            cancel.Cancel()
            Try
                Invoke(folders, New List(Of DownloadFile) From {Item("mods/fixture.jar")}, True, cancel.Token)
            Catch ex As Exception
                structural = ex
            End Try
        End Using
        Check(TypeOf structural Is IOException AndAlso Not TypeOf structural Is OperationCanceledException AndAlso folderBytes = 0 AndAlso File.ReadAllBytes(marker).SequenceEqual(Payload), "shared Mods folder conflicts retain priority before cancellation or file hashing", passed)
        For Each mods In {False, True}
            Dim area = IO.Path.Combine(root, "skip-existing-hash-" & mods), expected = Item(If(mods, "mods/fixture.jar", "resources/fixture.bin")), target = SafeFiles.UnderRoot(area, expected.RelativePath)
            Dim original = Encoding.UTF8.GetBytes("preserve unverified existing file") : Seed(target, original)
            If mods Then expected.Sha1 = Nothing
            Dim bytes As Long = 0, returned As Boolean = False
            Using cancel As New CancellationTokenSource(), probe As New Probe(Sub(algorithm, count) bytes += count)
                cancel.Cancel()
                Invoke(area, New List(Of DownloadFile) From {expected}, mods, cancel.Token, repair:=Not mods) : returned = True
            End Using
            Check(returned AndAlso bytes = 0 AndAlso File.ReadAllBytes(target).SequenceEqual(original) AndAlso Released({target}), "initial preflight retains its hashless-Mod or resource-repair gate without trusting/replacing old bytes: mods=" & mods, passed)
        Next
    End Sub
End Module
