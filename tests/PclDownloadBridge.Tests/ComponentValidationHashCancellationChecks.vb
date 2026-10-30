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

' Exercise ApplyAsync through a real legacy Forge installer and MinecraftInstaller's
' staging/parallel branch boundary, rather than invoking the matcher in isolation.
Friend Module ComponentValidationHashCancellationChecks
    Private Const Relative As String = "libraries/fixture/runtime/1/runtime-1.jar"
    Private ReadOnly Payload As Byte() = Enumerable.Range(0, 512 * 1024 + 19).Select(Function(index) CByte(index Mod 251)).ToArray()
    Private ReadOnly ApplyState As Type = GetType(ComponentInstaller).GetMethod("ApplyAsync").GetCustomAttribute(Of AsyncStateMachineAttribute)().StateMachineType

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

    Private Function DirectMatcherCaller() As Type
        ' Ignore framework/provider/test frames and SafeFiles' own helpers. The next
        ' Core frame must be ApplyAsync itself, not a downloader or Forge helper.
        For Each frame In New StackTrace().GetFrames()
            Dim declaring = frame.GetMethod()?.DeclaringType
            If declaring Is Nothing OrElse declaring.Assembly IsNot GetType(ComponentInstaller).Assembly OrElse declaring Is GetType(SafeFiles) Then Continue For
            Return declaring
        Next
        Return Nothing
    End Function

    Private Sub Seed(path As String, bytes As Byte())
        Directory.CreateDirectory(IO.Path.GetDirectoryName(path)) : File.WriteAllBytes(path, bytes)
    End Sub

    Private Sub Entry(zip As ZipArchive, name As String, bytes As Byte())
        Using output = zip.CreateEntry(name).Open()
            output.Write(bytes, 0, bytes.Length)
        End Using
    End Sub

    Private Function Selection(fixture As InstallationChecks.InstallFixture) As ComponentSelection
        ' An explicitly empty URL keeps this bundled runtime out of Forge's earlier
        ' dependency downloader. Both measured checks are the final ApplyAsync plan.
        Dim artifact As New JObject From {{"path", Relative.Substring(10)}, {"url", ""}, {"size", Payload.Length}, {"sha1", Hash(Payload)}}
        Dim version As New JObject From {{"mainClass", "fixture.LegacyLoader"}, {"inheritsFrom", fixture.Entry.Id},
            {"libraries", New JArray(New JObject From {{"name", "fixture:runtime:1"}, {"downloads", New JObject From {{"artifact", artifact}}}})}}
        Dim profile As New JObject From {{"minecraft", fixture.Entry.Id}, {"versionInfo", version},
            {"install", New JObject From {{"filePath", "universal.jar"}, {"path", "fixture:runtime:1"}}}}
        Using buffer As New MemoryStream()
            Using zip As New ZipArchive(buffer, ZipArchiveMode.Create, True)
                Entry(zip, "install_profile.json", Encoding.UTF8.GetBytes(profile.ToString()))
                Entry(zip, "universal.jar", Payload)
            End Using
            fixture.Server.Files("/runtime-installer") = buffer.ToArray()
        End Using
        Dim result As New ComponentSelection()
        result.Entries.Add(ComponentKind.Forge, New ComponentEntry With {.Kind = ComponentKind.Forge, .Minecraft = fixture.Entry.Id,
            .Version = "14.23.5.2859", .Url = fixture.Server.Url & "/runtime-installer", .Sha1 = Hash(fixture.Server.Files("/runtime-installer"))})
        Return result
    End Function

    Private Function Clean(fixture As InstallationChecks.InstallFixture) As Boolean
        Return Not Directory.EnumerateFiles(fixture.Root, "*.component-*", SearchOption.AllDirectories).Any() AndAlso
            {"components", "staging"}.All(Function(branch)
                                               Dim folder = Path.Combine(fixture.Root, ".vanilla-downloader", branch)
                                               Return Not Directory.Exists(folder) OrElse Not Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any()
                                           End Function)
    End Function

    Private Function Released(paths As IEnumerable(Of String)) As Boolean
        For Each path In paths
            Using lease As New FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            End Using
        Next
        Return True
    End Function

    Friend Async Function RunAsync(root As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        If onlyCase IsNot Nothing AndAlso onlyCase <> "component-validation-hash-all" Then Throw New ArgumentException(onlyCase)
        root = Path.Combine(root, "component-validation-hash-cancellation") : Directory.CreateDirectory(root)
        Dim passed As New List(Of String), failures As New List(Of Exception), cases As New JObject, before = Mappings(), callback = FileMatchHashProbe.OnBlock
        For Each scope In {"missing-plan", "final-validation", "final-precancel"}
            Try
                Await RunCaseAsync(root, scope, passed, cases)
            Catch ex As Exception
                failures.Add(New InvalidOperationException("Component validation hash cancellation: " & scope, ex))
            End Try
        Next
        For Each pair In before
            Using sha = DirectCast(CryptoConfig.CreateFromName(pair.Key), HashAlgorithm)
                Check(sha.GetType() Is pair.Value, "component validation restores its original algorithm mapping: " & pair.Key, passed)
            End Using
        Next
        Check(Object.ReferenceEquals(FileMatchHashProbe.OnBlock, callback), "component validation restores the process-local callback", passed)
        Dim report = Path.Combine(root, "result.json")
        File.WriteAllText(report, New JObject From {{"Succeeded", failures.Count = 0}, {"Count", passed.Count}, {"Passed", New JArray(passed)},
            {"Cases", cases}, {"Failures", New JArray(failures.Select(Function(ex) ex.ToString()))}}.ToString())
        Console.WriteLine("COMPONENT_VALIDATION_HASH_RESULT=" & report)
        If failures.Count > 0 Then Throw New AggregateException("Component validation hash cancellation regressions", failures)
        Return passed
    End Function

    Private Async Function RunCaseAsync(root As String, scope As String, passed As List(Of String), cases As JObject) As Task
        Dim final = scope <> "missing-plan", precancel = scope = "final-precancel", instanceName = "cancelled-runtime"
        Using fixture As New InstallationChecks.InstallFixture(root, "validation-" & scope, "1.12.2")
            Dim components = Selection(fixture), sharedPath = SafeFiles.UnderRoot(fixture.Root, Relative)
            Dim original = DirectCast(Payload.Clone(), Byte()) : original(0) = original(0) Xor CByte(1)
            Seed(sharedPath, original)
            Dim stamp As New DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc) : File.SetLastWriteTimeUtc(sharedPath, stamp)
            Dim settings = SafeFiles.UnderRoot(fixture.Root, "versions/kept/PCL/Setup.ini"), existing = SafeFiles.UnderRoot(fixture.Root, "versions/kept/kept.json")
            Dim settingsBytes = Encoding.UTF8.GetBytes("VersionArgumentIndieV2:False" & vbCrLf & "keep:用户设置"), existingBytes = Encoding.UTF8.GetBytes("{""id"":""kept"",""userSetting"":true}")
            Seed(settings, settingsBytes) : Seed(existing, existingBytes)
            Dim finalPhase As Boolean, finalNotice As Boolean, returned As Boolean, blocked As Boolean, failure As Exception = Nothing
            Dim bytes As Long, blocks As Integer, actualCaller As String = Nothing, privateRuntime As String = Nothing, originalToken As Boolean
            Using cancel As New CancellationTokenSource()
                Dim progress As New InlineProgress(Sub(value)
                                                       If value.Stage <> "校验安装结果" Then Return
                                                       finalNotice = True : finalPhase = True
                                                       If precancel Then cancel.Cancel()
                                                   End Sub)
                Using probe As New Probe(Sub(algorithm, count)
                                             If count <= 0 OrElse algorithm <> "sha1" OrElse finalPhase <> final Then Return
                                             Dim caller = DirectMatcherCaller()
                                             If caller IsNot ApplyState Then Return
                                             actualCaller = caller.FullName : bytes += count : blocks += 1
                                             If blocks <> 1 Then Return
                                             Dim work = Path.Combine(fixture.Root, ".vanilla-downloader", "components")
                                             privateRuntime = Directory.EnumerateFiles(work, "runtime-1.jar", SearchOption.AllDirectories).Single()
                                             Try
                                                 Using lease As New FileStream(privateRuntime, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                                                 End Using
                                             Catch ex As IOException
                                                 blocked = True
                                             End Try
                                             If Not precancel Then cancel.Cancel()
                                         End Sub)
                    Try
                        Await fixture.InstallAsync(instanceName, components, progress:=progress, token:=cancel.Token)
                        returned = True
                    Catch ex As Exception
                        failure = ex
                    End Try
                    ' MinecraftInstaller rethrows the public caller's token after its
                    ' linked component branch has ended, so test the full API contract.
                    originalToken = TypeOf failure Is OperationCanceledException AndAlso DirectCast(failure, OperationCanceledException).CancellationToken = cancel.Token
                End Using
                Dim sharedPreserved = File.ReadAllBytes(sharedPath).SequenceEqual(original) AndAlso File.GetLastWriteTimeUtc(sharedPath) = stamp
                Dim settingsPreserved = File.ReadAllBytes(settings).SequenceEqual(settingsBytes) AndAlso File.ReadAllBytes(existing).SequenceEqual(existingBytes)
                Dim noInstance = Not Directory.Exists(Path.Combine(fixture.Root, "versions", instanceName)), cleanAfterCancel = Clean(fixture)
                Dim releasedAfterCancel = Released({sharedPath, settings, existing}), requestsBeforeRetry = fixture.Server.Requests("/runtime-installer")
                Dim observation As New JObject From {{"Algorithm", "sha1"}, {"ExpectedPhase", scope}, {"ActualDirectCaller", actualCaller},
                    {"FinalNotice", finalNotice}, {"Bytes", bytes}, {"Length", Payload.Length}, {"Blocks", blocks}, {"CancelRequested", cancel.IsCancellationRequested},
                    {"Returned", returned}, {"OriginalToken", originalToken}, {"Exception", failure?.GetType().FullName}, {"ExceptionDetail", failure?.Message},
                    {"PrivateRuntime", privateRuntime}, {"PrivateRuntimeRemoved", privateRuntime Is Nothing OrElse Not File.Exists(privateRuntime)}, {"WriteBlockedDuringHash", blocked},
                    {"SharedOriginalRetained", sharedPreserved}, {"SharedActualSHA1", Hash(File.ReadAllBytes(sharedPath))}, {"SharedOriginalSHA1", Hash(original)},
                    {"ExistingSettingsPreserved", settingsPreserved}, {"NoInstance", noInstance}, {"PrivateTreeClean", cleanAfterCancel}, {"LeasesReleased", releasedAfterCancel}}
                cases(scope) = observation
                Using fresh As New CancellationTokenSource()
                    Dim result = Await fixture.InstallAsync(instanceName, components, token:=fresh.Token)
                    Dim metadata = JObject.Parse(File.ReadAllText(Path.Combine(result.InstancePath, instanceName & ".json")))
                    observation("RetrySuccess") = Not fresh.IsCancellationRequested AndAlso File.Exists(Path.Combine(result.InstancePath, instanceName & ".jar")) AndAlso
                        File.ReadAllBytes(sharedPath).SequenceEqual(Payload) AndAlso CStr(metadata.SelectToken("libraries[0].downloads.artifact.sha1")) = Hash(Payload)
                End Using
                observation("RetryInstallerRequestsBefore") = requestsBeforeRetry
                observation("RetryInstallerRequestsAfter") = fixture.Server.Requests("/runtime-installer")
                observation("RetrySettingsPreserved") = File.ReadAllBytes(settings).SequenceEqual(settingsBytes) AndAlso File.ReadAllBytes(existing).SequenceEqual(existingBytes)
                observation("RetryPrivateTreeClean") = Clean(fixture)
                Console.WriteLine("COMPONENT_VALIDATION_HASH_CANCEL scope=" & scope & " bytes=" & bytes & " length=" & Payload.Length & " blocks=" & blocks & " originalToken=" & originalToken & " retry=" & CBool(observation("RetrySuccess")))
                If precancel Then
                    Check(finalNotice AndAlso cancel.IsCancellationRequested, "final validation progress can cancel before the runtime hash begins", passed)
                Else
                    Check(blocks > 0 AndAlso actualCaller = ApplyState.FullName AndAlso finalNotice = final, "component cancellation hits the intended ApplyAsync validation phase: " & scope, passed)
                    Check(blocked, "component validation protects its private runtime while hashing: " & scope, passed)
                End If
                Check(originalToken AndAlso Not returned, "component validation cancellation preserves the public caller token without success: " & scope, passed)
                Check(sharedPreserved AndAlso If(precancel, settingsPreserved, True), "cancelled validation preserves the original shared runtime and timestamp: " & scope, passed)
                If Not precancel Then Check(settingsPreserved, "cancelled validation preserves existing instance metadata and isolation settings: " & scope, passed)
                Check(noInstance AndAlso cleanAfterCancel, "cancelled validation publishes no instance and cleans its private work/staging trees: " & scope, passed)
                Check(releasedAfterCancel, "cancelled validation releases shared/settings file leases: " & scope, passed)
                Check(CBool(observation("RetrySuccess")), "fresh-token retry publishes complete checked runtime and instance: " & scope, passed)
                Check(CInt(observation("RetryInstallerRequestsAfter")) = requestsBeforeRetry AndAlso CBool(observation("RetrySettingsPreserved")) AndAlso CBool(observation("RetryPrivateTreeClean")), "validation retry reuses the checked installer cache, preserves existing settings and cleans private files: " & scope, passed)
                If precancel Then
                    Check(bytes = 0 AndAlso blocks = 0, "pre-cancelled final validation hashes zero runtime bytes", passed)
                Else
                    Check(bytes > 0 AndAlso bytes < Payload.Length, "component validation stops hashing before reading the complete runtime: " & scope, passed)
                End If
            End Using
        End Using
    End Function
End Module
