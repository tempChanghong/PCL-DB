Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Sub VerifyVersionRowOwnership(directory As String, check As Action(Of Boolean, String))
        Dim appearance = _appearance.ToJson(), owner As New MainWindow(True), original = owner.Content
        Dim host As New Border With {.Width = 850, .Height = 600}, layout = ControlLayout(host)
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using source = PreviewSurface.Attach(host, 850, 600)
                layout()
                Dim row = owner.BrowserRows("正式版")(0)
                Dim card = owner.VersionCards.Children.OfType(Of PclCard)().Single(Function(item) item.Body Is row.Parent)
                layout() : FinishDeferredCard(card, layout)
                card.Collapsed = True : SeekControl(card.HeightTransition, 10) : layout()
                PointerEvent(row, Mouse.MouseDownEvent) : PointerEvent(row, Mouse.MouseUpEvent) : layout()
                IO.File.WriteAllText(IO.Path.Combine(directory, "vanilla-row-retirement.json"), New JObject From {
                    {"folded", card.Collapsed}, {"view", owner._downloadView.ToString()}, {"selectedOldEntry", owner._selectedEntry Is row.Tag}}.ToString())
                check(owner._downloadView = DownloadView.Versions AndAlso owner._selectedEntry Is Nothing,
                    "a still-visible vanilla row cannot open installation after its category has logically folded")
                card.Collapsed = False : FinishDeferredCard(card, layout)
                Dim observations As New JArray
                Dim ignored As Action(Of PclListItem, String) =
                    Sub(target, label)
                        Dim view = owner._downloadView, selected = owner._selectedEntry, generation = owner._componentGeneration, task = owner._taskGeneration
                        Dim focus = owner._keyboardVersionReturn, pending = owner._pendingDownloadFocus, name = owner.InstanceName.Text, edited = owner._nameEdited
                        PointerEvent(target, Mouse.MouseDownEvent) : PointerEvent(target, Mouse.MouseUpEvent)
                        If PresentationSource.FromVisual(target) IsNot Nothing Then ControlKey(target, Key.Enter, True)
                        target.Activate() : layout()
                        Dim unchanged = owner._downloadView = view AndAlso owner._selectedEntry Is selected AndAlso owner._componentGeneration = generation AndAlso owner._taskGeneration = task AndAlso
                            owner._keyboardVersionReturn Is focus AndAlso owner._pendingDownloadFocus Is pending AndAlso owner.InstanceName.Text = name AndAlso owner._nameEdited = edited
                        observations.Add(New JObject From {{"case", label}, {"unchanged", unchanged}})
                        IO.File.WriteAllText(IO.Path.Combine(directory, "vanilla-row-ownership-states.json"), observations.ToString())
                        check(unchanged, "retired vanilla row actions preserve selection, component/name state, focus and task ownership: " & label)
                    End Sub
                For Each boundary In {"page-gate", "view-gate", "disabled", "hidden", "busy", "closing", "dialog", "settings"}
                    Select Case boundary
                        Case "page-gate" : owner.PageSurface.IsHitTestVisible = False
                        Case "view-gate" : owner.DownloadViewHost.IsHitTestVisible = False
                        Case "disabled" : host.IsEnabled = False
                        Case "hidden" : card.Visibility = Visibility.Collapsed
                        Case "busy" : owner._busy = True
                        Case "closing" : owner._closing = True
                        Case "dialog" : owner._choosingOnline = True
                        Case "settings" : owner.ShowSettings(Nothing, Nothing)
                    End Select
                    layout() : ignored(row, boundary)
                    owner.PageSurface.IsHitTestVisible = True : owner.DownloadViewHost.IsHitTestVisible = True : host.IsEnabled = True : card.Visibility = Visibility.Visible
                    owner._busy = False : owner._closing = False : owner._choosingOnline = False
                    If boundary = "settings" Then owner.ShowDownload(Nothing, Nothing)
                    layout()
                Next
                owner.UpdateVersions() : layout()
                ignored(row, "catalogue-replaced")
                row = owner.BrowserRows("正式版")(0) : layout()
                Dim previous = DirectCast(row.Tag, VersionEntry), replacement = previous.Copy()
                owner._versions = owner._versions.Select(Function(entry) If(entry Is previous, replacement, entry)).ToList()
                ignored(row, "same-id-new-entry")
                owner.UpdateVersions() : layout()
                row = owner.BrowserRows("正式版")(0) : layout()
                PointerEvent(row, Mouse.MouseDownEvent) : PointerEvent(row, Mouse.MouseUpEvent) : layout() : owner._pageSwitch.Finish()
                check(owner._downloadView = DownloadView.Install AndAlso owner._selectedEntry Is replacement,
                    "a fresh vanilla row after catalogue replacement opens the accepted metadata object")
                owner.InstanceName.Text = "保留自定义名称" : owner._nameEdited = True
                ignored(row, "existing-install-form")
                owner.SetDownloadView(DownloadView.Versions) : owner._pageSwitch.Finish() : layout()
                owner._loading = True
                PointerEvent(row, Mouse.MouseDownEvent) : PointerEvent(row, Mouse.MouseUpEvent) : layout() : owner._pageSwitch.Finish()
                check(owner._downloadView = DownloadView.Install AndAlso owner._selectedEntry Is replacement AndAlso Not owner._nameEdited,
                    "a still-current usable vanilla cache remains selectable during a background manifest refresh")
                owner._loading = False : owner.SetDownloadView(DownloadView.Versions) : owner._pageSwitch.Finish() : layout()
                Dim body = row.Parent
                Dim extra = BrowserEntry("fixture-supplement", "release", "1980-01-01T00:00:00Z") : extra.SupplementalId = extra.Id
                owner._versions = owner._versions.Concat({extra}).ToList() : owner.AddSupplementalVersionCards() : layout()
                PointerEvent(row, Mouse.MouseDownEvent) : PointerEvent(row, Mouse.MouseUpEvent) : layout()
                check(row.Parent Is body AndAlso owner._downloadView = DownloadView.Install AndAlso owner._selectedEntry Is replacement,
                    "incremental supplemental insertion retains the existing official row and its accepted click identity")
            End Using
        Finally
            owner._pageSwitch.Finish() : owner._categoryMotion.Finish() : owner._surfaceMotion.Finish()
            owner.CancelComponentRequests()
            host.Child = Nothing : owner.Content = original
            RestoreAppearance(appearance)
        End Try
    End Sub
End Class
