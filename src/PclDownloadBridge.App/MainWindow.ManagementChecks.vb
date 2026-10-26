Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Sub VerifyManagementUi(directory As String, check As Action(Of Boolean, String))
        Dim owner As New MainWindow(True), original = owner.Content
        Dim host As New Border With {.Width = 850, .Height = 700}, layout = ControlLayout(host)
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using source = PreviewSurface.Attach(host, 850, 700)
                layout()
                Dim work = owner.VerifyManagementWorkflowAsync(directory, host, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 20000) Then Throw New TimeoutException("Management UI did not settle.")
                work.GetAwaiter().GetResult()
            End Using
        Finally
            owner._closing = True : owner.CancelManagementRequest()
            owner._managementMotion.Finish() : owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner._pageSwitch.Finish()
            host.Child = Nothing : owner.Content = original
        End Try
    End Sub

    Private Function PrepareManagementFixture(directory As String) As List(Of GameFolder)
        Dim fixture = Path.Combine(directory, "management-ui-" & Guid.NewGuid().ToString("N")), game = Path.Combine(fixture, "游戏目录")
        Dim pcl = Path.Combine(fixture, "PCL"), ce = Path.Combine(fixture, "CE"), hmcl = Path.Combine(fixture, "HMCL")
        IO.Directory.CreateDirectory(Path.Combine(pcl, "PCL")) : IO.Directory.CreateDirectory(Path.Combine(ce, "PCL")) : IO.Directory.CreateDirectory(hmcl)
        File.WriteAllText(Path.Combine(pcl, "PCL/Setup.ini"), "LaunchArgumentIndieV2:0" & vbCrLf & "Unrelated:保留")
        File.WriteAllText(Path.Combine(ce, "PCL/config.v1.yml"), "LaunchArgumentIndieV2: 4" & vbLf & "Unrelated: keep")
        Dim hmclConfig = Path.Combine(hmcl, "hmcl.json")
        File.WriteAllText(hmclConfig, New JObject From {{"configurations", New JObject From {{"fixture", New JObject From {
            {"gameDir", game}, {"global", New JObject From {{"gameDirType", 0}, {"unrelated", "keep"}}}}}}}}.ToString())
        Dim plain = Path.Combine(game, "versions", "我的原版"), fabric = Path.Combine(game, "versions", "Fabric 实例"), broken = Path.Combine(game, "versions", "无法读取")
        For Each instance In {plain, fabric, broken}
            IO.Directory.CreateDirectory(instance)
        Next
        Dim metadata As New JObject From {{"id", "我的原版"}, {"clientVersion", "1.20.1"}, {"type", "release"},
            {"mainClass", "net.minecraft.client.main.Main"}, {"libraries", New JArray()},
            {"downloads", New JObject From {{"client", New JObject From {{"sha1", New String("a"c, 40)}, {"size", 1}, {"url", "https://piston-data.mojang.com/fixture.jar"}}}}},
            {"assetIndex", New JObject From {{"id", "fixture"}, {"sha1", New String("b"c, 40)}, {"url", "https://piston-meta.mojang.com/fixture.json"}}}}
        File.WriteAllText(Path.Combine(plain, "我的原版.json"), metadata.ToString())
        Dim modded = DirectCast(metadata.DeepClone(), JObject)
        modded("id") = "Fabric 实例" : modded("mainClass") = "net.fabricmc.loader.impl.launch.knot.KnotClient"
        DirectCast(modded("libraries"), JArray).Add(New JObject From {{"name", "net.fabricmc:fabric-loader:0.16.10"}})
        File.WriteAllText(Path.Combine(fabric, "Fabric 实例.json"), modded.ToString())
        IO.Directory.CreateDirectory(Path.Combine(fabric, "PCL"))
        File.WriteAllText(Path.Combine(fabric, "PCL/Setup.ini"), "VersionArgumentIndieV2:True" & vbCrLf & "WindowWidth:900")
        File.WriteAllText(Path.Combine(broken, "无法读取.json"), "{broken")
        For Each relative In {"mods", "saves", "screenshots"}
            IO.Directory.CreateDirectory(Path.Combine(game, relative))
            IO.Directory.CreateDirectory(Path.Combine(fabric, relative))
        Next
        _pclBase = pcl
        Dim result As New List(Of GameFolder) From {
            New GameFolder With {.Name = "PCL 游戏目录", .Location = game, .Origin = "UI fixture", .LauncherKind = ExternalLauncherKind.Pcl, .LauncherBindingId = "pcl-fixture", .LauncherWorkspace = pcl},
            New GameFolder With {.Name = "HMCL 同一游戏目录", .Location = game, .Origin = "UI fixture", .LauncherKind = ExternalLauncherKind.Hmcl, .LauncherBindingId = "hmcl-fixture", .LauncherWorkspace = hmcl, .LauncherConfigPath = hmclConfig, .LauncherSharedConfigPath = Path.Combine(hmcl, "shared.json")},
            New GameFolder With {.Name = "CE 同一游戏目录", .Location = game, .Origin = "UI fixture", .LauncherKind = ExternalLauncherKind.PclCe, .LauncherBindingId = "ce-fixture", .LauncherWorkspace = ce},
            New GameFolder With {.Name = "尚不存在的目录", .Location = Path.Combine(fixture, "empty"), .Origin = "UI fixture", .LauncherKind = ExternalLauncherKind.Pcl, .LauncherBindingId = "empty-fixture", .LauncherWorkspace = pcl}}
        _managementProvider = Function(target, token) Task.FromResult(InstalledInstanceCatalog.Scan(target, token))
        _folders = result : FolderList.ItemsSource = result : FolderList.SelectedItem = result(0)
        RefreshManagementFolders()
        Return result
    End Function

    Private Async Function VerifyManagementWorkflowAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim folders = PrepareManagementFixture(directory), root = folders(0).Location
        Dim bytes = IO.Directory.GetFiles(Path.GetDirectoryName(root), "*", SearchOption.AllDirectories).ToDictionary(Function(filePath) filePath, Function(filePath) File.ReadAllBytes(filePath))
        SelectVersion(_versions.Single(Function(entry) entry.Id = "1.20.1"))
        SelectComponent(_componentEntries(ComponentKind.Fabric)(0)) : InstanceName.Text = "保留下载方案"
        Dim entryBefore = _selectedEntry, componentBefore = _componentSelection.Item(ComponentKind.Fabric), taskGeneration = _taskGeneration
        ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(False) : layout()
        check(ManagementTab.Text = "管理" AndAlso ManagementTab.Checked AndAlso ManagementPage.IsVisible AndAlso Not DownloadPage.IsVisible AndAlso Not SettingsPage.IsVisible,
            "management owns an independent main tab without revealing hidden download or settings pages")
        check(_managementInstances.Count = 3 AndAlso ManagementRows.Children.Count = 3 AndAlso ManagementSummary.Text.Contains("3"),
            "installed management includes readable instances and an individual damaged instance")
        check(ManagementFolders.Items.Count = 4 AndAlso ManagementFolders.SelectedItem Is _selectedFolder,
            "management retains overlapping PCL HMCL CE binding identities and the selected target")
        check(_selectedEntry Is entryBefore AndAlso _componentSelection.Item(ComponentKind.Fabric) Is componentBefore AndAlso InstanceName.Text = "保留下载方案" AndAlso _taskGeneration = taskGeneration,
            "opening and scanning management preserves the existing download plan and task generation")
        Dim plain = _managementInstances.Single(Function(item) item.Name = "我的原版"), fabric = _managementInstances.Single(Function(item) item.Name = "Fabric 实例")
        ManagementSearch.Text = "fabric" : layout()
        check(ManagementRows.Children.Count = 1 AndAlso ManagementSummary.Text.Contains("1"), "local search matches loader identity without another directory scan")
        ManagementSearch.Text = "does-not-exist" : layout()
        check(ManagementRows.Children.Count = 0 AndAlso Not ManagementListCard.IsVisible AndAlso ManagementStatus.Text.Contains("没有找到"),
            "a nonmatching local search shows an explicit empty result")
        ManagementSearch.Text = "" : layout()
        SelectManagedInstance(plain) : layout()
        check(ManagementDetailPage.IsVisible AndAlso Not ManagementListPage.IsVisible AndAlso _managedInstance Is plain AndAlso InnerTitle.Text = plain.Name,
            "an installed row opens a title-back detail page for that exact instance")
        check(ManagedInstancePath.Text = plain.InstancePath AndAlso ManagedRunPath.Text = root AndAlso ManagedIsolation.Text.Contains("共享") AndAlso ManagedVersion.Text.Contains("1.20.1"),
            "instance details distinguish version storage from its shared runtime directory")
        check(ManagedRepair.IsEnabled AndAlso ManagedProblem.IsVisible, "a supported vanilla candidate exposes repair and explains shared data ownership")
        Dim actions As New List(Of String)
        _managementOpenFolder = Sub(path) actions.Add(path)
        Dim mods = ManagedFolderActions.Children.OfType(Of PclButton)().Single(Function(button) CStr(button.Tag) = "mods")
        OpenManagedFolder(mods, Nothing)
        Await WaitPackUiAsync(Function() _managementFolderRequest Is Nothing)
        check(actions.SequenceEqual({Path.Combine(root, "mods")}), "shared Mods shortcuts resolve through the instance runtime directory")
        Dim resources = ManagedFolderActions.Children.OfType(Of PclButton)().Single(Function(button) CStr(button.Tag) = "resourcepacks")
        OpenManagedFolder(resources, Nothing)
        Await WaitPackUiAsync(Function() _managementFolderRequest Is Nothing)
        check(Not resources.IsEnabled AndAlso actions.Count = 1 AndAlso Not IO.Directory.Exists(Path.Combine(root, "resourcepacks")),
            "opening a missing content folder neither creates it nor launches another location")
        _managementOpenFolder = Sub(path)
                                    Throw New IOException("controlled management shell failure")
                                End Sub
        OpenManagedFolder(mods, Nothing)
        Await WaitPackUiAsync(Function() _managementFolderRequest Is Nothing)
        check(ManagedFolderStatus.Text.Contains("controlled management shell failure") AndAlso _managedInstance.Name = plain.Name, "folder launch failure is reported in its shortcut card without changing the selected instance")
        BackFromManagement(Nothing, Nothing) : layout() : SelectManagedInstance(fabric) : layout()
        check(Not ManagedRepair.IsEnabled AndAlso ManagedRunPath.Text = fabric.InstancePath AndAlso ManagedIsolation.Text.Contains("隔离"),
            "modded instance detail preserves isolation and cannot offer vanilla repair")
        _managementOpenFolder = Sub(path) actions.Add(path)
        OpenManagedFolder(mods, Nothing)
        Await WaitPackUiAsync(Function() _managementFolderRequest Is Nothing)
        check(actions.Last() = Path.Combine(fabric.InstancePath, "mods"), "isolated Mods shortcuts never open a different instance's shared Mods")
        check(NavigateTitleBack() AndAlso ManagementListPage.IsVisible, "title back restores the installed list instead of a hidden download subpage")
        SelectManagedInstance(_managementInstances.Single(Function(item) item.Problem IsNot Nothing)) : layout()
        check(ManagedProblem.IsVisible AndAlso Not ManagedRepair.IsEnabled AndAlso ManagedFolderActions.Children.OfType(Of PclButton)().All(Function(button) Not button.IsEnabled),
            "a damaged instance remains inspectable while uncertain mutation and folder actions stay unavailable")
        BackFromManagement(Nothing, Nothing)

        For Each folder In folders.Skip(1).Take(2)
            ManagementFolders.SelectedItem = folder : Await ScanManagementAsync(False) : layout()
            check(_selectedFolder Is folder AndAlso _managementInstances.Count = 3 AndAlso _managementInstances.All(Function(item) item.Target.Kind = folder.LauncherKind.Value),
                "switching a shared physical root retains the " & folder.LauncherKind.Value.ToString() & " interpretation context")
        Next
        ManagementFolders.SelectedItem = folders(3) : Await ScanManagementAsync(False) : layout()
        check(_managementInstances.Count = 0 AndAlso ManagementStatus.Text.Contains("没有已安装") AndAlso Not IO.Directory.Exists(folders(3).Location),
            "an absent game directory renders empty without being created by browsing")
        ManagementFolders.SelectedItem = folders(0) : Await ScanManagementAsync(False) : layout()

        Dim gate As New TaskCompletionSource(Of List(Of InstalledInstance))(TaskCreationOptions.RunContinuationsAsynchronously)
        _managementProvider = Function(target, token) gate.Task
        Dim old = ScanManagementAsync(True)
        _managementProvider = Function(target, token) Task.FromResult(InstalledInstanceCatalog.Scan(target, token))
        Await ScanManagementAsync(True) : Dim accepted = _managementInstances
        gate.TrySetResult(New List(Of InstalledInstance)()) : Await old
        check(_managementInstances Is accepted AndAlso _managementInstances.Count = 3, "an older completed scan cannot replace the latest accepted installed list")
        gate = New TaskCompletionSource(Of List(Of InstalledInstance))(TaskCreationOptions.RunContinuationsAsynchronously)
        _managementProvider = Function(target, token) gate.Task : old = ScanManagementAsync(True)
        ShowSettings(Nothing, Nothing) : gate.TrySetResult(New List(Of InstalledInstance)()) : Await old : layout()
        check(SettingsPage.IsVisible AndAlso _managementInstances Is accepted AndAlso Not _managementScanning, "leaving management cancels its scan and ignores a provider that completes after cancellation")
        _managementProvider = Function(target, token) Task.FromResult(InstalledInstanceCatalog.Scan(target, token))
        ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(False) : layout()
        plain = _managementInstances.Single(Function(item) item.Name = "我的原版")
        SelectManagedInstance(plain) : layout()
        _busy = True : SetBusy(True)
        ManagementFolders.SelectedItem = folders(3) : layout()
        check(_selectedFolder Is folders(0) AndAlso ManagementFolders.SelectedItem Is folders(0) AndAlso Not ManagedRepair.IsEnabled AndAlso Not ManagementFolders.IsEnabled,
            "an active download permits reading management but rejects queued target switches and repair")
        _busy = False : SetBusy(False)

        _versions = New List(Of VersionEntry)()
        Dim versionGate As New TaskCompletionSource(Of List(Of VersionEntry))(TaskCreationOptions.RunContinuationsAsynchronously)
        _versionProvider = Function(policy, token) versionGate.Task
        Dim preparing = PrepareManagedRepairAsync()
        check(_managementPreparing AndAlso Not ManagedRepair.IsEnabled AndAlso Not ManagementFolders.IsEnabled, "repair metadata preparation freezes repeated submissions and directory editing")
        BackFromManagement(Nothing, Nothing) : ShowSettings(Nothing, Nothing)
        versionGate.TrySetResult(New List(Of VersionEntry) From {New VersionEntry With {.Id = "1.20.1", .Kind = "release"}})
        Await preparing : layout()
        check(SettingsPage.IsVisible AndAlso _selectedEntry Is entryBefore AndAlso _managedRepairContext Is Nothing AndAlso Not _managementPreparing,
            "returning and changing pages invalidates an older repair preparation without opening the installer")
        _versions = New List(Of VersionEntry) From {entryBefore}
        _versionProvider = Nothing
        ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(False) : layout()
        plain = _managementInstances.Single(Function(item) item.Name = "我的原版")
        SelectManagedInstance(plain) : layout() : Await PrepareManagedRepairAsync() : layout()
        check(DownloadPage.IsVisible AndAlso _downloadView = DownloadView.Install AndAlso RepairBox.IsChecked.GetValueOrDefault() AndAlso InstanceName.Text = plain.Name AndAlso _componentSelection.Entries.Count = 0,
            "supported vanilla repair hands off to the existing installer with the exact installed name and no loader selection")
        check(_managedRepairContext Is plain AndAlso _selectedEntry.Id = plain.MinecraftVersion AndAlso SameManagementTarget(_managedRepairContext.Target, CurrentInstallTarget()),
            "repair handoff binds the scanned instance and native launcher target")
        AcceptComponentList(ComponentKind.Fabric, _selectedEntry, _componentGeneration, _componentEntries(ComponentKind.Fabric))
        check(_nameEdited AndAlso InstanceName.Text = plain.Name AndAlso _managedRepairContext Is plain,
            "late component metadata preserves the installed custom name selected for managed repair")
        Dim gameConfig = Path.Combine(plain.InstancePath, plain.Name & ".json"), originalBytes = File.ReadAllBytes(gameConfig)
        File.WriteAllText(gameConfig, File.ReadAllText(gameConfig) & " ")
        Try
            Dim failed = False
            Try
                InstalledInstanceCatalog.EnsureUnchanged(_managedRepairContext, CancellationToken.None)
            Catch ex As InvalidDataException
                failed = True
            End Try
            check(failed, "a changed instance JSON invalidates a previously prepared repair context")
        Finally
            File.WriteAllBytes(gameConfig, originalBytes)
        End Try
        Dim installGate As New TaskCompletionSource(Of InstallResult)(TaskCreationOptions.RunContinuationsAsynchronously), calls = 0
        Dim frozenRoot As String = Nothing, frozenName As String = Nothing
        _installRunner = Function(version, destination, name, components, mode, strategy, token)
                             calls += 1 : frozenRoot = destination : frozenName = name
                             Return installGate.Task
                         End Function
        StartInstall(Nothing, Nothing) : Await WaitPackUiAsync(Function() _busy) : layout()
        check(calls = 1 AndAlso _taskInstallTarget.Root = root AndAlso frozenRoot = root AndAlso frozenName = plain.Name AndAlso _managementTaskOrigin Is plain,
            "managed repair uses the shared task pipeline with its frozen native target and installed name")
        check(DownloadProgress.IsVisible AndAlso _downloadView = DownloadView.Progress AndAlso Not GameFolderCard.IsEnabled,
            "managed repair exposes the existing progress page while freezing destination edits")
        ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(False) : layout()
        BackFromManagement(Nothing, Nothing) : layout()
        SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = "Fabric 实例")) : layout()
        check(_busy AndAlso _managedInstance.Name = "Fabric 实例" AndAlso _managementTaskOrigin Is plain,
            "browsing another installed detail during repair does not change the task's return identity")
        ShowProgress(Nothing, Nothing) : layout()
        CancelInstall(Nothing, Nothing) : installGate.TrySetCanceled()
        Await WaitPackUiAsync(Function() Not _busy) : layout()
        ResumeInstallation(Nothing, Nothing) : Await ScanManagementAsync(False) : layout()
        check(ManagementPage.IsVisible AndAlso ManagementDetailPage.IsVisible AndAlso _managedInstance.Name = plain.Name AndAlso _taskOutcome = TaskOutcome.Cancelled,
            "returning from a cancelled managed repair restores that instance detail without cancelling navigation")
        Await PrepareManagedRepairAsync() : layout()
        _installRunner = Function(version, destination, name, components, mode, strategy, token)
                             calls += 1
                             Return Task.FromResult(New InstallResult With {.InstancePath = plain.InstancePath, .FileCount = 1, .Reused = 1})
                         End Function
        StartInstall(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _busy) : layout()
        check(calls = 2 AndAlso _taskOutcome = TaskOutcome.Succeeded AndAlso _taskMinecraft = plain.MinecraftVersion,
            "a cancelled managed repair can retry through the same existing task pipeline")
        ResumeInstallation(Nothing, Nothing) : Await ScanManagementAsync(False) : layout()
        check(ManagementPage.IsVisible AndAlso ManagementDetailPage.IsVisible AndAlso _managedInstance.Name = plain.Name AndAlso ManagedRepair.IsEnabled,
            "successful managed repair returns to a refreshed detail with another repair available")
        ShowProgress(Nothing, Nothing) : layout()
        Dim returnGate As New TaskCompletionSource(Of List(Of InstalledInstance))(TaskCreationOptions.RunContinuationsAsynchronously)
        _managementProvider = Function(target, token) returnGate.Task
        _managementDirty = True
        ResumeInstallation(Nothing, Nothing)
        Await WaitPackUiAsync(Function() _managementScanning)
        Dim returnRequest = _managementRequest
        _managementProvider = Function(target, token) Task.FromResult(InstalledInstanceCatalog.Scan(target, token))
        ManagementFolders.SelectedItem = folders(1) : Await ScanManagementAsync(False) : layout()
        returnGate.TrySetResult(New List(Of InstalledInstance)())
        Await WaitPackUiAsync(Function() ManagementRequestReleasedForChecks(returnRequest))
        Await Task.Yield() : layout()
        check(_selectedFolder Is folders(1) AndAlso ManagementListPage.IsVisible AndAlso Not ManagementDetailPage.IsVisible AndAlso _managementReturnName Is Nothing,
            "a late task-return scan cannot select a same-named instance after changing the launcher context")
        ManagementFolders.SelectedItem = folders(0) : Await ScanManagementAsync(False) : layout()
        Dim completedGeneration = _taskGeneration
        StartInstall(Nothing, Nothing) : layout()
        check(Not _busy AndAlso calls = 2 AndAlso _taskGeneration = completedGeneration,
            "a stale download submission cannot start a repair behind the visible management page")
        ResetTaskView("新的独立任务", False)
        check(_managementTaskOrigin Is Nothing, "a new independent task clears a previous managed-repair return destination")
        _installRunner = Nothing
        _managedRepairContext = Nothing
        ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(False) : layout()
        SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = "Fabric 实例")) : layout()
        For Each dark In {False, True}
            RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", dark}})
            host.Width = 810 : host.Height = 470 : layout()
            check(ManagedRunPath.ActualWidth > 350 AndAlso ManagementDetailPage.ScrollableHeight > 0 AndAlso ManagedFolderActions.ActualWidth > 350,
                "installed details and wrapped folder actions fit the minimum window in dark mode " & dark)
            SaveControlVisual(host, Path.Combine(directory, "management-detail-" & If(dark, "dark", "light") & ".png"))
        Next
        check(bytes.All(Function(pair) pair.Value.SequenceEqual(File.ReadAllBytes(pair.Key))),
            "management scans searches navigation and repair preparation preserve all external configuration and instance bytes")
        File.WriteAllText(Path.Combine(directory, "management-ui-evidence.json"), New JObject From {{"ExternalBytesPreserved", True}, {"RealGameLaunched", False}, {"RealRepairTransferred", False}}.ToString())
    End Function

    Private Shared Function ManagementRequestReleasedForChecks(request As CancellationTokenSource) As Boolean
        Try
            Dim token = request.Token
            Return False
        Catch ex As ObjectDisposedException
            Return True
        End Try
    End Function

    Private Sub VerifyManagementMotion(directory As String, check As Action(Of Boolean, String))
        Dim owner As New MainWindow(True), original = owner.Content
        Dim host As New Border With {.Width = 850, .Height = 700}, layout = ControlLayout(host)
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using source = PreviewSurface.Attach(host, 850, 700)
                layout() : Dim folders = owner.PrepareManagementFixture(directory)
                owner.ShowManagement(Nothing, Nothing) : layout()
                If Not PumpMotionUntil(Function() Not owner._managementScanning) Then Throw New TimeoutException("Management motion fixture did not load.")
                Dim instance = owner._managementInstances.Single(Function(item) item.Name = "我的原版")
                owner._motionPreview = True : owner._managementMotion.PauseForChecks = True : owner._surfaceMotion.PauseForChecks = True
                owner.SelectManagedInstance(instance) : layout()
                Dim entering = owner._managementMotion.Transition
                check(entering IsNot Nothing AndAlso owner.ManagementDetailPage.IsVisible AndAlso Not owner.ManagementViewHost.IsHitTestVisible AndAlso owner.ManagementGhost.Children.Count > 0,
                    "installed detail enters through PCL card motion with inert exit layers and input gating")
                Dim plan = DirectCast(entering.Timeline, ParallelTimeline)
                check(plan.Children.OfType(Of PclPageYAnimation)().First().BeginTime.Value.TotalMilliseconds = 140 AndAlso owner._managementMotion.ItemCount > 1,
                    "installed details retain PCL's 110 plus 30 millisecond handoff and staggered visible cards")
                PauseSelectionClock(entering) : entering.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(190), TimeSeekOrigin.BeginTime) : layout()
                check(owner.ManagementViewHost.IsHitTestVisible AndAlso owner.ManagementGhost.Children.Count = 0 AndAlso owner.ManagementDetailPage.IsVisible,
                    "detail content becomes interactive during its remaining card entry without stale exit images")
                SaveControlVisual(host, Path.Combine(directory, "management-detail-entry-190ms.png"))
                owner.BackFromManagement(Nothing, Nothing) : layout()
                Dim returning = owner._managementMotion.Transition
                check(returning IsNot Nothing AndAlso returning IsNot entering AndAlso owner.ManagementListPage.IsVisible AndAlso owner.ManagementGhost.Children.Count = 1,
                    "a rapid installed-detail return snapshots its interrupted composite and restores the list")
                entering.Controller.SkipToFill() : layout()
                check(owner._managementMotion.Transition Is returning AndAlso owner.ManagementListPage.IsVisible,
                    "obsolete detail-entry completion cannot override the newer list return")
                Dim selected = owner._managedInstance
                owner.SelectManagedInstance(owner._managementInstances.Single(Function(item) item.Name = "Fabric 实例"))
                check(owner._managedInstance Is selected AndAlso owner._managementMotion.Transition Is returning,
                    "gated stale installed-row actions cannot reopen a detail during list exit")
                owner._managementMotion.Finish() : layout()
                check(owner.ManagementViewHost.IsHitTestVisible AndAlso owner.ManagementGhost.Children.Count = 0 AndAlso owner._managementMotion.Transition Is Nothing,
                    "completed installed-list return releases its clocks overlays and input gate")
                owner.SelectManagedInstance(instance) : layout()
                Dim retired = owner._managementMotion.Transition
                owner.ShowSettings(Nothing, Nothing) : layout()
                check(owner.SettingsPage.IsVisible AndAlso owner._managementMotion.Transition Is Nothing AndAlso owner.ManagementGhost.Children.Count = 0,
                    "settings handoff retires active management card motion and detached exit layers")
                retired.Controller.SkipToFill() : layout()
                check(owner.SettingsPage.IsVisible AndAlso Not owner.ManagementPage.IsVisible, "retired management motion cannot reveal a hidden detail over settings")
                owner._surfaceMotion.Finish() : owner.ShowManagement(Nothing, Nothing) : layout()
                check(owner._surfaceMotion.Transition IsNot Nothing AndAlso owner.ManagementTab.Checked AndAlso Not owner.SettingsTab.Checked AndAlso Not owner.DownloadTab.Checked,
                    "returning from settings uses the shared PCL root motion and selects only the management tab")
                owner._surfaceMotion.Finish() : layout() : owner.BackFromManagement(Nothing, Nothing) : layout()
                host.Width = 810 : layout()
                check(owner._managementMotion.Transition Is Nothing AndAlso owner.ManagementViewHost.IsHitTestVisible AndAlso owner.ManagementGhost.Children.Count = 0,
                    "resizing interrupted management navigation settles the requested page and releases stale motion")
                owner.SelectManagedInstance(instance) : layout()
                owner._managementMotion.Finish() : layout()
                owner._managementProvider = Function(target, token) Task.FromResult(owner._managementInstances.Where(Function(item) item IsNot instance).ToList())
                Dim reload = owner.ScanManagementAsync(True)
                If Not PumpMotionUntil(Function() reload.IsCompleted) Then Throw New TimeoutException("Management deletion refresh did not settle.")
                reload.GetAwaiter().GetResult() : layout()
                check(owner.ManagementListPage.IsVisible AndAlso Not owner.ManagementDetailPage.IsVisible AndAlso owner.ManagementListPage.Opacity = 0 AndAlso owner._managementMotion.Transition IsNot Nothing,
                    "a refreshed-away instance enters the actual replacement list through the correct card motion owner")
                owner._managementMotion.Finish() : layout()
                owner._managementProvider = Function(target, token) Task.FromResult(InstalledInstanceCatalog.Scan(target, token))
                reload = owner.ScanManagementAsync(True)
                If Not PumpMotionUntil(Function() reload.IsCompleted) Then Throw New TimeoutException("Management reload did not settle.")
                reload.GetAwaiter().GetResult() : owner._managementMotion.Finish() : layout()
                instance = owner._managementInstances.Single(Function(item) item.Name = "我的原版")
                owner.SelectManagedInstance(instance) : owner._managementMotion.Finish() : layout()
                owner.ManagementFolders.SelectedItem = folders(3) : layout()
                check(owner.ManagementListPage.IsVisible AndAlso Not owner.ManagementDetailPage.IsVisible AndAlso owner._selectedFolder Is folders(3) AndAlso owner.ManagementListPage.Opacity = 0,
                    "switching directories from an installed detail animates the incoming list rather than its hidden former detail")
                owner._managementMotion.Finish() : layout()
                owner.ManagementFolders.SelectedItem = folders(0) : layout()
                owner._managementMotion.Finish() : layout()
                owner.SelectManagedInstance(owner._managementInstances.Single(Function(item) item.Name = "我的原版")) : layout()
                source.RootVisual = Nothing : FlushControlLayout()
                check(owner._managementMotion.Transition Is Nothing AndAlso owner._surfaceMotion.Transition Is Nothing AndAlso owner.ManagementGhost.Children.Count = 0 AndAlso owner.PageGhost.Children.Count = 0,
                    "unloading management removes all owned card and root clocks and exit layers")
            End Using
        Finally
            owner._closing = True : owner.CancelManagementRequest()
            owner._managementMotion.Finish() : owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner._pageSwitch.Finish()
            host.Child = Nothing : owner.Content = original
        End Try
    End Sub
End Class
