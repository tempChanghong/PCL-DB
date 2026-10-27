Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyManagementRetirementUi(directory As String, passed As List(Of String))
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
                Dim work = owner.CheckManagementRetirementAsync(directory, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 60000) Then Throw New TimeoutException("Management retirement checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "management-retirement.json"), observations.ToString())
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

    Private Async Function CheckManagementRetirementAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
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
                    SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = "Fabric 实例")) : settle()
                    Dim target = CurrentInstallTarget(), owner = _managedInstance, realProvider = _managementProvider
                    Dim files = DirectoryFilesForModUpdateChecks(Path.GetDirectoryName(target.Root))
                    Dim items = InstalledInstanceCatalog.Scan(target, CancellationToken.None)
                    Dim button = ManagedFolderActions.Children.OfType(Of PclButton)().Single(Function(item) CStr(item.Tag) = "mods")
                    Dim actions As New List(Of String)
                    _managementOpenFolder = Sub(folder) actions.Add(folder)
                    RestoreAppearance(New JObject From {{"darkMode", dark}}) : _motionPreview = animated : settle()
                    Dim label = "/launcher=" & target.Kind.ToString() & "/dark=" & dark & "/motion=" & animated
                    Dim first As New TaskCompletionSource(Of InstalledInstance)(TaskCreationOptions.RunContinuationsAsynchronously)
                    Dim second As New TaskCompletionSource(Of InstalledInstance)(TaskCreationOptions.RunContinuationsAsynchronously)
                    Dim folderReturned = False, replacementFolderReads = 0
                    _managementFolderReader = Async Function(instance, token)
                                                  Try
                                                      Return Await first.Task
                                                  Finally
                                                      folderReturned = True
                                                  End Try
                                              End Function
                    OpenManagedFolder(button, Nothing)
                    Dim retiredFolder = _managementFolderRequest, newestFolder As CancellationTokenSource = Nothing
                    Using registration = retiredFolder.Token.Register(Sub()
                                                                          _managementFolderReader = Function(instance, token)
                                                                                                        replacementFolderReads += 1 : Return second.Task
                                                                                                    End Function
                                                                          OpenManagedFolder(button, Nothing) : newestFolder = _managementFolderRequest
                                                                      End Sub)
                        OpenManagedFolder(button, Nothing)
                        Dim notifiedState = _managementFolderRequest Is newestFolder AndAlso newestFolder IsNot Nothing AndAlso ManagedFolderStatus.Text.Contains("正在读取")
                        first.SetResult(owner) : Await WaitPackUiAsync(Function() folderReturned) : Await Task.Yield()
                        check(notifiedState AndAlso replacementFolderReads = 1 AndAlso _managementFolderRequest Is newestFolder AndAlso Not newestFolder.IsCancellationRequested AndAlso actions.Count = 0 AndAlso ManagedFolderStatus.Text.Contains("正在读取"),
                            "retiring a folder read preserves the replacement folder spinner and cancellation owner" & label)
                        second.SetResult(owner) : Await WaitPackUiAsync(Function() _managementFolderRequest Is Nothing) : settle()
                        check(actions.SequenceEqual({Path.Combine(owner.RunDirectory, "mods")}) AndAlso ManagedFolderStatus.Text = "" AndAlso ManagementViewHost.IsHitTestVisible,
                            "the replacement folder action alone opens its checked native path and settles normally" & label)
                    End Using

                    Dim oldGate As New TaskCompletionSource(Of List(Of InstalledInstance))(TaskCreationOptions.RunContinuationsAsynchronously)
                    Dim newGate As New TaskCompletionSource(Of List(Of InstalledInstance))(TaskCreationOptions.RunContinuationsAsynchronously)
                    _managementProvider = Function(binding, token) oldGate.Task
                    Dim reading = ScanManagementAsync(True), retired = _managementRequest, newest As CancellationTokenSource = Nothing, replacement As Task = Nothing, replacementReads = 0
                    Using registration = retired.Token.Register(Sub()
                                                                    _managementProvider = Function(binding, token)
                                                                                              replacementReads += 1 : Return newGate.Task
                                                                                          End Function
                                                                    replacement = ScanManagementAsync(True) : newest = _managementRequest
                                                                End Sub)
                        Dim restarting = ScanManagementAsync(True)
                        Dim notifiedState = newest IsNot Nothing AndAlso _managementRequest Is newest AndAlso _managementScanning AndAlso Not newest.IsCancellationRequested
                        oldGate.SetResult(New List(Of InstalledInstance)) : Await reading
                        check(notifiedState AndAlso replacementReads = 1 AndAlso _managementRequest Is newest AndAlso _managementScanning AndAlso Not newest.IsCancellationRequested,
                            "retiring a management scan cannot detach the newer scan or clear its loading gate" & label)
                        newGate.SetResult(items) : Await replacement : Await restarting : settle()
                        check(_managementInstances Is items AndAlso _managedInstance Is items.Single(Function(item) item.Name = owner.Name) AndAlso Not _managementScanning AndAlso _managementRequest Is Nothing,
                            "a replacement management scan accepts only its own native results after the retired scan exits" & label)
                    End Using

                    owner = _managedInstance : actions.Clear()
                    first = New TaskCompletionSource(Of InstalledInstance)(TaskCreationOptions.RunContinuationsAsynchronously) : folderReturned = False
                    _managementFolderReader = Async Function(instance, token)
                                                  Try
                                                      Return Await first.Task
                                                  Finally
                                                      folderReturned = True
                                                  End Try
                                              End Function
                    OpenManagedFolder(button, Nothing) : retiredFolder = _managementFolderRequest
                    newGate = New TaskCompletionSource(Of List(Of InstalledInstance))(TaskCreationOptions.RunContinuationsAsynchronously)
                    Dim latestItems = InstalledInstanceCatalog.Scan(target, CancellationToken.None)
                    Using registration = retiredFolder.Token.Register(Sub()
                                                                          _managementProvider = Function(binding, token) newGate.Task
                                                                          replacement = ScanManagementAsync(True) : newest = _managementRequest
                                                                      End Sub)
                        CancelManagementRequest()
                        Dim notifiedState = newest IsNot Nothing AndAlso _managementRequest Is newest AndAlso _managementScanning AndAlso Not newest.IsCancellationRequested
                        first.SetResult(owner) : Await WaitPackUiAsync(Function() folderReturned) : Await Task.Yield()
                        check(notifiedState AndAlso _managementRequest Is newest AndAlso Not newest.IsCancellationRequested AndAlso _managementScanning AndAlso actions.Count = 0,
                            "folder cancellation cannot make the outer retirement cancel a newly started management scan" & label)
                        newGate.SetResult(latestItems) : Await replacement : settle()
                        check(_managementInstances Is latestItems AndAlso Not _managementScanning AndAlso _managementRequest Is Nothing AndAlso actions.Count = 0 AndAlso ManagementViewHost.IsHitTestVisible,
                            "a scan started during folder retirement keeps usable results and ordinary animation handoff" & label)
                    End Using
                    _managementProvider = realProvider
                    Dim finalFiles = DirectoryFilesForModUpdateChecks(Path.GetDirectoryName(target.Root))
                    check(finalFiles.Count = files.Count AndAlso files.All(Function(item) finalFiles.ContainsKey(item.Key) AndAlso finalFiles(item.Key).SequenceEqual(item.Value)),
                        "management retirement preserves all game and native launcher configuration bytes" & label)
                Next
            Next
        Next
    End Function
End Class
