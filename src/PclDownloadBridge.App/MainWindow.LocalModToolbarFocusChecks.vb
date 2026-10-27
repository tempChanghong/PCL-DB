Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Public Sub VerifyLocalModToolbarFocusUi(directory As String, passed As List(Of String))
        Dim failed As New List(Of String), observed = 0
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observed += 1
                                                     If ok Then passed.Add(label) Else failed.Add(label)
                                                 End Sub
        For Each dark In {False, True}
            For Each moving In {False, True}
                VerifyLocalModToolbarFocusCase(directory, dark, moving, check)
            Next
        Next
        If failed.Count > 0 Then Throw New InvalidOperationException("Mod toolbar focus failures: " & failed.Count & "/" & observed & vbCrLf & String.Join(vbCrLf, failed))
    End Sub
    Private Sub VerifyLocalModToolbarFocusCase(directory As String, dark As Boolean, moving As Boolean, check As Action(Of Boolean, String))
        Dim saved = _appearance.ToJson(), previous = SynchronizationContext.Current, nativeFocus = LocalModCheckGetNativeFocus()
        Dim owner As New MainWindow(True), original = owner.Content
        Dim host As New Border With {.Width = 900, .Height = 700}, layout = ControlLayout(host)
        Dim label As Func(Of String, String) = Function(value) value & "; dark=" & dark & "; motion=" & moving
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 700)
                owner.PrepareLocalModStylePage(Path.Combine(directory, "toolbar-focus-" & dark & "-" & moving), layout)
                For index = 0 To 15
                    LocalModJarForChecks(Path.Combine(owner._localModInstance.RunDirectory, "mods/focus-" & index & ".jar"), "focus_" & index)
                Next
                owner.LoadLocalModsAsync(False).GetAwaiter().GetResult() : layout()
                owner.RestoreAppearance(New JObject From {{"darkMode", dark}}) : layout()
                Dim entry = owner._localMods.Single(Function(item) item.FileName = "first.jar")
                Dim row = DirectCast(owner._localModRowViews(entry), PclLocalModItem)
                owner._localModBarPolicy.EnabledForChecks = moving : owner._localModBarPolicy.PauseForChecks = moving
                row.Checked = True : layout()
                If moving Then
                    owner._localModBarOpacity.Clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(60), TimeSeekOrigin.BeginTime)
                    owner._localModBarOffset.Clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(360), TimeSeekOrigin.BeginTime) : layout()
                End If
                owner.LocalModsPage.ScrollToVerticalOffset(250) : layout()
                owner.FocusLocalModElementForChecks(owner.LocalModClearSelection) : layout()
                Dim offset = owner.LocalModsPage.VerticalOffset
                PressControlKey(owner.LocalModClearSelection, Key.Enter) : layout()
                check(owner._localModSelection.Count = 0 AndAlso Not owner.LocalModSelectionBar.IsHitTestVisible,
                    label("keyboard activation of cancel-selection clears files and retires toolbar input"))
                check(Keyboard.FocusedElement Is owner.LocalModsPage,
                    label("cancel-selection returns keyboard focus from the retiring toolbar to the Mods viewport"))
                check(offset > 100 AndAlso Math.Abs(owner.LocalModsPage.VerticalOffset - offset) < 0.5,
                    label("returning toolbar focus preserves the user's scrolled Mods viewport"))
                Dim oldFade = owner._localModBarOpacity.Clock, oldOffset = owner._localModBarOffset.Clock
                If oldFade IsNot Nothing Then oldFade.Controller.Resume() : oldFade.Controller.SkipToFill()
                If oldOffset IsNot Nothing Then oldOffset.Controller.Resume() : oldOffset.Controller.SkipToFill()
                check(PumpMotionUntil(Function() Not owner.LocalModSelectionBar.IsVisible) AndAlso Keyboard.FocusedElement Is owner.LocalModsPage AndAlso
                    owner._localModBarOpacity.Clock Is Nothing AndAlso owner._localModBarOffset.Clock Is Nothing,
                    label("toolbar exit completion leaves a usable viewport focus with no retired clocks"))
                row.Checked = True : layout()
                owner.FocusLocalModElementForChecks(owner.LocalModSearch) : owner.LocalModSearch.Text = "missing-result" : layout()
                check(owner._localModSelection.Count = 0 AndAlso Keyboard.FocusedElement Is owner.LocalModSearch AndAlso owner.LocalModSearch.Text = "missing-result",
                    label("search pruning clears selection without moving focus away from the editor"))
                owner.LocalModSearch.Text = "" : layout()
                owner.SelectAllLocalMods(Nothing, Nothing) : owner.FocusLocalModElementForChecks(owner.LocalModSelectAll)
                owner.SelectAllLocalMods(owner.LocalModSelectAll, Nothing) : layout()
                check(owner._localModSelection.Count = 0 AndAlso Keyboard.FocusedElement Is owner.LocalModSelectAll,
                    label("the persistent select-all button retains focus when it clears selection"))
                row = DirectCast(owner._localModRowViews(entry), PclLocalModItem) : row.Checked = True : layout()
                owner.FocusLocalModElementForChecks(owner.LocalModClearSelection)
                oldFade = owner._localModBarOpacity.Clock
                owner.ShowSettings(Nothing, Nothing) : layout() : owner.FocusLocalModElementForChecks(owner.SpeedLimitBox)
                oldFade?.Controller.SkipToFill() : layout()
                check(owner.SettingsPage.IsVisible AndAlso Keyboard.FocusedElement Is owner.SpeedLimitBox AndAlso owner._localModSelection.Count = 0,
                    label("retiring selection during navigation cannot redirect the settings page's focus"))
            End Using
        Finally
            owner._closing = True : owner.CancelLocalModRequest() : owner.CancelManagementRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._managementMotion.Finish() : owner._categoryMotion.Finish()
            host.Child = Nothing : owner.Content = original
            RestoreAppearance(saved) : SynchronizationContext.SetSynchronizationContext(previous) : LocalModCheckSetNativeFocus(nativeFocus)
        End Try
    End Sub
End Class
