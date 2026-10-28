Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyModWriteStartUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim focus = LocalModCheckGetNativeFocus(), observations As New JObject
        Dim host As New Border With {.Width = 900, .Height = 700}, layout = ControlLayout(host)
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                     File.WriteAllText(Path.Combine(directory, "mod-write-start.json"), observations.ToString())
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 700)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckModWriteStartAsync(directory, layout, check)
                ' This complete native matrix has 36 cases and 144 assertions.
                ' Retain progress if the bounded total budget is ever exhausted.
                If Not PumpMotionUntil(Function() work.IsCompleted, 180000) Then Throw New TimeoutException("Mod write startup checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "mod-write-start.json"), observations.ToString())
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
    Private Async Function CheckModWriteStartAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim details As New JArray
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _managementMotion.Finish() : _categoryMotion.Finish() : layout()
                               End Sub
        For launcher = 0 To 2
            For Each dark In {False, True}
                For Each animated In {False, True}
                    For scenario = 0 To 2
                        _motionPreview = False
                        Dim folders = PrepareLocalModFixture(Path.Combine(directory, "write-start-" & scenario))
                        FolderList.SelectedItem = folders(launcher)
                        Dim target = CurrentInstallTarget(), native = InstalledInstanceCatalog.Read(target, "Fabric 实例", CancellationToken.None)
                        SetLocalModIsolationForChecks(native, True)
                        Dim plain = InstalledInstanceCatalog.Read(target, "我的原版", CancellationToken.None)
                        LocalModJarForChecks(Path.Combine(plain.RunDirectory, "mods/kept.jar"), "write_start_kept")
                        Dim input = LocalModJarForChecks(Path.Combine(Path.GetDirectoryName(target.Root), "incoming/arriving.jar"), "write_start_added")
                        ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(True) : settle()
                        SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = native.Name)) : settle()
                        ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : settle()
                        IO.Directory.CreateDirectory(Path.Combine(target.Root, ".vanilla-downloader"))
                        File.WriteAllBytes(Path.Combine(target.Root, ".vanilla-downloader/install.lock"), {})
                        Dim originalOwner = _localModInstance, operationGeneration = _localModOperationGeneration, taskGeneration = _taskGeneration
                        If SameGameRoot(originalOwner.RunDirectory, plain.RunDirectory) Then Throw New InvalidOperationException("Write startup fixture requires different native directories.")
                        Dim files = DirectoryFilesForModUpdateChecks(Path.GetDirectoryName(target.Root))
                        Dim realImporter = _localModImporter, realProvider = _localModProvider
                        Dim gate As New TaskCompletionSource(Of InstalledInstance)(TaskCreationOptions.RunContinuationsAsynchronously)
                        _managementFolderReader = Function(instance, token) gate.Task
                        Dim opened = 0
                        _managementOpenFolder = Sub(folder) opened += 1
                        RestoreAppearance(New JObject From {{"darkMode", dark}}) : _motionPreview = animated : settle()
                        Dim oldRead = OpenLocalModFolderAsync(), retired = _localModFolderRequest
                        Dim label = "/launcher=" & target.Kind.ToString() & "/dark=" & dark & "/motion=" & animated & "/case=" & scenario
                        Dim reserved = False, calls = 0, expectedAdded As String = Nothing
                        Select Case scenario
                            Case 0
                                Dim actualOwner As InstalledInstance = Nothing, acceptedFeedback As String = Nothing
                                _localModImporter = Function(instance, paths, token)
                                                        calls += 1 : actualOwner = instance : Return realImporter(instance, paths, token)
                                                    End Function
                                Using registration = retired.Token.Register(Sub()
                                                                               reserved = _localModWriting AndAlso _localModWriteOwner Is originalOwner AndAlso _localModWriteRequest IsNot Nothing
                                                                               BackFromLocalMods() : settle() : BackFromManagement(Nothing, Nothing) : settle()
                                                                               SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = plain.Name)) : settle()
                                                                               ShowLocalMods(Nothing, Nothing) : settle() : acceptedFeedback = LocalModStatus.Text
                                                                           End Sub)
                                    Await ImportLocalModFilesAsync({input})
                                    gate.SetResult(originalOwner) : Await oldRead : settle()
                                    expectedAdded = Path.Combine(originalOwner.RunDirectory, "mods/arriving.jar")
                                    Dim wrongDestination = Path.Combine(plain.RunDirectory, "mods/arriving.jar")
                                    details.Add(New JObject From {{"case", label}, {"chosenInstance", originalOwner.Name}, {"actualInstance", actualOwner?.Name},
                                        {"expectedFile", expectedAdded}, {"expectedExists", File.Exists(expectedAdded)}, {"otherFile", wrongDestination}, {"otherExists", File.Exists(wrongDestination)}})
                                    check(reserved, "a Mod import binds its original owner and writer request before cancelling old reads" & label)
                                    check(calls = 1 AndAlso actualOwner Is originalOwner AndAlso File.Exists(expectedAdded) AndAlso File.ReadAllBytes(expectedAdded).SequenceEqual(File.ReadAllBytes(input)) AndAlso
                                        Not File.Exists(wrongDestination), "a real checked import publishes only into the instance chosen before return and navigation" & label)
                                    check(_localModInstance.Name = plain.Name AndAlso _localModReadSucceeded AndAlso _localMods.Count = 1 AndAlso _localMods(0).ModIds.Contains("write_start_kept") AndAlso
                                        LocalModStatus.Text = acceptedFeedback AndAlso LocalModInputAllowed() AndAlso LocalModImport.IsEnabled AndAlso opened = 0 AndAlso _taskGeneration = taskGeneration,
                                        "the newly viewed different directory retains its own Mod rows and feedback while the original import completes" & label)
                                End Using
                            Case 1
                                Dim reads = 0, nested As Task = Nothing, refresh As Task = Nothing, deferred = False
                                Dim writeGate As New TaskCompletionSource(Of LocalModActionResult)(TaskCreationOptions.RunContinuationsAsynchronously)
                                _localModProvider = Function(instance, token)
                                                        reads += 1 : Return realProvider(instance, token)
                                                    End Function
                                _localModImporter = Function(instance, paths, token)
                                                        calls += 1 : Return writeGate.Task
                                                    End Function
                                Using registration = retired.Token.Register(Sub()
                                                                               reserved = _localModWriting AndAlso _localModWriteOwner Is originalOwner AndAlso _localModWriteRequest IsNot Nothing
                                                                               refresh = LoadLocalModsAsync(True, refreshInstance:=True)
                                                                               nested = ImportLocalModFilesAsync({input})
                                                                               deferred = reads = 0 AndAlso _localModWaitingForWrite AndAlso nested.IsCompleted
                                                                           End Sub)
                                    Dim writing = ImportLocalModFilesAsync({input})
                                    check(reserved AndAlso deferred, "writer startup defers same-directory reads and rejects a nested write before notifying cancellation" & label)
                                    check(calls = 1 AndAlso _localModWriting AndAlso _localModWriteOwner Is originalOwner AndAlso _localModOperationGeneration = operationGeneration + 1,
                                        "one accepted Mod action retains one writer generation and request throughout startup" & label)
                                    gate.SetResult(originalOwner) : Await oldRead
                                    writeGate.SetResult(New LocalModActionResult(Path.Combine(originalOwner.RunDirectory, "mods"), 0, 1))
                                    Await nested : Await refresh : Await writing : settle()
                                    check(reads = 1 AndAlso Not _localModWriting AndAlso _localModWriteRequest Is Nothing AndAlso _localModReadSucceeded AndAlso
                                        LocalModStatus.Text.Contains("跳过 1") AndAlso LocalModImport.IsEnabled AndAlso LocalModInputAllowed() AndAlso opened = 0,
                                        "the single controlled writer performs one native completion refresh and restores ordinary Mod actions" & label)
                                End Using
                            Case Else
                                Dim cancelled = False
                                _localModImporter = Function(instance, paths, token)
                                                        calls += 1 : Return Task.FromResult(New LocalModActionResult(Path.Combine(instance.RunDirectory, "mods"), 0, 1))
                                                    End Function
                                Using registration = retired.Token.Register(Sub()
                                                                               reserved = _localModWriting AndAlso _localModWriteOwner Is originalOwner AndAlso _localModWriteRequest IsNot Nothing
                                                                               CancelLocalModOperation(Nothing, Nothing)
                                                                               cancelled = _localModWriteRequest IsNot Nothing AndAlso _localModWriteRequest.IsCancellationRequested
                                                                           End Sub)
                                    Await ImportLocalModFilesAsync({input})
                                    gate.SetResult(originalOwner) : Await oldRead : settle()
                                    check(reserved AndAlso cancelled, "a cancellation during writer startup addresses the already reserved original request" & label)
                                    check(calls = 0 AndAlso _localModOperationGeneration = operationGeneration + 1, "startup cancellation prevents the file operation delegate from running" & label)
                                    check(Not _localModWriting AndAlso _localModWriteRequest Is Nothing AndAlso _localModReadSucceeded AndAlso LocalModStatus.Text.Contains("取消") AndAlso
                                        LocalModImport.IsEnabled AndAlso LocalModInputAllowed() AndAlso opened = 0, "startup cancellation retains its outcome and permits an ordinary retry after native reread" & label)
                                End Using
                        End Select
                        Dim finalFiles = DirectoryFilesForModUpdateChecks(Path.GetDirectoryName(target.Root))
                        details.Add(New JObject From {{"case", label}, {"existingFiles", files.Count}, {"finalFiles", finalFiles.Count},
                            {"added", New JArray(finalFiles.Keys.Where(Function(path) Not files.ContainsKey(path)))},
                            {"changed", New JArray(files.Keys.Where(Function(path) Not finalFiles.ContainsKey(path) OrElse Not finalFiles(path).SequenceEqual(files(path))))}})
                        check(finalFiles.Count = files.Count + If(scenario = 0, 1, 0) AndAlso files.All(Function(item) finalFiles.ContainsKey(item.Key) AndAlso finalFiles(item.Key).SequenceEqual(item.Value)),
                            "writer startup preserves every existing game and native configuration byte and adds only the expected file count" & label)
                    Next
                Next
            Next
        Next
        File.WriteAllText(Path.Combine(directory, "mod-write-start-details.json"), details.ToString())
    End Function
End Class
