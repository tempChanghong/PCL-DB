Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Runtime.CompilerServices
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module SearchSessionCleanupChecks
    Private Sub Check(value As Boolean, name As String, passed As List(Of String))
        If Not value Then Throw New InvalidOperationException(name)
        passed.Add(name)
    End Sub

    ' Gates establish ordering. This delay only prevents a broken regression hanging CI.
    Private Async Function BoundedAsync(task As Task, name As String) As Task
        If Await Task.WhenAny(task, Task.Delay(5000)) IsNot task Then Throw New TimeoutException(name)
        Await task
    End Function

    Private Async Function FailureAsync(task As Task) As Task(Of Exception)
        If Await Task.WhenAny(task, Task.Delay(5000)) IsNot task Then Throw New TimeoutException("Search cleanup did not finish.")
        Try
            Await task
            Return Nothing
        Catch ex As Exception
            Return ex
        End Try
    End Function

    <MethodImpl(MethodImplOptions.NoInlining)>
    Private Sub ThrowPreview(failure As Exception)
        Throw failure
    End Sub

    Private Class Preview
        Implements IProgress(Of ModpackSearchPreview)
        Friend Property Notify As Action(Of ModpackSearchPreview)
        Public Sub Report(value As ModpackSearchPreview) Implements IProgress(Of ModpackSearchPreview).Report
            Notify.Invoke(value)
        End Sub
    End Class

    ' A net48-style read may finish its close asynchronously. Keep a private owned
    ' file until that read settles so premature ownership release is observable.
    Private Class OwnedRead
        Inherits Stream
        Friend ReadOnly Closing As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Friend ReadOnly Closed As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Friend ReadOnly ReadStarted As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Friend ReadOnly ReleaseRead As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Friend ReadToken As CancellationToken
        Friend ReadOnly OwnedPath As String
        Private ReadOnly _file As FileStream
        Private ReadOnly _bytes As Byte()
        Private _position As Integer
        Private _disposing As Integer
        Private _finished As Integer
        Friend Sub New(path As String, json As JObject)
            OwnedPath = path : _bytes = Encoding.UTF8.GetBytes(json.ToString())
            Directory.CreateDirectory(IO.Path.GetDirectoryName(path))
            _file = New FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)
        End Sub
        Public Overrides Async Function ReadAsync(buffer As Byte(), offset As Integer, count As Integer, token As CancellationToken) As Task(Of Integer)
            ReadToken = token : ReadStarted.TrySetResult(True)
            Try
                Await ReleaseRead.Task
                Dim length = Math.Min(count, _bytes.Length - _position)
                Array.Copy(_bytes, _position, buffer, offset, length) : _position += length
                Return length
            Finally
                If Volatile.Read(_disposing) <> 0 Then Finish()
            End Try
        End Function
        Private Sub Finish()
            If Interlocked.Exchange(_finished, 1) = 0 Then
                _file.Dispose() : Closed.TrySetResult(True)
            End If
        End Sub
        Protected Overrides Sub Dispose(disposing As Boolean)
            If disposing Then
                Interlocked.Exchange(_disposing, 1) : Closing.TrySetResult(True)
                If ReleaseRead.Task.IsCompleted Then Finish()
            End If
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

    Private Class CancelAtEof
        Inherits MemoryStream
        Private ReadOnly _source As CancellationTokenSource
        Private _cancelled As Boolean
        Friend Sub New(json As JObject, source As CancellationTokenSource)
            MyBase.New(Encoding.UTF8.GetBytes(json.ToString()))
            _source = source
        End Sub
        Public Overrides Function ReadAsync(buffer As Byte(), offset As Integer, count As Integer, token As CancellationToken) As Task(Of Integer)
            Dim length = MyBase.Read(buffer, offset, count)
            If length = 0 AndAlso Not _cancelled Then
                _cancelled = True : _source.Cancel()
            End If
            Return Task.FromResult(length)
        End Function
    End Class

    Private Class Handler
        Inherits HttpMessageHandler
        Friend Property Route As Func(Of Uri, HttpResponseMessage)
        Friend ReadOnly Addresses As New List(Of Uri)
        Friend ReadOnly Owned As New List(Of OwnedRead)
        Friend ReadOnly Property RequestCount As Integer
            Get
                SyncLock Addresses
                    Return Addresses.Count
                End SyncLock
            End Get
        End Property
        Protected Overrides Function SendAsync(request As HttpRequestMessage, token As CancellationToken) As Task(Of HttpResponseMessage)
            SyncLock Addresses
                Addresses.Add(request.RequestUri)
            End SyncLock
            Return Task.FromResult(Route.Invoke(request.RequestUri))
        End Function
        Friend Function Hold(folder As String, json As JObject) As HttpResponseMessage
            Dim input As New OwnedRead(Path.Combine(folder, "owned-" & Guid.NewGuid().ToString("N") & ".lock"), json)
            SyncLock Owned
                Owned.Add(input)
            End SyncLock
            Return New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New StreamContent(input)}
        End Function
        Friend Sub ReleaseAll()
            SyncLock Owned
                For Each held In Owned : held.ReleaseRead.TrySetResult(True) : Next
            End SyncLock
        End Sub
    End Class

    Private Function IsCf(uri As Uri) As Boolean
        Return uri.AbsolutePath.Contains("/mods/search")
    End Function
    Private Function Offset(uri As Uri) As Integer
        Return Integer.Parse(Regex.Match(uri.Query, "(?:\?|&)(?:index|offset)=(\d+)").Groups(1).Value, Globalization.CultureInfo.InvariantCulture)
    End Function
    Private Function Page(cf As Boolean, first As Integer, count As Integer, total As Integer) As JObject
        Dim items As New JArray()
        For id = first To first + count - 1
            If cf Then
                items.Add(New JObject From {{"id", 1000 + id}, {"classId", 4471}, {"name", "Fixture CF " & id}, {"slug", "fixture-cf-" & id}, {"summary", "fixture"}, {"downloadCount", 1000}, {"dateModified", "2026-10-01T00:00:00Z"}})
            Else
                items.Add(New JObject From {{"project_id", "mr" & id}, {"project_type", "modpack"}, {"title", "Fixture MR " & id}, {"slug", "fixture-mr-" & id}, {"description", "fixture"}, {"icon_url", ""}, {"downloads", 1000}, {"date_modified", "2026-10-01T00:00:00Z"}, {"versions", New JArray("1.20.1")}})
            End If
        Next
        If cf Then Return New JObject From {{"data", items}, {"pagination", New JObject From {{"totalCount", total}}}}
        Return New JObject From {{"hits", items}, {"total_hits", total}}
    End Function
    Private Function Response(json As JObject) As HttpResponseMessage
        Return New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New StringContent(json.ToString(), Encoding.UTF8, "application/json")}
    End Function
    Private Function Locked(path As String) As Boolean
        Try
            Using hold = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            End Using
            Return False
        Catch ex As IOException
            Return True
        End Try
    End Function
    Private Function UnchangedAbortState(session As ModpackSearchSession, expected As Integer) As Boolean
        Return session.Count = expected AndAlso session.HasMore AndAlso session.ErrorMessage = "" AndAlso session.FailedPlatforms.Length = 0
    End Function

    Private Async Function AbortedPreviewAsync(root As String, passed As List(Of String), callerCancel As Boolean, Optional withProgress As Boolean = True) As Task
        Dim label = If(callerCancel, "caller cancellation", "preview exception") & If(withProgress, "", " without progress"), primary As New InvalidOperationException("preview observer failed")
        Using source As New CancellationTokenSource(), handler As New Handler(), client As New ModpackCatalogClient(handler, "")
            Dim cfCalls = 0, mrCalls = 0, retryAfterClose = False
            handler.Route = Function(uri)
                                If IsCf(uri) Then
                                    cfCalls += 1
                                    If cfCalls = 1 Then Return handler.Hold(root, Page(True, 1, 1, 1))
                                    retryAfterClose = handler.Owned(0).Closed.Task.IsCompleted
                                Else
                                    mrCalls += 1
                                    If callerCancel AndAlso withProgress AndAlso mrCalls = 1 Then Return New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New StreamContent(New CancelAtEof(Page(False, 1, 1, 1), source))}
                                End If
                                Return Response(Page(IsCf(uri), 1, 1, 1))
                            End Function
            Dim session As New ModpackSearchSession(New ModpackQuery()), preview As New Preview With {.Notify = Sub(value)
                                                                                                                   If Not callerCancel Then ThrowPreview(primary)
                                                                                                               End Sub}
            Dim running = session.GetPageAsync(0, client, source.Token, If(withProgress, preview, Nothing)), pending = handler.Owned.Single()
            Dim closedAtReturn = running.ContinueWith(Function(ignored) pending.Closed.Task.IsCompleted, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
            Try
                If callerCancel AndAlso Not withProgress Then
                    Await BoundedAsync(pending.ReadStarted.Task, "Search without progress did not begin its owned read.")
                    source.Cancel()
                End If
                Await BoundedAsync(pending.Closing.Task, "Aborted search did not cancel the pending response: " & label)
                Dim cleanState = UnchangedAbortState(session, 0), waiting = Not running.IsCompleted, requests = handler.RequestCount
                Dim retry = session.GetPageAsync(0, client, CancellationToken.None)
                Dim retryBlocked = Not retry.IsCompleted AndAlso handler.RequestCount = requests
                Check(pending.ReadToken.IsCancellationRequested AndAlso Locked(pending.OwnedPath) AndAlso waiting, "search abort cancels pending reads and waits for their owned cleanup: " & label, passed)
                Check(cleanState AndAlso retryBlocked, "search abort retains cursor state and holds the session gate against immediate retry: " & label, passed)
                pending.ReleaseRead.TrySetResult(True)
                Dim failure = Await FailureAsync(running)
                If callerCancel Then
                    Dim cancelled = TryCast(failure, OperationCanceledException)
                    Check(cancelled IsNot Nothing AndAlso cancelled.CancellationToken = source.Token, "search cancellation keeps the original caller token after joining sources: " & label, passed)
                Else
                    Check(failure Is primary AndAlso failure.StackTrace.Contains(NameOf(ThrowPreview)), "search preview failure retains its original exception instance and throw site", passed)
                End If
                Await BoundedAsync(retry, "Immediate retry did not resume after cleanup: " & label)
                Check(Await closedAtReturn AndAlso retryAfterClose AndAlso Not Locked(pending.OwnedPath), "search returns and starts its queued retry only after old response ownership ends: " & label, passed)
                Check(retry.Result.Count = 2 AndAlso retry.Result.Select(Function(project) project.Key).Distinct().Count() = 2 AndAlso session.Count = 2 AndAlso Not session.HasMore AndAlso session.ErrorMessage = "", "same-session search retry retains both platform projects without loss or duplication: " & label, passed)
                requests = handler.RequestCount
                Dim cached = Await session.GetPageAsync(0, client, CancellationToken.None)
                Check(cached.Count = 2 AndAlso handler.RequestCount = requests AndAlso cfCalls = 2 AndAlso mrCalls = 2, "completed search retry reuses its page without duplicate HTTP requests: " & label, passed)
            Finally
                handler.ReleaseAll()
            End Try
        End Using
    End Function

    Private Async Function PartialFailureAsync(root As String, passed As List(Of String)) As Task
        Using handler As New Handler(), client As New ModpackCatalogClient(handler, "")
            handler.Route = Function(uri) If(IsCf(uri), New HttpResponseMessage(HttpStatusCode.BadRequest) With {.Content = New StringContent("{}")}, handler.Hold(root, Page(False, 1, 1, 1)))
            Dim signalled As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
            Dim preview As New Preview With {.Notify = Sub(value) signalled.TrySetResult(True)}
            Dim session As New ModpackSearchSession(New ModpackQuery()), running = session.GetPageAsync(0, client, CancellationToken.None, preview), pending = handler.Owned.Single()
            Try
                Await BoundedAsync(signalled.Task, "Partial failure did not publish its healthy-source loading preview.")
                Check(Not running.IsCompleted AndAlso Not pending.ReadToken.IsCancellationRequested AndAlso Not pending.Closing.Task.IsCompleted, "ordinary platform failure leaves the healthy source running and retains progressive loading", passed)
                pending.ReleaseRead.TrySetResult(True)
                Await BoundedAsync(running, "Healthy source did not finish after another platform failed.")
                Check(running.Result.Count = 1 AndAlso running.Result(0).Platform = ModpackPlatform.Modrinth AndAlso session.Count = 1 AndAlso Not session.HasMore, "ordinary platform failure still commits the healthy project page", passed)
                Check(session.FailedPlatforms.SequenceEqual({ModpackPlatform.CurseForge}) AndAlso session.ErrorMessage.Contains("CurseForge") AndAlso pending.Closed.Task.IsCompleted AndAlso Not Locked(pending.OwnedPath), "partial search failure records only the failed platform and closes healthy response ownership", passed)
                Dim requests = handler.RequestCount, cached = Await session.GetPageAsync(0, client, CancellationToken.None)
                Check(cached.Count = 1 AndAlso handler.RequestCount = requests, "partial failure result remains cached without silently retrying its failed platform", passed)
            Finally
                handler.ReleaseAll()
            End Try
        End Using
    End Function

    Private Async Function RoundRollbackAsync(root As String, passed As List(Of String)) As Task
        Dim primary As New InvalidOperationException("second-round preview failed")
        Using handler As New Handler(), client As New ModpackCatalogClient(handler, "")
            Dim heldRound = False, retryAfterClose = False
            handler.Route = Function(uri)
                                Dim start = Offset(uri)
                                If IsCf(uri) AndAlso start = 20 Then
                                    If Not heldRound Then
                                        heldRound = True : Return handler.Hold(root, Page(True, 21, 20, 40))
                                    End If
                                    retryAfterClose = handler.Owned(0).Closed.Task.IsCompleted
                                End If
                                Return Response(Page(IsCf(uri), start + 1, 20, 40))
                            End Function
            Dim session As New ModpackSearchSession(New ModpackQuery()), preview As New Preview With {.Notify = Sub(value)
                                                                                                                   If session.Count = 40 Then ThrowPreview(primary)
                                                                                                               End Sub}
            Dim running = session.GetPageAsync(1, client, CancellationToken.None, preview), pending = handler.Owned.Single()
            Try
                Await BoundedAsync(pending.Closing.Task, "Failed second round did not cancel its pending source.")
                Check(UnchangedAbortState(session, 40) AndAlso Not running.IsCompleted, "aborted search preserves the completed first round while withholding its partial second round", passed)
                Dim requests = handler.RequestCount, retry = session.GetPageAsync(1, client, CancellationToken.None)
                Check(Not retry.IsCompleted AndAlso handler.RequestCount = requests, "second-round retry waits for prior request cleanup before issuing HTTP", passed)
                pending.ReleaseRead.TrySetResult(True)
                Dim failure = Await FailureAsync(running)
                Check(failure Is primary AndAlso failure.StackTrace.Contains(NameOf(ThrowPreview)), "second-round cleanup preserves the preview exception and original stack", passed)
                Await BoundedAsync(retry, "Second-round retry did not finish.")
                Dim page0 = Await session.GetPageAsync(0, client, CancellationToken.None), page1 = Await session.GetPageAsync(1, client, CancellationToken.None)
                Dim projects = page0.Concat(page1).ToList()
                Check(retryAfterClose AndAlso pending.Closed.Task.IsCompleted AndAlso Not Locked(pending.OwnedPath), "second-round retry starts after the old source releases ownership", passed)
                Check(page0.Count = 40 AndAlso page1.Count = 40 AndAlso session.Count = 80 AndAlso projects.Select(Function(project) project.Key).Distinct().Count() = 80, "round rollback and retry retain all eighty distinct projects without dropping or duplicating either round", passed)
                Check(Not session.HasMore AndAlso session.FailedPlatforms.Length = 0 AndAlso session.ErrorMessage = "", "successful round retry leaves final pagination and platform error state consistent", passed)
                SyncLock handler.Addresses
                    Check(handler.Addresses.Count = 6 AndAlso handler.Addresses.Where(Function(uri) IsCf(uri) AndAlso Offset(uri) = 0).Count() = 1 AndAlso handler.Addresses.Where(Function(uri) Not IsCf(uri) AndAlso Offset(uri) = 0).Count() = 1 AndAlso handler.Addresses.Where(Function(uri) IsCf(uri) AndAlso Offset(uri) = 20).Count() = 2 AndAlso handler.Addresses.Where(Function(uri) Not IsCf(uri) AndAlso Offset(uri) = 20).Count() = 2, "retry refetches only the uncommitted source offsets and preserves completed-round cursors", passed)
                End SyncLock
            Finally
                handler.ReleaseAll()
            End Try
        End Using
    End Function

    Friend Async Function RunAsync(root As String) As Task(Of List(Of String))
        Dim passed As New List(Of String), scope = Path.Combine(root, "search-cleanup-" & Guid.NewGuid().ToString("N"))
        Await AbortedPreviewAsync(Path.Combine(scope, "preview"), passed, False)
        Await AbortedPreviewAsync(Path.Combine(scope, "cancel"), passed, True)
        Await AbortedPreviewAsync(Path.Combine(scope, "cancel-without-progress"), passed, True, False)
        Await PartialFailureAsync(Path.Combine(scope, "partial"), passed)
        Await RoundRollbackAsync(Path.Combine(scope, "rounds"), passed)
        Return passed
    End Function
End Module
