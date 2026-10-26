Imports System.IO
Imports System.Net.Http
Imports System.Threading
Imports System.Windows.Media.Imaging
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private _packDetailArtworkRequest As CancellationTokenSource
    Private _packDetailArtworkRow As PclResourceItem

    Private Sub RetirePackDetailArtwork()
        Dim request = _packDetailArtworkRequest
        _packDetailArtworkRequest = Nothing : _packDetailArtworkRow = Nothing
        request?.Cancel() : request?.Dispose()
    End Sub

    ' PCL MyResourceItem gives its own MyImage a source and a local fallback.
    ' Its header must not inherit a retired search page's artwork cancellation.
    Private Async Sub RefreshPackDetailArtwork()
        If _closing OrElse _downloadView <> DownloadView.Modpacks OrElse _packMode <> PackBrowserMode.Detail OrElse Not PackDetailSurface.IsVisible Then Return
        Dim row = PackProjectIdentity.Children.OfType(Of PclResourceItem)().FirstOrDefault(), project = ActivePackProject()
        If row Is Nothing OrElse project Is Nothing OrElse row.Tag IsNot project Then Return
        If _packDetailArtworkRow Is row AndAlso _packDetailArtworkRequest IsNot Nothing AndAlso Not _packDetailArtworkRequest.IsCancellationRequested Then Return
        RetirePackDetailArtwork()
        Dim request As New CancellationTokenSource()
        _packDetailArtworkRequest = request : _packDetailArtworkRow = row
        Try
            Await LoadPackIconCoreAsync(row, project, request.Token)
        Finally
            If _packDetailArtworkRequest Is request Then _packDetailArtworkRequest = Nothing : _packDetailArtworkRow = Nothing
            request.Dispose()
        End Try
    End Sub

    Private Async Sub LoadPackIconAsync(row As PclResourceItem, project As ModpackProject, token As CancellationToken)
        Await LoadPackIconCoreAsync(row, project, token)
    End Sub
    Private Function CurrentPackArtwork(row As PclResourceItem, project As ModpackProject, url As String, token As CancellationToken) As Boolean
        Return Not _closing AndAlso Not token.IsCancellationRequested AndAlso row.Tag Is project AndAlso String.Equals(project.IconUrl, url, StringComparison.Ordinal)
    End Function
    Private Async Function LoadPackIconCoreAsync(row As PclResourceItem, project As ModpackProject, token As CancellationToken) As Task
        Dim entered = False
        Try
            If row Is Nothing OrElse project Is Nothing Then Return
            Dim url = project.IconUrl
            If String.IsNullOrWhiteSpace(url) OrElse Not CurrentPackArtwork(row, project, url, token) Then Return
            Dim address As Uri = Nothing
            If Not Uri.TryCreate(url, UriKind.Absolute, address) OrElse address.Scheme <> "https" OrElse Not address.IsDefaultPort OrElse address.UserInfo <> "" OrElse Not {"cdn.modrinth.com", "media.forgecdn.net", "mediafilez.forgecdn.net"}.Contains(address.Host, StringComparer.OrdinalIgnoreCase) Then Return
            Dim cached As BitmapSource = Nothing
            If _packIcons.TryGetValue(url, cached) Then row.Icon.Source = cached : Return
            Await _packIconSlots.WaitAsync(token) : entered = True
            If Not CurrentPackArtwork(row, project, url, token) Then Return
            Using client = _packArtworkClientFactory(), response = Await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, token)
                response.EnsureSuccessStatusCode()
                Using deadline = CancellationTokenSource.CreateLinkedTokenSource(token)
                    deadline.CancelAfter(TimeSpan.FromSeconds(12))
                    Using registration = deadline.Token.Register(Sub() response.Dispose()), input = Await response.Content.ReadAsStreamAsync(), bytes As New MemoryStream()
                        Dim block(8191) As Byte
                        Do
                            Dim read = Await input.ReadAsync(block, 0, block.Length, deadline.Token)
                            If read = 0 Then Exit Do
                            If bytes.Length + read > 2 * 1024 * 1024 Then Return
                            bytes.Write(block, 0, read)
                        Loop
                        If Not CurrentPackArtwork(row, project, url, token) Then Return
                        bytes.Position = 0
                        Dim bitmap As New BitmapImage()
                        bitmap.BeginInit() : bitmap.CacheOption = BitmapCacheOption.OnLoad : bitmap.DecodePixelWidth = 100 : bitmap.StreamSource = bytes : bitmap.EndInit() : bitmap.Freeze()
                        If _packIcons.Count >= 160 Then _packIcons.Clear()
                        _packIcons(url) = bitmap : row.Icon.Source = bitmap
                    End Using
                End Using
            End Using
        Catch
            ' PCL's image fallback remains optional; versions and installs stay usable.
        Finally
            If entered Then _packIconSlots.Release()
        End Try
    End Function
End Class
