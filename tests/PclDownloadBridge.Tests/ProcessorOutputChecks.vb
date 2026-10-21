Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module ProcessorOutputChecks
    Private Const RuntimePath As String = "libraries/fixture/output/1/output-1.jar"

    Friend Async Function RunAsync(testRoot As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "processor-output-reuse" Then Await ReuseAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "processor-output-chain" Then Await ChainAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "processor-output-partial" Then Await PartialAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "processor-output-unverified" Then Await UnverifiedAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "processor-output-relative" Then Await RelativeAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "processor-output-cancel" Then Await CancelAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "processor-output-failure" Then Await FailureAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "processor-output-unavailable" Then Await UnavailableAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "processor-output-outside" Then Await OutsideAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "processor-output-log-all" OrElse onlyCase = "processor-output-log-readonly" Then Await LogUnavailableAsync(testRoot, passed, True)
        If onlyCase Is Nothing OrElse onlyCase = "processor-output-log-all" OrElse onlyCase = "processor-output-log-occupied" Then Await LogUnavailableAsync(testRoot, passed, False)
        If onlyCase Is Nothing OrElse onlyCase = "processor-output-log-all" OrElse onlyCase = "processor-output-log-failure" Then Await LogFailureAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "processor-output-log-all" OrElse onlyCase = "processor-output-log-cancel" Then Await LogCancelAsync(testRoot, passed)
        If passed.Count = 0 Then Throw New ArgumentException("Unknown processor output case: " & onlyCase)
        Return passed
    End Function

    Private Sub Assert(condition As Boolean, label As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub

    Private Function Hash(value As String) As String
        Return Hash(Encoding.UTF8.GetBytes(value))
    End Function
    Private Function Hash(value As Byte()) As String
        Using digest = Security.Cryptography.SHA1.Create()
            Return BitConverter.ToString(digest.ComputeHash(value)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function
    Private Function Processor(value As String, Optional outputName As String = "{PATCHED}") As JObject
        Return New JObject From {{"sides", New JArray("client")}, {"jar", "fixture:processor:1"}, {"classpath", New JArray()},
            {"args", New JArray(outputName, value)}, {"outputs", New JObject From {{outputName, Hash(value)}}}}
    End Function
    Private Function Selection(fixture As InstallationChecks.InstallFixture, processors As JArray, Optional finalValue As String = "patched") As ComponentSelection
        Dim jar = ComponentChecks.ProcessorJar()
        fixture.Server.Files("/output-processor") = jar
        Dim profile As New JObject From {{"minecraft", fixture.Entry.Id},
            {"libraries", New JArray(New JObject From {{"name", "fixture:processor:1"}, {"downloads", New JObject From {{"artifact", New JObject From {{"path", "fixture/processor/1/processor-1.jar"}, {"url", fixture.Server.Url & "/output-processor"}, {"sha1", Hash(jar)}, {"size", jar.Length}}}}}})},
            {"data", New JObject From {{"PATCHED", New JObject From {{"client", "[fixture:output:1]"}}}, {"PATCHED_SHA", New JObject From {{"client", "'" & Hash(finalValue).ToUpperInvariant() & "'"}}}}}, {"processors", processors}}
        Dim version As New JObject From {{"mainClass", "fixture.Loader"}, {"inheritsFrom", fixture.Entry.Id},
            {"libraries", New JArray(New JObject From {{"name", "fixture:output:1"}, {"downloads", New JObject From {{"artifact", New JObject From {{"path", RuntimePath.Substring(10)}, {"url", ""}, {"sha1", Hash(finalValue)}, {"size", Encoding.UTF8.GetByteCount(finalValue)}}}}}})}}
        Using buffer As New MemoryStream()
            Using archive As New ZipArchive(buffer, ZipArchiveMode.Create, True)
                For Each entry In {New KeyValuePair(Of String, JObject)("install_profile.json", profile), New KeyValuePair(Of String, JObject)("version.json", version)}
                    Using writer As New StreamWriter(archive.CreateEntry(entry.Key).Open(), New UTF8Encoding(False))
                        writer.Write(entry.Value.ToString())
                    End Using
                Next
            End Using
            fixture.Server.Files("/output-installer") = buffer.ToArray()
        End Using
        Dim selected As New ComponentSelection()
        selected.Entries(ComponentKind.NeoForge) = New ComponentEntry With {.Kind = ComponentKind.NeoForge, .Minecraft = fixture.Entry.Id, .Version = "fixture", .Url = fixture.Server.Url & "/output-installer", .Sha1 = Hash(fixture.Server.Files("/output-installer"))}
        Return selected
    End Function
    Private Function Executions(root As String) As Integer
        Dim log = SafeFiles.UnderRoot(root, ".vanilla-downloader/installer.log")
        Return If(File.Exists(log), File.ReadAllLines(log).Count(Function(line) line.Contains("fixture processor complete")), 0)
    End Function
    Private Function OutputFiles(root As String) As List(Of String)
        Dim cache = Path.Combine(root, ".vanilla-downloader", "processor-cache")
        Return If(Directory.Exists(cache), Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories).ToList(), New List(Of String))
    End Function

    Private Async Function ReuseAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(testRoot, "processor-output-reuse")
            Dim processorInfo = Processor("patched")
            processorInfo("outputs")("{PATCHED}") = "{PATCHED_SHA}"
            Dim selected = Selection(fixture, New JArray(processorInfo))
            Dim delayed As Boolean, sawUnknown As Boolean
            Dim progress As New InlineProgress(Sub(value)
                If value.Phase = "components" AndAlso Not delayed Then
                    delayed = True
                    Thread.Sleep(150) ' Ensure the real Java output passes the UI throttle.
                End If
                If value.Stage = "运行安装处理器" Then sawUnknown = value.StageFraction < 0
            End Sub)
            Await fixture.InstallAsync("first", selected, progress:=progress)
            Assert(sawUnknown, "parallel aggregation preserves indeterminate progress from actual Java processor output", passed)
            Await fixture.InstallAsync("second", selected)
            Console.WriteLine("PROCESSOR_EXECUTIONS_AFTER_TWO_INSTALLS=" & Executions(fixture.Root))
            Assert(Executions(fixture.Root) = 1, "repeat install restores checked processor outputs without another Java execution", passed)
            Assert(OutputFiles(fixture.Root).Count = 1 AndAlso File.ReadAllText(OutputFiles(fixture.Root).Single()) = "patched", "only verified output content survives private work cleanup", passed)
            Dim otherRoot = Path.Combine(testRoot, "other-processor-root", ".minecraft")
            Await New MinecraftInstaller(New SourcePolicy With {.AllowLoopback = True}).InstallAsync(fixture.Entry, otherRoot, "third", {fixture.Root}, Nothing, CancellationToken.None, components:=selected)
            Assert(Executions(otherRoot) = 0 AndAlso File.ReadAllText(SafeFiles.UnderRoot(otherRoot, RuntimePath)) = "patched" AndAlso OutputFiles(otherRoot).Count = 1, "selected reuse root supplies checked outputs and seeds the new root cache", passed)
            Assert(Not File.Exists(SafeFiles.UnderRoot(otherRoot, "libraries/fixture/processor/1/processor-1.jar")), "output reuse keeps installation-only dependencies out of shared runtime libraries", passed)
        End Using
    End Function
    Private Async Function ChainAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(testRoot, "processor-output-chain")
            Dim selected = Selection(fixture, New JArray(Processor("intermediate"), Processor("patched")))
            Await fixture.InstallAsync("chain-first", selected)
            Await fixture.InstallAsync("chain-second", selected)
            Assert(Executions(fixture.Root) = 2 AndAlso OutputFiles(fixture.Root).Count = 2, "processors sharing one output path restore their own declared content in execution order", passed)
            Assert(File.ReadAllText(SafeFiles.UnderRoot(fixture.Root, RuntimePath)) = "patched", "output chaining publishes the final stage rather than an earlier cached stage", passed)
        End Using
    End Function
    Private Async Function PartialAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(testRoot, "processor-output-partial")
            Dim processorInfo = Processor("patched")
            DirectCast(processorInfo("args"), JArray).Add("{ROOT}/data/extra.txt")
            DirectCast(processorInfo("args"), JArray).Add("extra")
            processorInfo("outputs")("{ROOT}/data/extra.txt") = Hash("extra")
            Dim selected = Selection(fixture, New JArray(processorInfo))
            Await fixture.InstallAsync("pair-first", selected)
            Dim extra = OutputFiles(fixture.Root).Single(Function(path) File.ReadAllText(path) = "extra")
            File.Delete(extra)
            Await fixture.InstallAsync("pair-missing", selected)
            Assert(Executions(fixture.Root) = 2 AndAlso OutputFiles(fixture.Root).Count = 2, "one missing output forces the entire multi-output processor to execute again", passed)
            Dim cached = OutputFiles(fixture.Root).Single(Function(path) File.ReadAllText(path) = "patched")
            File.WriteAllText(cached, "broken!", New UTF8Encoding(False))
            Await fixture.InstallAsync("pair-damaged", selected)
            Assert(Executions(fixture.Root) = 3 AndAlso File.ReadAllText(cached) = "patched", "same-size corrupt output cache is rejected and replaced after successful processing", passed)
            Await fixture.InstallAsync("pair-recovered", selected)
            Assert(Executions(fixture.Root) = 3, "all outputs must match before a multi-output processor can be skipped", passed)
        End Using
    End Function
    Private Async Function UnverifiedAsync(testRoot As String, passed As List(Of String)) As Task
        For Each mode In {"absent", "empty", "unhashed", "partialhash"}
            Using fixture As New InstallationChecks.InstallFixture(testRoot, "processor-output-" & mode)
                Dim processorInfo = Processor("patched")
                If mode = "absent" Then
                    processorInfo.Remove("outputs")
                ElseIf mode = "empty" Then
                    processorInfo("outputs") = New JObject()
                ElseIf mode = "unhashed" Then
                    processorInfo("outputs")("{PATCHED}") = ""
                Else
                    DirectCast(processorInfo("args"), JArray).Add("{ROOT}/data/extra.txt")
                    DirectCast(processorInfo("args"), JArray).Add("extra")
                    processorInfo("outputs")("{ROOT}/data/extra.txt") = ""
                End If
                Dim selected = Selection(fixture, New JArray(processorInfo))
                Await fixture.InstallAsync(mode & "-first", selected)
                Await fixture.InstallAsync(mode & "-second", selected)
                Assert(Executions(fixture.Root) = 2 AndAlso OutputFiles(fixture.Root).Count = 0, mode & " output declarations never authorize skipping a processor", passed)
            End Using
        Next
    End Function
    Private Async Function RelativeAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(testRoot, "processor-output-relative")
            Dim selected = Selection(fixture, New JArray(Processor("patched", RuntimePath)))
            Await fixture.InstallAsync("relative-first", selected)
            Await fixture.InstallAsync("relative-second", selected)
            Assert(Executions(fixture.Root) = 1 AndAlso File.ReadAllText(SafeFiles.UnderRoot(fixture.Root, RuntimePath)) = "patched", "relative processor outputs are checked under the Java working directory and reused", passed)
        End Using
    End Function
    Private Async Function CancelAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(testRoot, "processor-output-cancel"), cancel As New CancellationTokenSource()
            Dim selected = Selection(fixture, New JArray(Processor("patched")))
            Await fixture.InstallAsync("cancel-source", selected)
            Dim progress As New CallbackProgress(Sub(value)
                                                     If value.Detail IsNot Nothing AndAlso value.Detail.Contains("已校验输出") Then cancel.Cancel()
                                                 End Sub)
            Dim cancelled = False
            Try
                Await fixture.InstallAsync("cancel-target", selected, progress:=progress, token:=cancel.Token)
            Catch ex As OperationCanceledException
                cancelled = True
            End Try
            Assert(cancelled AndAlso Not Directory.Exists(SafeFiles.UnderRoot(fixture.Root, "versions/cancel-target")), "cancellation after output reuse prevents instance publication", passed)
            Await fixture.InstallAsync("cancel-target", selected)
            Assert(Executions(fixture.Root) = 1 AndAlso Directory.Exists(SafeFiles.UnderRoot(fixture.Root, "versions/cancel-source")), "cancelled reuse can be retried without rerunning Java or changing the original instance", passed)
        End Using
    End Function
    Private Async Function FailureAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(testRoot, "processor-output-failure")
            Dim processorInfo = Processor("wrong")
            processorInfo("outputs")("{PATCHED}") = Hash("patched")
            Dim selected = Selection(fixture, New JArray(processorInfo))
            Dim rejected = False
            Try
                Await fixture.InstallAsync("bad-output", selected)
            Catch ex As InvalidDataException
                rejected = True
            End Try
            Assert(rejected AndAlso OutputFiles(fixture.Root).Count = 0 AndAlso Not Directory.Exists(SafeFiles.UnderRoot(fixture.Root, "versions/bad-output")), "failed output verification never creates reusable content or a completed instance", passed)
            selected = Selection(fixture, New JArray(Processor("patched")))
            Await fixture.InstallAsync("bad-output", selected)
            Assert(Executions(fixture.Root) = 2 AndAlso OutputFiles(fixture.Root).Count = 1, "corrected installation can retry the failed instance name and cache its verified output", passed)
        End Using
    End Function
    Private Async Function UnavailableAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(testRoot, "processor-output-unavailable")
            Dim selected = Selection(fixture, New JArray(Processor("patched")))
            Dim blocked = SafeFiles.UnderRoot(fixture.Root, ".vanilla-downloader/processor-cache")
            Directory.CreateDirectory(Path.GetDirectoryName(blocked))
            File.WriteAllText(blocked, "keep")
            Await fixture.InstallAsync("blocked-first", selected)
            Await fixture.InstallAsync("blocked-second", selected)
            Assert(Executions(fixture.Root) = 2 AndAlso File.ReadAllText(blocked) = "keep", "optional output cache failure falls back to Java execution and preserves the blocking file", passed)
            Assert(Not Directory.EnumerateFiles(fixture.Root, "*.cache-*", SearchOption.AllDirectories).Any(), "output cache fallback leaves no temporary copies", passed)
        End Using
    End Function
    Private Async Function OutsideAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(testRoot, "processor-output-outside")
            Dim outside = Path.Combine(testRoot, "outside-processor-output.txt")
            File.WriteAllText(outside, "keep")
            Dim processorInfo = Processor("patched")
            DirectCast(processorInfo("outputs"), JObject).Add(outside, Hash("keep"))
            Dim selected = Selection(fixture, New JArray(processorInfo))
            Dim rejected = False
            Try
                Await fixture.InstallAsync("outside", selected)
            Catch ex As InvalidDataException
                rejected = True
            End Try
            Assert(rejected AndAlso Executions(fixture.Root) = 0 AndAlso File.ReadAllText(outside) = "keep" AndAlso OutputFiles(fixture.Root).Count = 0, "output declarations outside private work are rejected before execution or caching", passed)
        End Using
    End Function
    Private Function PrepareLog(root As String) As String
        Dim log = SafeFiles.UnderRoot(root, ".vanilla-downloader/installer.log")
        Directory.CreateDirectory(Path.GetDirectoryName(log))
        File.WriteAllText(log, "existing installation log" & vbCrLf, New UTF8Encoding(False))
        Return log
    End Function

    Private Function UncachedProcessor() As JObject
        Dim result = Processor("patched")
        result("outputs") = New JObject()
        Return result
    End Function

    Private Function LogProgress(onLine As Action) As IProgress(Of TransferProgress)
        Dim delayed As Integer
        Return New CallbackProgress(Sub(value)
                                        ' Let the real installer clock pass its UI throttle before Java starts.
                                        If value.Phase = "processors" AndAlso value.Stage <> "运行安装处理器" AndAlso Interlocked.Exchange(delayed, 1) = 0 Then Thread.Sleep(150)
                                        If value.Phase = "processors" AndAlso value.Stage = "运行安装处理器" Then onLine()
                                    End Sub)
    End Function

    Private Async Function LogUnavailableAsync(testRoot As String, passed As List(Of String), readOnlyLog As Boolean) As Task
        Dim mode = If(readOnlyLog, "readonly", "occupied")
        Using fixture As New InstallationChecks.InstallFixture(testRoot, "processor-output-log-" & mode)
            Dim log = PrepareLog(fixture.Root), original = File.ReadAllBytes(log)
            Dim attributes = File.GetAttributes(log), hold As FileStream = Nothing, reported As Integer
            Dim selected = Selection(fixture, New JArray(UncachedProcessor()))
            Try
                If readOnlyLog Then
                    File.SetAttributes(log, attributes Or FileAttributes.ReadOnly)
                Else
                    hold = New FileStream(log, FileMode.Open, FileAccess.Read, FileShare.Read)
                End If
                Await fixture.InstallAsync("log-first", selected, progress:=LogProgress(Sub() Interlocked.Increment(reported)))
                Assert(File.Exists(SafeFiles.UnderRoot(fixture.Root, "versions/log-first/log-first.json")) AndAlso File.ReadAllText(SafeFiles.UnderRoot(fixture.Root, RuntimePath)) = "patched", mode & " log does not prevent verified loader installation", passed)
                Assert(File.ReadAllBytes(log).SequenceEqual(original) AndAlso Volatile.Read(reported) > 0, mode & " log preserves original bytes while Java output still reaches progress", passed)
            Finally
                hold?.Dispose()
                File.SetAttributes(log, attributes)
            End Try
            Dim firstJson = SafeFiles.UnderRoot(fixture.Root, "versions/log-first/log-first.json"), firstBytes = File.ReadAllBytes(firstJson)
            Await fixture.InstallAsync("log-second", selected)
            Assert(Executions(fixture.Root) = 1 AndAlso File.ReadAllText(log).StartsWith(Encoding.UTF8.GetString(original), StringComparison.Ordinal) AndAlso File.ReadAllBytes(firstJson).SequenceEqual(firstBytes), mode & " log resumes appending after access recovers without changing the earlier instance", passed)
        End Using
    End Function

    Private Async Function LogFailureAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(testRoot, "processor-output-log-failure")
            Dim log = PrepareLog(fixture.Root), original = File.ReadAllBytes(log), attributes = File.GetAttributes(log)
            Dim processorInfo = UncachedProcessor()
            processorInfo("args") = New JArray("--fail")
            Dim selected = Selection(fixture, New JArray(processorInfo)), failure As IOException = Nothing
            Try
                File.SetAttributes(log, attributes Or FileAttributes.ReadOnly)
                Try
                    Await fixture.InstallAsync("log-retry", selected)
                Catch ex As IOException
                    failure = ex
                End Try
                Assert(failure IsNot Nothing AndAlso failure.Message.Contains("(7)") AndAlso failure.Message.Contains("fixture processor failure"), "readonly log preserves the Java exit code and stderr failure reason", passed)
                Assert(Not Directory.Exists(SafeFiles.UnderRoot(fixture.Root, "versions/log-retry")) AndAlso OutputFiles(fixture.Root).Count = 0 AndAlso File.ReadAllBytes(log).SequenceEqual(original), "failed Java with readonly log publishes neither an instance nor cached outputs and preserves the log", passed)
            Finally
                File.SetAttributes(log, attributes)
            End Try
            Await fixture.InstallAsync("log-retry", Selection(fixture, New JArray(Processor("patched"))))
            Assert(Executions(fixture.Root) = 1 AndAlso OutputFiles(fixture.Root).Count = 1 AndAlso File.Exists(SafeFiles.UnderRoot(fixture.Root, "versions/log-retry/log-retry.json")), "failed Java with unavailable log can retry the same instance name after recovery", passed)
        End Using
    End Function

    Private Async Function LogCancelAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New InstallationChecks.InstallFixture(testRoot, "processor-output-log-cancel"), cancel As New CancellationTokenSource()
            Dim log = PrepareLog(fixture.Root), original = File.ReadAllBytes(log), attributes = File.GetAttributes(log)
            Dim selected = Selection(fixture, New JArray(Processor("patched"))), cancelled As Boolean
            Try
                File.SetAttributes(log, attributes Or FileAttributes.ReadOnly)
                Try
                    Await fixture.InstallAsync("log-retry", selected, progress:=LogProgress(Sub() cancel.Cancel()), token:=cancel.Token)
                Catch ex As OperationCanceledException
                    cancelled = True
                End Try
                Assert(cancelled, "readonly log still permits cancellation from actual Java output progress", passed)
                Assert(Not Directory.Exists(SafeFiles.UnderRoot(fixture.Root, "versions/log-retry")) AndAlso OutputFiles(fixture.Root).Count = 0 AndAlso File.ReadAllBytes(log).SequenceEqual(original), "cancelled Java with readonly log publishes no instance or processor cache and preserves the log", passed)
            Finally
                File.SetAttributes(log, attributes)
            End Try
            Await fixture.InstallAsync("log-retry", selected)
            Assert(Executions(fixture.Root) = 1 AndAlso OutputFiles(fixture.Root).Count = 1 AndAlso File.Exists(SafeFiles.UnderRoot(fixture.Root, "versions/log-retry/log-retry.json")), "cancelled Java with unavailable log can retry the same instance name after recovery", passed)
        End Using
    End Function

    Private Class CallbackProgress
        Implements IProgress(Of TransferProgress)
        Private ReadOnly _callback As Action(Of TransferProgress)
        Friend Sub New(callback As Action(Of TransferProgress))
            _callback = callback
        End Sub
        Public Sub Report(value As TransferProgress) Implements IProgress(Of TransferProgress).Report
            _callback(value)
        End Sub
    End Class
End Module
