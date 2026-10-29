Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module SearchSessionStateChecks
    Private Sub Check(value As Boolean, name As String, passed As List(Of String))
        If Not value Then Throw New InvalidOperationException(name)
        passed.Add(name)
    End Sub

    Private Async Function BoundedAsync(task As Task, label As String) As Task
        If Await Task.WhenAny(task, Task.Delay(5000)) IsNot task Then Throw New TimeoutException(label)
        Await task
    End Function

    Private Class Handler
        Inherits HttpMessageHandler
        Friend Route As Func(Of Uri, CancellationToken, Task(Of HttpResponseMessage))
        Friend ReadOnly Addresses As New List(Of Uri)
        Friend ReadOnly Property Requests As Integer
            Get
                SyncLock Addresses
                    Return Addresses.Count
                End SyncLock
            End Get
        End Property
        Protected Overrides Async Function SendAsync(request As HttpRequestMessage, token As CancellationToken) As Task(Of HttpResponseMessage)
            SyncLock Addresses
                Addresses.Add(request.RequestUri)
            End SyncLock
            Return Await Route.Invoke(request.RequestUri, token)
        End Function
    End Class

    Private Class Preview
        Implements IProgress(Of ModpackSearchPreview)
        Friend Notify As Action(Of ModpackSearchPreview)
        Public Sub Report(value As ModpackSearchPreview) Implements IProgress(Of ModpackSearchPreview).Report
            Notify.Invoke(value)
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
            Dim title = If(cf AndAlso id = 1, "needle", "Result " & id)
            If cf Then
                items.Add(New JObject From {{"id", 1000 + id}, {"classId", 4471}, {"name", title}, {"slug", "fixture-cf-" & id}, {"summary", "fixture"}, {"downloadCount", 2000 - id}, {"dateModified", "2026-10-01T00:00:00Z"}})
            Else
                items.Add(New JObject From {{"project_id", "mr" & id}, {"project_type", "modpack"}, {"title", title}, {"slug", "fixture-mr-" & id}, {"description", "fixture"}, {"icon_url", ""}, {"downloads", 2000 - id}, {"date_modified", "2026-10-01T00:00:00Z"}, {"versions", New JArray("1.20.1")}})
            End If
        Next
        If cf Then Return New JObject From {{"data", items}, {"pagination", New JObject From {{"totalCount", total}}}}
        Return New JObject From {{"hits", items}, {"total_hits", total}}
    End Function
    Private Function Response(json As JObject) As HttpResponseMessage
        Return New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New StringContent(json.ToString(), Encoding.UTF8, "application/json")}
    End Function
    Private Function FailureResponse() As HttpResponseMessage
        Return New HttpResponseMessage(HttpStatusCode.BadRequest) With {.ReasonPhrase = "short platform failure", .Content = New StringContent("{}")}
    End Function

    Private Class Reads
        Friend Failure As Exception
        Friend CurrentGetter As String
        Friend Iterations As Integer
        Friend HighestCount As Integer
        Friend StopReading As Integer
        Friend Readonly Started As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Friend Readonly RoundCommitted As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Friend Readonly SawCommittedRound As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
    End Class

    Private Async Function ReadConcurrentlyAsync(session As ModpackSearchSession, reads As Reads) As Task
        Dim previous = 0
        Try
            reads.Started.TrySetResult(True)
            Await reads.RoundCommitted.Task
            ' Bounded, cooperative read worker. Separate getter calls may legitimately
            ' observe different committed rounds; no cross-call transaction is assumed.
            For iteration = 1 To 100000
                If Volatile.Read(reads.StopReading) <> 0 Then Exit For
                reads.CurrentGetter = "Count"
                Dim count = session.Count
                If count < previous Then Throw New InvalidOperationException("Published result count moved backwards.")
                previous = count : reads.HighestCount = Math.Max(reads.HighestCount, count)
                reads.CurrentGetter = "HasMore" : Dim more = session.HasMore
                reads.CurrentGetter = "HasNext" : Dim nextPage = session.HasNext(0), lastPage = session.HasNext(ModpackSearchSession.MaxPage)
                If session.HasNext(-1) OrElse session.HasNext(ModpackSearchSession.MaxPage + 1) Then Throw New InvalidOperationException("Invalid presentation page became available.")
                reads.CurrentGetter = "ErrorMessage" : Dim message = session.ErrorMessage
                If message Is Nothing Then Throw New InvalidOperationException("Published error text was null.")
                reads.CurrentGetter = "FailedPlatforms"
                Dim failed = session.FailedPlatforms
                If failed.Distinct().Count() <> failed.Length OrElse failed.Any(Function(platform) platform <> ModpackPlatform.CurseForge AndAlso platform <> ModpackPlatform.Modrinth) Then Throw New InvalidOperationException("Invalid published failed-platform identities.")
                If failed.Length > 0 Then
                    failed(0) = ModpackPlatform.All
                    If session.FailedPlatforms.Contains(ModpackPlatform.All) Then Throw New InvalidOperationException("FailedPlatforms exposed a writable shared snapshot array.")
                End If
                reads.Iterations += 1
                If count > 0 Then reads.SawCommittedRound.TrySetResult(True)
                If (iteration And 31) = 0 Then Await Task.Yield()
            Next
        Catch ex As Exception
            reads.Failure = ex
        Finally
            reads.SawCommittedRound.TrySetResult(True)
        End Try
    End Function

    Private Async Function ConcurrentCommitsAsync(passed As List(Of String)) As Task
        Dim iterations = 0, overlapped = 0
        For cycle = 1 To 12
            Using handler As New Handler(), client As New ModpackCatalogClient(handler, "")
                Dim session As New ModpackSearchSession(New ModpackQuery()), reads As New Reads()
                handler.Route = Async Function(uri, token)
                                    Await Task.Yield()
                                    If IsCf(uri) Then Return FailureResponse()
                                    Dim start = Offset(uri)
                                    If start = 1 Then
                                        reads.RoundCommitted.TrySetResult(True)
                                        Await reads.SawCommittedRound.Task
                                    End If
                                    If start = 31 Then Return FailureResponse()
                                    Return Response(Page(False, start + 1, 1, 64))
                                End Function
                Dim reader = Task.Run(Function() ReadConcurrentlyAsync(session, reads))
                Try
                    Await BoundedAsync(reads.Started.Task, "Concurrent state reader did not start.")
                    For fetch = 1 To 16
                        If Not session.HasMore Then Exit For
                        Await BoundedAsync(session.GetPageAsync(0, client, CancellationToken.None), "Source commits did not finish while the public reader was active.")
                    Next
                Finally
                    Volatile.Write(reads.StopReading, 1)
                    reads.RoundCommitted.TrySetResult(True)
                    reads.SawCommittedRound.TrySetResult(True)
                End Try
                Await BoundedAsync(reader, "Bounded state reader did not stop.")
                If reads.Failure IsNot Nothing Then Throw New InvalidOperationException("Concurrent public search-state getter failed: " & reads.CurrentGetter, reads.Failure)
                iterations += reads.Iterations
                If reads.HighestCount > 0 Then overlapped += 1
                Check(session.Count = 31 AndAlso Not session.HasMore AndAlso Not session.HasNext(0) AndAlso Not session.HasNext(ModpackSearchSession.MaxPage), "repeated success/error source commits retain their accepted result and final paging state: " & cycle, passed)
                Check(session.FailedPlatforms.OrderBy(Function(platform) CInt(platform)).SequenceEqual({ModpackPlatform.CurseForge, ModpackPlatform.Modrinth}) AndAlso session.ErrorMessage.Contains("CurseForge") AndAlso session.ErrorMessage.Contains("Modrinth"), "both platform failures publish complete error identities without exposing shared arrays: " & cycle, passed)
            End Using
        Next
        Console.WriteLine("SEARCH_STATE_READS iterations=" & iterations & " overlappingSessions=" & overlapped)
        Check(iterations > 0 AndAlso overlapped = 12, "bounded background readers overlap source commits and safely read Count, HasMore, HasNext, ErrorMessage and FailedPlatforms", passed)
    End Function

    Private Sub ReadInPreview(session As ModpackSearchSession, expected As Integer)
        If session.Count <> expected OrElse Not session.HasMore OrElse Not session.HasNext(0) OrElse session.ErrorMessage <> "" OrElse session.FailedPlatforms.Length <> 0 Then Throw New InvalidOperationException("Preview reader did not see the previously committed round.")
        If session.HasNext(-1) OrElse session.HasNext(ModpackSearchSession.MaxPage + 1) Then Throw New InvalidOperationException("Preview reader accepted an invalid page.")
    End Sub

    Private Async Function WaitGateAsync(gate As Task, token As CancellationToken) As Task
        Dim cancelled As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Using registration = token.Register(Sub() cancelled.TrySetResult(True))
            Await Task.WhenAny(gate, cancelled.Task)
            token.ThrowIfCancellationRequested()
        End Using
    End Function

    Private Function StateText(session As ModpackSearchSession) As String
        Return session.Count & "|" & session.HasMore.ToString() & "|" & session.HasNext(0).ToString() & "|" & session.ErrorMessage & "|" & String.Join(",", session.FailedPlatforms.Select(Function(platform) CInt(platform).ToString(Globalization.CultureInfo.InvariantCulture)))
    End Function

    Private Async Function PreviewAndRetryAsync(passed As List(Of String), cancelRound As Boolean) As Task
        Dim label = If(cancelRound, "cancelled round", "successful round")
        Using handler As New Handler(), client As New ModpackCatalogClient(handler, ""), source As New CancellationTokenSource()
            Dim release As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously), previewSeen As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
            Dim held = False, callbacks = 0, crossThreadReads = 0
            handler.Route = Async Function(uri, token)
                                Await Task.Yield()
                                Dim start = Offset(uri)
                                If Not IsCf(uri) AndAlso start = 20 AndAlso Not held Then
                                    held = True
                                    Await WaitGateAsync(release.Task, token)
                                End If
                                Return Response(Page(IsCf(uri), start + 1, 20, 40))
                            End Function
            Dim originalQuery As New ModpackQuery With {.Text = "needle"}, session As New ModpackSearchSession(originalQuery)
            originalQuery.Text = "changed after construction"
            Check(session.Count = 0 AndAlso session.HasMore AndAlso session.HasNext(ModpackSearchSession.MaxPage) AndAlso session.ErrorMessage = "" AndAlso session.FailedPlatforms.Length = 0 AndAlso session.Query.Text = "needle", "fresh search state and frozen query remain available before source work: " & label, passed)
            Dim preview As New Preview With {.Notify = Sub(value)
                                                          Dim expected = session.Count
                                                          ReadInPreview(session, expected)
                                                          Dim background = Task.Run(Sub() ReadInPreview(session, expected))
                                                          If Not background.Wait(3000) Then Throw New TimeoutException("Cross-thread getter blocked behind the async search gate.")
                                                          background.GetAwaiter().GetResult() : Interlocked.Increment(crossThreadReads) : Interlocked.Increment(callbacks)
                                                          If expected = 40 Then
                                                              If value.Projects.Count <> 20 OrElse Not value.Pending.SequenceEqual({ModpackPlatform.Modrinth}) Then Throw New InvalidOperationException("Second-round preview lost the pending platform or accepted projects.")
                                                              If cancelRound Then source.Cancel()
                                                              previewSeen.TrySetResult(True)
                                                          End If
                                                      End Sub}
            Dim first = Await session.GetPageAsync(0, client, CancellationToken.None, preview)
            Check(first.Count = 40 AndAlso first(0).Name = "needle" AndAlso session.Count = 40 AndAlso session.HasMore AndAlso session.ErrorMessage = "", "first full round preserves relevance sorting and per-platform result identities: " & label, passed)
            Dim accepted = StateText(session), running = session.GetPageAsync(1, client, source.Token, preview)
            Try
                Await BoundedAsync(previewSeen.Task, "Second-round preview did not run.")
                Check(callbacks > 0 AndAlso callbacks = crossThreadReads, "synchronous preview and its background callback can read every public getter without deadlocking: " & label, passed)
                Check(StateText(session) = accepted, "pending source preview exposes only the prior complete source round: " & label, passed)
                If cancelRound Then
                    Dim failure As Exception = Nothing
                    Try
                        Await BoundedAsync(running, "Cancelled source round did not finish.")
                    Catch ex As Exception
                        failure = ex
                    End Try
                    Dim cancelled = TryCast(failure, OperationCanceledException)
                    Check(cancelled IsNot Nothing AndAlso cancelled.CancellationToken = source.Token AndAlso StateText(session) = accepted, "cancelled uncommitted round retains prior published state and the original caller token", passed)
                    running = session.GetPageAsync(1, client, CancellationToken.None)
                Else
                    release.TrySetResult(True)
                End If
                Await BoundedAsync(running, "Accepted source round or its retry did not finish.")
                Dim second = running.Result, projects = first.Concat(second).ToList()
                Check(second.Count = 40 AndAlso session.Count = 80 AndAlso projects.Select(Function(project) project.Key).Distinct().Count() = 80 AndAlso Not session.HasMore AndAlso session.FailedPlatforms.Length = 0 AndAlso session.ErrorMessage = "", "round completion or cancellation retry accepts all distinct projects and publishes final state: " & label, passed)
                Dim requests = handler.Requests, cached = Await session.GetPageAsync(0, client, CancellationToken.None), lastLegal = Await session.GetPageAsync(ModpackSearchSession.MaxPage, client, CancellationToken.None)
                Check(cached.Select(Function(project) project.Key).SequenceEqual(first.Select(Function(project) project.Key)) AndAlso lastLegal.Count = 0 AndAlso handler.Requests = requests AndAlso Not session.HasNext(ModpackSearchSession.MaxPage), "cached results and the exhausted final legal page require no additional source requests: " & label, passed)
                For Each invalid In {-1, ModpackSearchSession.MaxPage + 1}
                    Dim rejected = False
                    Try
                        Await session.GetPageAsync(invalid, client, CancellationToken.None)
                    Catch ex As ArgumentOutOfRangeException
                        rejected = True
                    End Try
                    Check(rejected AndAlso Not session.HasNext(invalid) AndAlso handler.Requests = requests, "out-of-range presentation pages remain unavailable without changing search state: " & label & " / " & invalid, passed)
                Next
                SyncLock handler.Addresses
                    Dim repeats = If(cancelRound, 2, 1)
                    Check(handler.Addresses.Where(Function(uri) Offset(uri) = 0).Count() = 2 AndAlso handler.Addresses.Where(Function(uri) IsCf(uri) AndAlso Offset(uri) = 20).Count() = repeats AndAlso handler.Addresses.Where(Function(uri) Not IsCf(uri) AndAlso Offset(uri) = 20).Count() = repeats, "source offsets advance only for accepted rounds and retry only the uncommitted offsets: " & label, passed)
                End SyncLock
            Finally
                release.TrySetResult(True)
            End Try
        End Using
    End Function

    Friend Async Function RunAsync(root As String) As Task(Of List(Of String))
        If root Is Nothing Then Throw New ArgumentNullException(NameOf(root))
        Dim passed As New List(Of String)
        Await ConcurrentCommitsAsync(passed)
        Await PreviewAndRetryAsync(passed, False)
        Await PreviewAndRetryAsync(passed, True)
        Return passed
    End Function
End Module
