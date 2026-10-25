Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    ' Exercise the actual write owner and post-write catalogue handoff. A token
    ' callback may complete its awaited operation inline on the WPF dispatcher,
    ' just as the existing download cancellation path already permits.
    Private Sub VerifyLocalModCancelUi(directory As String, check As Action(Of Boolean, String))
        Dim previousContext = SynchronizationContext.Current
        Dim owner As New MainWindow(True), original = owner.Content
        Dim host As New Border With {.Width = 850, .Height = 700}, layout = ControlLayout(host)
        Dim registrations As New List(Of CancellationTokenRegistration)
        Dim gates As New List(Of TaskCompletionSource(Of LocalModActionResult))
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher))
            Using source = PreviewSurface.Attach(host, 850, 700)
                layout()
                Dim folders = owner.PrepareLocalModFixture(directory), game = folders(0).Location
                Dim protectedBytes = IO.Directory.GetFiles(Path.GetDirectoryName(game), "*", SearchOption.AllDirectories).
                    ToDictionary(Function(full) full, Function(full) File.ReadAllBytes(full))
                owner.ShowManagement(Nothing, Nothing) : owner.ScanManagementAsync(False).GetAwaiter().GetResult() : layout()
                owner.SelectManagedInstance(owner._managementInstances.Single(Function(item) item.Name = "Fabric 实例")) : layout()
                owner.ShowLocalMods(Nothing, Nothing) : owner.LoadLocalModsAsync(False).GetAwaiter().GetResult() : layout()
                Dim originalImporter = owner._localModImporter
                Dim sourcePath = owner._localMods.Single(Function(item) item.FileName = "first.jar").FilePath
                For Each completesSuccessfully In {False, True}
                    Dim success = completesSuccessfully
                    ' No RunContinuationsAsynchronously: cancellation must run
                    ' the complete UI continuation before Cancel() returns.
                    Dim gate As New TaskCompletionSource(Of LocalModActionResult), callbacks = 0
                    gates.Add(gate)
                    owner._localModImporter = Function(instance, files, token)
                                                  registrations.Add(token.Register(Sub()
                                                                                       callbacks += 1
                                                                                       If success Then
                                                                                           gate.TrySetResult(New LocalModActionResult(Path.Combine(instance.RunDirectory, "mods"), 0, 1))
                                                                                       Else
                                                                                           gate.TrySetCanceled()
                                                                                       End If
                                                                                   End Sub))
                                                  Return gate.Task
                                              End Function
                    Dim writing = owner.ImportLocalModFilesAsync({sourcePath})
                    Dim started = owner._localModWriting AndAlso Not writing.IsCompleted
                    owner.CancelLocalModOperation(Nothing, Nothing)
                    check(started AndAlso callbacks = 1 AndAlso writing.IsCompleted AndAlso Not owner._localModWriting AndAlso owner._localModWriteRequest Is Nothing AndAlso
                        owner.LocalModStatus.Text.Contains(If(success, "完成", "取消")) AndAlso Not owner.LocalModStatus.Text.Contains("等待") AndAlso
                        owner.LocalModCancel.Visibility = Visibility.Collapsed AndAlso owner.LocalModImport.IsEnabled AndAlso owner.LocalModRows.IsEnabled,
                        "inline token completion retains the final outcome and restores input after cancellation: success=" & success.ToString())
                    writing.GetAwaiter().GetResult()
                    Dim finalStatus = owner.LocalModStatus.Text
                    owner.CancelLocalModOperation(Nothing, Nothing)
                    check(owner.LocalModStatus.Text = finalStatus AndAlso callbacks = 1 AndAlso Not owner._localModWriting AndAlso owner.LocalModImport.IsEnabled,
                        "a late cancel after an inline completion cannot replace the accepted final feedback: success=" & success.ToString())
                Next

                Dim pending As New TaskCompletionSource(Of LocalModActionResult), pendingCallbacks = 0
                gates.Add(pending)
                owner._localModImporter = Function(instance, files, token)
                                              registrations.Add(token.Register(Sub() pendingCallbacks += 1))
                                              Return pending.Task
                                          End Function
                Dim waiting = owner.ImportLocalModFilesAsync({sourcePath})
                owner.CancelLocalModOperation(Nothing, Nothing)
                Dim waitingStatus = owner.LocalModStatus.Text, waitingRequest = owner._localModWriteRequest
                owner.CancelLocalModOperation(Nothing, Nothing)
                check(pendingCallbacks = 1 AndAlso owner._localModWriting AndAlso Not waiting.IsCompleted AndAlso owner._localModWriteRequest Is waitingRequest AndAlso
                    waitingRequest IsNot Nothing AndAlso waitingRequest.IsCancellationRequested AndAlso owner.LocalModStatus.Text = waitingStatus AndAlso
                    Not owner.LocalModCancel.IsEnabled AndAlso Not owner.LocalModImport.IsEnabled,
                    "repeated cancellation while cleanup is pending leaves its request feedback and input gates unchanged")
                pending.TrySetCanceled()
                check(waiting.IsCompleted AndAlso Not owner._localModWriting AndAlso owner._localModWriteRequest Is Nothing AndAlso
                    owner.LocalModStatus.Text.Contains("取消") AndAlso Not owner.LocalModStatus.Text.Contains("等待") AndAlso owner.LocalModImport.IsEnabled,
                    "a pending cancellation publishes its final feedback when the operation actually exits")
                waiting.GetAwaiter().GetResult()
                owner._localModImporter = originalImporter
                owner.ImportLocalModFilesAsync({sourcePath}).GetAwaiter().GetResult()
                check(owner.LocalModStatus.Text.Contains("完成") AndAlso owner.LocalModStatus.Text.Contains("跳过 1") AndAlso Not owner._localModWriting AndAlso
                    owner.LocalModImport.IsEnabled AndAlso owner.LocalModCancel.Visibility = Visibility.Collapsed,
                    "the real file action can immediately retry an identical local Mod after inline or pending cancellation")
                check(protectedBytes.All(Function(pair) File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),
                    "inline and repeated cancellation plus retry preserve every source archive and external configuration byte")
            End Using
        Finally
            owner._closing = True
            For Each gate In gates
                gate.TrySetCanceled()
            Next
            owner._localModWriteRequest?.Cancel()
            For Each registration In registrations
                registration.Dispose()
            Next
            owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._managementMotion.Finish() : owner._surfaceMotion.Finish()
            host.Child = Nothing : owner.Content = original
            SynchronizationContext.SetSynchronizationContext(previousContext)
        End Try
    End Sub
End Class
