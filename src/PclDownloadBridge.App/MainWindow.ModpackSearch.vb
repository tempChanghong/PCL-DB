Imports System.IO
Imports System.Net.Http
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports System.Windows.Media.Imaging
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
    Private _onlinePromptRoot As String
    Private _onlineOptionalPrompt As Boolean
    Private _onlinePromptMotion As ClockGroup
    Private _onlinePromptRotation As RotateTransform
    Private _onlinePromptOffset As TranslateTransform
    Private _onlinePromptShade As SolidColorBrush
    Private _choosingOnline As Boolean
    Private _onlineLastName As String
    Private _onlineLastRelease As String
    Private _packCatalogFactory As Func(Of ModpackCatalogClient) = Function() New ModpackCatalogClient()
    Private _onlineInstallRunner As Func(Of ModpackRelease, String, String, IProgress(Of TransferProgress), CancellationToken, Task(Of InstallResult))
    Private ReadOnly _packIcons As New Dictionary(Of String, BitmapSource)(StringComparer.Ordinal)
    Private ReadOnly _packIconSlots As New SemaphoreSlim(4)

    Private Sub InitializePackSearch()
        _packResultMotion = New PclSurfaceMotion(PackSearchResultsHost, PackSearchGhost,
            Function() AllowPageMotion() AndAlso _downloadView = DownloadView.Modpacks AndAlso _packMode = PackBrowserMode.Search AndAlso DownloadPage.IsVisible AndAlso _categoryMotion.Transition Is Nothing AndAlso _surfaceMotion.Transition Is Nothing,
            Function(surface) PclSurfaceMotion.Units(surface))
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
        AddHandler PackSearchText.KeyDown, AddressOf PackSearchEnter
        AddHandler PackSearchGame.KeyDown, AddressOf PackSearchEnter
        AddHandler OnlinePackName.TextChanged, Sub() ValidateOnlinePackName()
        AddHandler OnlinePackName.KeyDown, Sub(sender, e)
                                                If e.Key = Key.Enter AndAlso Not e.IsRepeat Then e.Handled = True : ConfirmOnlinePackName(sender, e)
                                            End Sub
        PackProjectRows.Children.Add(New TextBlock With {.Text = "输入名称或选择条件后搜索整合包。", .Margin = New Thickness(10)})
    End Sub
    Private Sub EnsurePackSearch()
        If Not _preview AndAlso Not _busy AndAlso Not _closing AndAlso _packSearchSession Is Nothing AndAlso _downloadView = DownloadView.Modpacks AndAlso _packMode = PackBrowserMode.Search AndAlso DownloadPage.IsVisible AndAlso DownloadViewHost.IsHitTestVisible AndAlso PageSurface.IsHitTestVisible Then RunPackSearch(Nothing, Nothing)
    End Sub
    Private Sub PackSearchEnter(sender As Object, e As KeyEventArgs)
        If e.Key <> Key.Enter OrElse e.IsRepeat Then Return
        e.Handled = True : RunPackSearch(sender, e)
    End Sub
    Private Sub ChangePackMode(mode As PackBrowserMode)
        If _packMode = mode Then Return
        If _packMode = PackBrowserMode.Search Then _packSearchScroll = ModpackPage.VerticalOffset
        If _packMode = PackBrowserMode.Detail Then _packDetailScroll = ModpackPage.VerticalOffset
        ModpackPage.StopScrolling()
        Dim apply As Action = Sub()
                                  _packResultMotion.Finish()
                                  _packMode = mode
                                  PackSearchSurface.Visibility = If(mode = PackBrowserMode.Search, Visibility.Visible, Visibility.Collapsed)
                                  PackDetailSurface.Visibility = If(mode = PackBrowserMode.Detail, Visibility.Visible, Visibility.Collapsed)
                                  PackLocalSurface.Visibility = If(mode = PackBrowserMode.Local, Visibility.Visible, Visibility.Collapsed)
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
        If _busy OrElse _choosingOnline Then Return
        ChangePackMode(PackBrowserMode.Local)
    End Sub
    Private Sub BackToPackSearch(sender As Object, e As RoutedEventArgs)
        If _choosingOnline Then Return
        ChangePackMode(PackBrowserMode.Search)
    End Sub
    Private Async Sub RunPackSearch(sender As Object, e As RoutedEventArgs)
        If _busy OrElse _choosingOnline OrElse _closing OrElse Not PageSurface.IsHitTestVisible OrElse Not DownloadViewHost.IsHitTestVisible Then Return
        _packSearchRequest?.Cancel()
        _packDetailRequest?.Cancel()
        Dim game = PackSearchGameValue(), category = TryCast(PackSearchCategory.SelectedItem, ComboBoxItem)
        Dim query As New ModpackQuery With {.Text = PackSearchText.Text.Trim(), .Minecraft = game,
            .Platforms = If(PackSearchSource.SelectedIndex = 1, ModpackPlatform.CurseForge, If(PackSearchSource.SelectedIndex = 2, ModpackPlatform.Modrinth, ModpackPlatform.All)),
            .Category = If(category Is Nothing, "/", CStr(category.Tag))}
        Dim key = query.Platforms.ToString() & "|" & query.Category & "|" & query.Minecraft & "|" & query.Text
        Try
            If _packSearchSession Is Nothing OrElse key <> _packLastQuery OrElse _packSearchSession.ErrorMessage <> "" OrElse DateTime.UtcNow - _packLastSearch > TimeSpan.FromSeconds(60) Then _packSearchSession = New ModpackSearchSession(query)
        Catch ex As ArgumentException
            PackSearchHint.Text = ex.Message : PackSearchHint.Visibility = Visibility.Visible : Return
        End Try
        _packLastQuery = key : _packLastSearch = DateTime.UtcNow
        _packProject = Nothing : _packReleases.Clear()
        _packSearchPage = 0 : _packSearchScroll = 0
        ChangePackMode(PackBrowserMode.Search)
        Await LoadPackSearchPageAsync(0)
    End Sub
    Private Sub ResetPackSearch(sender As Object, e As RoutedEventArgs)
        If _busy OrElse _choosingOnline Then Return
        PackSearchText.Text = "" : PackSearchGame.SelectedIndex = 0 : PackSearchSource.SelectedIndex = 0 : PackSearchCategory.SelectedIndex = 0
        PackSearchGame.Text = "全部 (也可自行输入)"
        _packLastQuery = Nothing
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
        If _packMode = PackBrowserMode.Search AndAlso _downloadView = DownloadView.Modpacks AndAlso DownloadPage.IsVisible AndAlso _categoryMotion.Transition Is Nothing AndAlso _surfaceMotion.Transition Is Nothing Then
            _packResultMotion.Change(PackSearchResultsSurface, PackSearchResultsSurface, update, contentRefresh:=True)
        Else
            update()
        End If
    End Sub
    Private Async Function LoadPackSearchPageAsync(page As Integer) As Task
        Dim session = _packSearchSession
        If session Is Nothing OrElse _busy OrElse _closing OrElse _choosingOnline Then Return
        _packSearchRequest?.Cancel()
        _packArtworkRequest?.Cancel() : _packArtworkRequest?.Dispose()
        _packArtworkRequest = New CancellationTokenSource()
        Dim artworkToken = _packArtworkRequest.Token
        Dim request As New CancellationTokenSource : _packSearchRequest = request
        PackFirstPage.IsEnabled = False : PackPreviousPage.IsEnabled = False : PackNextPage.IsEnabled = False
        ChangePackResults(Sub()
                              PackSearchHint.Visibility = Visibility.Collapsed : PackPagingCard.Visibility = Visibility.Collapsed
                              PackProjectRows.Children.Clear() : PackResultsCard.Title = "正在获取整合包列表…"
                              PackResultsCard.HorizontalAlignment = HorizontalAlignment.Center
                              PackProjectRows.Children.Add(New PclLoadingView("正在获取整合包列表"))
                          End Sub)
        Try
            Dim items As List(Of ModpackProject)
            Using client = _packCatalogFactory()
                items = Await session.GetPageAsync(page, client, request.Token)
            End Using
            If Not CurrentPackSearch(session, request) Then Return
            _packSearchPage = page
            ChangePackResults(Sub()
                                  PackProjectRows.Children.Clear() : PackResultsCard.Title = "整合包"
                                  PackResultsCard.HorizontalAlignment = HorizontalAlignment.Stretch
                                  For Each project In items
                                      Dim row As New PclResourceItem(project) With {.Tag = project}
                                      AddHandler row.Click, Sub() OpenPackDetails(project)
                                      PackProjectRows.Children.Add(row)
                                      LoadPackIconAsync(row, project, artworkToken)
                                  Next
                                  If items.Count = 0 Then PackProjectRows.Children.Add(New TextBlock With {.Text = If(session.ErrorMessage = "", "没有找到符合条件的整合包。", "未能取得结果，请重试或选择其他来源。"), .Margin = New Thickness(10), .TextWrapping = TextWrapping.Wrap})
                                  PackSearchHint.Theme = If(items.Count > 0, PclHintTheme.Yellow, PclHintTheme.Red)
                                  PackSearchHint.Text = If(items.Count > 0, "部分来源未能完成查询，目前仅显示可用来源的内容。点击搜索可重试。", "无法取得整合包列表，请稍后重试或选择其他来源。")
                                  PackSearchHint.ToolTip = session.ErrorMessage
                                  PackSearchHint.Visibility = If(session.ErrorMessage = "", Visibility.Collapsed, Visibility.Visible)
                                  PackPagingCard.Visibility = If(session.Count > 0, Visibility.Visible, Visibility.Collapsed)
                                  PackPageLabel.Text = (page + 1).ToString()
                                  PackFirstPage.IsEnabled = page > 0 : PackPreviousPage.IsEnabled = page > 0 : PackNextPage.IsEnabled = session.HasNext(page)
                                  PackNextPage.Text = If(session.Count < (page + 1) * ModpackCatalogClient.PageSize AndAlso session.HasMore, "继续加载", "下一页")
                              End Sub)
            If _packMode = PackBrowserMode.Search AndAlso _downloadView = DownloadView.Modpacks Then ModpackPage.ScrollToHome()
        Catch ex As OperationCanceledException
            ' A newer query or closing owns the surface now.
        Catch ex As Exception
            If CurrentPackSearch(session, request) Then
                PackResultsCard.Title = "获取失败" : PackProjectRows.Children.Clear()
                PackSearchHint.Text = ex.GetBaseException().Message : PackSearchHint.Visibility = Visibility.Visible
            End If
        Finally
            If _packSearchRequest Is request Then _packSearchRequest = Nothing
            request.Dispose()
        End Try
    End Function
    Private Function CurrentPackSearch(session As ModpackSearchSession, request As CancellationTokenSource) As Boolean
        Return Not _closing AndAlso Not request.IsCancellationRequested AndAlso _packSearchRequest Is request AndAlso _packSearchSession Is session
    End Function
    Private Async Sub LoadPackIconAsync(row As PclResourceItem, project As ModpackProject, token As CancellationToken)
        Dim entered = False
        Try
            If String.IsNullOrWhiteSpace(project.IconUrl) Then Return
            Dim cached As BitmapSource = Nothing
            If _packIcons.TryGetValue(project.IconUrl, cached) Then row.Icon.Source = cached : Return
            Dim address As Uri = Nothing
            If Not Uri.TryCreate(project.IconUrl, UriKind.Absolute, address) OrElse address.Scheme <> "https" OrElse Not address.IsDefaultPort OrElse address.UserInfo <> "" OrElse Not {"cdn.modrinth.com", "media.forgecdn.net", "mediafilez.forgecdn.net"}.Contains(address.Host, StringComparer.OrdinalIgnoreCase) Then Return
            Await _packIconSlots.WaitAsync(token) : entered = True
            Using client As New HttpClient(New HttpClientHandler With {.AllowAutoRedirect = False}) With {.Timeout = TimeSpan.FromSeconds(12)}, response = Await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, token)
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
                        token.ThrowIfCancellationRequested() : bytes.Position = 0
                        Dim bitmap As New BitmapImage()
                        bitmap.BeginInit() : bitmap.CacheOption = BitmapCacheOption.OnLoad : bitmap.DecodePixelWidth = 100 : bitmap.StreamSource = bytes : bitmap.EndInit() : bitmap.Freeze()
                        If _packIcons.Count >= 160 Then _packIcons.Clear()
                        _packIcons(project.IconUrl) = bitmap
                        If Not _closing AndAlso Not token.IsCancellationRequested AndAlso row.Tag Is project Then row.Icon.Source = bitmap
                    End Using
                End Using
            End Using
        Catch
            ' Artwork is optional and never changes the query/install state.
        Finally
            If entered Then _packIconSlots.Release()
        End Try
    End Sub
    Private Async Sub OpenPackDetails(project As ModpackProject)
        If _busy OrElse _choosingOnline OrElse _closing OrElse Not DownloadViewHost.IsHitTestVisible OrElse Not PackSearchResultsHost.IsHitTestVisible Then Return
        If _packProject Is Nothing OrElse _packProject.Key <> project.Key Then
            _packProject = project : _packReleases.Clear() : _packDetailScroll = 0
            _packGameFilter = PackSearchGameValue()
        End If
        PackProjectTitle.Text = project.Name : PackProjectDescription.Text = project.Summary
        If PackProjectIdentity.Children.Count = 0 OrElse DirectCast(PackProjectIdentity.Children(0), PclResourceItem).Tag IsNot project Then
            PackProjectIdentity.Children.Clear()
            Dim intro As New PclResourceItem(project) With {.Tag = project, .IsHitTestVisible = False, .Focusable = False}
            PackProjectIdentity.Children.Add(intro)
            LoadPackIconAsync(intro, project, If(_packArtworkRequest Is Nothing, CancellationToken.None, _packArtworkRequest.Token))
        End If
        PackProjectWebsite.Text = "转到 " & project.Platform.ToString()
        ChangePackMode(PackBrowserMode.Detail)
        If _packReleases.Count = 0 Then Await LoadPackDetailsAsync(project)
    End Sub
    Private Async Sub RetryPackDetails(sender As Object, e As RoutedEventArgs)
        If _packProject IsNot Nothing AndAlso Not _busy Then Await LoadPackDetailsAsync(_packProject)
    End Sub
    Private Async Function LoadPackDetailsAsync(project As ModpackProject) As Task
        _packDetailRequest?.Cancel()
        Dim request As New CancellationTokenSource : _packDetailRequest = request
        PackVersionCards.Children.Clear() : PackVersionFilters.Children.Clear()
        PackVersionFilterCard.Visibility = Visibility.Collapsed
        PackDetailHint.Visibility = Visibility.Collapsed : PackDetailRetry.Visibility = Visibility.Collapsed
        Dim loading As New PclCard With {.ShowHeader = False, .HorizontalAlignment = HorizontalAlignment.Center, .BodyMargin = New Thickness(0), .Body = New PclLoadingView("正在获取版本列表")}
        PackVersionCards.Children.Add(loading)
        Try
            Using client = _packCatalogFactory()
                Dim verified = Await client.GetProjectAsync(project.Platform, project.Id, request.Token)
                Dim releases = Await client.GetVersionsAsync(verified, request.Token)
                If _closing OrElse request.IsCancellationRequested OrElse _packDetailRequest IsNot request OrElse _packProject IsNot project Then Return
                PackProjectDescription.Text = verified.Summary : _packReleases = releases
            End Using
            PackDetailHint.Visibility = Visibility.Collapsed : RenderPackVersions()
        Catch ex As OperationCanceledException
        Catch ex As Exception
            If Not _closing AndAlso Not request.IsCancellationRequested AndAlso _packDetailRequest Is request AndAlso _packProject Is project Then
                PackVersionCards.Children.Clear()
                PackDetailHint.Theme = PclHintTheme.Red : PackDetailHint.Text = "无法获取可安装的客户端版本，请重试。" : PackDetailHint.ToolTip = ex.GetBaseException().Message : PackDetailHint.Visibility = Visibility.Visible : PackDetailRetry.Visibility = Visibility.Visible
            End If
        Finally
            If _packDetailRequest Is request Then _packDetailRequest = Nothing
            request.Dispose()
        End Try
    End Function
    Private Sub RenderPackVersions()
        PackVersionFilters.Children.Clear() : PackVersionCards.Children.Clear()
        PackVersionFilterCard.Visibility = Visibility.Visible
        Dim games = _packReleases.SelectMany(Function(release) release.GameVersions).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(Function(value) value, New MinecraftGameComparer()).ToList()
        For Each game In New String() {""}.Concat(games)
            Dim filter = game
            Dim button As New PclButton With {.Text = If(game = "", "全部", game), .Height = 28, .MinWidth = 66, .Margin = New Thickness(0, 0, 8, 6), .Highlight = game = _packGameFilter}
            AddHandler button.Click, Sub()
                                         If _busy OrElse _choosingOnline Then Return
                                         _packGameFilter = filter : RenderPackVersions()
                                     End Sub
            PackVersionFilters.Children.Add(button)
        Next
        Dim visible = _packReleases.Where(Function(release) _packGameFilter = "" OrElse release.GameVersions.Contains(_packGameFilter, StringComparer.OrdinalIgnoreCase)).ToList()
        Dim groups = visible.GroupBy(Function(release) If(release.GameVersions.FirstOrDefault(), "其他版本")).ToList()
        For Each group In groups
            Dim versions = group.ToList()
            Dim card As New PclCard With {.Title = group.Key & "（" & versions.Count & "）", .Collapsible = True, .BodyMargin = New Thickness(15, 40, 15, 15)}
            card.Collapsed = groups.Count > 1
            card.Builder = Function()
                               Dim rows As New StackPanel
                               For Each release In versions
                                   Dim selected = release
                                   Dim row As New PclListItem With {.Height = 42, .Title = PackReleaseTitle(release.Name), .Info = String.Join("、", release.Loaders) & If(release.Loaders.Length > 0, "，", "") & release.Published.ToLocalTime().ToString("yyyy/MM/dd") & "，" & release.ReleaseType, .Tag = release,
                                       .Logo = "pack://application:,,,/Images/Blocks/" & If(release.ReleaseType = "release", "Release", If(release.ReleaseType = "beta", "Beta", "Alpha")) & ".png"}
                                   AddHandler row.Click, Sub() InstallOnlinePack(selected)
                                   rows.Children.Add(row)
                               Next
                               Return rows
                           End Function
            If Not card.Collapsed Then card.Body = card.Builder.Invoke()
            PackVersionCards.Children.Add(card)
        Next
        If visible.Count = 0 Then
            PackDetailHint.Text = "没有适用于当前筛选条件、且具备下载地址及校验信息的客户端整合包。" : PackDetailHint.Visibility = Visibility.Visible
        Else
            PackDetailHint.Visibility = Visibility.Collapsed
        End If
    End Sub
    Private Function PackSearchGameValue() As String
        Dim value = PackSearchGame.Text.Trim()
        Return If(value.StartsWith("全部", StringComparison.Ordinal), "", value)
    End Function
    Private Shared Function PackReleaseTitle(value As String) As String
        For Each extension In {".zip", ".mrpack"}
            If value.EndsWith(extension, StringComparison.OrdinalIgnoreCase) Then Return value.Substring(0, value.Length - extension.Length)
        Next
        Return value
    End Function
    Private Class MinecraftGameComparer
        Implements IComparer(Of String)
        Public Function Compare(x As String, y As String) As Integer Implements IComparer(Of String).Compare
            Dim a As Version = Nothing, b As Version = Nothing
            If Version.TryParse(x, a) AndAlso Version.TryParse(y, b) Then Return a.CompareTo(b)
            Return StringComparer.OrdinalIgnoreCase.Compare(x, y)
        End Function
    End Class
    Private Sub OpenPackProjectWebsite(sender As Object, e As RoutedEventArgs)
        If _packProject Is Nothing OrElse _preview Then Return
        System.Diagnostics.Process.Start(New System.Diagnostics.ProcessStartInfo(_packProject.Website) With {.UseShellExecute = True})
    End Sub
    Private Sub CopyPackProjectName(sender As Object, e As RoutedEventArgs)
        If _packProject IsNot Nothing Then Clipboard.SetText(_packProject.Name)
    End Sub

    Private Async Sub InstallOnlinePack(release As ModpackRelease)
        If _busy OrElse _choosingOnline OrElse _readingPack OrElse _closing OrElse _packProject Is Nothing OrElse _packMode <> PackBrowserMode.Detail OrElse _downloadView <> DownloadView.Modpacks OrElse Not DownloadPage.IsVisible OrElse Not PageSurface.IsHitTestVisible OrElse Not DownloadViewHost.IsHitTestVisible Then Return
        If _selectedFolder Is Nothing Then ChooseInstallFolder(Nothing, Nothing) : StatusText.Text = "请先选择游戏目录。" : Return
        Dim limit As Double
        If Not TryGetSpeedLimit(limit) Then NavigateToSetting(SpeedLimitBox, True) : Return
        Dim project = _packProject, snapshot = release.Copy(), root = _selectedFolder.Location
        If snapshot.Platform <> project.Platform OrElse snapshot.ProjectId <> project.Id Then Return
        _choosingOnline = True
        Try
            Dim proposed = If(_onlineLastRelease = project.Key & ":" & release.Id, _onlineLastName, SuggestedPackName(project.Name))
            Dim name = Await PromptOnlinePackAsync("输入版本名称", project.Name & " · " & release.Name, root, proposed, False, CancellationToken.None)
            If String.IsNullOrEmpty(name) OrElse _closing Then Return
            _onlineLastName = name : _onlineLastRelease = project.Key & ":" & release.Id
            Await RunOnlinePackInstallationAsync(snapshot, root, name, CLng(limit * 1024 * 1024))
        Catch ex As Exception
            If Not _closing Then StatusText.Text = ex.GetBaseException().Message
            If Not _preview Then Log(ex)
        Finally
            _choosingOnline = False
        End Try
    End Sub
    Private Shared Function SuggestedPackName(name As String) As String
        Dim result = name
        For Each invalid In IO.Path.GetInvalidFileNameChars() : result = result.Replace(invalid, "_"c) : Next
        result = result.Trim().TrimEnd("."c)
        If result.Length > 65 Then result = result.Substring(0, 65).TrimEnd()
        Return result
    End Function
    Private Function PromptOnlinePackAsync(title As String, description As String, root As String, proposed As String, optionalFile As Boolean, token As CancellationToken) As Task(Of String)
        If _onlinePrompt IsNot Nothing Then Throw New InvalidOperationException("已有待确认的整合包操作。")
        token.ThrowIfCancellationRequested()
        _onlinePrompt = New TaskCompletionSource(Of String)(TaskCreationOptions.RunContinuationsAsynchronously)
        _onlinePromptRoot = root : _onlineOptionalPrompt = optionalFile
        StopOnlinePromptMotion()
        PackNameTitle.Text = title : PackNameDescription.Text = description
        OnlinePackName.Visibility = If(optionalFile, Visibility.Collapsed, Visibility.Visible)
        OnlinePackName.Text = If(proposed, "") : OnlinePackNameHint.Visibility = Visibility.Collapsed
        OnlinePackConfirm.Text = If(optionalFile, "是", "确定") : OnlinePackCancel.Text = If(optionalFile, "否", "取消")
        PageSurface.IsEnabled = False : TopTabs.IsEnabled = False : PackNameOverlay.Visibility = Visibility.Visible
        PackNameDialog.IsHitTestVisible = True
        _onlinePromptRotation = New RotateTransform(0) : _onlinePromptOffset = New TranslateTransform()
        Dim transforms As New TransformGroup() : transforms.Children.Add(_onlinePromptRotation) : transforms.Children.Add(_onlinePromptOffset)
        PackNameDialog.RenderTransform = transforms
        _onlinePromptShade = New SolidColorBrush(Color.FromArgb(90, 0, 0, 0)) : PackNameOverlay.Background = _onlinePromptShade
        AnimateOnlinePrompt(True)
        ValidateOnlinePackName()
        If optionalFile Then OnlinePackConfirm.Focus() Else OnlinePackName.Focus() : OnlinePackName.SelectAll()
        Return _onlinePrompt.Task
    End Function
    Private Sub ValidateOnlinePackName()
        If _onlinePrompt Is Nothing Then Return
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
        If _onlinePrompt Is Nothing OrElse Not OnlinePackConfirm.IsEnabled Then Return
        ValidateOnlinePackName()
        If OnlinePackConfirm.IsEnabled Then CompleteOnlinePrompt(If(_onlineOptionalPrompt, "yes", OnlinePackName.Text))
    End Sub
    Private Sub CancelOnlinePackName(sender As Object, e As RoutedEventArgs)
        CompleteOnlinePrompt(If(_onlineOptionalPrompt, "no", Nothing))
    End Sub
    Private Sub CompleteOnlinePrompt(value As String)
        Dim prompt = _onlinePrompt : _onlinePrompt = Nothing
        If prompt Is Nothing OrElse Not AllowPageMotion() Then
            StopOnlinePromptMotion() : HideOnlinePrompt()
        Else
            PackNameDialog.IsHitTestVisible = False
            AnimateOnlinePrompt(False)
        End If
        prompt?.TrySetResult(value)
        If prompt IsNot Nothing AndAlso Not _closing Then FocusDownloadPage()
    End Sub
    Private Sub HideOnlinePrompt()
        PackNameOverlay.Visibility = Visibility.Collapsed : PageSurface.IsEnabled = True : TopTabs.IsEnabled = True
        If Not _closing Then FocusDownloadPage()
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
            PackNameDialog.Opacity = 1
            If Not entering Then HideOnlinePrompt()
            Return
        End If
        Dim timeline As New ParallelTimeline()
        If entering Then
            PackNameDialog.Opacity = 0 : _onlinePromptRotation.Angle = -4 : _onlinePromptOffset.Y = 40
            _onlinePromptShade.Color = Color.FromArgb(0, 0, 0, 0)
            timeline.Children.Add(New DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)) With {.BeginTime = TimeSpan.FromMilliseconds(60)})
            timeline.Children.Add(New PclDialogYAnimation With {.BeginTime = TimeSpan.FromMilliseconds(60)})
            timeline.Children.Add(New DoubleAnimation(-4, 0, TimeSpan.FromMilliseconds(300)) With {.BeginTime = TimeSpan.FromMilliseconds(60), .EasingFunction = New PowerEase With {.Power = 2, .EasingMode = EasingMode.EaseOut}})
        Else
            timeline.Children.Add(New DoubleAnimation(PackNameDialog.Opacity, 0, TimeSpan.FromMilliseconds(80)) With {.BeginTime = TimeSpan.FromMilliseconds(20)})
            timeline.Children.Add(New DoubleAnimation(_onlinePromptOffset.Y, 20, TimeSpan.FromMilliseconds(150)) With {.EasingFunction = New PowerEase With {.Power = 3, .EasingMode = EasingMode.EaseOut}})
            timeline.Children.Add(New DoubleAnimation(_onlinePromptRotation.Angle, 6, TimeSpan.FromMilliseconds(150)) With {.EasingFunction = New PowerEase With {.Power = 2, .EasingMode = EasingMode.EaseIn}})
        End If
        timeline.Children.Add(New ColorAnimation(_onlinePromptShade.Color, If(entering, Color.FromArgb(90, 0, 0, 0), Color.FromArgb(0, 0, 0, 0)), TimeSpan.FromMilliseconds(200)))
        Dim clock = DirectCast(timeline.CreateClock(True), ClockGroup)
        _onlinePromptMotion = clock
        AddHandler clock.Completed, Sub()
                                        If _onlinePromptMotion IsNot clock Then Return
                                        StopOnlinePromptMotion()
                                        If Not entering AndAlso _onlinePrompt Is Nothing Then HideOnlinePrompt()
                                    End Sub
        PackNameDialog.ApplyAnimationClock(OpacityProperty, DirectCast(clock.Children(0), AnimationClock))
        _onlinePromptOffset.ApplyAnimationClock(TranslateTransform.YProperty, DirectCast(clock.Children(1), AnimationClock))
        _onlinePromptRotation.ApplyAnimationClock(RotateTransform.AngleProperty, DirectCast(clock.Children(2), AnimationClock))
        _onlinePromptShade.ApplyAnimationClock(SolidColorBrush.ColorProperty, DirectCast(clock.Children(3), AnimationClock))
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
    Private Async Function RunOnlinePackInstallationAsync(release As ModpackRelease, root As String, name As String, limit As Long) As Task
        InstallPreflight.ValidateDestination(root, name, False)
        CancelPackRead()
        _packSearchRequest?.Cancel() : _packDetailRequest?.Cancel() : _packResultMotion.Finish()
        Dim policySnapshot = Policy(), reuse = If(ReuseBox.IsChecked = True, _folders.Select(Function(folder) folder.Location).ToList(), New List(Of String))
        Dim connections = ConnectionCount(), javaPath = JavaPathBox.Text.Trim()
        Dim request As New CancellationTokenSource : _cancellation = request : _busy = True
        SetBusy(True) : PersistSettings() : TaskPanel.Visibility = Visibility.Visible
        ResetTaskView(name, True, root, hasArchive:=True)
        _taskMinecraft = String.Join("、", release.GameVersions) : _taskComponents = release.Platform.ToString() & " " & release.Name
        _taskRows("metadata").Label.Text = "读取整合包清单与游戏版本信息" : _taskRows("files").Label.Text = "下载游戏、资源与整合包文件"
        ShowProgress(Nothing, Nothing)
        Try
            Dim progress = CreateTaskProgress(), result As InstallResult
            If _onlineInstallRunner IsNot Nothing Then
                result = Await _onlineInstallRunner(release, root, name, progress, request.Token)
            ElseIf _preview Then
                Throw New InvalidOperationException("界面预览不会执行实际安装。")
            Else
                result = Await Task.Run(Function() ModpackArchiveInstaller.InstallAsync(release, root, name, policySnapshot, _pclBase, reuse, progress, request.Token, connections, limit, javaPath,
                    Function(package, token) Dispatcher.InvokeAsync(Function() ChooseOnlineOptionalAsync(package, token)).Task.Unwrap()))
            End If
            _taskInstancePath = result.InstancePath : TaskTitle.Text = "安装完成 · " & name
            StatusText.Text = "完成：" & result.FileCount & " 个文件。回到 PCL 刷新版本列表即可。"
            FinishTaskView(True, StatusText.Text)
        Catch ex As OperationCanceledException
            TaskTitle.Text = "已取消，可重新开始" : StatusText.Text = "已取消。已校验归档与共享文件会在重试时复用。"
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
