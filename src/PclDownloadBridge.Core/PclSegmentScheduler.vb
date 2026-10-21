Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Threading
Imports Newtonsoft.Json.Linq

' PCL NetFile.TryBeginThread: prefer unfinished work, then split the largest
' remaining piece 60/40, never split below 256 KiB, and avoid too many connecting
' workers. Adds a persistent interval journal so dynamic boundaries survive restart.
Friend Class PclSegmentScheduler
    Friend Delegate Function SendRange(url As String, first As Long?, last As Long?, token As CancellationToken, body As Func(Of HttpResponseMessage, Task)) As Task
    Private ReadOnly _gate As New Object
    Private ReadOnly _parts As New List(Of Piece)
    Private ReadOnly _cache As String
    Private ReadOnly _size As Long
    Private ReadOnly _send As SendRange
    Private ReadOnly _received As Func(Of Integer, CancellationToken, Task)
    Private ReadOnly _grow As Func(Of Boolean)
    Private ReadOnly _limit As Integer
    Private ReadOnly _split As Action
    Private Class Piece
        Public First As Long
        Public Last As Long
        Public Done As Long
        Public Phase As Integer
        Public Work As Task
        Public ReadOnly Property Remaining As Long
            Get
                Return Last - First + 1 - Done
            End Get
        End Property
    End Class
    Friend Sub New(cache As String, size As Long, sender As SendRange, received As Func(Of Integer, CancellationToken, Task), grow As Func(Of Boolean), limit As Integer, split As Action)
        _cache = cache
        _size = size
        _send = sender
        _received = received
        _grow = grow
        _limit = limit
        _split = split
    End Sub
    Private Function PartPath(piece As Piece) As String
        Return SafeFiles.UnderRoot(_cache, "segment-" & piece.First & ".part")
    End Function
    Private Sub Load()
        Dim journal = Path.Combine(_cache, "segments.json")
        Try
            If File.Exists(journal) Then
                Dim saved = JObject.Parse(File.ReadAllText(journal))
                If saved("size") Is Nothing OrElse saved("size").Type <> JTokenType.Integer Then Throw New InvalidDataException("分片总长度无效。")
                If CLng(saved("size")) <> _size Then Throw New InvalidDataException("分片大小已变化。")
                Dim parts = TryCast(saved("parts"), JArray)
                If parts Is Nothing OrElse parts.Count = 0 OrElse parts.Count > 128 Then Throw New InvalidDataException("分片列表无效。")
                For Each part In parts
                    If Not TypeOf part Is JObject OrElse part("first") Is Nothing OrElse part("last") Is Nothing OrElse part("first").Type <> JTokenType.Integer OrElse part("last").Type <> JTokenType.Integer Then Throw New InvalidDataException("分片范围格式无效。")
                    _parts.Add(New Piece With {.First = CLng(part("first")), .Last = CLng(part("last"))})
                Next
            Else
                _parts.Add(New Piece With {.First = 0, .Last = _size - 1})
            End If
            If _parts.Count = 0 OrElse _parts.Count > 128 Then Throw New InvalidDataException("分片数无效。")
            Dim cursor As Long
            For Each piece In _parts.OrderBy(Function(p) p.First)
                If piece.First <> cursor OrElse piece.Last < piece.First OrElse piece.Last >= _size Then Throw New InvalidDataException("分片范围不连续。")
                cursor = piece.Last + 1
                Dim path = PartPath(piece)
                piece.Done = If(File.Exists(path), New FileInfo(path).Length, 0L)
                If piece.Done > piece.Last - piece.First + 1 Then
                    ' An interrupted resize journal can leave an old worker's longer
                    ' tail on disk. It is safe to truncate only this known segment.
                    Using stream As New FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None)
                        stream.SetLength(piece.Last - piece.First + 1)
                    End Using
                    piece.Done = piece.Last - piece.First + 1
                End If
            Next
            If cursor <> _size Then Throw New InvalidDataException("分片未覆盖完整文件。")
        Catch ex As Exception When TypeOf ex Is InvalidDataException OrElse TypeOf ex Is Newtonsoft.Json.JsonException OrElse TypeOf ex Is OverflowException
            Clear(_cache)
            _parts.Clear()
            _parts.Add(New Piece With {.First = 0, .Last = _size - 1})
        End Try
        Save()
    End Sub
    Private Sub Save()
        Dim json As New JObject From {{"size", _size}, {"parts", New JArray(_parts.Select(Function(p) New JObject From {{"first", p.First}, {"last", p.Last}}))}}
        Dim temp = Path.Combine(_cache, "segments.tmp")
        File.WriteAllText(temp, json.ToString())
        SafeFiles.CommitFile(temp, Path.Combine(_cache, "segments.json"))
    End Sub
    Friend Async Function RunAsync(url As String, destination As String, token As CancellationToken) As Task
        Load()
        Using linked = CancellationTokenSource.CreateLinkedTokenSource(token)
            Dim failure As Exception = Nothing
          Try
            Do
                token.ThrowIfCancellationRequested()
                SyncLock _gate
                    Dim broken = _parts.FirstOrDefault(Function(p) p.Work IsNot Nothing AndAlso (p.Work.IsFaulted OrElse p.Work.IsCanceled))
                    If broken IsNot Nothing Then
                        failure = If(broken.Work.IsCanceled, New IOException("分片连接超时或被取消。"), broken.Work.Exception.GetBaseException())
                        Exit Do
                    End If
                    Dim active = _parts.Where(Function(p) p.Work IsNot Nothing AndAlso Not p.Work.IsCompleted).ToList()
                    ' Start persisted pending ranges before allocating new work.
                    For Each pending In _parts.Where(Function(p) p.Work Is Nothing AndAlso p.Remaining > 0).Take(Math.Max(0, _limit - active.Count)).ToList()
                        pending.Work = TransferAsync(url, pending, linked.Token)
                    Next
                    If _parts.All(Function(p) p.Remaining = 0 AndAlso (p.Work Is Nothing OrElse p.Work.IsCompleted)) Then Exit Do
                    active = _parts.Where(Function(p) p.Work IsNot Nothing AndAlso Not p.Work.IsCompleted).ToList()
                    Dim preparing = active.Where(Function(p) p.Phase < 2).Count()
                    Dim downloading = active.Where(Function(p) p.Phase = 2).Count()
                    If active.Count < _limit AndAlso _parts.Count < 128 AndAlso preparing <= downloading AndAlso downloading > 0 AndAlso _grow.Invoke() Then
                        Dim largest = active.OrderByDescending(Function(p) p.Remaining).FirstOrDefault()
                        If largest IsNot Nothing AndAlso largest.Remaining >= 256 * 1024 Then
                            Dim start = largest.Last - CLng(Math.Floor(largest.Remaining * 0.4)) + 1
                            Dim extra As New Piece With {.First = start, .Last = largest.Last}
                            largest.Last = start - 1
                            _parts.Add(extra)
                            Save() ' Persist the boundary before issuing its request.
                            _split.Invoke()
                            extra.Work = TransferAsync(url, extra, linked.Token)
                        End If
                    End If
                End SyncLock
                Await Task.Delay(100, token).ConfigureAwait(False)
            Loop
          Catch ex As Exception
            failure = ex
          End Try
            linked.Cancel()
            Dim workers = _parts.Where(Function(p) p.Work IsNot Nothing).Select(Function(p) p.Work).ToArray()
            Try
                Await Task.WhenAll(workers).ConfigureAwait(False)
            Catch ex As Exception
                If failure Is Nothing Then failure = ex
            End Try
            token.ThrowIfCancellationRequested()
            If failure IsNot Nothing Then Throw failure
        End Using
        Using output As New FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None)
            For Each piece In _parts.OrderBy(Function(p) p.First)
                Using input = File.OpenRead(PartPath(piece))
                    input.CopyTo(output)
                End Using
            Next
        End Using
    End Function
    Private Async Function TransferAsync(url As String, piece As Piece, token As CancellationToken) As Task
        Dim requestFirst = piece.First + piece.Done
        Dim requestLast = piece.Last
        Await _send.Invoke(url, requestFirst, requestLast, token,
            Async Function(response)
                Dim range = response.Content.Headers.ContentRange
                If response.StatusCode <> HttpStatusCode.PartialContent OrElse range Is Nothing OrElse range.From <> requestFirst OrElse range.To <> requestLast OrElse range.Length <> _size Then Throw New RangeUnavailableException()
                piece.Phase = 1
                Using input = Await response.Content.ReadAsStreamAsync().ConfigureAwait(False), output As New FileStream(PartPath(piece), FileMode.Append, FileAccess.Write, FileShare.Read, 65536, True)
                    Dim buffer(65535) As Byte
                    While True
                        Dim remaining As Long
                        SyncLock _gate
                            remaining = piece.Remaining
                        End SyncLock
                        If remaining <= 0 Then Exit While
                        Dim count = Await input.ReadAsync(buffer, 0, CInt(Math.Min(buffer.Length, remaining)), token).ConfigureAwait(False)
                        If count = 0 Then Throw New IOException("分片连接提前结束。")
                        SyncLock _gate
                            Dim usable = CInt(Math.Min(count, piece.Remaining))
                            output.Write(buffer, 0, usable)
                            piece.Done += usable
                            piece.Phase = 2
                        End SyncLock
                        Await _received.Invoke(count, token).ConfigureAwait(False)
                    End While
                End Using
            End Function).ConfigureAwait(False)
    End Function
    Friend Shared Sub Clear(cache As String)
        If Not Directory.Exists(cache) Then Return
        For Each path In Directory.EnumerateFiles(cache, "segment-*.part")
            If Text.RegularExpressions.Regex.IsMatch(IO.Path.GetFileName(path), "\Asegment-\d+\.part\z") Then File.Delete(SafeFiles.UnderRoot(cache, IO.Path.GetFileName(path)))
        Next
        For Each name In {"segments.json", "segments.tmp"}
            Dim path = SafeFiles.UnderRoot(cache, name)
            If File.Exists(path) Then File.Delete(path)
        Next
    End Sub
End Class

Friend Class RangeUnavailableException
    Inherits IOException
    Public Sub New()
        MyBase.New("下载源不支持可靠的 Range 分段，改用单连接。")
    End Sub
End Class
