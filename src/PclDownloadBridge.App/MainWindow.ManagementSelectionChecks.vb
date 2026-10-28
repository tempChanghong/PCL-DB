Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyManagementSelectionUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim focus = LocalModCheckGetNativeFocus(), observations As New JObject
        Dim host As New Border With {.Width = 900, .Height = 700}, layout = ControlLayout(host)
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                     File.WriteAllText(Path.Combine(directory, "management-selection.json"), observations.ToString())
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 700)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckManagementSelectionAsync(directory, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 90000) Then Throw New TimeoutException("Management selection checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            Dim failed = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failed.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failed))
        Finally
            owner._closing = True : owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._managementMotion.Finish() : owner._categoryMotion.Finish()
            host.Child = Nothing : owner.Content = original
            SynchronizationContext.SetSynchronizationContext(previous) : LocalModCheckSetNativeFocus(focus)
        End Try
    End Sub

    Private Async Function CheckManagementSelectionAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _managementMotion.Finish() : _categoryMotion.Finish() : layout()
                               End Sub
        For Each returning In {False, True}
            For launcher = 0 To 2
                For Each folderRead In {False, True}
                    For scenario = 0 To 4
                        _motionPreview = False : _managementMotion.PauseForChecks = False : _surfaceMotion.PauseForChecks = False
                        ShowDownload(Nothing, Nothing) : settle()
                        Dim folders = PrepareManagementFixture(Path.Combine(directory, "selection-" & returning & "-" & launcher & "-" & folderRead & "-" & scenario))
                        FolderList.SelectedItem = folders(launcher)
                        _managementProvider = Function(binding, token) Task.FromResult(InstalledInstanceCatalog.Scan(binding, token))
                        ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(True) : settle()
                        Dim target = CurrentInstallTarget(), plain = _managementInstances.Single(Function(item) item.Name = "我的原版")
                        Dim fabric = _managementInstances.Single(Function(item) item.Name = "Fabric 实例")
                        If returning Then SelectManagedInstance(fabric) : settle()
                        Dim files = DirectoryFilesForModUpdateChecks(Path.GetDirectoryName(target.Root)), taskGeneration = _taskGeneration
                        Dim opened As New List(Of String), oldRead As Task = Task.CompletedTask, folderReturned = False
                        _managementOpenFolder = Sub(path) opened.Add(path)
                        Dim scanGate As New TaskCompletionSource(Of List(Of InstalledInstance))(TaskCreationOptions.RunContinuationsAsynchronously)
                        Dim folderGate As New TaskCompletionSource(Of InstalledInstance)(TaskCreationOptions.RunContinuationsAsynchronously)
                        Dim retired As CancellationTokenSource
                        If folderRead Then
                            SelectManagedInstance(fabric) : settle()
                            _managementFolderReader = Async Function(instance, token)
                                                          Try
                                                              Return Await folderGate.Task
                                                          Finally
                                                              folderReturned = True
                                                          End Try
                                                      End Function
                            Dim button = ManagedFolderActions.Children.OfType(Of PclButton)().Single(Function(item) CStr(item.Tag) = "instance")
                            OpenManagedFolder(button, Nothing) : retired = _managementFolderRequest
                        Else
                            _managementProvider = Function(binding, token) scanGate.Task
                            oldRead = ScanManagementAsync(True) : retired = _managementRequest
                        End If
                        Dim animated = (launcher + scenario \ 2) Mod 2 = 0, chosenCase = scenario
                        RestoreAppearance(New JObject From {{"darkMode", (launcher + scenario) Mod 2 = 0}})
                        _motionPreview = animated : _managementMotion.PauseForChecks = animated : _surfaceMotion.PauseForChecks = animated : layout()
                        Dim expectedInstance = _managedInstance, expectedContent = ManagementContent(), expectedSurface = ActiveSurface()
                        Dim expectedRows = _managementInstances, expectedClock As Media.Animation.ClockGroup = Nothing, expectedSurfaceClock As Media.Animation.ClockGroup = Nothing
                        Dim nested As Task = Task.CompletedTask, escaped = False
                        Dim label = "/return=" & returning & "/launcher=" & target.Kind.ToString() & "/folder=" & folderRead & "/case=" & scenario
                        Using registration = retired.Token.Register(Sub()
                                                                       Select Case chosenCase
                                                                           Case 0, 4 : SelectManagedInstance(If(returning, plain, fabric))
                                                                           Case 1
                                                                               _managementProvider = Function(binding, token) Task.FromResult(InstalledInstanceCatalog.Scan(binding, token))
                                                                               nested = ScanManagementAsync(True)
                                                                           Case 2 : ShowSettings(Nothing, Nothing)
                                                                           Case 3 : Throw New IOException("Controlled selection cancellation failure")
                                                                       End Select
                                                                       expectedInstance = _managedInstance : expectedRows = _managementInstances
                                                                       expectedContent = ManagementContent() : expectedSurface = ActiveSurface()
                                                                       expectedClock = _managementMotion.Transition : expectedSurfaceClock = _surfaceMotion.Transition
                                                                       If chosenCase = 4 Then Throw New IOException("Controlled obsolete selection failure")
                                                                   End Sub)
                            Try
                                If returning Then BackFromManagement(Nothing, Nothing) Else SelectManagedInstance(plain)
                            Catch
                                escaped = True
                            End Try
                            layout()
                            Dim feedback = If(ManagementListPage.IsVisible, ManagementStatus.Text, ManagedRepairStatus.Text)
                            check(Not escaped AndAlso _managedInstance Is expectedInstance AndAlso ManagementContent() Is expectedContent AndAlso ActiveSurface() Is expectedSurface AndAlso
                                If(chosenCase = 3, feedback.Contains("重试"), _managementInstances Is expectedRows AndAlso _managementMotion.Transition Is expectedClock AndAlso _surfaceMotion.Transition Is expectedSurfaceClock),
                                "superseded instance selection or return preserves newer selection, refreshed cache, surface and entry clocks; owned cancellation faults allow retry" & label)
                            scanGate.TrySetResult(New List(Of InstalledInstance) From {plain, fabric}) : folderGate.TrySetResult(fabric)
                            Await oldRead : Await nested
                            If folderRead Then Await WaitPackUiAsync(Function() folderReturned)
                            Await Task.Yield() : settle()
                            check(_managedInstance Is expectedInstance AndAlso ManagementContent() Is expectedContent AndAlso ActiveSurface() Is expectedSurface AndAlso
                                If(chosenCase = 3, True, _managementInstances Is expectedRows) AndAlso opened.Count = 0 AndAlso _managementRequest Is Nothing AndAlso _managementFolderRequest Is Nothing,
                                "late scan and folder completions cannot reopen the retired directory or replace the accepted instance and page" & label)
                        End Using
                        _managementProvider = Function(binding, token) Task.FromResult(InstalledInstanceCatalog.Scan(binding, token))
                        _managementMotion.PauseForChecks = False : _surfaceMotion.PauseForChecks = False
                        ShowManagement(Nothing, Nothing) : settle()
                        If ManagementDetailPage.IsVisible Then BackFromManagement(Nothing, Nothing) : settle()
                        Dim retry = _managementInstances.Single(Function(item) item.Name = plain.Name)
                        SelectManagedInstance(retry) : settle()
                        check(_managedInstance Is retry AndAlso ManagementDetailPage.IsVisible AndAlso _managementInstances.Contains(_managedInstance) AndAlso
                            SameManagementTarget(target, _managedInstance.Target) AndAlso ManagedIntro.Title = retry.Name AndAlso ManagedRunPath.Text = retry.RunDirectory AndAlso
                            ManagementViewHost.IsHitTestVisible AndAlso PageSurface.IsHitTestVisible AndAlso _taskGeneration = taskGeneration AndAlso
                            files.All(Function(item) File.Exists(item.Key) AndAlso File.ReadAllBytes(item.Key).SequenceEqual(item.Value)),
                            "return and explicit retry use the current native instance snapshot without altering files or starting a download" & label)
                    Next
                Next
            Next
        Next
    End Function
End Class
