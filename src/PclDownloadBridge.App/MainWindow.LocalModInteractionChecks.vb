Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Documents
Imports System.Windows.Input
Imports System.Windows.Interop
Imports System.Windows.Media.Animation
Imports System.Windows.Threading
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    <DllImport("user32.dll", EntryPoint:="GetFocus")>
    Private Shared Function LocalModCheckGetNativeFocus() As IntPtr
    End Function
    <DllImport("user32.dll", EntryPoint:="SetFocus")>
    Private Shared Function LocalModCheckSetNativeFocus(window As IntPtr) As IntPtr
    End Function
    Private Function FocusLocalModButtonForChecks(entry As LocalModEntry) As PclIconButton
        Dim button = _localModRowViews(entry).Children.OfType(Of PclIconButton)().Single()
        FocusLocalModElementForChecks(button)
        Return button
    End Function
    Private Sub FocusLocalModElementForChecks(element As UIElement)
        Keyboard.ClearFocus()
        Dim source = TryCast(PresentationSource.FromVisual(element), HwndSource)
        ' Earlier fixtures dispose their keyboard input providers. Give only the
        ' current hidden source the calling thread's native focus; never show,
        ' activate or bring a desktop window to the foreground.
        If source IsNot Nothing Then LocalModCheckSetNativeFocus(source.Handle)
        FocusManager.SetFocusedElement(FocusManager.GetFocusScope(element), element)
        element.Focus()
        If Keyboard.FocusedElement Is element Then Return
        If Not PumpMotionUntil(Function()
                                   element.Focus()
                                   Return Keyboard.FocusedElement Is element
                               End Function) Then
            Throw New InvalidOperationException("Local Mod interaction fixture requires actual keyboard focus: " & element.GetType().Name &
                "; enabled=" & element.IsEnabled & "; visible=" & element.IsVisible & "; focusable=" & element.Focusable &
                "; hit=" & element.IsHitTestVisible & "; source=" & (PresentationSource.FromVisual(element) IsNot Nothing) &
                "; read=" & _localModReadSucceeded & "; scanning=" & _localModScanning & "; writing=" & _localModWriting)
        End If
    End Sub
    Private Sub VerifyLocalModInteractionUi(directory As String, check As Action(Of Boolean, String))
        Dim previousNativeFocus = LocalModCheckGetNativeFocus()
        Dim owner As New MainWindow(True), original = owner.Content
        Dim host As New Border With {.Width = 850, .Height = 700}, layout = ControlLayout(host)
        FocusManager.SetIsFocusScope(host, True)
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Dim previousContext = SynchronizationContext.Current
        SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(Dispatcher))
        Try
            Using source = PreviewSurface.Attach(host, 850, 700)
                layout()
                Dim work = owner.VerifyLocalModInteractionWorkflowAsync(directory, host, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 25000) Then Throw New TimeoutException("Local Mod interaction workflow did not settle.")
                work.GetAwaiter().GetResult()
            End Using
        Finally
            owner._closing = True : owner.CancelManagementRequest() : owner.CancelLocalModRequest() : owner._localModWriteRequest?.Cancel()
            owner._managementMotion.Finish() : owner._surfaceMotion.Finish()
            host.Child = Nothing : owner.Content = original
            LocalModCheckSetNativeFocus(previousNativeFocus)
            SynchronizationContext.SetSynchronizationContext(previousContext)
        End Try
    End Sub

    Private Async Function VerifyLocalModInteractionWorkflowAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim folders = PrepareLocalModFixture(directory), game = folders(0).Location
        Dim protectedBytes = IO.Directory.GetFiles(Path.GetDirectoryName(game), "*", SearchOption.AllDirectories).
            Where(Function(full) Not full.Contains(Path.DirectorySeparatorChar & "mods" & Path.DirectorySeparatorChar)).
            ToDictionary(Function(full) full, Function(full) File.ReadAllBytes(full))
        For index = 0 To 25
            LocalModJarForChecks(Path.Combine(game, "versions/Fabric 实例/mods/interaction-" & index & ".jar"), "interaction_" & index)
        Next
        ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(False) : layout()
        SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = "Fabric 实例")) : layout()
        ShowLocalMods(Nothing, Nothing) : Await LoadLocalModsAsync(False) : layout()
        LocalModsPage.ScrollToVerticalOffset(400) : layout()
        Dim entry = _localMods(12), previous = FocusLocalModButtonForChecks(entry)
        layout() : Dim offset = LocalModsPage.VerticalOffset
        Await ToggleLocalModAsync(entry) : layout()
        Dim replacement = _localMods.Single(Function(item) item.EnabledName = entry.EnabledName)
        Dim expected = _localModRowViews(replacement).Children.OfType(Of PclIconButton)().Single()
        check(Keyboard.FocusedElement Is expected AndAlso expected IsNot previous AndAlso Not replacement.Enabled AndAlso
            Math.Abs(LocalModsPage.VerticalOffset - offset) < 0.5 AndAlso LocalModStatus.Text.Contains("完成"),
            "without motion a completed toggle restores the corresponding fresh row button and retains the current Mods viewport")

        FilterLocalMods(LocalModEnabled, New RoutedEventArgs()) : layout()
        entry = _localMods.First(Function(item) item.Enabled AndAlso item.CanToggle)
        FocusLocalModButtonForChecks(entry) : Await ToggleLocalModAsync(entry) : layout()
        check(Keyboard.FocusedElement Is LocalModsPage AndAlso _localModFilter = "enabled" AndAlso
            LocalModRows.Children.Cast(Of Grid)().All(Function(row) DirectCast(row.Tag, LocalModEntry).Enabled),
            "a toggle hidden by the active enabled filter restores the viewport instead of a detached or filtered row")
        FilterLocalMods(LocalModAll, New RoutedEventArgs()) : layout()

        Dim writeGate As New TaskCompletionSource(Of LocalModActionResult)(TaskCreationOptions.RunContinuationsAsynchronously)
        _localModImporter = Function(instance, files, token) writeGate.Task
        entry = _localMods.Single(Function(item) item.FileName = "first.jar")
        FocusLocalModButtonForChecks(entry)
        Dim writing = ImportLocalModFilesAsync({entry.FilePath})
        PointerEvent(LocalModCancel, Mouse.PreviewMouseDownEvent) : FocusLocalModElementForChecks(LocalModCancel)
        CancelLocalModOperation(Nothing, Nothing) : writeGate.TrySetCanceled() : Await writing : layout()
        replacement = _localMods.Single(Function(item) item.EnabledName = entry.EnabledName)
        check(Keyboard.FocusedElement Is LocalModsPage AndAlso
            LocalModStatus.Text.Contains("取消") AndAlso Not _localModWriting AndAlso LocalModImport.IsEnabled,
            "using the temporary cancel button restores a usable viewport after that control retires without reviving older row focus")

        _localModImporter = Function(instance, files, token) Task.FromException(Of LocalModActionResult)(New IOException("controlled local import failure"))
        entry = replacement : FocusLocalModButtonForChecks(entry)
        Await ImportLocalModFilesAsync({entry.FilePath}) : layout()
        replacement = _localMods.Single(Function(item) item.EnabledName = entry.EnabledName)
        check(Keyboard.FocusedElement Is _localModRowViews(replacement).Children.OfType(Of PclIconButton)().Single() AndAlso LocalModStatus.Text.Contains("controlled local import failure"),
            "a failed file operation retains its error and restores focus from fresh accepted metadata")

        _localModImporter = Function(instance, files, token) Task.FromResult(New LocalModActionResult(Path.Combine(instance.RunDirectory, "mods"), 0, 1))
        _localModProvider = Function(instance, token) Task.FromException(Of List(Of LocalModEntry))(New IOException("controlled interaction rescan failure"))
        FocusLocalModButtonForChecks(replacement) : Await ImportLocalModFilesAsync({replacement.FilePath}) : layout()
        check(Keyboard.FocusedElement Is LocalModsPage AndAlso Not LocalModRows.IsEnabled AndAlso Not LocalModImport.IsEnabled AndAlso
            LocalModStatus.Text.Contains("controlled interaction rescan failure"),
            "a failed post-write rescan focuses the usable viewport without re-enabling or focusing stale file actions")
        _localModProvider = Function(instance, token) Task.FromResult(LocalModCatalog.Scan(instance, token))
        Await LoadLocalModsAsync(True) : layout()

        writeGate = New TaskCompletionSource(Of LocalModActionResult)(TaskCreationOptions.RunContinuationsAsynchronously)
        _localModImporter = Function(instance, files, token) writeGate.Task
        entry = _localMods.Single(Function(item) item.FileName = "first.jar") : FocusLocalModButtonForChecks(entry)
        writing = ImportLocalModFilesAsync({entry.FilePath})
        FocusLocalModElementForChecks(LocalModSearch) : LocalModSearch.Text = "enabled_fixture" : layout()
        writeGate.TrySetResult(New LocalModActionResult(Path.Combine(_localModInstance.RunDirectory, "mods"), 0, 1)) : Await writing : layout()
        check(Keyboard.FocusedElement Is LocalModSearch AndAlso LocalModSearch.Text = "enabled_fixture" AndAlso LocalModRows.Children.Count = 1,
            "a newer search focus and query supersede an older import's automatic row focus request")
        LocalModSearch.Text = "" : layout()

        writeGate = New TaskCompletionSource(Of LocalModActionResult)(TaskCreationOptions.RunContinuationsAsynchronously)
        entry = _localMods.Single(Function(item) item.FileName = "first.jar") : FocusLocalModButtonForChecks(entry)
        writing = ImportLocalModFilesAsync({entry.FilePath})
        ShowSettings(Nothing, Nothing) : layout()
        FocusLocalModElementForChecks(SpeedLimitBox)
        writeGate.TrySetCanceled() : Await writing : layout()
        check(SettingsPage.IsVisible AndAlso Keyboard.FocusedElement Is SpeedLimitBox AndAlso _localModFocusRestore Is Nothing,
            "off-page import completion cannot steal settings focus or retain a row focus callback for a hidden Mods page")

        ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(False)
        Await WaitPackUiAsync(Function() Not _localModScanning AndAlso Not _managementScanning) : layout()
        Dim readGate As New TaskCompletionSource(Of List(Of LocalModEntry))(TaskCreationOptions.RunContinuationsAsynchronously)
        _localModProvider = Function(instance, token) readGate.Task
        _localModImporter = Function(instance, files, token) Task.FromResult(New LocalModActionResult(Path.Combine(instance.RunDirectory, "mods"), 0, 1))
        entry = _localMods.Single(Function(item) item.FileName = "first.jar") : FocusLocalModButtonForChecks(entry)
        Dim oldWrite = ImportLocalModFilesAsync({entry.FilePath}), oldOwner = _localModInstance
        Await WaitPackUiAsync(Function() _localModScanning AndAlso Not _localModWriting)
        BackFromLocalMods() : layout()
        _localModProvider = Function(instance, token) Task.FromResult(LocalModCatalog.Scan(instance, token))
        ShowLocalMods(Nothing, Nothing) : Await LoadLocalModsAsync(False) : layout()
        entry = _localMods.Single(Function(item) item.FileName = "second.jar.disabled") : FocusLocalModButtonForChecks(entry)
        writeGate = New TaskCompletionSource(Of LocalModActionResult)(TaskCreationOptions.RunContinuationsAsynchronously)
        _localModImporter = Function(instance, files, token) writeGate.Task
        writing = ImportLocalModFilesAsync({entry.FilePath})
        Dim status = LocalModStatus.Text
        readGate.TrySetResult(LocalModCatalog.Scan(oldOwner, CancellationToken.None)) : Await oldWrite : layout()
        check(_localModWriting AndAlso LocalModStatus.Text = status AndAlso _localModFocusRestore Is Nothing,
            "a retired post-write read cannot queue old row focus while a newer operation owns the returned Mods page")
        writeGate.TrySetCanceled() : Await writing : layout()
        replacement = _localMods.Single(Function(item) item.EnabledName = entry.EnabledName)
        check(Keyboard.FocusedElement Is _localModRowViews(replacement).Children.OfType(Of PclIconButton)().Single() AndAlso LocalModStatus.Text.Contains("取消"),
            "the newer cancelled operation independently restores its own current row after the stale read is discarded")
        check(protectedBytes.All(Function(pair) File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),
            "local row focus filtering and cancellation preserve every original external configuration byte")
    End Function

    Private Sub VerifyLocalModInteractionMotion(directory As String, check As Action(Of Boolean, String))
        Dim previousNativeFocus = LocalModCheckGetNativeFocus()
        Dim owner As New MainWindow(True), original = owner.Content
        Dim host As New Border With {.Width = 850, .Height = 700}, layout = ControlLayout(host)
        FocusManager.SetIsFocusScope(host, True)
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using source = PreviewSurface.Attach(host, 850, 700)
                layout() : owner.PrepareLocalModFixture(directory) : owner.ShowManagement(Nothing, Nothing) : layout()
                If Not PumpMotionUntil(Function() Not owner._managementScanning) Then Throw New TimeoutException("Local interaction motion fixture did not load.")
                owner.SelectManagedInstance(owner._managementInstances.Single(Function(item) item.Name = "Fabric 实例")) : layout()
                owner.ShowLocalMods(Nothing, Nothing) : layout()
                If Not PumpMotionUntil(Function() Not owner._localModScanning) Then Throw New TimeoutException("Local interaction motion page did not load.")
                Dim entry = owner._localMods.Single(Function(item) item.FileName = "first.jar"), previous = owner.FocusLocalModButtonForChecks(entry)
                owner._motionPreview = True : owner._managementMotion.PauseForChecks = True : owner._surfaceMotion.PauseForChecks = True
                owner.ToggleLocalModAsync(entry).GetAwaiter().GetResult() : layout()
                Dim clock = owner._managementMotion.Transition
                check(clock IsNot Nothing AndAlso owner._localModFocusRestore IsNot Nothing AndAlso Not owner.ManagementViewHost.IsHitTestVisible AndAlso Keyboard.FocusedElement IsNot previous,
                    "a post-write animation defers fresh row focus while the PCL exit and entry input gate owns its page")
                clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(190), TimeSeekOrigin.BeginTime) : layout()
                Dim replacement = owner._localMods.Single(Function(item) item.EnabledName = entry.EnabledName)
                Dim button = owner._localModRowViews(replacement).Children.OfType(Of PclIconButton)().Single()
                check(Keyboard.FocusedElement Is button AndAlso owner.ManagementViewHost.IsHitTestVisible AndAlso owner._localModFocusRestore Is Nothing AndAlso Not replacement.Enabled,
                    "PCL content handoff restores the new suffix-matched row button before the remaining card animation settles")
                owner._managementMotion.Finish() : layout()
                check(Keyboard.FocusedElement Is button AndAlso owner.ManagementGhost.Children.Count = 0,
                    "later card settlement keeps the restored row focus and releases the outgoing frozen layers")

                owner.FocusLocalModElementForChecks(owner.LocalModSearch) : owner.LocalModSearch.Text = "enabled_fixture" : layout()
                owner.LoadLocalModsAsync(True).GetAwaiter().GetResult() : layout()
                clock = owner._managementMotion.Transition
                owner.LocalModSearch.SelectAll()
                Dim allowed = EditingCommands.Delete.CanExecute(Nothing, owner.LocalModSearch), inputVersion = owner._downloadFocusInputVersion
                EditingCommands.Delete.Execute(Nothing, owner.LocalModSearch) : layout()
                check(allowed AndAlso owner.LocalModSearch.Text = "" AndAlso owner.LocalModRows.Children.Count = 1 AndAlso owner._localModFilterPending AndAlso
                    RouteDownloadKey(owner.LocalModSearch, Key.A) AndAlso owner._downloadFocusInputVersion = inputVersion,
                    "native text editing during a card gate records a pending query while ordinary shell key input remains blocked")
                clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(190), TimeSeekOrigin.BeginTime) : layout()
                check(owner.LocalModRows.Children.Count = 4 AndAlso Not owner._localModFilterPending AndAlso owner._managementMotion.Transition Is Nothing AndAlso
                    Keyboard.FocusedElement Is owner.LocalModSearch AndAlso owner.ManagementGhost.Children.Count = 0,
                    "query handoff retires its owned card clock and reapplies the latest editor text without stealing search focus")

                entry = owner._localMods.Single(Function(item) item.FileName = "first.jar.disabled") : owner.FocusLocalModButtonForChecks(entry)
                owner.ToggleLocalModAsync(entry).GetAwaiter().GetResult() : layout()
                clock = owner._managementMotion.Transition
                clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(190), TimeSeekOrigin.BeginTime) : layout()
                owner.FocusLocalModElementForChecks(owner.LocalModSearch) : owner._managementMotion.Finish() : layout()
                check(Keyboard.FocusedElement Is owner.LocalModSearch AndAlso owner._localModFocusRestore Is Nothing,
                    "a search focus chosen after card interaction opens survives the older animation's completion")

                entry = owner._localMods.Single(Function(item) item.FileName = "first.jar") : owner.FocusLocalModButtonForChecks(entry)
                owner.ToggleLocalModAsync(entry).GetAwaiter().GetResult() : layout()
                clock = owner._managementMotion.Transition
                owner.BackFromLocalMods() : layout()
                Dim returning = owner._managementMotion.Transition
                owner._managementMotion.Finish() : owner.ShowSettings(Nothing, Nothing) : owner._surfaceMotion.Finish() : layout()
                owner.FocusLocalModElementForChecks(owner.SpeedLimitBox)
                clock.Controller.SkipToFill() : returning.Controller.SkipToFill() : layout()
                check(owner.SettingsPage.IsVisible AndAlso Keyboard.FocusedElement Is owner.SpeedLimitBox AndAlso owner._localModFocusRestore Is Nothing AndAlso
                    owner._managementMotion.Transition Is Nothing AndAlso owner.ManagementGhost.Children.Count = 0,
                    "reversal and a later root-page switch invalidate pending row focus and reject obsolete animation completions")
            End Using
        Finally
            owner._closing = True : owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._managementMotion.Finish() : owner._surfaceMotion.Finish()
            host.Child = Nothing : owner.Content = original
            LocalModCheckSetNativeFocus(previousNativeFocus)
        End Try
    End Sub
End Class
