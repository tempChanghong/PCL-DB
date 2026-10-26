Imports System.ComponentModel
Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Shared Function LocalModJarForChecks(full As String, id As String) As String
        IO.Directory.CreateDirectory(Path.GetDirectoryName(full))
        Using stream As New FileStream(full, FileMode.Create, FileAccess.Write), zip As New ZipArchive(stream, ZipArchiveMode.Create)
            Using output = zip.CreateEntry("fabric.mod.json").Open(), writer As New StreamWriter(output, New UTF8Encoding(False))
                writer.Write(New JObject From {{"schemaVersion", 1}, {"id", id}, {"version", "1.2.3"}, {"name", "测试 Mod · " & id}, {"description", "本地 Mod 夹具"}}.ToString())
            End Using
        End Using
        Return full
    End Function
    Private Function PrepareLocalModFixture(directory As String) As List(Of GameFolder)
        Dim folders = PrepareManagementFixture(directory)
        Dim mods = Path.Combine(folders(0).Location, "versions/Fabric 实例/mods")
        LocalModJarForChecks(Path.Combine(mods, "first.jar"), "enabled_fixture")
        LocalModJarForChecks(Path.Combine(mods, "second.jar.disabled"), "disabled_fixture")
        Using stream As New FileStream(Path.Combine(mods, "unknown.jar"), FileMode.Create), zip As New ZipArchive(stream, ZipArchiveMode.Create)
            Using writer As New StreamWriter(zip.CreateEntry("README").Open())
                writer.Write("unknown archive")
            End Using
        End Using
        File.WriteAllText(Path.Combine(mods, "broken.jar"), "broken archive")
        ' All stages in these deterministic row/clock fixtures complete inline.
        ' Async native reads and navigation are exercised by dedicated fixtures.
        _localModInstanceReader = Function(instance, token) Task.FromResult(InstalledInstanceCatalog.Read(instance.Target, instance.Name, token))
        _localModProvider = Function(instance, token) Task.FromResult(LocalModCatalog.Scan(instance, token))
        _localModImporter = Function(instance, paths, token) Task.FromResult(LocalModActions.ImportFiles(instance, paths, token))
        _localModToggle = Function(instance, entry, enabled, token) Task.FromResult(LocalModActions.SetEnabled(instance, entry, enabled, token))
        Return folders
    End Function
    Private Sub VerifyLocalModUi(directory As String, check As Action(Of Boolean, String))
        Dim owner As New MainWindow(True), original = owner.Content
        Dim host As New Border With {.Width = 850, .Height = 700}, layout = ControlLayout(host)
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using source = PreviewSurface.Attach(host, 850, 700)
                layout()
                Dim work = owner.VerifyLocalModWorkflowAsync(directory, host, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 25000) Then Throw New TimeoutException("Local Mod workflow did not settle.")
                work.GetAwaiter().GetResult()
            End Using
        Finally
            owner._closing = True : owner.CancelLocalModRequest() : owner._localModWriteRequest?.Cancel()
            owner._managementMotion.Finish() : owner._surfaceMotion.Finish()
            host.Child = Nothing : owner.Content = original
        End Try
    End Sub

    Private Async Function VerifyLocalModWorkflowAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim folders = PrepareLocalModFixture(directory), game = folders(0).Location
        Dim protectedBytes = IO.Directory.GetFiles(Path.GetDirectoryName(game), "*", SearchOption.AllDirectories).
            Where(Function(full) Not full.Contains(Path.DirectorySeparatorChar & "mods" & Path.DirectorySeparatorChar)).
            ToDictionary(Function(full) full, Function(full) File.ReadAllBytes(full))
        ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(False) : layout()
        Dim instance = _managementInstances.Single(Function(item) item.Name = "Fabric 实例")
        SelectManagedInstance(instance) : layout() : ShowLocalMods(Nothing, Nothing) : Await LoadLocalModsAsync(False) : layout()
        check(LocalModsPage.IsVisible AndAlso Not ManagementDetailPage.IsVisible AndAlso Not ManagementToolbar.IsVisible AndAlso ManagementTab.Checked AndAlso InnerTitle.Text.Contains("Mod"),
            "local Mods are a title-back management child page for the selected installed instance")
        check(_localModInstance Is _managedInstance AndAlso SameLocalModContext(instance) AndAlso _localMods.Count = 4 AndAlso LocalModRows.Children.Count = 4 AndAlso LocalModRows.Children.Cast(Of FrameworkElement)().All(Function(row) row.Height = 44),
            "native local Mod rows preserve all healthy unknown broken and disabled files at PCL row height")
        instance = _managedInstance
        check(LocalModDirectory.Text = Path.Combine(instance.RunDirectory, "mods") AndAlso Not LocalModSharedHint.IsVisible,
            "an explicitly isolated instance displays its actual private Mods directory")
        check(_localMods.Any(Function(entry) Not entry.Recognized AndAlso entry.Problem Is Nothing) AndAlso _localMods.Any(Function(entry) entry.Problem IsNot Nothing AndAlso entry.CanToggle),
            "unknown and invalid archives stay visible without fabricated compatibility or lost disable access")
        Dim rowViews = LocalModRows.Children.Cast(Of Grid)().ToDictionary(Function(row) DirectCast(row.Tag, LocalModEntry).FilePath, Function(row) row)
        FilterLocalMods(LocalModDisabled, New RoutedEventArgs()) : layout()
        check(LocalModRows.Children.Count = 1 AndAlso _localModFilter = "disabled" AndAlso LocalModDisabled.Checked,
            "PCL local enabled-state filtering shows disabled files only")
        FilterLocalMods(LocalModEnabled, New RoutedEventArgs()) : layout()
        check(LocalModRows.Children.Count = 3 AndAlso LocalModEnabled.Checked, "enabled filter includes unknown and broken enabled archives")
        FilterLocalMods(LocalModAll, New RoutedEventArgs())
        check(LocalModRows.Children.Cast(Of Grid)().All(Function(row) rowViews(DirectCast(row.Tag, LocalModEntry).FilePath) Is row),
            "enabled-state filtering restores existing row controls instead of replaying every row's entry or losing focus ownership")
        LocalModSearch.Text = "enabled_fixture" : layout()
        check(LocalModRows.Children.Count = 1 AndAlso _localMods.Count = 4, "local search matches the manifest ID without discarding the accepted catalogue")
        LocalModSearch.Text = "no-matching-local-mod" : layout()
        check(LocalModRows.Children.Count = 0 AndAlso LocalModEmpty.IsVisible AndAlso LocalModEmpty.Text.Contains("没有找到"), "local Mod search has a distinct no-results presentation")
        LocalModSearch.Text = "" : layout()
        Dim first = _localMods.Single(Function(entry) entry.FileName = "first.jar"), firstBytes = File.ReadAllBytes(first.FilePath)
        Await ToggleLocalModAsync(first) : layout()
        check(File.Exists(first.FilePath & ".disabled") AndAlso Not File.Exists(first.FilePath) AndAlso _localMods.Any(Function(entry) entry.FileName = "first.jar.disabled" AndAlso Not entry.Enabled),
            "a UI disable action runs the real rename transaction and refreshes the actual file state")
        check(File.ReadAllBytes(first.FilePath & ".disabled").SequenceEqual(firstBytes), "UI disable preserves the archive bytes")
        Dim disabled = _localMods.Single(Function(entry) entry.FileName = "first.jar.disabled")
        Await ToggleLocalModAsync(disabled) : layout()
        check(File.Exists(first.FilePath) AndAlso _localMods.Any(Function(entry) entry.FileName = "first.jar" AndAlso entry.Enabled), "UI enable returns the same Mod to its original native directory")
        Dim imported = LocalModJarForChecks(Path.Combine(directory, "local-mod-ui-import-" & Guid.NewGuid().ToString("N"), "from-file.jar"), "imported_fixture")
        Dim importedBytes = File.ReadAllBytes(imported)
        Await ImportLocalModFilesAsync({imported}) : layout()
        check(_localMods.Count = 5 AndAlso File.ReadAllBytes(Path.Combine(instance.RunDirectory, "mods/from-file.jar")).SequenceEqual(importedBytes) AndAlso File.ReadAllBytes(imported).SequenceEqual(importedBytes),
            "UI import copies source bytes through the production publication kernel and rescans its owner")
        ShowSettings(Nothing, Nothing) : layout() : ShowManagement(Nothing, Nothing)
        Await WaitPackUiAsync(Function() LocalModsPage.IsVisible AndAlso Not _localModScanning AndAlso Not _managementScanning) : layout()
        Dim reloadedOnReturn = _localModReadSucceeded AndAlso LocalModImport.IsEnabled AndAlso _localMods.Count = 5
        CancelLocalModRequest()
        RefreshManagement(Nothing, New RoutedEventArgs())
        Await WaitPackUiAsync(Function() _localModReadSucceeded AndAlso Not _localModScanning AndAlso Not _managementScanning) : layout()
        check(reloadedOnReturn AndAlso _localModReadSucceeded AndAlso LocalModImport.IsEnabled AndAlso _localMods.Count = 5,
            "returning from settings and refreshing management both reload the retained Mod page instead of leaving its read gate permanently closed")
        instance = _managedInstance
        Dim toggleCalls = 0, gate As New TaskCompletionSource(Of LocalModActionResult)(TaskCreationOptions.RunContinuationsAsynchronously)
        _localModToggle = Function(owner, entry, enabled, token)
                              toggleCalls += 1
                              Return gate.Task
                          End Function
        first = _localMods.Single(Function(entry) entry.FileName = "first.jar")
        Dim writing = ToggleLocalModAsync(first)
        check(_localModWriting AndAlso Not LocalModRows.IsEnabled AndAlso Not LocalModImport.IsEnabled AndAlso Not ManagementFolders.IsEnabled AndAlso Not GameFolderCard.IsEnabled AndAlso Not ExternalLauncherCard.IsEnabled,
            "local Mod writes freeze repeated actions and all external-directory editing")
        Await ToggleLocalModAsync(first)
        check(toggleCalls = 1, "a double toggle cannot start a second file operation")
        Dim selectedBefore = _selectedFolder
        FolderList.SelectedItem = folders(1)
        check(_selectedFolder Is selectedBefore AndAlso FolderList.SelectedItem Is selectedBefore, "programmatic directory changes are also rejected while a local Mod transaction owns the target")
        Dim beforeTask = _taskGeneration
        StartInstall(Nothing, Nothing)
        check(Not _busy AndAlso _taskGeneration = beforeTask, "a hidden or stale download action cannot start behind an active local Mod operation")
        BackFromLocalMods() : layout() : ShowSettings(Nothing, Nothing) : layout()
        Dim accepted = _localMods
        gate.TrySetResult(New LocalModActionResult(Path.Combine(instance.RunDirectory, "mods"), 0, 1))
        Await writing : layout()
        check(SettingsPage.IsVisible AndAlso _localMods Is accepted AndAlso Not _localModWriting AndAlso _managedInstance Is instance,
            "finishing a local file operation after leaving the page cannot reopen or replace another surface")
        check(StatusText.Text.Contains(instance.Name) AndAlso GameFolderCard.IsEnabled, "off-page local operation completion reports its frozen owner and releases editing gates")

        ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(False) : layout()
        SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = "我的原版")) : layout()
        ShowLocalMods(Nothing, Nothing) : Await LoadLocalModsAsync(False) : layout()
        check(LocalModSharedHint.IsVisible AndAlso LocalModDirectory.Text = Path.Combine(game, "mods"),
            "a nonisolated instance explicitly explains the shared Mods write scope")
        Dim writeOwner = _localModInstance
        _localModImporter = Function(owner, paths, token)
                                gate = New TaskCompletionSource(Of LocalModActionResult)(TaskCreationOptions.RunContinuationsAsynchronously)
                                Return gate.Task
                            End Function
        writing = ImportLocalModFilesAsync({imported})
        BackFromLocalMods() : layout() : BackFromManagement(Nothing, Nothing) : layout()
        SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = "Fabric 实例")) : layout()
        ShowLocalMods(Nothing, Nothing) : Await LoadLocalModsAsync(False) : layout()
        Dim displayedWriteOwner = LocalModStatus.Text.Contains(writeOwner.Name) AndAlso Not LocalModImport.IsEnabled
        gate.TrySetResult(New LocalModActionResult(Path.Combine(writeOwner.RunDirectory, "mods"), 0, 1))
        Await writing : layout()
        check(displayedWriteOwner AndAlso _localModInstance.Name = "Fabric 实例" AndAlso _localMods.Count = 5 AndAlso LocalModImport.IsEnabled AndAlso
            Not LocalModStatus.Text.Contains("正在处理") AndAlso Not LocalModStatus.Text.Contains("完成"),
            "another instance's completed write reloads the currently browsed Mods and clears its owner hint without relabeling the result")
        BackFromLocalMods() : layout() : BackFromManagement(Nothing, Nothing) : layout()
        SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = "我的原版")) : layout()
        ShowLocalMods(Nothing, Nothing) : Await LoadLocalModsAsync(False) : layout()
        Dim readGate As New TaskCompletionSource(Of List(Of LocalModEntry))(TaskCreationOptions.RunContinuationsAsynchronously)
        _localModProvider = Function(owner, token) readGate.Task
        Dim oldRead = LoadLocalModsAsync(True)
        check(_localModScanning AndAlso Not LocalModImport.IsEnabled AndAlso Not LocalModRows.IsEnabled, "in-flight local scanning blocks file actions against old entries")
        BackFromLocalMods() : layout()
        BackFromManagement(Nothing, Nothing) : layout()
        SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = "Fabric 实例")) : layout()
        _localModProvider = Function(owner, token) Task.FromResult(LocalModCatalog.Scan(owner, token))
        ShowLocalMods(Nothing, Nothing) : Await LoadLocalModsAsync(False) : layout()
        Dim fresh = _localMods
        readGate.TrySetResult(New List(Of LocalModEntry)()) : Await oldRead : layout()
        check(_localMods Is fresh AndAlso _localModInstance.Name = "Fabric 实例" AndAlso _localMods.Count = 5, "an old scan cannot replace Mods belonging to a newly selected instance")
        _localModImporter = Function(owner, paths, token)
                                gate = New TaskCompletionSource(Of LocalModActionResult)(TaskCreationOptions.RunContinuationsAsynchronously)
                                Return gate.Task
                            End Function
        writing = ImportLocalModFilesAsync({imported})
        CancelLocalModOperation(Nothing, Nothing)
        check(_localModWriteRequest.IsCancellationRequested AndAlso Not LocalModCancel.IsEnabled AndAlso _localModWriting, "explicit cancellation waits for the file operation to release ownership")
        gate.TrySetCanceled() : Await writing : layout()
        check(Not _localModWriting AndAlso LocalModImport.IsEnabled AndAlso LocalModStatus.Text.Contains("取消"), "a cancelled local import settles its status and permits a fresh retry")
        _localModImporter = Function(owner, paths, token) Task.FromResult(LocalModActions.ImportFiles(owner, paths, token))
        Await ImportLocalModFilesAsync({imported}) : layout()
        check(LocalModStatus.Text.Contains("跳过") AndAlso _localMods.Count = 5, "retry after UI cancellation can reuse identical already-installed content")
        _localModProvider = Function(owner, token)
                                Throw New IOException("controlled post-write scan failure")
                            End Function
        _localModImporter = Function(owner, paths, token) Task.FromResult(New LocalModActionResult(Path.Combine(owner.RunDirectory, "mods"), 0, 1))
        Await ImportLocalModFilesAsync({imported}) : layout()
        check(LocalModStatus.Text.Contains("controlled post-write scan failure") AndAlso Not LocalModImport.IsEnabled AndAlso Not LocalModRows.IsEnabled,
            "a failed post-write rescan preserves its read error and cannot expose file actions against stale entries")
        _localModProvider = Function(owner, token) Task.FromResult(LocalModCatalog.Scan(owner, token))
        Await LoadLocalModsAsync(True) : layout()
        check(LocalModImport.IsEnabled AndAlso LocalModRows.IsEnabled AndAlso _localMods.Count = 5,
            "a successful refresh after a read failure restores local Mod operations from fresh file identities")
        Dim refreshGate As New TaskCompletionSource(Of List(Of LocalModEntry))(TaskCreationOptions.RunContinuationsAsynchronously)
        _localModProvider = Function(owner, token) refreshGate.Task
        _localModImporter = Function(owner, paths, token) Task.FromResult(New LocalModActionResult(Path.Combine(owner.RunDirectory, "mods"), 0, 1))
        Dim olderWrite = ImportLocalModFilesAsync({imported})
        Await WaitPackUiAsync(Function() _localModScanning AndAlso Not _localModWriting)
        BackFromLocalMods() : layout()
        _localModProvider = Function(owner, token) Task.FromResult(LocalModCatalog.Scan(owner, token))
        ShowLocalMods(Nothing, Nothing) : Await LoadLocalModsAsync(False) : layout()
        _localModImporter = Function(owner, paths, token)
                                gate = New TaskCompletionSource(Of LocalModActionResult)(TaskCreationOptions.RunContinuationsAsynchronously)
                                Return gate.Task
                            End Function
        writing = ImportLocalModFilesAsync({imported})
        Dim currentOperationText = LocalModStatus.Text
        refreshGate.TrySetResult(New List(Of LocalModEntry)()) : Await olderWrite : layout()
        check(_localModWriting AndAlso LocalModStatus.Text = currentOperationText, "an older completed write's delayed rescan cannot overwrite a newer operation status after returning to the same instance")
        gate.TrySetCanceled() : Await writing : layout()
        _localModImporter = Function(owner, paths, token)
                                gate = New TaskCompletionSource(Of LocalModActionResult)(TaskCreationOptions.RunContinuationsAsynchronously)
                                Return gate.Task
                            End Function
        writing = ImportLocalModFilesAsync({imported})
        Dim closing As New CancelEventArgs()
        WindowClosing(Nothing, closing)
        check(closing.Cancel AndAlso Not _closing AndAlso _localModWriting AndAlso _localModWriteRequest.IsCancellationRequested,
            "window close requests cancellation and waits for the owned Mod transaction instead of abandoning file publication")
        gate.TrySetCanceled() : Await writing : layout()
        check(Not _closing AndAlso Not _localModWriting AndAlso LocalModImport.IsEnabled, "settled close cancellation leaves the current Mod page usable for retry or a later close")
        For Each dark In {False, True}
            RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", dark}})
            host.Width = 810 : host.Height = 470 : layout()
            check(LocalModDirectory.ActualWidth > 300 AndAlso LocalModListCard.ActualWidth > 600 AndAlso LocalModsPage.ScrollableHeight > 0, "local Mod controls fit the minimum window in dark mode " & dark)
            SaveControlVisual(host, Path.Combine(directory, "local-mods-" & If(dark, "dark", "light") & ".png"))
        Next
        check(protectedBytes.All(Function(pair) File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)), "UI local scanning import state changes and navigation preserve all original non-Mod configuration bytes")
    End Function

    Private Sub VerifyLocalModMotion(directory As String, check As Action(Of Boolean, String))
        Dim owner As New MainWindow(True), original = owner.Content
        Dim host As New Border With {.Width = 850, .Height = 700}, layout = ControlLayout(host)
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using source = PreviewSurface.Attach(host, 850, 700)
                layout() : owner.PrepareLocalModFixture(directory) : owner.ShowManagement(Nothing, Nothing) : layout()
                If Not PumpMotionUntil(Function() Not owner._managementScanning) Then Throw New TimeoutException("Local Mod motion fixture did not load.")
                Dim instance = owner._managementInstances.Single(Function(item) item.Name = "Fabric 实例")
                owner.SelectManagedInstance(instance) : layout()
                owner._motionPreview = True : owner._managementMotion.PauseForChecks = True : owner._surfaceMotion.PauseForChecks = True
                Dim delayedRead As New TaskCompletionSource(Of List(Of LocalModEntry))(TaskCreationOptions.RunContinuationsAsynchronously)
                owner._localModProvider = Function(target, token) delayedRead.Task
                owner.ShowLocalMods(Nothing, Nothing) : layout()
                Dim initialEntry = owner._managementMotion.Transition IsNot Nothing AndAlso owner.LocalModsPage.IsVisible AndAlso Not owner.ManagementToolbar.IsVisible AndAlso owner.ManagementGhost.Children.Count > 0
                delayedRead.TrySetResult(LocalModCatalog.Scan(instance, CancellationToken.None))
                If Not PumpMotionUntil(Function() Not owner._localModScanning) Then Throw New TimeoutException("Delayed Mod entry did not settle.")
                owner._localModProvider = Function(target, token) Task.FromResult(LocalModCatalog.Scan(target, token))
                layout()
                Dim entering = owner._managementMotion.Transition
                check(initialEntry AndAlso entering IsNot Nothing AndAlso owner.LocalModsPage.IsVisible AndAlso Not owner.ManagementViewHost.IsHitTestVisible AndAlso owner.ManagementGhost.Children.Count > 0,
                    "local Mods enter through existing PCL card motion with inert exit layers and an input gate")
                Dim plan = DirectCast(entering.Timeline, ParallelTimeline)
                check(plan.Children.OfType(Of PclPageYAnimation)().First().BeginTime.Value.TotalMilliseconds = 140 AndAlso owner._managementMotion.ItemCount > 1,
                    "local Mods preserve PCL's 110 plus 30 millisecond handoff and staggered visible cards")
                Dim stale = owner._localMods.First(), staleCalls = 0
                owner._localModToggle = Function(target, entry, enabled, token)
                                           staleCalls += 1
                                           Return Task.FromResult(New LocalModActionResult("", 0, 0))
                                       End Function
                owner.ToggleLocalModAsync(stale).GetAwaiter().GetResult()
                check(staleCalls = 0 AndAlso Not owner._localModWriting, "card input gating rejects a stale Mod toggle during entry")
                PauseSelectionClock(entering) : entering.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(190), TimeSeekOrigin.BeginTime) : layout()
                check(owner.ManagementViewHost.IsHitTestVisible AndAlso owner.ManagementGhost.Children.Count = 0, "local Mod cards release their entry gate while the remaining motion continues")
                SaveControlVisual(host, Path.Combine(directory, "local-mod-entry-190ms.png"))
                owner.BackFromLocalMods() : layout()
                Dim returning = owner._managementMotion.Transition
                check(returning IsNot Nothing AndAlso returning IsNot entering AndAlso owner.ManagementDetailPage.IsVisible AndAlso Not owner.LocalModsPage.IsVisible AndAlso owner.ManagementGhost.Children.Count = 1,
                    "rapid local Mod return snapshots its interrupted composite and restores the instance overview")
                entering.Controller.SkipToFill() : layout()
                check(owner._managementMotion.Transition Is returning AndAlso owner.ManagementDetailPage.IsVisible, "obsolete local Mod entry completion cannot replace the newer detail return")
                owner._managementMotion.Finish() : layout()
                check(owner.ManagementViewHost.IsHitTestVisible AndAlso owner.ManagementGhost.Children.Count = 0, "local Mod return releases old clocks images and input ownership")
                owner.ShowLocalMods(Nothing, Nothing) : layout()
                Dim retired = owner._managementMotion.Transition
                owner.ShowSettings(Nothing, Nothing) : layout()
                check(owner.SettingsPage.IsVisible AndAlso owner._managementMotion.Transition Is Nothing AndAlso owner.ManagementGhost.Children.Count = 0, "switching from local Mods to settings retires all child-page clocks")
                retired?.Controller.SkipToFill() : layout()
                check(owner.SettingsPage.IsVisible AndAlso Not owner.LocalModsPage.IsVisible, "an old local Mod child completion cannot reactivate its hidden page")
                owner._surfaceMotion.Finish() : owner.ShowManagement(Nothing, Nothing) : layout()
                owner._surfaceMotion.Finish() : owner.SelectManagedInstance(instance) : layout() : owner._managementMotion.Finish()
                owner.ShowLocalMods(Nothing, Nothing) : layout()
                host.Width = 900 : layout()
                check(owner._managementMotion.Transition Is Nothing AndAlso owner.ManagementViewHost.IsHitTestVisible, "resizing during local Mod motion settles the visible page and its gate")
                owner.ShowSettings(Nothing, Nothing) : layout() : host.Child = Nothing : layout()
                check(owner._managementMotion.Transition Is Nothing AndAlso owner._surfaceMotion.Transition Is Nothing AndAlso owner.ManagementGhost.Children.Count = 0 AndAlso owner.PageGhost.Children.Count = 0,
                    "unloading the local Mod surface releases child root clocks and frozen exit layers")
            End Using
        Finally
            owner._closing = True : owner.CancelLocalModRequest()
            owner._managementMotion.Finish() : owner._surfaceMotion.Finish()
            host.Child = Nothing : owner.Content = original
        End Try
    End Sub
End Class
