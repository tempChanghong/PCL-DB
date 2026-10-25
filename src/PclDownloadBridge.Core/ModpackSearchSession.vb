Imports System.Threading

Public NotInheritable Class ModpackSearchPreview
    Public ReadOnly Property Projects As IReadOnlyList(Of ModpackProject)
    Public ReadOnly Property Pending As ModpackPlatform()
    Friend Sub New(items As IEnumerable(Of ModpackProject), pendingSources As IEnumerable(Of ModpackPlatform))
        Projects = items.ToList().AsReadOnly() : Pending = pendingSources.ToArray()
    End Sub
End Class

' PCL ResourceSearcher.SearchResult: one cursor per source, append-only results,
' partial failure, and a stable 40-item presentation page. No fuzzy identity merge.
Public NotInheritable Class ModpackSearchSession
    ' Zero-based presentation pages. Source cursors may still advance while the
    ' last legal page is incomplete; a full final page must never offer page 501.
    Public Const MaxPage As Integer = 500
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
        ' PCL ResourceSearcher distinguishes an unsupported category from no hits.
        If _offsets.Count = 0 Then Throw New ArgumentException(_query.Platforms.ToString() & " 不支持所选的" & CommunityResources.Name(_query.Kind) & "类型，请更改来源或类型。")
    End Sub
    ' PCL opens details with Loader.Input.GameVersion, not the editable form.
    Public ReadOnly Property Query As ModpackQuery
        Get
            Return _query.Copy()
        End Get
    End Property
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
    Public ReadOnly Property FailedPlatforms As ModpackPlatform()
        Get
            Return _errors.Keys.ToArray()
        End Get
    End Property
    Public Function HasNext(page As Integer) As Boolean
        If page < 0 OrElse page > MaxPage Then Return False
        If page = MaxPage Then Return _results.Count < (page + 1) * ModpackCatalogClient.PageSize AndAlso HasMore
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
    Public Async Function GetPageAsync(page As Integer, client As ModpackCatalogClient, token As CancellationToken, Optional progress As IProgress(Of ModpackSearchPreview) = Nothing) As Task(Of List(Of ModpackProject))
        If page < 0 OrElse page > MaxPage Then Throw New ArgumentOutOfRangeException(NameOf(page))
        Await _gate.WaitAsync(token).ConfigureAwait(False)
        Try
            Dim target = (page + 1) * ModpackCatalogClient.PageSize
            For round = 1 To 4
                If _results.Count >= target OrElse Not HasMore Then Exit For
                Dim active = _offsets.Keys.Where(Function(platform) Not _ended.Contains(platform) AndAlso Not _errors.ContainsKey(platform)).ToArray()
                Dim pending = active.Select(Function(platform) New With {.Platform = platform, .Work = FetchAsync(client, platform, _query, _offsets(platform), token)}).ToList()
                Dim batches As FetchResult()
                If progress IsNot Nothing AndAlso pending.Count > 1 Then
                    Dim completed As New List(Of FetchResult)
                    Try
                        While pending.Count > 0
                            Dim work = Await Task.WhenAny(pending.Select(Function(item) item.Work)).ConfigureAwait(False)
                            Dim batch = Await work.ConfigureAwait(False)
                            pending.RemoveAll(Function(item) item.Work Is work) : completed.Add(batch)
                            token.ThrowIfCancellationRequested()
                            If pending.Count > 0 Then
                                Dim freshPreview = completed.Where(Function(item) item.Page IsNot Nothing).SelectMany(Function(item) item.Page.Projects).
                                    GroupBy(Function(item) item.Key, StringComparer.Ordinal).Select(Function(group) group.First()).Where(Function(item) Not _seen.Contains(item.Key)).
                                    OrderByDescending(Function(item) Relevance(item.Name)).ThenByDescending(Function(item) item.Downloads).ThenBy(Function(item) item.Key, StringComparer.Ordinal)
                                Dim preview = _results.Concat(freshPreview).Skip(page * ModpackCatalogClient.PageSize).Take(ModpackCatalogClient.PageSize)
                                progress.Report(New ModpackSearchPreview(preview, pending.Select(Function(item) item.Platform)))
                            End If
                        End While
                    Finally
                        ' Pending requests own their response/stream cleanup. On
                        ' cancellation observe their eventual faults before disposal.
                        For Each item In pending
                            Dim observer = item.Work.ContinueWith(Function(task) task.Exception, TaskContinuationOptions.OnlyOnFaulted Or TaskContinuationOptions.ExecuteSynchronously)
                        Next
                    End Try
                    batches = completed.ToArray()
                Else
                    batches = Await Task.WhenAll(pending.Select(Function(item) item.Work)).ConfigureAwait(False)
                End If
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
