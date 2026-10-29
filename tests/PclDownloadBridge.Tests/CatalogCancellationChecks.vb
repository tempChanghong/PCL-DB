Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

' Exercise the public catalogue API over a real stalled response and a stream whose
' cancellation cleanup fails after closing it. Neither fixture contacts a platform.
Friend Module CatalogCancellationChecks
    Private Const ProjectId As String = "cancelcatalog"

    Private Sub Check(value As Boolean, label As String, passed As List(Of String))
        If Not value Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub

    Friend Async Function RunAsync(root As String) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        For Each platform In {ModpackPlatform.Modrinth, ModpackPlatform.CurseForge}
            Await RealBodyAsync(platform, passed)
            Await ThrowingDisposeAsync(platform, passed)
            Await UncancelledFailureAsync(platform, False, passed)
            Await UncancelledFailureAsync(platform, True, passed)
        Next
        Return passed
    End Function

    Private Function Id(platform As ModpackPlatform) As String
        Return If(platform = ModpackPlatform.Modrinth, ProjectId, "453763")
    End Function

    Private Function ResponseData(platform As ModpackPlatform) As JObject
        Return If(platform = ModpackPlatform.Modrinth, ModpackCatalogChecks.MrProject(ProjectId),
                  New JObject From {{"data", ModpackCatalogChecks.CfProject(453763)}})
    End Function

    Private Async Function WithinAsync(work As Task, label As String) As Task
        If Await Task.WhenAny(work, Task.Delay(5000)) IsNot work Then Throw New InvalidOperationException(label & " did not finish within five seconds")
    End Function

    Private Async Function VerifyCancelledAsync(work As Task, token As CancellationToken, prefix As String, passed As List(Of String)) As Task
        Await WithinAsync(work, prefix)
        Dim failure As Exception = Nothing
        Try
            Await work
        Catch ex As Exception
            failure = ex
        End Try
        Check(TypeOf failure Is OperationCanceledException, prefix & " returns cancellation instead of a transport failure", passed)
        Check(work.IsCanceled AndAlso Not work.IsFaulted, prefix & " leaves the catalogue task cancelled", passed)
        Check(DirectCast(failure, OperationCanceledException).CancellationToken = token, prefix & " preserves the caller token", passed)
    End Function

    Private Async Function RealBodyAsync(platform As ModpackPlatform, passed As List(Of String)) As Task
        Using server As New TestServer(), body As New ManualResetEventSlim(), cancel As New CancellationTokenSource(),
              handler As New LoopbackHandler(server.Url & "/project"), client As New ModpackCatalogClient(handler, "fixture")
            server.Files("/project") = Encoding.UTF8.GetBytes(ResponseData(platform).ToString())
            server.BodyGates("/project") = body
            Dim work = client.GetProjectAsync(platform, Id(platform), cancel.Token)
            Await WithinAsync(handler.Headers.Task, "real catalogue headers")
            Await handler.Headers.Task
            ' The server has sent headers and deliberately withholds the entire body.
            ' Allow the net48 body read to settle before cancelling its response lease.
            Await Task.Delay(50)
            Check(server.Requests.ContainsKey("/project") AndAlso Not body.IsSet AndAlso Not work.IsCompleted,
                  platform.ToString() & " cancellation reaches a stalled real HTTP body", passed)
            Dim notification As Exception = Nothing
            Try
                cancel.Cancel()
            Catch ex As Exception
                notification = ex
            End Try
            Check(notification Is Nothing, platform.ToString() & " real cancellation notification does not throw", passed)
            Await VerifyCancelledAsync(work, cancel.Token, platform.ToString() & " real body", passed)
            body.Set()
            Dim retry = client.GetProjectAsync(platform, Id(platform), CancellationToken.None)
            Await WithinAsync(retry, "real catalogue retry")
            Dim project = Await retry
            Check(project.Id = Id(platform) AndAlso server.Requests("/project") = 2,
                  platform.ToString() & " retries immediately after real body cancellation", passed)
        End Using
    End Function

    Private Async Function ThrowingDisposeAsync(platform As ModpackPlatform, passed As List(Of String)) As Task
        Using handler As New ThrowingHandler(platform), client As New ModpackCatalogClient(handler, "fixture"), cancel As New CancellationTokenSource()
            Dim work = client.GetProjectAsync(platform, Id(platform), cancel.Token)
            Await WithinAsync(handler.Content.Input.Started.Task, "throwing catalogue stream")
            Await handler.Content.Input.Started.Task
            Dim notification As Exception = Nothing
            Try
                cancel.Cancel()
            Catch ex As Exception
                notification = ex
            End Try
            Check(notification Is Nothing AndAlso handler.Content.Input.Closed,
                  platform.ToString() & " cancellation closes a failing response without throwing from Cancel", passed)
            Await VerifyCancelledAsync(work, cancel.Token, platform.ToString() & " failing cleanup", passed)
            Dim retry = client.GetProjectAsync(platform, Id(platform), CancellationToken.None)
            Await WithinAsync(retry, "throwing catalogue retry")
            Dim project = Await retry
            Check(project.Id = Id(platform) AndAlso handler.Attempts = 2,
                  platform.ToString() & " retries immediately after response disposal throws", passed)
        End Using
    End Function

    Private Async Function UncancelledFailureAsync(platform As ModpackPlatform, json As Boolean, passed As List(Of String)) As Task
        Using handler As New FailureHandler(json), client As New ModpackCatalogClient(handler, "fixture"), cancel As New CancellationTokenSource()
            Dim work = client.GetProjectAsync(platform, Id(platform), cancel.Token)
            Await WithinAsync(work, "uncancelled catalogue failure")
            Dim failure As Exception = Nothing
            Try
                Await work
            Catch ex As Exception
                failure = ex
            End Try
            Dim label = platform.ToString() & If(json, " invalid JSON", " read/disposal IO failure")
            Check(TypeOf failure Is IOException AndAlso work.IsFaulted AndAlso Not work.IsCanceled AndAlso Not cancel.IsCancellationRequested,
                  label & " remains a fault when the caller has not cancelled", passed)
            Dim official = If(platform = ModpackPlatform.Modrinth, "api.modrinth.com", "api.curseforge.com")
            Check(handler.Addresses.Where(Function(host) host = official).Count() = 1 AndAlso
                  handler.Addresses.Where(Function(host) host = "mod.mcimirror.top").Count() = If(json, 1, 2),
                  label & " preserves source fallback and bounded transport retries", passed)
        End Using
    End Function

    Private NotInheritable Class LoopbackHandler
        Inherits HttpMessageHandler
        Private ReadOnly _address As String
        Private ReadOnly _client As New HttpClient(New HttpClientHandler With {.AllowAutoRedirect = False}) With {.Timeout = Timeout.InfiniteTimeSpan}
        Friend ReadOnly Headers As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Friend Sub New(address As String)
            _address = address
        End Sub
        Protected Overrides Async Function SendAsync(request As HttpRequestMessage, token As CancellationToken) As Task(Of HttpResponseMessage)
            Using forwarded As New HttpRequestMessage(request.Method, _address)
                Dim response = Await _client.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(False)
                Headers.TrySetResult(True)
                Return response
            End Using
        End Function
        Protected Overrides Sub Dispose(disposing As Boolean)
            If disposing Then _client.Dispose()
            MyBase.Dispose(disposing)
        End Sub
    End Class

    Private NotInheritable Class ThrowingHandler
        Inherits HttpMessageHandler
        Private ReadOnly _platform As ModpackPlatform
        Friend ReadOnly Content As New ThrowingContent()
        Friend Attempts As Integer
        Friend Sub New(platform As ModpackPlatform)
            _platform = platform
        End Sub
        Protected Overrides Function SendAsync(request As HttpRequestMessage, token As CancellationToken) As Task(Of HttpResponseMessage)
            token.ThrowIfCancellationRequested()
            Dim attempt = Interlocked.Increment(Attempts)
            Dim payload As HttpContent = If(attempt = 1, DirectCast(Content, HttpContent), New StringContent(ResponseData(_platform).ToString(), Encoding.UTF8, "application/json"))
            Return Task.FromResult(New HttpResponseMessage(HttpStatusCode.OK) With {.Content = payload})
        End Function
    End Class

    Private NotInheritable Class FailureHandler
        Inherits HttpMessageHandler
        Private ReadOnly _json As Boolean
        Friend ReadOnly Addresses As New List(Of String)
        Friend Sub New(json As Boolean)
            _json = json
        End Sub
        Protected Overrides Function SendAsync(request As HttpRequestMessage, token As CancellationToken) As Task(Of HttpResponseMessage)
            token.ThrowIfCancellationRequested()
            Addresses.Add(request.RequestUri.Host)
            Dim payload As HttpContent = If(_json, DirectCast(New StringContent("{invalid", Encoding.UTF8, "application/json"), HttpContent), New ThrowingContent(True))
            Return Task.FromResult(New HttpResponseMessage(HttpStatusCode.OK) With {.Content = payload})
        End Function
    End Class

    Private NotInheritable Class ThrowingContent
        Inherits HttpContent
        Friend ReadOnly Input As InterruptedStream
        Friend Sub New(Optional failImmediately As Boolean = False)
            Input = New InterruptedStream(failImmediately)
        End Sub
        Protected Overrides Function CreateContentReadStreamAsync() As Task(Of Stream)
            Return Task.FromResult(Of Stream)(Input)
        End Function
        Protected Overrides Function SerializeToStreamAsync(stream As Stream, context As TransportContext) As Task
            Throw New NotSupportedException()
        End Function
        Protected Overrides Function TryComputeLength(ByRef length As Long) As Boolean
            length = 0
            Return False
        End Function
        Protected Overrides Sub Dispose(disposing As Boolean)
            If disposing Then Input.Dispose()
            MyBase.Dispose(disposing)
            If disposing Then Throw New IOException("fixture response closed before disposal failed")
        End Sub
    End Class

    Private NotInheritable Class InterruptedStream
        Inherits Stream
        Friend ReadOnly Started As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Private ReadOnly _read As New TaskCompletionSource(Of Integer)(TaskCreationOptions.RunContinuationsAsynchronously)
        Private _closed As Integer
        Friend Sub New(failImmediately As Boolean)
            If failImmediately Then _read.TrySetException(New IOException("fixture pending read failed without cancellation"))
        End Sub
        Friend ReadOnly Property Closed As Boolean
            Get
                Return Volatile.Read(_closed) <> 0
            End Get
        End Property
        Public Overrides ReadOnly Property CanRead As Boolean
            Get
                Return Not Closed
            End Get
        End Property
        Public Overrides ReadOnly Property CanSeek As Boolean
            Get
                Return False
            End Get
        End Property
        Public Overrides ReadOnly Property CanWrite As Boolean
            Get
                Return False
            End Get
        End Property
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
        Public Overrides Function ReadAsync(buffer As Byte(), offset As Integer, count As Integer, token As CancellationToken) As Task(Of Integer)
            Started.TrySetResult(True)
            ' Deliberately model a net48 transport interrupted by Dispose rather than
            ' a cooperative stream completing its read with the deadline token.
            Return _read.Task
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
        Protected Overrides Sub Dispose(disposing As Boolean)
            If disposing Then
                Interlocked.Exchange(_closed, 1)
                _read.TrySetException(New IOException("fixture pending read interrupted by response disposal"))
            End If
            MyBase.Dispose(disposing)
        End Sub
    End Class
End Module
