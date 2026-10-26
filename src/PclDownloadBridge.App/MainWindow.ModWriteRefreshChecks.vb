Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyModWriteRefreshUi(directory As String, passed As List(Of String))
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
                Dim work = owner.CheckModWriteRefreshAsync(directory, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 45000) Then Throw New TimeoutException("Mod write refresh did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "mod-write-refresh.json"), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            owner._closing = True : owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner._managementMotion.Finish()
            host.Child = Nothing : owner.Content = original : SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub

    Private Async Function CheckModWriteRefreshAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : _managementMotion.Finish() : layout()
                               End Sub
        For launcher = 0 To 2
            For Each animated In {False, True}
                _motionPreview = False
                Dim folders = PrepareLocalModFixture(directory), game = folders(0).Location
                LocalModJarForChecks(Path.Combine(game, "mods/shared.jar"), "native_refresh_shared")
                FolderList.SelectedItem = folders(launcher)
                Dim target = CurrentInstallTarget(), native = InstalledInstanceCatalog.Read(target, "Fabric 实例", CancellationToken.None)
                Dim config = Path.GetFullPath(SetLocalModIsolationForChecks(native, True))
                Dim protectedFiles = IO.Directory.GetFiles(Path.GetDirectoryName(game), "*", SearchOption.AllDirectories).ToDictionary(Function(path) path, Function(path) File.ReadAllBytes(path))
                ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(True) : settle()
                SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = native.Name)) : settle()
                ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                _motionPreview = animated : RestoreAppearance(New JObject From {{"darkMode", animated}})
                Dim label = "/launcher=" & target.Kind.ToString() & "/motion-dark=" & animated
                Dim realImporter = _localModImporter, reads As New List(Of String), nativeReads = 0
                _localModInstanceReader = Function(instance, token)
                                              nativeReads += 1
                                              Return Task.FromResult(InstalledInstanceCatalog.Read(instance.Target, instance.Name, token))
                                          End Function
                _localModProvider = Function(instance, token)
                                        reads.Add(instance.RunDirectory)
                                        Return Task.FromResult(LocalModCatalog.Scan(instance, token))
                                    End Function
                Dim gate As New TaskCompletionSource(Of LocalModActionResult)(TaskCreationOptions.RunContinuationsAsynchronously), tokenSeen As CancellationToken
                _localModImporter = Function(instance, files, token)
                                        tokenSeen = token : Return gate.Task
                                    End Function
                Dim frozen = _localModInstance, writing = ImportLocalModFilesAsync({Path.Combine(frozen.RunDirectory, "mods/first.jar")})
                SetLocalModIsolationForChecks(frozen, False) : protectedFiles(config) = File.ReadAllBytes(config)
                CancelLocalModOperation(Nothing, Nothing) : gate.SetCanceled()
                Await writing : settle()
                check(tokenSeen.IsCancellationRequested AndAlso nativeReads = 1 AndAlso _localModReadSucceeded AndAlso
                    _managedInstance.Name = frozen.Name AndAlso SameGameRoot(_managedInstance.RunDirectory, game) AndAlso _localMods.Count = 1 AndAlso
                    LocalModSharedHint.IsVisible AndAlso StatusText.Text.Contains(frozen.Name) AndAlso StatusText.Text.Contains("取消"),
                    "completion rereads unobserved native isolation changes and retains the frozen operation outcome" & label)
                check(reads.Count = 1 AndAlso SameGameRoot(reads(0), game) AndAlso Not _localModWriting AndAlso LocalModImport.IsEnabled,
                    "post-write catalogue IO targets only the freshly resolved shared directory without scanning the retired private folder" & label)
                _localModImporter = realImporter
                Await ImportLocalModFilesAsync({Path.Combine(game, "mods/shared.jar")}) : settle()
                check(_localModReadSucceeded AndAlso LocalModStatus.Text.Contains("跳过 1") AndAlso LocalModImport.IsEnabled,
                    "immediate real idempotent retry uses the newly shared native snapshot" & label)

                ' Recover the baseline too, so later observations remain independent.
                RefreshLocalMods(Nothing, New RoutedEventArgs()) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                gate = New TaskCompletionSource(Of LocalModActionResult)(TaskCreationOptions.RunContinuationsAsynchronously)
                _localModImporter = Function(instance, files, token) gate.Task
                writing = ImportLocalModFilesAsync({Path.Combine(game, "mods/shared.jar")})
                CorruptModContextForChecks(config, target.Kind) : protectedFiles(config) = File.ReadAllBytes(config)
                gate.SetResult(New LocalModActionResult(Path.Combine(game, "mods"), 0, 1))
                Await writing : settle()
                check(Not _localModReadSucceeded AndAlso Not LocalModImport.IsEnabled AndAlso LocalModRefresh.IsEnabled AndAlso
                    LocalModStatus.Text.Contains("无法读取 Mod 列表") AndAlso StatusText.Text.Contains("完成") AndAlso StatusText.Text.Contains(frozen.Name),
                    "unobserved invalid native configuration exposes a retryable read failure without hiding the completed write outcome" & label)
                SetLocalModIsolationForChecks(_managedInstance, True) : protectedFiles(config) = File.ReadAllBytes(config)
                RefreshLocalMods(Nothing, New RoutedEventArgs()) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                _localModImporter = realImporter
                Await ImportLocalModFilesAsync({Path.Combine(frozen.InstancePath, "mods/first.jar")}) : settle()
                check(_localModReadSucceeded AndAlso SameGameRoot(_managedInstance.RunDirectory, frozen.InstancePath) AndAlso _localMods.Count = 4 AndAlso
                    LocalModStatus.Text.Contains("跳过 1") AndAlso Not LocalModSharedHint.IsVisible,
                    "same-page native repair and real retry restore the private directory after completion refresh fails" & label)

                Dim reread As New TaskCompletionSource(Of InstalledInstance)(TaskCreationOptions.RunContinuationsAsynchronously), readToken As CancellationToken, readCalled = False
                Dim oldSnapshot = _managedInstance
                _localModInstanceReader = Function(instance, token)
                                              readCalled = True : readToken = token : Return reread.Task
                                          End Function
                _localModImporter = Function(instance, files, token) Task.FromResult(New LocalModActionResult(Path.Combine(instance.RunDirectory, "mods"), 0, 1))
                writing = ImportLocalModFilesAsync({Path.Combine(frozen.InstancePath, "mods/first.jar")})
                Dim pending = readCalled AndAlso _localModScanning AndAlso Not writing.IsCompleted
                Dim returned = NavigateTitleBack() : settle()
                ShowSettings(Nothing, Nothing) : settle()
                _localModInstanceReader = Function(instance, token) Task.FromResult(InstalledInstanceCatalog.Read(instance.Target, instance.Name, token))
                ShowManagement(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _managementScanning) : settle()
                If ManagementDetailPage.Visibility = Visibility.Visible Then BackFromManagement(Nothing, Nothing)
                settle() : SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = "我的原版")) : settle()
                ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                Dim accepted = _localModInstance, entries = _localMods, generation = _localModGeneration
                reread.SetResult(oldSnapshot) : Await writing : settle()
                check(pending AndAlso returned AndAlso readToken.IsCancellationRequested AndAlso _localModGeneration = generation AndAlso
                    _localModInstance Is accepted AndAlso _localMods Is entries AndAlso _managedInstance.Name = "我的原版" AndAlso
                    LocalModsPage.IsVisible AndAlso _localModReadSucceeded AndAlso LocalModImport.IsEnabled,
                    "return, settings and another instance retire a pending post-write native read even when it ignores cancellation" & label)
                check(protectedFiles.All(Function(pair) File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),
                    "native completion refresh, controlled outcomes and real no-op retries preserve every archive and expected configuration byte" & label)
            Next
        Next
    End Function
End Class
