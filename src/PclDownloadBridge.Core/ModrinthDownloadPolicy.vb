Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Threading

' The mrpack format specifies a host allowlist and bounded HTTPS redirects.
' GitHub releases use separate asset hosts after the initial github.com URL.
Friend NotInheritable Class ModrinthDownloadPolicy
    Private Sub New()
    End Sub
    Friend Shared Sub ValidateUrl(value As String, allowLoopback As Boolean, redirect As Boolean)
        Dim address As Uri = Nothing
        If String.IsNullOrWhiteSpace(value) OrElse value.Any(Function(ch) Char.IsWhiteSpace(ch) OrElse Char.IsControl(ch) OrElse AscW(ch) < 0 OrElse AscW(ch) > 127) OrElse value.Contains("\"c) OrElse Text.RegularExpressions.Regex.IsMatch(value, "%(?![0-9A-Fa-f]{2})") OrElse Not Uri.TryCreate(value, UriKind.Absolute, address) Then Throw New InvalidDataException("Modrinth 下载地址无效。")
        If allowLoopback AndAlso address.IsLoopback AndAlso address.Scheme = "http" AndAlso address.UserInfo.Length = 0 Then Return
        If address.Scheme <> "https" OrElse Not address.IsDefaultPort OrElse address.UserInfo.Length > 0 OrElse address.Fragment.Length > 0 Then Throw New InvalidDataException("Modrinth 下载地址必须使用标准 HTTPS。")
        Dim allowed = {"cdn.modrinth.com", "github.com", "raw.githubusercontent.com", "gitlab.com"}.Contains(address.Host, StringComparer.OrdinalIgnoreCase)
        If redirect Then allowed = allowed OrElse {"release-assets.githubusercontent.com", "objects.githubusercontent.com", "github-releases.githubusercontent.com"}.Contains(address.Host, StringComparer.OrdinalIgnoreCase)
        If Not allowed Then Throw New InvalidDataException("Modrinth 下载主机不在允许列表中：" & address.Host)
    End Sub
    Friend Shared Function CreateClient(allowLoopback As Boolean) As HttpClient
        Dim client As New HttpClient(New RedirectHandler(allowLoopback)) With {.Timeout = Timeout.InfiniteTimeSpan}
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PclDownloadBridge/4.0.0-beta.1 (local Minecraft installer)")
        Return client
    End Function
    Private NotInheritable Class RedirectHandler
        Inherits DelegatingHandler
        Private ReadOnly _allowLoopback As Boolean
        Friend Sub New(allowLoopback As Boolean)
            MyBase.New(New HttpClientHandler With {.AllowAutoRedirect = False, .AutomaticDecompression = DecompressionMethods.None, .MaxConnectionsPerServer = 16})
            _allowLoopback = allowLoopback
        End Sub
        Protected Overrides Async Function SendAsync(request As HttpRequestMessage, token As CancellationToken) As Task(Of HttpResponseMessage)
            Dim address = request.RequestUri, seen As New HashSet(Of String)(StringComparer.Ordinal)
            For hop = 0 To 5
                ValidateUrl(address.AbsoluteUri, _allowLoopback, hop > 0)
                If Not seen.Add(address.AbsoluteUri) Then Throw New InvalidDataException("Modrinth 下载重定向形成循环。")
                Using forwarded As New HttpRequestMessage(request.Method, address)
                    For Each header In request.Headers
                        forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value)
                    Next
                    Dim response = Await MyBase.SendAsync(forwarded, token).ConfigureAwait(False)
                    Dim code = CInt(response.StatusCode)
                    If Not {301, 302, 303, 307, 308}.Contains(code) Then Return response
                    Try
                        If hop = 5 OrElse response.Headers.Location Is Nothing Then Throw New InvalidDataException("Modrinth 下载重定向过多或缺少地址。")
                        address = New Uri(address, response.Headers.Location)
                    Finally
                        response.Dispose()
                    End Try
                End Using
            Next
            Throw New InvalidDataException("Modrinth 下载重定向失败。")
        End Function
    End Class
End Class
