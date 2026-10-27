Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyLocalModReadStartUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim focus = LocalModCheckGetNativeFocus(), observations As New JObject
        Dim host As New Border With {.Width = 900, .Height = 700}, layout = ControlLayout(host)
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 700)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckLocalModReadStartAsync(directory, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 90000) Then Throw New TimeoutException("Mod read startup checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "local-mod-read-start.json"), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            owner._closing = True : owner.CancelLocalModRequest() : owner.CancelManagementRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._managementMotion.Finish() : owner._categoryMotion.Finish()
            host.Child = Nothing : owner.Content = original
            SynchronizationContext.SetSynchronizationContext(previous) : LocalModCheckSetNativeFocus(focus)
        End Try
    End Sub
    Private Async Function CheckLocalModReadStartAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _managementMotion.Finish() : _categoryMotion.Finish() : layout()
                               End Sub
        For launcher = 0 To 2
            For Each dark In {False, True}
                For Each animated In {False, True}
                    For scenario = 0 To 3
                        _motionPreview = False
                        Dim folders = PrepareLocalModFixture(Path.Combine(directory, "read-start-" & scenario))
                        FolderList.SelectedItem = folders(launcher)
                        Dim target = CurrentInstallTarget(), native = InstalledInstanceCatalog.Read(target, "Fabric 实例", CancellationToken.None)
                        SetLocalModIsolationForChecks(native, True)
                        Dim plain = InstalledInstanceCatalog.Read(target, "我的原版", CancellationToken.None)
                        LocalModJarForChecks(Path.Combine(plain.RunDirectory, "mods/kept.jar"), "read_start_kept")
                        ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(True) : settle()
                        SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = native.Name)) : settle()
                        ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                        Dim owner = _localModInstance, entries = _localMods.ToList(), files = DirectoryFilesForModUpdateChecks(Path.GetDirectoryName(target.Root))
                        Dim realProvider = _localModProvider, realReader = _localModInstanceReader, taskGeneration = _taskGeneration, opened = 0
                        _managementOpenFolder = Sub(folder) opened += 1
                        RestoreAppearance(New JObject From {{"darkMode", dark}}) : _motionPreview = animated : settle()
                        Dim label = "/launcher=" & target.Kind.ToString() & "/dark=" & dark & "/motion=" & animated & "/case=" & scenario
                        If scenario < 2 Then
                            Dim calls = 0, nested As Task = Nothing, newest As CancellationTokenSource = Nothing
                            Dim nextGate As New TaskCompletionSource(Of List(Of LocalModEntry))(TaskCreationOptions.RunContinuationsAsynchronously)
                            Dim supplyLatest As Func(Of InstalledInstance, CancellationToken, Task(Of List(Of LocalModEntry))) =
                                Function(instance, token)
                                    calls += 1
                                    If calls > 1 Then Return Task.FromException(Of List(Of LocalModEntry))(New IOException("stale outer Mod read executed"))
                                    Return nextGate.Task
                                End Function
                            If scenario = 0 Then
                                Dim oldGate As New TaskCompletionSource(Of List(Of LocalModEntry))(TaskCreationOptions.RunContinuationsAsynchronously)
                                _localModProvider = Function(instance, token) oldGate.Task
                                Dim oldRead = LoadLocalModsAsync(True), retired = _localModRequest
                                Using registration = retired.Token.Register(Sub()
                                                                               _localModProvider = supplyLatest
                                                                               nested = LoadLocalModsAsync(True, refreshInstance:=True) : newest = _localModRequest
                                                                           End Sub)
                                    Dim outgoing = LoadLocalModsAsync(True, refreshInstance:=True)
                                    oldGate.SetResult(New List(Of LocalModEntry)) : Await oldRead : Await outgoing
                                    check(calls = 1 AndAlso _localModRequest Is newest AndAlso Not newest.IsCancellationRequested AndAlso _localModScanning AndAlso
                                        LocalModStatus.Text.Contains("正在读取 Mod 列表"), "a superseded local refresh preserves the newer request and loading feedback" & label)
                                    nextGate.SetResult(entries) : Await nested : settle()
                                    check(_localMods Is entries AndAlso _localModReadSucceeded AndAlso Not _localModScanning AndAlso LocalModImport.IsEnabled AndAlso
                                        Not LocalModStatus.Text.Contains("stale") AndAlso LocalModInputAllowed(), "only the latest local refresh accepts its rows after the old read and outer click exit" & label)
                                End Using
                            Else
                                Dim rootGate As New TaskCompletionSource(Of List(Of InstalledInstance))(TaskCreationOptions.RunContinuationsAsynchronously)
                                _managementProvider = Function(binding, token) rootGate.Task
                                Dim rootRead = ScanManagementAsync(True), retired = _managementRequest
                                Using registration = retired.Token.Register(Sub()
                                                                               _localModProvider = supplyLatest
                                                                               nested = LoadLocalModsAsync(True) : newest = _localModRequest
                                                                           End Sub)
                                    Dim outgoing = LoadLocalModsAsync(True, refreshInstance:=True)
                                    rootGate.SetResult(New List(Of InstalledInstance)) : Await rootRead : Await outgoing
                                    check(calls = 1 AndAlso _localModRequest Is newest AndAlso Not newest.IsCancellationRequested AndAlso _localModScanning AndAlso
                                        LocalModStatus.Text.Contains("正在读取 Mod 列表"), "cancelling a management scan cannot make an older Mod refresh cancel the newer local read" & label)
                                    nextGate.SetResult(entries) : Await nested : settle()
                                    check(_localMods Is entries AndAlso _localModReadSucceeded AndAlso _managedInstance.Name = owner.Name AndAlso LocalModImport.IsEnabled AndAlso
                                        Not LocalModStatus.Text.Contains("stale") AndAlso LocalModInputAllowed(), "a read started during parent cancellation retains its accepted native binding and rows" & label)
                                End Using
                            End If
                        Else
                            Dim oldGate As New TaskCompletionSource(Of InstalledInstance)(TaskCreationOptions.RunContinuationsAsynchronously)
                            _managementFolderReader = Function(instance, token) oldGate.Task
                            Dim oldRead = OpenLocalModFolderAsync(), retired = _localModFolderRequest
                            If scenario = 2 Then
                                Dim calls = 0
                                Using registration = retired.Token.Register(Sub()
                                                                               BackFromLocalMods() : settle() : BackFromManagement(Nothing, Nothing) : settle()
                                                                               SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = plain.Name)) : settle()
                                                                               ShowLocalMods(Nothing, Nothing) : settle()
                                                                               _localModProvider = Function(instance, token)
                                                                                                       calls += 1 : Return Task.FromException(Of List(Of LocalModEntry))(New IOException("old refresh read the newly selected instance"))
                                                                                                   End Function
                                                                           End Sub)
                                    Await LoadLocalModsAsync(True, refreshInstance:=True)
                                    oldGate.SetResult(owner) : Await oldRead : settle()
                                    check(calls = 0 AndAlso _localModInstance.Name = plain.Name AndAlso _managedInstance.Name = plain.Name AndAlso Not _localModScanning AndAlso
                                        _localModRequest Is Nothing AndAlso _localModReadSucceeded, "return and instance switching cannot redirect an old Mod refresh into the newly opened instance" & label)
                                    check(_localMods.Count = 1 AndAlso _localMods(0).ModIds.Contains("read_start_kept") AndAlso Not LocalModStatus.Text.Contains("old refresh") AndAlso
                                        LocalModImport.IsEnabled AndAlso LocalModInputAllowed() AndAlso opened = 0, "the newly viewed instance keeps its own local rows and feedback after retired refresh startup" & label)
                                End Using
                            Else
                                Dim readsDuringWrite = 0, nestedRead As Task = Nothing, writing As Task = Nothing, writeRequest As CancellationTokenSource = Nothing
                                Dim writeGate As New TaskCompletionSource(Of LocalModActionResult)(TaskCreationOptions.RunContinuationsAsynchronously)
                                _localModImporter = Function(instance, paths, token) writeGate.Task
                                Using registration = retired.Token.Register(Sub()
                                                                               nestedRead = LoadLocalModsAsync(True)
                                                                               settle()
                                                                               writing = ImportLocalModFilesAsync({_localMods.First(Function(item) item.CanToggle).FilePath})
                                                                               writeRequest = _localModWriteRequest
                                                                               _localModInstanceReader = Function(instance, token)
                                                                                                             If _localModWriting Then readsDuringWrite += 1
                                                                                                             Return realReader(instance, token)
                                                                                                         End Function
                                                                           End Sub)
                                    Await LoadLocalModsAsync(True, refreshInstance:=True)
                                    oldGate.SetResult(owner) : Await oldRead
                                    check(readsDuringWrite = 0 AndAlso _localModWriting AndAlso _localModWriteRequest Is writeRequest AndAlso Not writeRequest.IsCancellationRequested AndAlso
                                        LocalModStatus.Text.Contains("正在导入"), "a superseded Mod refresh cannot reread native metadata or replace current writer feedback" & label)
                                    writeGate.SetResult(New LocalModActionResult(Path.Combine(owner.RunDirectory, "mods"), 0, 1))
                                    Await nestedRead : Await writing : settle()
                                    check(_localModReadSucceeded AndAlso Not _localModWriting AndAlso _localModWriteRequest Is Nothing AndAlso LocalModStatus.Text.Contains("跳过 1") AndAlso
                                        LocalModImport.IsEnabled AndAlso LocalModInputAllowed(), "the newer controlled writer still performs its completion refresh and restores ordinary Mod actions" & label)
                                End Using
                            End If
                        End If
                        Dim finalFiles = DirectoryFilesForModUpdateChecks(Path.GetDirectoryName(target.Root))
                        check(finalFiles.Count = files.Count AndAlso files.All(Function(item) finalFiles.ContainsKey(item.Key) AndAlso finalFiles(item.Key).SequenceEqual(item.Value)) AndAlso
                            _taskGeneration = taskGeneration AndAlso SameManagementTarget(_localModInstance.Target, target), "Mod refresh startup preserves every game and native launcher configuration byte" & label)
                    Next
                Next
            Next
        Next
    End Function
End Class
