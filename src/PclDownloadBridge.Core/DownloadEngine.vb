Imports System.Collections.Concurrent
Imports System.Diagnostics
Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Security.Cryptography
Imports System.Text.RegularExpressions
Imports System.Threading

' PCL 5ef81e4 ModNet reference: existing-file checks, bounded file concurrency,
' large-file splitting, no splitting on BMCLAPI, fallback for servers without Range,
' multi-source retries, verify-before-merge/commit. Standalone async implementation.
Public Class DownloadEngine
    Implements IDisposable
    Private ReadOnly _policy As SourcePolicy
    Private ReadOnly _client As HttpClient = SourcePolicy.CreateClient()
    Private ReadOnly _clock As Stopwatch = Stopwatch.StartNew()
    Private ReadOnly _progress As IProgress(Of TransferProgress)
    Private ReadOnly _reuseRoots As List(Of String)
    Private ReadOnly _cacheRoot As String
    Private _componentCache As VerifiedComponentCache
    Private ReadOnly _connectionLimit As Integer
    Private _networkBytes As Long
    Private _completed As Integer
    Private _reused As Integer
    Private _total As Integer
    Private _knownBytes As Long
    Private _verifiedBytes As Long
    Private _retries As Integer
    Private _source As String
    Private _currentFile As String
    Private _lastReport As Long
    Private ReadOnly _reportLock As New Object
    Private ReadOnly _statistics As New PclTransferStatistics()
    Private _splits As Integer
    Private ReadOnly _responseState As New AsyncLocal(Of ResponseState)
    Private ReadOnly _canFallback As New AsyncLocal(Of Boolean)
    Public Property SlowSourceGrace As TimeSpan = TimeSpan.FromSeconds(12)
    Public Property MinimumUsefulSpeed As Long = 16 * 1024
    Private Class ResponseState
        Public Clock As Stopwatch = Stopwatch.StartNew()
        Public Bytes As Long
        Public Deadline As CancellationTokenSource
        Public AllowFallback As Boolean
    End Class
    Private ReadOnly _budget As DownloadBudget
    Private ReadOnly _ownsBudget As Boolean
    Public Property BytesPerSecondLimit As Long
        Get
            Return _budget.BytesPerSecondLimit
        End Get
        Set(value As Long)
            _budget.BytesPerSecondLimit = value
        End Set
    End Property
    Public ReadOnly Property DynamicSplits As Integer
        Get
            Return Threading.Volatile.Read(_splits)
        End Get
    End Property
    Public ReadOnly Property Retries As Integer
        Get
            Return Threading.Volatile.Read(_retries)
        End Get
    End Property
    Public ReadOnly Property NetworkBytes As Long
        Get
            Return Interlocked.Read(_networkBytes)
        End Get
    End Property
    Public ReadOnly Property Reused As Integer
        Get
            Return _reused
        End Get
    End Property

    Public Sub New(policy As SourcePolicy, progress As IProgress(Of TransferProgress), reuseRoots As IEnumerable(Of String), Optional connections As Integer = 16, Optional cacheRoot As String = Nothing, Optional useComponentCache As Boolean = False, Optional budget As DownloadBudget = Nothing)
        _policy = policy
        _progress = progress
        _reuseRoots = reuseRoots.Concat(If(String.IsNullOrWhiteSpace(cacheRoot), Enumerable.Empty(Of String)(), {cacheRoot})).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        _cacheRoot = cacheRoot
        If useComponentCache Then EnableVerifiedInstallCache()
        _ownsBudget = budget Is Nothing
        _budget = If(budget, New DownloadBudget(connections))
        _connectionLimit = _budget.ConnectionLimit
    End Sub

    ' Called between joined batches, never while their workers are running.
    Friend Sub EnableVerifiedInstallCache()
        If String.IsNullOrWhiteSpace(_cacheRoot) Then Throw New ArgumentException("启用安装缓存需要指定游戏根目录。", "cacheRoot")
        If _componentCache Is Nothing Then _componentCache = New VerifiedComponentCache(_cacheRoot, _reuseRoots)
    End Sub

    Public Async Function DownloadAsync(root As String, files As IEnumerable(Of DownloadFile), token As CancellationToken) As Task
        token.ThrowIfCancellationRequested()
        Dim queue As New ConcurrentQueue(Of DownloadFile)(DownloadPlan.Merge(files, root))
        token.ThrowIfCancellationRequested()
        _budget.BeginBatch()
        Try
            _total = queue.Count
            _knownBytes = queue.Sum(Function(f) Math.Max(0L, f.Size))
            _verifiedBytes = 0
            _completed = 0
            Dim workers As New List(Of Task)
            Using linked = CancellationTokenSource.CreateLinkedTokenSource(token)
                For i = 1 To Math.Min(_connectionLimit, Math.Max(1, queue.Count))
                    workers.Add(WorkerAsync(root, queue, linked))
                Next
                Try
                    Await Task.WhenAll(workers).ConfigureAwait(False)
                Catch
                    token.ThrowIfCancellationRequested()
                    ' PCL logs the complete exception group before choosing the
                    ' task's primary failure. Keep all sources and retries here.
                    InstallLog.Failure(_cacheRoot, "下载批次失败", workers.Select(Function(task) CType(task.Exception, Exception)))
                    Dim failure = workers.Where(Function(t) t.Exception IsNot Nothing).SelectMany(Function(t) t.Exception.Flatten().InnerExceptions).FirstOrDefault(Function(e) Not TypeOf e Is OperationCanceledException)
                    If failure IsNot Nothing Then Throw failure
                    Throw
                End Try
            End Using
            token.ThrowIfCancellationRequested()
            Report("文件下载完成", True)
        Finally
            _budget.EndBatch()
        End Try
    End Function

    Private Async Function WorkerAsync(root As String, queue As ConcurrentQueue(Of DownloadFile), linked As CancellationTokenSource) As Task
        Dim item As DownloadFile = Nothing
        While queue.TryDequeue(item)
            Try
                linked.Token.ThrowIfCancellationRequested()
                _currentFile = item.RelativePath
                Await DownloadOneAsync(root, item, linked.Token).ConfigureAwait(False)
                Interlocked.Add(_verifiedBytes, Math.Max(0L, item.Size))
                Interlocked.Increment(_completed)
                Report(item.RelativePath)
            Catch
                linked.Cancel()
                Throw
            End Try
        End While
    End Function

    Private Async Function DownloadOneAsync(root As String, item As DownloadFile, token As CancellationToken) As Task
        Dim cacheKey As String
        Using sha = SHA256.Create()
            cacheKey = BitConverter.ToString(sha.ComputeHash(Text.Encoding.UTF8.GetBytes(item.Url & "|" & item.Sha1 & "|" & item.Size & If(item.Md5 Is Nothing, "", "|" & item.Md5) & If(item.S3SinglePartETag Is Nothing, "", "|s3-etag1:" & item.S3SinglePartETag)))).Replace("-", "").ToLowerInvariant()
        End Using
        ' Asset aliases share a transfer cache even when their final paths differ.
        ' Hold one cache owner through validation/publication, not a network connection slot.
        Dim transfer = _budget.Transfers.GetOrAdd(cacheKey, Function(key) New SemaphoreSlim(1))
        Await transfer.WaitAsync(token).ConfigureAwait(False)
        Try
            Await DownloadOneExclusiveAsync(root, item, cacheKey, token).ConfigureAwait(False)
        Finally
            transfer.Release()
        End Try
    End Function

    Private Async Function DownloadOneExclusiveAsync(root As String, item As DownloadFile, cacheKey As String, token As CancellationToken) As Task
        Dim target = SafeFiles.UnderRoot(root, item.RelativePath)
        If SafeFiles.Matches(target, item) Then
            _componentCache?.Save(target, item, token)
            Interlocked.Increment(_reused)
            Return
        End If
        Directory.CreateDirectory(Path.GetDirectoryName(target))
        If _componentCache IsNot Nothing AndAlso _componentCache.TryRestore(target, item, token) Then
            _componentCache.Save(target, item, token)
            _source = "本地已校验缓存"
            Interlocked.Increment(_reused)
            Return
        End If
        For Each candidate In ReuseCandidates(item)
            token.ThrowIfCancellationRequested()
            If String.Equals(candidate, target, StringComparison.OrdinalIgnoreCase) Then Continue For
            Dim reusable As Boolean
            Try
                reusable = SafeFiles.Matches(candidate, item)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                Continue For
            End Try
            If reusable Then
                Dim copyTemp = target & ".reuse-" & Guid.NewGuid().ToString("N")
                Try
                    Try
                        token.ThrowIfCancellationRequested()
                        File.Copy(candidate, copyTemp)
                        token.ThrowIfCancellationRequested()
                        If Not SafeFiles.Matches(copyTemp, item) Then Throw New IOException("复用文件在复制期间发生变化。")
                        token.ThrowIfCancellationRequested()
                    Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                        ' PCL CheckExistingFiles falls back when an optional copy fails.
                        ' Keep cancellation and the actual target commit outside this filter.
                        Continue For
                    End Try
                    SafeFiles.CommitFile(copyTemp, target)
                    _componentCache?.Save(target, item, token)
                    Interlocked.Increment(_reused)
                    Return
                Finally
                    DeleteReuseCopy(copyTemp)
                End Try
            End If
        Next
        Dim cache = SafeFiles.UnderRoot(If(_cacheRoot, root), ".vanilla-downloader/transfers/" & cacheKey & "/entry")
        cache = Path.GetDirectoryName(cache)
        Directory.CreateDirectory(cache)
        Dim failures As New List(Of Exception)
        Dim sources = _policy.Candidates(item.Url, item.Category)
        For sourceIndex = 0 To sources.Count - 1
            Dim source = sources(sourceIndex)
            _source = New Uri(source).Host
            _canFallback.Value = sourceIndex < sources.Count - 1
            For attempt = 0 To 1
                token.ThrowIfCancellationRequested()
                Dim failure As Exception = Nothing
                Dim merged = SafeFiles.UnderRoot(cache, "complete.tmp")
                Try
                    Dim completeCached = CanResumeSingle(item) AndAlso SafeFiles.Matches(merged, item)
                    ' PCL avoids segmentation on BMCLAPI. Only hashed, sized large
                    ' official/loopback files may combine separately fetched ranges.
                    Dim canSplit = item.Size >= 8L * 1024 * 1024 AndAlso Not String.IsNullOrWhiteSpace(item.Sha1) AndAlso Not New Uri(source).Host.Contains("bmclapi")
                    If completeCached Then
                        _source = "本地已校验续传缓存"
                    ElseIf canSplit Then
                        Dim unsupported = False
                        Try
                            Await SegmentedAsync(source, item, cache, merged, token).ConfigureAwait(False)
                        Catch ex As RangeUnavailableException
                            unsupported = True
                        End Try
                        If unsupported Then Await SingleAsync(source, merged, item, token).ConfigureAwait(False)
                    Else
                        Await SingleAsync(source, merged, item, token).ConfigureAwait(False)
                    End If
                    If Not SafeFiles.Matches(merged, item) Then Throw New InvalidDataException("大小或哈希校验失败：" & item.RelativePath)
                    token.ThrowIfCancellationRequested()
                    SafeFiles.CommitFile(merged, target)
                    _componentCache?.Save(target, item, token)
                    If completeCached Then Interlocked.Increment(_reused)
                    TransferCleanup.DeleteCommitted(If(_cacheRoot, root), cacheKey)
                    Return
                Catch ex As Exception When Not token.IsCancellationRequested
                    Interlocked.Increment(_retries)
                    failure = ex
                    failures.Add(New IOException(source & "：" & ex.Message, ex))
                    ' Bad hashes must not poison future resume attempts.
                    If TypeOf ex Is InvalidDataException Then
                        If File.Exists(merged) Then File.Delete(merged)
                        PclSegmentScheduler.Clear(cache)
                        For part = 0 To 3
                            Dim partPath = Path.Combine(cache, part & ".part")
                            If File.Exists(partPath) Then File.Delete(partPath)
                        Next
                    End If
                End Try
                If TypeOf failure Is SlowSourceException Then Exit For
                If failure IsNot Nothing Then Await Task.Delay(400 * (attempt + 1), token).ConfigureAwait(False)
            Next
        Next
        token.ThrowIfCancellationRequested()
        Throw New AggregateException("下载失败：" & item.RelativePath, failures)
    End Function

    Private Shared Sub DeleteReuseCopy(temporary As String)
        Try
            If File.Exists(temporary) Then File.Delete(temporary)
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            ' An occupied private copy must not replace cancellation or the real error.
        End Try
    End Sub

    Private Function ReuseCandidates(item As DownloadFile) As IEnumerable(Of String)
        ' PCL CheckExistingFile requires an upstream size or hash before searching
        ' other folders. A matching filename alone does not identify their content.
        If item.Size < 0 AndAlso String.IsNullOrWhiteSpace(item.Sha1) AndAlso String.IsNullOrWhiteSpace(item.Md5) Then Return Enumerable.Empty(Of String)()
        Dim candidates As New List(Of String)
        For Each reuseRoot In _reuseRoots
          Try
            candidates.Add(SafeFiles.UnderRoot(reuseRoot, item.RelativePath))
            ' PCL CheckExistingFiles searches other version directories for matching
            ' client JARs. Staged client downloads have only a basename as relative path.
            If item.RelativePath.IndexOfAny({"/"c, "\"c}) < 0 AndAlso item.RelativePath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) Then
                Dim versions = Path.Combine(reuseRoot, "versions")
                If Directory.Exists(versions) Then
                    For Each version In Directory.EnumerateDirectories(versions)
                        Dim name = Path.GetFileName(version)
                        Dim versionRoot = Path.GetDirectoryName(SafeFiles.UnderRoot(reuseRoot, "versions/" & name & "/entry"))
                        ' PCL searches every matching extension in the version folder,
                        ' including clients whose basename differs from the folder.
                        For Each jar In Directory.EnumerateFiles(versionRoot, "*.jar", SearchOption.TopDirectoryOnly)
                            If Not String.Equals(Path.GetExtension(jar), ".jar", StringComparison.OrdinalIgnoreCase) Then Continue For
                            candidates.Add(SafeFiles.UnderRoot(reuseRoot, "versions/" & name & "/" & Path.GetFileName(jar)))
                        Next
                    Next
                End If
            End If
            If item.Category = "asset" AndAlso Not String.IsNullOrEmpty(item.Sha1) Then candidates.Add(SafeFiles.UnderRoot(reuseRoot, "assets/objects/" & item.Sha1.Substring(0, 2) & "/" & item.Sha1))
          Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is ArgumentException
            ' An optional PCL source folder being unavailable must not block downloading.
          End Try
        Next
        Return candidates.Distinct(StringComparer.OrdinalIgnoreCase)
    End Function

    Private Shared Function CanResumeSingle(item As DownloadFile) As Boolean
        If item.Size <= 0 Then Return False
        If Not String.IsNullOrWhiteSpace(item.Sha1) Then Return Regex.IsMatch(item.Sha1, "\A[0-9a-fA-F]{40}\z")
        Return Regex.IsMatch(If(item.Md5, ""), "\A[0-9a-fA-F]{32}\z") OrElse Regex.IsMatch(If(item.S3SinglePartETag, ""), "\A[0-9a-fA-F]{32}-1\z")
    End Function

    Private Async Function SingleAsync(url As String, target As String, item As DownloadFile, token As CancellationToken) As Task
        Dim offset As Long
        If CanResumeSingle(item) AndAlso File.Exists(target) Then
            Dim retained = New FileInfo(target).Length
            If retained > 0 AndAlso retained < item.Size Then offset = retained
        End If
        Dim unsupported = False
        Try
            Await SingleRequestAsync(url, target, item.Size, offset, token).ConfigureAwait(False)
        Catch ex As RangeUnavailableException When offset > 0
            unsupported = True
        End Try
        If unsupported Then Await SingleRequestAsync(url, target, item.Size, 0, token).ConfigureAwait(False)
    End Function

    Private Async Function SingleRequestAsync(url As String, target As String, size As Long, offset As Long, token As CancellationToken) As Task
        Await WithResponseAsync(url, If(offset > 0, New Long?(offset), Nothing), If(offset > 0, New Long?(size - 1), Nothing), token,
            Async Function(response)
                If offset > 0 AndAlso response.StatusCode = HttpStatusCode.RequestedRangeNotSatisfiable Then Throw New RangeUnavailableException()
                response.EnsureSuccessStatusCode()
                Dim start = offset
                If response.StatusCode = HttpStatusCode.PartialContent Then
                    Dim range = response.Content.Headers.ContentRange
                    Dim length = response.Content.Headers.ContentLength
                    If size < 0 OrElse range Is Nothing OrElse Not String.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase) OrElse Not range.From.HasValue OrElse Not range.To.HasValue OrElse Not range.Length.HasValue OrElse range.From.Value <> offset OrElse range.To.Value <> size - 1 OrElse range.Length.Value <> size OrElse (length.HasValue AndAlso length.Value <> size - offset) Then
                        If offset > 0 Then Throw New RangeUnavailableException()
                        Throw New InvalidDataException("服务器返回了无效的文件区间。")
                    End If
                ElseIf response.StatusCode = HttpStatusCode.OK Then
                    ' A server may ignore Range: use its full response, never append it.
                    start = 0
                ElseIf offset > 0 Then
                    Throw New RangeUnavailableException()
                End If
                If size >= 0 AndAlso response.Content.Headers.ContentLength.HasValue AndAlso response.Content.Headers.ContentLength.Value > size - start Then Throw New InvalidDataException("服务器返回的文件超过预期大小。")
                Using input = Await response.Content.ReadAsStreamAsync().ConfigureAwait(False), output As New FileStream(target, If(start > 0, FileMode.Open, FileMode.Create), FileAccess.Write, FileShare.None, 65536, True)
                    If start > 0 Then
                        If output.Length <> start Then Throw New IOException("续传缓存大小在连接期间发生变化。")
                        output.Position = start
                        Report("正在续传文件 · 已保留 " & start & " 字节", True)
                    End If
                    Await CopyAsync(input, output, token, If(size >= 0, size - start, Long.MaxValue)).ConfigureAwait(False)
                    If size >= 0 AndAlso output.Length <> size Then Throw New IOException("文件连接提前结束，保留已接收内容以便续传。")
                End Using
            End Function).ConfigureAwait(False)
    End Function

    Private Async Function SegmentedAsync(url As String, item As DownloadFile, cache As String, target As String, token As CancellationToken) As Task
        Dim scheduler As New PclSegmentScheduler(cache, item.Size, AddressOf WithResponseAsync, AddressOf AccountBytesAsync,
            Function()
                _statistics.Sample(NetworkBytes)
                Return _statistics.WantsMoreConnections AndAlso _budget.ActiveConnections < _connectionLimit
            End Function, Math.Min(16, _connectionLimit), Sub() Interlocked.Increment(_splits))
        Await scheduler.RunAsync(url, target, token).ConfigureAwait(False)
    End Function

    Private Async Function WithResponseAsync(url As String, first As Long?, last As Long?, token As CancellationToken, body As Func(Of HttpResponseMessage, Task)) As Task
        Dim host = _budget.Hosts.GetOrAdd(New Uri(url).Host, Function(key) New SemaphoreSlim(_budget.HostLimit))
        Await host.WaitAsync(token).ConfigureAwait(False)
        Try
            Await _budget.Connections.WaitAsync(token).ConfigureAwait(False)
            Try
                If New Uri(url).Host.IndexOf("bmclapi", StringComparison.OrdinalIgnoreCase) >= 0 Then
                    ' PCL starter threads sleep 100ms after starting BMCLAPI requests.
                    Await _budget.MirrorGate.WaitAsync(token).ConfigureAwait(False)
                    Try
                        Dim delay = 100 - (_budget.Clock.ElapsedMilliseconds - _budget.LastMirrorRequest)
                        If delay > 0 Then Await Task.Delay(CInt(delay), token).ConfigureAwait(False)
                        _budget.LastMirrorRequest = _budget.Clock.ElapsedMilliseconds
                    Finally
                        _budget.MirrorGate.Release()
                    End Try
                End If
                Interlocked.Increment(_budget.ActiveConnections)
                Try
                Using request As New HttpRequestMessage(HttpMethod.Get, url)
                    If first.HasValue Then request.Headers.Range = New RangeHeaderValue(first, last)
                    Using timeout = CancellationTokenSource.CreateLinkedTokenSource(token)
                        timeout.CancelAfter(TimeSpan.FromMinutes(3))
                        Using response = Await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(False)
                            ' Body deadline measures inactivity, not total file length.
                            timeout.CancelAfter(TimeSpan.FromSeconds(45))
                            ' Pass the deadline to reads through cancellation closing the response.
                            Using registration = timeout.Token.Register(Sub() response.Dispose())
                                Dim previous = _responseState.Value
                                _responseState.Value = New ResponseState With {.Deadline = timeout, .AllowFallback = _canFallback.Value}
                                Try
                                    Await body(response).ConfigureAwait(False)
                                Finally
                                    _responseState.Value = previous
                                End Try
                            End Using
                        End Using
                    End Using
                End Using
                Finally
                    Interlocked.Decrement(_budget.ActiveConnections)
                End Try
            Finally
                _budget.Connections.Release()
            End Try
        Finally
            host.Release()
        End Try
    End Function

    Private Async Function CopyAsync(input As Stream, output As Stream, token As CancellationToken, Optional maximum As Long = Long.MaxValue) As Task
        Dim buffer(65535) As Byte
        Dim written As Long = 0
        While True
            Dim count = Await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(False)
            If count = 0 Then Exit While
            written += count
            If written > maximum Then Throw New InvalidDataException("服务器返回的数据超过预期长度。")
            Await output.WriteAsync(buffer, 0, count, token).ConfigureAwait(False)
            Await AccountBytesAsync(count, token).ConfigureAwait(False)
        End While
    End Function

    Private Async Function AccountBytesAsync(count As Integer, token As CancellationToken) As Task
        Interlocked.Add(_networkBytes, count)
        Dim response = _responseState.Value
        If response IsNot Nothing Then
            response.Bytes += count
            If response.AllowFallback AndAlso BytesPerSecondLimit = 0 AndAlso response.Clock.Elapsed >= SlowSourceGrace AndAlso response.Bytes / Math.Max(0.1, response.Clock.Elapsed.TotalSeconds) < MinimumUsefulSpeed Then Throw New SlowSourceException()
            ' A successful read resets inactivity. Our own bandwidth wait is not
            ' network inactivity; suspend its timer until the next read can start.
            response.Deadline.CancelAfter(Timeout.Infinite)
        End If
        Try
            Await WaitForBandwidthAsync(count, token).ConfigureAwait(False)
        Finally
            If response IsNot Nothing AndAlso Not response.Deadline.IsCancellationRequested Then response.Deadline.CancelAfter(TimeSpan.FromSeconds(45))
        End Try
        _statistics.Sample(NetworkBytes)
        Report("正在传输文件")
    End Function

    Private Async Function WaitForBandwidthAsync(count As Integer, token As CancellationToken) As Task
        If BytesPerSecondLimit <= 0 Then Return
        Dim due = _budget.ReserveBytes(count)
        Do
            token.ThrowIfCancellationRequested()
            Dim wait = (due - _budget.Clock.Elapsed.TotalSeconds) * 1000
            If wait <= 0 Then Exit Do
            _statistics.Sample(NetworkBytes)
            Report("正在按速度上限传输文件")
            Await Task.Delay(CInt(Math.Ceiling(Math.Min(wait, 100))), token).ConfigureAwait(False)
        Loop
    End Function

    Private Class SlowSourceException
        Inherits IOException
        Public Sub New()
            MyBase.New("下载源持续低速，切换到备选来源。")
        End Sub
    End Class

    Private Sub Report(detail As String, Optional force As Boolean = False)
        If _progress Is Nothing Then Return
        SyncLock _reportLock
            If Not force AndAlso _clock.ElapsedMilliseconds - _lastReport < 120 Then Return
            _lastReport = _clock.ElapsedMilliseconds
            _progress.Report(New TransferProgress With {.Stage = "下载并校验", .Detail = detail, .CurrentFile = _currentFile, .Source = _source, .Retries = _retries, .KnownBytes = _knownBytes, .VerifiedBytes = Interlocked.Read(_verifiedBytes), .Completed = _completed, .Total = _total, .NetworkBytes = NetworkBytes, .BytesPerSecond = _statistics.Speed, .Reused = _reused, .ActiveConnections = _budget.ActiveConnections, .DynamicSplits = _splits, .Fraction = If(_total = 0, 0, _completed / CDbl(_total))})
        End SyncLock
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        _client.Dispose()
        If _ownsBudget Then _budget.Dispose()
    End Sub
End Class
