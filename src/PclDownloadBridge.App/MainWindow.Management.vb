Imports System.Diagnostics
Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private _managementMotion As PclSurfaceMotion
    Private _managementInstances As New List(Of InstalledInstance)
    Private _managedInstance As InstalledInstance
    Private _managementTarget As LauncherInstallTarget
    Private _managementRequest As CancellationTokenSource
    Private _managementGeneration As Long
    Private _managementScanning As Boolean
    Private _managementDirty As Boolean = True
    Private _managementPreparing As Boolean
    Private _managementRestoringFolders As Boolean
    Private _managementListOffset As Double
    Private _managedRepairContext As InstalledInstance
    Private _managementTaskOrigin As InstalledInstance
    Private _managementReturnName As String
    Private _managementProvider As Func(Of LauncherInstallTarget, CancellationToken, Task(Of List(Of InstalledInstance))) =
        Function(target, token) Task.Run(Function() InstalledInstanceCatalog.Scan(target, token), token)
    Private _managementOpenFolder As Action(Of String) = Sub(folder) Process.Start(New ProcessStartInfo(folder) With {.UseShellExecute = True})

    Private Sub InitializeManagement()
        _managementMotion = New PclSurfaceMotion(ManagementViewHost, ManagementGhost,
            Function() AllowPageMotion() AndAlso ManagementPage.IsVisible AndAlso _surfaceMotion.Transition Is Nothing,
            Function(surface) PclSurfaceMotion.Units(surface))
        AddHandler _managementMotion.Settled, Sub() FocusManagementViewport()
        AddHandler _managementMotion.ContentRevealed, Sub() FlushLocalModInteraction()
        AddHandler _managementMotion.Settled, Sub() FlushLocalModInteraction()
        AddHandler _surfaceMotion.ContentRevealed, Sub() FlushLocalModInteraction()
        AddHandler _surfaceMotion.Settled, Sub() FlushLocalModInteraction()
    End Sub

    Private Function ManagementContent() As FrameworkElement
        If LocalModsPage.Visibility = Visibility.Visible Then Return LocalModsPage
        Return If(ManagementDetailPage.Visibility = Visibility.Visible, DirectCast(ManagementDetailPage, FrameworkElement), ManagementListPage)
    End Function
    Private Shared Function SameManagementTarget(a As LauncherInstallTarget, b As LauncherInstallTarget) As Boolean
        Return a IsNot Nothing AndAlso b IsNot Nothing AndAlso a.Kind = b.Kind AndAlso a.BindingId = b.BindingId AndAlso
            SameGameRoot(a.Root, b.Root) AndAlso String.Equals(a.Workspace, b.Workspace, StringComparison.OrdinalIgnoreCase) AndAlso
            String.Equals(a.ConfigPath, b.ConfigPath, StringComparison.OrdinalIgnoreCase) AndAlso String.Equals(a.SharedConfigPath, b.SharedConfigPath, StringComparison.OrdinalIgnoreCase)
    End Function
    Private Sub CancelManagementRequest()
        _managementGeneration += 1
        _managementRequest?.Cancel()
        _managementRequest = Nothing
        _managementScanning = False : _managementPreparing = False
    End Sub
    Private Sub RefreshManagementFolders()
        If ManagementFolders Is Nothing Then Return
        _managementRestoringFolders = True
        Try
            ManagementFolders.ItemsSource = Nothing
            ManagementFolders.ItemsSource = _folders.ToList()
            ManagementFolders.SelectedItem = _selectedFolder
        Finally
            _managementRestoringFolders = False
        End Try
        Dim target = If(_selectedFolder Is Nothing, Nothing, CurrentInstallTarget())
        If Not SameManagementTarget(target, _managementTarget) Then
            CancelManagementRequest()
            CancelLocalModRequest()
            _managementReturnName = Nothing
            _managementDirty = True
            _managementTarget = Nothing : _managedInstance = Nothing : _managementInstances.Clear()
            ChangeManagementContent(Sub()
                                        ManagementListPage.Visibility = Visibility.Visible
                                        ManagementDetailPage.Visibility = Visibility.Collapsed
                                        LocalModsPage.Visibility = Visibility.Collapsed
                                        RenderManagementRows()
                                        ManagementStatus.Text = If(target Is Nothing, "请选择游戏目录。", "点击刷新读取已安装版本。")
                                    End Sub, ManagementListPage)
        End If
        RefreshManagementAvailability()
    End Sub
    Private Sub ManagementFolderSelected(sender As Object, e As SelectionChangedEventArgs)
        If _managementRestoringFolders Then Return
        If _busy OrElse _choosingOnline OrElse _localModWriting OrElse _closing Then RefreshManagementFolders() : Return
        Dim folder = TryCast(ManagementFolders.SelectedItem, GameFolder)
        If folder Is Nothing Then Return
        FolderList.SelectedItem = folder
        If ManagementPage.Visibility = Visibility.Visible Then RefreshManagement(Me, New RoutedEventArgs())
    End Sub
    Private Async Sub ShowManagement(sender As Object, e As RoutedEventArgs)
        If _closing OrElse _choosingOnline Then Return
        Dim target = If(_selectedFolder Is Nothing, Nothing, CurrentInstallTarget())
        RefreshManagementFolders()
        SelectSurface(ManagementPage)
        Dim scan = ScanManagementAsync(False), generation = _managementGeneration
        Await scan
        If _closing OrElse generation <> _managementGeneration OrElse _selectedFolder Is Nothing OrElse Not SameManagementTarget(target, CurrentInstallTarget()) Then Return
        If ManagementPage.Visibility = Visibility.Visible AndAlso _managementReturnName IsNot Nothing Then
            Dim name = _managementReturnName
            _managementReturnName = Nothing
            Dim instance = _managementInstances.FirstOrDefault(Function(item) item.Name = name)
            If instance IsNot Nothing Then
                ChangeManagementContent(Sub()
                                            _managedInstance = instance
                                            RenderManagedInstance()
                                            ManagementDetailPage.Visibility = Visibility.Visible : ManagementListPage.Visibility = Visibility.Collapsed
                                            LocalModsPage.Visibility = Visibility.Collapsed
                                            ManagementDetailPage.ScrollToHome()
                                            RefreshTitleNavigation()
                                        End Sub, ManagementDetailPage)
            End If
        End If
        If ManagementPage.Visibility = Visibility.Visible AndAlso LocalModsPage.Visibility = Visibility.Visible Then Await LoadLocalModsAsync(True)
        If ManagementPage.Visibility = Visibility.Visible Then FocusManagementViewport()
    End Sub
    Private Async Sub RefreshManagement(sender As Object, e As RoutedEventArgs)
        If ManagementPage.Visibility <> Visibility.Visible OrElse Not PageSurface.IsHitTestVisible OrElse _managementPreparing Then Return
        If _selectedFolder Is Nothing Then Return
        Dim target = CurrentInstallTarget(), scan = ScanManagementAsync(True), generation = _managementGeneration
        Await scan
        If _closing OrElse generation <> _managementGeneration OrElse _selectedFolder Is Nothing OrElse _managementDirty OrElse
            Not SameManagementTarget(target, CurrentInstallTarget()) OrElse _localModWriting Then Return
        If ManagementPage.Visibility = Visibility.Visible AndAlso LocalModsPage.Visibility = Visibility.Visible Then Await LoadLocalModsAsync(True)
    End Sub
    Private Async Function ScanManagementAsync(force As Boolean) As Task
        If _closing OrElse _selectedFolder Is Nothing OrElse ManagementPage.Visibility <> Visibility.Visible Then Return
        Dim target = CurrentInstallTarget()
        If Not force AndAlso (_managementScanning OrElse (Not _managementDirty AndAlso SameManagementTarget(_managementTarget, target))) Then Return
        CancelManagementRequest()
        Dim generation = _managementGeneration, request As New CancellationTokenSource()
        _managementRequest = request : _managementScanning = True
        ManagementStatus.Text = "正在读取已安装版本…"
        RefreshManagementAvailability()
        Try
            Dim instances = Await _managementProvider(target, request.Token)
            If request.IsCancellationRequested OrElse generation <> _managementGeneration OrElse _closing OrElse
                ManagementPage.Visibility <> Visibility.Visible OrElse Not SameManagementTarget(target, CurrentInstallTarget()) Then Return
            Dim selectedName = _managedInstance?.Name
            _managementTarget = target : _managementInstances = instances : _managementDirty = False
            _managedInstance = instances.FirstOrDefault(Function(item) item.Name = selectedName)
            ChangeManagementContent(Sub()
                                        RenderManagementRows()
                                        If ManagementDetailPage.Visibility = Visibility.Visible OrElse LocalModsPage.Visibility = Visibility.Visible Then
                                            If _managedInstance Is Nothing Then
                                                ManagementDetailPage.Visibility = Visibility.Collapsed : ManagementListPage.Visibility = Visibility.Visible
                                                LocalModsPage.Visibility = Visibility.Collapsed : CancelLocalModRequest()
                                            Else
                                                If ManagementDetailPage.Visibility = Visibility.Visible Then RenderManagedInstance()
                                            End If
                                        End If
                                        ManagementStatus.Text = If(instances.Count = 0, "这个目录中还没有已安装版本。", "")
                                        RefreshTitleNavigation()
                                    End Sub, If((ManagementDetailPage.Visibility = Visibility.Visible OrElse LocalModsPage.Visibility = Visibility.Visible) AndAlso _managedInstance Is Nothing, DirectCast(ManagementListPage, FrameworkElement), Nothing))
        Catch ex As OperationCanceledException
        Catch ex As Exception
            If generation = _managementGeneration AndAlso ManagementPage.Visibility = Visibility.Visible AndAlso Not _closing Then
                ManagementStatus.Text = "无法读取版本列表：" & ex.GetBaseException().Message
                _managementDirty = True
            End If
        Finally
            If generation = _managementGeneration Then
                _managementRequest = Nothing : _managementScanning = False
                RefreshManagementAvailability()
            End If
            request.Dispose()
        End Try
    End Function

    Private Sub ChangeManagementContent(update As Action, Optional incoming As FrameworkElement = Nothing)
        If _managementMotion Is Nothing OrElse ManagementPage.Visibility <> Visibility.Visible Then
            update() : Return
        End If
        If _surfaceMotion.Transition IsNot Nothing Then
            _surfaceMotion.Change(ManagementPage, ManagementPage, Sub()
                                                                    _managementMotion.Finish()
                                                                    update()
                                                                End Sub, contentRefresh:=True)
        Else
            Dim previous = ManagementContent()
            _managementMotion.Change(previous, If(incoming, previous), update, contentRefresh:=incoming Is Nothing OrElse incoming Is previous)
        End If
    End Sub
    Private Sub ManagementSearchChanged(sender As Object, e As TextChangedEventArgs)
        If ManagementRows Is Nothing OrElse ManagementPage.Visibility <> Visibility.Visible OrElse ManagementListPage.Visibility <> Visibility.Visible Then Return
        ' Filtering has no navigation or data side effects; release old row clocks
        ' before detaching rows, and retain the focused search editor.
        _surfaceMotion?.Finish() : _managementMotion?.Finish()
        RenderManagementRows()
    End Sub
    Private Sub RenderManagementRows()
        ManagementRows.Children.Clear()
        Dim query = ManagementSearch.Text.Trim()
        Dim matching = _managementInstances.Where(Function(item) query.Length = 0 OrElse
            (item.Name & " " & item.MinecraftVersion & " " & item.LoaderSummary).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList()
        For Each item In matching
            Dim captured = item
            ManagementRows.Children.Add(New PclLazyRow(Function()
                                                          Dim row As New PclListItem With {.Title = captured.Name, .Info = ManagedSubtitle(captured),
                                                              .Logo = ManagedIcon(captured), .Height = 42, .Tag = captured, .ToolTip = captured.InstancePath}
                                                          AddHandler row.Click, Sub() SelectManagedInstance(captured)
                                                          Return row
                                                      End Function))
        Next
        ManagementSummary.Text = _managementInstances.Count & " 个版本" & If(query.Length = 0, "", " · 找到 " & matching.Count & " 个")
        ManagementListCard.Visibility = If(matching.Count > 0, Visibility.Visible, Visibility.Collapsed)
        If query.Length > 0 Then
            ManagementStatus.Text = If(matching.Count = 0, "没有找到匹配的版本。", "")
        ElseIf Not _managementScanning Then
            ManagementStatus.Text = If(_managementInstances.Count = 0, "这个目录中还没有已安装版本。", "")
        End If
    End Sub
    Private Shared Function ManagedSubtitle(item As InstalledInstance) As String
        If Not String.IsNullOrEmpty(item.Problem) Then Return "无法识别 · " & item.Problem
        Return If(String.IsNullOrEmpty(item.MinecraftVersion), "游戏版本未知", item.MinecraftVersion) & " · " &
            If(String.IsNullOrEmpty(item.LoaderSummary), "未知组件", item.LoaderSummary) & " · " & item.IsolationSummary & If(item.IsModpack, " · 整合包", "")
    End Function
    Private Shared Function ManagedIcon(item As InstalledInstance) As String
        Dim name = "Grass"
        If Not String.IsNullOrEmpty(item.Problem) Then
            name = "NoIcon"
        ElseIf item.IsModpack Then
            name = "CommandBlock"
        ElseIf If(item.LoaderSummary, "").IndexOf("Fabric", StringComparison.OrdinalIgnoreCase) >= 0 Then
            name = "Fabric"
        ElseIf Not String.IsNullOrEmpty(item.LoaderSummary) AndAlso item.LoaderSummary <> "原版" AndAlso item.LoaderSummary <> "未知" Then
            name = "Anvil"
        End If
        Return "pack://application:,,,/Images/Blocks/" & name & ".png"
    End Function
    Private Sub SelectManagedInstance(instance As InstalledInstance)
        If _closing OrElse ManagementPage.Visibility <> Visibility.Visible OrElse Not PageSurface.IsHitTestVisible OrElse Not ManagementViewHost.IsHitTestVisible OrElse
            Not _managementInstances.Contains(instance) Then Return
        CancelManagementRequest()
        CancelLocalModRequest()
        _managementReturnName = Nothing
        _managementListOffset = ManagementListPage.VerticalOffset
        Dim outgoing = ManagementContent()
        ChangeManagementContent(Sub()
                                                                  _managedInstance = instance
                                                                  RenderManagedInstance()
                                                                  ManagementListPage.Visibility = Visibility.Collapsed
                                                                  ManagementDetailPage.Visibility = Visibility.Visible
                                                                  LocalModsPage.Visibility = Visibility.Collapsed
                                                                  ManagementDetailPage.ScrollToHome()
                                                                  RefreshTitleNavigation()
                                End Sub, ManagementDetailPage)
        FocusManagementViewport()
    End Sub
    Private Sub RenderManagedInstance()
        Dim item = _managedInstance
        If item Is Nothing Then Return
        ManagedIntro.Title = item.Name : ManagedIntro.Info = ManagedSubtitle(item) : ManagedIntro.Logo = ManagedIcon(item)
        ManagedVersion.Text = "Minecraft " & If(item.MinecraftVersion, "未知") & If(String.IsNullOrEmpty(item.LoaderSummary), "", " · " & item.LoaderSummary)
        ManagedIsolation.Text = item.IsolationSummary
        ManagedInstancePath.Text = item.InstancePath
        ManagedRunPath.Text = If(item.RunDirectory, "无法确定")
        Dim issues = New List(Of String)(item.Warnings)
        If Not String.IsNullOrEmpty(item.Problem) Then issues.Insert(0, item.Problem)
        ManagedProblem.Text = String.Join("；", issues)
        ManagedProblem.Visibility = If(issues.Count = 0, Visibility.Collapsed, Visibility.Visible)
        ManagedRepairStatus.Text = If(item.IsVanillaRepairCandidate, "仅补全原版文件，保留已有设置。", "此实例暂不支持原版校验补全。")
        RefreshManagementAvailability()
    End Sub
    Private Sub BackFromManagement(sender As Object, e As RoutedEventArgs)
        If ManagementPage.Visibility <> Visibility.Visible OrElse ManagementDetailPage.Visibility <> Visibility.Visible Then Return
        CancelManagementRequest()
        _managementReturnName = Nothing
        _managementMotion.Change(ManagementDetailPage, ManagementListPage, Sub()
                                                                            ManagementDetailPage.Visibility = Visibility.Collapsed
                                                                            ManagementListPage.Visibility = Visibility.Visible
                                                                            ManagementListPage.ScrollToVerticalOffset(_managementListOffset)
                                                                            RefreshTitleNavigation()
                                                                        End Sub)
        FocusManagementViewport()
    End Sub
    Private Sub FocusManagementViewport()
        If _closing OrElse ManagementPage.Visibility <> Visibility.Visible Then Return
        Dim target = ManagementContent()
        Dim focus As Action = Sub()
                                  If _closing OrElse ManagementPage.Visibility <> Visibility.Visible OrElse ManagementContent() IsNot target Then Return
                                  If ManagementPage.IsKeyboardFocusWithin Then Return
                                  FocusManager.SetFocusedElement(FocusManager.GetFocusScope(target), target)
                                  target.Focus()
                              End Sub
        If _surfaceMotion.Transition IsNot Nothing Then
            _pendingSurfaceFocus = focus
        ElseIf _managementMotion.Transition Is Nothing Then
            focus()
        End If
    End Sub
    Private Sub RefreshManagementAvailability()
        If ManagementFolders Is Nothing Then Return
        ManagementFolders.IsEnabled = Not _busy AndAlso Not _choosingOnline AndAlso Not _managementPreparing AndAlso Not _localModWriting
        ManagementChooseFolder.IsEnabled = ManagementFolders.IsEnabled
        ManagementRefresh.IsEnabled = Not _managementPreparing
        ManagedRepair.IsEnabled = Not _busy AndAlso Not _choosingOnline AndAlso Not _localModWriting AndAlso Not _managementScanning AndAlso Not _managementPreparing AndAlso _managedInstance IsNot Nothing AndAlso _managedInstance.IsVanillaRepairCandidate
        ManagedMods.IsEnabled = _managedInstance IsNot Nothing AndAlso _managedInstance.Problem Is Nothing AndAlso _managedInstance.RunDirectory IsNot Nothing
        For Each button In ManagedFolderActions.Children.OfType(Of PclButton)()
            Dim folder = ManagedFolderLocation(CStr(button.Tag))
            button.IsEnabled = folder IsNot Nothing AndAlso Directory.Exists(folder)
        Next
    End Sub
    Private Function ManagedFolderLocation(kind As String) As String
        If _managedInstance Is Nothing OrElse String.IsNullOrEmpty(_managedInstance.Problem) = False Then Return Nothing
        If kind = "instance" Then Return _managedInstance.InstancePath
        If String.IsNullOrEmpty(_managedInstance.RunDirectory) Then Return Nothing
        If Not {"mods", "saves", "resourcepacks", "screenshots"}.Contains(kind) Then Return Nothing
        Return Path.Combine(_managedInstance.RunDirectory, kind)
    End Function
    Private Sub OpenManagedFolder(sender As Object, e As RoutedEventArgs)
        If ManagementPage.Visibility <> Visibility.Visible OrElse ManagementDetailPage.Visibility <> Visibility.Visible OrElse
            Not PageSurface.IsHitTestVisible OrElse Not ManagementViewHost.IsHitTestVisible Then Return
        Try
            Dim kind = CStr(DirectCast(sender, FrameworkElement).Tag)
            If Not {"instance", "mods", "saves", "resourcepacks", "screenshots"}.Contains(kind) Then Return
            Dim folder = InstalledInstanceCatalog.FolderPath(_managedInstance, If(kind = "instance", "", kind))
            _managementOpenFolder(folder)
        Catch ex As Exception
            ManagedRepairStatus.Text = "无法打开文件夹：" & ex.GetBaseException().Message
        End Try
    End Sub
    Private Async Sub RepairManagedInstance(sender As Object, e As RoutedEventArgs)
        Await PrepareManagedRepairAsync()
    End Sub
    Private Async Function PrepareManagedRepairAsync() As Task
        If _closing OrElse _busy OrElse _choosingOnline OrElse _localModWriting OrElse _managementPreparing OrElse _managementScanning OrElse _managedInstance Is Nothing OrElse
            Not _managedInstance.IsVanillaRepairCandidate OrElse ManagementPage.Visibility <> Visibility.Visible OrElse ManagementDetailPage.Visibility <> Visibility.Visible OrElse
            Not PageSurface.IsHitTestVisible OrElse Not ManagementViewHost.IsHitTestVisible Then Return
        CancelManagementRequest()
        Dim instance = _managedInstance, generation = _managementGeneration, request As New CancellationTokenSource(TimeSpan.FromSeconds(100))
        _managementRequest = request : _managementPreparing = True
        RefreshManagementAvailability()
        ManagedRepairStatus.Text = "正在核对原版下载信息…"
        Try
            InstalledInstanceCatalog.EnsureUnchanged(instance, request.Token)
            Dim entry = _versions.FirstOrDefault(Function(version) version.Id = instance.MinecraftVersion)
            If entry Is Nothing Then
                Dim policySnapshot = Policy()
                Dim entries = If(_versionProvider Is Nothing,
                    Await Task.Run(Function() New MinecraftInstaller(policySnapshot).VersionsAsync(request.Token)),
                    Await _versionProvider(policySnapshot, request.Token))
                entry = entries.FirstOrDefault(Function(version) version.Id = instance.MinecraftVersion)
            End If
            If request.IsCancellationRequested OrElse generation <> _managementGeneration OrElse _closing OrElse _busy OrElse
                ManagementPage.Visibility <> Visibility.Visible OrElse _managedInstance IsNot instance OrElse Not SameManagementTarget(instance.Target, CurrentInstallTarget()) Then Return
            If entry Is Nothing Then Throw New NotSupportedException("当前下载清单没有这个原版，暂时无法补全。")
            InstalledInstanceCatalog.EnsureUnchanged(instance, request.Token)
            _managementRequest = Nothing : _managementPreparing = False
            ' SelectVersion uses the existing installer UI, including validation,
            ' component gating and transitions. Preserve the installed name.
            SelectVersion(entry.Copy())
            SetAutomaticInstanceName(instance.Name)
            _nameEdited = True
            RepairBox.IsChecked = True : RepairCard.Collapsed = False
            _managedRepairContext = instance
            RefreshInstallAvailability()
            FocusDownloadPage()
            StatusText.Text = "已选择原版校验补全：" & instance.Name
        Catch ex As OperationCanceledException
        Catch ex As Exception
            If generation = _managementGeneration AndAlso ManagementPage.Visibility = Visibility.Visible AndAlso Not _closing Then ManagedRepairStatus.Text = ex.GetBaseException().Message
        Finally
            If generation = _managementGeneration Then _managementRequest = Nothing : _managementPreparing = False
            request.Dispose()
            RefreshManagementAvailability()
        End Try
    End Function
    Private Function ManagedRepairProblem() As String
        If _managedRepairContext Is Nothing OrElse Not RepairBox.IsChecked.GetValueOrDefault() Then Return Nothing
        If _selectedFolder Is Nothing OrElse Not SameManagementTarget(_managedRepairContext.Target, CurrentInstallTarget()) OrElse
            _managedRepairContext.Name <> InstanceName.Text OrElse _managedRepairContext.MinecraftVersion <> _selectedEntry?.Id Then
            Return "实例或游戏目录已变化，请从管理页重新选择校验补全。"
        End If
        Return Nothing
    End Function
End Class
