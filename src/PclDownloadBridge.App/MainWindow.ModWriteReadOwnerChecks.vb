Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyModWriteReadOwnerUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim host As New Border With {.Width = 900, .Height = 700}, observations As New JObject
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                     File.WriteAllText(Path.Combine(directory, "mod-write-read-owner.json"), observations.ToString())
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 700)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckModWriteReadOwnerAsync(directory, ControlLayout(host), check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 180000) Then Throw New TimeoutException("Mod write read ownership checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            Dim failed = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failed.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failed))
        Finally
            owner._closing = True : owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._managementMotion.Finish() : owner._categoryMotion.Finish()
            host.Child = Nothing : owner.Content = original : SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub

    Private Async Function CheckModWriteReadOwnerAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _managementMotion.Finish() : _categoryMotion.Finish() : _titleNavigation.Finish() : layout()
                               End Sub
        For launcher = 0 To 2
            For Each useShared In {False, True}
                For scenario = 0 To 5
                    _motionPreview = False : _surfaceMotion.PauseForChecks = False : _managementMotion.PauseForChecks = False
                    Dim folders = PrepareLocalModFixture(Path.Combine(directory, "native-" & launcher & "-" & useShared & "-" & scenario)), game = folders(0).Location
                    LocalModJarForChecks(Path.Combine(game, "mods/useShared.jar"), "read_owner_shared")
                    FolderList.SelectedItem = folders(launcher)
                    Dim target = CurrentInstallTarget(), native = InstalledInstanceCatalog.Read(target, "Fabric 实例", CancellationToken.None)
                    SetLocalModIsolationForChecks(native, Not useShared)
                    _managementProvider = Function(binding, token) Task.FromResult(InstalledInstanceCatalog.Scan(binding, token))
                    ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(True) : settle()
                    SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = native.Name)) : settle()
                    ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                    Dim instance = _localModInstance, files = DirectoryFilesForModUpdateChecks(Path.GetDirectoryName(game)), taskGeneration = _taskGeneration
                    Dim sourceFile = _localMods.First(Function(item) item.CanToggle AndAlso item.IsArchiveValid AndAlso String.IsNullOrEmpty(item.Problem) AndAlso item.Enabled).FilePath
                    Dim realImporter = _localModImporter, realMods = _localModProvider
                    Dim writeGate As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously), readGate = ListGate(Of LocalModEntry)()
                    Dim folderGate As New TaskCompletionSource(Of InstalledInstance)(TaskCreationOptions.RunContinuationsAsynchronously), chosen = scenario, opened = 0
                    _managementOpenFolder = Sub(path) opened += 1
                    _localModImporter = Async Function(frozen, paths, token)
                                            Await writeGate.Task
                                            Return LocalModActions.ImportFiles(frozen, paths, token)
                                        End Function
                    RestoreAppearance(New JObject From {{"darkMode", useShared}}) : _motionPreview = scenario Mod 2 = 1
                    _surfaceMotion.PauseForChecks = _motionPreview : _managementMotion.PauseForChecks = _motionPreview
                    Dim writing = ImportLocalModFilesAsync({sourceFile}), parent As New CancellationTokenSource(), parentToken = parent.Token
                    _managementRequest = parent
                    Dim nested As Task = Task.CompletedTask, latest As CancellationTokenSource = Nothing, latestToken As CancellationToken
                    Dim expectedRows = _localMods, expectedStatus As String = Nothing, expectedMain = ActiveSurface()
                    Dim expectedSurfaceClock = _surfaceMotion.Transition, expectedChildClock = _managementMotion.Transition
                    Dim label = "/launcher=" & target.Kind.ToString() & "/useShared=" & useShared & "/case=" & scenario
                    Using registration = parentToken.Register(Sub()
                                                                 Select Case chosen
                                                                     Case 0, 1, 2
                                                                         _localModProvider = Function(current, token)
                                                                                                 If chosen = 0 Then Return readGate.Task
                                                                                                 If chosen = 2 Then Return Task.FromException(Of List(Of LocalModEntry))(New IOException("controlled newer reload failure"))
                                                                                                 Return Task.FromResult(_localMods.ToList())
                                                                                             End Function
                                                                         nested = LoadLocalModsAsync(True) : latest = _localModRequest
                                                                         If latest IsNot Nothing Then latestToken = latest.Token
                                                                         If chosen = 1 Then LocalModStatus.Text = "newer completed reload"
                                                                     Case 3 : Throw New IOException("controlled owned refresh failure")
                                                                     Case 4 : ShowSettings(Nothing, Nothing)
                                                                     Case 5
                                                                         _managementFolderReader = Function(current, token) folderGate.Task
                                                                         nested = OpenLocalModFolderAsync() : latest = _localModFolderRequest
                                                                         If latest IsNot Nothing Then latestToken = latest.Token
                                                                 End Select
                                                                 StatusText.Text = "newer action owns global feedback"
                                                                 expectedRows = _localMods : expectedStatus = LocalModStatus.Text : expectedMain = ActiveSurface()
                                                                 expectedSurfaceClock = _surfaceMotion.Transition : expectedChildClock = _managementMotion.Transition
                                                                 If chosen = 4 OrElse chosen = 5 Then Throw New IOException("controlled superseded refresh failure")
                                                             End Sub)
                        writeGate.SetResult(True) : Await writing : layout()
                        check(Not _localModWriting AndAlso _localModWriteRequest Is Nothing AndAlso parentToken.IsCancellationRequested AndAlso _localMods Is expectedRows AndAlso
                            If(chosen = 3, LocalModStatus.Text.Contains("controlled owned refresh") AndAlso StatusText.Text.Contains("完成"),
                                LocalModStatus.Text = expectedStatus AndAlso StatusText.Text = "newer action owns global feedback" AndAlso ActiveSurface() Is expectedMain AndAlso
                                _surfaceMotion.Transition Is expectedSurfaceClock AndAlso _managementMotion.Transition Is expectedChildClock) AndAlso
                            If(chosen = 0 OrElse chosen = 5, latest IsNot Nothing AndAlso Not latestToken.IsCancellationRequested, True),
                            "post-write refresh retains its own read identity instead of adopting a newer pending, completed or failed action" & label)
                        readGate.TrySetResult(_localMods.ToList()) : folderGate.TrySetResult(instance) : Await nested : Await Task.Yield() : settle()
                        check(Not _localModWriting AndAlso _localModRequest Is Nothing AndAlso _localModFolderRequest Is Nothing AndAlso _localModWriteRequest Is Nothing AndAlso
                            If(chosen = 3, LocalModStatus.Text.Contains("controlled owned refresh") AndAlso StatusText.Text.Contains("完成"), StatusText.Text = "newer action owns global feedback") AndAlso
                            ActiveSurface() Is expectedMain AndAlso PageSurface.IsHitTestVisible AndAlso ManagementViewHost.IsHitTestVisible AndAlso opened = If(chosen = 5, 1, 0) AndAlso
                            _taskGeneration = taskGeneration AndAlso files.All(Function(item) File.ReadAllBytes(item.Key).SequenceEqual(item.Value)),
                            "late refresh and directory completion preserve newer feedback, input and native useShared files" & label)
                    End Using
                    parent.Dispose() : _localModProvider = realMods : _localModImporter = realImporter
                    _managementFolderReader = Function(current, token) Task.FromResult(InstalledInstanceCatalog.Read(current.Target, current.Name, token))
                    _motionPreview = False : _surfaceMotion.PauseForChecks = False : _managementMotion.PauseForChecks = False
                    ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(True) : settle()
                    If LocalModsPage.IsVisible Then BackFromLocalMods() : settle()
                    SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = instance.Name)) : settle()
                    ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                    Await ImportLocalModFilesAsync({sourceFile}) : settle()
                    check(_localModReadSucceeded AndAlso SameManagementTarget(_localModInstance.Target, target) AndAlso _localModInstance.IsIsolated.GetValueOrDefault() = (Not useShared) AndAlso
                        LocalModStatus.Text.Contains("跳过 1") AndAlso LocalModImport.IsEnabled AndAlso _taskGeneration = taskGeneration AndAlso
                        files.All(Function(item) File.ReadAllBytes(item.Key).SequenceEqual(item.Value)),
                        "normal idempotent retry owns its refresh and outcome while preserving native isolation and existing files" & label)
                Next
            Next
        Next
    End Function
End Class

