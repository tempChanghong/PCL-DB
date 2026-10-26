Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Automation.Peers
Imports System.Windows.Automation.Provider
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyResourceInstallationUi(directory As String, passed As List(Of String))
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     If Not ok Then Throw New InvalidOperationException(label)
                                                     passed.Add(label)
                                                 End Sub
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim host As New Border With {.Width = 810, .Height = 470}, layout = ControlLayout(host), focus = LocalModCheckGetNativeFocus()
        FocusManager.SetIsFocusScope(host, True)
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 810, 470)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckResourceInstallationUiAsync(directory, host, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 60000) Then Throw New TimeoutException("Resource installation UI did not settle.")
                work.GetAwaiter().GetResult()
            End Using
        Finally
            owner.CompleteOnlinePrompt(Nothing) : owner.StopOnlinePromptMotion() : owner.HideOnlinePrompt()
            owner._closing = True : owner.CancelResourceRequests() : owner.CancelLocalModRequest() : owner.CancelManagementRequest()
            owner._categoryMotion.Finish() : owner._surfaceMotion.Finish()
            host.Child = Nothing : owner.Content = original
            LocalModCheckSetNativeFocus(focus) : SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub
    Private Async Function CheckResourceInstallationUiAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim fixture = Path.Combine(directory, "resource-target-ui-" & Guid.NewGuid().ToString("N")), game = Path.Combine(fixture, "game"), work = Path.Combine(fixture, "work")
        IO.Directory.CreateDirectory(Path.Combine(work, "PCL")) : File.WriteAllText(Path.Combine(work, "PCL/Setup.ini"), "LaunchArgumentIndieV2:0")
        For Each name As String In {"Shared", "Isolated", "Old"}
            Dim instance = Path.Combine(game, "versions", name) : IO.Directory.CreateDirectory(instance)
            File.WriteAllText(Path.Combine(instance, name & ".json"), New JObject From {{"id", name}, {"clientVersion", If(name = "Old", "1.5.2", "1.20.1")}, {"mainClass", "net.minecraft.client.main.Main"}, {"type", "release"}, {"libraries", New JArray(New JObject From {{"name", "net.fabricmc:fabric-loader:0.16.0"}})}}.ToString())
            If name = "Isolated" Then IO.Directory.CreateDirectory(Path.Combine(instance, "PCL")) : File.WriteAllText(Path.Combine(instance, "PCL/Setup.ini"), "VersionArgumentIndieV2:True")
        Next
        For Each runPath In {game, Path.Combine(game, "versions/Isolated")}
            IO.Directory.CreateDirectory(Path.Combine(runPath, "saves", If(runPath = game, "SharedWorld", "IsolatedWorld")))
            File.WriteAllText(Path.Combine(runPath, "saves", If(runPath = game, "SharedWorld", "IsolatedWorld"), "level.dat"), "world fixture")
        Next
        _pclBase = work : _selectedFolder = New GameFolder With {.Location = game, .LauncherKind = ExternalLauncherKind.Pcl, .LauncherWorkspace = work, .LauncherBindingId = "resource-target-fixture", .Name = "Fixture"}
        _folders = New List(Of GameFolder) From {_selectedFolder} : FolderList.ItemsSource = _folders : FolderList.SelectedItem = _selectedFolder
        Dim target = CurrentInstallTarget(), instances = InstalledInstanceCatalog.Scan(target, CancellationToken.None), sharedInstance = instances.Single(Function(value) value.Name = "Shared"), isolated = instances.Single(Function(value) value.Name = "Isolated")
        _managementProvider = Function(binding, token) Task.FromResult(instances)
        _packCatalogFactory = Function() New ModpackCatalogClient(New CommunityFixtureHandler(), "")
        SelectResourceKind(CommunityResourceKind.Shader) : _categoryMotion.Finish() : layout()
        PackSearchSource.SelectedIndex = 2 : RunPackSearch(Nothing, Nothing)
        Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : _packResultMotion.Finish() : layout()
        Dim project = DirectCast(PackProjectRows.Children.OfType(Of PclResourceItem)().Single().Tag, ModpackProject)
        OpenPackDetails(project) : Await WaitPackUiAsync(Function() _packDetailCompletedProject Is project) : _categoryMotion.Finish() : layout()
        Dim row = DirectCast(DirectCast(PackVersionCards.Children(0), PclCard).Body, StackPanel).Children.OfType(Of PclListItem)().Single(), icon = row.Children.OfType(Of PclIconButton)().Single()
        Dim saves = 0 : _resourceSaveChooser = Function(release)
                                                 saves += 1 : Return Nothing
                                             End Function
        Dim peer = UIElementAutomationPeer.CreatePeerForElement(icon), invoke = DirectCast(peer.GetPattern(PatternInterface.Invoke), IInvokeProvider)
        Dim retainedLimit = SpeedLimitBox.Text, invalidGeneration = _taskGeneration
        SpeedLimitBox.Text = "invalid"
        InstallCommunityResource(_packReleases.Single(), icon) : layout()
        Dim openedInvalid = _resourceTargetPrompt
        If openedInvalid Then CompleteOnlinePrompt("no") : Await WaitPackUiAsync(Function() Not _choosingOnline)
        check(Not openedInvalid AndAlso SettingsPage.IsVisible AndAlso _taskGeneration = invalidGeneration, "invalid download limits navigate to their setting before opening an instance picker or creating a task")
        SpeedLimitBox.Text = retainedLimit : ShowDownload(Nothing, Nothing) : _surfaceMotion.Finish() : _categoryMotion.Finish() : layout()
        Await Task.Delay(1) : layout()
        invoke.Invoke() : Await WaitPackUiAsync(Function() _resourceTargetPrompt AndAlso Not _resourceTargetLoading) : layout()
        check(saves = 0 AndAlso Not _busy AndAlso PackNameOverlay.IsVisible AndAlso ResourceTargetChoices.IsVisible AndAlso Not OnlinePackName.IsVisible, "version-row install action opens a target picker without activating parent Save As")
        check(Not PageSurface.IsEnabled AndAlso Not TopTabs.IsEnabled AndAlso ResourceTargetInstance.Items.Count = 3 AndAlso ResourceTargetWorldRow.Visibility = Visibility.Collapsed, "instance picker retains PCL modal background input gates and hides world choices for shaders")
        File.WriteAllText(Path.Combine(directory, "resource-initial-focus.json"), New JObject From {{"Focus", TryCast(FocusManager.GetFocusedElement(host), FrameworkElement)?.Name}, {"Confirm", OnlinePackConfirm.IsEnabled}, {"Problem", _resourceTargetProblem}, {"Loading", _resourceTargetLoading}, {"WorldLoading", _resourceWorldLoading}, {"Kind", _resourceTargetRelease.Kind.ToString()}, {"Limit", SpeedLimitBox.Text}}.ToString())
        check(FocusManager.GetFocusedElement(host) Is ResourceTargetInstance AndAlso OnlinePackConfirm.IsEnabled, "loaded instance choices receive logical focus and enable only a valid target")
        check(PackNameDialog.TranslatePoint(New Point(0, PackNameDialog.ActualHeight), host).Y <= host.ActualHeight AndAlso PackNameDialog.TranslatePoint(New Point(0, 0), host).Y >= 0, "target dialog and confirmation fit the minimum 810 by 470 surface")
        ResourceTargetInstance.SelectedItem = ResourceTargetInstance.Items.OfType(Of ComboBoxItem)().Single(Function(value) value.Tag Is sharedInstance) : layout()
        check(ResourceTargetLocation.Text.Contains(Path.Combine(game, "shaderpacks")) AndAlso ResourceTargetLocation.Text.Contains("共享") AndAlso ResourceTargetLocation.Text.Contains("启用"), "shared shader target shows its real folder and game-side enabling hint")
        Dim configPath = Path.Combine(work, "PCL/Setup.ini"), config = File.ReadAllBytes(configPath), starts = 0, frozen As ResourceInstallationPlan = Nothing
        _resourceInstallRunner = Async Function(plan, cache, policy, progress, token)
                                     starts += 1 : frozen = plan : Await Task.Delay(1000, token)
                                 End Function
        ConfirmOnlinePackName(Nothing, Nothing) : Await WaitPackUiAsync(Function() _busy) : layout()
        check(frozen IsNot Nothing AndAlso frozen.InstanceName = "Shared" AndAlso frozen.Destination = Path.Combine(game, "shaderpacks", _packReleases.Single().FileName) AndAlso starts = 1 AndAlso Not _resourceTargetPrompt, "confirm freezes resource, native launcher and selected destination before task transition")
        Dim generation = _taskGeneration
        Try
            invoke.Invoke()
        Catch ex As System.Windows.Automation.ElementNotEnabledException
        End Try
        InstallCommunityResource(_packReleases.Single(), icon) : layout()
        check(starts = 1 AndAlso generation = _taskGeneration, "retired detail install action cannot start another task while downloading")
        CancelInstall(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _busy AndAlso Not _choosingOnline) : layout()
        check(_taskOutcome = TaskOutcome.Cancelled AndAlso _managementDirty AndAlso Not File.Exists(frozen.Destination) AndAlso File.ReadAllBytes(configPath).SequenceEqual(config), "cancelled install invalidates management conservatively while preserving config and destination")
        Await ResumeResourceTaskForChecksAsync() : _packResultMotion.Finish() : layout()
        check(_packProject Is project AndAlso _packMode = PackBrowserMode.Detail AndAlso DownloadSidebar.Visibility = Visibility.Collapsed, "cancelled resource task returns to its exact PCL detail page")
        _managedInstance = sharedInstance : _localModInstance = sharedInstance : _localModReadSucceeded = True
        _resourceInstallRunner = Async Function(plan, cache, policy, progress, token)
                                     starts += 1 : frozen = plan : Await Task.Delay(1, token)
                                 End Function
        ' Use the current rendered row after task return; old detached rows must not be revived.
        row = DirectCast(DirectCast(PackVersionCards.Children(0), PclCard).Body, StackPanel).Children.OfType(Of PclListItem)().Single() : icon = row.Children.OfType(Of PclIconButton)().Single()
        InstallCommunityResource(_packReleases.Single(), icon) : Await WaitPackUiAsync(Function() _resourceTargetPrompt AndAlso Not _resourceTargetLoading)
        ConfirmOnlinePackName(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _busy AndAlso Not _choosingOnline) : layout()
        check(starts = 2 AndAlso _taskOutcome = TaskOutcome.Succeeded AndAlso Not _localModReadSucceeded AndAlso _managementDirty AndAlso _taskInstancePath Is Nothing, "successful shared-directory install invalidates local management without inventing an instance installation")
        Await ResumeResourceTaskForChecksAsync() : layout()
        _managementProvider = Function(binding, token) Task.FromResult(InstalledInstanceCatalog.Scan(binding, token))
        _managementInstances = instances : _managementTarget = target : _managedInstance = sharedInstance
        ManagementListPage.Visibility = Visibility.Collapsed : ManagementDetailPage.Visibility = Visibility.Collapsed : LocalModsPage.Visibility = Visibility.Visible
        LocalModJarForChecks(Path.Combine(game, "mods/new-resource.jar"), "new_resource")
        ShowManagement(Nothing, Nothing) : _surfaceMotion.Finish() : _categoryMotion.Finish()
        Await WaitPackUiAsync(Function() Not _managementDirty AndAlso Not _managementScanning AndAlso _localModReadSucceeded AndAlso Not _localModScanning) : layout()
        check(_localMods.Single().ModIds.Contains("new_resource") AndAlso _localModInstance.Name = "Shared" AndAlso SameManagementTarget(_localModInstance.Target, target), "returning to local management rescans the real shared Mods directory and shows the newly added file")
        Dim oldMods As New TaskCompletionSource(Of List(Of LocalModEntry))()
        _localModProvider = Function(instance, token) oldMods.Task
        Dim retiredRead = LoadLocalModsAsync(True)
        InvalidateResourceManagement(frozen) : oldMods.SetResult(New List(Of LocalModEntry)()) : Await retiredRead : layout()
        check(_managementDirty AndAlso Not _localModReadSucceeded AndAlso Not _localModScanning AndAlso _localMods.Count = 1, "a late local Mod read cannot restore stale success or replace rows after a resource invalidates its shared directory")
        _localModProvider = Function(instance, token) Task.FromResult(LocalModCatalog.Scan(instance, token))
        ShowManagement(Nothing, Nothing) : _surfaceMotion.Finish() : _categoryMotion.Finish()
        Await WaitPackUiAsync(Function() Not _managementDirty AndAlso Not _managementScanning AndAlso _localModReadSucceeded AndAlso Not _localModScanning) : layout()
        check(_localMods.Single().ModIds.Contains("new_resource") AndAlso File.ReadAllBytes(configPath).SequenceEqual(config), "management retries its invalidated shared Mod read without changing external configuration")
        ShowDownload(Nothing, Nothing) : _surfaceMotion.Finish() : _categoryMotion.Finish() : layout()
        _managementProvider = Function(binding, token) Task.FromResult(instances)
        Dim data As New ModpackRelease With {.Kind = CommunityResourceKind.DataPack, .Name = "数据包", .FileName = "data.zip", .GameVersions = {"1.20.1"}, .Loaders = {"datapack"}, .Size = 100, .Sha1 = New String("a"c, 40)}
        Dim prompt = PromptResourceTargetAsync(data, target, icon)
        Await WaitPackUiAsync(Function() Not _resourceTargetLoading AndAlso Not _resourceWorldLoading) : layout()
        check(ResourceTargetWorldRow.IsVisible AndAlso ResourceTargetWorld.Items.Count = 1 AndAlso ResourceTargetWorld.SelectedIndex = -1 AndAlso Not OnlinePackConfirm.IsEnabled, "datapack picker never chooses a world implicitly")
        check(ResourceTargetInstance.Items.OfType(Of ComboBoxItem)().Single(Function(value) DirectCast(value.Tag, InstalledInstance).Name = "Old").IsEnabled = False, "incompatible Minecraft instances remain visible with disabled selection")
        ResourceTargetWorld.SelectedIndex = 0 : layout()
        check(OnlinePackConfirm.IsEnabled AndAlso ResourceTargetLocation.Text.Contains("saves" & Path.DirectorySeparatorChar & "SharedWorld" & Path.DirectorySeparatorChar & "datapacks"), "explicit world selection shows its datapacks folder and permits installation")
        ResourceTargetInstance.SelectedItem = ResourceTargetInstance.Items.OfType(Of ComboBoxItem)().Single(Function(value) value.Tag Is isolated)
        Await WaitPackUiAsync(Function() Not _resourceWorldLoading) : layout()
        check(ResourceTargetWorld.Items.Count = 1 AndAlso DirectCast(DirectCast(ResourceTargetWorld.Items(0), ComboBoxItem).Tag, InstalledWorld).Name = "IsolatedWorld" AndAlso ResourceTargetWorld.SelectedIndex = -1 AndAlso Not OnlinePackConfirm.IsEnabled, "changing isolation target discards the previous selected world")
        ' Rapid changes must leave only the last world's directory and a fresh explicit selection.
        For index = 1 To 4
            ResourceTargetInstance.SelectedItem = ResourceTargetInstance.Items.OfType(Of ComboBoxItem)().Single(Function(value) value.Tag Is sharedInstance)
            ResourceTargetInstance.SelectedItem = ResourceTargetInstance.Items.OfType(Of ComboBoxItem)().Single(Function(value) value.Tag Is isolated)
        Next
        Await WaitPackUiAsync(Function() Not _resourceWorldLoading) : layout()
        check(ResourceTargetWorld.Items.Count = 1 AndAlso DirectCast(DirectCast(ResourceTargetWorld.Items(0), ComboBoxItem).Tag, InstalledWorld).Name = "IsolatedWorld" AndAlso Not OnlinePackConfirm.IsEnabled, "retired world reads cannot populate a rapidly replaced instance selection")
        ResourceTargetWorld.SelectedIndex = 0 : layout()
        For Each dark In {False, True}
            RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", dark}}) : layout()
            check(PackNameDialog.ActualHeight <= host.ActualHeight - 40 AndAlso OnlinePackConfirm.IsVisible AndAlso ResourceTargetWorld.ActualHeight = 30, "datapack target selector remains fully visible in minimum surface light/dark mode: " & dark)
            SaveControlVisual(host, Path.Combine(directory, "resource-target-" & If(dark, "dark", "light") & ".png"))
        Next
        Dim description = PackNameDescription.Text
        PackNameDescription.Text = String.Concat(Enumerable.Repeat("较长的资源发布版本名称 ", 18))
        _resourceTargetProblem = "无法读取实例配置：" & String.Concat(Enumerable.Repeat("较长目录\\", 25)) & "PCL\\Setup.ini"
        ValidateResourceTarget() : ResourceTargetLocation.Text = String.Concat(Enumerable.Repeat("D:\\较长资源目录\\", 30)) : layout()
        Dim buttonBottom = OnlinePackConfirm.TranslatePoint(New Point(0, OnlinePackConfirm.ActualHeight), host).Y
        File.WriteAllText(Path.Combine(directory, "resource-target-overflow.json"), New JObject From {{"HostHeight", host.ActualHeight}, {"DialogHeight", PackNameDialog.ActualHeight}, {"ConfirmBottom", buttonBottom}}.ToString())
        SaveControlVisual(host, Path.Combine(directory, "resource-target-long.png"))
        check(buttonBottom <= host.ActualHeight - 20 AndAlso PackNameDialog.TranslatePoint(New Point(0, 0), host).Y >= 20, "long resource names, target paths and read errors keep dialog actions inside the minimum window")
        check(OnlinePromptBody.ScrollableHeight > 100 AndAlso Not OnlinePackConfirm.IsEnabled, "long target errors remain scrollable while installation stays blocked")
        OnlinePromptBody.ScrollToEnd() : layout()
        check(OnlinePromptBody.VerticalOffset > 0 AndAlso OnlinePackCancel.TranslatePoint(New Point(0, OnlinePackCancel.ActualHeight), host).Y <= host.ActualHeight - 20, "scrolling target details and errors never moves the cancel action out of view")
        PackNameDescription.Text = description : _resourceTargetProblem = Nothing : ValidateResourceTarget() : layout()
        CompleteOnlinePrompt("no") : check(Await prompt Is Nothing, "cancel target selection returns no installation plan")
        Dim late As New TaskCompletionSource(Of List(Of InstalledInstance))()
        _managementProvider = Function(binding, token) late.Task
        prompt = PromptResourceTargetAsync(data, target, icon) : CompleteOnlinePrompt("no") : Await prompt
        _managementProvider = Function(binding, token) Task.FromResult(New List(Of InstalledInstance) From {isolated})
        Dim replacement = PromptResourceTargetAsync(data, target, icon)
        late.SetResult(instances) : Await WaitPackUiAsync(Function() Not _resourceTargetLoading AndAlso Not _resourceWorldLoading) : layout()
        check(OnlinePromptBody.VerticalOffset = 0 AndAlso OnlinePromptBody.MotionClock Is Nothing, "replacement target dialogs reset body scrolling and discard previous scroll animations")
        check(ResourceTargetInstance.Items.Count = 1 AndAlso DirectCast(DirectCast(ResourceTargetInstance.Items(0), ComboBoxItem).Tag, InstalledInstance) Is isolated, "a cancelled target scan completing late cannot replace a newer picker")
        CompleteOnlinePrompt("no") : Await replacement
        _motionPreview = True
        prompt = PromptResourceTargetAsync(data, target, icon) : Dim entering = _onlinePromptMotion
        check(entering IsNot Nothing AndAlso _onlinePromptOffset.Y = 40 AndAlso _onlinePromptRotation.Angle = -4, "resource target picker uses the original PCL dialog entry motion")
        CompleteOnlinePrompt("no") : Dim exiting = _onlinePromptMotion
        entering.Controller.SkipToFill() : layout()
        check(exiting IsNot entering AndAlso _onlinePromptMotion Is exiting AndAlso Not PageSurface.IsEnabled, "retired picker entrance cannot release its newer exit input gate")
        exiting.Controller.SkipToFill() : Await WaitPackUiAsync(Function() _onlinePromptMotion Is Nothing) : Await prompt : layout()
        check(Not PackNameOverlay.IsVisible AndAlso PageSurface.IsEnabled AndAlso TopTabs.IsEnabled, "picker exit releases only its own modal gates")
        _motionPreview = False
        Dim ordinary = PromptOnlinePackAsync("版本名称", "原有整合包命名", target.Root, "untouched", False, CancellationToken.None, icon) : layout()
        check(OnlinePackName.IsVisible AndAlso ResourceTargetChoices.Visibility = Visibility.Collapsed AndAlso OnlinePackName.Text = "untouched", "resource target mode does not leak into existing modpack name dialogs")
        CompleteOnlinePrompt(Nothing) : Await ordinary
        Dim delayed As New TaskCompletionSource(Of List(Of InstalledInstance))()
        _managementProvider = Function(binding, token) delayed.Task
        Dim focusPrompt = PromptResourceTargetAsync(_packReleases.Single(), target, icon)
        PackNameDescription.Text = String.Concat(Enumerable.Repeat("读取目录期间可以继续查看说明 ", 45)) : layout()
        OnlinePromptBody.ScrollToVerticalOffset(100) : layout()
        FocusManager.SetFocusedElement(host, OnlinePackCancel)
        Dim retainedOffset = OnlinePromptBody.VerticalOffset
        delayed.SetResult(instances)
        Await WaitPackUiAsync(Function() Not _resourceTargetLoading) : layout()
        check(FocusManager.GetFocusedElement(host) Is OnlinePackCancel AndAlso Math.Abs(OnlinePromptBody.VerticalOffset - retainedOffset) < 0.5, "late target scans preserve the user's Cancel focus and body reading position")
        CompleteOnlinePrompt("no") : Await focusPrompt
        _managementProvider = Function(binding, token) Task.FromResult(InstalledInstanceCatalog.Scan(binding, token))
        Dim refreshPrompt = PromptResourceTargetAsync(data, target, icon)
        Await WaitPackUiAsync(Function() Not _resourceTargetLoading AndAlso Not _resourceWorldLoading)
        ResourceTargetInstance.SelectedItem = ResourceTargetInstance.Items.OfType(Of ComboBoxItem)().Single(Function(value) DirectCast(value.Tag, InstalledInstance).Name = "Isolated")
        Await WaitPackUiAsync(Function() Not _resourceWorldLoading)
        ResourceTargetWorld.SelectedIndex = 0
        Dim staleWorld = DirectCast(DirectCast(ResourceTargetWorld.SelectedItem, ComboBoxItem).Tag, InstalledWorld), taskBeforeRefresh = _taskGeneration
        File.WriteAllText(Path.Combine(staleWorld.Folder, "level.dat"), "changed while picker stayed open")
        ConfirmOnlinePackName(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _resourceTargetValidating) : layout()
        check(_resourceTargetPrompt AndAlso _confirmedResourcePlan Is Nothing AndAlso OnlinePackNameHint.IsVisible AndAlso ResourceTargetRefresh.IsEnabled AndAlso _taskGeneration = taskBeforeRefresh, "changed world confirmation remains in the picker with an actionable refresh and no installation task")
        RefreshResourceTargets(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _resourceTargetLoading AndAlso Not _resourceWorldLoading) : layout()
        check(DirectCast(DirectCast(ResourceTargetInstance.SelectedItem, ComboBoxItem).Tag, InstalledInstance).Name = "Isolated" AndAlso ResourceTargetWorld.SelectedIndex = -1 AndAlso Not OnlinePackConfirm.IsEnabled, "refresh retains the explicit instance while requiring a new world selection")
        ResourceTargetWorld.SelectedIndex = 0 : ConfirmOnlinePackName(Nothing, Nothing)
        Dim refreshed = Await refreshPrompt
        check(refreshed IsNot Nothing AndAlso refreshed.InstanceName = "Isolated" AndAlso refreshed.WorldName = staleWorld.Name AndAlso File.ReadAllText(Path.Combine(staleWorld.Folder, "level.dat")) = "changed while picker stayed open", "refreshed world confirmation binds the new marker without modifying its contents")
        Dim otherWork = Path.Combine(fixture, "other-hmcl"), otherConfig = Path.Combine(otherWork, "hmcl.json")
        IO.Directory.CreateDirectory(otherWork)
        File.WriteAllText(otherConfig, New JObject From {{"configurations", New JObject From {{"same-root", New JObject From {{"gameDir", target.Root}, {"global", New JObject From {{"gameDirType", 0}, {"unknown", "keep"}}}}}}}}.ToString())
        Dim otherTarget As New LauncherInstallTarget(New GameFolder With {.Location = target.Root, .LauncherKind = ExternalLauncherKind.Hmcl, .LauncherBindingId = "other-hmcl", .LauncherWorkspace = otherWork, .LauncherConfigPath = otherConfig, .LauncherSharedConfigPath = Path.Combine(otherWork, "absent.json")}, otherWork)
        Dim otherInstance = InstalledInstanceCatalog.Read(otherTarget, "Shared", CancellationToken.None), otherBytes = File.ReadAllBytes(otherConfig)
        _managementProvider = Function(binding, token) Task.FromResult(New List(Of InstalledInstance) From {otherInstance, sharedInstance})
        Dim bindingPrompt = PromptResourceTargetAsync(_packReleases.Single(), target, icon)
        Await WaitPackUiAsync(Function() Not _resourceTargetLoading) : layout()
        check(ResourceTargetInstance.Items.Count = 1 AndAlso DirectCast(DirectCast(ResourceTargetInstance.SelectedItem, ComboBoxItem).Tag, InstalledInstance).Target.Kind = ExternalLauncherKind.Pcl AndAlso File.ReadAllBytes(otherConfig).SequenceEqual(otherBytes), "a same-root HMCL response cannot enter a PCL-bound instance picker or change external configuration")
        CompleteOnlinePrompt("no") : Await bindingPrompt
        _managementProvider = Function(binding, token) Task.FromResult(InstalledInstanceCatalog.Scan(binding, token))
        Await CheckResourceRefreshBoundariesAsync(directory, target, icon, layout, check)
        Await CheckResourceWorkflowAsync(directory, fixture, target, configPath, config, host, layout, check)
    End Function
End Class
