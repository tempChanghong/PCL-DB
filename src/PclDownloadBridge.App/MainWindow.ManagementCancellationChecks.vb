Imports System.IO
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyManagementCancellationUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim host As New Border With {.Width = 850, .Height = 700}, layout = ControlLayout(host)
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     If Not ok Then Throw New InvalidOperationException(label)
                                                     passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using source = PreviewSurface.Attach(host, 850, 700)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckManagementCancellationAsync(directory, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 30000) Then Throw New TimeoutException("Management cancellation checks timed out.")
                work.GetAwaiter().GetResult()
            End Using
        Finally
            owner._closing = True : owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._managementMotion.Finish() : owner._surfaceMotion.Finish()
            host.Child = Nothing : owner.Content = original
            SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub

    Private Async Function CheckManagementCancellationAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim uiThread = Thread.CurrentThread.ManagedThreadId
        Dim threadId = Await RunManagementWorkerAsync(Function() Thread.CurrentThread.ManagedThreadId, CancellationToken.None)
        check(threadId <> uiThread, "management work remains off the UI thread and returns its successful result")
        Using source As New CancellationTokenSource()
            source.Cancel()
            Dim called = False, pending = RunManagementWorkerAsync(Function()
                                                                      called = True : Return 1
                                                                  End Function, source.Token)
            Try
                Await pending
            Catch ex As OperationCanceledException
            End Try
            check(pending.IsCanceled AndAlso Not called, "pre-cancelled management work never touches an instance or a Mod file")
        End Using
        Using source As New CancellationTokenSource()
            Dim entered As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
            Dim pending = RunManagementWorkerAsync(Function()
                                                      entered.TrySetResult(True)
                                                      If Not source.Token.WaitHandle.WaitOne(5000) Then Throw New TimeoutException("Cancellation did not arrive.")
                                                      source.Token.ThrowIfCancellationRequested()
                                                      Return 1
                                                  End Function, source.Token)
            Await entered.Task : source.Cancel()
            Dim observed As CancellationToken
            Try
                Await pending
            Catch ex As OperationCanceledException
                observed = ex.CancellationToken
            End Try
            check(pending.IsCanceled AndAlso observed = source.Token, "running management cancellation is handled inside the worker and retains a cancelled task with its owner's token")
        End Using
        Using alien As New CancellationTokenSource()
            alien.Cancel()
            Dim failure As New OperationCanceledException("foreign scan cancellation", alien.Token), observed As Exception = Nothing
            Dim pending = RunManagementWorkerAsync(Of Integer)(Function()
                                                                  Throw failure
                                                              End Function, CancellationToken.None)
            Try
                Await pending
            Catch ex As Exception
                observed = ex
            End Try
            check(pending.IsFaulted AndAlso observed Is failure, "another owner's cancellation remains an observable fault rather than an empty successful list")
        End Using
        Using source As New CancellationTokenSource()
            Dim failure As New IOException("scan read failure"), observed As Exception = Nothing
            Dim pending = RunManagementWorkerAsync(Of Integer)(Function()
                                                                  source.Cancel() : Throw failure
                                                              End Function, source.Token)
            Try
                Await pending
            Catch ex As Exception
                observed = ex
            End Try
            check(pending.IsFaulted AndAlso observed Is failure, "an actual read failure is preserved even when the request is cancelled at the same time")
        End Using
        PrepareLocalModFixture(directory)
        _localModProvider = Function(instance, token) RunManagementWorkerAsync(Function() LocalModCatalog.Scan(instance, token), token)
        layout() : ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(False) : layout()
        SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = "Fabric 实例")) : layout()
        ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : layout()
        check(_localModReadSucceeded AndAlso _localMods.Count = 4 AndAlso LocalModImport.IsEnabled, "real temporary archive scanning through the production worker retains healthy unknown broken and disabled Mods")
        Dim enteredScan As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        _localModProvider = Function(instance, token) RunManagementWorkerAsync(Function()
                                                                                enteredScan.TrySetResult(True)
                                                                                If Not token.WaitHandle.WaitOne(5000) Then Throw New TimeoutException("Scan cancellation did not arrive.")
                                                                                Return LocalModCatalog.Scan(instance, token)
                                                                            End Function, token)
        Dim retired = LoadLocalModsAsync(True)
        Await enteredScan.Task : BackFromLocalMods() : Await retired : layout()
        check(ManagementDetailPage.IsVisible AndAlso Not _localModScanning AndAlso _localModRequest Is Nothing AndAlso Not LocalModStatus.Text.Contains("无法读取"),
            "returning during a real worker scan absorbs its owned cancellation without publishing stale rows or a failure")
        _localModProvider = Function(instance, token) RunManagementWorkerAsync(Function() LocalModCatalog.Scan(instance, token), token)
        ShowLocalMods(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _localModScanning) : layout()
        check(LocalModsPage.IsVisible AndAlso _localModReadSucceeded AndAlso _localMods.Count = 4 AndAlso LocalModImport.IsEnabled, "reopening after cancellation scans the original instance and restores normal input")
        _localModProvider = Function(instance, token) RunManagementWorkerAsync(Of List(Of LocalModEntry))(Function()
                                                                                                            Throw New OperationCanceledException("unexpected scan cancellation")
                                                                                                        End Function, token)
        Await LoadLocalModsAsync(True) : layout()
        check(Not _localModScanning AndAlso Not _localModReadSucceeded AndAlso LocalModRefresh.IsEnabled AndAlso LocalModStatus.Text.Contains("无法读取 Mod 列表") AndAlso LocalModStatus.Text.Contains("unexpected scan cancellation"),
            "an unrequested cancellation is reported as a read failure and leaves refresh usable")
        _localModProvider = Function(instance, token) RunManagementWorkerAsync(Function() LocalModCatalog.Scan(instance, token), token)
        Await LoadLocalModsAsync(True) : layout()
        check(_localModReadSucceeded AndAlso _localMods.Count = 4 AndAlso LocalModImport.IsEnabled AndAlso Not LocalModStatus.Text.Contains("无法读取"), "a genuine scan failure can be retried without losing existing Mod files or instance settings")
        Dim enteredOld As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously), releaseOld As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        _localModProvider = Function(instance, token) RunManagementWorkerAsync(Function()
                                                                                enteredOld.TrySetResult(True)
                                                                                If Not releaseOld.Task.Wait(5000) Then Throw New TimeoutException("Old scan was not released.")
                                                                                Return LocalModCatalog.Scan(instance, token)
                                                                            End Function, token)
        Dim oldRead = LoadLocalModsAsync(True) : Await enteredOld.Task
        _localModProvider = Function(instance, token) RunManagementWorkerAsync(Function() LocalModCatalog.Scan(instance, token), token)
        Await LoadLocalModsAsync(True) : releaseOld.TrySetResult(True) : Await oldRead : layout()
        check(_localModReadSucceeded AndAlso _localMods.Count = 4 AndAlso Not _localModScanning AndAlso LocalModImport.IsEnabled AndAlso Not LocalModStatus.Text.Contains("无法读取"),
            "a cancelled old worker completing after a newer refresh cannot clear its results, availability or success")
    End Function
End Class
