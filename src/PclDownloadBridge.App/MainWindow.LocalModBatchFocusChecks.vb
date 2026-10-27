Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media.Animation
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyLocalModBatchFocusUi(directory As String, passed As List(Of String))
        Dim failures As New List(Of String), observed = 0
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observed += 1
                                                     If ok Then passed.Add(label) Else failures.Add(label)
                                                 End Sub
        For Each dark In {False, True}
            For Each moving In {False, True}
                Dim saved = _appearance.ToJson(), previous = SynchronizationContext.Current, nativeFocus = LocalModCheckGetNativeFocus()
                Dim owner As New MainWindow(True), original = owner.Content
                Dim host As New Border With {.Width = 900, .Height = 700}, layout = ControlLayout(host)
                owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
                FocusManager.SetIsFocusScope(host, True)
                Try
                    Using surface = PreviewSurface.Attach(host, 900, 700)
                        SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                        Dim work = owner.CheckLocalModBatchFocusAsync(Path.Combine(directory, "batch-focus-" & dark & "-" & moving), dark, moving, layout, check)
                        If Not PumpMotionUntil(Function() work.IsCompleted, 30000) Then Throw New TimeoutException("Batch Mod focus checks did not settle.")
                        work.GetAwaiter().GetResult()
                    End Using
                Finally
                    owner._closing = True : owner.CancelLocalModRequest() : owner.CancelManagementRequest()
                    owner._localModWriteRequest?.Cancel()
                    owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
                    owner._surfaceMotion.Finish() : owner._managementMotion.Finish() : owner._categoryMotion.Finish()
                    host.Child = Nothing : owner.Content = original
                    RestoreAppearance(saved) : SynchronizationContext.SetSynchronizationContext(previous) : LocalModCheckSetNativeFocus(nativeFocus)
                End Try
            Next
        Next
        If failures.Count > 0 Then Throw New InvalidOperationException("Mod batch focus failures: " & failures.Count & "/" & observed & vbCrLf & String.Join(vbCrLf, failures))
    End Sub

    Private Async Function CheckLocalModBatchFocusAsync(directory As String, dark As Boolean, moving As Boolean, layout As Action, check As Action(Of Boolean, String)) As Task
        PrepareLocalModStylePage(directory, layout)
        For index = 0 To 15
            LocalModJarForChecks(Path.Combine(_localModInstance.RunDirectory, "mods/batch-" & index & ".jar"), "batch_" & index)
        Next
        RestoreAppearance(New JObject From {{"darkMode", dark}}) : layout()
        Dim protectedBytes = IO.Directory.GetFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(Function(full) full, Function(full) File.ReadAllBytes(full))
        Dim label As Func(Of String, String) = Function(value) value & "; dark=" & dark & "; motion=" & moving
        For Each outcome In {"success", "failure", "cancel", "read-failure"}
            _motionPreview = False
            _localModProvider = Function(instance, token) Task.FromResult(LocalModCatalog.Scan(instance, token))
            Await LoadLocalModsAsync(False) : layout()
            Dim entry = _localMods.Single(Function(item) item.FileName = "first.jar")
            DirectCast(_localModRowViews(entry), PclLocalModItem).Checked = True : layout()
            FocusLocalModElementForChecks(LocalModDisableSelected)
            LocalModsPage.ScrollToVerticalOffset(250) : layout()
            Dim offset = LocalModsPage.VerticalOffset
            Dim gate As New TaskCompletionSource(Of LocalModActionResult)(TaskCreationOptions.RunContinuationsAsynchronously)
            Dim writing = RunLocalModWriteAsync(Function(instance, token) gate.Task, "controlled batch write")
            layout()
            check(_localModWriting AndAlso _localModSelection.Count = 0 AndAlso Not LocalModSelectionBar.IsVisible AndAlso Not LocalModRows.IsEnabled,
                label("batch write owns input and retires selection before awaiting its provider: " & outcome))
            _motionPreview = moving : _managementMotion.PauseForChecks = moving
            If outcome = "read-failure" Then _localModProvider = Function(instance, token) Task.FromException(Of List(Of LocalModEntry))(New IOException("controlled batch rescan failure"))
            If outcome = "cancel" Then
                CancelLocalModOperation(Nothing, Nothing) : gate.TrySetCanceled()
            ElseIf outcome = "failure" Then
                gate.TrySetException(New IOException("controlled batch failure"))
            Else
                gate.TrySetResult(New LocalModActionResult(Path.Combine(_localModInstance.RunDirectory, "mods"), 0, 1))
            End If
            Await writing : layout()
            Dim transition = _managementMotion.Transition
            check(If(moving AndAlso outcome <> "read-failure", transition IsNot Nothing AndAlso _localModFocusRestore IsNot Nothing AndAlso Not ManagementViewHost.IsHitTestVisible,
                    _localModFocusRestore Is Nothing),
                label("batch focus respects the content animation's input handoff: " & outcome))
            If transition IsNot Nothing Then transition.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(190), TimeSeekOrigin.BeginTime) : layout()
            _managementMotion.Finish() : layout()
            Dim feedback = If(outcome = "success", "完成", If(outcome = "failure", "controlled batch failure", If(outcome = "cancel", "取消", "controlled batch rescan failure")))
            check(Keyboard.FocusedElement Is LocalModsPage AndAlso _localModFocusRestore Is Nothing AndAlso offset > 100 AndAlso
                Math.Abs(LocalModsPage.VerticalOffset - offset) < 0.5 AndAlso LocalModStatus.Text.Contains(feedback) AndAlso
                LocalModRows.IsEnabled = (outcome <> "read-failure"),
                label("batch completion or cancellation restores viewport focus, scroll and its own outcome: " & outcome))
        Next
        _motionPreview = False
        _localModProvider = Function(instance, token) Task.FromResult(LocalModCatalog.Scan(instance, token))
        Await LoadLocalModsAsync(True) : layout()
        Dim first = _localMods.Single(Function(item) item.FileName = "first.jar")
        DirectCast(_localModRowViews(first), PclLocalModItem).Checked = True : layout()
        FocusLocalModElementForChecks(LocalModDisableSelected)
        Dim pending As New TaskCompletionSource(Of LocalModActionResult)(TaskCreationOptions.RunContinuationsAsynchronously)
        Dim operation = RunLocalModWriteAsync(Function(instance, token) pending.Task, "controlled newer search")
        FocusLocalModElementForChecks(LocalModSearch) : LocalModSearch.Text = "enabled_fixture" : layout()
        pending.TrySetResult(New LocalModActionResult(Path.Combine(_localModInstance.RunDirectory, "mods"), 0, 1))
        Await operation : layout()
        check(Keyboard.FocusedElement Is LocalModSearch AndAlso LocalModSearch.Text = "enabled_fixture" AndAlso _localModFocusRestore Is Nothing,
            label("a newer search editor owns focus when an older batch completes"))
        LocalModSearch.Text = "" : layout()
        first = _localMods.Single(Function(item) item.FileName = "first.jar")
        DirectCast(_localModRowViews(first), PclLocalModItem).Checked = True : layout()
        FocusLocalModElementForChecks(LocalModDisableSelected)
        pending = New TaskCompletionSource(Of LocalModActionResult)(TaskCreationOptions.RunContinuationsAsynchronously)
        operation = RunLocalModWriteAsync(Function(instance, token) pending.Task, "controlled batch navigation")
        ShowSettings(Nothing, Nothing) : layout() : FocusLocalModElementForChecks(SpeedLimitBox)
        pending.TrySetCanceled() : Await operation : layout()
        check(SettingsPage.IsVisible AndAlso Keyboard.FocusedElement Is SpeedLimitBox AndAlso _localModFocusRestore Is Nothing,
            label("a cancelled off-page batch cannot steal settings focus"))
        check(protectedBytes.All(Function(pair) File.Exists(pair.Key) AndAlso File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),
            label("controlled batch lifecycle fixtures preserve every original game and launcher byte"))
    End Function
End Class
