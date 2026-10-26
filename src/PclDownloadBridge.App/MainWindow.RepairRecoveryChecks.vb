Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyRepairRecoveryUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim host As New Border With {.Width = 900, .Height = 650}, layout = ControlLayout(host), observations As New JObject
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 650)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckRepairRecoveryAsync(directory, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 45000) Then Throw New TimeoutException("Repair recovery checks did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "repair-recovery.json"), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            owner._closing = True : owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner._managementMotion.Finish()
            host.Child = Nothing : owner.Content = original : SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub

    Private Async Function CheckRepairRecoveryAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim version = _versions.Single(Function(entry) entry.Id = "1.20.1").Copy()
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion() : _managementMotion.Finish() : layout()
                               End Sub
        For launcher = 0 To 2
            For failure = 0 To 3
                _motionPreview = False
                Dim folders = PrepareManagementFixture(directory)
                FolderList.SelectedItem = folders(launcher)
                _managedRepairContext = Nothing : _versions = New List(Of VersionEntry)
                ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(False) : settle()
                Dim instance = _managementInstances.Single(Function(item) item.Name = "我的原版")
                SelectManagedInstance(instance) : settle()
                _motionPreview = failure >= 2 : RestoreAppearance(New JObject From {{"darkMode", failure Mod 2 = 1}})
                Dim protectedFiles = IO.Directory.GetFiles(Path.GetDirectoryName(instance.Target.Root), "*", SearchOption.AllDirectories).ToDictionary(Function(path) path, Function(path) File.ReadAllBytes(path))
                Dim originalEntry = _selectedEntry, taskGeneration = _taskGeneration, calls = 0
                Dim firstGate As New TaskCompletionSource(Of List(Of VersionEntry))(TaskCreationOptions.RunContinuationsAsynchronously), firstToken As CancellationToken
                _versionProvider = Function(policy, token)
                                       calls += 1 : firstToken = token : Return firstGate.Task
                                   End Function
                Dim preparing = PrepareManagedRepairAsync(), request = _managementRequest
                Await PrepareManagedRepairAsync()
                Dim label = "/launcher=" & instance.Target.Kind.ToString() & "/failure=" & failure
                check(calls = 1 AndAlso _managementPreparing AndAlso Not ManagedRepair.IsEnabled AndAlso Not ManagementFolders.IsEnabled AndAlso
                    ManagedRepairStatus.Text.Contains("正在核对") AndAlso _taskGeneration = taskGeneration,
                    "repair preparation owns one metadata request and freezes repeated submissions without starting an install" & label)
                Select Case failure
                    Case 0
                        request.Cancel() : firstGate.SetCanceled()
                    Case 1
                        request.Cancel() : firstGate.SetResult(New List(Of VersionEntry) From {version.Copy()})
                    Case 2
                        Using alien As New CancellationTokenSource()
                            alien.Cancel() : firstGate.SetException(New OperationCanceledException("controlled foreign metadata cancellation", alien.Token))
                        End Using
                    Case Else
                        firstGate.SetException(New IOException("controlled metadata read failure"))
                End Select
                Await preparing : settle()
                Dim expected = If(failure < 2, "取消", If(failure = 2, "controlled foreign metadata cancellation", "controlled metadata read failure"))
                check(Not _managementPreparing AndAlso _managementRequest Is Nothing AndAlso ManagedRepair.IsEnabled AndAlso ManagementFolders.IsEnabled AndAlso
                    ManagedRepairStatus.Text.Contains(expected) AndAlso Not ManagedRepairStatus.Text.Contains("正在核对") AndAlso _managedInstance Is instance AndAlso
                    ManagementDetailPage.IsVisible AndAlso _selectedEntry Is originalEntry AndAlso _taskGeneration = taskGeneration,
                    "current cancellation or metadata failure replaces loading feedback and permits a same-instance retry" & label)

                Dim retiredGate As New TaskCompletionSource(Of List(Of VersionEntry))(TaskCreationOptions.RunContinuationsAsynchronously), retiredToken As CancellationToken
                _versionProvider = Function(policy, token)
                                       retiredToken = token : Return retiredGate.Task
                                   End Function
                Dim retired = PrepareManagedRepairAsync()
                BackFromManagement(Nothing, Nothing) : settle() : SelectManagedInstance(instance) : settle()
                Dim currentGate As New TaskCompletionSource(Of List(Of VersionEntry))(TaskCreationOptions.RunContinuationsAsynchronously), currentToken As CancellationToken
                _versionProvider = Function(policy, token)
                                       currentToken = token : Return currentGate.Task
                                   End Function
                Dim current = PrepareManagedRepairAsync(), currentRequest = _managementRequest, generation = _managementGeneration
                If failure Mod 2 = 0 Then retiredGate.SetResult(New List(Of VersionEntry) From {version.Copy()}) Else retiredGate.SetException(New IOException("retired repair failure"))
                Await retired : settle()
                check(retiredToken.IsCancellationRequested AndAlso currentToken <> firstToken AndAlso currentToken <> retiredToken AndAlso Not currentToken.IsCancellationRequested AndAlso
                    _managementRequest Is currentRequest AndAlso _managementGeneration = generation AndAlso _managementPreparing AndAlso Not ManagedRepair.IsEnabled AndAlso
                    ManagedRepairStatus.Text.Contains("正在核对") AndAlso Not ManagedRepairStatus.Text.Contains("retired") AndAlso ManagementDetailPage.IsVisible,
                    "return and rapid retry retire old metadata results without clearing the new request or replacing its feedback" & label)
                currentGate.SetResult(New List(Of VersionEntry) From {version.Copy()})
                Await current : settle()
                check(Not _managementPreparing AndAlso _managementRequest Is Nothing AndAlso DownloadPage.IsVisible AndAlso _downloadView = DownloadView.Install AndAlso
                    RepairBox.IsChecked.GetValueOrDefault() AndAlso _managedRepairContext Is _managedInstance AndAlso _managedRepairContext.Name = instance.Name AndAlso
                    _managedRepairContext.MetadataSha256 = instance.MetadataSha256 AndAlso SameManagementTarget(_managedRepairContext.Target, instance.Target) AndAlso InstanceName.Text = instance.Name AndAlso _nameEdited AndAlso
                    _taskGeneration = taskGeneration AndAlso SameManagementTarget(instance.Target, CurrentInstallTarget()) AndAlso
                    protectedFiles.All(Function(pair) File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),
                    "successful retry hands the exact existing instance to the original installer without mutating native settings or files" & label)
            Next
        Next
    End Function
End Class
