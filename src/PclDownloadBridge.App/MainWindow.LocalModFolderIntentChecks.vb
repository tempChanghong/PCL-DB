Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyLocalModFolderIntentUi(directory As String, passed As List(Of String))
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
                Dim work = owner.CheckLocalModFolderIntentAsync(directory, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 60000) Then Throw New TimeoutException("Local folder intent checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "local-mod-folder-intent.json"), observations.ToString())
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
    Private Async Function CheckLocalModFolderIntentAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _managementMotion.Finish() : _categoryMotion.Finish() : layout()
                               End Sub
        For launcher = 0 To 2
            For Each dark In {False, True}
                For Each animated In {False, True}
                    _motionPreview = False
                    Dim folders = PrepareLocalModFixture(directory)
                    FolderList.SelectedItem = folders(launcher)
                    Dim target = CurrentInstallTarget(), native = InstalledInstanceCatalog.Read(target, "Fabric 实例", CancellationToken.None)
                    SetLocalModIsolationForChecks(native, True)
                    Dim plain = InstalledInstanceCatalog.Read(target, "我的原版", CancellationToken.None)
                    LocalModJarForChecks(Path.Combine(plain.RunDirectory, "mods/kept.jar"), "kept_folder_intent")
                    ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(True) : settle()
                    SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = native.Name)) : settle()
                    ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                    Dim files = DirectoryFilesForModUpdateChecks(Path.GetDirectoryName(target.Root)), generation = _localModGeneration, taskGeneration = _taskGeneration
                    Dim owner = _localModInstance, retained = _localMods, realProvider = _localModProvider, realImporter = _localModImporter
                    Dim actions As New List(Of String), reads = 0
                    _managementOpenFolder = Sub(folder) actions.Add(folder)
                    RestoreAppearance(New JObject From {{"darkMode", dark}}) : _motionPreview = animated : settle()
                    Dim label = "/launcher=" & target.Kind.ToString() & "/dark=" & dark & "/motion=" & animated
                    Dim oldGate As TaskCompletionSource(Of InstalledInstance) = Nothing
                    Dim beginOld As Func(Of Task) = Function()
                                                       oldGate = New TaskCompletionSource(Of InstalledInstance)(TaskCreationOptions.RunContinuationsAsynchronously)
                                                       _managementFolderReader = Function(instance, token) oldGate.Task
                                                       Return OpenLocalModFolderAsync()
                                                   End Function
                    Dim staleReader As Func(Of InstalledInstance, CancellationToken, Task(Of InstalledInstance)) =
                        Function(instance, token)
                            reads += 1 : Return Task.FromException(Of InstalledInstance)(New IOException("stale local folder read executed"))
                        End Function
                    Dim oldRead = beginOld(), retired = _localModFolderRequest, nested As Task = Nothing, newest As CancellationTokenSource = Nothing
                    Dim nextGate As New TaskCompletionSource(Of InstalledInstance)(TaskCreationOptions.RunContinuationsAsynchronously)
                    Using registration = retired.Token.Register(Sub()
                                                                   _managementFolderReader = Function(instance, token)
                                                                                                 reads += 1 : Return nextGate.Task
                                                                                             End Function
                                                                   nested = OpenLocalModFolderAsync() : newest = _localModFolderRequest
                                                               End Sub)
                        Dim outgoing = OpenLocalModFolderAsync()
                        check(reads = 1 AndAlso _localModFolderRequest Is newest AndAlso Not newest.IsCancellationRequested AndAlso LocalModStatus.Text.Contains("正在读取文件夹") AndAlso
                            _localMods Is retained AndAlso _localModGeneration = generation, "a local folder replacement retains the newest request without starting a duplicate read" & label)
                        oldGate.SetResult(owner) : Await oldRead
                        nextGate.SetResult(owner) : Await nested : Await outgoing : settle()
                        check(actions.SequenceEqual({Path.Combine(owner.RunDirectory, "mods")}) AndAlso _localModFolderRequest Is Nothing AndAlso LocalModStatus.Text = "" AndAlso
                            _localMods Is retained AndAlso _localModReadSucceeded AndAlso LocalModInputAllowed(), "the accepted local folder action alone opens the checked native directory and preserves its rows" & label)
                    End Using

                    reads = 0 : actions.Clear() : owner = _localModInstance
                    oldRead = beginOld() : retired = _localModFolderRequest
                    Dim updated = _localMods.ToList(), readGate As New TaskCompletionSource(Of List(Of LocalModEntry))(TaskCreationOptions.RunContinuationsAsynchronously)
                    Dim refreshing As Task = Nothing, readRequest As CancellationTokenSource = Nothing
                    _localModProvider = Function(instance, token) readGate.Task
                    Using registration = retired.Token.Register(Sub()
                                                                   refreshing = LoadLocalModsAsync(True, refreshInstance:=True) : readRequest = _localModRequest
                                                                   _managementFolderReader = staleReader
                                                               End Sub)
                        Dim outgoing = OpenLocalModFolderAsync()
                        oldGate.SetResult(owner) : Await oldRead : Await outgoing
                        check(reads = 0 AndAlso _localModRequest Is readRequest AndAlso Not readRequest.IsCancellationRequested AndAlso _localModScanning AndAlso
                            LocalModStatus.Text.Contains("正在读取 Mod 列表") AndAlso actions.Count = 0, "a retired folder click cannot replace the newer Mod refresh loading feedback or request" & label)
                        readGate.SetResult(updated) : Await refreshing : settle()
                        check(_localMods Is updated AndAlso _localModReadSucceeded AndAlso Not _localModScanning AndAlso LocalModImport.IsEnabled AndAlso LocalModInputAllowed() AndAlso
                            Not LocalModStatus.Text.Contains("stale") AndAlso SameManagementTarget(_localModInstance.Target, target), "the newer Mod refresh remains usable and accepts its own rows after old folder completion" & label)
                    End Using
                    _localModProvider = realProvider

                    reads = 0 : actions.Clear() : owner = _localModInstance
                    oldRead = beginOld() : retired = _localModFolderRequest
                    Dim writeGate As New TaskCompletionSource(Of LocalModActionResult)(TaskCreationOptions.RunContinuationsAsynchronously), writing As Task = Nothing, writeRequest As CancellationTokenSource = Nothing
                    _localModImporter = Function(instance, paths, token) writeGate.Task
                    Using registration = retired.Token.Register(Sub()
                                                                   writing = ImportLocalModFilesAsync({_localMods.First(Function(item) item.CanToggle).FilePath})
                                                                   writeRequest = _localModWriteRequest : _managementFolderReader = staleReader
                                                               End Sub)
                        Dim outgoing = OpenLocalModFolderAsync()
                        oldGate.SetResult(owner) : Await oldRead : Await outgoing
                        check(reads = 0 AndAlso _localModWriting AndAlso _localModWriteRequest Is writeRequest AndAlso Not writeRequest.IsCancellationRequested AndAlso LocalModCancel.IsEnabled AndAlso
                            LocalModStatus.Text.Contains("正在导入") AndAlso actions.Count = 0, "a retired folder click preserves the current Mod writer feedback and cancel action" & label)
                        writeGate.SetResult(New LocalModActionResult(Path.Combine(owner.RunDirectory, "mods"), 0, 1)) : Await writing : settle()
                        check(Not _localModWriting AndAlso _localModWriteRequest Is Nothing AndAlso _localModReadSucceeded AndAlso LocalModImport.IsEnabled AndAlso LocalModInputAllowed() AndAlso
                            LocalModStatus.Text.Contains("跳过 1") AndAlso actions.Count = 0, "the controlled Mod writer still refreshes and restores input after a superseded folder click" & label)
                    End Using
                    _localModImporter = realImporter

                    reads = 0 : actions.Clear() : owner = _localModInstance
                    oldRead = beginOld() : retired = _localModFolderRequest
                    Using registration = retired.Token.Register(Sub()
                                                                   BackFromLocalMods() : settle() : BackFromManagement(Nothing, Nothing) : settle()
                                                                   SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = plain.Name)) : settle()
                                                                   ShowLocalMods(Nothing, Nothing) : settle() : _managementFolderReader = staleReader
                                                               End Sub)
                        Dim outgoing = OpenLocalModFolderAsync()
                        oldGate.SetResult(owner) : Await oldRead : Await outgoing : settle()
                        check(reads = 0 AndAlso _localModInstance.Name = plain.Name AndAlso _managedInstance.Name = plain.Name AndAlso _localModFolderRequest Is Nothing AndAlso
                            _localModReadSucceeded AndAlso actions.Count = 0, "return and instance switching cannot make an old folder click read the new instance" & label)
                        check(_localMods.Count = 1 AndAlso _localMods(0).ModIds.Contains("kept_folder_intent") AndAlso Not LocalModStatus.Text.Contains("stale") AndAlso
                            LocalModInputAllowed() AndAlso LocalModImport.IsEnabled AndAlso SameManagementTarget(_localModInstance.Target, target) AndAlso _taskGeneration = taskGeneration,
                            "the newly opened instance keeps its accepted Mod list and feedback after the retired folder task exits" & label)
                    End Using
                    Dim finalFiles = DirectoryFilesForModUpdateChecks(Path.GetDirectoryName(target.Root))
                    check(finalFiles.Count = files.Count AndAlso files.All(Function(item) finalFiles.ContainsKey(item.Key) AndAlso finalFiles(item.Key).SequenceEqual(item.Value)),
                        "local folder intent checks preserve every game and native launcher configuration byte" & label)
                Next
            Next
        Next
    End Function
End Class
