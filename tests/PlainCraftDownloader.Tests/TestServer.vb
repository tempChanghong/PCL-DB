Imports System.Collections.Concurrent
Imports System.IO
Imports System.Net
Imports System.Net.Sockets
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading

' Minimal isolated HTTP fixture. Binds only to loopback and needs no HTTP.sys ACL.
Public Class TestServer
    Implements IDisposable
    Private ReadOnly _listener As New TcpListener(IPAddress.Loopback, 0)
    Private ReadOnly _stop As New CancellationTokenSource()
    Public ReadOnly Files As New ConcurrentDictionary(Of String, Byte())
    Public ReadOnly Requests As New ConcurrentDictionary(Of String, Integer)
    Public ReadOnly Gates As New ConcurrentDictionary(Of String, ManualResetEventSlim)
    Public ReadOnly PathDelayMs As New ConcurrentDictionary(Of String, Integer)
    Public Property RangeRequests As Integer
    Public Property IgnoreRanges As Boolean
    Public Property Slow As Boolean
    Public Property Corrupt As Boolean
    Public Property SlowFirstRange As Boolean
    Public Property HeaderDelayMs As Integer
    Public ReadOnly RangeStarts As New ConcurrentQueue(Of Long)
    Public Property RejectRanges As Boolean
    Public Property InvalidRange As String
    Public Property TruncateFirstResponseAt As Integer
    Public ReadOnly Property Url As String
        Get
            Return "http://127.0.0.1:" & DirectCast(_listener.LocalEndpoint, IPEndPoint).Port
        End Get
    End Property
    Public Sub New()
        _listener.Start()
        Task.Run(Async Function()
                     While Not _stop.IsCancellationRequested
                         Try
                             Dim client = Await _listener.AcceptTcpClientAsync()
                             Dim responseTask = Task.Run(Sub() Respond(client))
                         Catch ex As Exception When _stop.IsCancellationRequested
                             Exit While
                         End Try
                     End While
                 End Function)
    End Sub
    Private Sub Respond(client As TcpClient)
        Try
            Using ownedClient = client, stream = client.GetStream(), reader As New StreamReader(stream, Encoding.ASCII, False, 8192, True)
                Dim request = reader.ReadLine()
                If request Is Nothing Then Return
                Dim path = request.Split(" "c)(1)
                Requests.AddOrUpdate(path, 1, Function(key, count) count + 1)
                Dim range As String = Nothing
                Do
                    Dim line = reader.ReadLine()
                    If String.IsNullOrEmpty(line) Then Exit Do
                    If line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase) Then range = line.Substring(6).Trim()
                Loop
                Dim gate As ManualResetEventSlim = Nothing
                If Gates.TryGetValue(path, gate) Then gate.Wait(_stop.Token)
                Dim pathDelay As Integer
                If PathDelayMs.TryGetValue(path, pathDelay) Then Thread.Sleep(pathDelay)
                Dim bytes As Byte() = Nothing
                If Not Files.TryGetValue(path, bytes) Then
                    Dim missing = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found" & vbCrLf & "Content-Length: 0" & vbCrLf & "Connection: close" & vbCrLf & vbCrLf)
                    stream.Write(missing, 0, missing.Length)
                    Return
                End If
                Dim first = 0
                Dim last = bytes.Length - 1
                Dim status = "200 OK"
                Dim extra = ""
                If range IsNot Nothing Then
                    SyncLock Me
                        RangeRequests += 1
                    End SyncLock
                    Dim requested = Regex.Match(range, "bytes=(\d+)-")
                    If requested.Success Then RangeStarts.Enqueue(Long.Parse(requested.Groups(1).Value))
                    If RejectRanges Then
                        Dim rejected = Encoding.ASCII.GetBytes("HTTP/1.1 416 Range Not Satisfiable" & vbCrLf & "Content-Length: 0" & vbCrLf & "Connection: close" & vbCrLf & vbCrLf)
                        stream.Write(rejected, 0, rejected.Length)
                        Return
                    End If
                    If Not IgnoreRanges Then
                        Dim match = Regex.Match(range, "bytes=(\d+)-(\d*)")
                        first = Integer.Parse(match.Groups(1).Value)
                        last = If(match.Groups(2).Length > 0, Integer.Parse(match.Groups(2).Value), last)
                        status = "206 Partial Content"
                        extra = "Content-Range: bytes " & first & "-" & last & "/" & bytes.Length & vbCrLf
                        If InvalidRange = "start" Then extra = "Content-Range: bytes " & Math.Max(0, first - 1) & "-" & last & "/" & bytes.Length & vbCrLf
                        If InvalidRange = "end" Then extra = "Content-Range: bytes " & first & "-" & (last - 1) & "/" & bytes.Length & vbCrLf
                        If InvalidRange = "total" Then extra = "Content-Range: bytes " & first & "-" & last & "/" & (bytes.Length + 1) & vbCrLf
                        If InvalidRange = "missing" Then extra = ""
                        If InvalidRange = "wildcard" Then extra = "Content-Range: bytes */" & bytes.Length & vbCrLf
                        If InvalidRange = "nototal" Then extra = "Content-Range: bytes " & first & "-" & last & "/*" & vbCrLf
                        If InvalidRange = "unit" Then extra = "Content-Range: items " & first & "-" & last & "/" & bytes.Length & vbCrLf
                    End If
                End If
                If HeaderDelayMs > 0 Then Thread.Sleep(HeaderDelayMs)
                Dim contentLength = last - first + 1
                If range IsNot Nothing AndAlso InvalidRange = "length" Then contentLength -= 1
                Dim header = Encoding.ASCII.GetBytes("HTTP/1.1 " & status & vbCrLf & "Content-Length: " & contentLength & vbCrLf & extra & "Connection: close" & vbCrLf & vbCrLf)
                stream.Write(header, 0, header.Length)
                If TruncateFirstResponseAt > 0 AndAlso Requests(path) = 1 Then last = Math.Min(last, first + TruncateFirstResponseAt - 1)
                Dim current = first
                While current <= last
                    Dim count = Math.Min(16384, last - current + 1)
                    If Corrupt Then
                        Dim bad(count - 1) As Byte
                        stream.Write(bad, 0, bad.Length)
                    Else
                        stream.Write(bytes, current, count)
                    End If
                    current += count
                    If Slow Then Thread.Sleep(8)
                    If SlowFirstRange AndAlso first = 0 Then Thread.Sleep(80)
                End While
            End Using
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is SocketException OrElse TypeOf ex Is ObjectDisposedException OrElse TypeOf ex Is OperationCanceledException
            ' Clients deliberately cancel reads and reject unsupported ranges.
        End Try
    End Sub
    Public Sub Dispose() Implements IDisposable.Dispose
        _stop.Cancel()
        _listener.Stop()
        _stop.Dispose()
    End Sub
End Class
