Imports System.Collections.Concurrent
Imports System.Diagnostics
Imports System.IO
Imports System.IO.Compression
Imports System.Reflection
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module ProcessorStreamChecks
    Private Const ModeVariable As String = "PCL-DB_TEST_PROCESSOR_STREAMS"
    Private Const StateVariable As String = "PCL-DB_TEST_PROCESSOR_STATE"

    Friend Async Function RunAsync(testRoot As String, Optional onlyCase As String = "processor-streams-all") As Task(Of List(Of String))
        Dim passed As New List(Of String)
        If onlyCase = "processor-streams-all" OrElse onlyCase = "processor-streams-normal" Then Await NormalAsync(testRoot, passed)
        If onlyCase = "processor-streams-all" OrElse onlyCase = "processor-streams-entry" Then Await EntryAsync(testRoot, passed)
        If onlyCase = "processor-streams-all" OrElse onlyCase = "processor-streams-cancel" Then
            Await InheritedAsync(testRoot, "inherited-cancel", passed)
            Await InheritedAsync(testRoot, "inherited-exit", passed, cancelDrain:=True)
        End If
        If onlyCase = "processor-streams-all" OrElse onlyCase = "processor-streams-timeout" Then
            Await InheritedAsync(testRoot, "inherited-exit", passed)
            Await InheritedAsync(testRoot, "inherited-fail", passed)
        End If
        If onlyCase = "processor-streams-all" OrElse onlyCase = "processor-streams-install" Then Await InstallationAsync(testRoot, passed)
        If passed.Count = 0 Then Throw New ArgumentException(onlyCase)
        Return passed
    End Function

    Friend Function TryRunFixture(args As String()) As Boolean
        Dim mode = Environment.GetEnvironmentVariable(ModeVariable)
        If String.IsNullOrEmpty(mode) Then Return False
        If args.SequenceEqual({"-version"}) Then
            Console.Error.WriteLine("openjdk version ""21.0.2""")
            Return True
        End If
        Dim state = Environment.GetEnvironmentVariable(StateVariable)
        If mode = "child" Then
            WritePid(state, "child.pid")
            Thread.Sleep(30000)
            Return True
        End If
        WritePid(state, "parent.pid")
        If mode.StartsWith("inherited-", StringComparison.Ordinal) Then
            Dim info As New ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--processor-stream-fixture") With {.UseShellExecute = False, .CreateNoWindow = True, .RedirectStandardInput = True}
            info.EnvironmentVariables(ModeVariable) = "child"
            Using child = Process.Start(info)
                child.StandardInput.Close()
                Dim clock = Stopwatch.StartNew()
                While Not File.Exists(Path.Combine(state, "child.pid")) AndAlso clock.ElapsedMilliseconds < 3000
                    Thread.Sleep(10)
                End While
            End Using
        End If
        Console.Out.WriteLine("fixture stdout line")
        Console.Error.WriteLine("fixture stderr line")
        File.WriteAllText(Path.Combine(state, "ready"), "ready")
        If mode = "inherited-cancel" Then Thread.Sleep(30000)
        If mode = "failure" OrElse mode = "inherited-fail" Then
            Console.Error.Write("fixture final failure")
            Environment.ExitCode = 7
        Else
            Dim main = Array.IndexOf(args, "FixtureProcessor")
            If main >= 0 Then
                Directory.CreateDirectory(Path.GetDirectoryName(args(main + 1)))
                File.WriteAllText(args(main + 1), args(main + 2), New UTF8Encoding(False))
            End If
            Console.Out.Write("fixture final stdout")
        End If
        Return True
    End Function

    Private Sub WritePid(state As String, name As String)
        Dim target = Path.Combine(state, name), temporary = target & ".new"
        File.WriteAllText(temporary, Process.GetCurrentProcess().Id.ToString())
        If File.Exists(target) Then File.Replace(temporary, target, Nothing) Else File.Move(temporary, target)
    End Sub

    Private Sub Check(condition As Boolean, label As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub

    Private Async Function FailureAsync(work As Task) As Task(Of Exception)
        Try
            Await work
            Return Nothing
        Catch ex As Exception
            Return ex
        End Try
    End Function

    Private Async Function FinishAsync(work As Task) As Task(Of Exception)
        If Await Task.WhenAny(work, Task.Delay(5000)) IsNot work Then Throw New TimeoutException("Fixture processor did not settle after its known child was stopped.")
        Return Await FailureAsync(work)
    End Function

    Private Async Function NormalAsync(testRoot As String, passed As List(Of String)) As Task
        For Each mode In {"normal", "failure"}
            Using fixture As New ProcessFixture(testRoot, mode)
                Dim lines As New ConcurrentQueue(Of String)
                Dim failure = Await FailureAsync(JavaRuntime.RunAsync(fixture.Java, {}, fixture.State, AddressOf lines.Enqueue, CancellationToken.None))
                Check(lines.Contains("fixture stdout line") AndAlso lines.Contains("fixture stderr line"), "processor drains stdout and stderr before returning: " & mode, passed)
                Check(If(mode = "normal", failure Is Nothing AndAlso lines.Contains("fixture final stdout"),
                         TypeOf failure Is IOException AndAlso failure.Message.Contains("(7)") AndAlso failure.Message.Contains("fixture final failure")),
                      "processor preserves exit status and the final unterminated output line: " & mode, passed)
            End Using
        Next
    End Function

    Private Async Function EntryAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New ProcessFixture(testRoot, "normal"), cancel As New CancellationTokenSource()
            cancel.Cancel()
            Dim failure = Await FailureAsync(JavaRuntime.RunAsync(fixture.Java, {}, fixture.State, Nothing, cancel.Token))
            Check(TypeOf failure Is OperationCanceledException, "an already cancelled processor remains cancelled", passed)
            Check(Not File.Exists(Path.Combine(fixture.State, "parent.pid")), "an already cancelled processor starts no child process", passed)
        End Using
    End Function

    Private Async Function InheritedAsync(testRoot As String, mode As String, passed As List(Of String), Optional cancelDrain As Boolean = False) As Task
        Using fixture As New ProcessFixture(testRoot, mode), cancel As New CancellationTokenSource()
            Dim work = JavaRuntime.RunAsync(fixture.Java, {}, fixture.State, Nothing, cancel.Token)
            Dim completed As Boolean, parentStopped As Boolean, childAlive As Boolean
            Dim watch As Stopwatch = Nothing, pending As Exception = Nothing
            Try
                Await fixture.ReadyAsync(mode <> "inherited-cancel")
                watch = Stopwatch.StartNew()
                If mode = "inherited-cancel" OrElse cancelDrain Then cancel.Cancel()
                Dim timeout = If(cancel.IsCancellationRequested, 2500, 8000)
                completed = Await Task.WhenAny(work, Task.Delay(timeout)) Is work
                parentStopped = Not fixture.Alive("parent.pid")
                childAlive = fixture.Alive("child.pid")
                watch.Stop()
            Catch ex As Exception
                pending = ex
            Finally
                fixture.StopKnown("child.pid")
                fixture.StopKnown("parent.pid")
            End Try
            Dim failure = Await FinishAsync(work)
            If pending IsNot Nothing Then Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(pending).Throw()
            Dim label = mode & "; cancelDrain=" & cancelDrain.ToString()
            Console.WriteLine("PROCESSOR_STREAMS " & label & " completed=" & completed.ToString() & " milliseconds=" & watch.ElapsedMilliseconds & " childAlive=" & childAlive.ToString())
            Check(completed, "inherited output pipes cannot keep the processor task pending: " & label, passed)
            Check(If(cancel.IsCancellationRequested, TypeOf failure Is OperationCanceledException,
                     TypeOf failure Is IOException AndAlso failure.Message.Contains(If(mode = "inherited-fail", "(7)", "(0)")) AndAlso failure.Message.Contains("输出流")),
                  "inherited-pipe completion preserves cancellation or reports the exit code and drain failure: " & label, passed)
            Check(parentStopped AndAlso childAlive, "processor completion stops only its owned parent and leaves the inherited-pipe child running: " & label, passed)
        End Using
    End Function

    Private Async Function InstallationAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New ProcessFixture(testRoot, "inherited-cancel"), installation As New InstallationChecks.InstallFixture(testRoot, "processor-stream-retry"), cancel As New CancellationTokenSource()
            Dim selection = SelectionFor(installation, fixture.Java)
            Dim work = installation.InstallAsync("retry", selection, token:=cancel.Token)
            Dim completed As Boolean, pending As Exception = Nothing
            Try
                Await fixture.ReadyAsync(False)
                cancel.Cancel()
                completed = Await Task.WhenAny(work, Task.Delay(2500)) Is work
                If completed Then
                    Dim failure = Await FailureAsync(work)
                    Check(TypeOf failure Is OperationCanceledException AndAlso Not Directory.Exists(Path.Combine(installation.Root, "versions", "retry")), "cancelled inherited-pipe installation publishes no instance", passed)
                    Using rootLock As New FileStream(Path.Combine(installation.Root, ".vanilla-downloader", "install.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                        Check(True, "cancelled inherited-pipe installation releases the game root lock", passed)
                    End Using
                    fixture.ChangeMode("normal")
                    Dim retry = Await installation.InstallAsync("retry", selection)
                    Check(retry.NetworkBytes = 0 AndAlso File.Exists(Path.Combine(retry.InstancePath, "retry.json")) AndAlso fixture.Alive("child.pid"), "same-name retry reuses checked downloads and completes while the separate fixture child remains alive", passed)
                End If
            Catch ex As Exception
                pending = ex
            Finally
                fixture.StopKnown("child.pid")
                fixture.StopKnown("parent.pid")
            End Try
            Await FinishAsync(work)
            If pending IsNot Nothing Then Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(pending).Throw()
            Check(completed, "cancellation cannot strand an installation behind inherited output pipes", passed)
        End Using
    End Function

    Private Function SelectionFor(fixture As InstallationChecks.InstallFixture, java As String) As ComponentSelection
        Dim profile As New JObject From {{"minecraft", fixture.Entry.Id}, {"libraries", New JArray()},
            {"data", New JObject From {{"PATCHED", New JObject From {{"client", "[fixture:stream-runtime:1]"}}}}},
            {"processors", New JArray(New JObject From {{"sides", New JArray("client")}, {"jar", "fixture:processor:1"}, {"classpath", New JArray()},
                {"args", New JArray("{PATCHED}", "patched")}, {"outputs", New JObject From {{"{PATCHED}", Hash(Encoding.UTF8.GetBytes("patched"))}}}})}}
        Dim version As New JObject From {{"mainClass", "fixture.Loader"}, {"libraries", New JArray(New JObject From {{"name", "fixture:stream-runtime:1"},
            {"downloads", New JObject From {{"artifact", New JObject From {{"path", "fixture/stream-runtime/1/stream-runtime-1.jar"}, {"url", ""}, {"sha1", Hash(Encoding.UTF8.GetBytes("patched"))}, {"size", 7}}}}}})}}
        Using buffer As New MemoryStream()
            Using zip As New ZipArchive(buffer, ZipArchiveMode.Create, True)
                For Each item In {New KeyValuePair(Of String, Byte())("install_profile.json", Encoding.UTF8.GetBytes(profile.ToString())),
                                  New KeyValuePair(Of String, Byte())("version.json", Encoding.UTF8.GetBytes(version.ToString())),
                                  New KeyValuePair(Of String, Byte())("maven/fixture/processor/1/processor-1.jar", ComponentChecks.ProcessorJar())}
                    Using output = zip.CreateEntry(item.Key).Open()
                        output.Write(item.Value, 0, item.Value.Length)
                    End Using
                Next
            End Using
            fixture.Server.Files("/stream-installer") = buffer.ToArray()
        End Using
        Dim selected As New ComponentSelection With {.JavaPath = java}
        selected.Entries(ComponentKind.NeoForge) = New ComponentEntry With {.Kind = ComponentKind.NeoForge, .Minecraft = fixture.Entry.Id, .Version = "fixture", .Url = fixture.Server.Url & "/stream-installer", .Sha1 = Hash(fixture.Server.Files("/stream-installer"))}
        Return selected
    End Function

    Private Function Hash(bytes As Byte()) As String
        Using digest = Security.Cryptography.SHA1.Create()
            Return BitConverter.ToString(digest.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function

    Private Class ProcessFixture
        Implements IDisposable
        Friend ReadOnly State As String
        Friend ReadOnly Java As String
        Private ReadOnly _previousMode As String = Environment.GetEnvironmentVariable(ModeVariable)
        Private ReadOnly _previousState As String = Environment.GetEnvironmentVariable(StateVariable)
        Friend Sub New(testRoot As String, mode As String)
            State = Path.Combine(testRoot, "处理器输出 样本", Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(State)
            Dim source = Assembly.GetExecutingAssembly().Location
            Java = Path.Combine(State, "java.exe")
            For Each name In {Path.GetFileName(source), Path.GetFileName(source) & ".config", "PclDownloadBridge.Core.dll", "Newtonsoft.Json.dll"}
                Dim input = Path.Combine(Path.GetDirectoryName(source), name)
                If File.Exists(input) Then File.Copy(input, Path.Combine(State, If(name = Path.GetFileName(source), "java.exe", If(name = Path.GetFileName(source) & ".config", "java.exe.config", name))))
            Next
            Environment.SetEnvironmentVariable(StateVariable, State)
            ChangeMode(mode)
        End Sub
        Friend Sub ChangeMode(mode As String)
            Environment.SetEnvironmentVariable(ModeVariable, mode)
        End Sub
        Friend Async Function ReadyAsync(parentExited As Boolean) As Task
            Dim clock = Stopwatch.StartNew()
            Do
                If File.Exists(Path.Combine(State, "ready")) AndAlso File.Exists(Path.Combine(State, "child.pid")) AndAlso
                   (Not parentExited OrElse Not Alive("parent.pid")) Then Return
                If clock.ElapsedMilliseconds > 5000 Then Throw New TimeoutException("Processor fixture did not reach its inherited-pipe state.")
                Await Task.Delay(10)
            Loop
        End Function
        Friend Function Alive(pidFile As String) As Boolean
            If Not File.Exists(Path.Combine(State, pidFile)) Then Return False
            Try
                Using owned = Process.GetProcessById(Integer.Parse(File.ReadAllText(Path.Combine(State, pidFile))))
                    Return Not owned.HasExited AndAlso String.Equals(owned.MainModule.FileName, Java, StringComparison.OrdinalIgnoreCase)
                End Using
            Catch ex As ArgumentException
                Return False
            Catch ex As InvalidOperationException
                Return False
            End Try
        End Function
        Friend Sub StopKnown(pidFile As String)
            If Not Alive(pidFile) Then Return
            Try
                Using owned = Process.GetProcessById(Integer.Parse(File.ReadAllText(Path.Combine(State, pidFile))))
                    If String.Equals(owned.MainModule.FileName, Java, StringComparison.OrdinalIgnoreCase) Then
                        owned.Kill()
                        owned.WaitForExit(2000)
                    End If
                End Using
            Catch ex As ArgumentException
            Catch ex As InvalidOperationException
            End Try
        End Sub
        Public Sub Dispose() Implements IDisposable.Dispose
            Try
                StopKnown("child.pid")
                StopKnown("parent.pid")
            Finally
                Environment.SetEnvironmentVariable(ModeVariable, _previousMode)
                Environment.SetEnvironmentVariable(StateVariable, _previousState)
            End Try
        End Sub
    End Class
End Module
