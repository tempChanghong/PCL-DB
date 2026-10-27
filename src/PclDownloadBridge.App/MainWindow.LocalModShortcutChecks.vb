Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyLocalModShortcutUi(directory As String, passed As List(Of String))
        Dim failed As New List(Of String), observed = 0
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observed += 1
                                                     If ok Then passed.Add(label) Else failed.Add(label)
                                                 End Sub
        For Each dark In {False, True}
            VerifyLocalModShortcutCase(directory, dark, check)
        Next
        If failed.Count > 0 Then Throw New InvalidOperationException("Mod shortcut failures: " & failed.Count & "/" & observed & vbCrLf & String.Join(vbCrLf, failed))
    End Sub
    Private Sub VerifyLocalModShortcutCase(directory As String, dark As Boolean, check As Action(Of Boolean, String))
        Dim saved = _appearance.ToJson(), previous = SynchronizationContext.Current, nativeFocus = LocalModCheckGetNativeFocus()
        Dim owner As New MainWindow(True), original = owner.Content
        Dim host As New Border With {.Width = 900, .Height = 700}, layout = ControlLayout(host)
        Dim label As Func(Of String, String) = Function(value) value & " in dark mode " & dark
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 700)
                owner.PrepareLocalModStylePage(Path.Combine(directory, "shortcuts-" & dark), layout)
                owner.RestoreAppearance(New JObject From {{"darkMode", dark}}) : layout()
                owner._localModBarPolicy.EnabledForChecks = True : owner._localModBarPolicy.PauseForChecks = True
                Dim rows = owner.LocalModRows.Children.OfType(Of PclLocalModItem)().ToArray(), unloaded = 0
                For Each row In rows
                    row.MotionPolicy.EnabledForChecks = True : row.MotionPolicy.PauseForChecks = True
                    AddHandler row.Unloaded, Sub() unloaded += 1
                Next
                Dim shortcut As Func(Of Key, ModifierKeys, Boolean) =
                    Function(value, modifiers) owner.LocalModSelectionKey(New KeyEventArgs(Keyboard.PrimaryDevice, surface, Environment.TickCount, value) With {.RoutedEvent = Keyboard.PreviewKeyDownEvent}, modifiers)
                owner.FocusLocalModElementForChecks(owner.LocalModsPage)
                check(shortcut(Key.A, ModifierKeys.Control) AndAlso owner._localModSelection.Count = rows.Length AndAlso rows.All(Function(row) row.Checked),
                    label("Ctrl+A selects every currently displayed local Mod"))
                Dim fade = owner._localModBarOpacity.Clock, bounce = owner._localModBarOffset.Clock
                Dim rowClocks = rows.Select(Function(row) LocalModSelectionClock(row)).ToArray()
                check(shortcut(Key.A, ModifierKeys.Control) AndAlso owner._localModSelection.Count = rows.Length AndAlso rows.All(Function(row) row.Checked),
                    label("repeated Ctrl+A remains select-all instead of clearing the catalogue"))
                check(owner._localModBarOpacity.Clock Is fade AndAlso owner._localModBarOffset.Clock Is bounce AndAlso
                    rows.Select(Function(row) LocalModSelectionClock(row)).SequenceEqual(rowClocks),
                    label("repeated Ctrl+A preserves existing row and floating-toolbar clocks"))
                check(unloaded = 0 AndAlso rows.SequenceEqual(owner.LocalModRows.Children.OfType(Of PclLocalModItem)()) AndAlso Keyboard.FocusedElement Is owner.LocalModsPage,
                    label("keyboard select-all retains attached row identities and viewport focus"))
                owner.ClearLocalModSelection(Nothing, Nothing)
                owner.SelectAllLocalMods(Nothing, Nothing) : owner.SelectAllLocalMods(Nothing, Nothing) : layout()
                check(owner._localModSelection.Count = 0 AndAlso rows.All(Function(row) Not row.Checked),
                    label("the toolbar button retains its explicit select-all and cancel-all toggle"))
                owner.LocalModSearch.Text = "" : owner.FocusLocalModElementForChecks(owner.LocalModSearch)
                check(shortcut(Key.A, ModifierKeys.Control) AndAlso owner._localModSelection.Count = rows.Length AndAlso Keyboard.FocusedElement Is owner.LocalModsPage,
                    label("Ctrl+A in an empty Mod search moves focus to the viewport and selects files like PCL"))
                owner.LocalModSearch.Text = "fixture" : layout()
                owner.FocusLocalModElementForChecks(owner.LocalModSearch)
                Dim selection = owner._localModSelection.OrderBy(Function(value) value).ToArray()
                Dim nativeEditor = Not shortcut(Key.A, ModifierKeys.Control)
                ApplicationCommands.SelectAll.Execute(Nothing, owner.LocalModSearch)
                check(nativeEditor AndAlso owner.LocalModSearch.SelectionLength = owner.LocalModSearch.Text.Length AndAlso
                    owner._localModSelection.OrderBy(Function(value) value).SequenceEqual(selection) AndAlso Keyboard.FocusedElement Is owner.LocalModSearch,
                    label("nonempty Mod search retains native text select-all without changing selected files"))
                owner.LocalModSearch.Text = " " : layout()
                check(Not shortcut(Key.A, ModifierKeys.Control) AndAlso Keyboard.FocusedElement Is owner.LocalModSearch,
                    label("whitespace text still belongs to the native search editor"))
                owner.LocalModSearch.Text = "" : owner.FilterLocalMods(owner.LocalModEnabled, Nothing) : layout()
                owner.ClearLocalModSelection(Nothing, Nothing) : owner.FocusLocalModElementForChecks(owner.LocalModsPage)
                shortcut(Key.A, ModifierKeys.Control) : shortcut(Key.A, ModifierKeys.Control)
                check(owner._localModSelection.Count = owner.LocalModRows.Children.Count AndAlso owner._localModSelection.Count = 3 AndAlso
                    owner.LocalModRows.Children.OfType(Of PclLocalModItem)().All(Function(row) row.Checked AndAlso DirectCast(row.Tag, LocalModEntry).Enabled),
                    label("repeated Ctrl+A targets only the active enabled filter"))
                owner.LocalModSearch.Text = "enabled_fixture" : layout()
                owner.ClearLocalModSelection(Nothing, Nothing) : owner.FocusLocalModElementForChecks(owner.LocalModsPage)
                shortcut(Key.A, ModifierKeys.Control) : shortcut(Key.A, ModifierKeys.Control)
                check(owner._localModSelection.Count = 1 AndAlso owner.LocalModRows.Children.Count = 1 AndAlso DirectCast(owner.LocalModRows.Children(0), PclLocalModItem).Checked,
                    label("repeated Ctrl+A keeps a single search result selected"))
                owner.LocalModSearch.Text = "" : owner.FilterLocalMods(owner.LocalModAll, Nothing) : layout()
                owner.ClearLocalModSelection(Nothing, Nothing)
                owner.PageSurface.IsHitTestVisible = False
                check(Not shortcut(Key.A, ModifierKeys.Control) AndAlso owner._localModSelection.Count = 0,
                    label("the outer page handoff rejects shortcut selection"))
                owner.PageSurface.IsHitTestVisible = True : owner.ManagementViewHost.IsHitTestVisible = False
                check(Not shortcut(Key.A, ModifierKeys.Control) AndAlso owner._localModSelection.Count = 0,
                    label("the management content gate rejects shortcut selection"))
                owner.ManagementViewHost.IsHitTestVisible = True : layout()
                owner._localModWriting = True : owner.RefreshLocalModAvailability()
                shortcut(Key.A, ModifierKeys.Control)
                check(owner._localModSelection.Count = 0 AndAlso Not owner.LocalModSelectAll.IsEnabled,
                    label("a running file operation cannot select Mods through the shortcut"))
                owner._localModWriting = False : owner.RefreshLocalModAvailability()
                check(Not shortcut(Key.A, ModifierKeys.None) AndAlso Not shortcut(Key.A, ModifierKeys.Control Or ModifierKeys.Alt) AndAlso owner._localModSelection.Count = 0,
                    label("ordinary A and Ctrl+Alt+A do not select files"))
                owner.FocusLocalModElementForChecks(owner.LocalModsPage) : shortcut(Key.A, ModifierKeys.Control)
                Dim firstEscape = shortcut(Key.Escape, ModifierKeys.None), secondEscape = shortcut(Key.Escape, ModifierKeys.None)
                check(firstEscape AndAlso Not secondEscape AndAlso owner._localModSelection.Count = 0 AndAlso owner.LocalModsPage.IsVisible,
                    label("Escape clears selection once and then yields to normal title-back handling"))
                owner.FocusLocalModElementForChecks(owner.LocalModSearch) : owner.PageSurface.IsHitTestVisible = False
                check(Not shortcut(Key.A, ModifierKeys.Control) AndAlso Keyboard.FocusedElement Is owner.LocalModSearch AndAlso owner._localModSelection.Count = 0,
                    label("an outgoing empty search cannot transfer focus or select files"))
                owner.PageSurface.IsHitTestVisible = True : owner.ShowSettings(Nothing, Nothing) : layout()
                owner.FocusLocalModElementForChecks(owner.SpeedLimitBox)
                check(Not shortcut(Key.A, ModifierKeys.Control) AndAlso Not shortcut(Key.Escape, ModifierKeys.None) AndAlso Keyboard.FocusedElement Is owner.SpeedLimitBox,
                    label("hidden Mod shortcuts preserve the current settings page and focus"))
            End Using
        Finally
            owner._closing = True : owner._localModWriting = False : owner.CancelLocalModRequest() : owner.CancelManagementRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._managementMotion.Finish() : owner._categoryMotion.Finish()
            host.Child = Nothing : owner.Content = original
            RestoreAppearance(saved) : SynchronizationContext.SetSynchronizationContext(previous) : LocalModCheckSetNativeFocus(nativeFocus)
        End Try
    End Sub
End Class
