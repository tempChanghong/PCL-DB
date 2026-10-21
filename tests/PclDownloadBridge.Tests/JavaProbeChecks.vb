Imports System.Diagnostics
Imports System.IO
Imports System.Reflection
Imports System.Threading
Imports PclDownloadBridge.Core

Friend Module JavaProbeChecks
    Friend Function RunAsync(testRoot As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        If onlyCase = "java-probe-all" Then onlyCase = Nothing
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "java-probe-real-noise" Then RealNoise(passed)
        If onlyCase Is Nothing OrElse onlyCase = "java-probe-formats" Then Formats(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "java-probe-streams" Then Streams(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "java-probe-failures" Then Failures(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "java-probe-inherited" Then InheritedPipe(testRoot, passed)
        If passed.Count = 0 Then Throw New ArgumentException(onlyCase)
        Return Task.FromResult(passed)
    End Function

    Friend Function TryRunFixture(args As String()) As Boolean
        If args.Length <> 1 OrElse args(0) <> "-version" Then Return False
        Dim mode = Environment.GetEnvironmentVariable("PCL-DB_TEST_JAVA_PROBE")
        If String.IsNullOrEmpty(mode) Then Return False
        Select Case mode
            Case "legacy"
                Console.Error.WriteLine("java version ""1.8.0_402""")
            Case "modern"
                Console.Error.WriteLine("openjdk version ""21.0.2"" 2024-01-16")
            Case "unquoted"
                Console.Error.WriteLine("openjdk 17.0.12 2024-07-16")
            Case "ea"
                Console.Error.WriteLine("openjdk version ""25-ea"" 2025-09-16")
            Case "stdout"
                Console.Out.WriteLine("openjdk version ""21.0.2""")
            Case "tail"
                Console.Error.Write("java version ""1.8.0_402""")
            Case "split"
                Console.Error.Write("openjdk version ""2") : Console.Error.Flush()
                Thread.Sleep(50)
                Console.Error.Write("1.0.2""")
            Case "large-before", "large-after"
                If mode = "large-after" Then Console.Error.WriteLine("openjdk version ""21.0.2""")
                Console.Out.WriteLine(New String("o"c, 2 * 1024 * 1024))
                Console.Error.WriteLine(New String("e"c, 2 * 1024 * 1024))
                If mode = "large-before" Then Console.Error.WriteLine("openjdk version ""21.0.2""")
            Case "priority"
                Console.Out.WriteLine("openjdk version ""21.0.2""")
                Console.Error.WriteLine("java version ""1.8.0_402""")
            Case "exit"
                Console.Error.WriteLine("openjdk version ""21.0.2""")
                Environment.ExitCode = 7
            Case "spoof"
                Console.Error.WriteLine("Picked up JAVA_TOOL_OPTIONS: -Dpcd.message=version ""99""")
                Console.Error.WriteLine("java version ""1.8.0_402""")
            Case "missing"
                Console.Error.WriteLine("runtime initialization failed")
            Case "overflow"
                Console.Error.WriteLine("openjdk version ""999999999999999999999999.0.1""")
            Case "wait"
                File.WriteAllText(Environment.GetEnvironmentVariable("PCL-DB_TEST_JAVA_PID"), Process.GetCurrentProcess().Id.ToString())
                Thread.Sleep(30000)
            Case "inherited"
                Dim info As New ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "-version") With {.UseShellExecute = False, .CreateNoWindow = True, .RedirectStandardInput = True}
                info.EnvironmentVariables("PCL-DB_TEST_JAVA_PROBE") = "wait"
                Using child = Process.Start(info)
                    child.StandardInput.Close()
                    Dim clock = Stopwatch.StartNew()
                    While Not File.Exists(Environment.GetEnvironmentVariable("PCL-DB_TEST_JAVA_PID")) AndAlso clock.ElapsedMilliseconds < 3000
                        Thread.Sleep(10)
                    End While
                End Using
                Console.Error.WriteLine("openjdk version ""21.0.2""")
            Case Else
                Throw New ArgumentException(mode)
        End Select
        Return True
    End Function

    Private Sub Check(condition As Boolean, label As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub

    Private Sub InheritedPipe(testRoot As String, passed As List(Of String))
        Dim java = Fixture(testRoot), pidFile = Path.Combine(Path.GetDirectoryName(java), "inherited.pid")
        Dim saved = Environment.GetEnvironmentVariable("PCL-DB_TEST_JAVA_PID")
        Try
            Environment.SetEnvironmentVariable("PCL-DB_TEST_JAVA_PID", pidFile)
            Dim watch = Stopwatch.StartNew(), available = Probe(java, "inherited", 21)
            Check(Not available AndAlso watch.ElapsedMilliseconds < 8000, "Java probe bounds output draining when a descendant inherits an open pipe", passed)
            Dim pid = Integer.Parse(File.ReadAllText(pidFile))
            Using child = Process.GetProcessById(pid)
                Check(Not child.HasExited AndAlso String.Equals(child.MainModule.FileName, java, StringComparison.OrdinalIgnoreCase), "Java probe does not terminate the separate inherited-pipe descendant", passed)
            End Using
        Finally
            Environment.SetEnvironmentVariable("PCL-DB_TEST_JAVA_PID", saved)
            If File.Exists(pidFile) Then
                Try
                    Using child = Process.GetProcessById(Integer.Parse(File.ReadAllText(pidFile)))
                        If String.Equals(child.MainModule.FileName, java, StringComparison.OrdinalIgnoreCase) Then
                            child.Kill()
                            child.WaitForExit(2000)
                        End If
                    End Using
                Catch ex As ArgumentException
                End Try
            End If
        End Try
    End Sub

    Private Function Fixture(testRoot As String) As String
        Dim directory = Path.Combine(testRoot, "Java 探测样本")
        System.IO.Directory.CreateDirectory(directory)
        Dim source = Assembly.GetExecutingAssembly().Location, target = Path.Combine(directory, "java.exe")
        For Each name In {Path.GetFileName(source), Path.GetFileName(source) & ".config", "PclDownloadBridge.Core.dll", "Newtonsoft.Json.dll"}
            Dim input = Path.Combine(Path.GetDirectoryName(source), name)
            If File.Exists(input) Then File.Copy(input, Path.Combine(directory, If(name = Path.GetFileName(source), "java.exe", If(name = Path.GetFileName(source) & ".config", "java.exe.config", name))), True)
        Next
        Return target
    End Function

    Private Function Probe(java As String, mode As String, minimum As Integer) As Boolean
        Dim saved = Environment.GetEnvironmentVariable("PCL-DB_TEST_JAVA_PROBE")
        Try
            Environment.SetEnvironmentVariable("PCL-DB_TEST_JAVA_PROBE", mode)
            Try
                Return JavaRuntime.Find(java, minimum) = java
            Catch ex As InvalidOperationException
                Return False
            End Try
        Finally
            Environment.SetEnvironmentVariable("PCL-DB_TEST_JAVA_PROBE", saved)
        End Try
    End Function

    Private Sub Formats(testRoot As String, passed As List(Of String))
        Dim java = Fixture(testRoot)
        For Each item In {Tuple.Create("legacy", 8), Tuple.Create("modern", 21), Tuple.Create("unquoted", 17), Tuple.Create("ea", 25), Tuple.Create("stdout", 21), Tuple.Create("tail", 8), Tuple.Create("split", 21)}
            Check(Probe(java, item.Item1, item.Item2), "Java probe accepts " & item.Item1 & " version output in a space-containing Unicode path", passed)
        Next
        Check(Not Probe(java, "legacy", 17), "Java probe rejects a version below the installer minimum", passed)
    End Sub

    Private Sub Streams(testRoot As String, passed As List(Of String))
        Dim java = Fixture(testRoot)
        Check(Probe(java, "large-before", 21), "Java probe drains both streams and finds a version after multi-megabyte diagnostic lines", passed)
        Check(Probe(java, "large-after", 21), "Java probe retains an early version while draining later multi-megabyte output", passed)
        Check(Not Probe(java, "priority", 17), "Java probe preserves stderr version precedence independently of stream scheduling", passed)
    End Sub

    Private Sub Failures(testRoot As String, passed As List(Of String))
        Dim java = Fixture(testRoot)
        For Each mode In {"exit", "spoof", "missing", "overflow"}
            Check(Not Probe(java, mode, 17), "Java probe rejects " & mode & " output instead of reporting an available runtime", passed)
        Next
        Dim saved = Environment.GetEnvironmentVariable("PCL-DB_TEST_JAVA_PID"), pidFile = Path.Combine(Path.GetDirectoryName(java), "probe.pid")
        Try
            Environment.SetEnvironmentVariable("PCL-DB_TEST_JAVA_PID", pidFile)
            Dim watch = Stopwatch.StartNew()
            Check(Not Probe(java, "wait", 8) AndAlso watch.ElapsedMilliseconds < 8000, "Java probe rejects a stalled runtime within its bounded timeout", passed)
            Dim pid = Integer.Parse(File.ReadAllText(pidFile)), stopped As Boolean
            Try
                Using owned = Process.GetProcessById(pid)
                    stopped = owned.WaitForExit(2000)
                    If Not stopped Then owned.Kill()
                End Using
            Catch ex As ArgumentException
                stopped = True
            End Try
            Check(stopped, "Java probe terminates its own stalled process before returning", passed)
        Finally
            Environment.SetEnvironmentVariable("PCL-DB_TEST_JAVA_PID", saved)
        End Try
    End Sub

    Private Sub RealNoise(passed As List(Of String))
        Dim names = {"JAVA_TOOL_OPTIONS", "JDK_JAVA_OPTIONS", "_JAVA_OPTIONS"}
        Dim saved = names.ToDictionary(Function(name) name, Function(name) Environment.GetEnvironmentVariable(name))
        Try
            For Each name In names
                Environment.SetEnvironmentVariable(name, Nothing)
            Next
            Dim java = JavaRuntime.Find(Nothing, 8)
            Environment.SetEnvironmentVariable("JAVA_TOOL_OPTIONS", "-Dpcd.probe=" & New String("x"c, 16384))
            Dim watch = Stopwatch.StartNew(), selected As String
            Try
                selected = JavaRuntime.Find(java, 8)
            Catch ex As InvalidOperationException
                Throw New InvalidOperationException("The same verified Java was rejected with 16 KiB JAVA_TOOL_OPTIONS output after " & watch.ElapsedMilliseconds & " ms.", ex)
            End Try
            If selected <> java Then Throw New InvalidOperationException("Noisy real Java selection changed its path.")
            passed.Add("real Java with 16 KiB JAVA_TOOL_OPTIONS output remains available")
        Finally
            For Each name In names
                Environment.SetEnvironmentVariable(name, saved(name))
            Next
        End Try
    End Sub
End Module
