Imports System.IO
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module MetadataTransferChecks
    Private Const Limit As Integer = 32 * 1024 * 1024

    Friend Async Function RunAsync(Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        If onlyCase = "metadata-transfer-all" Then onlyCase = Nothing
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "metadata-transfer-header" Then Await HeaderAsync(False, passed)
        If onlyCase Is Nothing OrElse onlyCase = "metadata-transfer-status" Then Await HeaderAsync(True, passed)
        If onlyCase Is Nothing OrElse onlyCase = "metadata-transfer-stream" Then Await StreamingAsync(passed)
        If onlyCase Is Nothing OrElse onlyCase = "metadata-transfer-text" Then Await TextAsync(passed)
        If onlyCase Is Nothing OrElse onlyCase = "metadata-transfer-cancel" Then Await CancelAsync(passed)
        If passed.Count = 0 Then Throw New ArgumentException(onlyCase)
        Return passed
    End Function

    Private Sub Check(condition As Boolean, name As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(name)
        passed.Add(name)
    End Sub

    Private Async Function HeaderAsync(badStatus As Boolean, passed As List(Of String)) As Task
        Using server As New TestServer(), body As New ManualResetEventSlim(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(8))
            server.Files("/bad") = Encoding.UTF8.GetBytes("do not wait for this body")
            server.Files("/good") = Encoding.UTF8.GetBytes("{""ok"":true}")
            server.BodyGates("/bad") = body
            If badStatus Then
                server.StatusCodes("/bad") = 503
            Else
                server.DeclaredLengths("/bad") = Limit + 1L
            End If
            Dim result As JObject = Nothing
            Dim failure As Exception = Nothing
            Try
                result = Await New SourcePolicy With {.AllowLoopback = True}.RaceJsonAsync({server.Url & "/bad", server.Url & "/good"}, timeout.Token, fallbackDelay:=60000)
            Catch ex As Exception
                failure = ex
            End Try
            Dim kind = If(badStatus, "HTTP error", "oversized declaration")
            Console.WriteLine("METADATA_HEADER kind=" & kind & " fallback=" & server.Requests.ContainsKey("/good") & " failure=" & If(failure?.GetType().Name, "none"))
            Check(result IsNot Nothing AndAlso CBool(result("ok")) AndAlso Not body.IsSet, kind & " starts a validated fallback without waiting for the preferred response body", passed)
            Check(server.Requests("/bad") = 1 AndAlso server.Requests("/good") = 1, kind & " neither retries the rejected body nor starts duplicate fallback requests", passed)
        End Using
    End Function

    Private Async Function StreamingAsync(passed As List(Of String)) As Task
        Using server As New TestServer(), tail As New ManualResetEventSlim(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(10))
            server.Files("/stream") = New Byte(Limit + 32768 - 1) {}
            server.OmitLengths("/stream") = True
            server.BodyGates("/stream") = tail
            server.BodyPauseAt("/stream") = Limit + 16384
            server.Files("/good") = Encoding.UTF8.GetBytes("{""ok"":true}")
            Dim result As JObject = Nothing
            Try
                result = Await New SourcePolicy With {.AllowLoopback = True}.RaceJsonAsync({server.Url & "/stream", server.Url & "/good"}, timeout.Token, fallbackDelay:=60000)
            Catch ex As OperationCanceledException
            End Try
            Check(result IsNot Nothing AndAlso CBool(result("ok")) AndAlso Not tail.IsSet, "unknown-length metadata exceeding the byte limit falls back before the response finishes", passed)
        End Using
    End Function

    Private Async Function TextAsync(passed As List(Of String)) As Task
        Using server As New TestServer(), timeout As New CancellationTokenSource(TimeSpan.FromSeconds(10))
            Dim policy As New SourcePolicy With {.AllowLoopback = True}
            server.Files("/exact") = Encoding.ASCII.GetBytes(New String("x"c, Limit))
            Dim exact = Await policy.GetTextAsync(server.Url & "/exact", timeout.Token)
            Check(exact.Length = Limit, "metadata exactly at the byte limit remains valid", passed)
            server.Files("/utf16") = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("版本列表 ✓")).ToArray()
            server.ContentTypes("/utf16") = "text/plain; charset=utf-16"
            Dim text = Await policy.GetTextAsync(server.Url & "/utf16", timeout.Token)
            Check(text = "版本列表 ✓", "bounded text decoding preserves charset and BOM behavior", passed)
            server.Files("/unknown") = Encoding.UTF8.GetBytes("{""ok"":true}")
            server.OmitLengths("/unknown") = True
            Dim json = Await policy.RaceJsonAsync({server.Url & "/unknown"}, timeout.Token)
            Check(CBool(json("ok")), "complete metadata without Content-Length still parses normally", passed)
        End Using
    End Function

    Private Async Function CancelAsync(passed As List(Of String)) As Task
        For Each useRace In {False, True}
            Using server As New TestServer(), body As New ManualResetEventSlim(), cancel As New CancellationTokenSource(TimeSpan.FromSeconds(1))
                server.Files("/waiting") = Encoding.UTF8.GetBytes("{""ok"":true}")
                server.BodyGates("/waiting") = body
                Dim policy As New SourcePolicy With {.AllowLoopback = True}
                Dim cancelled = False
                Try
                    If useRace Then
                        Await policy.RaceJsonAsync({server.Url & "/waiting"}, cancel.Token)
                    Else
                        Await policy.GetTextAsync(server.Url & "/waiting", cancel.Token)
                    End If
                Catch ex As OperationCanceledException
                    cancelled = True
                End Try
                Check(cancelled AndAlso Not body.IsSet, "cancelling a stalled metadata body returns cancellation; race=" & useRace, passed)
            End Using
        Next
    End Function
End Module
