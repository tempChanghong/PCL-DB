Imports System.Collections.Concurrent
Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module CurseForgeCancellationIdentityChecks
    Private Sub Check(value As Boolean, label As String, passed As List(Of String))
        If Not value Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub

    Friend Async Function RunAsync(root As String) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        Dim folder = Path.Combine(root, "cf-cancel-identity-" & Guid.NewGuid().ToString("N"))
        Dim manifest = CurseForgeChecks.Manifest(Nothing)
        manifest("files") = New JArray(Enumerable.Range(1, 5).Select(Function(index) New JObject From {
            {"projectID", index}, {"fileID", 1000 + index}, {"required", True}}))
        Dim package = CurseForgePackage.Read(CurseForgeChecks.Pack(folder, "five", manifest))
        Check(package.References.Count = 5 AndAlso package.References.All(Function(item) item.Required),
              "CF cancellation fixture retains five ordered required references", passed)
        Await PreCancelledAsync(package, passed)
        Await HeldAsync(package, passed)
        Await FileFailureAsync(package, passed)
        Await ProgressFailureAsync(package, passed)
        Return passed
    End Function

    Private Async Function WithinAsync(work As Task, label As String) As Task
        If Await Task.WhenAny(work, Task.Delay(5000)) IsNot work Then Throw New TimeoutException(label)
    End Function

    Private Async Function FailureAsync(work As Task) As Task(Of Exception)
        Await WithinAsync(work, "CF identity operation did not finish")
        Try
            Await work
        Catch ex As Exception
            Return ex
        End Try
        Return Nothing
    End Function

    Private Async Function CheckCancelledAsync(work As Task, token As CancellationToken, prefix As String, passed As List(Of String)) As Task
        Dim failure = Await FailureAsync(work)
        Check(TypeOf failure Is OperationCanceledException, prefix & " reports cancellation", passed)
        Check(work.IsCanceled AndAlso Not work.IsFaulted, prefix & " leaves the task cancelled", passed)
        Check(DirectCast(failure, OperationCanceledException).CancellationToken = token,
              prefix & " preserves the original caller token", passed)
    End Function

    Private Async Function PreCancelledAsync(package As CurseForgePackage, passed As List(Of String)) As Task
        Using handler As New HeldHandler With {.Serve = True}, client As New HttpClient(handler),
              resolver As New CurseForgeFileResolver(New SourcePolicy(), client, ""), cancel As New CancellationTokenSource()
            cancel.Cancel()
            Dim work = resolver.ResolveAsync(package, {}, cancel.Token)
            Await CheckCancelledAsync(work, cancel.Token, "pre-cancelled CF batch", passed)
            Check(handler.Started = 0 AndAlso handler.Active = 0, "pre-cancelled CF references never acquire a network request", passed)
        End Using
    End Function

    Private Async Function HeldAsync(package As CurseForgePackage, passed As List(Of String)) As Task
        Using handler As New HeldHandler(), client As New HttpClient(handler),
              resolver As New CurseForgeFileResolver(New SourcePolicy(), client, ""), cancel As New CancellationTokenSource()
            Dim work = resolver.ResolveAsync(package, {}, cancel.Token)
            Await WithinAsync(handler.FourStarted.Task, "CF four active requests did not enter")
            Await handler.FourStarted.Task
            Check(handler.Started = 4 AndAlso handler.Active = 4 AndAlso package.References.Count = 5,
                  "CF batch holds four active requests while its fifth reference queues", passed)
            Dim notification As Exception = Nothing
            Try
                cancel.Cancel()
            Catch ex As Exception
                notification = ex
            End Try
            Check(notification Is Nothing, "CF caller cancellation notification does not throw", passed)
            Await Task.Delay(50)
            Check(Not work.IsCompleted, "CF cancellation waits for held request cleanup before returning", passed)
            Check(handler.Started = 4 AndAlso handler.Notifications = 4,
                  "CF cancellation reaches active requests without starting its queued reference", passed)
            handler.Release.TrySetResult(True)
            Await CheckCancelledAsync(work, cancel.Token, "active and queued CF batch", passed)
            Check(handler.Active = 0 AndAlso handler.Finished = 4 AndAlso handler.Started = 4,
                  "CF cancellation joins every started request before releasing the batch", passed)
            handler.Serve = True
            Dim retryWork = resolver.ResolveAsync(package, {}, CancellationToken.None)
            Await WithinAsync(retryWork, "CF cancellation retry did not finish")
            Dim retry = Await retryWork
            Check(retry.Count = 5, "CF same-resolver retry resolves all five references", passed)
            Check(retry.Select(Function(item) item.Reference.FileId).SequenceEqual(package.References.Select(Function(item) item.FileId)),
                  "CF retry preserves manifest reference order", passed)
            Check(retry.All(Function(item) item.Download.Size = Payload(item.Reference.FileId).Length AndAlso
                                item.Download.Sha1 = Digest(item.Reference.FileId)),
                  "CF retry retains each resolved file size and digest", passed)
            Check(retry.All(Function(item) item.Download.RelativePath = "mods/fixture-" & item.Reference.FileId.ToString() & ".jar"),
                  "CF retry binds resolved filenames to their original reference IDs", passed)
            Check(handler.Started = 9 AndAlso handler.Finished = 9 AndAlso handler.Active = 0,
                  "CF successful retry also joins all request resources", passed)
        End Using
    End Function

    Private Async Function FileFailureAsync(package As CurseForgePackage, passed As List(Of String)) As Task
        Using handler As New HeldHandler With {.Serve = True, .BadFile = 1003}, client As New HttpClient(handler),
              resolver As New CurseForgeFileResolver(New SourcePolicy(), client, "")
            Dim work = resolver.ResolveAsync(package, {}, CancellationToken.None)
            Dim failure = Await FailureAsync(work)
            Check(TypeOf failure Is CurseForgeQueryException, "uncancelled CF file failures remain a query aggregate", passed)
            Check(work.IsFaulted AndAlso Not work.IsCanceled, "uncancelled CF file failure remains faulted", passed)
            Dim query = DirectCast(failure, CurseForgeQueryException)
            Check(query.Failures.Count = 1 AndAlso query.Failures(0).Contains("项目 3 / 文件 1003"),
                  "CF failure aggregate retains the exact failed project and file IDs", passed)
            Check(handler.Requests.Where(Function(id) id = 1003).Count() = 2 AndAlso handler.Started = 6 AndAlso
                  handler.Finished = 6 AndAlso handler.Active = 0,
                  "CF file failure keeps source fallback while joining healthy references", passed)
        End Using
    End Function

    Private Async Function ProgressFailureAsync(package As CurseForgePackage, passed As List(Of String)) As Task
        Dim expected As New InvalidOperationException("fixture CF progress observer failure")
        Dim progress As New FailingProgress(expected)
        Using handler As New HeldHandler With {.Serve = True}, client As New HttpClient(handler),
              resolver As New CurseForgeFileResolver(New SourcePolicy(), client, "")
            Dim work = resolver.ResolveAsync(package, {}, CancellationToken.None, progress)
            Dim failure = Await FailureAsync(work)
            Check(work.IsFaulted AndAlso Not work.IsCanceled, "CF progress observer failure is not reclassified as caller cancellation", passed)
            Check(failure Is expected, "CF progress observer failure preserves its original exception instance", passed)
            Check(progress.Reports >= 1 AndAlso progress.Reports <= handler.Started AndAlso handler.Started <= 4 AndAlso
                  handler.Finished = handler.Started AndAlso handler.Active = 0,
                  "CF progress observer failure retires queued work and joins its active request", passed)
            Dim retryWork = resolver.ResolveAsync(package, {}, CancellationToken.None)
            Await WithinAsync(retryWork, "CF observer retry did not finish")
            Check((Await retryWork).Count = 5 AndAlso handler.Active = 0,
                  "CF same-resolver retry succeeds after a progress observer fault", passed)
        End Using
    End Function

    Private Function Payload(file As Long) As Byte()
        Return Encoding.UTF8.GetBytes("fixture file " & file.ToString())
    End Function

    Private Function Digest(file As Long) As String
        Using sha = SHA1.Create()
            Return BitConverter.ToString(sha.ComputeHash(Payload(file))).Replace("-", "").ToLowerInvariant()
        End Using
    End Function

    Private NotInheritable Class FailingProgress
        Implements IProgress(Of TransferProgress)
        Private ReadOnly _failure As Exception
        Friend Reports As Integer
        Friend Sub New(failure As Exception)
            _failure = failure
        End Sub
        Public Sub Report(value As TransferProgress) Implements IProgress(Of TransferProgress).Report
            Interlocked.Increment(Reports)
            Throw _failure
        End Sub
    End Class

    Private NotInheritable Class HeldHandler
        Inherits HttpMessageHandler
        Friend ReadOnly FourStarted As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Friend ReadOnly Release As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Friend ReadOnly Requests As New ConcurrentQueue(Of Long)
        Friend Started As Integer
        Friend Active As Integer
        Friend Finished As Integer
        Friend Notifications As Integer
        Friend Serve As Boolean
        Friend BadFile As Long
        Protected Overrides Async Function SendAsync(request As HttpRequestMessage, token As CancellationToken) As Task(Of HttpResponseMessage)
            Dim file = Long.Parse(request.RequestUri.Segments.Last())
            Requests.Enqueue(file)
            Dim sequence = Interlocked.Increment(Started)
            Interlocked.Increment(Active)
            Try
                If Not Serve Then
                    If sequence = 4 Then FourStarted.TrySetResult(True)
                    Using registration = token.Register(Sub() Interlocked.Increment(Notifications))
                        ' Ignore cancellation until this fixture releases the resource;
                        ' the resolver must join it rather than only observe its fault.
                        Await Release.Task.ConfigureAwait(False)
                        token.ThrowIfCancellationRequested()
                    End Using
                End If
                If file = BadFile Then Return New HttpResponseMessage(HttpStatusCode.ServiceUnavailable) With {.Content = New StringContent("fixture file unavailable")}
                Dim data As New JObject From {{"data", New JObject From {
                    {"id", file}, {"projectId", file - 1000}, {"fileName", "fixture-" & file.ToString() & ".jar"},
                    {"fileLength", Payload(file).Length}, {"gameVersions", New JArray("1.20.1")}, {"status", 4},
                    {"isCompatibleWithClient", True}, {"hashes", New JArray(New JObject From {{"algo", 1}, {"value", Digest(file)}})}}}}
                Return New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New StringContent(data.ToString())}
            Finally
                Interlocked.Decrement(Active)
                Interlocked.Increment(Finished)
            End Try
        End Function
        Protected Overrides Sub Dispose(disposing As Boolean)
            If disposing Then Release.TrySetResult(True)
            MyBase.Dispose(disposing)
        End Sub
    End Class
End Module
