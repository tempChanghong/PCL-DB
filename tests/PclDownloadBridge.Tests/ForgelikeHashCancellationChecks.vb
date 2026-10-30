Imports System.Diagnostics
Imports System.IO
Imports System.IO.Compression
Imports System.Reflection
Imports System.Runtime.CompilerServices
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

' Modern install_profile/version.json processors use the real embedded Java fixture.
' 25,000 UTF-16 characters fit Windows' command line and yield 75,000 UTF-8 bytes:
' enough to distinguish one cancellable block from a completed file hash.
Friend Module ForgelikeHashCancellationChecks
    Private Const OutputRelative As String = "libraries/fixture/cancel-output/1/cancel-output-1.jar"
    Private Const DependencyRelative As String = "libraries/fixture/cancel-dependency/1/cancel-dependency-1.jar"
    Private Const ProcessorRelative As String = "libraries/fixture/processor/1/processor-1.jar"
    Private ReadOnly TextPayload As String = New String("测"c, 25000)
    Private ReadOnly Payload As Byte() = Encoding.UTF8.GetBytes(TextPayload)
    Private ReadOnly ForgelikeState As Type = GetType(ComponentInstaller).GetMethod("InstallForgelikeAsync", BindingFlags.Instance Or BindingFlags.NonPublic).GetCustomAttribute(Of AsyncStateMachineAttribute)().StateMachineType

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

    Private Function DirectMatcherCaller() As MethodBase
        Dim frames = New StackTrace().GetFrames()
        If Not frames.Any(Function(frame) frame.GetMethod()?.DeclaringType Is ForgelikeState) Then Return Nothing
        For Each frame In frames
            Dim method = frame.GetMethod(), declaring = method?.DeclaringType
            If declaring Is Nothing OrElse declaring.Assembly IsNot GetType(ComponentInstaller).Assembly OrElse declaring Is GetType(SafeFiles) Then Continue For
            Return method
        Next
        Return Nothing
    End Function

    Private Function CallerMatches(caller As MethodBase, scope As String) As Boolean
        If caller Is Nothing Then Return False
        Dim parameters = caller.GetParameters()
        If scope = "dependency-filter" Then Return parameters.Length = 1 AndAlso parameters(0).ParameterType Is GetType(DownloadFile)
        If scope = "restored-output-all" Then Return parameters.Length = 1 AndAlso parameters(0).ParameterType Is GetType(KeyValuePair(Of String, DownloadFile))
        Return caller.DeclaringType Is ForgelikeState
    End Function

    Private Sub Seed(path As String, bytes As Byte())
        Directory.CreateDirectory(IO.Path.GetDirectoryName(path)) : File.WriteAllBytes(path, bytes)
    End Sub

    Private Sub Entry(zip As ZipArchive, name As String, bytes As Byte())
        Using output = zip.CreateEntry(name).Open()
            output.Write(bytes, 0, bytes.Length)
        End Using
    End Sub

    Private Function Artifact(relative As String, url As String, bytes As Byte()) As JObject
        Return New JObject From {{"name", If(relative = ProcessorRelative, "fixture:processor:1", If(relative = DependencyRelative, "fixture:cancel-dependency:1", "fixture:cancel-output:1"))},
            {"downloads", New JObject From {{"artifact", New JObject From {{"path", relative.Substring(10)}, {"url", url}, {"size", bytes.Length}, {"sha1", Hash(bytes)}}}}}}
    End Function

    Private Function Selection(fixture As InstallationChecks.InstallFixture, scope As String) As ComponentSelection
        Dim jar = ComponentChecks.ProcessorJar(), libraries As New JArray()
        If scope = "dependency-filter" Then libraries.Add(Artifact(DependencyRelative, fixture.Server.Url & "/unused-dependency", Payload))
        libraries.Add(Artifact(ProcessorRelative, fixture.Server.Url & "/unused-processor", jar))
        Dim profile As New JObject From {{"minecraft", fixture.Entry.Id}, {"libraries", libraries},
            {"data", New JObject From {{"PATCHED", New JObject From {{"client", "[fixture:cancel-output:1]"}}}}},
            {"processors", New JArray(New JObject From {{"sides", New JArray("client")}, {"jar", "fixture:processor:1"}, {"classpath", New JArray()},
                {"args", New JArray("{PATCHED}", TextPayload, "{ROOT}/data/executed.txt", "executed")}, {"outputs", New JObject From {{"{PATCHED}", Hash(Payload)}}}})}}
        Dim version As New JObject From {{"mainClass", "fixture.Loader"}, {"inheritsFrom", fixture.Entry.Id}, {"libraries", New JArray(Artifact(OutputRelative, "", Payload))}}
        Using buffer As New MemoryStream()
            Using zip As New ZipArchive(buffer, ZipArchiveMode.Create, True)
                Entry(zip, "install_profile.json", Encoding.UTF8.GetBytes(profile.ToString()))
                Entry(zip, "version.json", Encoding.UTF8.GetBytes(version.ToString()))
                Entry(zip, "maven/" & ProcessorRelative.Substring(10), jar)
                If scope = "dependency-filter" Then Entry(zip, "maven/" & DependencyRelative.Substring(10), Payload)
                If scope = "embedded-output" Then Entry(zip, "maven/" & OutputRelative.Substring(10), Payload)
            End Using
            fixture.Server.Files("/hash-installer") = buffer.ToArray()
        End Using
        Dim result As New ComponentSelection()
        result.Entries.Add(ComponentKind.NeoForge, New ComponentEntry With {.Kind = ComponentKind.NeoForge, .Minecraft = fixture.Entry.Id,
            .Version = "fixture", .Url = fixture.Server.Url & "/hash-installer", .Sha1 = Hash(fixture.Server.Files("/hash-installer"))})
        Return result
    End Function

    Private Function Executions(root As String) As Integer
        Dim log = Path.Combine(root, ".vanilla-downloader", "installer.log")
        Return If(File.Exists(log), File.ReadAllLines(log).Count(Function(line) line.Contains("fixture processor complete")), 0)
    End Function

    Private Function OutputCache(root As String) As Dictionary(Of String, String)
        Dim area = Path.Combine(root, ".vanilla-downloader", "processor-cache"), result As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        If Directory.Exists(area) Then
            For Each path In Directory.EnumerateFiles(area, "*", SearchOption.AllDirectories)
                result.Add(path, Hash(File.ReadAllBytes(path)))
            Next
        End If
        Return result
    End Function

    Private Function SameCache(before As Dictionary(Of String, String), after As Dictionary(Of String, String)) As Boolean
        Return before.Count = after.Count AndAlso before.All(Function(pair) after.ContainsKey(pair.Key) AndAlso after(pair.Key) = pair.Value)
    End Function

    Private Function Clean(fixture As InstallationChecks.InstallFixture) As Boolean
        Return Not Directory.EnumerateFiles(fixture.Root, "*.component-*", SearchOption.AllDirectories).Any() AndAlso
            Not Directory.EnumerateFiles(fixture.Root, "*.cache-*", SearchOption.AllDirectories).Any() AndAlso
            {"components", "staging"}.All(Function(branch)
                                               Dim area = Path.Combine(fixture.Root, ".vanilla-downloader", branch)
                                               Return Not Directory.Exists(area) OrElse Not Directory.EnumerateFiles(area, "*", SearchOption.AllDirectories).Any()
                                           End Function)
    End Function

    Private Function Locked(path As String) As Boolean
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
        For Each path In paths
            Using lease As New FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            End Using
        Next
        Return True
    End Function

    Friend Async Function RunAsync(root As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        If onlyCase IsNot Nothing AndAlso onlyCase <> "forgelike-hash-all" Then Throw New ArgumentException(onlyCase)
        root = Path.Combine(root, "forgelike-hash-cancellation") : Directory.CreateDirectory(root)
        Dim passed As New List(Of String), failures As New List(Of Exception), cases As New JObject, before = Mappings(), callback = FileMatchHashProbe.OnBlock
        For Each scope In {"dependency-filter", "embedded-output", "restored-output-all", "executed-output"}
            Try
                Await RunCaseAsync(root, scope, passed, cases)
            Catch ex As Exception
                failures.Add(New InvalidOperationException("Forgelike hash cancellation: " & scope, ex))
            End Try
        Next
        For Each pair In before
            Using sha = DirectCast(CryptoConfig.CreateFromName(pair.Key), HashAlgorithm)
                Check(sha.GetType() Is pair.Value, "Forgelike hashing restores its original algorithm mapping: " & pair.Key, passed)
            End Using
        Next
        Check(Object.ReferenceEquals(FileMatchHashProbe.OnBlock, callback), "Forgelike hashing restores the process-local callback", passed)
        Dim report = Path.Combine(root, "result.json")
        File.WriteAllText(report, New JObject From {{"Succeeded", failures.Count = 0}, {"Count", passed.Count}, {"Passed", New JArray(passed)},
            {"Cases", cases}, {"Failures", New JArray(failures.Select(Function(ex) ex.ToString()))}}.ToString())
        Console.WriteLine("FORGELIKE_HASH_RESULT=" & report)
        If failures.Count > 0 Then Throw New AggregateException("Forgelike hash cancellation regressions", failures)
        Return passed
    End Function

    Private Async Function RunCaseAsync(root As String, scope As String, passed As List(Of String), cases As JObject) As Task
        Using fixture As New InstallationChecks.InstallFixture(root, "forgelike-" & scope)
            Dim selected = Selection(fixture, scope), instanceName = "retry-hash", warmed = scope = "restored-output-all"
            If warmed Then Await fixture.InstallAsync("cache-source", selected)
            Dim sourceJson = Path.Combine(fixture.Root, "versions", "cache-source", "cache-source.json")
            Dim sourceBytes = If(warmed, File.ReadAllBytes(sourceJson), Nothing)
            Dim sharedPath = SafeFiles.UnderRoot(fixture.Root, OutputRelative), original = Encoding.UTF8.GetBytes("preserve existing runtime")
            Seed(sharedPath, original)
            Dim stamp As New DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc) : File.SetLastWriteTimeUtc(sharedPath, stamp)
            Dim existing = SafeFiles.UnderRoot(fixture.Root, "versions/kept/kept.json"), settings = SafeFiles.UnderRoot(fixture.Root, "versions/kept/PCL/Setup.ini")
            Dim existingBytes = Encoding.UTF8.GetBytes("{""id"":""kept"",""userSetting"":true}"), settingsBytes = Encoding.UTF8.GetBytes("VersionArgumentIndieV2:False" & vbCrLf & "keep:用户设置")
            Seed(existing, existingBytes) : Seed(settings, settingsBytes)
            Dim oldCache = OutputCache(fixture.Root), beforeExecutions = Executions(fixture.Root)
            Dim processorNotice As Boolean, skippedNotice As Boolean, reached As Boolean, bytes As Long, blocks As Integer
            Dim actualCaller As String = Nothing, privateTarget As String = Nothing, markerAtHash As Boolean, failure As Exception = Nothing, returned As Boolean, originalToken As Boolean
            Using cancel As New CancellationTokenSource()
                Dim progress As New InlineProgress(Sub(value)
                                                       If value.Phase <> "processors" OrElse value.Stage <> "安装 NeoForge" Then Return
                                                       processorNotice = True
                                                       If value.Detail IsNot Nothing AndAlso value.Detail.Contains("已校验输出") Then skippedNotice = True
                                                   End Sub)
                Using probe As New Probe(Sub(algorithm, count)
                                             If count <= 0 OrElse algorithm <> "sha1" Then Return
                                             Dim caller = DirectMatcherCaller()
                                             If Not CallerMatches(caller, scope) Then Return
                                             Dim area = Path.Combine(fixture.Root, ".vanilla-downloader", "components")
                                             If Not Directory.Exists(area) Then Return
                                             Dim work = Directory.EnumerateDirectories(area).Single(), marker = Path.Combine(work, "data", "executed.txt")
                                             Dim markerExists = File.Exists(marker)
                                             If scope = "dependency-filter" Then
                                                 If processorNotice Then Return
                                             ElseIf scope = "executed-output" Then
                                                 If Not processorNotice OrElse Not markerExists Then Return
                                             ElseIf Not processorNotice OrElse skippedNotice OrElse markerExists Then
                                                 Return
                                             End If
                                             Dim target = SafeFiles.UnderRoot(work, If(scope = "dependency-filter", DependencyRelative, OutputRelative))
                                             If Not Locked(target) Then Return
                                             reached = True : actualCaller = caller.DeclaringType.FullName & "." & caller.Name
                                             privateTarget = target : markerAtHash = markerExists : blocks += 1 : bytes += count
                                             If blocks = 1 Then cancel.Cancel()
                                         End Sub)
                    Try
                        Await fixture.InstallAsync(instanceName, selected, progress:=progress, token:=cancel.Token)
                        returned = True
                    Catch ex As Exception
                        failure = ex
                    End Try
                    originalToken = TypeOf failure Is OperationCanceledException AndAlso DirectCast(failure, OperationCanceledException).CancellationToken = cancel.Token
                End Using
                Dim sharedPreserved = File.ReadAllBytes(sharedPath).SequenceEqual(original) AndAlso File.GetLastWriteTimeUtc(sharedPath) = stamp
                Dim settingsPreserved = File.ReadAllBytes(existing).SequenceEqual(existingBytes) AndAlso File.ReadAllBytes(settings).SequenceEqual(settingsBytes) AndAlso
                    (Not warmed OrElse File.ReadAllBytes(sourceJson).SequenceEqual(sourceBytes))
                Dim noInstance = Not Directory.Exists(Path.Combine(fixture.Root, "versions", instanceName)), cleanAfterCancel = Clean(fixture)
                Dim cachePreserved = SameCache(oldCache, OutputCache(fixture.Root)), afterExecutions = Executions(fixture.Root)
                Dim releasedAfterCancel = Released({sharedPath, existing, settings, Path.Combine(fixture.Root, ".vanilla-downloader", "install.lock")}.Concat(oldCache.Keys))
                Dim requestsBeforeRetry = fixture.Server.Requests("/hash-installer")
                Dim observation As New JObject From {{"Algorithm", "sha1"}, {"ExpectedStage", scope}, {"ActualDirectCaller", actualCaller},
                    {"Bytes", bytes}, {"Length", Payload.Length}, {"Blocks", blocks}, {"ReachedProtectedTarget", reached}, {"ProcessorNotice", processorNotice},
                    {"SkippedNotice", skippedNotice}, {"ExecutionMarkerAtHash", markerAtHash}, {"Returned", returned}, {"OriginalToken", originalToken}, {"Exception", failure?.GetType().FullName},
                    {"PrivateTarget", privateTarget}, {"PrivateTargetRemoved", privateTarget Is Nothing OrElse Not File.Exists(privateTarget)}, {"SharedOriginalRetained", sharedPreserved},
                    {"ExistingSettingsPreserved", settingsPreserved}, {"NoInstance", noInstance}, {"PrivateTreeClean", cleanAfterCancel}, {"LeasesAndRootLockReleased", releasedAfterCancel},
                    {"ExistingCachePreserved", cachePreserved}, {"OutputCacheBefore", oldCache.Count}, {"OutputCacheAfterCancel", OutputCache(fixture.Root).Count},
                    {"ProcessorExecutionsBefore", beforeExecutions}, {"ProcessorExecutionsAfterCancel", afterExecutions}}
                cases(scope) = observation
                Using fresh As New CancellationTokenSource()
                    Dim result = Await fixture.InstallAsync(instanceName, selected, token:=fresh.Token)
                    Dim metadata = JObject.Parse(File.ReadAllText(Path.Combine(result.InstancePath, instanceName & ".json")))
                    observation("RetrySuccess") = Not fresh.IsCancellationRequested AndAlso File.Exists(Path.Combine(result.InstancePath, instanceName & ".jar")) AndAlso
                        File.ReadAllBytes(sharedPath).SequenceEqual(Payload) AndAlso CStr(metadata.SelectToken("libraries[0].downloads.artifact.sha1")) = Hash(Payload)
                End Using
                observation("RetryProcessorExecutions") = Executions(fixture.Root)
                observation("RetryInstallerRequestsBefore") = requestsBeforeRetry
                observation("RetryInstallerRequestsAfter") = fixture.Server.Requests("/hash-installer")
                observation("RetryExistingSettingsPreserved") = File.ReadAllBytes(existing).SequenceEqual(existingBytes) AndAlso File.ReadAllBytes(settings).SequenceEqual(settingsBytes) AndAlso
                    (Not warmed OrElse File.ReadAllBytes(sourceJson).SequenceEqual(sourceBytes))
                observation("RetryClean") = Clean(fixture) AndAlso OutputCache(fixture.Root).Count = 1 AndAlso Not File.Exists(SafeFiles.UnderRoot(fixture.Root, ProcessorRelative)) AndAlso Not File.Exists(SafeFiles.UnderRoot(fixture.Root, DependencyRelative))
                Console.WriteLine("FORGELIKE_HASH_CANCEL scope=" & scope & " bytes=" & bytes & " length=" & Payload.Length & " blocks=" & blocks & " originalToken=" & originalToken & " executions=" & beforeExecutions & "/" & afterExecutions & "/" & CInt(observation("RetryProcessorExecutions")))
                Check(reached AndAlso actualCaller IsNot Nothing AndAlso markerAtHash = (scope = "executed-output"), "Forgelike cancellation reaches its intended protected hash call: " & scope, passed)
                Check(originalToken AndAlso Not returned, "Forgelike hash cancellation preserves the caller token without success: " & scope, passed)
                Check(noInstance AndAlso cleanAfterCancel, "cancelled Forgelike hash publishes no instance and cleans private work/staging/copies: " & scope, passed)
                Check(sharedPreserved AndAlso settingsPreserved, "cancelled Forgelike hash preserves the existing shared runtime, timestamp and instance/isolation settings: " & scope, passed)
                Check(cachePreserved AndAlso oldCache.Count = If(warmed, 1, 0), "cancelled Forgelike hash retains complete cached outputs and never caches unfinished output: " & scope, passed)
                Check(releasedAfterCancel, "cancelled Forgelike hash releases file leases and the game root lock: " & scope, passed)
                Check(afterExecutions - beforeExecutions = If(scope = "executed-output", 1, 0), "Forgelike cancellation has the expected processor execution count before retry: " & scope, passed)
                Check(CBool(observation("RetrySuccess")), "Forgelike fresh-token same-name retry publishes complete checked runtime and instance: " & scope, passed)
                Check(CInt(observation("RetryProcessorExecutions")) - afterExecutions = If(scope = "embedded-output" OrElse warmed, 0, 1) AndAlso CInt(observation("RetryInstallerRequestsAfter")) = requestsBeforeRetry,
                    "Forgelike retry reuses checked downloads and skips only verified processor execution: " & scope, passed)
                Check(CBool(observation("RetryExistingSettingsPreserved")) AndAlso CBool(observation("RetryClean")), "Forgelike retry preserves prior settings, cleans private files and keeps install-only dependencies private: " & scope, passed)
                Check(bytes > 0 AndAlso bytes < Payload.Length, "Forgelike cancellation stops hashing before reading the complete file: " & scope, passed)
            End Using
        End Using
    End Function
End Module
