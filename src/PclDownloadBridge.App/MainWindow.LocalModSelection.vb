Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private ReadOnly _localModSelection As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _localModUpdates As New Dictionary(Of String, LocalModUpdate)(StringComparer.OrdinalIgnoreCase)
    Private _localModRenderingSelection As Boolean, _localModSelectedCount As Integer
    Private _localModUpdatesRequest As CancellationTokenSource
    Private _localModAutoCheckForChecks As Boolean
    Private _localModUpdateCacheKey As String, _localModUpdateCacheTime As DateTime
    Private _localModUpdateCache As LocalModUpdateCheckResult
    Private _localModBarPolicy As PclControlMotionPolicy, _localModBarOpacity As PclMotionSlot, _localModBarOffset As PclMotionSlot
    Private _localModUpdateProvider As Func(Of InstalledInstance, List(Of LocalModEntry), CancellationToken, Task(Of LocalModUpdateCheckResult))
    Private _localModUpdateInstaller As Func(Of InstalledInstance, List(Of LocalModUpdate), String, SourcePolicy, CancellationToken, Integer, Long, Task(Of LocalModActionResult)) =
        Function(instance, updates, cache, policy, token, connections, limit) LocalModUpdater.InstallAsync(instance, updates, cache, policy, Nothing, token, connections, limit)
    Private Function HasLocalModUpdate(entry As LocalModEntry) As Boolean
        Dim update As LocalModUpdate = Nothing
        Return _localModUpdates.TryGetValue(entry.FilePath, update) AndAlso update.Entry.Sha256 = entry.Sha256 AndAlso update.HasUpdate
    End Function
    Private Sub ConfigureLocalModSelection(row As PclLocalModItem, entry As LocalModEntry)
        row.Checked = _localModSelection.Contains(entry.FilePath)
        row.SetUpdateInfo(If(HasLocalModUpdate(entry), _localModUpdates(entry.FilePath), Nothing))
    End Sub
    Private Sub LocalModChecked(row As PclLocalModItem, entry As LocalModEntry)
        If _localModRenderingSelection Then Return
        If Not LocalModInputAllowed() OrElse Not _localModReadSucceeded OrElse Not _localMods.Contains(entry) OrElse Not LocalModRows.Children.Contains(row) OrElse Not LocalModRows.IsEnabled Then
            _localModRenderingSelection = True : row.Checked = _localModSelection.Contains(entry.FilePath) : _localModRenderingSelection = False : Return
        End If
        If row.Checked Then _localModSelection.Add(entry.FilePath) Else _localModSelection.Remove(entry.FilePath)
        RefreshLocalModSelection(True)
    End Sub
    Private Sub SelectAllLocalMods(sender As Object, e As RoutedEventArgs)
        If Not LocalModInputAllowed() OrElse Not LocalModSelectAll.IsEnabled Then Return
        Dim visible = LocalModRows.Children.OfType(Of PclLocalModItem)().Select(Function(row) DirectCast(row.Tag, LocalModEntry)).ToList()
        SetAllLocalMods(Not visible.All(Function(entry) _localModSelection.Contains(entry.FilePath)))
    End Sub
    Private Sub SetAllLocalMods(selected As Boolean)
        If Not LocalModInputAllowed() OrElse Not LocalModSelectAll.IsEnabled Then Return
        For Each row In LocalModRows.Children.OfType(Of PclLocalModItem)()
            Dim entry = DirectCast(row.Tag, LocalModEntry)
            If selected Then _localModSelection.Add(entry.FilePath) Else _localModSelection.Remove(entry.FilePath)
        Next
        ApplyLocalModSelection()
    End Sub
    Private Sub ClearLocalModSelection(sender As Object, e As RoutedEventArgs)
        If Not LocalModInputAllowed() OrElse _localModWriting Then Return
        _localModSelection.Clear() : ApplyLocalModSelection()
    End Sub
    Private Sub ApplyLocalModSelection()
        ' PCL ChangeAllSelected updates attached rows; rebuilding the list would
        ' unload the controls and settle their just-started selection clocks.
        _localModRenderingSelection = True
        Try
            For Each row In LocalModRows.Children.OfType(Of PclLocalModItem)()
                row.Checked = _localModSelection.Contains(DirectCast(row.Tag, LocalModEntry).FilePath)
            Next
        Finally
            _localModRenderingSelection = False
        End Try
        RefreshLocalModSelection(True)
    End Sub
    Private Function LocalModSelectionKey(e As KeyEventArgs, Optional modifiers As ModifierKeys? = Nothing) As Boolean
        If Not LocalModInputAllowed() Then Return False
        If e.Key = Key.Escape AndAlso _localModSelection.Count > 0 AndAlso Not _localModWriting Then
            ClearLocalModSelection(Nothing, Nothing) : Return True
        End If
        ' The optional modifier input keeps isolated checks off the desktop
        ' keyboard. Normal shell events read the current Windows modifiers.
        If e.Key = Key.A AndAlso modifiers.GetValueOrDefault(Keyboard.Modifiers) = ModifierKeys.Control Then
            If LocalModSearch.IsKeyboardFocusWithin AndAlso LocalModSearch.Text.Length > 0 Then Return False
            If LocalModSelectAll.IsEnabled Then
                ' PCL's empty search yields Ctrl+A to the Mods viewport. Its
                ' shortcut always selects; only the toolbar button toggles.
                If LocalModSearch.IsKeyboardFocusWithin Then LocalModsPage.Focus()
                SetAllLocalMods(True)
            End If
            Return True
        End If
        Return False
    End Function
    Private Sub RefreshLocalModSelectionAvailability(canWrite As Boolean)
        If LocalModSelectionBar Is Nothing Then Return
        Dim selected = _localMods.Where(Function(entry) _localModSelection.Contains(entry.FilePath)).ToList()
        Dim valid = canWrite AndAlso selected.Count > 0 AndAlso selected.All(Function(entry) entry.CanToggle)
        LocalModUpdateSelected.IsEnabled = valid AndAlso selected.Any(Function(entry) HasLocalModUpdate(entry))
        LocalModEnableSelected.IsEnabled = valid AndAlso selected.Any(Function(entry) Not entry.Enabled)
        LocalModDisableSelected.IsEnabled = valid AndAlso selected.Any(Function(entry) entry.Enabled)
        LocalModDeleteSelected.IsEnabled = valid : LocalModClearSelection.IsEnabled = canWrite
        LocalModSelectionBar.IsHitTestVisible = canWrite AndAlso selected.Count > 0 AndAlso LocalModInputAllowed()
    End Sub
    Private Sub RefreshLocalModSelection(animate As Boolean)
        If LocalModSelectionBar Is Nothing Then Return
        If _localModBarPolicy Is Nothing Then
            _localModBarOpacity = New PclMotionSlot(LocalModSelectionBar, OpacityProperty)
            _localModBarOffset = New PclMotionSlot(LocalModSelectionOffset, TranslateTransform.YProperty)
            _localModBarPolicy = New PclControlMotionPolicy(LocalModsPage, Sub() RefreshLocalModSelection(False), requiresInteraction:=False)
        End If
        Dim count = If(LocalModsPage.IsVisible AndAlso Not _closing, _localModSelection.Count, 0), before = _localModSelectedCount
        _localModSelectedCount = count
        If count = 0 AndAlso before > 0 AndAlso LocalModSelectionBar.IsKeyboardFocusWithin AndAlso LocalModInputAllowed() AndAlso
            _localModReadSucceeded AndAlso Not _localModScanning AndAlso Not _localModWriting Then
            ' Move focus before fading/collapsing its owner. A deferred exit
            ' callback could otherwise steal a newer editor or page focus.
            Dim offset = LocalModsPage.VerticalOffset
            FocusManager.SetFocusedElement(FocusManager.GetFocusScope(LocalModsPage), LocalModsPage)
            LocalModsPage.Focus()
            LocalModsPage.ScrollToVerticalOffset(offset)
        End If
        If count > 0 Then LocalModSelectionCount.Text = "已选择 " & count & " 个文件"
        LocalModSelectAll.Text = If(count > 0 AndAlso count = LocalModRows.Children.Count, "取消全选", "全选")
        LocalModContent.Margin = New Thickness(25, 10, 25, If(count > 0, 95, 10))
        Dim moving = animate AndAlso _localModBarPolicy.CanAnimate
        If count > 0 Then LocalModSelectionBar.Visibility = Visibility.Visible
        If moving AndAlso count > before Then
            Dim bounce As New DoubleAnimationUsingKeyFrames With {.Duration = TimeSpan.FromMilliseconds(360)}
            bounce.KeyFrames.Add(New LinearDoubleKeyFrame(LocalModSelectionOffset.Y, KeyTime.FromTimeSpan(TimeSpan.Zero)))
            bounce.KeyFrames.Add(New EasingDoubleKeyFrame(-27, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)), New QuadraticEase With {.EasingMode = EasingMode.EaseOut}))
            bounce.KeyFrames.Add(New EasingDoubleKeyFrame(-24, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(270)), New QuadraticEase With {.EasingMode = EasingMode.EaseInOut}))
            bounce.KeyFrames.Add(New EasingDoubleKeyFrame(-25, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(360)), New QuadraticEase With {.EasingMode = EasingMode.EaseInOut}))
            _localModBarOffset.Animate(bounce, -25.0, _localModBarPolicy.PauseForChecks)
            _localModBarOpacity.Animate(New DoubleAnimation(LocalModSelectionBar.Opacity, 1, TimeSpan.FromMilliseconds(60)), 1.0, _localModBarPolicy.PauseForChecks)
        ElseIf moving AndAlso count = 0 AndAlso before > 0 Then
            _localModBarOffset.Animate(New DoubleAnimation(LocalModSelectionOffset.Y, -10, TimeSpan.FromMilliseconds(90)) With {
                .EasingFunction = New QuadraticEase With {.EasingMode = EasingMode.EaseIn}}, -10.0, _localModBarPolicy.PauseForChecks)
            _localModBarOpacity.Animate(New DoubleAnimation(LocalModSelectionBar.Opacity, 0, TimeSpan.FromMilliseconds(90)), 0.0, _localModBarPolicy.PauseForChecks)
            Dim clock = _localModBarOpacity.Clock
            AddHandler clock.Completed, Sub()
                                            If _localModSelectedCount = 0 AndAlso _localModBarOpacity.Clock Is Nothing Then LocalModSelectionBar.Visibility = Visibility.Collapsed
                                        End Sub
        ElseIf Not moving Then
            _localModBarOffset.SetValue(If(count > 0, -25.0, -10.0)) : _localModBarOpacity.SetValue(If(count > 0, 1.0, 0.0))
            LocalModSelectionBar.Visibility = If(count > 0, Visibility.Visible, Visibility.Collapsed)
        End If
        ' PCL RefreshBars ignores repeated empty refreshes: an existing exit
        ' owns its fade until completion. Availability still follows current input.
        RefreshLocalModSelectionAvailability(LocalModRows.IsEnabled AndAlso _localModReadSucceeded AndAlso Not _localModWriting)
    End Sub
    Private Function RetireLocalModUpdates(Optional afterRetire As Action = Nothing) As Action
        Dim previous = _localModUpdatesRequest
        _localModUpdatesRequest = Nothing
        Try
            _localModUpdates.Clear() : _localModSelection.Clear()
            If LocalModUpdateStatus IsNot Nothing Then LocalModUpdateStatus.Text = "" : LocalModUpdateStatus.ToolTip = Nothing
            RefreshLocalModSelection(False)
            afterRetire?.Invoke()
        Catch
            CancelSubmittedSearchRequest(previous)
            Throw
        End Try
        Return Sub() CancelSubmittedSearchRequest(previous)
    End Function
    Private Async Sub StartLocalModUpdates()
        Await CheckLocalModUpdatesAsync()
    End Sub
    Private Async Function CheckLocalModUpdatesAsync() As Task
        If Not _localModReadSucceeded OrElse _localModScanning OrElse _localModWriting OrElse Not SameLocalModContext(_localModInstance) Then Return
        Dim owner = _localModInstance, entries = _localMods.ToList(), generation = _localModGeneration
        Dim previous = _localModUpdatesRequest, request As New CancellationTokenSource(TimeSpan.FromMinutes(3))
        _localModUpdatesRequest = request
        Dim current As Func(Of Boolean) = Function() Not _closing AndAlso _localModUpdatesRequest Is request AndAlso generation = _localModGeneration AndAlso
            SameLocalModContext(owner) AndAlso LocalModsPage.IsVisible AndAlso Not request.IsCancellationRequested
        Try
            previous?.Cancel()
            If Not current() Then Return
            Dim key = owner.Target.Root & "|" & owner.RunDirectory & "|" & owner.MinecraftVersion & "|" & owner.LoaderSummary & "|" & _communityPreferences.Source & "|" &
                String.Join("|", entries.Select(Function(entry) entry.FilePath & ":" & entry.Sha256))
            LocalModUpdateStatus.Text = "正在检查 Mod 更新…" : LocalModUpdateStatus.ToolTip = Nothing
            Dim result As LocalModUpdateCheckResult
            Dim age = DateTime.UtcNow - _localModUpdateCacheTime
            Dim fromCache = key = _localModUpdateCacheKey AndAlso age >= TimeSpan.Zero AndAlso age < TimeSpan.FromHours(6)
            If fromCache Then
                result = _localModUpdateCache
            ElseIf _localModUpdateProvider IsNot Nothing Then
                result = Await _localModUpdateProvider(owner, entries, request.Token)
            Else
                Dim fingerprints = Await RunManagementWorkerAsync(Function() LocalModFingerprint.Read(owner, entries, request.Token), request.Token)
                Using client = _packCatalogFactory()
                    result = Await client.CheckLocalModsAsync(owner, fingerprints, request.Token)
                End Using
            End If
            Await RunManagementWorkerAsync(Function()
                                              InstalledInstanceCatalog.EnsureUnchanged(owner, request.Token) : Return True
                                          End Function, request.Token)
            If Not current() Then Return
            _localModUpdates.Clear()
            For Each match In result.Matches
                Dim entry = entries.FirstOrDefault(Function(item) item.FilePath = match.Entry.FilePath AndAlso item.Sha256 = match.Entry.Sha256)
                If entry IsNot Nothing Then _localModUpdates(entry.FilePath) = New LocalModUpdate(entry, match.Project, match.Current, match.Release)
            Next
            ' Reading a cached result must not renew its original query lifetime.
            If result.Errors.Count = 0 AndAlso Not fromCache Then _localModUpdateCacheKey = key : _localModUpdateCacheTime = DateTime.UtcNow : _localModUpdateCache = result
            Dim updateCount = _localMods.Where(Function(item) HasLocalModUpdate(item)).Count(), recognized = _localModUpdates.Count
            Dim feedback = If(updateCount > 0, "发现 " & updateCount & " 个可更新的 Mod。",
                If(recognized > 0, "已检查 " & recognized & " 个 Mod，暂无可用更新。",
                    If(entries.Count = 0, "没有可检查更新的 Mod。", "未匹配到 Mod 更新信息。")))
            If result.Errors.Count > 0 Then feedback = If(updateCount > 0, feedback & " ", "") & "部分来源暂不可用，点击刷新重试。"
            LocalModUpdateStatus.Text = feedback
            LocalModUpdateStatus.ToolTip = "已识别 " & recognized & " / " & entries.Count & " 个 Mod，可更新 " & updateCount & " 个。" &
                If(result.Errors.Count > 0, vbCrLf & String.Join(vbCrLf, result.Errors), "")
            ' Online metadata does not own page motion or keyboard focus.
            Dim query = LocalModSearch.Text.Trim()
            Dim updatable = _localMods.Where(Function(item) HasLocalModUpdate(item) AndAlso (query.Length = 0 OrElse
                (item.DisplayName & " " & item.Version & " " & item.FileName & " " & item.Description & " " & String.Join(" ", item.ModIds)).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)).ToList()
            If _localModFilter = "updates" AndAlso Not updatable.SequenceEqual(LocalModRows.Children.OfType(Of PclLocalModItem)().Select(Function(item) DirectCast(item.Tag, LocalModEntry))) Then
                RenderLocalMods()
            Else
                ' Matching membership needs metadata only. Reattaching these
                ' same rows would settle their partly entered selection clocks.
                For Each item In LocalModRows.Children.OfType(Of PclLocalModItem)()
                    ConfigureLocalModSelection(item, DirectCast(item.Tag, LocalModEntry))
                Next
                LocalModUpdatable.Text = "可更新 (" & updatable.Count & ")" : LocalModUpdatable.Visibility = If(updatable.Count > 0 OrElse _localModFilter = "updates", Visibility.Visible, Visibility.Collapsed)
                RefreshLocalModSelectionAvailability(LocalModRows.IsEnabled AndAlso _localModReadSucceeded AndAlso Not _localModWriting)
            End If
        Catch ex As OperationCanceledException When request.IsCancellationRequested
            If _localModUpdatesRequest Is request AndAlso generation = _localModGeneration Then LocalModUpdateStatus.Text = "更新检查超时，点击刷新重试。"
        Catch ex As Exception
            If current() Then LocalModUpdateStatus.Text = "无法检查更新，点击刷新重试。" : LocalModUpdateStatus.ToolTip = ex.Message
            If Not _preview Then Log(ex)
        Finally
            If _localModUpdatesRequest Is request Then _localModUpdatesRequest = Nothing
            request.Dispose()
        End Try
    End Function
    Private Function SelectedLocalMods() As List(Of LocalModEntry)
        Return _localMods.Where(Function(entry) _localModSelection.Contains(entry.FilePath)).ToList()
    End Function
    Private Async Sub ToggleSelectedLocalMods(sender As Object, e As RoutedEventArgs)
        If Not LocalModInputAllowed() OrElse Not DirectCast(sender, UIElement).IsEnabled Then Return
        Dim selected = SelectedLocalMods(), enabled = Boolean.Parse(CStr(DirectCast(sender, FrameworkElement).Tag))
        Await RunLocalModWriteAsync(Function(instance, token) RunManagementWorkerAsync(Function() LocalModActions.SetEnabledBatch(instance, selected, enabled, token), token), If(enabled, "正在启用所选 Mod…", "正在禁用所选 Mod…"))
    End Sub
    Private Async Function ConfirmLocalModBatchAsync(title As String, description As String, Optional returnTarget As FrameworkElement = Nothing) As Task(Of Boolean)
        Dim owner = _localModInstance, generation = _localModGeneration, selected = String.Join("|", _localModSelection.OrderBy(Function(value) value))
        If Not LocalModInputAllowed() OrElse _choosingOnline OrElse _onlinePrompt IsNot Nothing Then Return False
        Dim prompt = PromptOnlinePackAsync(title, description, Nothing, Nothing, True, CancellationToken.None, returnTarget)
        Dim closed = _onlinePromptClosed.Task
        OnlinePackConfirm.Text = "继续" : OnlinePackCancel.Text = "取消"
        Dim answer = Await prompt
        Dim exited = Await closed
        Return exited AndAlso answer = "yes" AndAlso generation = _localModGeneration AndAlso _localModInstance Is owner AndAlso SameLocalModContext(owner) AndAlso
            selected = String.Join("|", _localModSelection.OrderBy(Function(value) value)) AndAlso LocalModInputAllowed()
    End Function
    Private Async Sub DeleteSelectedLocalMods(sender As Object, e As RoutedEventArgs)
        If Not LocalModInputAllowed() OrElse Not LocalModDeleteSelected.IsEnabled Then Return
        Dim selected = SelectedLocalMods()
        If Not Await ConfirmLocalModBatchAsync("删除所选 Mod", "将移出 " & selected.Count & " 个 Mod。原文件保留在运行目录的 .pcl-db-mod-backups 中，可手动恢复。共享目录中的其他实例也会受到影响。", LocalModDeleteSelected) Then Return
        Await RunLocalModWriteAsync(Function(instance, token) RunManagementWorkerAsync(Function() LocalModActions.DeleteBatch(instance, selected, token), token), "正在移出所选 Mod…")
    End Sub
    Private Async Sub UpdateSelectedLocalMods(sender As Object, e As RoutedEventArgs)
        If Not LocalModInputAllowed() OrElse Not LocalModUpdateSelected.IsEnabled Then Return
        Dim selected = SelectedLocalMods().Where(Function(entry) HasLocalModUpdate(entry)).Select(Function(entry) _localModUpdates(entry.FilePath)).ToList()
        If Not Await ConfirmLocalModBatchAsync("更新所选 Mod", "将更新 " & selected.Count & " 个 Mod。更新可能影响整合包兼容性；原文件将保留备份，并保留启用或禁用状态。共享目录中的其他实例也会受到影响。", LocalModUpdateSelected) Then Return
        Dim limit As Double
        If Not TryGetSpeedLimit(limit) Then Return
        Dim policySnapshot = Policy(), connections = ConnectionCount(), cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PclDownloadBridge", "ResourceCache")
        _localModUpdateCacheKey = Nothing
        ' Settings use MB/s; the shared download budget consumes bytes/s.
        Dim bytesPerSecond = CLng(limit * 1024 * 1024)
        Await RunLocalModWriteAsync(Function(instance, token) _localModUpdateInstaller(instance, selected, cache, policySnapshot, token, connections, bytesPerSecond), "正在下载并更新所选 Mod…")
    End Sub
    Private Sub DownloadNewLocalMod(sender As Object, e As RoutedEventArgs)
        If Not LocalModInputAllowed() OrElse Not LocalModDownload.IsEnabled Then Return
        Dim owner = _localModInstance, main = ActiveSurface(), generation = _resourceCategoryGeneration + 1
        SelectResourceKind(CommunityResourceKind.Mod, Sub() ConfigureLocalModSearch(owner))
        If _closing OrElse _busy OrElse generation <> _resourceCategoryGeneration OrElse ActiveSurface() IsNot main OrElse _resourceKind <> CommunityResourceKind.Mod OrElse
            _downloadView <> DownloadView.Modpacks OrElse _localModInstance IsNot owner OrElse Not SameLocalModContext(owner) OrElse Not LocalModInputAllowed() Then Return
        ShowDownload(Nothing, Nothing)
    End Sub
    Private Sub ConfigureLocalModSearch(instance As InstalledInstance)
        ' The category transaction defers old notifications until these target filters are bound.
        CancelResourceRequests() : _packSearchSession = Nothing : _packSearchPage = 0 : _packSearchScroll = 0 : _resourceBack.Clear()
        PackSearchText.Text = "" : PackSearchGame.Text = instance.MinecraftVersion : PackSearchCategory.SelectedIndex = 0
        ResourceLoader.SelectedIndex = 0
        Dim installed = System.Text.RegularExpressions.Regex.Matches(instance.LoaderSummary, "(?:\A| \+ )(NeoForge|Forge|Fabric|Quilt|LiteLoader)(?: |\z)", System.Text.RegularExpressions.RegexOptions.IgnoreCase).
            Cast(Of System.Text.RegularExpressions.Match)().Select(Function(match) match.Groups(1).Value).ToArray()
        For index = 1 To ResourceLoader.Items.Count - 1
            Dim loader = CStr(DirectCast(ResourceLoader.Items(index), FrameworkElement).Tag)
            If installed.Contains(loader, StringComparer.OrdinalIgnoreCase) Then ResourceLoader.SelectedIndex = index : Exit For
        Next
        ChangePackMode(PackBrowserMode.Search) : PackProjectRows.Children.Clear()
        PackSearchHint.Visibility = Visibility.Collapsed : PackPagingCard.Visibility = Visibility.Collapsed
    End Sub
End Class
