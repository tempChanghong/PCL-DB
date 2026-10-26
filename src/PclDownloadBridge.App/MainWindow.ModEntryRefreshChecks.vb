Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyModEntryRefreshUi(directory As String, passed As List(Of String))
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
                Dim work = owner.CheckModEntryRefreshAsync(directory, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 45000) Then Throw New TimeoutException("Mod entry refresh did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "mod-entry-refresh.json"), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            owner._closing = True : owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner._managementMotion.Finish()
            host.Child = Nothing : owner.Content = original : SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub

    Private Async Function CheckModEntryRefreshAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : _managementMotion.Finish() : layout()
                               End Sub
        For launcher = 0 To 2
            For Each animated In {False, True}
                _motionPreview = False
                Dim folders = PrepareLocalModFixture(directory), game = folders(0).Location
                LocalModJarForChecks(Path.Combine(game, "mods/shared.jar"), "entry_shared")
                FolderList.SelectedItem = folders(launcher)
                Dim target = CurrentInstallTarget(), native = InstalledInstanceCatalog.Read(target, "Fabric 实例", CancellationToken.None)
                Dim config = Path.GetFullPath(SetLocalModIsolationForChecks(native, True))
                Dim protectedFiles = IO.Directory.GetFiles(Path.GetDirectoryName(game), "*", SearchOption.AllDirectories).ToDictionary(Function(path) path, Function(path) File.ReadAllBytes(path))
                ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(True) : settle()
                SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = native.Name)) : settle()
                Dim cached = _managedInstance
                SetLocalModIsolationForChecks(cached, False) : protectedFiles(config) = File.ReadAllBytes(config)
                _motionPreview = animated : RestoreAppearance(New JObject From {{"darkMode", animated}})
                ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                Dim label = "/launcher=" & target.Kind.ToString() & "/motion-dark=" & animated
                check(_managedInstance IsNot cached AndAlso _localModInstance Is _managedInstance AndAlso _localModReadSucceeded AndAlso
                    SameGameRoot(_managedInstance.RunDirectory, game) AndAlso _localMods.Count = 1 AndAlso LocalModSharedHint.IsVisible AndAlso
                    LocalModDirectory.Text = Path.Combine(game, "mods") AndAlso SameManagementTarget(_managedInstance.Target, target),
                    "entering from a cached detail resolves current native isolation before displaying an actionable Mods directory" & label)
                Await ImportLocalModFilesAsync({Path.Combine(game, "mods/shared.jar")}) : settle()
                check(_localModReadSucceeded AndAlso LocalModImport.IsEnabled AndAlso LocalModStatus.Text.Contains("跳过 1"),
                    "the first real idempotent import after entry uses the refreshed native snapshot without a manual list refresh" & label)

                RefreshLocalMods(Nothing, New RoutedEventArgs()) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                BackFromLocalMods() : settle()
                CorruptModContextForChecks(config, target.Kind) : protectedFiles(config) = File.ReadAllBytes(config)
                ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                check(Not _localModReadSucceeded AndAlso Not LocalModImport.IsEnabled AndAlso Not LocalModRows.IsEnabled AndAlso LocalModRefresh.IsEnabled AndAlso
                    LocalModStatus.Text.Contains("无法读取 Mod 列表") AndAlso LocalModsPage.IsVisible,
                    "entry with an unobserved invalid native configuration stays on a retryable error page with stale file actions disabled" & label)
                SetLocalModIsolationForChecks(_managedInstance, True) : protectedFiles(config) = File.ReadAllBytes(config)
                RefreshLocalMods(Nothing, New RoutedEventArgs()) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                Await ImportLocalModFilesAsync({Path.Combine(native.InstancePath, "mods/first.jar")}) : settle()
                check(_localModReadSucceeded AndAlso SameGameRoot(_managedInstance.RunDirectory, native.InstancePath) AndAlso _localMods.Count = 4 AndAlso
                    LocalModStatus.Text.Contains("跳过 1") AndAlso Not LocalModSharedHint.IsVisible,
                    "same-page retry after repairing entry configuration restores the private directory and accepts a real checked import" & label)

                BackFromLocalMods() : settle()
                Dim gate As New TaskCompletionSource(Of InstalledInstance)(TaskCreationOptions.RunContinuationsAsynchronously), called = False, returned = False, readToken As CancellationToken
                Dim retired = _managedInstance
                _localModInstanceReader = Async Function(instance, token)
                                              called = True : readToken = token
                                              Dim result = Await gate.Task
                                              returned = True : Return result
                                          End Function
                ShowLocalMods(Nothing, Nothing) : settle()
                Dim pending = called AndAlso _localModScanning AndAlso Not LocalModImport.IsEnabled
                Dim backed = NavigateTitleBack() : settle()
                ShowSettings(Nothing, Nothing) : settle()
                _localModInstanceReader = Function(instance, token) Task.FromResult(InstalledInstanceCatalog.Read(instance.Target, instance.Name, token))
                ShowManagement(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _managementScanning) : settle()
                If ManagementDetailPage.Visibility = Visibility.Visible Then BackFromManagement(Nothing, Nothing)
                settle() : SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = "我的原版")) : settle()
                ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                Dim accepted = _localModInstance, entries = _localMods, generation = _localModGeneration
                gate.SetResult(retired)
                If called Then Await WaitPackUiAsync(Function() returned)
                Await Task.Yield() : settle()
                check(pending AndAlso backed AndAlso readToken.IsCancellationRequested AndAlso _localModGeneration = generation AndAlso
                    _localModInstance Is accepted AndAlso _localMods Is entries AndAlso _managedInstance.Name = "我的原版" AndAlso
                    LocalModsPage.IsVisible AndAlso _localModReadSucceeded AndAlso LocalModImport.IsEnabled,
                    "return, settings and another instance retire a delayed entry read even if its provider ignores cancellation" & label)
                check(protectedFiles.All(Function(pair) File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),
                    "entry, native read failures, repair and checked no-op imports preserve every archive and expected configuration byte" & label)
            Next
        Next
    End Function
End Class
