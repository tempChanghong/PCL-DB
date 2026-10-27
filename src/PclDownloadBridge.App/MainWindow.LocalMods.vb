Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media.Animation
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private _localMods As New List(Of LocalModEntry)
    Private _localModInstance As InstalledInstance
    Private _localModRequest As CancellationTokenSource
    Private _localModGeneration As Long
    Private _localModScanning As Boolean
    Private _localModWaitingForWrite As Boolean
    Private _localModFolderRequest As CancellationTokenSource
    Private _localModReadSucceeded As Boolean
    Private _localModWriting As Boolean
    Private _localModWriteRequest As CancellationTokenSource
    Private _localModOperationGeneration As Long
    Private _localModWriteOwner As InstalledInstance
    Private _localModFilter As String = "all"
    Private _localModDetailOffset As Double
    Private _localModFocusRestore As Action
    Private _localModFilterPending As Boolean
    Private _localModInteractionFlushing As Boolean
    Private _localModSearchClearMotion As PclMotionSlot
    Private _localModSearchClearPolicy As PclControlMotionPolicy
    Private ReadOnly _localModRowViews As New Dictionary(Of LocalModEntry, Grid)
    Private _localModProvider As Func(Of InstalledInstance, CancellationToken, Task(Of List(Of LocalModEntry))) =
        Function(instance, token) RunManagementWorkerAsync(Function() LocalModCatalog.Scan(instance, token), token)
    Private _localModInstanceReader As Func(Of InstalledInstance, CancellationToken, Task(Of InstalledInstance)) =
        Function(instance, token) RunManagementWorkerAsync(Function() InstalledInstanceCatalog.Read(instance.Target, instance.Name, token), token)
    Private _localModImporter As Func(Of InstalledInstance, String(), CancellationToken, Task(Of LocalModActionResult)) =
        Function(instance, files, token) RunManagementWorkerAsync(Function() LocalModActions.ImportFiles(instance, files, token), token)
    Private _localModToggle As Func(Of InstalledInstance, LocalModEntry, Boolean, CancellationToken, Task(Of LocalModActionResult)) =
        Function(instance, entry, enabled, token) RunManagementWorkerAsync(Function() LocalModActions.SetEnabled(instance, entry, enabled, token), token)

    Private Function SameLocalModContext(instance As InstalledInstance) As Boolean
        Return SameLocalModIdentity(instance) AndAlso
            instance.Problem Is Nothing AndAlso _managedInstance.Problem Is Nothing AndAlso
            Not String.IsNullOrEmpty(instance.RunDirectory) AndAlso Not String.IsNullOrEmpty(_managedInstance.RunDirectory) AndAlso
            SameGameRoot(instance.RunDirectory, _managedInstance.RunDirectory)
    End Function
    Private Function SameLocalModIdentity(instance As InstalledInstance) As Boolean
        Return instance IsNot Nothing AndAlso _managedInstance IsNot Nothing AndAlso _selectedFolder IsNot Nothing AndAlso instance.Name = _managedInstance.Name AndAlso
            SameManagementTarget(instance.Target, _managedInstance.Target) AndAlso SameManagementTarget(instance.Target, CurrentInstallTarget())
    End Function
    Private Function LocalModInputAllowed() As Boolean
        Return Not _closing AndAlso Not _restoringSettings AndAlso Not _restoringLaunchers AndAlso ManagementPage.Visibility = Visibility.Visible AndAlso LocalModsPage.Visibility = Visibility.Visible AndAlso
            PageSurface.IsEnabled AndAlso PageSurface.IsHitTestVisible AndAlso ManagementViewHost.IsHitTestVisible AndAlso SameLocalModContext(_localModInstance)
    End Function
    Private Function LocalModRefreshAllowed() As Boolean
        ' Refresh repairs a stale/broken snapshot by rereading its native
        ' instance. File actions still require the complete usable context.
        Return Not _closing AndAlso Not _restoringSettings AndAlso Not _restoringLaunchers AndAlso ManagementPage.Visibility = Visibility.Visible AndAlso LocalModsPage.Visibility = Visibility.Visible AndAlso
            PageSurface.IsHitTestVisible AndAlso ManagementViewHost.IsHitTestVisible AndAlso SameLocalModIdentity(_localModInstance)
    End Function
    Private Function LocalModReadWaitsForWrite(instance As InstalledInstance) As Boolean
        ' PCL LocalResourceLoaders waits for updates to this Mods folder.
        ' Other physical directories remain readable during a file operation.
        Return _localModWriting AndAlso _localModWriteOwner IsNot Nothing AndAlso (instance Is Nothing OrElse instance.Problem IsNot Nothing OrElse
            String.IsNullOrEmpty(instance.RunDirectory) OrElse SameGameRoot(instance.RunDirectory, _localModWriteOwner.RunDirectory))
    End Function
    Private Sub InvalidateChangedLocalModContext()
        If LocalModsPage.Visibility <> Visibility.Visible OrElse _managedInstance Is Nothing OrElse SameLocalModContext(_localModInstance) Then Return
        CancelLocalModRequest()
        LocalModStatus.Text = If(_managedInstance.Problem IsNot Nothing, "无法读取 Mod 列表：" & _managedInstance.Problem, "版本目录已变化，请刷新列表。")
        RefreshLocalModAvailability()
    End Sub
    Private Sub CancelLocalModRequest(Optional afterRetire As Action = Nothing)
        Dim folderRequest = _localModFolderRequest, request = _localModRequest
        ' PCL ModLoader changes state before notifying cancellation. A token
        ' callback may synchronously restart a read, so retire all old ownership
        ' and input state before cancelling any of the captured requests.
        _localModGeneration += 1
        _localModFolderRequest = Nothing : _localModRequest = Nothing : _localModScanning = False
        _localModReadSucceeded = False
        _localModWaitingForWrite = False
        _localModFocusRestore = Nothing : _localModFilterPending = False
        RetireLocalModUpdates(afterRetire)
        folderRequest?.Cancel() : request?.Cancel()
    End Sub
    Private Async Sub ShowLocalMods(sender As Object, e As RoutedEventArgs)
        If _closing OrElse _managedInstance Is Nothing OrElse _managedInstance.Problem IsNot Nothing OrElse _managedInstance.RunDirectory Is Nothing OrElse
            ManagementPage.Visibility <> Visibility.Visible OrElse ManagementDetailPage.Visibility <> Visibility.Visible OrElse
            Not PageSurface.IsHitTestVisible OrElse Not ManagementViewHost.IsHitTestVisible Then Return
        CancelManagementRequest() : CancelLocalModRequest()
        _localModDetailOffset = ManagementDetailPage.VerticalOffset
        If Not SameLocalModContext(_localModInstance) Then
            _localMods = New List(Of LocalModEntry)()
            LocalModSearch.Text = "" : _localModFilter = "all"
        End If
        _localModInstance = _managedInstance
        ChangeManagementContent(Sub()
                                    ManagementDetailPage.Visibility = Visibility.Collapsed
                                    ManagementListPage.Visibility = Visibility.Collapsed
                                    LocalModsPage.Visibility = Visibility.Visible
                                    LocalModsPage.ScrollToHome()
                                    RenderLocalMods()
                                    RefreshTitleNavigation()
                                End Sub, LocalModsPage)
        ' A cached detail can outlive an external launcher's isolation setting.
        Await LoadLocalModsAsync(True, refreshInstance:=True)
        If LocalModInputAllowed() Then FocusManagementViewport()
    End Sub
    Private Async Sub RefreshLocalMods(sender As Object, e As RoutedEventArgs)
        If Not LocalModRefreshAllowed() OrElse _localModWriting Then Return
        _localModUpdateCacheKey = Nothing
        Await LoadLocalModsAsync(True, refreshInstance:=True)
    End Sub
    Private Async Function LoadLocalModsAsync(force As Boolean, Optional refreshInstance As Boolean = False) As Task
        If _closing OrElse ManagementPage.Visibility <> Visibility.Visible OrElse LocalModsPage.Visibility <> Visibility.Visible OrElse
            _managedInstance Is Nothing OrElse
            (_localModScanning AndAlso Not force) Then Return
        If LocalModReadWaitsForWrite(_managedInstance) Then _localModWaitingForWrite = True : RefreshLocalModAvailability() : Return
        If Not refreshInstance AndAlso (_managedInstance.RunDirectory Is Nothing OrElse _managedInstance.Problem IsNot Nothing) Then Return
        Dim instance = _managedInstance, beforeGeneration = _localModGeneration, folderRequest = _localModFolderRequest
        If refreshInstance Then
            Dim parentGeneration = _managementGeneration + 1, folderGeneration = _managementFolderGeneration + 1
            CancelManagementRequest()
            If parentGeneration <> _managementGeneration OrElse folderGeneration <> _managementFolderGeneration OrElse beforeGeneration <> _localModGeneration OrElse
                _localModFolderRequest IsNot folderRequest OrElse _managedInstance IsNot instance OrElse Not SameLocalModIdentity(instance) OrElse _closing OrElse
                ManagementPage.Visibility <> Visibility.Visible OrElse LocalModsPage.Visibility <> Visibility.Visible Then Return
        End If
        Dim request As New CancellationTokenSource(), generation As Long = -1
        Try
            ' Retire old state, then reserve this read before notifying any old
            ' update/folder/list token. A forced replacement can retire it again.
            CancelLocalModRequest(Sub()
                                      generation = _localModGeneration
                                      _localModInstance = instance : _localModRequest = request : _localModScanning = True
                                      LocalModStatus.Text = "正在读取 Mod 列表…"
                                      RefreshLocalModAvailability()
                                  End Sub)
            If request.IsCancellationRequested OrElse _localModRequest IsNot request OrElse generation <> _localModGeneration OrElse _closing OrElse Not SameLocalModIdentity(instance) OrElse
                ManagementPage.Visibility <> Visibility.Visible OrElse LocalModsPage.Visibility <> Visibility.Visible Then Return
            If refreshInstance Then
                Dim previous = instance
                instance = Await _localModInstanceReader(previous, request.Token)
                If request.IsCancellationRequested OrElse generation <> _localModGeneration OrElse _closing OrElse Not SameLocalModIdentity(previous) OrElse
                    ManagementPage.Visibility <> Visibility.Visible OrElse LocalModsPage.Visibility <> Visibility.Visible Then Return
                If instance Is Nothing OrElse instance.Name <> previous.Name OrElse Not SameManagementTarget(instance.Target, previous.Target) Then Throw New InvalidDataException("实例读取来源已变化，请重新选择实例。")
                If instance.Problem IsNot Nothing Then Throw New InvalidDataException(instance.Problem)
                If String.IsNullOrEmpty(instance.RunDirectory) Then Throw New InvalidDataException("无法确定实例的实际运行目录。")
            End If
            If LocalModReadWaitsForWrite(instance) Then _localModWaitingForWrite = True : RefreshLocalModAvailability() : Return
            Dim entries = Await _localModProvider(instance, request.Token)
            If request.IsCancellationRequested OrElse generation <> _localModGeneration OrElse _closing OrElse Not SameLocalModIdentity(instance) OrElse
                (Not refreshInstance AndAlso Not SameLocalModContext(instance)) OrElse
                ManagementPage.Visibility <> Visibility.Visible OrElse LocalModsPage.Visibility <> Visibility.Visible Then Return
            ChangeManagementContent(Sub()
                                        If refreshInstance Then
                                            Dim index = _managementInstances.FindIndex(Function(current) current.Name = instance.Name AndAlso SameManagementTarget(current.Target, instance.Target))
                                            If index >= 0 Then _managementInstances(index) = instance
                                            _managedInstance = instance
                                            RenderManagementRows()
                                        End If
                                        _localModInstance = instance
                                        _localMods = entries
                                        _localModReadSucceeded = True
                                        RenderLocalMods()
                                        LocalModStatus.Text = If(entries.Count = 0, "可从文件安装 Mod，或将文件拖入此页。", "")
                                        If Not _preview OrElse _localModAutoCheckForChecks Then Dispatcher.BeginInvoke(New Action(Sub()
                                                                                                  If generation = _localModGeneration AndAlso _localModReadSucceeded AndAlso Not _closing Then StartLocalModUpdates()
                                                                                              End Sub))
                                    End Sub)
        Catch ex As OperationCanceledException When request.IsCancellationRequested
        Catch ex As Exception
            If generation = _localModGeneration AndAlso _localModRequest Is request AndAlso Not _closing AndAlso LocalModsPage.Visibility = Visibility.Visible Then
                If Not _preview Then Log(ex)
                LocalModStatus.Text = "无法读取 Mod 列表：" & ex.Message
            End If
        Finally
            If generation = _localModGeneration AndAlso _localModRequest Is request Then _localModRequest = Nothing : _localModScanning = False
            request.Dispose()
            RefreshLocalModAvailability()
            FlushLocalModInteraction()
        End Try
    End Function
    Private _localModRenderedStyle As Integer
    Private Sub RenderLocalMods()
        _localModFilterPending = False
        LocalModRows.Children.Clear()
        If _localModRenderedStyle <> _communityPreferences.LocalModStyle Then _localModRowViews.Clear()
        _localModRenderedStyle = _communityPreferences.LocalModStyle
        Dim current = New HashSet(Of LocalModEntry)(_localMods)
        For Each obsolete In _localModRowViews.Keys.Where(Function(entry) Not current.Contains(entry)).ToList()
            _localModRowViews.Remove(obsolete)
        Next
        LocalModAll.Checked = _localModFilter = "all" : LocalModEnabled.Checked = _localModFilter = "enabled" : LocalModDisabled.Checked = _localModFilter = "disabled" : LocalModUpdatable.Checked = _localModFilter = "updates"
        Dim query = LocalModSearch.Text.Trim()
        Dim candidates = _localMods.Where(Function(entry) query.Length = 0 OrElse
            (entry.DisplayName & " " & entry.Version & " " & entry.FileName & " " & entry.Description & " " & String.Join(" ", entry.ModIds)).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList()
        Dim enabledCount = candidates.Where(Function(entry) entry.Enabled).Count(), disabledCount = candidates.Count - enabledCount
        LocalModAll.Text = If(query.Length = 0, "全部", "搜索结果") & " (" & candidates.Count & ")"
        LocalModEnabled.Text = "启用 (" & enabledCount & ")" : LocalModDisabled.Text = "禁用 (" & disabledCount & ")"
        Dim updateCount = candidates.Where(Function(entry) HasLocalModUpdate(entry)).Count()
        LocalModUpdatable.Text = "可更新 (" & updateCount & ")"
        LocalModUpdatable.Visibility = If(updateCount > 0 OrElse _localModFilter = "updates", Visibility.Visible, Visibility.Collapsed)
        LocalModEnabled.Visibility = If(_localModFilter = "enabled" OrElse (enabledCount > 0 AndAlso enabledCount < candidates.Count), Visibility.Visible, Visibility.Collapsed)
        LocalModDisabled.Visibility = If(_localModFilter = "disabled" OrElse disabledCount > 0, Visibility.Visible, Visibility.Collapsed)
        Dim visible = candidates.Where(Function(entry) _localModFilter = "all" OrElse (_localModFilter = "updates" AndAlso HasLocalModUpdate(entry)) OrElse
            (_localModFilter = "enabled" AndAlso entry.Enabled) OrElse (_localModFilter = "disabled" AndAlso Not entry.Enabled)).ToList()
        _localModSelection.IntersectWith(visible.Select(Function(entry) entry.FilePath))
        _localModRenderingSelection = True
        For Each entry In visible
            Dim captured = entry
            Dim existing As Grid = Nothing
            If _localModRowViews.TryGetValue(captured, existing) Then
                ConfigureLocalModSelection(DirectCast(existing, PclLocalModItem), captured)
                LocalModRows.Children.Add(existing)
                Continue For
            End If
            Dim row As New PclLocalModItem(captured, _communityPreferences.LocalModStyle = 1)
            AddHandler row.ToggleButton.Click, Async Sub() Await ToggleLocalModAsync(captured)
            AddHandler row.CheckedChanged, Sub() LocalModChecked(row, captured)
            ConfigureLocalModSelection(row, captured)
            _localModRowViews.Add(captured, row)
            LocalModRows.Children.Add(row)
        Next
        _localModRenderingSelection = False
        LocalModSummary.Text = visible.Count & " / " & _localMods.Count & " 个 Mod"
        LocalModEmpty.Visibility = If(visible.Count = 0, Visibility.Visible, Visibility.Collapsed)
        LocalModEmpty.Text = If(_localMods.Count = 0, "尚未安装 Mod。", "没有找到匹配的 Mod。")
        If _localModInstance IsNot Nothing Then
            LocalModDirectory.Text = Path.Combine(_localModInstance.RunDirectory, "mods")
            Dim sharedDirectory = Not _localModInstance.IsIsolated.GetValueOrDefault() OrElse _managementInstances.Where(Function(instance) instance.RunDirectory IsNot Nothing AndAlso SameGameRoot(instance.RunDirectory, _localModInstance.RunDirectory)).Count() > 1
            LocalModSharedHint.Text = "这个 Mods 目录可能被其他实例共用，启用、禁用和导入会同时影响它们。"
            LocalModSharedHint.Visibility = If(sharedDirectory, Visibility.Visible, Visibility.Collapsed)
        End If
        RefreshLocalModAvailability()
        RefreshLocalModSelection(True)
    End Sub
    Private Shared Function ShortLocalModText(value As String, maximum As Integer) As String
        If value Is Nothing OrElse maximum <= 0 Then Return ""
        If value.Length <= maximum Then Return value
        Dim count = maximum - 1
        If count > 0 AndAlso Char.IsHighSurrogate(value(count - 1)) Then count -= 1
        Return value.Substring(0, count) & "…"
    End Function
    Private Sub LocalModSearchChanged(sender As Object, e As TextChangedEventArgs)
        RefreshLocalModSearchClear(True)
        If LocalModRows Is Nothing OrElse _closing OrElse _restoringSettings OrElse _restoringLaunchers OrElse
            ManagementPage.Visibility <> Visibility.Visible OrElse LocalModsPage.Visibility <> Visibility.Visible OrElse Not SameLocalModContext(_localModInstance) Then Return
        If Not LocalModInputAllowed() Then
            ' Text editing commands and accessibility can change the editor while
            ' page clocks gate normal key input. Apply the latest query at handoff.
            _localModFilterPending = True
            Return
        End If
        _surfaceMotion.Finish() : _managementMotion.Finish()
        RenderLocalMods()
    End Sub
    Private Sub InitializeLocalModSearchClear(sender As Object, e As EventArgs)
        If _localModSearchClearMotion IsNot Nothing Then Return
        Dim button = LocalModSearchClear
        If button Is Nothing OrElse LocalModSearch Is Nothing Then Return
        _localModSearchClearMotion = New PclMotionSlot(button, UIElement.OpacityProperty)
        _localModSearchClearPolicy = New PclControlMotionPolicy(LocalModSearch, Sub() RefreshLocalModSearchClear(False), requiresInteraction:=False)
    End Sub
    Private Sub RefreshLocalModSearchClear(animate As Boolean)
        If LocalModSearchClear Is Nothing OrElse LocalModSearch Is Nothing OrElse _localModSearchClearMotion Is Nothing Then Return
        Dim shown = LocalModSearch.Text.Length > 0, opacity = If(shown, 1.0, 0.0)
        LocalModSearchClear.IsHitTestVisible = shown : LocalModSearchClear.Focusable = shown
        If animate AndAlso _localModSearchClearPolicy.CanAnimate Then
            _localModSearchClearMotion.Animate(New DoubleAnimation(LocalModSearchClear.Opacity, opacity, TimeSpan.FromMilliseconds(90)), opacity, _localModSearchClearPolicy.PauseForChecks)
        Else
            _localModSearchClearMotion.SetValue(opacity)
        End If
    End Sub
    Private Sub ClearLocalModSearch(sender As Object, e As RoutedEventArgs)
        If sender IsNot LocalModSearchClear OrElse Not LocalModInputAllowed() OrElse Not LocalModSearchClear.IsHitTestVisible OrElse LocalModSearch.Text.Length = 0 Then Return
        Dim instance = _localModInstance, generation = _localModGeneration, inputVersion = _downloadFocusInputVersion, previous = Keyboard.FocusedElement
        LocalModSearch.Text = ""
        If Not LocalModInputAllowed() OrElse _localModInstance IsNot instance OrElse generation <> _localModGeneration OrElse inputVersion <> _downloadFocusInputVersion Then Return
        Dim current = Keyboard.FocusedElement, source = PresentationSource.FromVisual(LocalModsPage)
        If current IsNot Nothing AndAlso current IsNot previous AndAlso current IsNot LocalModSearch AndAlso current IsNot Me AndAlso current IsNot source?.RootVisual Then Return
        LocalModSearch.Focus()
    End Sub
    Private Sub FlushLocalModInteraction()
        If _localModInteractionFlushing OrElse Not LocalModInputAllowed() Then Return
        _localModInteractionFlushing = True
        Try
            If _localModFilterPending Then
                _localModFilterPending = False
                _surfaceMotion.Finish() : _managementMotion.Finish()
                RenderLocalMods()
            End If
            Dim restore = _localModFocusRestore
            _localModFocusRestore = Nothing
            restore?.Invoke()
        Finally
            _localModInteractionFlushing = False
        End Try
    End Sub
    Private Sub QueueLocalModWriteFocus(owner As InstalledInstance, entry As LocalModEntry, previous As IInputElement, inputVersion As Long, operationGeneration As Long, readGeneration As Long)
        If entry Is Nothing Then Return
        _localModFocusRestore = Sub()
                                   If _closing OrElse _localModWriting OrElse _localModScanning OrElse operationGeneration <> _localModOperationGeneration OrElse
                                       readGeneration <> _localModGeneration OrElse Not SameLocalModContext(owner) OrElse Not LocalModInputAllowed() Then Return
                                   Dim current = Keyboard.FocusedElement
                                   ' Disabling/detaching the old row can return WPF
                                   ' focus to this presentation root before handoff.
                                   Dim source = PresentationSource.FromVisual(LocalModsPage)
                                   If current IsNot Nothing AndAlso current IsNot previous AndAlso current IsNot Me AndAlso current IsNot LocalModsPage AndAlso current IsNot source?.RootVisual Then Return
                                   Dim destination As UIElement = LocalModsPage
                                   If inputVersion = _downloadFocusInputVersion AndAlso _localModReadSucceeded Then
                                       Dim replacement = _localMods.FirstOrDefault(Function(item) String.Equals(item.FileName, entry.FileName, StringComparison.OrdinalIgnoreCase))
                                       If replacement Is Nothing Then replacement = _localMods.FirstOrDefault(Function(item) String.Equals(item.EnabledName, entry.EnabledName, StringComparison.OrdinalIgnoreCase))
                                       Dim row As Grid = Nothing
                                       If replacement IsNot Nothing AndAlso _localModRowViews.TryGetValue(replacement, row) AndAlso LocalModRows.Children.Contains(row) Then
                                           Dim button = row.Children.OfType(Of PclIconButton)().FirstOrDefault()
                                           If button IsNot Nothing AndAlso button.IsEnabled AndAlso button.IsVisible Then destination = button
                                       End If
                                   End If
                                   Dim offset = LocalModsPage.VerticalOffset
                                   FocusManager.SetFocusedElement(FocusManager.GetFocusScope(destination), destination)
                                   destination.Focus()
                                   ' Focus must not bring a newly built row over the
                                   ' user's retained viewport (PCL MyScrollViewer #3854).
                                   LocalModsPage.ScrollToVerticalOffset(offset)
                               End Sub
        FlushLocalModInteraction()
    End Sub
    Private Sub FilterLocalMods(sender As Object, e As RoutedEventArgs)
        If Not LocalModInputAllowed() Then Return
        _localModFilter = CStr(DirectCast(sender, FrameworkElement).Tag)
        _surfaceMotion.Finish() : _managementMotion.Finish()
        RenderLocalMods()
    End Sub
    Private Sub BackFromLocalMods()
        If LocalModsPage.Visibility <> Visibility.Visible Then Return
        CancelLocalModRequest()
        ChangeManagementContent(Sub()
                                    LocalModsPage.Visibility = Visibility.Collapsed
                                    ManagementDetailPage.Visibility = Visibility.Visible
                                    RenderManagedInstance()
                                    ManagementDetailPage.ScrollToVerticalOffset(_localModDetailOffset)
                                    RefreshTitleNavigation()
                                End Sub, ManagementDetailPage)
        FocusManagementViewport()
    End Sub
    Private Sub RefreshLocalModAvailability()
        If LocalModRows Is Nothing Then Return
        Dim canBrowse = SameLocalModContext(_localModInstance)
        LocalModSearch.IsEnabled = canBrowse : LocalModSearchClear.IsEnabled = canBrowse
        For Each filterTab As FrameworkElement In {LocalModAll, LocalModEnabled, LocalModDisabled, LocalModUpdatable} : filterTab.IsEnabled = canBrowse : Next
        Dim canWrite = _localModReadSucceeded AndAlso Not _busy AndAlso Not _choosingOnline AndAlso Not _restoringSettings AndAlso Not _restoringLaunchers AndAlso Not _localModWriting AndAlso Not _localModScanning AndAlso SameLocalModContext(_localModInstance)
        LocalModRows.IsEnabled = canWrite
        LocalModImport.IsEnabled = canWrite : LocalModRefresh.IsEnabled = Not _localModWriting
        LocalModSelectAll.IsEnabled = canWrite AndAlso LocalModRows.Children.Count > 0
        LocalModDownload.IsEnabled = canWrite
        LocalModOpen.ToolTip = LocalModDirectory.Text
        RefreshLocalModSelectionAvailability(canWrite)
        LocalModOpen.IsEnabled = Not _localModScanning AndAlso Not _localModWriting AndAlso SameLocalModContext(_localModInstance) AndAlso Directory.Exists(Path.Combine(_localModInstance.RunDirectory, "mods"))
        LocalModCancel.Visibility = If(_localModWriting, Visibility.Visible, Visibility.Collapsed)
        LocalModCancel.IsEnabled = _localModWriteRequest IsNot Nothing AndAlso Not _localModWriteRequest.IsCancellationRequested
        If _localModWriting AndAlso _localModWriteOwner IsNot Nothing Then
            LocalModCancel.ToolTip = "取消 " & _localModWriteOwner.Name & " 的 Mod 文件操作"
            If LocalModWriterFeedbackOwned() Then
                If _localModWriteRequest IsNot Nothing AndAlso _localModWriteRequest.IsCancellationRequested Then
                    LocalModStatus.Text = "正在等待文件操作退出…"
                ElseIf Not SameLocalModContext(_localModWriteOwner) Then
                    LocalModStatus.Text = "正在处理 " & _localModWriteOwner.Name & " 的 Mod 文件，请等待操作结束。"
                End If
            End If
        End If
    End Sub
    Private Function LocalModWriterFeedbackOwned() As Boolean
        Return _localModWriteOwner IsNot Nothing AndAlso (SameLocalModIdentity(_localModWriteOwner) OrElse _localModWaitingForWrite OrElse
            SameGameRoot(_managedInstance?.RunDirectory, _localModWriteOwner.RunDirectory))
    End Function
    Private Sub RefreshLocalModWriteGates()
        RefreshManagementAvailability()
        GameFolderCard.IsEnabled = Not _busy AndAlso Not _localModWriting
        ExternalLauncherCard.IsEnabled = Not _busy AndAlso Not _localModWriting
        ChangeFolderButton.IsEnabled = Not _busy AndAlso Not _localModWriting
        RefreshInstallAvailability()
        RefreshLocalModAvailability()
    End Sub
    Private Async Sub ChooseLocalMods(sender As Object, e As RoutedEventArgs)
        If Not LocalModInputAllowed() OrElse Not LocalModImport.IsEnabled Then Return
        Dim owner = _localModInstance
        Dim dialog As New Microsoft.Win32.OpenFileDialog With {.Title = "从文件安装 Mod", .Filter = "Mod 文件 (*.jar;*.litemod;*.disabled;*.old)|*.jar;*.litemod;*.disabled;*.old", .Multiselect = True, .CheckFileExists = True}
        If dialog.ShowDialog(Me) <> True OrElse Not LocalModInputAllowed() OrElse _localModInstance IsNot owner Then Return
        Await ImportLocalModFilesAsync(dialog.FileNames)
    End Sub
    Private Async Function ImportLocalModFilesAsync(paths As String()) As Task
        If paths Is Nothing OrElse paths.Length = 0 Then Return
        Dim files = paths.ToArray()
        Await RunLocalModWriteAsync(Function(instance, token) _localModImporter(instance, files, token), "正在导入 Mod…")
    End Function
    Private Async Function ToggleLocalModAsync(entry As LocalModEntry) As Task
        If entry Is Nothing OrElse Not _localMods.Contains(entry) OrElse Not entry.CanToggle Then Return
        Await RunLocalModWriteAsync(Function(instance, token) _localModToggle(instance, entry, Not entry.Enabled, token), If(entry.Enabled, "正在禁用 Mod…", "正在启用 Mod…"))
    End Function
    Private Async Function RunLocalModWriteAsync(operation As Func(Of InstalledInstance, CancellationToken, Task(Of LocalModActionResult)), message As String) As Task
        If Not LocalModInputAllowed() OrElse Not _localModReadSucceeded OrElse _busy OrElse _choosingOnline OrElse _localModWriting OrElse _localModScanning Then Return
        Dim focused = Keyboard.FocusedElement, inputVersion = _downloadFocusInputVersion
        Dim focusedRow = LocalModRows.Children.OfType(Of Grid)().FirstOrDefault(Function(row) row.IsKeyboardFocusWithin)
        Dim focusedEntry = TryCast(focusedRow?.Tag, LocalModEntry)
        _localModOperationGeneration += 1
        Dim operationGeneration = _localModOperationGeneration
        Dim owner = _localModInstance, request As New CancellationTokenSource(), outcome As String = Nothing, taskGeneration = _taskGeneration
        _localModWriting = True : _localModWriteRequest = request
        _localModWriteOwner = owner
        Try
            ' Bind the accepted writer before cancellation can navigate or
            ' reenter another file action. Its target must remain immutable.
            CancelLocalModRequest()
            If SameLocalModContext(owner) AndAlso LocalModsPage.Visibility = Visibility.Visible Then LocalModStatus.Text = message
            RefreshLocalModWriteGates()
            request.Token.ThrowIfCancellationRequested()
            Dim result = Await operation(owner, request.Token)
            outcome = "完成：更改 " & result.ChangedCount & " 个，跳过 " & result.SkippedCount & " 个。" &
                If(result.Warnings.Count = 0, "", " " & ShortLocalModText(String.Join("；", result.Warnings), 1024)) & If(result.BackupDirectory Is Nothing, "", " 原文件备份：" & result.BackupDirectory)
        Catch ex As OperationCanceledException When request.IsCancellationRequested
            outcome = "操作已取消，可重新尝试。"
        Catch ex As Exception
            If Not _preview Then Log(ex)
            outcome = ex.Message
        Finally
            _localModWriting = False : _localModWriteRequest = Nothing : request.Dispose()
            _localModWriteOwner = Nothing
            _managementDirty = True
            RefreshLocalModWriteGates()
        End Try
        If _closing Then Return
        If SameLocalModContext(owner) AndAlso ManagementPage.Visibility = Visibility.Visible AndAlso LocalModsPage.Visibility = Visibility.Visible Then
            ' Like PCL's reload input, resolve the current run directory again.
            ' Native launcher settings can change without a management scan.
            Dim refresh = LoadLocalModsAsync(True, refreshInstance:=True), readGeneration = _localModGeneration
            Await refresh
            If operationGeneration = _localModOperationGeneration AndAlso readGeneration = _localModGeneration Then
                QueueLocalModWriteFocus(owner, focusedEntry, focused, inputVersion, operationGeneration, readGeneration)
            End If
            If operationGeneration = _localModOperationGeneration AndAlso readGeneration = _localModGeneration AndAlso _localModReadSucceeded AndAlso Not _localModWriting AndAlso Not _localModScanning AndAlso
                SameLocalModContext(owner) AndAlso LocalModsPage.Visibility = Visibility.Visible AndAlso Not _closing Then LocalModStatus.Text = outcome
            If operationGeneration = _localModOperationGeneration AndAlso readGeneration = _localModGeneration AndAlso Not _localModWriting AndAlso Not _busy AndAlso Not _closing AndAlso
                taskGeneration = _taskGeneration AndAlso (Not _localModReadSucceeded OrElse Not SameLocalModContext(owner)) Then StatusText.Text = owner.Name & " · " & outcome
        Else
            ' PCL coordinates reloads by the actual Mods directory. A different
            ' directory's accepted read/error does not belong to this writer.
            ' The explicit wait flag also covers native rereads redirected into
            ' the writer's directory before the cached instance was rebound.
            If ManagementPage.Visibility = Visibility.Visible AndAlso LocalModsPage.Visibility = Visibility.Visible AndAlso Not _localModScanning AndAlso
                (_localModWaitingForWrite OrElse SameGameRoot(_localModInstance?.RunDirectory, owner.RunDirectory)) Then Await LoadLocalModsAsync(True, refreshInstance:=True)
            If operationGeneration = _localModOperationGeneration AndAlso Not _localModWriting AndAlso Not _busy AndAlso Not _closing AndAlso taskGeneration = _taskGeneration Then StatusText.Text = owner.Name & " · " & outcome
        End If
    End Function
    Private Sub CancelLocalModOperation(sender As Object, e As RoutedEventArgs)
        Dim request = _localModWriteRequest
        If _closing OrElse Not _localModWriting OrElse request Is Nothing OrElse request.IsCancellationRequested Then Return
        RefreshLocalModAvailability()
        If LocalModWriterFeedbackOwned() Then LocalModStatus.Text = "正在等待文件操作退出…"
        LocalModCancel.IsEnabled = False
        request.Cancel()
    End Sub
    Private Async Sub OpenLocalModFolder(sender As Object, e As RoutedEventArgs)
        Await OpenLocalModFolderAsync()
    End Sub
    Private Async Function OpenLocalModFolderAsync() As Task
        If Not LocalModInputAllowed() OrElse _localModScanning OrElse _localModWriting Then Return
        Dim previous = _localModFolderRequest
        Dim owner = _localModInstance, generation = _localModGeneration
        Dim request As New CancellationTokenSource(TimeSpan.FromSeconds(100))
        ' Reserve this click before notifying cancellation. A newer action can
        ' replace/retire it while the previous request's callbacks are running.
        _localModFolderRequest = request
        Dim current As Func(Of Boolean) = Function() Not _closing AndAlso _localModFolderRequest Is request AndAlso generation = _localModGeneration AndAlso
            _localModInstance Is owner AndAlso SameLocalModContext(owner) AndAlso
            ManagementPage.Visibility = Visibility.Visible AndAlso LocalModsPage.Visibility = Visibility.Visible
        Try
            previous?.Cancel()
            If Not current() OrElse Not LocalModInputAllowed() OrElse _localModScanning OrElse _localModWriting Then Return
            request.Token.ThrowIfCancellationRequested()
            LocalModStatus.Text = "正在读取文件夹…"
            ' PCL opens the current instance's PathIndie/mods. Keep native
            ' rereads and unchanged/link validation off the WPF input thread.
            Dim refreshed = Await _managementFolderReader(owner, request.Token)
            If Not current() Then Return
            request.Token.ThrowIfCancellationRequested()
            If refreshed Is Nothing OrElse refreshed.Name <> owner.Name OrElse Not SameManagementTarget(refreshed.Target, owner.Target) Then Throw New IOException("实例信息已改变，请刷新后重试。")
            Dim folder = Await RunManagementWorkerAsync(Function() InstalledInstanceCatalog.FolderPath(refreshed, "mods", request.Token), request.Token)
            If Not current() Then Return
            request.Token.ThrowIfCancellationRequested()
            _managementOpenFolder(folder)
            If Not current() Then Return
            LocalModStatus.Text = ""
            ' Rows still describe the old directory until a full read accepts
            ' the native change. Never relabel old rows as the new folder.
            If Not SameGameRoot(refreshed.RunDirectory, owner.RunDirectory) Then Await LoadLocalModsAsync(True, refreshInstance:=True)
        Catch ex As OperationCanceledException When request.IsCancellationRequested
            If current() Then LocalModStatus.Text = "读取文件夹超时，请重试。"
        Catch ex As Exception
            If current() Then LocalModStatus.Text = "无法打开文件夹：" & ex.GetBaseException().Message
        Finally
            If _localModFolderRequest Is request Then _localModFolderRequest = Nothing
            request.Dispose()
        End Try
    End Function
End Class
