Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyModWriteHandoffUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim host As New Border With {.Width = 900, .Height = 650}, layout = ControlLayout(host), observations As New JObject
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 650)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckModWriteHandoffAsync(directory, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 45000) Then Throw New TimeoutException("Mod write handoff did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "mod-write-handoff.json"), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            owner._closing = True : owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner._managementMotion.Finish()
            host.Child = Nothing : owner.Content = original : SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub

    Private Async Function CheckModWriteHandoffAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion() : _managementMotion.Finish() : layout()
                               End Sub
        For launcher = 0 To 2
            For Each animated In {False, True}
                For Each dark In {False, True}
                    _motionPreview = False
                    Dim folders = PrepareLocalModFixture(directory), game = folders(0).Location
                    LocalModJarForChecks(Path.Combine(game, "mods/shared.jar"), "write_handoff_shared")
                    FolderList.SelectedItem = folders(launcher)
                    Dim target = CurrentInstallTarget(), fabric = InstalledInstanceCatalog.Read(target, "Fabric 实例", CancellationToken.None)
                    Dim plain = InstalledInstanceCatalog.Read(target, "我的原版", CancellationToken.None)
                    IO.Directory.CreateDirectory(Path.Combine(plain.InstancePath, "PCL"))
                    SetLocalModIsolationForChecks(plain, False)
                    Dim config = Path.GetFullPath(SetLocalModIsolationForChecks(fabric, True))
                    Dim protectedFiles = IO.Directory.GetFiles(Path.GetDirectoryName(game), "*", SearchOption.AllDirectories).ToDictionary(Function(path) path, Function(path) File.ReadAllBytes(path))
                    ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(True) : settle()
                    Dim selectMods As Func(Of String, Task) = Async Function(name)
                                                                 If LocalModsPage.Visibility = Visibility.Visible Then BackFromLocalMods()
                                                                 settle()
                                                                 If ManagementDetailPage.Visibility = Visibility.Visible Then BackFromManagement(Nothing, Nothing)
                                                                 settle()
                                                                 SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = name)) : settle()
                                                                 ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                                                             End Function
                    Await selectMods("Fabric 实例")
                    _motionPreview = animated : RestoreAppearance(New JObject From {{"darkMode", dark}})
                    Dim realImporter = _localModImporter, conflictingReads = 0, otherReads = 0
                    _localModProvider = Function(instance, token)
                                            If _localModWriting AndAlso _localModWriteOwner IsNot Nothing Then
                                                If SameGameRoot(instance.RunDirectory, _localModWriteOwner.RunDirectory) Then conflictingReads += 1 Else otherReads += 1
                                            End If
                                            Return Task.FromResult(LocalModCatalog.Scan(instance, token))
                                        End Function
                    Dim gate As New TaskCompletionSource(Of LocalModActionResult)(TaskCreationOptions.RunContinuationsAsynchronously), writeToken As CancellationToken
                    _localModImporter = Function(instance, files, token)
                                            writeToken = token : Return gate.Task
                                        End Function
                    Dim frozen = _localModInstance, sourcePath = Path.Combine(frozen.RunDirectory, "mods/first.jar")
                    Dim writing = ImportLocalModFilesAsync({sourcePath}), request = _localModWriteRequest, initialStatus = LocalModStatus.Text
                    ShowManagement(Nothing, Nothing) : settle()
                    Await LoadLocalModsAsync(True) : settle()
                    Dim label = "/launcher=" & target.Kind.ToString() & "/motion=" & animated & "/dark=" & dark
                    check(_localModWriting AndAlso _localModWriteRequest Is request AndAlso _localModWriteOwner Is frozen AndAlso conflictingReads = 0 AndAlso
                        Not _localModScanning AndAlso Not _localModReadSucceeded AndAlso LocalModStatus.Text = initialStatus,
                        "reselecting management and ordinary refresh do not scan a directory owned by the active Mod writer or replace its feedback" & label)
                    Await selectMods("我的原版")
                    check(otherReads > 0 AndAlso _localModReadSucceeded AndAlso _localMods.Count = 1 AndAlso _localModInstance.Name = "我的原版" AndAlso
                        _localModWriting AndAlso _localModWriteOwner Is frozen AndAlso _localModWriteRequest Is request AndAlso Not writeToken.IsCancellationRequested AndAlso Not LocalModImport.IsEnabled,
                        "a different physical Mods directory remains readable without changing the frozen writer or allowing a second write" & label)
                    Await selectMods("Fabric 实例")
                    CorruptModContextForChecks(config, target.Kind) : protectedFiles(config) = File.ReadAllBytes(config)
                    RefreshManagement(Nothing, New RoutedEventArgs())
                    Await WaitPackUiAsync(Function() Not _managementScanning) : settle()
                    CancelLocalModOperation(Nothing, Nothing)
                    ShowManagement(Nothing, Nothing) : settle() : RefreshLocalModAvailability()
                    check(_managedInstance.Problem IsNot Nothing AndAlso _localModWriting AndAlso writeToken.IsCancellationRequested AndAlso Not LocalModCancel.IsEnabled AndAlso
                        LocalModStatus.Text.Contains("正在等待文件操作退出") AndAlso Not _localModScanning,
                        "native context changes and repeated management refresh cannot replace pending cancellation with generic running or reading feedback" & label)
                    If animated Then
                        SetLocalModIsolationForChecks(_managedInstance, Not dark) : protectedFiles(config) = File.ReadAllBytes(config)
                    End If
                    If dark Then gate.SetResult(New LocalModActionResult(Path.Combine(frozen.RunDirectory, "mods"), 0, 1)) Else gate.SetCanceled()
                    Await writing : settle()
                    Dim recovered = If(animated, _localModReadSucceeded AndAlso _managedInstance.Problem Is Nothing AndAlso
                        SameGameRoot(_managedInstance.RunDirectory, If(dark, game, frozen.InstancePath)),
                        Not _localModReadSucceeded AndAlso LocalModStatus.Text.Contains("无法读取 Mod 列表") AndAlso LocalModRefresh.IsEnabled)
                    check(recovered AndAlso Not _localModWriting AndAlso _localModWriteRequest Is Nothing AndAlso LocalModCancel.Visibility = Visibility.Collapsed AndAlso
                        Not LocalModStatus.Text.Contains("正在处理") AndAlso Not LocalModStatus.Text.Contains("正在等待") AndAlso Not LocalModStatus.Text.Contains("正在读取") AndAlso
                        StatusText.Text.Contains(frozen.Name) AndAlso StatusText.Text.Contains(If(dark, "完成", "取消")),
                        "writer exit rereads the current native snapshot and publishes either a usable directory or its actual read error while retaining the writer outcome" & label)
                    SetLocalModIsolationForChecks(_managedInstance, True) : protectedFiles(config) = File.ReadAllBytes(config)
                    RefreshLocalMods(Nothing, New RoutedEventArgs())
                    Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                    check(_localModReadSucceeded AndAlso _localModInstance Is _managedInstance AndAlso SameGameRoot(_managedInstance.RunDirectory, frozen.InstancePath) AndAlso
                        _localMods.Count = 4 AndAlso LocalModImport.IsEnabled AndAlso Not _localModWriting,
                        "explicit retry after completion reads the repaired native instance and restores only its isolated Mods directory" & label)
                    _localModImporter = realImporter
                    Await ImportLocalModFilesAsync({sourcePath}) : settle()
                    check(_localModReadSucceeded AndAlso LocalModImport.IsEnabled AndAlso LocalModStatus.Text.Contains("跳过 1") AndAlso Not _localModWriting,
                        "real idempotent import immediately retries existing checked content after the overlap and cancellation" & label)

                    Dim leaveGate As New TaskCompletionSource(Of LocalModActionResult)(TaskCreationOptions.RunContinuationsAsynchronously), leaveToken As CancellationToken
                    _localModImporter = Function(instance, files, token)
                                            leaveToken = token : Return leaveGate.Task
                                        End Function
                    Dim leaving = ImportLocalModFilesAsync({sourcePath})
                    Dim returned = NavigateTitleBack() : settle()
                    ShowSettings(Nothing, Nothing) : settle()
                    CancelLocalModOperation(Nothing, Nothing) : leaveGate.SetCanceled()
                    Await leaving : settle()
                    Dim stayed = SettingsPage.IsVisible
                    ShowManagement(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _managementScanning) : settle()
                    ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                    check(returned AndAlso stayed AndAlso leaveToken.IsCancellationRequested AndAlso _localModReadSucceeded AndAlso LocalModsPage.IsVisible AndAlso
                        Not _localModWriting AndAlso LocalModImport.IsEnabled AndAlso Not LocalModStatus.Text.Contains("正在处理"),
                        "return and settings navigation remain owned by the user while an off-page cancelled write settles and the Mod page reopens" & label)
                    check(conflictingReads = 0 AndAlso otherReads > 0 AndAlso protectedFiles.All(Function(pair) File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),
                        "folder-scoped read fencing preserves other-directory browsing and every archive and expected native configuration byte" & label)
                Next
            Next
        Next
        Await CheckModWriteRerouteAsync(directory, layout, check)
    End Function

    Private Async Function CheckModWriteRerouteAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : _managementMotion.Finish() : layout()
                               End Sub
        For launcher = 0 To 2
            _motionPreview = False
            Dim folders = PrepareLocalModFixture(directory), game = folders(0).Location
            LocalModJarForChecks(Path.Combine(game, "mods/shared.jar"), "reroute_shared")
            FolderList.SelectedItem = folders(launcher)
            Dim target = CurrentInstallTarget(), fabric = InstalledInstanceCatalog.Read(target, "Fabric 实例", CancellationToken.None)
            Dim plain = InstalledInstanceCatalog.Read(target, "我的原版", CancellationToken.None)
            SetLocalModIsolationForChecks(fabric, False)
            IO.Directory.CreateDirectory(Path.Combine(plain.InstancePath, "PCL"))
            Dim plainConfig = Path.GetFullPath(SetLocalModIsolationForChecks(plain, True))
            LocalModJarForChecks(Path.Combine(plain.InstancePath, "mods/other.jar"), "reroute_other")
            Dim protectedFiles = IO.Directory.GetFiles(Path.GetDirectoryName(game), "*", SearchOption.AllDirectories).ToDictionary(Function(path) path, Function(path) File.ReadAllBytes(path))
            ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(True) : settle()
            SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = "Fabric 实例")) : settle()
            ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
            Dim realImporter = _localModImporter, forbiddenReads = 0
            _localModProvider = Function(instance, token)
                                    If _localModWriting AndAlso SameGameRoot(instance.RunDirectory, _localModWriteOwner.RunDirectory) Then forbiddenReads += 1
                                    Return Task.FromResult(LocalModCatalog.Scan(instance, token))
                                End Function
            _localModInstanceReader = Function(instance, token) Task.FromResult(InstalledInstanceCatalog.Read(instance.Target, instance.Name, token))
            Dim gate As New TaskCompletionSource(Of LocalModActionResult)(TaskCreationOptions.RunContinuationsAsynchronously), writerToken As CancellationToken
            _localModImporter = Function(instance, files, token)
                                    writerToken = token : Return gate.Task
                                End Function
            Dim writing = ImportLocalModFilesAsync({Path.Combine(game, "mods/shared.jar")}), writer = _localModWriteOwner
            BackFromLocalMods() : settle() : BackFromManagement(Nothing, Nothing) : settle()
            SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = "我的原版")) : settle()
            ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
            SetLocalModIsolationForChecks(_managedInstance, False) : protectedFiles(plainConfig) = File.ReadAllBytes(plainConfig)
            ' A background post-write reread may already be pending when a new
            ' writer starts; the normal refresh button is intentionally gated.
            Await LoadLocalModsAsync(True, refreshInstance:=True) : settle()
            Dim label = "/reroute/launcher=" & target.Kind.ToString()
            Dim diagnostics As New JObject From {{"writer", writer.Name}, {"writerRun", writer.RunDirectory}, {"current", _managedInstance.Name},
                {"currentRun", _managedInstance.RunDirectory}, {"conflictingReads", forbiddenReads}, {"writing", _localModWriting},
                {"readSucceeded", _localModReadSucceeded}, {"scanning", _localModScanning}, {"status", LocalModStatus.Text}}
            check(forbiddenReads = 0 AndAlso _localModWriting AndAlso Not _localModReadSucceeded AndAlso Not _localModScanning AndAlso
                LocalModStatus.Text.Contains(writer.Name) AndAlso Not LocalModStatus.Text.Contains("正在读取"),
                "native reread that redirects another instance into the active shared Mods folder defers before catalogue IO" & label)
            CancelLocalModOperation(Nothing, Nothing) : gate.SetCanceled()
            Await writing : settle()
            check(_localModReadSucceeded AndAlso _managedInstance.Name = "我的原版" AndAlso SameGameRoot(_managedInstance.RunDirectory, game) AndAlso
                _localModInstance Is _managedInstance AndAlso _localMods.Count = 1 AndAlso Not _localModWriting AndAlso LocalModSharedHint.IsVisible AndAlso
                StatusText.Text.Contains(writer.Name) AndAlso StatusText.Text.Contains("取消"),
                "writer exit rereads the currently viewed instance after a shared-folder reroute rather than returning to its old owner" & label)
            _localModImporter = realImporter
            RefreshLocalMods(Nothing, New RoutedEventArgs()) : Await WaitPackUiAsync(Function() Not _localModScanning)
            Await ImportLocalModFilesAsync({Path.Combine(game, "mods/shared.jar")}) : settle()
            check(_localModReadSucceeded AndAlso LocalModImport.IsEnabled AndAlso LocalModStatus.Text.Contains("跳过 1") AndAlso _managedInstance.Name = "我的原版",
                "the newly shared instance can retry a checked idempotent import after the previous writer releases it" & label)
            check(writerToken.IsCancellationRequested AndAlso SameManagementTarget(target, _managedInstance.Target) AndAlso
                protectedFiles.All(Function(pair) File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),
                "shared-folder rerouting preserves the selected native binding and all expected configuration and archive bytes" & label)
            diagnostics("changedFiles") = New JArray(protectedFiles.Where(Function(pair) Not File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)).Select(Function(pair) pair.Key))
            diagnostics("writerCancelled") = writerToken.IsCancellationRequested
            File.WriteAllText(Path.Combine(directory, "reroute-" & target.Kind.ToString() & ".json"), diagnostics.ToString())
        Next
    End Function
End Class
