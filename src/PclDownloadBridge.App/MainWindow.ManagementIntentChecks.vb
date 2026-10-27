Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyManagementIntentUi(directory As String, passed As List(Of String))
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
                Dim work = owner.CheckManagementIntentAsync(directory, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 60000) Then Throw New TimeoutException("Management intent checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "management-intent.json"), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            owner._closing = True : owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._managementMotion.Finish() : owner._categoryMotion.Finish()
            host.Child = Nothing : owner.Content = original
            SynchronizationContext.SetSynchronizationContext(previous) : LocalModCheckSetNativeFocus(focus)
        End Try
    End Sub

    Private Async Function CheckManagementIntentAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _managementMotion.Finish() : _categoryMotion.Finish() : layout()
                               End Sub
        For launcher = 0 To 2
            For Each dark In {False, True}
                For Each animated In {False, True}
                    _motionPreview = False
                    Dim folders = PrepareManagementFixture(directory)
                    FolderList.SelectedItem = folders(launcher)
                    ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(True) : settle()
                    Dim plain = _managementInstances.Single(Function(item) item.Name = "我的原版")
                    Dim fabric = _managementInstances.Single(Function(item) item.Name = "Fabric 实例")
                    SelectManagedInstance(plain) : settle()
                    IO.Directory.CreateDirectory(Path.Combine(plain.RunDirectory, "mods"))
                    Dim files = DirectoryFilesForModUpdateChecks(Path.GetDirectoryName(plain.Target.Root)), taskGeneration = _taskGeneration
                    Dim realProvider = _managementProvider, calls = 0
                    Dim button = ManagedFolderActions.Children.OfType(Of PclButton)().Single(Function(item) CStr(item.Tag) = "mods")
                    Dim actions As New List(Of String)
                    _managementOpenFolder = Sub(folder) actions.Add(folder)
                    _managementRepairReader = Function(instance, token)
                                                  calls += 1
                                                  Return Task.FromException(Of InstalledInstance)(New IOException("stale repair entry executed"))
                                              End Function
                    RestoreAppearance(New JObject From {{"darkMode", dark}}) : _motionPreview = animated : settle()
                    Dim label = "/launcher=" & plain.Target.Kind.ToString() & "/dark=" & dark & "/motion=" & animated
                    Dim oldGate As New TaskCompletionSource(Of InstalledInstance)(TaskCreationOptions.RunContinuationsAsynchronously), returned = False
                    _managementFolderReader = Async Function(instance, token)
                                                  Try
                                                      Return Await oldGate.Task
                                                  Finally
                                                      returned = True
                                                  End Try
                                              End Function
                    OpenManagedFolder(button, Nothing)
                    Dim oldRequest = _managementFolderRequest
                    Using registration = oldRequest.Token.Register(Sub() SelectManagedInstance(fabric))
                        Await PrepareManagedRepairAsync() : settle()
                        check(calls = 0 AndAlso _managedInstance Is fabric AndAlso ManagementDetailPage.IsVisible AndAlso Not _managementPreparing AndAlso _managementRequest Is Nothing,
                            "a retired repair click cannot reread an instance selected by its cancellation notification" & label)
                        oldGate.SetResult(plain) : Await WaitPackUiAsync(Function() returned) : Await Task.Yield() : settle()
                        check(_managedInstance Is fabric AndAlso Not ManagedRepair.IsEnabled AndAlso ManagedRepairStatus.Text.Contains("暂不支持") AndAlso
                            Not ManagedRepairStatus.Text.Contains("stale") AndAlso actions.Count = 0 AndAlso _taskGeneration = taskGeneration AndAlso Not DownloadPage.IsVisible AndAlso ManagementViewHost.IsHitTestVisible,
                            "the newly selected component instance retains its own repair feedback and ordinary page handoff" & label)
                    End Using

                    BackFromManagement(Nothing, Nothing) : settle() : SelectManagedInstance(plain) : settle()
                    For Each repair In {True, False}
                        calls = 0 : actions.Clear() : _managementProblem = Nothing : _managementProvider = realProvider
                        _managementProvider = Function(binding, token)
                                                  calls += 1
                                                  Return Task.FromException(Of List(Of InstalledInstance))(New IOException("stale management scan executed"))
                                              End Function
                        oldGate = New TaskCompletionSource(Of InstalledInstance)(TaskCreationOptions.RunContinuationsAsynchronously) : returned = False
                        Dim newGate As New TaskCompletionSource(Of InstalledInstance)(TaskCreationOptions.RunContinuationsAsynchronously)
                        _managementFolderReader = Async Function(instance, token)
                                                      Try
                                                          Return Await oldGate.Task
                                                      Finally
                                                          returned = True
                                                      End Try
                                                  End Function
                        OpenManagedFolder(button, Nothing) : oldRequest = _managementFolderRequest
                        Dim newest As CancellationTokenSource = Nothing, directoryReads = 0
                        Using registration = oldRequest.Token.Register(Sub()
                                                                           _managementFolderReader = Function(instance, token)
                                                                                                         directoryReads += 1 : Return newGate.Task
                                                                                                     End Function
                                                                           OpenManagedFolder(button, Nothing) : newest = _managementFolderRequest
                                                                       End Sub)
                            If repair Then Await PrepareManagedRepairAsync() Else Await ScanManagementAsync(True)
                            check(calls = 0 AndAlso directoryReads = 1 AndAlso _managementFolderRequest Is newest AndAlso Not newest.IsCancellationRequested AndAlso
                                Not _managementPreparing AndAlso Not _managementScanning AndAlso _managementRequest Is Nothing AndAlso ManagedFolderStatus.Text.Contains("正在读取"),
                                "a superseded " & If(repair, "repair", "refresh") & " entry preserves the newer directory request instead of taking its loading state" & label)
                            oldGate.SetResult(plain) : Await WaitPackUiAsync(Function() returned) : Await Task.Yield()
                            newGate.SetResult(plain) : Await WaitPackUiAsync(Function() _managementFolderRequest Is Nothing) : settle()
                            check(actions.SequenceEqual({Path.Combine(plain.RunDirectory, "mods")}) AndAlso ManagedFolderStatus.Text = "" AndAlso
                                Not ManagedRepairStatus.Text.Contains("stale") AndAlso _managementProblem Is Nothing AndAlso _taskGeneration = taskGeneration AndAlso
                                _managedInstance.Name = plain.Name AndAlso Not DownloadPage.IsVisible AndAlso ManagementViewHost.IsHitTestVisible,
                                "only the newer directory action completes after the retired " & If(repair, "repair", "refresh") & " click" & label)
                        End Using
                        _managementProblem = Nothing : _managementProvider = realProvider
                        RenderManagedInstance() : RefreshManagementStatus() : settle()
                    Next
                    Dim finalFiles = DirectoryFilesForModUpdateChecks(Path.GetDirectoryName(plain.Target.Root))
                    check(finalFiles.Count = files.Count AndAlso files.All(Function(item) finalFiles.ContainsKey(item.Key) AndAlso finalFiles(item.Key).SequenceEqual(item.Value)),
                        "repair and refresh intent checks preserve every native configuration and game file byte" & label)
                Next
            Next
        Next
    End Function
End Class
