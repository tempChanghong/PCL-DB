Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private _resourceKind As CommunityResourceKind = CommunityResourceKind.Modpack
    Private _resourceTargetLoader As String = ""
    Private ReadOnly _resourceBrowsers As New Dictionary(Of CommunityResourceKind, ResourceBrowserState)
    Private ReadOnly _resourceBack As New Stack(Of ResourceDetailState)
    Private _dependencyRequest As CancellationTokenSource
    Private _resourceNamesRequest As CancellationTokenSource
    Private ReadOnly _resourceNameSlots As New SemaphoreSlim(4)
    Private ReadOnly _resourceDependencyNames As New Dictionary(Of String, String)(StringComparer.Ordinal)
    Private ReadOnly _resourceDependencyNameLoads As New Dictionary(Of String, Task(Of String))(StringComparer.Ordinal)
    Private _resourceSaveRunner As Func(Of ModpackRelease, String, IProgress(Of TransferProgress), CancellationToken, Task)
    Private _resourceSaveChooser As Func(Of ModpackRelease, String)

    Private Class ResourceDetailState
        Friend Project As ModpackProject, Verified As ModpackProject, Completed As ModpackProject
        Friend Releases As List(Of ModpackRelease)
        Friend Scroll As Double, Game As String, Filter As String, Loader As String
        Friend Expanded As String()
    End Class
    Private Class ResourceBrowserState
        Friend Text As String = "", GameText As String = "全部 (也可自行输入)"
        Friend Category As Integer, Source As Integer, Loader As Integer, Page As Integer
        Friend Session As ModpackSearchSession, LastQuery As String, LastSearch As DateTime
        Friend Scroll As Double, Mode As PackBrowserMode
        Friend Detail As ResourceDetailState
        Friend Back As ResourceDetailState() = {}
    End Class
    Private Function CaptureResourceDetail() As ResourceDetailState
        Return New ResourceDetailState With {.Project = _packProject, .Verified = _packVerifiedProject, .Completed = _packDetailCompletedProject,
            .Releases = _packReleases, .Scroll = ModpackPage.VerticalOffset, .Game = _packTargetGame, .Filter = _packGameFilter, .Loader = _resourceTargetLoader,
            .Expanded = CapturePackVersionExpansions()}
    End Function
    Private Sub CancelResourceRequests()
        _dependencyRequest?.Cancel()
        ResetResourceNames()
        _packSearchRequest?.Cancel() : _packDetailRequest?.Cancel() : _packArtworkRequest?.Cancel()
        _packArtworkRequest?.Dispose() : _packArtworkRequest = Nothing
        RetirePackDetailArtwork()
        _packSearchRequest = Nothing : _packDetailRequest = Nothing
        _packSearchLoading?.Reset() : _packDetailLoading?.Reset()
        FinishPackContentMotion()
    End Sub
    Private Sub ResetResourceNames()
        Dim previous = _resourceNamesRequest
        _resourceDependencyNameLoads.Clear()
        ' Publish the new ownership before cancellation can start another label
        ' read or reset. Only the captured previous source may be disposed here.
        _resourceNamesRequest = If(_closing, Nothing, New CancellationTokenSource())
        Try
            previous?.Cancel()
        Finally
            previous?.Dispose()
        End Try
    End Sub
    Private Sub RefreshResourceDependencyLabels()
        If ActivePackProject() Is Nothing Then Return
        ResetResourceNames()
        For Each card In PackVersionCards.Children.OfType(Of PclCard)()
            Dim rows = TryCast(card.Body, StackPanel)
            If rows Is Nothing Then Continue For
            For Each links In rows.Children.OfType(Of WrapPanel)()
                For Each button In links.Children.OfType(Of PclButton)()
                    Dim dependency = TryCast(button.Tag, ResourceDependency)
                    If dependency IsNot Nothing Then LoadDependencyNameAsync(ActivePackProject(), dependency, button, _resourceNamesRequest.Token)
                Next
            Next
        Next
    End Sub
    Private Sub SaveResourceBrowser()
        _resourceBrowsers(_resourceKind) = New ResourceBrowserState With {.Text = PackSearchText.Text, .GameText = PackSearchGame.Text,
            .Category = PackSearchCategory.SelectedIndex, .Source = PackSearchSource.SelectedIndex, .Loader = ResourceLoader.SelectedIndex,
            .Session = _packSearchSession, .LastQuery = _packLastQuery, .LastSearch = _packLastSearch, .Page = _packSearchPage,
            .Scroll = If(_packMode = PackBrowserMode.Search, ModpackPage.VerticalOffset, _packSearchScroll), .Mode = _packMode,
            .Detail = CaptureResourceDetail(), .Back = _resourceBack.Reverse().ToArray()}
    End Sub
    Private Sub ShowResourceDownloads(sender As Object, e As RoutedEventArgs)
        Dim item = TryCast(sender, FrameworkElement)
        If item Is Nothing Then Return
        Dim kind As CommunityResourceKind
        If Not [Enum].TryParse(CStr(item.Tag), kind) OrElse kind = CommunityResourceKind.Any Then Return
        SelectResourceKind(kind)
    End Sub
    Private Sub SelectResourceKind(kind As CommunityResourceKind, Optional configure As Action = Nothing)
        If _busy OrElse _choosingOnline OrElse _closing Then Return
        If kind = _resourceKind Then
            configure?.Invoke()
            SetDownloadView(DownloadView.Modpacks) : EnsurePackSearch() : Return
        End If
        RetirePromptFocus() : ClearDownloadFocus() : _surfaceMotion.Finish()
        _pendingSurfaceFocus = Nothing : _restorePackViewportFocus = False
        Dim previous = _downloadView
        _categoryMotion.Change(DownloadContent(previous), ModpackPage,
            Sub()
                SaveResourceBrowser() : CancelResourceRequests()
                _packFiltersPending = False : _packSearchValidation = False
                _resourceKind = kind
                Dim state As ResourceBrowserState = Nothing
                If Not _resourceBrowsers.TryGetValue(kind, state) Then state = New ResourceBrowserState()
                ConfigureResourceSearch()
                PackSearchText.Text = state.Text : PackSearchGame.Text = state.GameText
                PackSearchSource.SelectedIndex = state.Source
                PackSearchCategory.SelectedIndex = Math.Max(0, Math.Min(state.Category, PackSearchCategory.Items.Count - 1))
                ResourceLoader.SelectedIndex = state.Loader
                _packSearchSession = state.Session : _packLastQuery = state.LastQuery : _packLastSearch = state.LastSearch
                _packSearchPage = state.Page : _packSearchScroll = state.Scroll : _packMode = state.Mode
                _resourceBack.Clear()
                For Each frame In state.Back : _resourceBack.Push(frame) : Next
                _packProject = Nothing : _packVerifiedProject = Nothing : _packDetailCompletedProject = Nothing
                _packReleases = New List(Of ModpackRelease) : _packVersionCache.Clear()
                PackProjectRows.Children.Clear() : PackVersionCards.Children.Clear() : PackVersionFilters.Children.Clear()
                PackSearchHint.Visibility = Visibility.Collapsed : PackSearchInputHint.Visibility = Visibility.Collapsed
                PackPagingCard.Visibility = Visibility.Collapsed : PackResultsCard.Title = CommunityResources.Name(kind)
                PackResultsCard.HorizontalAlignment = HorizontalAlignment.Stretch
                PackResultsCard.Visibility = Visibility.Visible
                PackDetailRetry.Visibility = Visibility.Collapsed : PackDetailHint.Visibility = Visibility.Collapsed
                PackVersionFilterCard.Visibility = Visibility.Collapsed
                PackSearchSurface.Visibility = If(state.Mode = PackBrowserMode.Search, Visibility.Visible, Visibility.Collapsed)
                PackDetailSurface.Visibility = If(state.Mode = PackBrowserMode.Detail, Visibility.Visible, Visibility.Collapsed)
                PackLocalSurface.Visibility = If(state.Mode = PackBrowserMode.Local AndAlso kind = CommunityResourceKind.Modpack, Visibility.Visible, Visibility.Collapsed)
                ApplyDownloadView(DownloadView.Modpacks, previous)
                If state.Mode = PackBrowserMode.Detail AndAlso state.Detail?.Project IsNot Nothing Then
                    RestoreResourceDetail(state.Detail)
                ElseIf state.Session IsNot Nothing Then
                    RestoreResourceSearchAsync(state.Page)
                End If
                ModpackPage.UpdateLayout() : ModpackPage.ScrollToVerticalOffset(If(state.Mode = PackBrowserMode.Search, state.Scroll, _packDetailScroll))
                configure?.Invoke()
            End Sub, contentRefresh:=previous = DownloadView.Modpacks, compositeExit:=HasPackContentMotion() OrElse _pageSwitch.HasMotion)
        EnsurePackSearch()
    End Sub
    Private Async Sub RestoreResourceSearchAsync(page As Integer)
        ' Restore inside the accepted request, after its rows have a real extent.
        ' A cancelled page can otherwise return after a newer navigation and
        ' overwrite that page's reading position despite sharing the same session.
        Await LoadPackSearchPageAsync(page, _packSearchScroll)
    End Sub
    Private Async Sub RestoreResourceDetail(state As ResourceDetailState)
        ResetResourceNames()
        _packProject = state.Project : _packVerifiedProject = state.Verified : _packDetailCompletedProject = state.Completed
        _packReleases = state.Releases : _packDetailScroll = state.Scroll : _packTargetGame = state.Game : _resourceTargetLoader = state.Loader
        _packVersionCache.Clear() : RenderPackProject(ActivePackProject())
        InitializePackVersions(state.Expanded)
        ' Empty is PCL's explicit All choice, not an absent saved selection.
        _packGameFilter = If(state.Filter, "") : RenderPackVersions()
        ModpackPage.UpdateLayout() : ModpackPage.ScrollToVerticalOffset(state.Scroll)
        If state.Completed IsNot state.Project Then Await LoadPackDetailsAsync(state.Project)
    End Sub
    Private Sub AddResourceDependencies(rows As StackPanel, releases As List(Of ModpackRelease))
        Dim project = ActivePackProject()
        If project Is Nothing OrElse project.Kind = CommunityResourceKind.Modpack Then Return
        Dim dependencies = releases.SelectMany(Function(release) release.Dependencies).GroupBy(Function(item) item.ProjectId & ":" & item.VersionId, StringComparer.Ordinal).Select(Function(group) group.First()).ToList()
        If dependencies.Count = 0 Then Return
        If _resourceNamesRequest Is Nothing Then ResetResourceNames()
        Dim token = _resourceNamesRequest.Token
        rows.Children.Add(New TextBlock With {.Text = "前置资源", .FontSize = 14, .Margin = New Thickness(6, 2, 0, 5)})
        Dim links As New WrapPanel With {.Margin = New Thickness(6, 0, 0, 10)}
        For Each dependency In dependencies
            Dim captured = dependency
            Dim button As New PclButton With {.Text = "查看前置", .Height = 28, .MinWidth = 95, .MaxWidth = 280, .Margin = New Thickness(0, 0, 8, 5), .Tag = dependency, .ToolTip = "查看必需前置"}
            AddHandler button.Click, Sub() OpenResourceDependency(project, captured, button)
            links.Children.Add(button)
            LoadDependencyNameAsync(project, captured, button, token)
        Next
        rows.Children.Add(links)
    End Sub
    Private Async Sub LoadDependencyNameAsync(project As ModpackProject, dependency As ResourceDependency, button As PclButton, token As CancellationToken)
        Try
            Dim name = Await ResourceDependencyNameAsync(project.Platform, dependency, token)
            If Not token.IsCancellationRequested AndAlso ActivePackProject() Is project Then button.Text = name
        Catch
            ' Optional labels cannot prevent version downloads. The link retries.
        End Try
    End Sub
    Private Function ResourceDependencyNameAsync(platform As ModpackPlatform, dependency As ResourceDependency, token As CancellationToken) As Task(Of String)
        Dim key = platform.ToString() & ":" & If(String.IsNullOrEmpty(dependency.ProjectId), "version:" & dependency.VersionId, dependency.ProjectId)
        Dim name As String = Nothing, pending As Task(Of String) = Nothing
        If _resourceDependencyNames.TryGetValue(key, name) Then Return Task.FromResult(name)
        If _resourceDependencyNameLoads.TryGetValue(key, pending) Then Return pending
        ' Install ownership before starting: cached HTTP responses may finish inline.
        ' Like PCL's project cache/batched dependency lookup, all version groups
        ' subscribe to the same accepted project-name request.
        Dim completion As New TaskCompletionSource(Of String)(TaskCreationOptions.RunContinuationsAsynchronously)
        _resourceDependencyNameLoads.Add(key, completion.Task)
        ResolveResourceDependencyNameAsync(key, platform, dependency, token, completion)
        Return completion.Task
    End Function
    Private Async Sub ResolveResourceDependencyNameAsync(key As String, platform As ModpackPlatform, dependency As ResourceDependency,
                                                        token As CancellationToken, completion As TaskCompletionSource(Of String))
        Dim entered = False
        Try
            Await _resourceNameSlots.WaitAsync(token) : entered = True
            token.ThrowIfCancellationRequested()
            Dim name As String = Nothing
            If Not _resourceDependencyNames.TryGetValue(key, name) Then
                Using client = _packCatalogFactory()
                    ' Names belong to projects. Version-pinned navigation still
                    ' resolves and validates the original dependency on click.
                    Dim linked = If(String.IsNullOrEmpty(dependency.ProjectId),
                        Await client.ResolveDependencyAsync(platform, dependency, token),
                        Await client.GetProjectAsync(platform, dependency.ProjectId, token, CommunityResourceKind.Any))
                    name = linked.Name
                End Using
            End If
            token.ThrowIfCancellationRequested()
            Dim current As Task(Of String) = Nothing
            If Not _resourceDependencyNameLoads.TryGetValue(key, current) OrElse current IsNot completion.Task Then
                completion.TrySetCanceled() : Return
            End If
            If _resourceDependencyNames.Count >= 512 Then _resourceDependencyNames.Clear()
            _resourceDependencyNames(key) = name
            completion.TrySetResult(name)
        Catch ex As OperationCanceledException When token.IsCancellationRequested
            completion.TrySetCanceled()
        Catch ex As Exception
            completion.TrySetException(ex)
        Finally
            If entered Then _resourceNameSlots.Release()
            Dim current As Task(Of String) = Nothing
            If _resourceDependencyNameLoads.TryGetValue(key, current) AndAlso current Is completion.Task Then _resourceDependencyNameLoads.Remove(key)
        End Try
    End Sub
    Private Sub ConfigureResourceSearch()
        Dim name = ResourceNameSpaced(_resourceKind)
        PackSearchText.ToolTip = "输入" & name & "名称，按 Enter 搜索"
        PackSearchText.Tag = ("搜索" & name).TrimEnd()
        Automation.AutomationProperties.SetName(PackSearchText, CStr(PackSearchText.Tag))
        PackSearchImport.Visibility = If(_resourceKind = CommunityResourceKind.Modpack, Visibility.Visible, Visibility.Collapsed)
        ResourceLoaderFilters.Visibility = If(_resourceKind = CommunityResourceKind.Mod, Visibility.Visible, Visibility.Collapsed)
        If ResourceLoader.Items.Count = 0 Then
            For Each loader In {"", "fabric", "forge", "neoforge", "quilt", "liteloader"}
                ResourceLoader.Items.Add(New PclComboBoxItem With {.Content = If(loader = "", "全部", If(loader = "neoforge", "NeoForge", If(loader = "liteloader", "LiteLoader", Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(loader)))), .Tag = loader})
            Next
        End If
        ResourceLoader.SelectedIndex = 0
        PackSearchCategory.Items.Clear()
        Dim tags As IEnumerable(Of KeyValuePair(Of String, String)) = ResourceSearchCategories.Entries(_resourceKind)
        If _resourceKind = CommunityResourceKind.Modpack Then tags = New Dictionary(Of String, String) From {{"全部", "/"}, {"战斗", "4483/combat"}, {"任务", "4478/quests"}, {"科技", "4472/technology"}, {"魔法", "4473/magic"}, {"冒险", "4475/adventure"}, {"优化", "/optimization"}, {"轻量", "4481/lightweight"}, {"多人", "4484/"}, {"大型", "4482/"}}
        For Each pair In tags : PackSearchCategory.Items.Add(New PclComboBoxItem With {.Content = pair.Key, .Tag = pair.Value, .IsEnabled = pair.Value IsNot Nothing}) : Next
        PackSearchCategory.SelectedIndex = 0
    End Sub

    Private Shared Function ResourceNameSpaced(kind As CommunityResourceKind) As String
        ' PCL PageResource.TypeNameSpaced keeps Latin names separated from CJK.
        Return If(kind = CommunityResourceKind.Mod, " Mod ", CommunityResources.Name(kind))
    End Function
    Private Async Sub OpenResourceDependency(project As ModpackProject, dependency As ResourceDependency, owner As FrameworkElement)
        If Not CanUseResourceDetail(owner) OrElse ActivePackProject() IsNot project Then Return
        Dim previous = _dependencyRequest, request As New CancellationTokenSource
        _dependencyRequest = request
        Try
            ' PCL registers loader state before cancellation notification. A
            ' newer click or title return may synchronously retire this lookup.
            previous?.Cancel()
            If request.IsCancellationRequested OrElse _dependencyRequest IsNot request OrElse ActivePackProject() IsNot project OrElse Not CanUseResourceDetail(owner) Then Return
            Using client = _packCatalogFactory()
                Dim linked = Await client.ResolveDependencyAsync(project.Platform, dependency, request.Token)
                If request.IsCancellationRequested OrElse _dependencyRequest IsNot request OrElse ActivePackProject() IsNot project OrElse Not CanUseResourceDetail(owner) Then Return
                If _resourceBack.Count >= 32 Then Throw New InvalidOperationException("前置跳转层级过多，请返回后重试。")
                ' PCL's page stack records PageCurrent at the actual handoff.
                ' The parent can still be filtered, scrolled or folded while this
                ' lookup is pending; preserve those latest accepted choices.
                _resourceBack.Push(CaptureResourceDetail())
                ' Same-type references can use the cached project card; a fixed
                ' dependency version remains part of its accepted detail owner.
                ShowDependencyProjectAsync(linked)
            End Using
        Catch ex As OperationCanceledException
        Catch ex As Exception
            ' Filtering/folding can remove this link without changing the project.
            ' Keep errors with their visible link; an entry's temporary input gate
            ' alone does not revoke feedback from an otherwise current lookup.
            If Not request.IsCancellationRequested AndAlso _dependencyRequest Is request AndAlso ActivePackProject() Is project AndAlso
                owner.IsLoaded AndAlso owner.IsVisible AndAlso PackDetailSurface.IsVisible Then
                PackProjectActionStatus.Text = "无法获取前置，请重试。" : PackProjectActionStatus.ToolTip = ex.GetBaseException().Message : PackProjectActionStatus.Visibility = Visibility.Visible
            End If
        Finally
            If _dependencyRequest Is request Then _dependencyRequest = Nothing
            request.Dispose()
        End Try
    End Sub
    Private Async Sub ShowDependencyProjectAsync(project As ModpackProject)
        _packDetailRequest?.Cancel()
        ResetResourceNames()
        ChangePackContent(PackBrowserMode.Detail, Sub()
            _packProject = project : _packVerifiedProject = Nothing : _packDetailCompletedProject = Nothing
            _packReleases = New List(Of ModpackRelease) : _packDetailScroll = 0 : _packVersionCache.Clear()
            RenderPackProject(project) : PackVersionCards.Children.Clear()
            ModpackPage.ScrollToHome()
        End Sub)
        Await LoadPackDetailsAsync(project)
    End Sub
    Private Function CanUseResourceDetail(owner As FrameworkElement) As Boolean
        Return Not _busy AndAlso Not _choosingOnline AndAlso Not _closing AndAlso owner.IsLoaded AndAlso owner.IsVisible AndAlso owner.IsEnabled AndAlso owner.IsHitTestVisible AndAlso
            _downloadView = DownloadView.Modpacks AndAlso _packMode = PackBrowserMode.Detail AndAlso DownloadPage.IsVisible AndAlso PageSurface.IsHitTestVisible AndAlso DownloadViewHost.IsHitTestVisible AndAlso PackDetailResultsHost.IsHitTestVisible
    End Function
    Private Async Sub SaveCommunityResource(release As ModpackRelease, owner As FrameworkElement)
        If Not CanUseResourceDetail(owner) OrElse _localModWriting OrElse _readingPack OrElse _packProject Is Nothing OrElse Not _packReleases.Contains(release) Then Return
        Dim project = ActivePackProject()
        If release.Platform <> project.Platform OrElse release.ProjectId <> project.Id OrElse release.Kind <> project.Kind Then Return
        Dim snapshot = release.Copy(), destination As String = Nothing, limit As Double
        If Not TryGetSpeedLimit(limit) Then NavigateToSetting(SpeedLimitBox, True) : Return
        _choosingOnline = True
        Try
            If _resourceSaveChooser IsNot Nothing Then
                destination = _resourceSaveChooser(snapshot)
            ElseIf Not _preview Then
                Dim dialog As New Microsoft.Win32.SaveFileDialog With {.Title = "选择保存位置", .FileName = _communityPreferences.SuggestedFileName(project, snapshot), .AddExtension = True,
                    .DefaultExt = Path.GetExtension(snapshot.FileName), .Filter = CommunityResources.Name(snapshot.Kind) & "文件 (*" & Path.GetExtension(snapshot.FileName) & ")|*" & Path.GetExtension(snapshot.FileName), .OverwritePrompt = False}
                Dim initial = ResourceSaveDirectory(snapshot.Kind)
                If initial IsNot Nothing Then dialog.InitialDirectory = initial
                If dialog.ShowDialog(Me) = True Then destination = dialog.FileName
            End If
        Catch ex As Exception
            If Not _closing Then PackProjectActionStatus.Text = ex.GetBaseException().Message : PackProjectActionStatus.Visibility = Visibility.Visible
            Return
        Finally
            _choosingOnline = False
        End Try
        If String.IsNullOrEmpty(destination) OrElse Not CanUseResourceDetail(owner) OrElse ActivePackProject() IsNot project Then Return
        Try
            Await RunResourceSaveAsync(snapshot, destination)
        Catch ex As Exception
            If Not _closing Then PackProjectActionStatus.Text = ex.GetBaseException().Message : PackProjectActionStatus.Visibility = Visibility.Visible
        End Try
    End Sub
    Private Function ResourceSaveContext() As InstalledInstance
        If _selectedFolder Is Nothing Then Return Nothing
        For Each instance In {_managedInstance, _localModInstance}
            If instance IsNot Nothing AndAlso instance.Problem Is Nothing AndAlso instance.RunDirectory IsNot Nothing AndAlso SameManagementTarget(instance.Target, CurrentInstallTarget()) Then Return instance
        Next
        Return Nothing
    End Function
    Private Function ResourceSaveDirectory(kind As CommunityResourceKind) As String
        Dim instance = ResourceSaveContext(), folder = If(instance?.RunDirectory, _selectedFolder?.Location)
        If folder Is Nothing OrElse Not Directory.Exists(folder) Then Return Nothing
        Dim child = If(kind = CommunityResourceKind.Mod, "mods", If(kind = CommunityResourceKind.ResourcePack, "resourcepacks", If(kind = CommunityResourceKind.Shader, "shaderpacks", "")))
        Dim destination = Path.Combine(folder, child)
        Return If(Directory.Exists(destination), destination, folder)
    End Function
    Private Async Function RunResourceSaveAsync(release As ModpackRelease, destination As String, Optional installation As ResourceInstallationPlan = Nothing) As Task
        Dim cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PclDownloadBridge", "ResourceCache")
        Dim policySnapshot = Policy(), connections = ConnectionCount(), limit As Double
        If Not TryGetSpeedLimit(limit) Then Return
        Dim instance = ResourceSaveContext()
        If instance IsNot Nothing AndAlso (release.Kind <> CommunityResourceKind.Mod OrElse Not SameGameRoot(Path.GetDirectoryName(Path.GetFullPath(destination)), Path.Combine(instance.RunDirectory, "mods"))) Then instance = Nothing
        CancelResourceRequests()
        Dim request As New CancellationTokenSource : _cancellation = request : _busy = True
        _taskInstallTarget = Nothing : SetBusy(True)
        ResetTaskView(Path.GetFileName(destination), False, cache, hasArchive:=True)
        _taskResourceTransfer = True
        TaskTitle.Text = If(installation Is Nothing, "下载 ", "安装 ") & Path.GetFileName(destination)
        For Each key In {"metadata", "files"}
            DirectCast(_taskRows(key).Label.Parent, UIElement).Visibility = Visibility.Collapsed
        Next
        _taskMinecraft = String.Join("、", release.GameVersions) : _taskComponents = CommunityResources.Name(release.Kind) & " · " & release.Platform.ToString()
        _taskRows("archive").Label.Text = "下载并校验文件"
        _taskRows("commit").Label.Text = If(installation Is Nothing, "保存文件", "安装到 " & installation.InstanceName)
        ShowProgress(Nothing, Nothing)
        Try
            If installation IsNot Nothing Then
                If _resourceInstallRunner IsNot Nothing Then
                    Await _resourceInstallRunner(installation, cache, policySnapshot, CreateTaskProgress(), request.Token)
                ElseIf _preview Then
                    Throw New InvalidOperationException("界面预览不会执行实际安装。")
                Else
                    Await installation.InstallAsync(cache, policySnapshot, CreateTaskProgress(), request.Token, connections, CLng(limit * 1024 * 1024))
                End If
                InvalidateResourceManagement(installation)
            ElseIf _resourceSaveRunner IsNot Nothing Then
                Await _resourceSaveRunner(release, destination, CreateTaskProgress(), request.Token)
            ElseIf _preview Then
                Throw New InvalidOperationException("界面预览不会保存实际文件。")
            Else
                Await CommunityResourceDownload.SaveAsync(release, destination, cache, policySnapshot, CreateTaskProgress(), request.Token, connections, CLng(limit * 1024 * 1024), instance)
            End If
            TaskTitle.Text = If(installation Is Nothing, "下载完成 · ", "安装完成 · ") & Path.GetFileName(destination) : StatusText.Text = "已保存到：" & destination
            _taskResourceFile = destination
            FinishTaskView(True, StatusText.Text)
        Catch ex As OperationCanceledException
            TaskTitle.Text = "已取消，可重新下载" : StatusText.Text = "操作已取消，重试时会校验下载缓存。"
            FinishTaskView(False, StatusText.Text, cancelled:=True)
        Catch ex As Exception
            TaskTitle.Text = "下载未完成，可重试" : StatusText.Text = ex.GetBaseException().Message
            FinishTaskView(False, StatusText.Text, cancelled:=request.IsCancellationRequested)
        Finally
            If installation IsNot Nothing Then InvalidateResourceManagement(installation)
            _busy = False : _cancellation = Nothing : request.Dispose() : SetBusy(False)
        End Try
    End Function
End Class
