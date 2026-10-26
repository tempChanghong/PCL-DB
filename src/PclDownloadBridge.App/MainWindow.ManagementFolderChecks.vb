Imports System.IO
Imports System.Reflection
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyManagementFolderUi(directory As String, passed As List(Of String))
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
                Dim work = owner.CheckManagementFoldersAsync(directory, host, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 60000) Then Throw New TimeoutException("Management folders did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "management-folders.json"), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            owner._closing = True : owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner._managementMotion.Finish()
            host.Child = Nothing : owner.Content = original : SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub

    Private Async Function CheckManagementFoldersAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        ' Reflection keeps this same reproducer compilable in the pre-fix Git snapshot.
        Dim flags = BindingFlags.Instance Or BindingFlags.NonPublic
        Dim readerField = GetType(MainWindow).GetField("_managementFolderReader", flags)
        Dim requestField = GetType(MainWindow).GetField("_managementFolderRequest", flags)
        Dim feedback = TryCast(FindName("ManagedFolderStatus"), TextBlock)
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : _managementMotion.Finish() : layout()
                               End Sub
        Dim waitOpen As Func(Of Task) = Function() WaitPackUiAsync(Function() requestField Is Nothing OrElse requestField.GetValue(Me) Is Nothing)
        Dim realReader As Func(Of InstalledInstance, CancellationToken, Task(Of InstalledInstance)) =
            Function(instance, token) Task.FromResult(InstalledInstanceCatalog.Read(instance.Target, instance.Name, token))
        For launcher = 0 To 2
            For Each dark In {False, True}
                _motionPreview = False
                Dim folders = PrepareLocalModFixture(directory), game = folders(0).Location
                LocalModJarForChecks(Path.Combine(game, "mods/shared.jar"), "folder_shared")
                FolderList.SelectedItem = folders(launcher)
                Dim target = CurrentInstallTarget(), native = InstalledInstanceCatalog.Read(target, "Fabric 实例", CancellationToken.None)
                Dim config = Path.GetFullPath(SetLocalModIsolationForChecks(native, True))
                _managementProvider = Function(binding, token) Task.FromResult(InstalledInstanceCatalog.Scan(binding, token))
                If readerField IsNot Nothing Then readerField.SetValue(Me, realReader)
                ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(True) : settle()
                SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = native.Name)) : settle()
                _motionPreview = dark : RestoreAppearance(New JObject From {{"darkMode", dark}}) : layout()
                Dim actions As New List(Of String), label = "/launcher=" & target.Kind.ToString() & "/animated-dark=" & dark
                _managementOpenFolder = Sub(folder) actions.Add(folder)
                Dim mods = ManagedFolderActions.Children.OfType(Of PclButton)().Single(Function(button) CStr(button.Tag) = "mods")
                Dim version = ManagedFolderActions.Children.OfType(Of PclButton)().Single(Function(button) CStr(button.Tag) = "instance")
                SetLocalModIsolationForChecks(_managedInstance, False)
                Dim protectedFiles = IO.Directory.GetFiles(Path.GetDirectoryName(game), "*", SearchOption.AllDirectories).ToDictionary(Function(path) path, Function(path) File.ReadAllBytes(path))
                OpenManagedFolder(mods, Nothing) : Await waitOpen() : layout()
                check(actions.SequenceEqual({Path.Combine(game, "mods")}) AndAlso ManagedRunPath.Text = game AndAlso Not _managedInstance.IsIsolated.Value AndAlso SameManagementTarget(_managedInstance.Target, target),
                    "folder shortcuts reread native isolation and open the currently bound shared directory" & label)

                ' Restore a current snapshot so a baseline failure cannot mask the shell-error observation.
                Await ScanManagementAsync(True) : settle()
                Dim repair = ManagedRepairStatus.Text
                _managementOpenFolder = Sub(folder)
                                            Throw New IOException("controlled folder launch failure")
                                        End Sub
                OpenManagedFolder(mods, Nothing) : Await waitOpen() : layout()
                check(feedback IsNot Nothing AndAlso feedback.Text.Contains("controlled folder launch failure") AndAlso feedback.IsVisible AndAlso
                    ManagedRepairStatus.Text = repair AndAlso feedback.Parent Is ManagedFolderActions.Parent AndAlso
                    DirectCast(feedback.Foreground, Media.SolidColorBrush).Color = DirectCast(ManagedRepairStatus.Foreground, Media.SolidColorBrush).Color AndAlso feedback.FontFamily.Source = ManagedRepairStatus.FontFamily.Source,
                    "folder launch failure belongs to the shortcut card without replacing repair feedback" & label)
                If feedback IsNot Nothing Then
                    feedback.BringIntoView() : layout()
                    SaveControlVisual(host, Path.Combine(directory, "management-folder-" & target.Kind.ToString() & If(dark, "-dark.png", "-light.png")))
                End If
                actions.Clear() : _managementOpenFolder = Sub(folder) actions.Add(folder)
                OpenManagedFolder(mods, Nothing) : Await waitOpen() : layout()
                check(actions.SequenceEqual({Path.Combine(game, "mods")}) AndAlso feedback IsNot Nothing AndAlso feedback.Text = "" AndAlso feedback.Visibility <> Visibility.Visible AndAlso ManagedRepairStatus.Text = repair,
                    "retry opens the same current folder and clears only shortcut feedback" & label)

                If readerField IsNot Nothing Then
                    Dim gate As New TaskCompletionSource(Of InstalledInstance)(TaskCreationOptions.RunContinuationsAsynchronously)
                    Dim captured = _managedInstance, retiredToken As CancellationToken, returned = False
                    Dim delayed As Func(Of InstalledInstance, CancellationToken, Task(Of InstalledInstance)) =
                        Async Function(instance, token)
                            retiredToken = token
                            Try
                                Return Await gate.Task
                            Finally
                                returned = True
                            End Try
                        End Function
                    readerField.SetValue(Me, delayed) : actions.Clear()
                    OpenManagedFolder(mods, Nothing)
                    readerField.SetValue(Me, realReader) : OpenManagedFolder(version, Nothing) : Await waitOpen()
                    If dark Then gate.SetException(New IOException("retired folder failure")) Else gate.SetResult(captured)
                    Await WaitPackUiAsync(Function() returned) : Await Task.Yield() : layout()
                    check(retiredToken.IsCancellationRequested AndAlso actions.SequenceEqual({captured.InstancePath}) AndAlso feedback.Text = "" AndAlso ManagementDetailPage.IsVisible,
                        "a newer folder click retires late success or failure from the previous click" & label)

                    gate = New TaskCompletionSource(Of InstalledInstance)(TaskCreationOptions.RunContinuationsAsynchronously) : returned = False
                    captured = _managedInstance : actions.Clear() : readerField.SetValue(Me, delayed)
                    OpenManagedFolder(mods, Nothing)
                    DirectCast(requestField.GetValue(Me), CancellationTokenSource).Cancel() : gate.SetResult(captured)
                    Await waitOpen()
                    Dim timedOut = feedback.Text.Contains("超时") AndAlso actions.Count = 0
                    readerField.SetValue(Me, realReader) : OpenManagedFolder(mods, Nothing) : Await waitOpen()
                    check(timedOut AndAlso actions.SequenceEqual({Path.Combine(game, "mods")}) AndAlso feedback.Text = "",
                        "an expired owned folder request reports its timeout and allows an immediate clean retry" & label)

                    gate = New TaskCompletionSource(Of InstalledInstance)(TaskCreationOptions.RunContinuationsAsynchronously) : returned = False
                    captured = _managedInstance : actions.Clear() : readerField.SetValue(Me, delayed)
                    OpenManagedFolder(mods, Nothing) : BackFromManagement(Nothing, Nothing) : settle()
                    gate.SetResult(captured) : Await WaitPackUiAsync(Function() returned) : Await Task.Yield() : layout()
                    check(retiredToken.IsCancellationRequested AndAlso actions.Count = 0 AndAlso ManagementListPage.IsVisible AndAlso feedback.Text = "",
                        "back cancels an in-flight folder read before it can open Explorer or publish feedback" & label)

                    SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = native.Name)) : settle()
                    captured = _managedInstance : gate = New TaskCompletionSource(Of InstalledInstance)(TaskCreationOptions.RunContinuationsAsynchronously) : returned = False
                    OpenManagedFolder(mods, Nothing)
                    ManagementFolders.SelectedItem = folders((launcher + 1) Mod 3) : Await ScanManagementAsync(True) : settle()
                    Dim nextTarget = CurrentInstallTarget()
                    gate.SetException(New IOException("old launcher folder failure"))
                    Await WaitPackUiAsync(Function() returned) : Await Task.Yield() : layout()
                    check(retiredToken.IsCancellationRequested AndAlso actions.Count = 0 AndAlso ManagementListPage.IsVisible AndAlso
                        Not SameManagementTarget(target, nextTarget) AndAlso SameManagementTarget(_managementTarget, nextTarget) AndAlso feedback.Text = "",
                        "switching launcher bindings over a shared root discards retired folder faults" & label)
                    readerField.SetValue(Me, realReader)

                    SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = native.Name)) : settle()
                    Dim scanGate As New TaskCompletionSource(Of List(Of InstalledInstance))(TaskCreationOptions.RunContinuationsAsynchronously)
                    _managementProvider = Function(binding, token) scanGate.Task
                    Dim refreshing = ScanManagementAsync(True)
                    Dim disabledDuringScan = Not mods.IsEnabled
                    OpenManagedFolder(mods, Nothing)
                    Dim ignored = requestField.GetValue(Me) Is Nothing AndAlso actions.Count = 0
                    scanGate.SetResult(InstalledInstanceCatalog.Scan(nextTarget, CancellationToken.None)) : Await refreshing : settle()
                    check(disabledDuringScan AndAlso ignored AndAlso mods.IsEnabled AndAlso feedback.Text = "" AndAlso SameManagementTarget(_managedInstance.Target, nextTarget),
                        "folder actions wait for an active native list refresh instead of replacing its snapshot" & label)
                Else
                    For Each scenario In {"a newer folder click retires late success or failure from the previous click", "an expired owned folder request reports its timeout and allows an immediate clean retry", "back cancels an in-flight folder read before it can open Explorer or publish feedback", "switching launcher bindings over a shared root discards retired folder faults", "folder actions wait for an active native list refresh instead of replacing its snapshot"}
                        check(False, scenario & label)
                    Next
                End If
                check(protectedFiles.All(Function(pair) File.Exists(pair.Key) AndAlso File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)) AndAlso
                    IO.Directory.GetFiles(Path.GetDirectoryName(game), "*", SearchOption.AllDirectories).Length = protectedFiles.Count AndAlso File.ReadAllBytes(config).SequenceEqual(protectedFiles(config)),
                    "folder success errors cancellation and retries preserve all launcher configs and game files" & label)
                _motionPreview = False : ShowDownload(Nothing, Nothing) : settle()
            Next
        Next
    End Function
End Class
