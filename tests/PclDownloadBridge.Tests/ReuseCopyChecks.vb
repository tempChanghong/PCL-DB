Imports System.IO
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports PclDownloadBridge.Core

Friend Module ReuseCopyChecks
    Friend Async Function RunAsync(testRoot As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        If onlyCase = "copy-all" Then onlyCase = Nothing
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "copy-network" Then Await NetworkAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "copy-local" Then Await LocalAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "copy-cancel-fallback" Then Await CancelFallbackAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "copy-corrupt" Then Await CorruptAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "copy-publish" Then Await PublishAsync(testRoot, passed)
        If onlyCase Is Nothing OrElse onlyCase = "copy-cancel-hash" OrElse onlyCase = "copy-cancel-hash-1" OrElse onlyCase = "copy-cancel-hash-2" Then
            Await CancelHashAsync(testRoot, passed, If(onlyCase = "copy-cancel-hash-1", 1, If(onlyCase = "copy-cancel-hash-2", 2, 0)))
        End If
        If passed.Count = 0 Then Throw New ArgumentException("Unknown reuse copy regression: " & onlyCase)
        Return passed
    End Function

    Private Sub Check(condition As Boolean, label As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub

    Private Class ProgressCapture
        Implements IProgress(Of TransferProgress)
        Friend Completed As Boolean
        Public Sub Report(value As TransferProgress) Implements IProgress(Of TransferProgress).Report
            If value.Completed > 0 Then Completed = True
        End Sub
    End Class

    Private Class Fixture
        Implements IDisposable
        Friend ReadOnly Server As New TestServer()
        Friend ReadOnly Root As String
        Friend ReadOnly Source As String
        Friend ReadOnly SourceFile As String
        Friend ReadOnly StreamPath As String
        Friend ReadOnly Item As DownloadFile
        Friend ReadOnly Bytes As Byte() = Encoding.UTF8.GetBytes("fixture checked reusable content")
        Friend Sub New(testRoot As String, name As String)
            Root = Path.Combine(testRoot, "copy-" & name)
            Source = Root & "-source"
            Item = New DownloadFile With {.RelativePath = "libraries/reused.jar", .Url = Server.Url & "/payload", .Size = Bytes.Length}
            Using hash As New SHA1CryptoServiceProvider()
                Item.Sha1 = BitConverter.ToString(hash.ComputeHash(Bytes)).Replace("-", "").ToLowerInvariant()
            End Using
            SourceFile = SafeFiles.UnderRoot(Source, Item.RelativePath)
            Directory.CreateDirectory(Path.GetDirectoryName(SourceFile))
            File.WriteAllBytes(SourceFile, Bytes)
            ' An NTFS alternate stream is independent of the checked default data.
            ' File.Copy still copies it, providing a deterministic real copy failure.
            StreamPath = "\\?\" & Path.GetFullPath(SourceFile) & ":fixture-note"
            File.WriteAllText(StreamPath, "fixture auxiliary metadata")
            Server.Files("/payload") = Bytes
        End Sub
        Friend Function Engine(Optional progress As IProgress(Of TransferProgress) = Nothing, Optional roots As IEnumerable(Of String) = Nothing) As DownloadEngine
            Return New DownloadEngine(New SourcePolicy With {.AllowLoopback = True}, progress, If(roots, {Source}), 1, Root)
        End Function
        Friend Function Target() As String
            Return SafeFiles.UnderRoot(Root, Item.RelativePath)
        End Function
        Friend Function HoldMetadata() As FileStream
            Return New FileStream(StreamPath, FileMode.Open, FileAccess.Read, FileShare.None)
        End Function
        Friend Function NoCopies() As Boolean
            Return Not Directory.Exists(Root) OrElse Not Directory.EnumerateFiles(Root, "*.reuse-*", SearchOption.AllDirectories).Any()
        End Function
        Friend Function SourceUnchanged() As Boolean
            Return File.ReadAllBytes(SourceFile).SequenceEqual(Bytes) AndAlso File.ReadAllText(StreamPath) = "fixture auxiliary metadata"
        End Function
        Public Sub Dispose() Implements IDisposable.Dispose
            Server.Dispose()
        End Sub
    End Class

    Private Async Function NetworkAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New Fixture(testRoot, "network"), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(10))
            Using hold = fixture.HoldMetadata(), engine = fixture.Engine()
                Await engine.DownloadAsync(fixture.Root, {fixture.Item}, timeout.Token)
                Check(SafeFiles.Matches(fixture.Target(), fixture.Item) AndAlso engine.Reused = 0 AndAlso engine.NetworkBytes = fixture.Bytes.Length AndAlso fixture.Server.Requests("/payload") = 1,
                      "a valid source whose auxiliary stream is locked falls back to one verified HTTP download", passed)
                Check(engine.Retries = 0 AndAlso fixture.NoCopies(), "optional copy failure is not an HTTP retry and leaves no partial reuse copy", passed)
            End Using
            Check(fixture.SourceUnchanged(), "copy fallback preserves both the source content and its auxiliary stream", passed)
            Using engine = fixture.Engine()
                Await engine.DownloadAsync(fixture.Root, {fixture.Item}, timeout.Token)
                Check(engine.Reused = 1 AndAlso engine.NetworkBytes = 0 AndAlso fixture.Server.Requests("/payload") = 1, "next batch reuses the verified fallback target without another request", passed)
            End Using
        End Using
    End Function

    Private Async Function LocalAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New Fixture(testRoot, "local"), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(10))
            Dim second = fixture.Root & "-second-source"
            Dim candidate = SafeFiles.UnderRoot(second, fixture.Item.RelativePath)
            Directory.CreateDirectory(Path.GetDirectoryName(candidate))
            File.WriteAllBytes(candidate, fixture.Bytes)
            Using hold = fixture.HoldMetadata(), engine = fixture.Engine(roots:={fixture.Source, second})
                Await engine.DownloadAsync(fixture.Root, {fixture.Item}, timeout.Token)
                Check(SafeFiles.Matches(fixture.Target(), fixture.Item) AndAlso engine.Reused = 1 AndAlso engine.NetworkBytes = 0 AndAlso fixture.Server.Requests.IsEmpty,
                      "copy failure in one optional root continues to another verified local candidate", passed)
                Check(fixture.NoCopies(), "successful later candidate leaves no temporary copies from the failed candidate", passed)
            End Using
            Check(fixture.SourceUnchanged() AndAlso File.ReadAllBytes(candidate).SequenceEqual(fixture.Bytes), "fallback between local candidates preserves both source roots", passed)
        End Using
    End Function

    Private Async Function CancelFallbackAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New Fixture(testRoot, "cancel-fallback"), gate As New ManualResetEventSlim(), cancel As New CancellationTokenSource(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(10))
            fixture.Server.Gates("/payload") = gate
            Dim progress As New ProgressCapture()
            Using hold = fixture.HoldMetadata(), engine = fixture.Engine(progress), linked = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token, timeout.Token)
                Dim task = engine.DownloadAsync(fixture.Root, {fixture.Item}, linked.Token)
                While Not fixture.Server.Requests.ContainsKey("/payload") AndAlso Not task.IsCompleted
                    Await Task.Delay(10, timeout.Token)
                End While
                Check(Not task.IsCompleted AndAlso fixture.Server.Requests.ContainsKey("/payload"), "failed local copy reaches the gated HTTP fallback before cancellation", passed)
                cancel.Cancel()
                Dim failure As Exception = Nothing
                Try
                    Await task
                Catch ex As Exception
                    failure = ex
                Finally
                    gate.Set()
                End Try
                Check(TypeOf failure Is OperationCanceledException AndAlso Not File.Exists(fixture.Target()) AndAlso Not progress.Completed,
                      "cancelling the fallback preserves cancellation and publishes no target or completion", passed)
                Check(engine.Reused = 0 AndAlso engine.Retries = 0 AndAlso fixture.NoCopies(), "cancelled fallback does not count a failed copy as reuse or a retry", passed)
                Using retry = fixture.Engine()
                    Await retry.DownloadAsync(fixture.Root, {fixture.Item}, timeout.Token)
                    Check(SafeFiles.Matches(fixture.Target(), fixture.Item), "retry after fallback cancellation publishes checked content while the optional stream remains locked", passed)
                End Using
            End Using
            Check(fixture.SourceUnchanged(), "fallback cancellation and retry leave the original source unchanged", passed)
        End Using
    End Function

    Private Async Function CorruptAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New Fixture(testRoot, "corrupt"), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(10))
            fixture.Server.Corrupt = True
            Using hold = fixture.HoldMetadata(), engine = fixture.Engine()
                Dim failure As Exception = Nothing
                Try
                    Await engine.DownloadAsync(fixture.Root, {fixture.Item}, timeout.Token)
                Catch ex As Exception
                    failure = ex
                End Try
                Check(TypeOf failure Is IOException AndAlso TypeOf failure.InnerException Is InvalidDataException AndAlso Not File.Exists(fixture.Target()) AndAlso fixture.Server.Requests("/payload") = 2 AndAlso engine.Retries = 2,
                      "bad hashes from HTTP fallback still fail after the normal download retries", passed)
                Check(engine.Reused = 0 AndAlso fixture.NoCopies(), "a failed fallback cannot count or publish the optional local copy", passed)
                fixture.Server.Corrupt = False
                Using retry = fixture.Engine()
                    Await retry.DownloadAsync(fixture.Root, {fixture.Item}, timeout.Token)
                    Check(SafeFiles.Matches(fixture.Target(), fixture.Item), "fixing the HTTP content allows a checked retry after copy and hash failures", passed)
                End Using
            End Using
            Check(fixture.SourceUnchanged(), "hash failure and retry preserve the original checked local source", passed)
        End Using
    End Function

    Private Async Function PublishAsync(testRoot As String, passed As List(Of String)) As Task
        Using fixture As New Fixture(testRoot, "publish"), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(10))
            Dim original(fixture.Bytes.Length - 1) As Byte
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.Target()))
            File.WriteAllBytes(fixture.Target(), original)
            Using hold As New FileStream(fixture.Target(), FileMode.Open, FileAccess.Read, FileShare.Read), engine = fixture.Engine()
                Dim failure As Exception = Nothing
                Try
                    Await engine.DownloadAsync(fixture.Root, {fixture.Item}, timeout.Token)
                Catch ex As Exception
                    failure = ex
                End Try
                Check(TypeOf failure Is IOException AndAlso fixture.Server.Requests.IsEmpty AndAlso File.ReadAllBytes(fixture.Target()).SequenceEqual(original),
                      "real target replacement failure remains an error and preserves the existing target", passed)
                Check(engine.Reused = 0 AndAlso fixture.NoCopies(), "target publication failure cleans its private copy without reporting reuse", passed)
            End Using
            Using retry = fixture.Engine()
                Await retry.DownloadAsync(fixture.Root, {fixture.Item}, timeout.Token)
                Check(retry.Reused = 1 AndAlso retry.NetworkBytes = 0 AndAlso SafeFiles.Matches(fixture.Target(), fixture.Item) AndAlso fixture.SourceUnchanged(),
                      "unlocking the real target allows verified local reuse without HTTP", passed)
            End Using
        End Using
    End Function

    Private Async Function CancelHashAsync(testRoot As String, passed As List(Of String), Optional onlyBoundary As Integer = 0) As Task
        For Each boundary In {1, 2}
            If onlyBoundary <> 0 AndAlso boundary <> onlyBoundary Then Continue For
            Using fixture As New Fixture(testRoot, "cancel-hash-" & boundary), cancel As New CancellationTokenSource(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(10))
                Dim progress As New ProgressCapture()
                Dim aliases = {"SHA1", "System.Security.Cryptography.SHA1"}
                Dim previous As New Dictionary(Of String, Type)
                For Each aliasName In aliases
                    Using algorithm = DirectCast(CryptoConfig.CreateFromName(aliasName), SHA1)
                        previous(aliasName) = algorithm.GetType()
                    End Using
                Next
                Dim count As Integer
                CopyCancellationSha1.OnHash = Sub()
                                                  If Interlocked.Increment(count) = boundary Then cancel.Cancel()
                                              End Sub
                Dim failure As Exception = Nothing
                Try
                    CryptoConfig.AddAlgorithm(GetType(CopyCancellationSha1), aliases)
                    Using engine = fixture.Engine(progress), linked = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token, timeout.Token)
                        Try
                            Await engine.DownloadAsync(fixture.Root, {fixture.Item}, linked.Token)
                        Catch ex As Exception
                            failure = ex
                        End Try
                        Check(cancel.IsCancellationRequested AndAlso TypeOf failure Is OperationCanceledException,
                              "controlled SHA-1 completion cancels the actual reuse pipeline; boundary=" & boundary, passed)
                        Check(Not File.Exists(fixture.Target()) AndAlso Not progress.Completed AndAlso engine.Reused = 0 AndAlso engine.NetworkBytes = 0 AndAlso fixture.Server.Requests.IsEmpty,
                              "cancellation after source or copy verification publishes no reused file; boundary=" & boundary, passed)
                    End Using
                Finally
                    CopyCancellationSha1.OnHash = Nothing
                    For Each entry In previous
                        CryptoConfig.AddAlgorithm(entry.Value, entry.Key)
                    Next
                End Try
                Check(fixture.NoCopies() AndAlso fixture.SourceUnchanged(), "verification-boundary cancellation cleans its copy and preserves its source; boundary=" & boundary, passed)
                Using retry = fixture.Engine()
                    Await retry.DownloadAsync(fixture.Root, {fixture.Item}, timeout.Token)
                    Check(retry.Reused = 1 AndAlso retry.NetworkBytes = 0 AndAlso SafeFiles.Matches(fixture.Target(), fixture.Item),
                          "retry after verification-boundary cancellation reuses the checked source; boundary=" & boundary, passed)
                End Using
            End Using
        Next
    End Function
End Module

' A test-process-only standard CryptoConfig provider injects cancellation after
' real SHA-1 computation. No watcher races, sleeps or production test hooks.
Public Class CopyCancellationSha1
    Inherits SHA1
    Friend Shared OnHash As Action
    Private ReadOnly _inner As New SHA1CryptoServiceProvider()
    Public Sub New()
        HashSizeValue = 160
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
