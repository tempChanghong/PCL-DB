Imports System.ComponentModel
Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyManagementFolderPublicationUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim host As New Border With {.Width = 900, .Height = 700}, layout = ControlLayout(host), observations As New JObject
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 700)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckManagementFolderPublicationAsync(directory, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 60000) Then Throw New TimeoutException("Management folder publication did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "management-folder-publication.json"), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            owner._closing = True : owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner._managementMotion.Finish()
            host.Child = Nothing : owner.Content = original : SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub

    Private Async Function CheckManagementFolderPublicationAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : _managementMotion.Finish() : layout()
                               End Sub
        Dim realReader As Func(Of InstalledInstance, CancellationToken, Task(Of InstalledInstance)) =
            Function(instance, token) Task.FromResult(InstalledInstanceCatalog.Read(instance.Target, instance.Name, token))
        Dim waitOpen As Func(Of Task) = Function() WaitPackUiAsync(Function() _managementFolderRequest Is Nothing)
        For launcher = 0 To 2
            For Each dark In {False, True}
                _motionPreview = False
                Dim folders = PrepareLocalModFixture(Path.Combine(directory, "folder-publication-" & launcher & "-" & dark)), game = folders(0).Location
                LocalModJarForChecks(Path.Combine(game, "mods/publication.jar"), "folder_publication")
                FolderList.SelectedItem = folders(launcher)
                Dim target = CurrentInstallTarget(), native = InstalledInstanceCatalog.Read(target, "Fabric 实例", CancellationToken.None)
                SetLocalModIsolationForChecks(native, False)
                _managementProvider = Function(binding, token) Task.FromResult(InstalledInstanceCatalog.Scan(binding, token))
                _managementFolderReader = realReader
                ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(True) : settle()
                SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = native.Name)) : settle()
                RestoreAppearance(New JObject From {{"darkMode", dark}}) : _motionPreview = True : _managementMotion.PauseForChecks = True
                Dim mods = ManagedFolderActions.Children.OfType(Of PclButton)().Single(Function(button) CStr(button.Tag) = "mods")
                Dim version = ManagedFolderActions.Children.OfType(Of PclButton)().Single(Function(button) CStr(button.Tag) = "instance")
                Dim protectedFiles = DirectoryFilesForModUpdateChecks(Path.GetDirectoryName(game)), taskGeneration = _taskGeneration
                Dim label = "/launcher=" & target.Kind.ToString() & "/dark=" & dark
                For Each duringRows In {True, False}
                    SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = native.Name)) : settle()
                    Dim actions As New List(Of String), observed = False, armed = True, clock As Media.Animation.ClockGroup = Nothing
                    Dim field = If(duringRows, ManagementSummary, ManagedRunPath)
                    field.Text = "controlled old folder snapshot"
                    Dim descriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, GetType(TextBlock))
                    Dim changed As EventHandler = Sub(sender, args)
                                                     If Not armed Then Return
                                                     armed = False : observed = True
                                                     BackFromManagement(Nothing, Nothing)
                                                     clock = _managementMotion.Transition
                                                 End Sub
                    _managementOpenFolder = Sub(folder) actions.Add(folder)
                    descriptor.AddValueChanged(field, changed)
                    Try
                        OpenManagedFolder(mods, Nothing) : Await waitOpen() : layout()
                        check(observed AndAlso actions.Count = 0 AndAlso clock IsNot Nothing AndAlso _managementMotion.Transition Is clock AndAlso _managementFolderRequest Is Nothing AndAlso
                            ManagedFolderStatus.Text = "" AndAlso _taskGeneration = taskGeneration,
                            "return during folder snapshot publication retires shell launch and retains the accepted return clock" & label & "/rows=" & duringRows)
                        settle()
                        check(ManagementListPage.IsVisible AndAlso Not ManagementDetailPage.IsVisible AndAlso PageSurface.IsHitTestVisible AndAlso ManagementViewHost.IsHitTestVisible AndAlso
                            ManagementListPage.Opacity = 1 AndAlso _managementMotion.Transition Is Nothing AndAlso SameManagementTarget(_managementTarget, target),
                            "folder snapshot retirement finishes the requested list entry with usable input" & label & "/rows=" & duringRows)
                    Finally
                        descriptor.RemoveValueChanged(field, changed)
                    End Try
                Next

                For Each fail In {False, True}
                    SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = native.Name)) : settle()
                    Dim gate As New TaskCompletionSource(Of InstalledInstance)(TaskCreationOptions.RunContinuationsAsynchronously)
                    Dim actions As New List(Of String), newer As CancellationTokenSource = Nothing, accepted As InstalledInstance = Nothing, nested = False
                    _managementFolderReader = realReader
                    _managementOpenFolder = Sub(folder)
                                                actions.Add(folder)
                                                If nested Then Return
                                                nested = True
                                                _managementFolderReader = Function(instance, token)
                                                                              accepted = instance
                                                                              Return gate.Task
                                                                          End Function
                                                OpenManagedFolder(version, Nothing)
                                                newer = _managementFolderRequest
                                            End Sub
                    OpenManagedFolder(mods, Nothing)
                    Await WaitPackUiAsync(Function() nested AndAlso newer IsNot Nothing) : Await Task.Yield() : layout()
                    check(_managementFolderRequest Is newer AndAlso Not newer.IsCancellationRequested AndAlso ManagedFolderStatus.Text = "正在读取文件夹…" AndAlso
                        actions.SequenceEqual({Path.Combine(game, "mods")}) AndAlso ManagementDetailPage.IsVisible AndAlso _managedInstance Is accepted,
                        "a newer folder click accepted by the shell callback retains its request and loading feedback" & label & "/failure=" & fail)
                    If fail Then gate.SetException(New IOException("controlled current folder read failure")) Else gate.SetResult(realReader(accepted, CancellationToken.None).Result)
                    Await waitOpen() : layout()
                    check(If(fail, actions.Count = 1 AndAlso ManagedFolderStatus.Text.Contains("controlled current folder read failure"),
                        actions.SequenceEqual({Path.Combine(game, "mods"), accepted.InstancePath}) AndAlso ManagedFolderStatus.Text = "") AndAlso
                        ManagementDetailPage.IsVisible AndAlso PageSurface.IsHitTestVisible AndAlso ManagementViewHost.IsHitTestVisible AndAlso _taskGeneration = taskGeneration,
                        "the newer folder request alone publishes its current success or failure without changing navigation" & label & "/failure=" & fail)
                    _managementFolderReader = realReader
                Next
                Dim finalFiles = DirectoryFilesForModUpdateChecks(Path.GetDirectoryName(game))
                check(finalFiles.Count = protectedFiles.Count AndAlso protectedFiles.All(Function(item) finalFiles.ContainsKey(item.Key) AndAlso finalFiles(item.Key).SequenceEqual(item.Value)),
                    "folder publication and shell handoff preserve all game and external launcher configuration bytes" & label)
                _motionPreview = False : ShowDownload(Nothing, Nothing) : settle()
            Next
        Next
    End Function
End Class
