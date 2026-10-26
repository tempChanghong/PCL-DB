Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Sub VerifyLocalModFollowupUi(directory As String, check As Action(Of Boolean, String))
        Dim owner As New MainWindow(True), original = owner.Content
        Dim host As New Border With {.Width = 850, .Height = 700}, layout = ControlLayout(host)
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using source = PreviewSurface.Attach(host, 850, 700)
                layout()
                Dim work = owner.VerifyLocalModFollowupWorkflowAsync(directory, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 25000) Then Throw New TimeoutException("Local Mod refresh followup did not settle.")
                work.GetAwaiter().GetResult()
            End Using
        Finally
            owner._closing = True : owner.CancelManagementRequest() : owner.CancelLocalModRequest() : owner._localModWriteRequest?.Cancel()
            owner._managementMotion.Finish() : owner._surfaceMotion.Finish()
            host.Child = Nothing : owner.Content = original
        End Try
    End Sub

    Private Async Function VerifyLocalModFollowupWorkflowAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim folders = PrepareLocalModFixture(directory), game = folders(0).Location
        LocalModJarForChecks(Path.Combine(game, "mods/shared.jar"), "shared_refresh_fixture")
        _localModInstanceReader = Function(instance, token) Task.FromResult(InstalledInstanceCatalog.Read(instance.Target, instance.Name, token))
        ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(False) : layout()
        For Each folder In {folders(0), folders(2), folders(1)}
            If LocalModsPage.Visibility = Visibility.Visible Then BackFromLocalMods() : layout()
            If ManagementDetailPage.Visibility = Visibility.Visible Then BackFromManagement(Nothing, Nothing) : layout()
            FolderList.SelectedItem = folder : RefreshManagementFolders() : Await ScanManagementAsync(True) : layout()
            SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = "Fabric 实例")) : layout()
            ShowLocalMods(Nothing, Nothing) : Await LoadLocalModsAsync(False) : layout()
            Dim previous = _managedInstance, staleEntry = _localMods.First(), isolated = Not previous.IsIsolated.Value
            Dim nativeConfig = SetLocalModIsolationForChecks(previous, isolated)
            Dim preserved = IO.Directory.GetFiles(Path.GetDirectoryName(game), "*", SearchOption.AllDirectories).
                ToDictionary(Function(full) full, Function(full) File.ReadAllBytes(full))
            RefreshLocalMods(Nothing, New RoutedEventArgs())
            Await WaitPackUiAsync(Function() Not _localModScanning) : layout()
            Dim expectedRun = If(isolated, previous.InstancePath, game), expectedCount = If(isolated, 4, 1)
            check(_localModReadSucceeded AndAlso _managedInstance IsNot previous AndAlso _localModInstance Is _managedInstance AndAlso
                _managedInstance.Target.Kind = folder.LauncherKind.Value AndAlso SameGameRoot(_managedInstance.RunDirectory, expectedRun) AndAlso
                LocalModDirectory.Text = Path.Combine(expectedRun, "mods") AndAlso LocalModSharedHint.IsVisible = Not isolated,
                "explicit refresh rereads " & folder.LauncherKind.Value.ToString() & " native isolation and atomically replaces its directory and shared hint")
            check(_localMods.Count = expectedCount AndAlso _localModRowViews.Count = expectedCount AndAlso
                _managementInstances.Single(Function(item) item.Name = previous.Name) Is _managedInstance AndAlso
                _localMods.All(Function(entry) SameGameRoot(Path.GetDirectoryName(entry.FilePath), Path.Combine(expectedRun, "mods"))),
                "native context refresh rebuilds only current Mod row identities and keeps the cached installed row bound to the accepted snapshot")
            Dim staleCalls = 0
            _localModToggle = Function(instance, entry, enabled, token)
                                  staleCalls += 1
                                  Return Task.FromResult(New LocalModActionResult("", 0, 0))
                              End Function
            Await ToggleLocalModAsync(staleEntry)
            check(staleCalls = 0 AndAlso preserved.All(Function(pair) File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)) AndAlso File.Exists(nativeConfig),
                "refresh rejects retired Mod callbacks and preserves every external config and archive byte for " & folder.LauncherKind.Value.ToString())
        Next

        BackFromLocalMods() : layout() : BackFromManagement(Nothing, Nothing) : layout()
        FolderList.SelectedItem = folders(0) : RefreshManagementFolders() : Await ScanManagementAsync(True) : layout()
        SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = "Fabric 实例")) : layout()
        ShowLocalMods(Nothing, Nothing) : Await LoadLocalModsAsync(False) : layout()
        Dim retained = _managedInstance, retainedEntries = _localMods
        Dim brokenConfig = Path.Combine(retained.InstancePath, "PCL/Setup.ini")
        File.WriteAllText(brokenConfig, "VersionArgumentIndieV2:invalid" & vbCrLf & "WindowWidth:900")
        Dim invalidBytes = File.ReadAllBytes(brokenConfig)
        RefreshLocalMods(Nothing, New RoutedEventArgs()) : Await WaitPackUiAsync(Function() Not _localModScanning) : layout()
        check(_managedInstance Is retained AndAlso _localMods Is retainedEntries AndAlso Not _localModReadSucceeded AndAlso
            Not LocalModImport.IsEnabled AndAlso Not LocalModRows.IsEnabled AndAlso LocalModRefresh.IsEnabled AndAlso
            LocalModStatus.Text.Contains("隔离设置无效") AndAlso File.ReadAllBytes(brokenConfig).SequenceEqual(invalidBytes),
            "invalid external isolation keeps the previous context inspectable but prohibits stale writes and automatic fallback")
        SetLocalModIsolationForChecks(retained, True)
        RefreshLocalMods(Nothing, New RoutedEventArgs()) : Await WaitPackUiAsync(Function() Not _localModScanning) : layout()
        check(_localModReadSucceeded AndAlso LocalModImport.IsEnabled AndAlso _managedInstance.IsIsolated.Value AndAlso _localMods.Count = 4,
            "repairing external settings permits a successful same-page refresh without backing out or rewriting the launcher config")

        Dim delayedInstance As New TaskCompletionSource(Of InstalledInstance)(TaskCreationOptions.RunContinuationsAsynchronously)
        _localModInstanceReader = Function(instance, token) delayedInstance.Task
        Dim retired = _managedInstance, refreshing = LoadLocalModsAsync(True, refreshInstance:=True)
        BackFromLocalMods() : layout() : BackFromManagement(Nothing, Nothing) : layout()
        SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = "我的原版")) : layout()
        ' The retired request keeps its gate; the newly entered instance owns
        ' a fresh native read rather than reusing the old test provider.
        _localModInstanceReader = Function(instance, token) Task.FromResult(InstalledInstanceCatalog.Read(instance.Target, instance.Name, token))
        ShowLocalMods(Nothing, Nothing) : Await LoadLocalModsAsync(False) : layout()
        Dim accepted = _managedInstance, acceptedEntries = _localMods
        delayedInstance.TrySetResult(InstalledInstanceCatalog.Read(retired.Target, retired.Name, CancellationToken.None)) : Await refreshing : layout()
        check(_managedInstance Is accepted AndAlso _localModInstance Is accepted AndAlso _localMods Is acceptedEntries AndAlso
            LocalModDirectory.Text = Path.Combine(game, "mods") AndAlso LocalModImport.IsEnabled,
            "an uncancellable old instance reread cannot replace the newer selected Mod page or its input gates")

        BackFromLocalMods() : layout() : BackFromManagement(Nothing, Nothing) : layout()
        Dim delayedRoot As New TaskCompletionSource(Of List(Of InstalledInstance))(TaskCreationOptions.RunContinuationsAsynchronously)
        _managementProvider = Function(target, token) delayedRoot.Task
        RefreshManagement(Nothing, New RoutedEventArgs())
        Dim rootRequest = _managementRequest
        SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = "Fabric 实例")) : layout()
        ShowLocalMods(Nothing, Nothing) : Await LoadLocalModsAsync(False) : layout()
        Dim writeGate As New TaskCompletionSource(Of LocalModActionResult)(TaskCreationOptions.RunContinuationsAsynchronously)
        _localModImporter = Function(instance, paths, token) writeGate.Task
        Dim writing = ImportLocalModFilesAsync({Path.Combine(game, "mods/shared.jar")})
        Dim readGeneration = _localModGeneration, writeStatus = LocalModStatus.Text, writeOwner = _localModInstance
        delayedRoot.TrySetResult(New List(Of InstalledInstance)())
        Await WaitPackUiAsync(Function() ManagementRequestReleasedForChecks(rootRequest)) : Await Task.Yield() : layout()
        check(_localModWriting AndAlso _localModGeneration = readGeneration AndAlso LocalModStatus.Text = writeStatus AndAlso _localModInstance Is writeOwner AndAlso
            Not LocalModImport.IsEnabled AndAlso Not _localModScanning,
            "a discarded older root refresh cannot restart a current Mod read or overwrite its active import status")
        writeGate.TrySetCanceled() : Await writing : layout()
        check(Not _localModWriting AndAlso _localModReadSucceeded AndAlso LocalModImport.IsEnabled AndAlso LocalModStatus.Text.Contains("取消"),
            "the protected current import still releases its ownership and refreshes correctly after cancellation")
    End Function

    Private Shared Function SetLocalModIsolationForChecks(instance As InstalledInstance, isolated As Boolean) As String
        Dim full As String
        Select Case instance.Target.Kind
            Case ExternalLauncherKind.Pcl
                full = Path.Combine(instance.InstancePath, "PCL/Setup.ini")
                File.WriteAllText(full, "VersionArgumentIndieV2:" & isolated.ToString() & vbCrLf & "WindowWidth:900")
            Case ExternalLauncherKind.PclCe
                full = Path.Combine(instance.InstancePath, "PCL/config.v1.yml")
                File.WriteAllText(full, "VersionArgumentIndieV2: " & isolated.ToString().ToLowerInvariant() & vbLf & "Unrelated: keep")
            Case ExternalLauncherKind.Hmcl
                full = Path.Combine(instance.InstancePath, "hmclversion.cfg")
                File.WriteAllText(full, New JObject From {{"usesGlobal", False}, {"gameDirType", If(isolated, 1, 0)}, {"unrelated", "keep"}}.ToString())
            Case Else
                Throw New InvalidOperationException("Unknown local Mod refresh fixture launcher.")
        End Select
        Return full
    End Function

    Private Sub VerifyLocalModFollowupMotion(directory As String, check As Action(Of Boolean, String))
        Dim owner As New MainWindow(True), original = owner.Content
        Dim host As New Border With {.Width = 850, .Height = 700}, layout = ControlLayout(host)
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using source = PreviewSurface.Attach(host, 850, 700)
                layout() : Dim folders = owner.PrepareLocalModFixture(directory), game = folders(0).Location
                LocalModJarForChecks(Path.Combine(game, "mods/shared.jar"), "shared_refresh_motion")
                owner._localModInstanceReader = Function(instance, token) Task.FromResult(InstalledInstanceCatalog.Read(instance.Target, instance.Name, token))
                owner.ShowManagement(Nothing, Nothing) : layout()
                If Not PumpMotionUntil(Function() Not owner._managementScanning) Then Throw New TimeoutException("Local Mod refresh motion fixture did not load.")
                owner.SelectManagedInstance(owner._managementInstances.Single(Function(item) item.Name = "Fabric 实例")) : layout()
                owner.ShowLocalMods(Nothing, Nothing) : layout()
                If Not PumpMotionUntil(Function() Not owner._localModScanning) Then Throw New TimeoutException("Local Mod refresh motion page did not load.")
                owner._motionPreview = True : owner._managementMotion.PauseForChecks = True : owner._surfaceMotion.PauseForChecks = True
                Dim nativeConfig = SetLocalModIsolationForChecks(owner._managedInstance, False), preserved = File.ReadAllBytes(nativeConfig)
                owner.RefreshLocalMods(Nothing, New RoutedEventArgs()) : layout()
                If Not PumpMotionUntil(Function() Not owner._localModScanning) Then Throw New TimeoutException("Local Mod refresh motion did not settle.")
                layout()
                Dim refreshed = owner._managementMotion.Transition
                check(refreshed IsNot Nothing AndAlso Not owner.ManagementViewHost.IsHitTestVisible AndAlso owner.ManagementGhost.Children.Count > 0 AndAlso
                    owner.LocalModDirectory.Text = Path.Combine(game, "mods") AndAlso owner.LocalModSharedHint.IsVisible AndAlso owner._localMods.Count = 1,
                    "a refreshed native Mods directory enters with the existing PCL card motion and atomic shared-path presentation")
                Dim plan = DirectCast(refreshed.Timeline, ParallelTimeline)
                check(plan.Children.OfType(Of PclPageYAnimation)().First().BeginTime.Value.TotalMilliseconds = 140 AndAlso owner._managementMotion.ItemCount > 1,
                    "native context refresh retains PCL staggered card entry and its 110 plus 30 millisecond handoff")
                owner.RefreshLocalMods(Nothing, New RoutedEventArgs())
                check(owner._managementMotion.Transition Is refreshed AndAlso Not owner._localModScanning,
                    "repeated refresh input cannot replace the active card transition before its input handoff")
                PauseSelectionClock(refreshed) : refreshed.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(190), TimeSeekOrigin.BeginTime) : layout()
                check(owner.ManagementViewHost.IsHitTestVisible AndAlso owner.ManagementGhost.Children.Count = 0,
                    "refreshed Mod cards release their input gate and frozen old directory after the entry handoff")
                SaveControlVisual(host, Path.Combine(directory, "local-mod-refresh-190ms.png"))
                owner.BackFromLocalMods() : layout()
                Dim returning = owner._managementMotion.Transition
                check(returning IsNot Nothing AndAlso returning IsNot refreshed AndAlso owner.ManagementDetailPage.IsVisible AndAlso
                    owner.ManagedRunPath.Text = game AndAlso owner.ManagedIsolation.Text.Contains("共享"),
                    "rapid return after native context refresh restores the updated instance details through a separate motion")
                refreshed.Controller.SkipToFill() : layout()
                check(owner._managementMotion.Transition Is returning AndAlso owner.ManagementDetailPage.IsVisible AndAlso Not owner.LocalModsPage.IsVisible,
                    "retired native-refresh animation completion cannot revive the Mod page or replace its return clock")
                owner._managementMotion.Finish() : layout()
                check(owner.ManagementViewHost.IsHitTestVisible AndAlso owner.ManagementGhost.Children.Count = 0 AndAlso File.ReadAllBytes(nativeConfig).SequenceEqual(preserved),
                    "native refresh and return release old clocks and preserve external isolation configuration bytes")
            End Using
        Finally
            owner._closing = True : owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._managementMotion.Finish() : owner._surfaceMotion.Finish()
            host.Child = Nothing : owner.Content = original
        End Try
    End Sub
End Class
