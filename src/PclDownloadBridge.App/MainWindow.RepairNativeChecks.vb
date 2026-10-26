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
    Public Sub VerifyRepairNativeUi(directory As String, passed As List(Of String))
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
                Dim work = owner.CheckRepairNativeAsync(directory, host, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 60000) Then Throw New TimeoutException("Native repair checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "repair-native.json"), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            owner._closing = True : owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner._managementMotion.Finish()
            host.Child = Nothing : owner.Content = original : SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub

    Private Async Function CheckRepairNativeAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        ' Inject this same file into the pre-fix snapshot without adding production fields.
        Dim readerField = GetType(MainWindow).GetField("_managementRepairReader", BindingFlags.Instance Or BindingFlags.NonPublic)
        Dim productionReader = If(readerField Is Nothing, Nothing, TryCast(readerField.GetValue(Me), Func(Of InstalledInstance, CancellationToken, Task(Of InstalledInstance))))
        Dim version = _versions.Single(Function(entry) entry.Id = "1.20.1").Copy()
        Dim realReader As Func(Of InstalledInstance, CancellationToken, Task(Of InstalledInstance)) =
            Function(instance, token) Task.FromResult(InstalledInstanceCatalog.Read(instance.Target, instance.Name, token))
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : _managementMotion.Finish() : FinishPackContentMotion() : layout()
                               End Sub
        For launcher = 0 To 2
            For Each dark In {False, True}
                _motionPreview = False
                Dim folders = PrepareManagementFixture(directory)
                Dim binding = folders(launcher)
                FolderList.SelectedItem = binding
                Dim target = CurrentInstallTarget(), native = InstalledInstanceCatalog.Read(target, "我的原版", CancellationToken.None)
                IO.Directory.CreateDirectory(Path.Combine(native.InstancePath, "PCL"))
                Dim config = Path.GetFullPath(SetLocalModIsolationForChecks(native, Not dark))
                Dim openDetail As Func(Of Task) =
                    Async Function()
                        _managedRepairContext = Nothing
                        _versions = New List(Of VersionEntry) From {version.Copy()}
                        FolderList.SelectedItem = binding
                        ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(True) : settle()
                        SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = native.Name)) : settle()
                    End Function
                If readerField IsNot Nothing Then readerField.SetValue(Me, productionReader)
                Await openDetail()
                _motionPreview = dark : RestoreAppearance(New JObject From {{"darkMode", dark}})
                SetLocalModIsolationForChecks(_managedInstance, dark)
                Dim protectedFiles = IO.Directory.GetFiles(Path.GetDirectoryName(target.Root), "*", SearchOption.AllDirectories).ToDictionary(Function(path) path, Function(path) File.ReadAllBytes(path))
                Dim label = "/launcher=" & target.Kind.ToString() & "/animated-dark=" & dark, taskGeneration = _taskGeneration, installs = 0
                _installRunner = Function(entry, root, name, components, mode, strategy, token)
                                     installs += 1 : Return Task.FromException(Of InstallResult)(New IOException("repair preparation must not start a download"))
                                 End Function
                Await PrepareManagedRepairAsync() : settle()
                check(DownloadPage.IsVisible AndAlso RepairBox.IsChecked.GetValueOrDefault() AndAlso _managedRepairContext IsNot Nothing AndAlso
                    _managedRepairContext.IsIsolated.HasValue AndAlso _managedRepairContext.IsIsolated.Value = dark AndAlso _managedRepairContext.RunDirectory = If(dark, native.InstancePath, target.Root) AndAlso
                    SameManagementTarget(_managedRepairContext.Target, target) AndAlso InstanceName.Text = native.Name,
                    "repair preparation rereads changed native isolation and freezes the current existing instance for handoff" & label)
                If readerField IsNot Nothing Then readerField.SetValue(Me, realReader)

                Await openDetail()
                Dim metadata = Path.Combine(native.InstancePath, native.Name & ".json"), originalMetadata = File.ReadAllBytes(metadata), owner = _managedInstance
                Dim changed = JObject.Parse(File.ReadAllText(metadata))
                changed("mainClass") = "net.fabricmc.loader.impl.launch.knot.KnotClient"
                DirectCast(changed("libraries"), JArray).Add(New JObject From {{"name", "net.fabricmc:fabric-loader:0.16.10"}})
                File.WriteAllText(metadata, changed.ToString())
                Dim changedBytes = File.ReadAllBytes(metadata), lookups = 0, selectedBefore = _selectedEntry
                _versions.Clear()
                _versionProvider = Function(policy, token)
                                       lookups += 1 : Return Task.FromResult(New List(Of VersionEntry) From {version.Copy()})
                                   End Function
                Await PrepareManagedRepairAsync() : settle()
                Dim rejected = ManagementDetailPage.IsVisible AndAlso _managedRepairContext Is Nothing AndAlso _managedInstance Is owner AndAlso
                    Not _managementPreparing AndAlso lookups = 0 AndAlso _selectedEntry Is selectedBefore AndAlso File.ReadAllBytes(metadata).SequenceEqual(changedBytes)
                ManagementDetailPage.StopScrolling() : ManagementDetailPage.ScrollToEnd() : layout() : Await Task.Yield() : layout()
                SaveControlVisual(host, Path.Combine(directory, "repair-native-" & target.Kind.ToString() & If(dark, "-dark.png", "-light.png")))
                File.WriteAllBytes(metadata, originalMetadata)
                Await PrepareManagedRepairAsync() : settle()
                check(rejected AndAlso DownloadPage.IsVisible AndAlso _managedRepairContext IsNot Nothing AndAlso _managedRepairContext.IsVanillaRepairCandidate AndAlso lookups = 1,
                    "a newly modded instance is rejected before metadata lookup and restoring it permits same-page repair retry" & label)

                If readerField IsNot Nothing Then
                    Await openDetail() : owner = _managedInstance : selectedBefore = _selectedEntry
                    Dim failReader As Func(Of InstalledInstance, CancellationToken, Task(Of InstalledInstance)) =
                        Function(instance, token) Task.FromException(Of InstalledInstance)(New IOException("controlled native repair failure"))
                    readerField.SetValue(Me, failReader)
                    Await PrepareManagedRepairAsync() : settle()
                    Dim failed = ManagedRepairStatus.Text.Contains("controlled native repair failure") AndAlso Not _managementPreparing AndAlso
                        _managementRequest Is Nothing AndAlso ManagedRepair.IsEnabled AndAlso ManagementFolders.IsEnabled AndAlso _managedInstance Is owner AndAlso _selectedEntry Is selectedBefore
                    readerField.SetValue(Me, realReader) : Await PrepareManagedRepairAsync() : settle()
                    check(failed AndAlso DownloadPage.IsVisible AndAlso _managedRepairContext IsNot Nothing AndAlso _managedRepairContext.Name = native.Name,
                        "native read failure replaces loading feedback without changing the plan and permits immediate retry" & label)

                    Await openDetail()
                    Dim gate As New TaskCompletionSource(Of InstalledInstance)(TaskCreationOptions.RunContinuationsAsynchronously), readToken As CancellationToken
                    Dim delayed As Func(Of InstalledInstance, CancellationToken, Task(Of InstalledInstance)) =
                        Function(instance, token)
                            readToken = token : Return gate.Task
                        End Function
                    readerField.SetValue(Me, delayed)
                    Dim preparing = PrepareManagedRepairAsync(), request = _managementRequest
                    Await Task.Yield() : layout()
                    Dim responsive = Not preparing.IsCompleted AndAlso _managementPreparing AndAlso Not ManagedRepair.IsEnabled AndAlso Not ManagementFolders.IsEnabled
                    request.Cancel() : gate.SetResult(InstalledInstanceCatalog.Read(target, native.Name, CancellationToken.None))
                    Await preparing : settle()
                    Dim cancelled = responsive AndAlso readToken.IsCancellationRequested AndAlso ManagedRepairStatus.Text.Contains("取消") AndAlso ManagedRepair.IsEnabled
                    readerField.SetValue(Me, realReader) : Await PrepareManagedRepairAsync() : settle()
                    check(cancelled AndAlso DownloadPage.IsVisible AndAlso _managedRepairContext IsNot Nothing,
                        "pending native reads yield to the dispatcher and owned cancellation can retry without a stuck preparation" & label)

                    Await openDetail() : owner = _managedInstance
                    gate = New TaskCompletionSource(Of InstalledInstance)(TaskCreationOptions.RunContinuationsAsynchronously) : readerField.SetValue(Me, delayed)
                    Dim retired = PrepareManagedRepairAsync()
                    BackFromManagement(Nothing, Nothing) : settle() : SelectManagedInstance(owner) : settle()
                    readerField.SetValue(Me, realReader) : _versions.Clear()
                    Dim metadataGate As New TaskCompletionSource(Of List(Of VersionEntry))(TaskCreationOptions.RunContinuationsAsynchronously)
                    _versionProvider = Function(policy, token) metadataGate.Task
                    Dim current = PrepareManagedRepairAsync(), currentRequest = _managementRequest, generation = _managementGeneration
                    If dark Then gate.SetException(New IOException("retired native repair failure")) Else gate.SetResult(InstalledInstanceCatalog.Read(target, native.Name, CancellationToken.None))
                    Await retired : settle()
                    Dim retained = readToken.IsCancellationRequested AndAlso _managementRequest Is currentRequest AndAlso _managementGeneration = generation AndAlso
                        _managementPreparing AndAlso ManagedRepairStatus.Text.Contains("正在核对") AndAlso Not ManagedRepairStatus.Text.Contains("retired")
                    metadataGate.SetResult(New List(Of VersionEntry) From {version.Copy()}) : Await current : settle()
                    check(retained AndAlso DownloadPage.IsVisible AndAlso _managedRepairContext IsNot Nothing AndAlso _managedRepairContext.Name = native.Name,
                        "back and rapid retry discard a late native success or fault without clearing the newer metadata request" & label)

                    Await openDetail()
                    gate = New TaskCompletionSource(Of InstalledInstance)(TaskCreationOptions.RunContinuationsAsynchronously) : readerField.SetValue(Me, delayed)
                    retired = PrepareManagedRepairAsync()
                    _motionPreview = True : _surfaceMotion.PauseForChecks = True : ShowSettings(Nothing, Nothing) : layout()
                    Dim exiting = _surfaceMotion.Transition IsNot Nothing AndAlso Not PageSurface.IsHitTestVisible AndAlso readToken.IsCancellationRequested
                    _surfaceMotion.PauseForChecks = False : settle()
                    FolderList.SelectedItem = folders((launcher + 1) Mod 3)
                    ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(True) : settle()
                    SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = native.Name)) : settle()
                    Dim nextInstance = _managedInstance, nextFeedback = ManagedRepairStatus.Text
                    gate.SetException(New IOException("old launcher native failure")) : Await retired : settle()
                    check(exiting AndAlso _managedInstance Is nextInstance AndAlso Not SameManagementTarget(target, nextInstance.Target) AndAlso
                        ManagementDetailPage.IsVisible AndAlso ManagedRepairStatus.Text = nextFeedback AndAlso Not _managementPreparing AndAlso ManagedRepair.IsEnabled AndAlso _managedRepairContext Is Nothing,
                        "animated root departure cancels a native read and its late failure cannot overwrite another launcher binding" & label)
                    _motionPreview = dark : readerField.SetValue(Me, realReader)
                Else
                    For Each scenario In {"native read failure replaces loading feedback without changing the plan and permits immediate retry", "pending native reads yield to the dispatcher and owned cancellation can retry without a stuck preparation", "back and rapid retry discard a late native success or fault without clearing the newer metadata request", "animated root departure cancels a native read and its late failure cannot overwrite another launcher binding"}
                        check(False, scenario & label)
                    Next
                End If

                Await openDetail() : _versions.Clear() : selectedBefore = _selectedEntry
                Dim finalGate As New TaskCompletionSource(Of List(Of VersionEntry))(TaskCreationOptions.RunContinuationsAsynchronously)
                _versionProvider = Function(policy, token) finalGate.Task
                Dim validating = PrepareManagedRepairAsync()
                SetLocalModIsolationForChecks(_managedInstance, Not dark) : protectedFiles(config) = File.ReadAllBytes(config)
                finalGate.SetResult(New List(Of VersionEntry) From {version.Copy()}) : Await validating : settle()
                Dim blocked = ManagementDetailPage.IsVisible AndAlso _managedRepairContext Is Nothing AndAlso ManagedRepairStatus.Text.Contains("变化") AndAlso _selectedEntry Is selectedBefore
                _versionProvider = Function(policy, token) Task.FromResult(New List(Of VersionEntry) From {version.Copy()})
                Await PrepareManagedRepairAsync() : settle()
                check(blocked AndAlso DownloadPage.IsVisible AndAlso _managedRepairContext IsNot Nothing AndAlso _managedRepairContext.IsIsolated.HasValue AndAlso _managedRepairContext.IsIsolated.Value = Not dark,
                    "native changes during metadata lookup still fail the final unchanged check and retry freezes the newer configuration" & label)
                check(installs = 0 AndAlso _taskGeneration = taskGeneration AndAlso protectedFiles.All(Function(pair) File.Exists(pair.Key) AndAlso File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)) AndAlso
                    IO.Directory.GetFiles(Path.GetDirectoryName(target.Root), "*", SearchOption.AllDirectories).Length = protectedFiles.Count,
                    "repair preparation failures cancellation and retries preserve all instance files and configs without starting a download" & label)
                _motionPreview = False : ShowDownload(Nothing, Nothing) : settle()
            Next
        Next
    End Function
End Class
