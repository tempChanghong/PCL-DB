Imports System.ComponentModel
Imports System.Diagnostics
Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Data
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private ReadOnly _preview As Boolean
    Private _folders As New List(Of GameFolder)
    Private _versions As New List(Of VersionEntry)
    Private _selectedFolder As GameFolder
    Private _cancellation As CancellationTokenSource
    Private _loading As Boolean
    Private _settingsLoadFailed As Boolean
    Private _closing As Boolean
    Private _versionGeneration As Integer
    Private _versionCancellation As CancellationTokenSource
    Private _versionsDirty As Boolean
    Private _versionProvider As Func(Of SourcePolicy, CancellationToken, Task(Of List(Of VersionEntry)))
    Private _busy As Boolean
    Private _selectedEntry As VersionEntry
    Private Enum DownloadView
        Versions
        Install
        Progress
        Modpacks
    End Enum
    Private _downloadView As DownloadView = DownloadView.Versions
    Private _vanillaView As DownloadView = DownloadView.Versions
    Private _progressReturnView As DownloadView = DownloadView.Versions
    Private ReadOnly _pageSwitch As PclPageSwitch
    Private ReadOnly _categoryMotion As PclSurfaceMotion
    Private _focusAfterPageSwitch As Boolean
    Private _hasTask As Boolean
    Private _pclBase As String = AppDomain.CurrentDomain.BaseDirectory
    Private ReadOnly _settingsDirectory As String = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PclDownloadBridge")
    Private ReadOnly Property SettingsFile As String
        Get
            Return Path.Combine(_settingsDirectory, "settings.json")
        End Get
    End Property
    Private ReadOnly Property LegacySettingsFile As String
        Get
            Dim oldest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DownloadSettingsFile.LegacyDirectoryName, "settings.json")
            If _preview Then Return oldest
            Dim previous = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DownloadSettingsFile.PreviousDirectoryName, "settings.json")
            Return DownloadSettingsFile.ChooseLegacyPath(previous, oldest)
        End Get
    End Property

    Public Sub New(Optional preview As Boolean = False)
        InitializeComponent()
        _pageSwitch = New PclPageSwitch(DownloadViewHost, VersionPage, InstallPage,
            Function() Not _closing AndAlso (preview OrElse SystemParameters.ClientAreaAnimation))
        AddHandler _pageSwitch.Settled, Sub()
                                           If Not _focusAfterPageSwitch Then Return
                                           _focusAfterPageSwitch = False
                                           If DownloadPage.IsVisible AndAlso Not _closing Then FocusDownloadPage()
                                       End Sub
        PclTheme.Apply(TitleBar, Shell)
        AddHandler Shell.SizeChanged, Sub() Shell.Clip = New RectangleGeometry(New Rect(0, 0, Shell.ActualWidth, Shell.ActualHeight), 6, 6)
        DownloadTab.Checked = True
        _preview = preview
        _surfaceMotion = New PclSurfaceMotion(PageSurface, PageGhost,
            AddressOf AllowPageMotion, AddressOf SurfaceUnits)
        _categoryMotion = New PclSurfaceMotion(DownloadViewHost, DownloadGhost,
            Function() AllowPageMotion() AndAlso DownloadPage.IsVisible,
            Function(surface) PclSurfaceMotion.Units(surface))
        AddHandler _categoryMotion.Settled, Sub()
                                              If Not _focusAfterPageSwitch Then Return
                                              _focusAfterPageSwitch = False
                                              If DownloadPage.IsVisible AndAlso Not _closing Then FocusDownloadPage()
                                          End Sub
        AddHandler PackInstanceName.TextChanged, Sub() RefreshPackAvailability()
        AddHandler PackInstanceName.KeyDown, AddressOf PackNameKeyDown
        AddHandler ModrinthInstanceName.TextChanged, Sub() RefreshModrinthAvailability()
        AddHandler ModrinthInstanceName.KeyDown, AddressOf ModrinthNameKeyDown
        InitializeMotion()
        InitializePackSearch()
        InitializeAppearance()
        InitializeDeveloperSettings()
        InitializeAbout()
        InitializeSettingsFeedback()
        AddHandler Shell.KeyDown, AddressOf ShellKeyDown
        AddHandler Shell.PreviewKeyDown, AddressOf ShellPreviewKeyDown
        AddHandler Shell.PreviewTextInput, Sub(sender, args)
                                             If GatedKeyboardSource(args.OriginalSource) Then args.Handled = True
                                         End Sub
        AddHandler Shell.PreviewGotKeyboardFocus, Sub(sender, args)
                                                    If GatedKeyboardSource(args.NewFocus) Then args.Handled = True
                                                End Sub
        AddHandler VersionSourceBox.SelectionChanged, AddressOf VersionSourceChanged
        AddHandler InstanceName.TextChanged, AddressOf InstanceNameChanged
        AddHandler InstanceName.KeyDown, AddressOf InstanceNameKeyDown
        AddHandler RepairBox.Checked, Sub() RefreshComponents()
        AddHandler RepairBox.Unchecked, Sub() RefreshComponents()
        AddHandler RepairBox.Checked, AddressOf IsolationChanged
        AddHandler RepairBox.Unchecked, AddressOf IsolationChanged
        AddHandler IsolationBox.SelectionChanged, AddressOf IsolationChanged
        RefreshIsolation(False)
        If preview Then
            SelectedPath.Text = "D:\Games\Minecraft\.minecraft"
            FolderSummary.Text = "PCL 当前选择 · 6 个已安装实例 · 共享支持库与资源文件"
            _versions = New List(Of VersionEntry) From {
                New VersionEntry With {.Id = "1.21.1", .Kind = "release", .Released = New DateTimeOffset(2024, 8, 8, 0, 0, 0, TimeSpan.Zero)},
                New VersionEntry With {.Id = "1.20.6", .Kind = "release", .Released = New DateTimeOffset(2024, 4, 29, 0, 0, 0, TimeSpan.Zero)},
                New VersionEntry With {.Id = "1.20.1", .Kind = "release", .Released = New DateTimeOffset(2023, 6, 12, 0, 0, 0, TimeSpan.Zero)},
                New VersionEntry With {.Id = "1.19.4", .Kind = "release", .Released = New DateTimeOffset(2023, 3, 14, 0, 0, 0, TimeSpan.Zero)},
                New VersionEntry With {.Id = "24w33a", .Kind = "snapshot", .Released = New DateTimeOffset(2024, 8, 14, 12, 0, 0, TimeSpan.Zero)},
                New VersionEntry With {.Id = "b1.7.3", .Kind = "old_beta", .Released = New DateTimeOffset(2011, 7, 8, 0, 0, 0, TimeSpan.Zero)},
                New VersionEntry With {.Id = "24w14potato", .Kind = "snapshot", .Released = New DateTimeOffset(2024, 4, 1, 12, 0, 0, TimeSpan.Zero)}}
            UpdateVersions()
            StatusText.Text = "界面预览 · 示例目录与版本数据"
        End If
    End Sub

    Public Sub PreviewPage(page As String)
        If Not _preview Then Return
        Select Case page
            Case "modpacks"
                SetDownloadView(DownloadView.Modpacks)
            Case "install"
                SelectVersion(_versions(0))
            Case "components"
                SelectVersion(_versions(0))
                SelectComponent(_componentEntries(ComponentKind.Fabric)(0))
                _componentViews(ComponentKind.Fabric).Card.Collapsed = False
            Case "components-long"
                PreviewLongComponentSummary()
            Case "appearance-dark"
                RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", True}})
                PreviewPage("settings")
            Case "developer"
                PreviewPage("settings")
                DeveloperEnabledBox.IsChecked = True
            Case "appearance", "appearance-colorful", "appearance-custom", "appearance-flat"
                If page = "appearance-colorful" OrElse page = "appearance-custom" Then OpenExperimentalPreview()
                Dim theme = If(page = "appearance-colorful", "colorful", If(page = "appearance-custom", "custom", "blue"))
                RestoreAppearance(New JObject From {{"theme", theme}, {"hue", 285}, {"saturation", 65}, {"colorfulBackground", page <> "appearance-flat"}})
                PreviewPage("settings")
            Case "about", "about-colorful", "about-dark"
                PreviewPage("settings")
                If page = "about-colorful" Then
                    OpenExperimentalPreview()
                    RestoreAppearance(New JObject From {{"theme", "colorful"}})
                ElseIf page = "about-dark" Then
                    RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", True}})
                End If
                ShowAbout(Me, New RoutedEventArgs())
            Case "appearance-download"
                PreviewPage("components")
                RestoreAppearance(New JObject From {{"theme", "colorful"}})
            Case "versions-special", "version-infinite"
                PreviewSpecialVersions(page = "version-infinite")
            Case "versions-colours", "versions-colours-dark", "version-colour-red"
                If page = "versions-colours-dark" Then RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", True}})
                _versions = New List(Of VersionEntry)
                For Each colour In {"blue", "red", "purple"}
                    _versions.Add(New VersionEntry With {.Id = "2.0_" & colour, .SupplementalId = "2point0_" & colour, .Kind = "snapshot", .Released = DateTimeOffset.Parse("2013-04-01T12:00:00Z")})
                Next
                UpdateVersions() : BrowserRows("特供版本")
                If page = "version-colour-red" Then SelectVersion(_versions.Single(Function(entry) entry.Id = "2.0_red"))
            Case "name-invalid"
                SelectVersion(_versions(0))
                InstanceName.Text = "bad/name"
            Case "name-duplicate"
                Dim previewRoot = Path.Combine(Path.GetTempPath(), "PclDownloadBridge-NamePreview-" & Guid.NewGuid().ToString("N"), ".minecraft")
                Directory.CreateDirectory(Path.Combine(previewRoot, "versions", _versions(0).Id))
                SelectManualFolder(previewRoot)
                SelectVersion(_versions(0))
            Case "interaction-pressed"
                PreviewPage("expanded")
                PointerEvent(BrowserRows("正式版").First(), Input.Mouse.MouseDownEvent)
                PointerEvent(SettingsTab, Input.Mouse.MouseDownEvent)
            Case "components-refreshing"
                PreviewPage("components")
                _componentPending.Add(ComponentKind.Fabric)
                _componentEntries.Remove(ComponentKind.Fabric)
                RefreshComponents()
            Case "components-unavailable"
                PreviewPage("components")
                _unresolvedComponents.Add(ComponentKind.Fabric)
                _componentEntries(ComponentKind.Fabric).Clear()
                RefreshComponents()
            Case "components-incompatible"
                PreviewForgeCompatibility()
                AcceptComponentList(ComponentKind.OptiFine, _selectedEntry, _componentGeneration, New List(Of ComponentEntry) From {ChangedOptiFineRequirement()})
            Case "components-bridge-required"
                PreviewBridgeRequirement()
            Case "expanded"
                For Each card In VersionCards.Children.OfType(Of PclCard)()
                    If card.Title.StartsWith("正式版", StringComparison.Ordinal) Then card.Collapsed = False
                Next
            Case "tasks"
                ResetTaskView("1.21.1-NeoForge 21.1.77", True)
                SetBusy(True)
                ShowProgress(Nothing, Nothing)
                RenderTaskProgress(New TransferProgress With {.Phase = "metadata", .Stage = "读取版本信息与资源索引"})
                RenderTaskProgress(New TransferProgress With {.Phase = "files", .Stage = "下载原版游戏与资源文件", .StageFraction = 0.86, .Total = 3962, .Completed = 3407, .BytesPerSecond = 46.2 * 1024 * 1024, .NetworkBytes = CLng(512.8 * 1024 * 1024), .Reused = 2106, .ActiveConnections = 48, .DynamicSplits = 17, .Retries = 2, .CurrentFile = "assets/objects/2c/2c2b...", .Source = "resources.download.minecraft.net"})
            Case "settings"
                SelectManualFolder("D:\Games\Minecraft\.minecraft")
                PclLocation.Text = "PCL 所在位置：D:\Games\Minecraft"
                SwitchMainPage(True)
            Case "settings-invalid"
                PreviewPage("settings")
                SpeedLimitBox.Text = "不是数字"
                SaveSettings(Nothing, Nothing)
            Case "settings-disabled"
                PreviewPage("settings")
                SetBusy(True)
            Case "task-detail"
                PreviewPage("tasks")
                TransferCard.Collapsed = False
            Case "task-parallel"
                PreviewPage("tasks")
                Dim states As New Dictionary(Of String, Double) From {{"metadata", 1}, {"files", 0.62}, {"components", 1}, {"processors", 0.4}, {"commit", 0}}
                RenderTaskProgress(New TransferProgress With {.Phase = "processors", .Stage = "运行 NeoForge 安装处理器", .StageFraction = 0.4, .PhaseFractions = states, .PhaseTotal = 0,
                    .Total = 3962, .Completed = 2456, .ActiveConnections = 12, .BytesPerSecond = 8.6 * 1024 * 1024, .NetworkBytes = 128 * 1024 * 1024,
                    .Detail = "界面预览 · 资源下载与组件处理器并行推进", .CurrentFile = "assets/objects/2c/2c2b...", .Source = "resources.download.minecraft.net"})
            Case "task-failed"
                PreviewPage("tasks")
                FinishTaskView(False, "界面预览 · 示例下载失败，可重试")
                SetBusy(False)
                TaskTitle.Text = "安装未完成 · 示例任务"
            Case "task-cancelling"
                PreviewPage("tasks")
                _busy = True
                CancelInstall(Nothing, Nothing)
            Case "folder-selection"
                SelectVersion(_versions(0))
                SelectComponent(_componentEntries(ComponentKind.Fabric)(0))
                ChooseInstallFolder(Nothing, Nothing)
                SelectManualFolder("D:\Games\Minecraft\.minecraft")
                PclLocation.Text = "PCL 所在位置：D:\Games\Minecraft"
        End Select
    End Sub

    Public Function VerifyUi(Optional artifactDirectory As String = Nothing, Optional checks As List(Of String) = Nothing) As List(Of String)
        If Not _preview Then Throw New InvalidOperationException("UI checks require isolated preview mode.")
        OpenExperimentalPreview()
        checks = If(checks, New List(Of String))
        Dim check As Action(Of Boolean, String) = Sub(condition, label)
                                                     If Not condition Then Throw New InvalidOperationException(label)
                                                     checks.Add(label)
                                                 End Sub
        If artifactDirectory IsNot Nothing Then UiCheckRunnerChecks.Verify(artifactDirectory, check)
        check(Shell.RowDefinitions(0).Height.Value = 48, "PCL title bar is 48 DIP")
        check(TopTabs.Children.Count = 2 AndAlso DownloadTab.Text = "下载" AndAlso SettingsTab.Text = "设置", "only Download and Settings remain in main navigation")
        check(FindName("FoldersPage") Is Nothing AndAlso FindName("TasksPage") Is Nothing AndAlso DownloadNav.Children.Count = 2, "download navigation contains only vanilla and modpacks without standalone folder or task pages")
        check(Not _hasTask AndAlso ProgressShortcut.Visibility = Visibility.Collapsed, "fresh download page has no empty task entry")
        check(VersionCards.Children.Count = 5, "latest plus four PCL categories")
        Dim releaseCard = DirectCast(VersionCards.Children(1), PclCard)
        check(releaseCard.Collapsed AndAlso releaseCard.Height = 40, "PCL category starts collapsed at 40 DIP")
        releaseCard.Collapsed = False
        Dim rows = BrowserRows("正式版")
        check(rows.Count = 4 AndAlso rows.All(Function(row) row.Height = 42), "PCL version rows are 42 DIP and lazily built")
        rows(0).Activate()
        check(InstallPage.Visibility = Visibility.Visible AndAlso VersionPage.Visibility = Visibility.Collapsed, "version row click opens install options inside Download")
        check(InstanceName.Text = "1.21.1" AndAlso _selectedEntry.Id = "1.21.1", "selected version and instance name are carried into installer")
        check(TopTabs.Visibility = Visibility.Visible AndAlso DownloadTab.Checked, "two main tabs remain available during component selection")
        ShowSettings(Nothing, Nothing)
        check(SettingsPage.Visibility = Visibility.Visible AndAlso SettingsTab.Checked AndAlso GameFolderCard.Parent IsNot Nothing, "Settings contains the game folder selection")
        ShowDownload(Nothing, Nothing)
        check(InstallPage.Visibility = Visibility.Visible AndAlso DownloadTab.Checked, "switching back from Settings preserves install options")
        check(New PclButton().PanFore.CornerRadius.TopLeft = 3, "original PCL button uses 3 DIP corners")
        check(ComponentCards.Children.Count = 7 AndAlso _componentViews.Values.All(Function(v) v.Card.Height = 40 AndAlso v.Card.HeaderContent IsNot Nothing), "all seven PCL component cards use 40 DIP summary headers")
        SelectComponent(_componentEntries(ComponentKind.Fabric)(0))
        check(_componentSelection.Has(ComponentKind.FabricApi) AndAlso Not _componentViews(ComponentKind.Forge).Card.Collapsible, "Fabric selection auto selects API and disables incompatible Forge")
        InstanceName.Text = "我的自定义名称"
        ClearComponent(ComponentKind.Fabric)
        check(Not _componentSelection.Has(ComponentKind.FabricApi) AndAlso InstanceName.Text = "我的自定义名称", "clearing Fabric clears dependencies and preserves custom instance name")
        ResetTaskView("测试任务", True)
        ShowProgress(Nothing, Nothing)
        check(DownloadProgress.Visibility = Visibility.Visible AndAlso DownloadProgress.Parent Is DownloadViewHost AndAlso DownloadViewHost.Parent Is DownloadRight AndAlso DownloadRight.Parent Is DownloadPage AndAlso DownloadTab.Checked AndAlso SettingsPage.Visibility = Visibility.Collapsed, "download progress is nested in the download right pane with its tab selected")
        RenderTaskProgress(New TransferProgress With {.Phase = "files", .StageFraction = 0.8, .Total = 100, .Completed = 80, .BytesPerSecond = 1024})
        Dim before = TaskProgress.Value
        RenderTaskProgress(New TransferProgress With {.Phase = "files", .StageFraction = 0.1, .Total = 10, .Completed = 1})
        check(TaskPhases.Children.Count = 5 AndAlso TaskProgress.Value >= before AndAlso TaskRemaining.Text = "9", "PCL task stages show real remaining files and monotonic weighted progress")
        ShowSettings(Nothing, Nothing)
        ShowDownload(Nothing, Nothing)
        check(DownloadProgress.Visibility = Visibility.Visible AndAlso TaskProgress.Value >= before, "settings round trip preserves the task view and progress")
        ResumeInstallation(Nothing, Nothing)
        check(InstallPage.Visibility = Visibility.Visible AndAlso ProgressShortcut.Visibility = Visibility.Visible, "returning to install options keeps an accessible progress shortcut")
        VerifyComponentArrivals(check)
        SelectVersion(_versions(0))
        SelectComponent(_componentEntries(ComponentKind.Fabric)(0))
        InstanceName.Text = "自定义实例名称"
        Dim selectedBeforeFolder = _selectedEntry
        ChooseInstallFolder(Nothing, Nothing)
        check(SettingsPage.Visibility = Visibility.Visible AndAlso SettingsTab.Checked, "change directory goes directly to Settings")
        SelectManualFolder(Path.Combine(Path.GetTempPath(), "PclDownloadBridge-UIOnly-Empty"))
        ShowDownload(Nothing, Nothing)
        check(InstallPage.Visibility = Visibility.Visible AndAlso DownloadPage.Visibility = Visibility.Visible AndAlso VersionPage.Visibility = Visibility.Collapsed AndAlso _selectedEntry Is selectedBeforeFolder AndAlso _componentSelection.Has(ComponentKind.Fabric) AndAlso InstanceName.Text = "自定义实例名称", "changing game folder returns to install page with version components and custom name intact")
        FolderList.SelectedItem = Nothing
        check(_selectedFolder Is Nothing AndAlso SelectedPath.Text = "请选择 PCL 的 .minecraft 文件夹", "clearing folder selection cannot retain a stale install destination")
        _busy = True
        SetBusy(True)
        ShowSettings(Nothing, Nothing)
        check(Not GameFolderCard.IsEnabled AndAlso Not DownloadSettingsCard.IsEnabled AndAlso SettingsTab.Checked, "active download allows reading settings while freezing the destination and transfer options")
        ShowDownload(Nothing, Nothing)
        check(DownloadProgress.Visibility = Visibility.Visible AndAlso CancelButton.IsEnabled, "returning during an active download restores progress and cancellation")
        _busy = False
        SetBusy(False)
        check(GameFolderCard.IsEnabled AndAlso DownloadSettingsCard.IsEnabled AndAlso Not CancelButton.IsEnabled, "finishing a download unlocks the two settings cards")
        BackToVersions(Nothing, Nothing)
        check(VersionScroll.Visibility = Visibility.Visible AndAlso InstallPage.Visibility = Visibility.Collapsed AndAlso DownloadProgress.Visibility = Visibility.Collapsed AndAlso ProgressShortcut.Visibility = Visibility.Visible, "back returns to the version browser without losing access to the task result")
        ResetTaskView("测试任务", True)
        RenderTaskProgress(New TransferProgress With {.Phase = "files", .Total = 10, .Completed = 3, .StageFraction = 0.3, .DynamicSplits = 17, .Retries = 2})
        RenderTaskProgress(New TransferProgress With {.Phase = "processors", .Stage = "处理组件", .StageFraction = 0.5})
        check(TransferStats.Text.Contains("累计分段 17 次") AndAlso TransferStats.Text.Contains("失败尝试 2 次"), "transfer counters remain cumulative during local processor stages")
        VerifySelectionControls(check, artifactDirectory)
        VerifyPageAlignment(check)
        VerifyPclDetails(check)
        VerifySourcePreferences(check, artifactDirectory)
        VerifyIsolationOptions(check)
        VerifyRefreshRebinding(check)
        VerifyRefreshWorkflow(check, artifactDirectory)
        VerifyCompatibilityWorkflow(check)
        VerifyTaskLifecycle(check, artifactDirectory)
        VerifyPointerAlignment(check)
        VerifyComponentBodyRetention(check)
        VerifyDefaultNameAlignment(check)
        VerifyNameChecks(check)
        VerifyVersionBrowser(check)
        VerifyLongVersionLists(check, artifactDirectory)
        VerifyLongComponentLists(check, artifactDirectory)
        VerifyInstallViewport(check, artifactDirectory)
        VerifyCardMotion(check, artifactDirectory)
        VerifyComponentSummary(check, artifactDirectory)
        VerifyVersionFormatUi(check, artifactDirectory)
        VerifyLoaderBoundaryUi(check, artifactDirectory)
        VerifyForgeRefreshUi(check, artifactDirectory)
        VerifyDeveloper(check, artifactDirectory)
        VerifyAppearance(check, artifactDirectory)
        VerifyAbout(check, artifactDirectory)
        VerifySettingsWorkflow(check, artifactDirectory)
        VerifyTaskResults(check, artifactDirectory)
        VerifyKeyboardWorkflow(check, artifactDirectory)
        VerifyNavigationUi(artifactDirectory, checks)
        VerifyCompatibilityUi(artifactDirectory, checks)
        VerifyCurseForge(check, artifactDirectory)
        VerifyModrinth(check, artifactDirectory)
        Return checks
    End Function

    Private Async Sub WindowLoaded(sender As Object, e As RoutedEventArgs)
        If _preview Then Return
        Dim migrationNotice As String = Nothing
        _settingsLoadFailed = False
        _restoringSettings = True
        Try
            Dim settings = DownloadSettingsFile.LoadCompatible(SettingsFile, LegacySettingsFile, Sub(message) migrationNotice = message)
            If settings IsNot Nothing Then
                RestoreDeveloperSettings(settings("developer"))
                RestoreAppearance(settings("appearance"))
                _pclBase = If(CStr(settings("pclBase")), _pclBase)
                RestoreSourcePreferences(settings)
                ConnectionsSlider.Value = ConnectionLevels(Math.Max(0, Math.Min(3, CInt(If(settings("connectionsIndex"), New JValue(1))))))
                Dim storedSpeed = If(CStr(settings("speed")), "0")
                Dim restoredSpeed As Double
                SpeedLimitBox.Text = If(Double.TryParse(storedSpeed, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, restoredSpeed), restoredSpeed.ToString(Globalization.CultureInfo.CurrentCulture), storedSpeed)
                ReuseBox.IsChecked = CBool(If(settings("reuse"), New JValue(True)))
                JavaPathBox.Text = If(CStr(settings("java")), "")
                DiscoverFolders(_pclBase)
                Dim lastRoot = CStr(settings("gameRoot"))
                If Not String.IsNullOrWhiteSpace(lastRoot) AndAlso Directory.Exists(lastRoot) Then SelectManualFolder(lastRoot)
            Else
                DiscoverFolders(_pclBase)
            End If
            CapturePreferenceBaseline()
        Catch ex As Exception
            _settingsLoadFailed = True
            migrationNotice = "读取偏好失败，已暂停自动保存；检查后可点击保存设置：" & ex.Message
        Finally
            _restoringSettings = False
        End Try
        Await LoadVersionsAsync()
        If migrationNotice IsNot Nothing Then
            WriteLog(migrationNotice)
            StatusText.Text &= "；" & migrationNotice
        End If
    End Sub

    Private ReadOnly _sourceSelection As New SourceSelectionState()
    Private _supplementalProvider As Func(Of SourcePolicy, CancellationToken, Task(Of List(Of VersionEntry)))
    Private Function Policy() As SourcePolicy
        Return New SourcePolicy(_sourceSelection) With {.Preference = SelectedSource(SourceBox), .VersionPreference = SelectedSource(VersionSourceBox)}
    End Function

    Private Async Function LoadVersionsAsync(Optional restart As Boolean = False) As Task
        If _closing OrElse _busy OrElse (_loading AndAlso Not restart) Then Return
        _versionGeneration += 1
        Dim generation = _versionGeneration
        _versionCancellation?.Cancel()
        Dim request As New CancellationTokenSource(TimeSpan.FromSeconds(100))
        _versionCancellation = request
        _loading = True
        StatusText.Text = "正在读取 Minecraft 版本列表…"
        Try
            Dim versions As List(Of VersionEntry)
            If _versionProvider IsNot Nothing Then
                versions = Await _versionProvider(Policy(), request.Token)
            ElseIf _preview Then
                versions = _versions.ToList()
            Else
                versions = Await New MinecraftInstaller(Policy()).VersionsAsync(request.Token)
            End If
            If _closing OrElse generation <> _versionGeneration Then Return
            request.Token.ThrowIfCancellationRequested()
            If _busy Then
                _versionsDirty = True
                Return
            End If
            _versions = versions
            _versionsDirty = False
            UpdateVersions()
            StatusText.Text = "已读取 " & _versions.Count & " 个版本，选择版本后可开始安装。"
            ' Keep the ordinary list interactive while optional UVMC metadata loads.
            _loading = False
            If _supplementalProvider IsNot Nothing OrElse (_versionProvider Is Nothing AndAlso Not _preview) Then
                Dim status = StatusText.Text
                Try
                    Dim extra = Await If(_supplementalProvider Is Nothing, SupplementalVersions.LoadAsync(Policy(), request.Token), _supplementalProvider(Policy(), request.Token))
                    If _closing OrElse generation <> _versionGeneration OrElse _busy Then Return
                    _versions = SupplementalVersions.Merge(_versions, extra)
                    ' Append separate PCL-style folded groups without rebuilding rows the
                    ' user has already opened, focused or scrolled through.
                    AddSupplementalVersionCards()
                    If _downloadView = DownloadView.Versions AndAlso StatusText.Text = status Then StatusText.Text = SupplementalCatalogueStatus(extra.Count > 0)
                Catch ex As OperationCanceledException
                    ' The main list has already succeeded. Its deadline or a cancelled
                    ' optional request must not turn that success into a refresh error.
                    Return
                Catch ex As Exception
                    ' Optional catalogue work cannot change the successful main list's
                    ' freshness or replace an active task/newer request's status.
                    If _closing OrElse generation <> _versionGeneration OrElse _busy Then Return
                    If _downloadView = DownloadView.Versions AndAlso StatusText.Text = status Then StatusText.Text = SupplementalCatalogueStatus(False)
                    If Not _preview Then Log(ex)
                End Try
            End If
        Catch ex As Exception
            If _closing OrElse generation <> _versionGeneration Then Return
            _versionsDirty = True
            If _busy Then Return
            EmptyVersions.Visibility = If(_versions.Count = 0 AndAlso _downloadView = DownloadView.Versions, Visibility.Visible, Visibility.Collapsed)
            EmptyVersions.Text = "版本列表读取失败，请刷新或切换下载来源。"
            StatusText.Text = "刷新失败：" & ex.GetBaseException().Message & If(_versions.Count > 0, "；仍显示上次的版本列表。", "")
            If Not _preview Then Log(ex)
        Finally
            If _versionCancellation Is request Then
                _versionCancellation = Nothing
                _loading = False
            End If
            request.Dispose()
        End Try
    End Function

    Private Function SupplementalCatalogueStatus(available As Boolean) As String
        Dim status = "已读取 " & _versions.Count & " 个版本"
        If Not available Then Return status & "（补充清单暂不可用，可刷新重试）。"
        Dim added = _versions.Where(Function(entry) entry.IsSupplemental).ToList()
        If added.Count = 0 Then Return status & "（无新增补充版本）。"
        Dim categories = added.Select(Function(entry) SupplementalVersions.Label(entry)).Distinct().ToList()
        Return status & "，含 " & added.Count & " 个补充版本：" & String.Join("、", categories) & "。"
    End Function

    Private Async Sub RefreshVersions(sender As Object, e As RoutedEventArgs)
        If _busy Then Return
        If _downloadView = DownloadView.Install Then
            Await RefreshComponentListsAsync()
        ElseIf _downloadView = DownloadView.Versions Then
            Await LoadVersionsAsync(True)
        End If
    End Sub

    Private Sub VersionSourceChanged(sender As Object, e As SelectionChangedEventArgs)
        If _restoringSettings OrElse _busy OrElse _closing Then Return
        _versionsDirty = True
        _versionGeneration += 1
        _versionCancellation?.Cancel()
        CancelComponentRequests()
        _componentsDirty = _selectedEntry IsNot Nothing
        If _selectedEntry IsNot Nothing Then RefreshComponents()
    End Sub

    Private Async Function RefreshCurrentListsIfNeededAsync() As Task
        If _busy OrElse _closing Then Return
        If _downloadView = DownloadView.Install AndAlso _componentsDirty Then
            Await RefreshComponentListsAsync()
        ElseIf _downloadView = DownloadView.Versions AndAlso _versionsDirty Then
            Await LoadVersionsAsync(True)
        End If
    End Function

    Private Sub UpdateVersions()
        If DownloadPage.IsVisible AndAlso _downloadView = DownloadView.Versions Then
            Dim refocus = _focusAfterPageSwitch
            _focusAfterPageSwitch = False
            ' A completed catalogue replaces its own entry group, as PCL's loader
            ' completion enters fresh content. Hidden catalogues do not touch it.
            Dim replace As Action = Sub()
                                        If _pageSwitch.Transition IsNot Nothing Then _pageSwitch.Finish()
                                        RenderVersionCatalogue()
                                    End Sub
            If _categoryMotion.Transition IsNot Nothing Then
                ' Retarget the controller already owning this viewport. Starting
                ' a parent entry here would animate the same catalogue twice.
                _categoryMotion.Change(VersionPage, VersionPage, replace, contentRefresh:=True)
            Else
                _surfaceMotion.Change(DownloadPage, DownloadPage, replace, contentRefresh:=True)
            End If
            If refocus Then FocusDownloadPage()
            If _surfaceMotion.Transition Is Nothing Then RestoreSurfaceFocus()
        Else
            RenderVersionCatalogue()
        End If
    End Sub

    Private Sub RenderVersionCatalogue()
        VersionCards.Children.Clear()
        SetDownloadView(_downloadView)
        EmptyVersions.Text = If(_versions.Count = 0, "点击刷新读取版本列表", "没有匹配的版本")
        If _versions.Count = 0 Then Return
        Dim versions = _versions.Select(Function(v) New VersionPresentation(v)).OrderByDescending(Function(v) v.Entry.Released).ToList()
        Dim releases = versions.Where(Function(v) v.Kind = "release" AndAlso Not v.Entry.IsSupplemental).ToList()
        Dim snapshots = versions.Where(Function(v) v.Kind = "snapshot" AndAlso Not v.Entry.IsSupplemental).ToList()
        Dim latest As New List(Of VersionPresentation)
        If releases.Count > 0 Then latest.Add(releases(0))
        If snapshots.Count > 0 AndAlso (releases.Count = 0 OrElse snapshots(0).Entry.Released > releases(0).Entry.Released) Then latest.Add(snapshots(0))
        AddVersionCard("最新版本", latest, False, True)
        For Each category In {"正式版", "预览版", "远古版", "愚人节版"}
            AddVersionCard(category, versions.Where(Function(v) v.Category = category).ToList(), True, False)
        Next
        AddSupplementalVersionCards()
    End Sub

    Private Sub AddSupplementalVersionCards()
        For Each category In {"特供版本", "远古版本补充", "历史快照补充"}
            If VersionCards.Children.OfType(Of PclCard)().Any(Function(card) card.Title.StartsWith(category & " (", StringComparison.Ordinal)) Then Continue For
            Dim entries = _versions.Where(Function(entry) entry.IsSupplemental).Select(Function(entry) New VersionPresentation(entry)).
                Where(Function(entry) entry.Category = category).OrderByDescending(Function(entry) entry.Entry.Released).ToList()
            AddVersionCard(category, entries, True, False)
        Next
    End Sub

    Private Sub AddVersionCard(title As String, entries As List(Of VersionPresentation), collapsed As Boolean, latest As Boolean)
        If entries.Count = 0 Then Return
        Dim card As New PclCard With {.Title = title & If(latest, "", " (" & entries.Count & ")"), .Collapsible = Not latest}
        card.Builder = Function()
                           Dim stack As New StackPanel()
                           For Each entry In entries
                               stack.Children.Add(New PclLazyRow(Function()
                                                                    Dim row As New PclListItem With {.Title = entry.DisplayId, .Height = 42, .Info = entry.Description(latest), .Logo = entry.Icon, .Tag = entry.Entry}
                                                                    AddHandler row.Click, Sub(sender, args)
                                                                                              Dim keyboard = TypeOf args Is Input.KeyEventArgs
                                                                                              _keyboardVersionReturn = If(keyboard, New WeakReference(Of PclListItem)(row), Nothing)
                                                                                              SelectVersion(entry.Entry)
                                                                                              If keyboard Then FocusDownloadPage()
                                                                                          End Sub
                                                                    Return row
                                                                End Function))
                           Next
                           Return stack
                       End Function
        card.Collapsed = collapsed
        VersionCards.Children.Add(card)
    End Sub

    Private Shared Function IconFor(entry As VersionEntry) As String
        Return New VersionPresentation(entry).Icon
    End Function

    Private Sub SelectVersion(entry As VersionEntry)
        If _busy Then Return
        _selectedEntry = entry
        _nameEdited = False
        Dim displayId = New VersionPresentation(entry).DisplayId
        SetAutomaticInstanceName(displayId)
        SelectedVersionText.Text = displayId
        SelectedVersionIcon.Source = New Imaging.BitmapImage(New Uri(IconFor(entry)))
        VanillaVersionIcon.Source = SelectedVersionIcon.Source
        InitializeComponents()
        SetDownloadView(DownloadView.Install)
        InstallScroll.ScrollToHome()
        SwitchMainPage(False)
    End Sub

    Private Sub DiscoverFolders(basePath As String)
        Dim discovery As New PclFolders()
        _folders = discovery.Discover(basePath)
        FolderList.ItemsSource = _folders
        PclLocation.Text = "PCL 所在位置：" & basePath
        If _folders.Count > 0 Then
            FolderList.SelectedIndex = 0
        Else
            StatusText.Text = "未发现游戏目录，请选择 PCL 启动器或直接选择 .minecraft。"
        End If
        If discovery.Warnings.Count > 0 Then StatusText.Text = String.Join("；", discovery.Warnings)
    End Sub

    Private Sub ChoosePcl(sender As Object, e As RoutedEventArgs)
        Dim dialog As New Microsoft.Win32.OpenFileDialog With {.Title = "选择 PCL 启动器", .Filter = "启动器程序 (*.exe)|*.exe", .CheckFileExists = True}
        If dialog.ShowDialog(Me) <> True Then Return
        _pclBase = Path.GetDirectoryName(dialog.FileName)
        DiscoverFolders(_pclBase)
        PersistSettings()
    End Sub

    Private Sub ChooseFolder(sender As Object, e As RoutedEventArgs)
        Using dialog As New System.Windows.Forms.FolderBrowserDialog With {.Description = "选择 PCL 使用的 .minecraft 游戏根目录", .ShowNewFolderButton = True}
            If _selectedFolder IsNot Nothing Then dialog.SelectedPath = _selectedFolder.Location
            If dialog.ShowDialog() <> System.Windows.Forms.DialogResult.OK Then Return
            SelectManualFolder(dialog.SelectedPath)
        End Using
        PersistSettings()
    End Sub

    Private Sub SelectManualFolder(root As String)
        root = Path.GetFullPath(root)
        Dim match = _folders.FirstOrDefault(Function(f) String.Equals(f.Location.TrimEnd("\"c), root.TrimEnd("\"c), StringComparison.OrdinalIgnoreCase))
        If match Is Nothing Then
            match = New GameFolder With {.Name = "手动选择的游戏目录", .Location = root, .Origin = "手动选择"}
            _folders.Add(match)
            FolderList.ItemsSource = Nothing
            FolderList.ItemsSource = _folders
        End If
        FolderList.SelectedItem = match
    End Sub

    Private Sub FolderSelected(sender As Object, e As SelectionChangedEventArgs)
        Dim folder = TryCast(FolderList.SelectedItem, GameFolder)
        If folder Is Nothing Then
            _selectedFolder = Nothing
            SelectedPath.Text = "请选择 PCL 的 .minecraft 文件夹"
            FolderSummary.Text = "安装前请选择游戏文件夹"
            RefreshInstallAvailability()
            Return
        End If
        _selectedFolder = folder
        SelectedPath.Text = folder.Location
        FolderSummary.Text = folder.Origin & " · " & folder.VersionCount & " 个已安装实例 · 共享支持库与资源文件"
        StatusText.Text = "已选择游戏目录：" & folder.Location
        RefreshInstallAvailability()
        PersistSettings()
    End Sub

    Private Sub SwitchMainPage(settings As Boolean, Optional prepare As Action = Nothing)
        If settings Then
            _focusAfterPageSwitch = False
        End If
        SelectSurface(If(settings, DirectCast(SettingsPage, FrameworkElement), DownloadPage),
            Sub()
                If settings AndAlso _pageSwitch.Transition IsNot Nothing Then _pageSwitch.Finish()
                If settings Then _categoryMotion.Finish()
                prepare?.Invoke()
            End Sub)
    End Sub

    Private Sub SetDownloadView(view As DownloadView)
        If view = _downloadView Then
            RefreshDownloadChrome()
            Return
        End If
        If view <> _downloadView Then
            _pendingSurfaceFocus = Nothing
            _surfaceMotion.Finish()
        End If
        Dim previous = _downloadView
        If (previous = DownloadView.Modpacks) Xor (view = DownloadView.Modpacks) Then
            If _pageSwitch.Transition IsNot Nothing Then _pageSwitch.Finish()
            Dim outgoing = DownloadContent(previous), incoming = DownloadContent(view)
            _categoryMotion.Change(outgoing, incoming, Sub() ApplyDownloadView(view, previous), compositeExit:=HasPackContentMotion())
        Else
            _categoryMotion.Finish()
            ApplyDownloadView(view, previous)
        End If
    End Sub
    Private Function DownloadContent(view As DownloadView) As FrameworkElement
        Select Case view
            Case DownloadView.Modpacks : Return ModpackPage
            Case DownloadView.Install : Return InstallPage
            Case DownloadView.Progress : Return DownloadProgress
            Case Else : Return VersionPage
        End Select
    End Function
    Private Sub ApplyDownloadView(view As DownloadView, previous As DownloadView)
        ' SetDownloadView captures an active pack animation before reaching here.
        If previous = DownloadView.Modpacks AndAlso view <> DownloadView.Modpacks Then FinishPackContentMotion()
        If view = DownloadView.Progress AndAlso previous <> DownloadView.Progress Then _progressReturnView = previous
        If view = DownloadView.Versions OrElse view = DownloadView.Install Then _vanillaView = view
        _downloadView = view
        If view = DownloadView.Progress OrElse view = DownloadView.Modpacks Then
            _focusAfterPageSwitch = False
            _pageSwitch.Finish()
            VersionPage.Visibility = Visibility.Collapsed
            InstallPage.Visibility = Visibility.Collapsed
        Else
            _pageSwitch.SelectInstall(view = DownloadView.Install, previous <> DownloadView.Progress AndAlso previous <> DownloadView.Modpacks)
            If previous = DownloadView.Progress OrElse previous = DownloadView.Modpacks Then _pageSwitch.Finish()
            If _pageSwitch.Transition IsNot Nothing AndAlso DownloadViewHost.IsKeyboardFocusWithin Then
                _focusAfterPageSwitch = True
                DownloadTab.Focus()
            End If
        End If
        DownloadProgress.Visibility = If(view = DownloadView.Progress, Visibility.Visible, Visibility.Collapsed)
        ModpackPage.Visibility = If(view = DownloadView.Modpacks, Visibility.Visible, Visibility.Collapsed)
        RefreshDownloadChrome()
    End Sub
    Private Sub RefreshDownloadChrome()
        Dim view = _downloadView
        Dim packCategory = view = DownloadView.Modpacks OrElse (view = DownloadView.Progress AndAlso _progressReturnView = DownloadView.Modpacks)
        VanillaNav.Selected = Not packCategory : ModpackNav.Selected = packCategory
        EmptyVersions.Visibility = If(view = DownloadView.Versions AndAlso _versions.Count = 0, Visibility.Visible, Visibility.Collapsed)
        RefreshButton.Visibility = If(view = DownloadView.Versions OrElse view = DownloadView.Install, Visibility.Visible, Visibility.Collapsed)
        RefreshButton.ToolTip = If(view = DownloadView.Install, "重新获取组件列表，保留已选版本与名称", "重新获取 Minecraft 版本列表")
        ProgressShortcut.Visibility = If(_hasTask AndAlso view <> DownloadView.Progress, Visibility.Visible, Visibility.Collapsed)
    End Sub
    Private Async Sub ShowVanillaDownloads(sender As Object, e As RoutedEventArgs)
        If _busy Then Return
        SetDownloadView(_vanillaView)
        Await RefreshCurrentListsIfNeededAsync()
    End Sub
    Private Sub ShowModpackDownloads(sender As Object, e As RoutedEventArgs)
        If _busy Then Return
        SetDownloadView(DownloadView.Modpacks)
        EnsurePackSearch()
    End Sub

    Private Async Sub ShowDownload(sender As Object, e As RoutedEventArgs)
        If _busy Then SetDownloadView(DownloadView.Progress)
        SwitchMainPage(False)
        Await RefreshCurrentListsIfNeededAsync()
    End Sub
    Private Sub ChooseInstallFolder(sender As Object, e As RoutedEventArgs)
        NavigateToSetting(GameFolderCard)
    End Sub
    Private Sub ShowSettings(sender As Object, e As RoutedEventArgs)
        SwitchMainPage(True)
    End Sub
    Private Sub ShowProgress(sender As Object, e As RoutedEventArgs)
        If Not _hasTask Then Return
        SetDownloadView(DownloadView.Progress)
        SwitchMainPage(False)
        RefreshTaskResultActions()
    End Sub
    Private Async Sub ResumeInstallation(sender As Object, e As RoutedEventArgs)
        SetDownloadView(If(_progressReturnView = DownloadView.Modpacks, DownloadView.Modpacks, If(_selectedEntry Is Nothing, DownloadView.Versions, DownloadView.Install)))
        Await RefreshCurrentListsIfNeededAsync()
    End Sub
    Private Async Sub BackToVersions(sender As Object, e As RoutedEventArgs)
        SetDownloadView(DownloadView.Versions)
        Await RefreshCurrentListsIfNeededAsync()
    End Sub
    Private Sub DragTitle(sender As Object, e As Input.MouseButtonEventArgs)
        If e.ChangedButton = Input.MouseButton.Left Then DragMove()
    End Sub
    Private Sub MinimizeWindow(sender As Object, e As RoutedEventArgs)
        WindowState = WindowState.Minimized
    End Sub
    Private Sub CloseWindow(sender As Object, e As RoutedEventArgs)
        Close()
    End Sub

    Private Async Sub StartInstall(sender As Object, e As RoutedEventArgs)
        If _busy OrElse _closing OrElse Not PageSurface.IsHitTestVisible OrElse Not DownloadViewHost.IsHitTestVisible Then Return
        If sender Is ModrinthInstallButton OrElse sender Is ModrinthInstanceName Then _activeModrinth = True
        If sender Is PackInstallButton OrElse sender Is PackInstanceName Then _activeModrinth = False
        Dim pack = If(_downloadView = DownloadView.Modpacks AndAlso Not _activeModrinth, _packSelection, Nothing)
        Dim modrinth = If(_downloadView = DownloadView.Modpacks AndAlso _activeModrinth, _modrinthSelection, Nothing)
        Dim isPack = pack IsNot Nothing OrElse modrinth IsNot Nothing
        If _downloadView = DownloadView.Modpacks AndAlso (_readingPack OrElse Not isPack) Then Return
        If _selectedFolder Is Nothing Then
            ChooseInstallFolder(sender, e)
            StatusText.Text = "请先选择游戏目录。"
            Return
        End If
        RefreshPackAvailability()
        Dim installProblem = If(modrinth IsNot Nothing, ModrinthNameProblem(), If(pack IsNot Nothing, PackNameProblem(), RefreshInstallAvailability()))
        If installProblem IsNot Nothing Then
            StatusText.Text = installProblem
            Return
        End If
        Dim entry = If(isPack, New VersionEntry With {.Id = If(modrinth IsNot Nothing, modrinth.Minecraft, pack?.Minecraft), .Kind = "release"}, _selectedEntry?.Copy())
        If entry Is Nothing Then
            StatusText.Text = "请先选择 Minecraft 版本。"
            Return
        End If
        Dim limit As Double
        If Not TryGetSpeedLimit(limit) Then
            NavigateToSetting(SpeedLimitBox, True)
            StatusText.Text = "请检查速度上限的输入提示。"
            Return
        End If
        Dim root = _selectedFolder.Location
        Dim name = If(modrinth IsNot Nothing, ModrinthInstanceName.Text, If(pack IsNot Nothing, PackInstanceName.Text, InstanceName.Text))
        Dim repair = RepairBox.IsChecked.GetValueOrDefault()
        Dim optionalFiles = PackOptionalFiles.Children.OfType(Of PclCheckBox)().Where(Function(box) box.IsChecked.GetValueOrDefault()).Select(Function(box) CLng(box.Tag)).ToArray()
        Dim modrinthOptions = ModrinthOptionalFiles.Children.OfType(Of PclCheckBox)().Where(Function(box) box.IsChecked.GetValueOrDefault()).Select(Function(box) CStr(box.Tag)).ToArray()
        If isPack Then repair = False
        Dim selection = If(isPack, New ComponentSelection(), _componentSelection.Copy())
        selection.JavaPath = JavaPathBox.Text.Trim()
        Try
            InstallPreflight.ValidateDestination(root, name, repair)
            selection.Validate(entry.Id)
            If repair AndAlso selection.Entries.Count > 0 Then Throw New InvalidOperationException("请为组件安装使用新名称；原版校验仅支持未添加加载器的实例。")
        Catch ex As Exception
            MessageBox.Show(Me, ex.Message, "检查安装选项", MessageBoxButton.OK, MessageBoxImage.Information)
            Return
        End Try
        Dim connections = ConnectionCount()
        Dim isolation = If(isPack, InstanceIsolationMode.Enabled, If(repair, InstanceIsolationMode.FollowPcl, SelectedIsolation()))
        Dim reuse = If(ReuseBox.IsChecked = True, _folders.Select(Function(f) f.Location).ToList(), New List(Of String))
        Dim installer As New MinecraftInstaller(Policy(), _pclBase)
        Dim packInstaller As New CurseForgeInstaller(Policy(), _pclBase)
        _cancellation = New CancellationTokenSource()
        _busy = True
        SetBusy(True)
        PersistSettings()
        TaskPanel.Visibility = Visibility.Visible
        ResetTaskView(name, If(modrinth IsNot Nothing, modrinth.LoaderKind.HasValue, If(pack IsNot Nothing, pack.LoaderKind.HasValue, selection.Entries.Count > 0)), root)
        _taskMinecraft = entry.Id
        If pack IsNot Nothing Then
            _taskComponents = "CurseForge " & pack.Name & " " & pack.Version & If(pack.LoaderKind.HasValue, " · " & pack.LoaderKind.ToString() & " " & pack.LoaderVersion, "")
            _taskRows("metadata").Label.Text = "查询整合包文件与游戏版本信息"
            _taskRows("files").Label.Text = "下载游戏、资源与整合包文件"
        End If
        If modrinth IsNot Nothing Then
            _taskComponents = "Modrinth " & modrinth.Name & " " & modrinth.Version & If(modrinth.LoaderKind.HasValue, " · " & modrinth.LoaderKind.ToString() & " " & modrinth.LoaderVersion, "")
            _taskRows("metadata").Label.Text = "校验整合包清单与游戏版本信息"
            _taskRows("files").Label.Text = "下载游戏、资源与整合包文件"
        End If
        ShowProgress(Nothing, Nothing)
        Try
            Dim progress = CreateTaskProgress()
            Dim result As InstallResult
            If _installRunner IsNot Nothing Then
                result = Await _installRunner(entry, root, name, selection, isolation, _cancellation.Token)
            ElseIf _preview Then
                Throw New InvalidOperationException("界面预览不会执行实际安装。")
            ElseIf modrinth IsNot Nothing Then
                result = Await Task.Run(Function() New ModrinthInstaller(Policy(), _pclBase).InstallAsync(modrinth, root, name, reuse, modrinthOptions, progress, _cancellation.Token, connections, CLng(limit * 1024 * 1024), selection.JavaPath))
            ElseIf pack IsNot Nothing Then
                result = Await Task.Run(Function() packInstaller.InstallAsync(pack, root, name, reuse, optionalFiles, progress, _cancellation.Token, connections, CLng(limit * 1024 * 1024), selection.JavaPath))
            Else
                result = Await Task.Run(Function() installer.InstallAsync(entry, root, name, reuse, progress, _cancellation.Token, repair, connections, CLng(limit * 1024 * 1024), selection, isolation))
            End If
            TaskProgress.IsIndeterminate = False
            TaskProgress.Value = 1
            TaskTitle.Text = If(repair, "校验补全完成", "安装完成") & " · " & name
            _taskInstancePath = result.InstancePath
            StatusText.Text = "完成：" & result.FileCount & " 个文件，复用 " & result.Reused & " 个，传输 " & FormatBytes(result.NetworkBytes) & "。回到 PCL 刷新版本列表即可。"
            FinishTaskView(True, StatusText.Text)
            If Not _preview Then WriteLog(StatusText.Text & Environment.NewLine & result.InstancePath)
            FolderSummary.Text = _selectedFolder.Origin & " · " & _selectedFolder.VersionCount & " 个已安装实例"
        Catch ex As OperationCanceledException
            TaskTitle.Text = "已取消，可重新开始"
            StatusText.Text = "已取消。已完成的共享文件会保留，后续会校验复用。"
            FinishTaskView(False, StatusText.Text, cancelled:=True)
        Catch ex As Exception
            If _cancellation.IsCancellationRequested Then
                TaskTitle.Text = "已取消，可重新开始"
            Else
                TaskTitle.Text = "安装未完成，可重试"
            End If
            TaskProgress.IsIndeterminate = False
            StatusText.Text = ex.GetBaseException().Message
            FinishTaskView(False, StatusText.Text)
            If Not _preview Then
                Log(ex)
                If Not _cancellation.IsCancellationRequested Then MessageBox.Show(Me, ex.GetBaseException().Message & Environment.NewLine & "详细记录已写入日志目录。", "安装未完成", MessageBoxButton.OK, MessageBoxImage.Warning)
            End If
        Finally
            _busy = False
            _cancellation.Dispose()
            _cancellation = Nothing
            SetBusy(False)
        End Try
    End Sub

    Private Sub SetBusy(value As Boolean)
        PackSearchSurface.IsEnabled = Not value
        PackDetailSurface.IsEnabled = Not value
        RefreshInstallAvailability(value)
        InstanceName.IsEnabled = Not value
        RepairBox.IsEnabled = Not value
        RefreshIsolation(value)
        VersionCards.IsEnabled = Not value
        ComponentCards.IsEnabled = Not value
        RefreshPackAvailability(value)
        DownloadNav.IsEnabled = Not value
        GameFolderCard.IsEnabled = Not value
        DownloadSettingsCard.IsEnabled = Not value
        ChangeFolderButton.IsEnabled = Not value
        RefreshButton.IsEnabled = Not value
        CancelButton.IsEnabled = value
    End Sub

    Private Function SelectedIsolation() As InstanceIsolationMode
        Return If(IsolationBox.SelectedIndex >= 0 AndAlso IsolationBox.SelectedIndex <= 2, CType(IsolationBox.SelectedIndex, InstanceIsolationMode), InstanceIsolationMode.FollowPcl)
    End Function
    Private Sub IsolationChanged(sender As Object, e As RoutedEventArgs)
        RefreshIsolation(_busy)
    End Sub
    Private Sub RefreshIsolation(busy As Boolean)
        If IsolationBox Is Nothing OrElse IsolationHint Is Nothing OrElse RepairBox Is Nothing Then Return
        Dim repair As Boolean = RepairBox.IsChecked.GetValueOrDefault()
        IsolationBox.IsEnabled = Not busy AndAlso Not repair
        IsolationHint.Text = If(repair, "补全时保留已有实例的隔离设置，此选项不参与修改。",
            If(SelectedIsolation() = InstanceIsolationMode.Disabled, "Mods、资源包与旧版 resources 使用游戏根目录。多个版本会共用 Mods，请留意兼容性；同名不同内容的文件不会被覆盖。",
            If(SelectedIsolation() = InstanceIsolationMode.Enabled, "Mods、资源包与旧版 resources 使用实例目录；支持库与 assets 仍在游戏根目录共享。",
                "跟随所选 PCL 的默认隔离策略；未找到设置时使用 PCL 默认值。关闭隔离时，多个版本会共用 Mods。")))
    End Sub

    Private Function CreateTaskProgress() As IProgress(Of TransferProgress)
        Dim generation = _taskGeneration
        Return New Progress(Of TransferProgress)(Sub(value) UpdateProgress(generation, value))
    End Function
    Private Sub UpdateProgress(generation As Long, value As TransferProgress)
        If Not _busy OrElse _closing OrElse generation <> _taskGeneration OrElse _taskFinished OrElse _taskCancelling Then Return
        RenderTaskProgress(value)
    End Sub

    Private Shared Function FormatBytes(value As Double) As String
        If value >= 1024 * 1024 * 1024 Then Return (value / (1024 * 1024 * 1024)).ToString("0.00") & " GiB"
        If value >= 1024 * 1024 Then Return (value / (1024 * 1024)).ToString("0.0") & " MiB"
        If value >= 1024 Then Return (value / 1024).ToString("0") & " KiB"
        Return value.ToString("0") & " B"
    End Function

    Private Sub CancelInstall(sender As Object, e As RoutedEventArgs)
        If Not _busy OrElse _taskFinished OrElse _taskCancelling Then Return
        Dim cancellation = _cancellation
        _taskCancelling = True
        CancelButton.IsEnabled = False
        TaskTitle.Text = "正在取消…"
        TaskProgress.IsIndeterminate = True
        TaskSpeed.Text = "—" : TaskConnections.Text = "—" : TaskRemaining.Text = "—"
        TaskDetail.Text = "正在等待下载连接与安装处理器退出，请稍候…"
        StatusText.Text = "已请求取消。已完成的共享文件会保留，后续会校验复用。"
        ' Like PCL LoaderBase.State, publish the state before invoking token
        ' callbacks. They may finish the awaited install inline on this dispatcher.
        cancellation?.Cancel()
    End Sub

    Private Sub WindowClosing(sender As Object, e As CancelEventArgs)
        If _busy Then
            CancelInstall(sender, Nothing)
            If _busy Then
                e.Cancel = True
                StatusText.Text = "正在停止下载，请稍后关闭窗口。"
                Return
            End If
        End If
        _closing = True
        CompleteOnlinePrompt(Nothing)
        _packSearchRequest?.Cancel()
        _packDetailRequest?.Cancel()
        _packArtworkRequest?.Cancel()
        _packArtworkRequest?.Dispose()
        _packArtworkRequest = Nothing
        FinishPackContentMotion()
        _packReadCancellation?.Cancel()
        _pendingSurfaceFocus = Nothing
        _focusAfterPageSwitch = False
        _pageSwitch.Finish()
        _categoryMotion.Finish()
        _surfaceMotion.Finish()
        For Each viewer In MotionViewers()
            viewer.StopScrolling()
        Next
        _versionGeneration += 1
        _versionCancellation?.Cancel()
        CancelComponentRequests()
        If Not _preview Then PersistSettings()
    End Sub

    Private Function PersistSettings(Optional explicitSave As Boolean = False) As Boolean
        If _restoringSettings Then Return False
        If _settingsLoadFailed Then
            ReportSettingsSave("偏好读取失败，已暂停自动保存；请检查后点击保存设置。", True)
            Return False
        End If
        Dim limit As Double
        If Not TryGetSpeedLimit(limit) Then
            ReportSettingsSave("设置尚未保存，请检查下载速度限制。", True)
            Return False
        End If
        If _preview Then Return True
        Try
            SavePreferenceValues(SettingsFile, LegacySettingsFile, limit, explicitSave)
            Return True
        Catch ex As Exception
            ReportSettingsSave("偏好设置无法保存：" & ex.Message, True)
            Return False
        End Try
    End Function

    Private Sub SaveSettings(sender As Object, e As RoutedEventArgs)
        Dim wasBlocked = _settingsLoadFailed
        _settingsLoadFailed = False
        If PersistSettings(True) Then
            ReportSettingsSave("下载设置已保存。", False)
        Else
            _settingsLoadFailed = wasBlocked
            Dim limit As Double
            If Not TryGetSpeedLimit(limit) Then NavigateToSetting(SpeedLimitBox, True)
        End If
    End Sub
    Private Sub OpenLogs(sender As Object, e As RoutedEventArgs)
        Directory.CreateDirectory(Path.Combine(_settingsDirectory, "logs"))
        Process.Start("explorer.exe", Path.Combine(_settingsDirectory, "logs"))
    End Sub
    Private Sub Log(ex As Exception)
        WriteLog(ex.ToString())
    End Sub
    Private Sub WriteLog(value As String)
        Try
            Dim directory = Path.Combine(_settingsDirectory, "logs")
            IO.Directory.CreateDirectory(directory)
            File.AppendAllText(Path.Combine(directory, DateTime.Now.ToString("yyyy-MM-dd") & ".log"), DateTime.Now.ToString("O") & " " & value & Environment.NewLine)
        Catch
            ' A logging failure must not obscure the original install failure.
        End Try
    End Sub
End Class
