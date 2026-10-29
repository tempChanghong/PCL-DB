Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Reflection
Imports System.Runtime.CompilerServices
Imports System.Security.Cryptography
Imports System.Threading
Imports PclDownloadBridge.Core

Friend Module CancellationCleanupChecks
    Private Sub Check(value As Boolean, name As String, passed As List(Of String))
        If Not value Then Throw New InvalidOperationException(name)
        passed.Add(name)
    End Sub

    ' Timeouts are deadlock guards. Ordering assertions use explicit read/cancel gates.
    Private Async Function EnteredAsync(task As Task, name As String) As Task
        If Await Task.WhenAny(task, Task.Delay(5000)) IsNot task Then Throw New TimeoutException(name)
        Await task
    End Function

    Private Async Function FailureAsync(task As Task) As Task(Of Exception)
        If Await Task.WhenAny(task, Task.Delay(5000)) IsNot task Then Throw New TimeoutException("Cancellation cleanup did not join its workers.")
        Try
            Await task
            Return Nothing
        Catch ex As Exception
            Return ex
        End Try
    End Function

    <MethodImpl(MethodImplOptions.NoInlining)>
    Private Sub ThrowOriginal(failure As Exception)
        Throw failure
    End Sub

    Private Class HeldStream
        Inherits Stream
        Friend ReadOnly Reading As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Friend ReadOnly Disposing As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Friend ReadOnly Release As New TaskCompletionSource(Of Integer)(TaskCreationOptions.RunContinuationsAsynchronously)
        Private ReadOnly _prefix As Boolean
        Private _read As Integer
        Friend Sub New(Optional prefix As Boolean = False)
            _prefix = prefix
        End Sub
        Public Overrides Function ReadAsync(buffer As Byte(), offset As Integer, count As Integer, token As CancellationToken) As Task(Of Integer)
            If _prefix AndAlso Interlocked.Increment(_read) = 1 Then
                Dim length = Math.Min(count, 65536)
                Array.Clear(buffer, offset, length)
                Return Task.FromResult(length)
            End If
            Reading.TrySetResult(True)
            Return Release.Task
        End Function
        Protected Overrides Sub Dispose(disposing As Boolean)
            If disposing Then Me.Disposing.TrySetResult(True)
            MyBase.Dispose(disposing)
        End Sub
        Public Overrides ReadOnly Property CanRead As Boolean = True
        Public Overrides ReadOnly Property CanSeek As Boolean = False
        Public Overrides ReadOnly Property CanWrite As Boolean = False
        Public Overrides ReadOnly Property Length As Long
            Get
                Throw New NotSupportedException()
            End Get
        End Property
        Public Overrides Property Position As Long
            Get
                Throw New NotSupportedException()
            End Get
            Set(value As Long)
                Throw New NotSupportedException()
            End Set
        End Property
        Public Overrides Sub Flush()
        End Sub
        Public Overrides Function Read(buffer As Byte(), offset As Integer, count As Integer) As Integer
            Throw New NotSupportedException()
        End Function
        Public Overrides Function Seek(offset As Long, origin As SeekOrigin) As Long
            Throw New NotSupportedException()
        End Function
        Public Overrides Sub SetLength(value As Long)
            Throw New NotSupportedException()
        End Sub
        Public Overrides Sub Write(buffer As Byte(), offset As Integer, count As Integer)
            Throw New NotSupportedException()
        End Sub
    End Class

    Private Class RangeFixture
        Friend ReadOnly Input As New HeldStream(True)
        Friend ReadOnly Cancelled As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Friend ReadOnly Finished As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Friend ReadOnly Primary As New InvalidDataException("range primary failure")
        Friend ReadOnly Secondary As New IOException("range cancellation callback failure")
        Friend Active As Integer
        Friend ThrowOnCancel As Boolean = True
        Friend FailSend As Boolean

        Friend Async Function SendAsync(url As String, first As Long?, last As Long?, token As CancellationToken, body As Func(Of HttpResponseMessage, Task)) As Task
            Interlocked.Increment(Active)
            Try
                If FailSend AndAlso first.Value > 0 Then ThrowOriginal(Primary)
                Using registration = token.Register(Sub()
                                                        Cancelled.TrySetResult(True)
                                                        If ThrowOnCancel Then Throw Secondary
                                                    End Sub), response As New HttpResponseMessage(HttpStatusCode.PartialContent)
                    response.Content = New StreamContent(Input)
                    response.Content.Headers.ContentRange = New ContentRangeHeaderValue(first.Value, last.Value, 1048576)
                    response.Content.Headers.ContentLength = last.Value - first.Value + 1
                    Await body(response)
                End Using
            Finally
                If Interlocked.Decrement(Active) = 0 Then Finished.TrySetResult(True)
            End Try
        End Function

        Friend Function Grow() As Boolean
            If Not FailSend Then ThrowOriginal(Primary)
            Return False
        End Function
    End Class

    Private Function ExclusiveFiles(folder As String) As Boolean
        For Each file In Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            Using hold As New FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            End Using
        Next
        Return True
    End Function

    Private Async Function RetryRangeAsync(cache As String, destination As String) As Task
        Dim sender As PclSegmentScheduler.SendRange = Async Function(url, first, last, token, body)
                                                         Dim bytes(CInt(last.Value - first.Value)) As Byte
                                                         Using response As New HttpResponseMessage(HttpStatusCode.PartialContent)
                                                             response.Content = New ByteArrayContent(bytes)
                                                             response.Content.Headers.ContentRange = New ContentRangeHeaderValue(first.Value, last.Value, 1048576)
                                                             Await body(response)
                                                         End Using
                                                     End Function
        Dim retry As New PclSegmentScheduler(cache, 1048576, sender, Function(count, token) Task.FromResult(0), Function() False, 2, Sub()
                                                                                                                               End Sub)
        Await retry.RunAsync("https://example.invalid/range", destination, CancellationToken.None)
    End Function

    Private Async Function RangeFailureAsync(root As String, passed As List(Of String), sendFailure As Boolean) As Task
        Dim label = If(sendFailure, "send", "grow"), folder = Path.Combine(root, "range-" & label)
        Directory.CreateDirectory(folder)
        If sendFailure Then File.WriteAllText(Path.Combine(folder, "segments.json"), "{""size"":1048576,""parts"":[{""first"":0,""last"":524287},{""first"":524288,""last"":1048575}]}")
        Dim state As New RangeFixture With {.FailSend = sendFailure}
        Dim scheduler As New PclSegmentScheduler(folder, 1048576, AddressOf state.SendAsync, Function(count, token) Task.FromResult(0), AddressOf state.Grow, 2, Sub()
                                                                                                                                                             End Sub)
        Dim destination = Path.Combine(folder, "same-name.bin"), running = scheduler.RunAsync("https://example.invalid/range", destination, CancellationToken.None)
        Dim activeAtReturn = running.ContinueWith(Function(ignored) Volatile.Read(state.Active), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
        Dim waitingForSender As Boolean
        Try
            Await EnteredAsync(state.Cancelled.Task, "Range cancellation callback did not run: " & label)
            waitingForSender = Not running.IsCompleted
        Finally
            state.Input.Release.TrySetCanceled()
        End Try
        Dim failure = Await FailureAsync(running)
        Await EnteredAsync(state.Finished.Task, "Range sender did not finish: " & label)
        Check(waitingForSender, "range failure joins the held sender after cancellation callback failure: " & label, passed)
        Check(failure Is state.Primary AndAlso failure.StackTrace.Contains(NameOf(ThrowOriginal)), "range failure preserves the original exception instance and throw site: " & label, passed)
        Check(Await activeAtReturn = 0 AndAlso state.Active = 0 AndAlso ExclusiveFiles(folder) AndAlso Not File.Exists(destination), "range failure leaves no active sender, occupied cache or published destination: " & label, passed)
        Await RetryRangeAsync(folder, destination)
        Check(New FileInfo(destination).Length = 1048576 AndAlso File.ReadAllBytes(destination).All(Function(value) value = 0) AndAlso ExclusiveFiles(folder), "range retry reuses the released journal and publishes the same destination: " & label, passed)
    End Function

    Private Async Function UserCancellationAsync(root As String, passed As List(Of String)) As Task
        Dim folder = Path.Combine(root, "range-user-cancel")
        Directory.CreateDirectory(folder)
        Dim state As New RangeFixture With {.ThrowOnCancel = False, .FailSend = True}
        Dim scheduler As New PclSegmentScheduler(folder, 1048576, AddressOf state.SendAsync, Function(count, token) Task.FromResult(0), Function() False, 1, Sub()
                                                                                                                                                           End Sub)
        Using source As New CancellationTokenSource()
            Dim destination = Path.Combine(folder, "same-name.bin"), running = scheduler.RunAsync("https://example.invalid/range", destination, source.Token)
            Dim activeAtReturn = running.ContinueWith(Function(ignored) Volatile.Read(state.Active), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
            Dim waitingForSender As Boolean
            Try
                Await EnteredAsync(state.Input.Reading.Task, "Cancelled range did not enter its held read.")
                source.Cancel()
                Await EnteredAsync(state.Cancelled.Task, "User cancellation did not reach the range sender.")
                waitingForSender = Not running.IsCompleted
            Finally
                state.Input.Release.TrySetCanceled()
            End Try
            Dim failure = TryCast(Await FailureAsync(running), OperationCanceledException)
            Check(waitingForSender AndAlso Await activeAtReturn = 0 AndAlso state.Active = 0, "user range cancellation joins its sender before returning", passed)
            Check(failure IsNot Nothing AndAlso failure.CancellationToken = source.Token AndAlso ExclusiveFiles(folder), "user range cancellation retains the original caller token and releases the cache", passed)
            Await RetryRangeAsync(folder, destination)
            Check(New FileInfo(destination).Length = 1048576, "user-cancelled range can retry the same destination", passed)
        End Using
    End Function

    Private Class FaultContent
        Inherits HttpContent
        Private ReadOnly _input As HeldStream
        Private _disposed As Integer
        Friend Sub New(input As HeldStream)
            _input = input
        End Sub
        Protected Overrides Function CreateContentReadStreamAsync() As Task(Of Stream)
            Return Task.FromResult(Of Stream)(_input)
        End Function
        Protected Overrides Function TryComputeLength(ByRef length As Long) As Boolean
            length = 0
            Return False
        End Function
        Protected Overrides Function SerializeToStreamAsync(stream As Stream, context As TransportContext) As Task
            Throw New NotSupportedException()
        End Function
        Protected Overrides Sub Dispose(disposing As Boolean)
            MyBase.Dispose(disposing)
            If disposing AndAlso Interlocked.Exchange(_disposed, 1) = 0 Then Throw New IOException("peer response disposal failure")
        End Sub
    End Class

    Private Class DownloadHandler
        Inherits HttpMessageHandler
        Friend ReadOnly Input As New HeldStream()
        Friend Success As Boolean
        Protected Overrides Function SendAsync(request As HttpRequestMessage, token As CancellationToken) As Task(Of HttpResponseMessage)
            Dim content As HttpContent = If(Success, DirectCast(New ByteArrayContent(New Byte(31) {}), HttpContent), New FaultContent(Input))
            Return Task.FromResult(New HttpResponseMessage(HttpStatusCode.OK) With {.Content = content})
        End Function
    End Class

    Private Class FailingPolicy
        Inherits SourcePolicy
        Friend ReadOnly Primary As New InvalidDataException("download worker primary failure")
        Friend ReadOnly Peer As HeldStream
        Friend Sub New(peer As HeldStream)
            Me.Peer = peer
        End Sub
        Public Overrides Function Candidates(url As String, category As String) As List(Of String)
            If url.EndsWith("/primary", StringComparison.Ordinal) Then
                If Not Peer.Reading.Task.IsCompleted Then Throw New InvalidOperationException("Peer must enter the network read before the failing worker starts.")
                ThrowOriginal(Primary)
            End If
            Return New List(Of String) From {url}
        End Function
    End Class

    Private Sub ReplaceOwnedClient(engine As DownloadEngine, handler As HttpMessageHandler)
        Dim field = GetType(DownloadEngine).GetField("_client", BindingFlags.Instance Or BindingFlags.NonPublic)
        Dim oldClient = DirectCast(field.GetValue(engine), HttpClient)
        field.SetValue(engine, New HttpClient(handler))
        oldClient.Dispose()
    End Sub

    Private Function PayloadHash() As String
        Using sha = SHA1.Create()
            Return BitConverter.ToString(sha.ComputeHash(New Byte(31) {})).Replace("-", "").ToLowerInvariant()
        End Using
    End Function

    Private Async Function WorkerFailureAsync(root As String, passed As List(Of String)) As Task
        Dim folder = Path.Combine(root, "download-workers"), handler As New DownloadHandler(), policy As New FailingPolicy(handler.Input)
        Dim files = {New DownloadFile With {.RelativePath = "peer.bin", .Url = "https://example.invalid/peer", .Size = 32, .Sha1 = PayloadHash()},
                     New DownloadFile With {.RelativePath = "primary.bin", .Url = "https://example.invalid/primary", .Size = 32, .Sha1 = PayloadHash()}}
        Using budget As New DownloadBudget(2), engine As New DownloadEngine(policy, Nothing, {}, 2, folder, budget:=budget)
            ReplaceOwnedClient(engine, handler)
            Dim running = engine.DownloadAsync(folder, files, CancellationToken.None), waitingForPeer As Boolean
            Dim connectionsAtReturn = running.ContinueWith(Function(ignored) Volatile.Read(budget.ActiveConnections), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
            Try
                Await EnteredAsync(handler.Input.Disposing.Task, "Worker failure did not cancel/dispose the peer response.")
                waitingForPeer = Not running.IsCompleted
            Finally
                handler.Input.Release.TrySetCanceled()
            End Try
            Dim failure = Await FailureAsync(running)
            Check(waitingForPeer AndAlso failure Is policy.Primary AndAlso failure.StackTrace.Contains(NameOf(ThrowOriginal)), "download worker preserves its original failure and throw site when peer cancellation cleanup fails", passed)
            Check(Await connectionsAtReturn = 0 AndAlso budget.ActiveConnections = 0 AndAlso budget.Connections.CurrentCount = 2 AndAlso budget.Hosts.Values.All(Function(slot) slot.CurrentCount = 2) AndAlso budget.Transfers.Values.All(Function(slot) slot.CurrentCount = 1), "failed download batch joins peers and releases host, connection and transfer slots", passed)
            Check(Not File.Exists(Path.Combine(folder, "peer.bin")) AndAlso Not File.Exists(Path.Combine(folder, "primary.bin")) AndAlso ExclusiveFiles(folder), "failed download workers publish nothing and release their cache files", passed)
            Using retry As New DownloadEngine(New SourcePolicy(), Nothing, {}, 2, folder, budget:=budget)
                ReplaceOwnedClient(retry, New DownloadHandler With {.Success = True})
                Await retry.DownloadAsync(folder, files, CancellationToken.None)
            End Using
            Check(files.All(Function(item) SafeFiles.Matches(Path.Combine(folder, item.RelativePath), item)) AndAlso budget.ActiveConnections = 0 AndAlso budget.Connections.CurrentCount = 2, "download batch retries the same file names with the released shared budget", passed)
        End Using
    End Function

    Friend Async Function RunAsync(root As String) As Task(Of List(Of String))
        Dim passed As New List(Of String), scope = Path.Combine(root, "cancellation-cleanup-" & Guid.NewGuid().ToString("N"))
        Await RangeFailureAsync(scope, passed, False)
        Await RangeFailureAsync(scope, passed, True)
        Await UserCancellationAsync(scope, passed)
        Await WorkerFailureAsync(scope, passed)
        Return passed
    End Function
End Module
