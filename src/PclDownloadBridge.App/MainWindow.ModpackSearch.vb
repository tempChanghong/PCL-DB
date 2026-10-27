Imports System.IO
Imports System.Net.Http
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports System.Windows.Media.Imaging
Imports Newtonsoft.Json
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Enum PackBrowserMode
        Search
        Detail
        Local
    End Enum
    Private _packMode As PackBrowserMode
    Private _packSearchSession As ModpackSearchSession
    Private _packSearchRequest As CancellationTokenSource
    Private _packDetailRequest As CancellationTokenSource
    Private _packArtworkRequest As CancellationTokenSource
    Private _packLastQuery As String
    Private _packLastSearch As DateTime
    Private _packSearchPage As Integer
    Private _packSearchScroll As Double
    Private _packDetailScroll As Double
    Private _packProject As ModpackProject
    Private _packReleases As New List(Of ModpackRelease)
    Private _packGameFilter As String = ""
    Private _packResultMotion As PclSurfaceMotion
    Private _onlinePrompt As TaskCompletionSource(Of String)
    Private _onlinePromptClosed As TaskCompletionSource(Of Boolean)
    Private _onlinePromptRoot As String
    Private _onlineOptionalPrompt As Boolean
    Private _onlinePromptMotion As ClockGroup
    Private _onlinePromptRotation As RotateTransform
    Private _onlinePromptOffset As TranslateTransform
    Private _onlinePromptShade As SolidColorBrush
    Private _choosingOnline As Boolean
    Private _onlineLastName As String
    Private _onlineLastRelease As String
    Private _packCatalogFactory As Func(Of ModpackCatalogClient) = Function() New ModpackCatalogClient(communitySource:=_communityPreferences.Source)
    Private _onlineInstallRunner As Func(Of ModpackRelease, String, String, IProgress(Of TransferProgress), CancellationToken, Task(Of InstallResult))
    Private ReadOnly _packIcons As New Dictionary(Of String, BitmapSource)(StringComparer.Ordinal)
    Private ReadOnly _packIconSlots As New SemaphoreSlim(4)
    Private _packArtworkClientFactory As Func(Of HttpClient) = Function() New HttpClient(New HttpClientHandler With {.AllowAutoRedirect = False}) With {.Timeout = TimeSpan.FromSeconds(12)}

    Private Sub InitializePackSearch()
        InitializeSearchPresentation()
        _packResultMotion = New PclSurfaceMotion(PackSearchResultsHost, PackSearchGhost,
            Function() CanAnimatePackContent(PackBrowserMode.Search),
            Function(surface) PclSurfaceMotion.Units(surface))
        _packDetailMotion = New PclSurfaceMotion(PackDetailResultsHost, PackDetailGhost,
            Function() CanAnimatePackContent(PackBrowserMode.Detail),
            Function(surface) PclSurfaceMotion.Units(surface))
        AddHandler PackDetailSurface.IsVisibleChanged, Sub()
                                                          If _closing Then Return
                                                          If PackDetailSurface.IsVisible Then RefreshResourceDependencyLabels() : RefreshPackDetailArtwork() : Return
                                                          RetirePackDetailArtwork()
                                                          RetirePackProjectAction()
                                                          _dependencyRequest?.Cancel() : _resourceNamesRequest?.Cancel()
                                                      End Sub
        AddHandler PackProjectIntro.IsEnabledChanged, Sub() If Not PackProjectIntro.IsEnabled Then RetirePackProjectAction()
        AddHandler PackProjectIntro.IsHitTestVisibleChanged, Sub() If Not PackProjectIntro.IsHitTestVisible Then RetirePackProjectAction()
        _packSearchLoading = New PclLoadingPresentation(PackSearchResultsHost, AddressOf CanTimePackLoading, Function() PackEntryMotion(PackBrowserMode.Search))
        _packDetailLoading = New PclLoadingPresentation(PackDetailResultsHost, AddressOf CanTimePackLoading, Function() PackEntryMotion(PackBrowserMode.Detail))
        AddHandler _categoryMotion.Settled, Sub() EnsurePackSearch()
        AddHandler _surfaceMotion.Settled, Sub() EnsurePackSearch()
        For Each game In {"全部 (也可自行输入)", "1.21.1", "1.20.1", "1.19.2", "1.18.2", "1.16.5", "1.12.2", "1.7.10"}
            PackSearchGame.Items.Add(New PclComboBoxItem With {.Content = game})
        Next
        PackSearchGame.SelectedIndex = 0
        PackSearchGame.Text = "全部 (也可自行输入)"
        Dim tags = New Dictionary(Of String, String) From {{"全部", "/"}, {"战斗", "4483/combat"}, {"任务", "4478/quests"}, {"科技", "4472/technology"}, {"魔法", "4473/magic"}, {"冒险", "4475/adventure"}, {"优化", "/optimization"}, {"轻量", "4481/lightweight"}, {"多人", "4484/"}, {"大型", "4482/"}}
        For Each pair In tags : PackSearchCategory.Items.Add(New PclComboBoxItem With {.Content = pair.Key, .Tag = pair.Value}) : Next
        PackSearchCategory.SelectedIndex = 0
        ConfigureResourceSearch()
        AddHandler PackSearchText.KeyDown, AddressOf PackSearchEnter
        AddHandler PackSearchGame.KeyDown, AddressOf PackSearchEnter
        AddHandler PackSearchSource.SelectionChanged, Sub() PackSearchInputsChanged()
        AddHandler PackSearchCategory.SelectionChanged, Sub() PackSearchInputsChanged()
        AddHandler PackSearchGame.SelectionChanged, Sub() PackSearchInputsChanged()
        AddHandler ResourceLoader.SelectionChanged, Sub() PackSearchInputsChanged()
        AddHandler PackSearchText.TextChanged, Sub() PackSearchInputsChanged()
        PackSearchGame.AddHandler(TextBox.TextChangedEvent, New TextChangedEventHandler(Sub() PackSearchInputsChanged()))
        AddHandler OnlinePackName.TextChanged, Sub() ValidateOnlinePackName()
        AddHandler OnlinePackName.KeyDown, Sub(sender, e)
                                                If e.Key = Key.Enter AndAlso Not e.IsRepeat Then e.Handled = True : ConfirmOnlinePackName(sender, e)
                                            End Sub
    End Sub
    Private _packImmediateSearch As Boolean
    Private Sub EnsurePackSearch(Optional initialEntry As Boolean = False)
        If (Not _preview OrElse _packAutoSearchForChecks) AndAlso Not _busy AndAlso Not _closing AndAlso _packSearchSession Is Nothing AndAlso _downloadView = DownloadView.Modpacks AndAlso _packMode = PackBrowserMode.Search AndAlso DownloadPage.IsVisible AndAlso DownloadViewHost.IsHitTestVisible AndAlso PageSurface.IsHitTestVisible Then
            _packImmediateSearch = initialEntry
            Try
                RunPackSearch(Nothing, Nothing)
            Finally
                _packImmediateSearch = False
            End Try
        End If
    End Sub
    Private Sub PackSearchEnter(sender As Object, e As KeyEventArgs)
        If e.Key <> Key.Enter OrElse e.IsRepeat Then Return
        e.Handled = True : SubmitPackSearch(sender, e)
    End Sub
    Private Sub ChangePackMode(mode As PackBrowserMode)
        If _packMode = mode Then Return
        RetirePromptFocus()
        If _packMode = PackBrowserMode.Search Then _packSearchScroll = ModpackPage.VerticalOffset
        If _packMode = PackBrowserMode.Detail Then _packDetailScroll = ModpackPage.VerticalOffset
        ModpackPage.StopScrolling()
        Dim apply As Action = Sub()
                                  FinishPackContentMotion()
                                  _packMode = mode
                                  If mode <> PackBrowserMode.Search Then _restorePackViewportFocus = False
                                  PackSearchSurface.Visibility = If(mode = PackBrowserMode.Search, Visibility.Visible, Visibility.Collapsed)
                                  PackDetailSurface.Visibility = If(mode = PackBrowserMode.Detail, Visibility.Visible, Visibility.Collapsed)
                                  PackLocalSurface.Visibility = If(mode = PackBrowserMode.Local, Visibility.Visible, Visibility.Collapsed)
                                  RefreshDownloadChrome()
                                  ModpackPage.UpdateLayout()
                                  ModpackPage.ScrollToVerticalOffset(If(mode = PackBrowserMode.Search, _packSearchScroll, If(mode = PackBrowserMode.Detail, _packDetailScroll, 0)))
                              End Sub
        If _downloadView = DownloadView.Modpacks AndAlso DownloadPage.IsVisible Then
            _surfaceMotion.Finish()
            _categoryMotion.Change(ModpackPage, ModpackPage, apply, contentRefresh:=True)
        Else
            apply()
        End If
    End Sub
    Private Sub OpenLocalPacks(sender As Object, e As RoutedEventArgs)
        If _busy OrElse _choosingOnline OrElse _resourceKind <> CommunityResourceKind.Modpack Then Return
        ChangePackMode(PackBrowserMode.Local)
    End Sub
    Private Sub BackToPackSearch(sender As Object, e As RoutedEventArgs)
        If _choosingOnline Then Return
        _dependencyRequest?.Cancel()
        ResetResourceNames()
        If _packMode = PackBrowserMode.Detail AndAlso _resourceBack.Count > 0 Then
            _packDetailRequest?.Cancel()
            Dim frame = _resourceBack.Pop()
            ChangePackContent(PackBrowserMode.Detail, Sub() RestoreResourceDetail(frame))
            Return
        End If
        ChangePackMode(PackBrowserMode.Search)
    End Sub
    Private Function CurrentPackQuery() As ModpackQuery
        Dim game = PackSearchGameValue(), category = TryCast(PackSearchCategory.SelectedItem, ComboBoxItem)
        Return New ModpackQuery With {.Kind = _resourceKind, .Loader = If(_resourceKind = CommunityResourceKind.Mod, CStr(TryCast(ResourceLoader.SelectedItem, ComboBoxItem)?.Tag), ""), .Text = PackSearchText.Text.Trim(), .Minecraft = game,
            .Platforms = If(PackSearchSource.SelectedIndex = 1, ModpackPlatform.CurseForge, If(PackSearchSource.SelectedIndex = 2, ModpackPlatform.Modrinth, ModpackPlatform.All)),
            .Category = If(category Is Nothing, "/", CStr(category.Tag))}
    End Function
    Private _packFiltersPending As Boolean
    Private _packSearchValidation As Boolean
    Private Function PackQueryKey(query As ModpackQuery) As String
        ' PCL's 60-second cache belongs to an accepted request. Transport policy
        ' is part of that identity, including when restoring another category.
        Return JsonConvert.SerializeObject(New With {.Query = query, .Source = _communityPreferences.Source})
    End Function
    Private Sub PackSearchInputsChanged(Optional userEdit As Boolean = True)
        If _closing OrElse _packSearchSession Is Nothing OrElse _packMode <> PackBrowserMode.Search OrElse Not PackSearchSurface.IsVisible Then Return
        If Not userEdit AndAlso _packSearchValidation Then Return
        If userEdit Then _packSearchValidation = False
        Dim pending = PackQueryKey(CurrentPackQuery()) <> _packLastQuery
        If pending Then
            PackSearchInputHint.Theme = PclHintTheme.Blue
            PackSearchInputHint.Text = "筛选已更改，点击「搜索」应用。" : PackSearchInputHint.Visibility = Visibility.Visible
            _packFiltersPending = True
        ElseIf _packFiltersPending Then
            PackSearchInputHint.Visibility = Visibility.Collapsed : _packFiltersPending = False
        End If
    End Sub
    Private Async Sub RunPackSearch(sender As Object, e As RoutedEventArgs)
        If _busy OrElse _choosingOnline OrElse _closing OrElse Not PageSurface.IsHitTestVisible OrElse Not DownloadViewHost.IsHitTestVisible Then Return
        Dim query = CurrentPackQuery()
        ' Structured identity keeps literal separators in different fields distinct.
        Dim key = PackQueryKey(query), candidate As ModpackSearchSession
        Try
            candidate = New ModpackSearchSession(query)
        Catch ex As ArgumentException
            _packSearchValidation = True : _packFiltersPending = False : PackSearchInputHint.Theme = PclHintTheme.Red
            PackSearchInputHint.Text = ex.Message : PackSearchInputHint.Visibility = Visibility.Visible : Return
        End Try
        _packSearchValidation = False : _packFiltersPending = False : PackSearchInputHint.Visibility = Visibility.Collapsed
        ' Like PCL's Loader.ShouldStart / Start, keep the accepted identical request.
        ' In particular, do not enumerate mutable session errors while it is fetching.
        If _packSearchSession IsNot Nothing AndAlso key = _packLastQuery AndAlso _packSearchRequest IsNot Nothing AndAlso Not _packSearchRequest.IsCancellationRequested Then Return
        Dim cacheAge = DateTime.UtcNow - _packLastSearch
        If _packSearchSession IsNot Nothing AndAlso key = _packLastQuery AndAlso _packSearchRequest Is Nothing AndAlso _packSearchSession.ErrorMessage = "" AndAlso cacheAge >= TimeSpan.Zero AndAlso cacheAge <= TimeSpan.FromSeconds(60) Then candidate = _packSearchSession
        _packSearchRequest?.Cancel()
        _packDetailRequest?.Cancel()
        _dependencyRequest?.Cancel() : _resourceBack.Clear()
        If candidate IsNot _packSearchSession Then _packLastSearch = DateTime.UtcNow
        _packSearchSession = candidate : _packLastQuery = key
        _packProject = Nothing : _packVerifiedProject = Nothing : _packDetailCompletedProject = Nothing : _packReleases.Clear()
        _packSearchPage = 0 : _packSearchScroll = 0
        ChangePackMode(PackBrowserMode.Search)
        Await LoadPackSearchPageAsync(0)
    End Sub
    Private Sub ResetPackSearch(sender As Object, e As RoutedEventArgs)
        If _busy OrElse _choosingOnline Then Return
        PackSearchText.Text = "" : PackSearchGame.SelectedIndex = 0 : PackSearchSource.SelectedIndex = 0 : PackSearchCategory.SelectedIndex = 0
        PackSearchGame.Text = "全部 (也可自行输入)"
        PackSearchInputHint.Visibility = Visibility.Collapsed
        ResourceLoader.SelectedIndex = 0
        _packLastQuery = Nothing
        PackSearchInputsChanged()
    End Sub
    Private Async Sub FirstPackPage(sender As Object, e As RoutedEventArgs)
        Await LoadPackSearchPageAsync(0)
    End Sub
    Private Async Sub PreviousPackPage(sender As Object, e As RoutedEventArgs)
        Await LoadPackSearchPageAsync(Math.Max(0, _packSearchPage - 1))
    End Sub
    Private Async Sub NextPackPage(sender As Object, e As RoutedEventArgs)
        Dim incomplete = _packSearchSession IsNot Nothing AndAlso _packSearchSession.Count < (_packSearchPage + 1) * ModpackCatalogClient.PageSize
        Await LoadPackSearchPageAsync(If(incomplete, _packSearchPage, _packSearchPage + 1))
    End Sub
    Private Sub ChangePackResults(update As Action)
        ChangePackContent(PackBrowserMode.Search, update)
    End Sub
    Private Shared Function PackSearchPageLimitText() As String
        Return "搜索最多支持前 " & (ModpackSearchSession.MaxPage + 1).ToString() & " 页，请缩小搜索范围或更改条件。"
    End Function
    Private Async Function LoadPackSearchPageAsync(page As Integer, Optional restoreScroll As Double? = Nothing) As Task
        Dim session = _packSearchSession
        If session Is Nothing OrElse _busy OrElse _closing OrElse _choosingOnline Then Return
        ' Reject stale/programmatic navigation before canceling the accepted
        ' request or replacing its rows. Core callers still receive an exception.
        If page < 0 OrElse page > ModpackSearchSession.MaxPage Then
            PackSearchHint.Theme = PclHintTheme.Yellow : PackSearchHint.Text = PackSearchPageLimitText()
            PackSearchHint.ToolTip = Nothing : PackSearchHint.Visibility = Visibility.Visible
            Return
        End If
        _packSearchRequest?.Cancel()
        _packArtworkRequest?.Cancel() : _packArtworkRequest?.Dispose()
        _packArtworkRequest = New CancellationTokenSource()
        Dim artworkToken = _packArtworkRequest.Token
        Dim request As New CancellationTokenSource : _packSearchRequest = request
        Dim acceptsPreviews = True, previewShown = False, previewWork As Task = Task.CompletedTask
        Dim progress As New Progress(Of ModpackSearchPreview)(Sub(value)
                                                                 If Dispatcher.HasShutdownStarted Then Return
                                                                 Try
                                                                     Dispatcher.BeginInvoke(New Action(Sub()
                                                                                                           If Not acceptsPreviews OrElse Not CurrentPackSearch(session, request) OrElse value.Projects.Count = 0 OrElse Not previewWork.IsCompleted Then Return
                                                                                                           previewWork = ShowPackSearchPreviewAsync(session, request, value, artworkToken,
                                                                                                               Function() acceptsPreviews, Sub() previewShown = True)
                                                                                                       End Sub))
                                                                 Catch ex As InvalidOperationException
                                                                     ' A closing dispatcher owns no presentation.
                                                                 End Try
                                                             End Sub)
        PackFirstPage.IsEnabled = False : PackPreviousPage.IsEnabled = False : PackNextPage.IsEnabled = False
        _packSearchLoading.Start(request, request.Token, AddressOf PreparePackSearchLoading, Sub() ChangePackResults(Sub()
                              PackSearchHint.Visibility = Visibility.Collapsed : PackPagingCard.Visibility = Visibility.Collapsed
                              PackResultsCard.Visibility = Visibility.Visible
                              PackProjectRows.Children.Clear() : PackResultsCard.Title = "正在获取" & ResourceNameSpaced(_resourceKind) & "列表…"
                              PackResultsCard.HorizontalAlignment = HorizontalAlignment.Center
                              PackProjectRows.Children.Add(New PclLoadingView("正在获取" & ResourceNameSpaced(_resourceKind) & "列表"))
                          End Sub), immediate:=_packImmediateSearch)
        Try
            Dim items As List(Of ModpackProject)
            Dim retainedPage = False
            Using client = _packCatalogFactory()
                items = Await session.GetPageAsync(page, client, request.Token, progress)
                ' A source may end early or fail while fetching the next page.
                ' Keep the last populated presentation page and its warning.
                If items.Count = 0 AndAlso session.Count > 0 AndAlso page * ModpackCatalogClient.PageSize >= session.Count Then
                    page = (session.Count - 1) \ ModpackCatalogClient.PageSize
                    items = Await session.GetPageAsync(page, client, request.Token)
                    retainedPage = True
                End If
            End Using
            acceptsPreviews = False
            Await previewWork
            If Not CurrentPackSearch(session, request) Then Return
            Await _packSearchLoading.CompleteAsync(request)
            If Not CurrentPackSearch(session, request) Then Return
            _packSearchPage = page
            Dim limitReached = page = ModpackSearchSession.MaxPage AndAlso session.Count >= (page + 1) * ModpackCatalogClient.PageSize AndAlso
                (session.HasMore OrElse session.Count > (page + 1) * ModpackCatalogClient.PageSize)
            ChangePackResults(Sub()
                                  PackProjectRows.Children.Clear() : PackResultsCard.Title = CommunityResources.Name(_resourceKind) : PackResultsCard.Visibility = Visibility.Visible
                                  PackResultsCard.HorizontalAlignment = HorizontalAlignment.Stretch
                                  For Each project In items
                                      Dim row As New PclResourceItem(project, _resourceVersionFamilies) With {.Tag = project}
                                      AddHandler row.Click, Sub() OpenPackDetails(project)
                                      PackProjectRows.Children.Add(row)
                                      LoadPackIconAsync(row, project, artworkToken)
                                  Next
                                  If items.Count = 0 Then PackProjectRows.Children.Add(New TextBlock With {.Text = If(session.HasMore, "暂无结果，可点击「继续加载」。", If(session.ErrorMessage = "", "没有找到符合条件的" & ResourceNameSpaced(_resourceKind) & "。", "获取失败，请重试或更换来源。")), .Margin = New Thickness(10), .TextWrapping = TextWrapping.Wrap})
                                  PackSearchHint.Theme = If(items.Count > 0 OrElse session.HasMore, PclHintTheme.Yellow, PclHintTheme.Red)
                                  If items.Count > 0 Then
                                      Dim sources = String.Join("、", session.FailedPlatforms.Select(Function(value) value.ToString()))
                                      PackSearchHint.Text = If(retainedPage, "加载失败，已保留结果。点击搜索重试。", If(sources = "", "部分来源", sources & " ") & "暂不可用，已显示可用结果。点击搜索重试。")
                                  ElseIf session.HasMore Then
                                      PackSearchHint.Text = "部分来源暂不可用，可继续加载其他来源。"
                                  Else
                                      PackSearchHint.Text = "获取失败，请重试或更换来源。"
                                  End If
                                  If limitReached Then
                                      PackSearchHint.Theme = PclHintTheme.Yellow
                                      PackSearchHint.Text = PackSearchPageLimitText() & If(session.ErrorMessage = "", "", vbCrLf & PackSearchHint.Text)
                                  End If
                                  PackSearchHint.ToolTip = session.ErrorMessage
                                  PackSearchHint.Visibility = If(limitReached OrElse session.ErrorMessage <> "", Visibility.Visible, Visibility.Collapsed)
                                  PackPagingCard.Visibility = If(session.Count > ModpackCatalogClient.PageSize OrElse session.HasMore, Visibility.Visible, Visibility.Collapsed)
                                  PackPageLabel.Text = (page + 1).ToString()
                                  PackFirstPage.IsEnabled = page > 0 : PackPreviousPage.IsEnabled = page > 0 : PackNextPage.IsEnabled = session.HasNext(page)
                                  PackNextPage.Text = If(session.Count < (page + 1) * ModpackCatalogClient.PageSize AndAlso session.HasMore, "继续加载", "下一页")
                              End Sub)
            If CurrentPackSearch(session, request) AndAlso _packMode = PackBrowserMode.Search AndAlso _downloadView = DownloadView.Modpacks Then
                If restoreScroll.HasValue Then
                    ModpackPage.UpdateLayout()
                    ModpackPage.ScrollToVerticalOffset(restoreScroll.Value)
                ElseIf Not previewShown Then
                    ModpackPage.ScrollToHome()
                End If
            End If
            PackSearchInputsChanged(False)
        Catch ex As Exception
            ' Intentional cancellation has already revoked ownership. An unexpected
            ' cancellation of the current request must still leave a terminal view.
            If CurrentPackSearch(session, request) Then
                _packSearchLoading.Cancel(request)
                ChangePackResults(Sub()
                                      PackResultsCard.Visibility = Visibility.Visible
                                      PackResultsCard.Title = "获取失败" : PackResultsCard.HorizontalAlignment = HorizontalAlignment.Stretch
                                      PackProjectRows.Children.Clear()
                                      PackProjectRows.Children.Add(New TextBlock With {.Text = "获取失败，请重试或更换来源。", .Margin = New Thickness(10), .TextWrapping = TextWrapping.Wrap})
                                      PackSearchHint.Theme = PclHintTheme.Red : PackSearchHint.Text = "获取失败，请点击搜索重试。"
                                      PackSearchHint.ToolTip = ex.GetBaseException().Message : PackSearchHint.Visibility = Visibility.Visible
                                      PackPagingCard.Visibility = Visibility.Collapsed
                                  End Sub)
            End If
        Finally
            acceptsPreviews = False
            _packSearchLoading.Cancel(request)
            If _packSearchRequest Is request Then _packSearchRequest = Nothing
            request.Dispose()
        End Try
    End Function
    Private Async Function ShowPackSearchPreviewAsync(session As ModpackSearchSession, request As CancellationTokenSource, preview As ModpackSearchPreview,
                                                       artworkToken As CancellationToken, accepted As Func(Of Boolean), shown As Action) As Task
        Await _packSearchLoading.CompleteAsync(request)
        If Not accepted() OrElse Not CurrentPackSearch(session, request) OrElse _packMode <> PackBrowserMode.Search OrElse _downloadView <> DownloadView.Modpacks Then Return
        ChangePackResults(Sub()
                              PackResultsCard.Visibility = Visibility.Visible : PackResultsCard.HorizontalAlignment = HorizontalAlignment.Stretch
                              PackResultsCard.Title = CommunityResources.Name(_resourceKind) : PackProjectRows.Children.Clear()
                              For Each project In preview.Projects
                                  Dim row As New PclResourceItem(project, _resourceVersionFamilies) With {.Tag = project}
                                  AddHandler row.Click, Sub() If _packSearchSession Is session Then OpenPackDetails(project)
                                  PackProjectRows.Children.Add(row) : LoadPackIconAsync(row, project, artworkToken)
                              Next
                              PackSearchHint.Theme = PclHintTheme.Blue
                              PackSearchHint.Text = "正在获取 " & String.Join("、", preview.Pending.Select(Function(value) value.ToString())) & " 结果…"
                              PackSearchHint.ToolTip = Nothing : PackSearchHint.Visibility = Visibility.Visible : PackPagingCard.Visibility = Visibility.Collapsed
                              shown()
                          End Sub)
    End Function
    Private Function CurrentPackSearch(session As ModpackSearchSession, request As CancellationTokenSource) As Boolean
        Return Not _closing AndAlso Not request.IsCancellationRequested AndAlso _packSearchRequest Is request AndAlso _packSearchSession Is session
    End Function
    Private Async Sub OpenPackDetails(project As ModpackProject)
        If _busy OrElse _choosingOnline OrElse _closing OrElse Not DownloadViewHost.IsHitTestVisible OrElse Not PackSearchResultsHost.IsHitTestVisible Then Return
        Dim changingProject = _packProject Is Nothing OrElse _packProject.Key <> project.Key
        If changingProject Then
            _dependencyRequest?.Cancel() : ResetResourceNames() : _resourceBack.Clear()
            RetirePromptFocus()
            _packProject = project : _packVerifiedProject = Nothing : _packDetailCompletedProject = Nothing : _packReleases.Clear() : _packDetailScroll = 0
            _packTargetGame = If(_packSearchSession?.Query.Minecraft, "")
            _resourceTargetLoader = If(_packSearchSession?.Query.Loader, "")
            _packVersionCache.Clear()
        End If
        RenderPackProject(ActivePackProject())
        ChangePackMode(PackBrowserMode.Detail)
        If _packDetailCompletedProject IsNot _packProject AndAlso (changingProject OrElse _packDetailRequest Is Nothing OrElse _packDetailRequest.IsCancellationRequested) Then
            Await LoadPackDetailsAsync(_packProject)
        End If
    End Sub
    Private Async Sub RetryPackDetails(sender As Object, e As RoutedEventArgs)
        If _packProject Is Nothing OrElse _busy OrElse _choosingOnline OrElse _closing OrElse _packMode <> PackBrowserMode.Detail OrElse _downloadView <> DownloadView.Modpacks OrElse Not PackDetailSurface.IsVisible OrElse Not PageSurface.IsHitTestVisible OrElse Not DownloadViewHost.IsHitTestVisible OrElse Not PackDetailResultsHost.IsHitTestVisible Then Return
        Await LoadPackDetailsAsync(_packProject)
    End Sub
    Private Async Function LoadPackDetailsAsync(project As ModpackProject) As Task
        If project Is Nothing OrElse project IsNot _packProject OrElse _closing Then Return
        ResetResourceNames()
        _packDetailRequest?.Cancel()
        _packDetailCompletedProject = Nothing
        Dim request As New CancellationTokenSource : _packDetailRequest = request
        _packDetailLoading.Start(request, request.Token, AddressOf PreparePackDetailLoading, Sub() ChangePackContent(PackBrowserMode.Detail, Sub()
                                                     PackVersionCards.Children.Clear() : PackVersionFilters.Children.Clear()
                                                     PackVersionFilterCard.Visibility = Visibility.Collapsed
                                                     PackDetailHint.Visibility = Visibility.Collapsed : PackDetailRetry.Visibility = Visibility.Collapsed
                                                     Dim loading As New PclCard With {.ShowHeader = False, .HorizontalAlignment = HorizontalAlignment.Center, .BodyMargin = New Thickness(0), .Body = New PclLoadingView("正在获取版本列表")}
                                                     PackVersionCards.Children.Add(loading)
                                                 End Sub))
        Try
            Using client = _packCatalogFactory()
                Dim verified = Await client.GetProjectAsync(project.Platform, project.Id, request.Token, project.Kind)
                verified.PreferredVersionId = project.PreferredVersionId
                Dim releases = Await client.GetVersionsAsync(verified, request.Token)
                If verified.Kind = CommunityResourceKind.Mod AndAlso Not String.IsNullOrEmpty(_resourceTargetLoader) Then releases = releases.Where(Function(item) item.Loaders.Contains(_resourceTargetLoader, StringComparer.OrdinalIgnoreCase)).ToList()
                If _closing OrElse request.IsCancellationRequested OrElse _packDetailRequest IsNot request OrElse _packProject IsNot project Then Return
                Await _packDetailLoading.CompleteAsync(request)
                If _closing OrElse request.IsCancellationRequested OrElse _packDetailRequest IsNot request OrElse _packProject IsNot project Then Return
                ChangePackContent(PackBrowserMode.Detail, Sub()
                                                             _packVerifiedProject = verified : _packReleases = releases
                                                             RenderPackProject(verified)
                                                             PackDetailHint.Visibility = Visibility.Collapsed : InitializePackVersions()
                                                         End Sub)
                If Not _closing AndAlso Not request.IsCancellationRequested AndAlso _packDetailRequest Is request AndAlso _packProject Is project Then
                    _packDetailCompletedProject = project
                End If
            End Using
        Catch ex As Exception
            If Not _closing AndAlso Not request.IsCancellationRequested AndAlso _packDetailRequest Is request AndAlso _packProject Is project Then
                _packDetailLoading.Cancel(request)
                _packDetailCompletedProject = Nothing
                ChangePackContent(PackBrowserMode.Detail, Sub()
                                                             PackVersionCards.Children.Clear()
                                                             PackDetailHint.Theme = PclHintTheme.Red : PackDetailHint.Text = "无法获取可下载的版本，请重试。" : PackDetailHint.ToolTip = ex.GetBaseException().Message : PackDetailHint.Visibility = Visibility.Visible : PackDetailRetry.Visibility = Visibility.Visible
                                                         End Sub)
            End If
        Finally
            _packDetailLoading.Cancel(request)
            If _packDetailRequest Is request Then _packDetailRequest = Nothing
            request.Dispose()
        End Try
    End Function
    Private Function PackSearchGameValue() As String
        Dim value = PackSearchGame.Text.Trim()
        Return If(value.StartsWith("全部", StringComparison.Ordinal), "", value)
    End Function
    Private Shared Function PackReleaseTitle(value As String) As String
        value = If(value, "").Trim()
        For Each extension In {".zip", ".mrpack"}
            If value.EndsWith(extension, StringComparison.OrdinalIgnoreCase) Then Return value.Substring(0, value.Length - extension.Length).TrimEnd()
        Next
        Return value
    End Function
    Private Async Sub OpenPackProjectWebsite(sender As Object, e As RoutedEventArgs)
        Await DispatchPackProjectActionAsync("link")
    End Sub
    Private Async Sub CopyPackProjectName(sender As Object, e As RoutedEventArgs)
        Await DispatchPackProjectActionAsync("copy")
    End Sub

    Private Async Sub InstallOnlinePack(release As ModpackRelease, Optional displayedTitle As String = Nothing, Optional returnTarget As FrameworkElement = Nothing)
        If _localModWriting Then Return
        If _busy OrElse _choosingOnline OrElse _readingPack OrElse _closing OrElse _packProject Is Nothing OrElse _packMode <> PackBrowserMode.Detail OrElse _downloadView <> DownloadView.Modpacks OrElse Not DownloadPage.IsVisible OrElse Not PageSurface.IsHitTestVisible OrElse Not DownloadViewHost.IsHitTestVisible OrElse Not PackDetailResultsHost.IsHitTestVisible Then Return
        Dim project = ActivePackProject(), snapshot = release.Copy()
        If snapshot.Platform <> project.Platform OrElse snapshot.ProjectId <> project.Id Then Return
        Dim projectKey = project.Key, projectName = project.Name
        Dim blocked = ModpackCompatibility.GetInstallBlockReason(snapshot)
        If blocked IsNot Nothing Then StatusText.Text = blocked : Return
        If _selectedFolder Is Nothing Then ChooseInstallFolder(Nothing, Nothing) : StatusText.Text = "请先选择游戏目录。" : Return
        Dim limit As Double
        If Not TryGetSpeedLimit(limit) Then NavigateToSetting(SpeedLimitBox, True) : Return
        Dim target = CurrentInstallTarget(), root = target.Root
        Try
            target.ValidateDestination(target.Root)
        Catch ex As InvalidOperationException
            PackProjectActionStatus.Text = ex.Message : PackProjectActionStatus.Visibility = Visibility.Visible
            PackProjectIntro.BringIntoView() : Return
        End Try
        _choosingOnline = True
        Try
            Dim name As String
            Try
                Dim proposed = If(_onlineLastRelease = projectKey & ":" & snapshot.Id, _onlineLastName, SuggestedPackName(projectName))
                name = Await PromptOnlinePackAsync("输入版本名称", projectName & " · " & PackReleaseConfirmation(snapshot, displayedTitle), root, proposed, False, CancellationToken.None, returnTarget)
            Finally
                ' Selection owns only the prompt. The installer owns its own busy
                ' state/token, including returns and management inspection.
                _choosingOnline = False
            End Try
            If String.IsNullOrEmpty(name) OrElse _closing Then Return
            _onlineLastName = name : _onlineLastRelease = projectKey & ":" & snapshot.Id
            Await RunOnlinePackInstallationAsync(snapshot, root, name, CLng(limit * 1024 * 1024), displayedTitle, target)
        Catch ex As Exception
            If Not _closing Then StatusText.Text = ex.GetBaseException().Message
            If Not _preview Then Log(ex)
        End Try
    End Sub
    Private Shared Function SuggestedPackName(name As String) As String
        Dim result = name
        For Each invalid In IO.Path.GetInvalidFileNameChars() : result = result.Replace(invalid, "_"c) : Next
        result = result.Trim().TrimEnd("."c)
        If result.Length > 65 Then result = result.Substring(0, 65).TrimEnd()
        Return result
    End Function
    Private Function PromptOnlinePackAsync(title As String, description As String, root As String, proposed As String, optionalFile As Boolean, token As CancellationToken, Optional returnTarget As FrameworkElement = Nothing) As Task(Of String)
        If _onlinePrompt IsNot Nothing Then Throw New InvalidOperationException("已有待确认的整合包操作。")
        token.ThrowIfCancellationRequested()
        BeginPromptFocus(returnTarget)
        _onlinePrompt = New TaskCompletionSource(Of String)(TaskCreationOptions.RunContinuationsAsynchronously)
        _onlinePromptRoot = root : _onlineOptionalPrompt = optionalFile
        ResourceTargetChoices.Visibility = If(_resourceTargetPrompt, Visibility.Visible, Visibility.Collapsed)
        StopOnlinePromptMotion()
        Dim previousExit = _onlinePromptClosed
        _onlinePromptClosed = New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        previousExit?.TrySetResult(False)
        Dim shade = If(PackNameOverlay.IsVisible AndAlso _onlinePromptShade IsNot Nothing, _onlinePromptShade.Color, Color.FromArgb(0, 0, 0, 0))
        PackNameTitle.Text = title : PackNameDescription.Text = description
        OnlinePromptBody.StopScrolling() : OnlinePromptBody.ScrollToHome()
        OnlinePackName.Visibility = If(optionalFile, Visibility.Collapsed, Visibility.Visible)
        OnlinePackName.Text = If(proposed, "") : OnlinePackNameHint.Visibility = Visibility.Collapsed : OnlinePackNameHint.Theme = PclHintTheme.Red
        OnlinePackConfirm.Text = If(optionalFile, "是", "确定") : OnlinePackCancel.Text = If(optionalFile, "否", "取消")
        PageSurface.IsEnabled = False : TopTabs.IsEnabled = False : PackNameOverlay.Visibility = Visibility.Visible
        PackNameDialog.IsHitTestVisible = True
        _onlinePromptRotation = New RotateTransform(0) : _onlinePromptOffset = New TranslateTransform()
        Dim transforms As New TransformGroup() : transforms.Children.Add(_onlinePromptRotation) : transforms.Children.Add(_onlinePromptOffset)
        PackNameDialog.RenderTransform = transforms
        _onlinePromptShade = New SolidColorBrush(shade) : PackNameOverlay.Background = _onlinePromptShade
        AnimateOnlinePrompt(True)
        ValidateOnlinePackName()
        If optionalFile Then OnlinePackConfirm.Focus() Else OnlinePackName.Focus() : OnlinePackName.SelectAll()
        Return _onlinePrompt.Task
    End Function
    Private Sub ValidateOnlinePackName()
        If _onlinePrompt Is Nothing Then Return
        If _resourceTargetPrompt Then ValidateResourceTarget() : Return
        Dim problem As String = Nothing
        If Not _onlineOptionalPrompt Then
            Try
                InstallPreflight.ValidateDestination(_onlinePromptRoot, OnlinePackName.Text, False)
            Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                problem = ex.Message
            End Try
        End If
        OnlinePackName.ValidationMessage = If(problem, "") : OnlinePackNameHint.Text = If(problem, "")
        OnlinePackNameHint.Visibility = If(problem Is Nothing, Visibility.Collapsed, Visibility.Visible)
        OnlinePackConfirm.IsEnabled = problem Is Nothing
    End Sub
    Private Sub ConfirmOnlinePackName(sender As Object, e As RoutedEventArgs)
        If _resourceTargetPrompt Then ConfirmResourceTargetAsync() : Return
        If _onlinePrompt Is Nothing OrElse Not OnlinePackConfirm.IsEnabled Then Return
        ValidateOnlinePackName()
        If OnlinePackConfirm.IsEnabled Then CompleteOnlinePrompt(If(_onlineOptionalPrompt, "yes", OnlinePackName.Text))
    End Sub
    Private Sub CancelOnlinePackName(sender As Object, e As RoutedEventArgs)
        CompleteOnlinePrompt(If(_onlineOptionalPrompt, "no", Nothing))
    End Sub
    Private Sub CompleteOnlinePrompt(value As String)
        ' MyMsgText.IsExited ignores repeated buttons while Close still owns
        ' its animation. Explicit shutdown/reduced motion can still settle it.
        If _onlinePrompt Is Nothing AndAlso AllowPageMotion() Then Return
        Dim prompt = _onlinePrompt : _onlinePrompt = Nothing
        Dim worldRequest As CancellationTokenSource = Nothing, targetRequest As CancellationTokenSource = Nothing
        If _resourceTargetPrompt Then
            _resourceTargetPrompt = False
            worldRequest = _resourceWorldRequest : targetRequest = _resourceTargetRequest
            _resourceWorldRequest = Nothing : _resourceTargetRequest = Nothing
            _resourceWorldLoading = False
        End If
        If prompt Is Nothing OrElse Not AllowPageMotion() Then
            StopOnlinePromptMotion() : HideOnlinePrompt()
        Else
            PackNameDialog.IsHitTestVisible = False
            AnimateOnlinePrompt(False)
        End If
        ' Cancel can synchronously reenter and open another picker. Retire all
        ' ownership and start this exit before notifying only captured requests.
        ' Finish the old result even if a cancellation callback fails.
        Try
            worldRequest?.Cancel()
        Finally
            Try
                targetRequest?.Cancel()
            Finally
                prompt?.TrySetResult(value)
            End Try
        End Try
    End Sub
    Private Sub HideOnlinePrompt()
        Dim restore = _promptFocusReturn : _promptFocusReturn = Nothing
        Dim closed = _onlinePromptClosed : _onlinePromptClosed = Nothing
        PackNameOverlay.Visibility = Visibility.Collapsed : PageSurface.IsEnabled = True : TopTabs.IsEnabled = True
        ' Parent enable notifications can settle the floating action bar before
        ' its rows have inherited the new value. Recompute gates before focus.
        If LocalModsPage.IsVisible Then RefreshLocalModAvailability()
        QueuePromptFocus(restore)
        closed?.TrySetResult(True)
    End Sub
    Private Sub StopOnlinePromptMotion()
        Dim previous = _onlinePromptMotion : _onlinePromptMotion = Nothing
        Dim opacity = PackNameDialog.Opacity, angle = If(_onlinePromptRotation?.Angle, 0), y = If(_onlinePromptOffset?.Y, 0)
        Dim shade = If(_onlinePromptShade?.Color, Color.FromArgb(90, 0, 0, 0))
        PackNameDialog.ApplyAnimationClock(OpacityProperty, Nothing) : PackNameDialog.Opacity = opacity
        If _onlinePromptRotation IsNot Nothing Then _onlinePromptRotation.ApplyAnimationClock(RotateTransform.AngleProperty, Nothing) : _onlinePromptRotation.Angle = angle
        If _onlinePromptOffset IsNot Nothing Then _onlinePromptOffset.ApplyAnimationClock(TranslateTransform.YProperty, Nothing) : _onlinePromptOffset.Y = y
        If _onlinePromptShade IsNot Nothing Then _onlinePromptShade.ApplyAnimationClock(SolidColorBrush.ColorProperty, Nothing) : _onlinePromptShade.Color = shade
        previous?.Controller.Remove()
    End Sub
    Private Sub AnimateOnlinePrompt(entering As Boolean)
        StopOnlinePromptMotion()
        If Not AllowPageMotion() Then
            SettleOnlinePromptVisual(entering)
            If Not entering Then HideOnlinePrompt()
            Return
        End If
        Dim timeline As New ParallelTimeline()
        If entering Then
            PackNameDialog.Opacity = 0 : _onlinePromptRotation.Angle = -4 : _onlinePromptOffset.Y = 40
            timeline.Children.Add(New DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)) With {.BeginTime = TimeSpan.FromMilliseconds(60)})
            timeline.Children.Add(New PclDialogYAnimation With {.BeginTime = TimeSpan.FromMilliseconds(60)})
            timeline.Children.Add(New DoubleAnimation(-4, 0, TimeSpan.FromMilliseconds(300)) With {.BeginTime = TimeSpan.FromMilliseconds(60), .EasingFunction = New PowerEase With {.Power = 2, .EasingMode = EasingMode.EaseOut}})
        Else
            timeline.Children.Add(New DoubleAnimation(PackNameDialog.Opacity, 0, TimeSpan.FromMilliseconds(80)) With {.BeginTime = TimeSpan.FromMilliseconds(20)})
            timeline.Children.Add(New DoubleAnimation(_onlinePromptOffset.Y, 20, TimeSpan.FromMilliseconds(150)) With {.EasingFunction = New PowerEase With {.Power = 3, .EasingMode = EasingMode.EaseOut}})
            timeline.Children.Add(New DoubleAnimation(_onlinePromptRotation.Angle, 6, TimeSpan.FromMilliseconds(150)) With {.EasingFunction = New PowerEase With {.Power = 2, .EasingMode = EasingMode.EaseIn}})
        End If
        ' MyMsgText.Close starts the Weak Fluent shade exit after 30 ms.
        ' A replacement enters from the displayed shade, never transparent.
        timeline.Children.Add(New ColorAnimation(_onlinePromptShade.Color, If(entering, Color.FromArgb(90, 0, 0, 0), Color.FromArgb(0, 0, 0, 0)), TimeSpan.FromMilliseconds(200)) With {
            .BeginTime = TimeSpan.FromMilliseconds(If(entering, 0, 30)),
            .EasingFunction = If(entering, Nothing, New QuadraticEase With {.EasingMode = EasingMode.EaseOut})})
        Dim clock = DirectCast(timeline.CreateClock(True), ClockGroup)
        _onlinePromptMotion = clock
        AddHandler clock.Completed, Sub()
                                        If _onlinePromptMotion IsNot clock Then Return
                                        StopOnlinePromptMotion()
                                        ' Completed can precede the last dependency-property
                                        ' invalidation. Do not freeze the previous rendered frame.
                                        SettleOnlinePromptVisual(entering)
                                        If Not entering AndAlso _onlinePrompt Is Nothing Then HideOnlinePrompt()
                                    End Sub
        PackNameDialog.ApplyAnimationClock(OpacityProperty, DirectCast(clock.Children(0), AnimationClock))
        _onlinePromptOffset.ApplyAnimationClock(TranslateTransform.YProperty, DirectCast(clock.Children(1), AnimationClock))
        _onlinePromptRotation.ApplyAnimationClock(RotateTransform.AngleProperty, DirectCast(clock.Children(2), AnimationClock))
        _onlinePromptShade.ApplyAnimationClock(SolidColorBrush.ColorProperty, DirectCast(clock.Children(3), AnimationClock))
    End Sub
    Private Sub SettleOnlinePromptVisual(entering As Boolean)
        PackNameDialog.Opacity = If(entering, 1.0, 0.0)
        _onlinePromptOffset.Y = If(entering, 0.0, 20.0)
        _onlinePromptRotation.Angle = If(entering, 0.0, 6.0)
        _onlinePromptShade.Color = If(entering, Color.FromArgb(90, 0, 0, 0), Color.FromArgb(0, 0, 0, 0))
    End Sub
    Private Async Function ChooseOnlineOptionalAsync(package As ModrinthPackage, token As CancellationToken) As Task(Of IEnumerable(Of String))
        Dim selected As New List(Of String)
        For Each file In package.References.Where(Function(item) item.Client = "optional")
            token.ThrowIfCancellationRequested()
            Dim pending = PromptOnlinePackAsync("下载可选文件", "是否要下载可选文件 " & IO.Path.GetFileName(file.Path) & "？" & vbCrLf & file.Path, Nothing, Nothing, True, token)
            Dim owner = _onlinePrompt
            Using registration = token.Register(Sub() Dispatcher.BeginInvoke(New Action(Sub()
                                                                                            If _onlinePrompt Is owner Then CompleteOnlinePrompt(Nothing)
                                                                                        End Sub)))
                Dim answer = Await pending
                token.ThrowIfCancellationRequested()
                If answer = "yes" Then selected.Add(file.Path)
            End Using
        Next
        Return selected
    End Function
    Private Async Function RunOnlinePackInstallationAsync(release As ModpackRelease, root As String, name As String, limit As Long, Optional displayedTitle As String = Nothing, Optional target As LauncherInstallTarget = Nothing) As Task
        target = If(target, CurrentInstallTarget())
        If Not SameGameRoot(root, target.Root) Then Throw New InvalidOperationException("安装目录与当前任务不一致，请重新选择。")
        target.ValidateDestination(root)
        InstallPreflight.ValidateDestination(root, name, False)
        CancelPackRead()
        _packSearchRequest?.Cancel() : _packDetailRequest?.Cancel()
        Dim policySnapshot = Policy(), reuse = If(ReuseBox.IsChecked = True, _folders.Select(Function(folder) folder.Location).ToList(), New List(Of String))
        Dim connections = ConnectionCount(), javaPath = JavaPathBox.Text.Trim()
        Dim request As New CancellationTokenSource : _cancellation = request : _busy = True
        _taskInstallTarget = target
        SetBusy(True) : PersistSettings() : TaskPanel.Visibility = Visibility.Visible
        ResetTaskView(name, True, root, hasArchive:=True)
        _taskMinecraft = String.Join("、", release.GameVersions) : _taskComponents = release.Platform.ToString() & " " & PackReleaseConfirmation(release, displayedTitle).Replace(vbCrLf, " · ")
        _taskRows("metadata").Label.Text = "读取整合包清单与游戏版本信息" : _taskRows("files").Label.Text = "下载游戏、资源与整合包文件"
        ShowProgress(Nothing, Nothing)
        Try
            Dim progress = CreateTaskProgress(), result As InstallResult
            If _onlineInstallRunner IsNot Nothing Then
                result = Await _onlineInstallRunner(release, root, name, progress, request.Token)
            ElseIf _preview Then
                Throw New InvalidOperationException("界面预览不会执行实际安装。")
            Else
                result = Await Task.Run(Function() ModpackArchiveInstaller.InstallAsync(release, root, name, policySnapshot, target.PclDirectory, reuse, progress, request.Token, connections, limit, javaPath,
                    Function(package, token) Dispatcher.InvokeAsync(Function() ChooseOnlineOptionalAsync(package, token)).Task.Unwrap(), launcherTarget:=target))
            End If
            _taskInstancePath = result.InstancePath : TaskTitle.Text = "安装完成 · " & name
            StatusText.Text = "完成：" & result.FileCount & " 个文件。请刷新启动器的版本列表。"
            FinishTaskView(True, StatusText.Text)
        Catch ex As OperationCanceledException
            TaskTitle.Text = "已取消，可重新开始" : StatusText.Text = "已取消，重试时会校验复用已下载文件。"
            FinishTaskView(False, StatusText.Text, cancelled:=True)
        Catch ex As Exception
            TaskTitle.Text = "安装未完成，可重试" : StatusText.Text = ex.GetBaseException().Message
            FinishTaskView(False, StatusText.Text, cancelled:=request.IsCancellationRequested)
            If Not _preview Then Log(ex)
        Finally
            CompleteOnlinePrompt(Nothing)
            _busy = False : _cancellation = Nothing : request.Dispose() : SetBusy(False)
        End Try
    End Function
End Class
