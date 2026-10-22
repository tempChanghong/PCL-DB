Imports System.Threading

' PCL ResourceSearcher.SearchResult: one cursor per source, append-only results,
' partial failure, and a stable 40-item presentation page. No fuzzy identity merge.
Public NotInheritable Class ModpackSearchSession
    Private ReadOnly _query As ModpackQuery
    Private ReadOnly _gate As New SemaphoreSlim(1)
    Private ReadOnly _results As New List(Of ModpackProject)
    Private ReadOnly _seen As New HashSet(Of String)(StringComparer.Ordinal)
    Private ReadOnly _offsets As New Dictionary(Of ModpackPlatform, Integer)
    Private ReadOnly _ended As New HashSet(Of ModpackPlatform)
    Private ReadOnly _errors As New Dictionary(Of ModpackPlatform, String)
    Public Sub New(query As ModpackQuery)
        _query = query.Copy()
        ' Validate even when a category excludes both sources.
        ModpackCatalogClient.SearchUrl(ModpackPlatform.Modrinth, _query, 0)
        Dim tags = _query.Category.Split("/"c)
        For Each platform In {ModpackPlatform.CurseForge, ModpackPlatform.Modrinth}
            If Not _query.Platforms.HasFlag(platform) Then Continue For
            If _query.Category <> "/" AndAlso tags(If(platform = ModpackPlatform.CurseForge, 0, 1)) = "" Then Continue For
            _offsets(platform) = 0
        Next
    End Sub
    Public ReadOnly Property HasMore As Boolean
        Get
            Return _offsets.Keys.Any(Function(platform) Not _ended.Contains(platform) AndAlso Not _errors.ContainsKey(platform))
        End Get
    End Property
    Public ReadOnly Property Count As Integer
        Get
            Return _results.Count
        End Get
    End Property
    Public ReadOnly Property ErrorMessage As String
        Get
            Return String.Join(vbCrLf, _errors.Select(Function(pair) pair.Key.ToString() & "：" & pair.Value))
        End Get
    End Property
    Public Function HasNext(page As Integer) As Boolean
        Return _results.Count > (page + 1) * ModpackCatalogClient.PageSize OrElse HasMore
    End Function
    Private Class FetchResult
        Public Platform As ModpackPlatform
        Public Page As ModpackCatalogPage
        Public Failure As String
    End Class
    Private Shared Async Function FetchAsync(client As ModpackCatalogClient, platform As ModpackPlatform, query As ModpackQuery, offset As Integer, token As CancellationToken) As Task(Of FetchResult)
        Try
            Return New FetchResult With {.Platform = platform, .Page = Await client.SearchAsync(platform, query, offset, token).ConfigureAwait(False)}
        Catch ex As Exception When Not token.IsCancellationRequested
            Return New FetchResult With {.Platform = platform, .Failure = ex.GetBaseException().Message}
        End Try
    End Function
    Public Async Function GetPageAsync(page As Integer, client As ModpackCatalogClient, token As CancellationToken) As Task(Of List(Of ModpackProject))
        If page < 0 OrElse page > 500 Then Throw New ArgumentOutOfRangeException(NameOf(page))
        Await _gate.WaitAsync(token).ConfigureAwait(False)
        Try
            Dim target = (page + 1) * ModpackCatalogClient.PageSize
            For round = 1 To 4
                If _results.Count >= target OrElse Not HasMore Then Exit For
                Dim active = _offsets.Keys.Where(Function(platform) Not _ended.Contains(platform) AndAlso Not _errors.ContainsKey(platform)).ToArray()
                Dim batches = Await Task.WhenAll(active.Select(Function(platform) FetchAsync(client, platform, _query, _offsets(platform), token))).ConfigureAwait(False)
                token.ThrowIfCancellationRequested()
                Dim fresh As New List(Of ModpackProject)
                For Each batch In batches
                    If batch.Failure IsNot Nothing Then
                        _errors(batch.Platform) = batch.Failure
                        Continue For
                    End If
                    Dim value = batch.Page
                    _offsets(batch.Platform) += value.Returned
                    If value.Returned = 0 OrElse _offsets(batch.Platform) >= value.Total Then _ended.Add(batch.Platform)
                    For Each project In value.Projects
                        If _seen.Add(project.Key) Then fresh.Add(project)
                    Next
                    ' Repeated non-empty raw pages must not create an endless next button.
                    If value.Projects.Count > 0 AndAlso value.Projects.All(Function(project) _results.Any(Function(previous) previous.Key = project.Key)) Then
                        _ended.Add(batch.Platform)
                        _errors(batch.Platform) = "平台分页返回了重复结果，请重新搜索。"
                    End If
                Next
                _results.AddRange(fresh.OrderByDescending(Function(project) Relevance(project.Name)).ThenByDescending(Function(project) project.Downloads).ThenBy(Function(project) project.Key, StringComparer.Ordinal))
            Next
            Return _results.Skip(page * ModpackCatalogClient.PageSize).Take(ModpackCatalogClient.PageSize).ToList()
        Finally
            _gate.Release()
        End Try
    End Function
    Private Function Relevance(name As String) As Integer
        Dim text = _query.Text.Trim()
        If text = "" Then Return 0
        If String.Equals(name, text, StringComparison.OrdinalIgnoreCase) Then Return 2
        Return If(name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0, 1, 0)
    End Function
End Class
