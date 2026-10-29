Imports System.IO
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifySurfaceReentryUi(directory As String, passed As List(Of String))
        Dim observations As New JObject, measurements As New JArray
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                     File.WriteAllText(Path.Combine(directory, "surface-reentry.json"), observations.ToString())
                                                 End Sub
        For Each duringLayout In {False, True}
            For Each snapshot In {False, True}
                Dim panel As New Grid, content As New StackPanel With {.Opacity = 0.82}, ghost As New Canvas With {.IsHitTestVisible = False}
                Dim host As New Border With {.Width = 500, .Height = 400, .Child = panel}, layout = ControlLayout(host)
                panel.Children.Add(content) : panel.Children.Add(ghost)
                Dim motion As New PclSurfaceMotion(panel, ghost, Function() True, Function(root) PclSurfaceMotion.Units(root)) With {.PauseForChecks = True}
                Dim original As New ScaleTransform(0.97, 0.97), accepted As ClockGroup = Nothing, entered = 0, bound = False, enteredWhileBinding = False
                Dim card As PclCard = Nothing, all As New List(Of PclCard)
                Dim replace As Action =
                    Sub()
                        entered += 1 : enteredWhileBinding = motion.ApplyingContent
                        motion.Change(content, content,
                            Sub()
                                content.Children.Clear()
                                For index = 0 To 1
                                    Dim row = SurfaceReentryCard("Accepted " & index)
                                    row.Opacity = 0.93 : row.RenderTransform = original
                                    all.Add(row) : content.Children.Add(row)
                                Next
                                card = all(0) : content.Tag = "accepted"
                            End Sub, contentRefresh:=True)
                        accepted = motion.Transition
                    End Sub
                Dim notify As EventHandler = Sub()
                                                 If entered = 0 AndAlso bound Then replace()
                                             End Sub
                content.Children.Add(SurfaceReentryCard("Outgoing"))
                Using source = PreviewSurface.Attach(host, 500, 400)
                    layout() : FlushControlLayout()
                    If duringLayout Then AddHandler content.LayoutUpdated, notify
                    Try
                        motion.Change(If(snapshot, content, Nothing), content,
                            Sub()
                                content.Children.Clear() : content.Children.Add(SurfaceReentryCard("Middle"))
                                bound = True
                                If Not duringLayout Then replace()
                            End Sub, contentRefresh:=True)
                        Dim label = "/surface-reentry/layout=" & duringLayout & "/snapshot=" & snapshot
                        Dim sameClock = motion.Transition Is accepted, currentItems = motion.ItemCount
                        check(entered = 1 AndAlso accepted IsNot Nothing AndAlso sameClock AndAlso enteredWhileBinding = Not duringLayout,
                            "the accepted nested page owns its original clock after content or layout reentry" & label)
                        Dim group = TryCast(card.RenderTransform, TransformGroup)
                        check(currentItems = 2 AndAlso group IsNot Nothing AndAlso group.Children.Count = 2 AndAlso group.Children(0) Is original,
                            "retired entry cannot double-wrap the accepted cards or collect their zero opacity" & label)
                        accepted.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(180), TimeSeekOrigin.BeginTime) : FlushControlLayout() : layout()
                        check(panel.IsHitTestVisible AndAlso ghost.Children.Count = 0 AndAlso Math.Abs(content.Opacity - 0.82) < 0.00001,
                            "the accepted page reveals its own content and releases the exit layer and input gate" & label)
                        motion.Finish() : accepted.Controller.SkipToFill() : FlushControlLayout() : layout()
                        check(motion.Transition Is Nothing AndAlso motion.ItemCount = 0 AndAlso ghost.Children.Count = 0 AndAlso panel.IsHitTestVisible AndAlso
                            Math.Abs(content.Opacity - 0.82) < 0.00001 AndAlso all.All(Function(row) row.RenderTransform Is original AndAlso Math.Abs(row.Opacity - 0.93) < 0.00001),
                            "finishing and late old completion preserve original accepted opacity transforms and input" & label)
                        measurements.Add(New JObject From {{"case", label}, {"callbacks", entered}, {"enteredWhileBinding", enteredWhileBinding}, {"acceptedClockRetained", sameClock},
                            {"itemsBeforeFinish", currentItems}, {"surfaceOpacityAfterFinish", content.Opacity}, {"cardOpacityAfterFinish", card.Opacity}, {"originalTransformAfterFinish", card.RenderTransform Is original}})
                    Finally
                        RemoveHandler content.LayoutUpdated, notify
                        motion.Cancel() : accepted?.Controller.Remove()
                        content.BeginAnimation(UIElement.OpacityProperty, Nothing) : content.Opacity = 0.82
                        For Each row In all
                            row.BeginAnimation(UIElement.OpacityProperty, Nothing) : row.RenderTransform = original : row.Opacity = 0.93
                        Next
                    End Try
                End Using
            Next
        Next
        For Each duringLayout In {False, True}
            For Each cancel In {False, True}
                Dim panel As New Grid, content As New StackPanel With {.Opacity = 0.82}, ghost As New Canvas With {.IsHitTestVisible = False}
                Dim original As New ScaleTransform(0.97, 0.97), card = SurfaceReentryCard("Retained accepted content")
                card.RenderTransform = original : card.Opacity = 0.93 : content.Children.Add(card)
                panel.Children.Add(content) : panel.Children.Add(ghost)
                Dim host As New Border With {.Width = 500, .Height = 400, .Child = panel}, layout = ControlLayout(host)
                Dim motion As New PclSurfaceMotion(panel, ghost, Function() True, Function(root) PclSurfaceMotion.Units(root)) With {.PauseForChecks = True}
                Dim entered = 0, bound = False
                Dim stopEntry As Action = Sub()
                                              entered += 1
                                              If cancel Then motion.Cancel() Else motion.Finish()
                                          End Sub
                Dim notify As EventHandler = Sub()
                                                 If entered = 0 AndAlso bound Then stopEntry()
                                             End Sub
                Using source = PreviewSurface.Attach(host, 500, 400)
                    layout() : FlushControlLayout()
                    If duringLayout Then AddHandler content.LayoutUpdated, notify
                    Try
                        motion.Change(content, content, Sub()
                                                            card.Title = "Accepted without entry" : content.Tag = "accepted" : bound = True
                                                            If Not duringLayout Then stopEntry()
                                                        End Sub, contentRefresh:=True)
                        Dim label = "/surface-stop/layout=" & duringLayout & "/cancel=" & cancel
                        check(entered = 1 AndAlso motion.Transition Is Nothing AndAlso motion.ItemCount = 0 AndAlso ghost.Children.Count = 0 AndAlso panel.IsHitTestVisible,
                            "a stop during binding or layout cannot be followed by the retired entry's clock" & label)
                        check(CStr(content.Tag) = "accepted" AndAlso card.Title = "Accepted without entry" AndAlso card.RenderTransform Is original AndAlso
                            Math.Abs(content.Opacity - 0.82) < 0.00001 AndAlso Math.Abs(card.Opacity - 0.93) < 0.00001,
                            "a stopped pending entry retains its accepted content and original presentation" & label)
                        measurements.Add(New JObject From {{"case", label}, {"callbacks", entered}, {"clockAfterStop", motion.Transition IsNot Nothing}, {"surfaceOpacity", content.Opacity}, {"cardOpacity", card.Opacity}})
                    Finally
                        RemoveHandler content.LayoutUpdated, notify : motion.Cancel()
                    End Try
                End Using
            Next
        Next
        Dim page As New Grid, rows As New StackPanel With {.Opacity = 0.82}, overlay As New Canvas
        Dim baseTransform As New ScaleTransform(0.97, 0.97), rowCard = SurfaceReentryCard("Ordinary entry")
        rowCard.RenderTransform = baseTransform : rowCard.Opacity = 0.93 : rows.Children.Add(rowCard)
        page.Children.Add(rows) : page.Children.Add(overlay)
        Dim parent As New Border With {.Width = 500, .Height = 400, .Child = page}, arrange = ControlLayout(parent), enabled = True
        Dim normal As New PclSurfaceMotion(page, overlay, Function() enabled, Function(root) PclSurfaceMotion.Units(root)) With {.PauseForChecks = True}
        Using source = PreviewSurface.Attach(parent, 500, 400)
            arrange() : FlushControlLayout()
            Try
                normal.Change(Nothing, rows, Sub()
                                           End Sub)
                Dim first = normal.Transition
                check(first IsNot Nothing AndAlso normal.ItemCount = 1, "ordinary first entry still creates its visible card clock")
                first.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(180), TimeSeekOrigin.BeginTime) : FlushControlLayout()
                check(page.IsHitTestVisible AndAlso overlay.Children.Count = 0, "ordinary first entry still releases input at its reveal")
                normal.Change(rows, rows, Sub() rowCard.Title = "Ordinary replacement", contentRefresh:=True)
                Dim second = normal.Transition
                first.Controller.SkipToFill() : FlushControlLayout()
                check(second IsNot Nothing AndAlso second IsNot first AndAlso normal.Transition Is second, "an ordinary replacement survives the retired clock's late completion")
                normal.Finish() : arrange()
                check(normal.Transition Is Nothing AndAlso rowCard.RenderTransform Is baseTransform AndAlso Math.Abs(rowCard.Opacity - 0.93) < 0.00001 AndAlso
                    Math.Abs(rows.Opacity - 0.82) < 0.00001 AndAlso page.IsHitTestVisible AndAlso overlay.Children.Count = 0,
                    "ordinary completion restores authored opacity and transform without an exit layer")
                normal.Change(Nothing, rows, Sub() page.Width = 420)
                check(normal.Transition IsNot Nothing AndAlso normal.ItemCount = 1 AndAlso Math.Abs(page.ActualWidth - 420) < 0.01,
                    "the incoming content can measure a resized host before its own entry clock exists")
                page.Width = 430 : arrange() : FlushControlLayout()
                check(normal.Transition Is Nothing AndAlso rowCard.RenderTransform Is baseTransform AndAlso Math.Abs(rowCard.Opacity - 0.93) < 0.00001 AndAlso
                    Math.Abs(rows.Opacity - 0.82) < 0.00001 AndAlso page.IsHitTestVisible AndAlso overlay.Children.Count = 0,
                    "a later resize still settles the running clock and restores authored presentation")
                enabled = False : normal.Change(rows, rows, Sub() rowCard.Title = "Reduced motion", contentRefresh:=True)
                check(normal.Transition Is Nothing AndAlso rowCard.Title = "Reduced motion" AndAlso rowCard.RenderTransform Is baseTransform AndAlso page.IsHitTestVisible,
                    "disabled page animation still applies content synchronously without a clock")
                enabled = True : normal.Change(rows, rows, Sub() rowCard.Title = "Same content")
                check(normal.Transition Is Nothing AndAlso rowCard.Title = "Same content" AndAlso Math.Abs(rows.Opacity - 0.82) < 0.00001,
                    "the non-refresh same-surface fast path remains synchronous and neutral")
            Finally
                normal.Cancel()
            End Try
        End Using
        VerifyPackCompositionForSurfaceChecks(layoutMeasurements:=measurements, check:=check)
        File.WriteAllText(Path.Combine(directory, "surface-reentry-measurements.json"), measurements.ToString())
        Dim failed = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
        If failed.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failed))
    End Sub

    Private Shared Function SurfaceReentryCard(title As String) As PclCard
        Return New PclCard With {.Title = title, .Body = New Border With {.Height = 70}, .Margin = New Thickness(8)}
    End Function
    Private Sub VerifyPackCompositionForSurfaceChecks(layoutMeasurements As JArray, check As Action(Of Boolean, String))
        Dim owner As New MainWindow(True), root = owner.Content
        Dim host As New Border With {.Width = 900, .Height = 550}, layout = ControlLayout(host)
        owner.Content = Nothing : host.Child = DirectCast(root, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 550)
                owner.ShowDownload(Nothing, Nothing) : owner.SelectResourceKind(CommunityResourceKind.Mod)
                Dim project As New ModpackProject With {.Platform = ModpackPlatform.Modrinth, .Kind = CommunityResourceKind.Mod, .Id = "surfacecompose", .Name = "Cached composition"}
                owner.PrepareResourceBackDetail(project)
                owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner.FinishPackContentMotion() : layout()
                Dim before = owner.PackDetailResultsSurface.RenderTransform, calls = 0, task = owner._taskGeneration
                owner._motionPreview = True : owner._packDetailMotion.PauseForChecks = True
                owner.ChangePackContent(PackBrowserMode.Detail,
                    Sub()
                        owner.ChangePackContent(PackBrowserMode.Detail,
                            Sub()
                                calls += 1 : owner.RenderPackVersions()
                            End Sub)
                    End Sub)
                Dim clock = owner._packDetailMotion.Transition
                check(clock IsNot Nothing AndAlso owner._packDetailMotion.ItemCount > 0 AndAlso calls = 1 AndAlso owner._packDetailCompletedProject Is project,
                    "nested cached detail rendering composes into its parent entry instead of stopping that pending entry")
                If clock IsNot Nothing Then clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(180), TimeSeekOrigin.BeginTime)
                FlushControlLayout() : layout()
                check(clock IsNot Nothing AndAlso owner._packDetailMotion.Transition Is clock AndAlso owner.PackDetailResultsHost.IsHitTestVisible AndAlso
                    owner.PackDetailGhost.Children.Count = 0 AndAlso Math.Abs(owner.PackDetailResultsSurface.Opacity - 1) < 0.00001,
                    "composed cached detail keeps the accepted entry's reveal and input timing")
                owner.FinishPackContentMotion() : layout()
                check(owner._packDetailMotion.Transition Is Nothing AndAlso owner.PackDetailResultsSurface.RenderTransform Is before AndAlso
                    Math.Abs(owner.PackDetailResultsSurface.Opacity - 1) < 0.00001 AndAlso owner.PackDetailResultsHost.IsHitTestVisible AndAlso
                    owner.PackDetailGhost.Children.Count = 0 AndAlso owner._taskGeneration = task AndAlso calls = 1,
                    "composed detail completion restores its authored surface without repeating render or changing download ownership")
                layoutMeasurements.Add(New JObject From {{"case", "cached-detail-composition"}, {"clockCreated", clock IsNot Nothing}, {"renders", calls},
                    {"completedSameProject", owner._packDetailCompletedProject Is project}, {"finalOpacity", owner.PackDetailResultsSurface.Opacity}})
            End Using
        Finally
            owner._closing = True : owner.CancelResourceRequests() : owner.CancelManagementRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner.FinishPackContentMotion()
            host.Child = Nothing : owner.Content = root
        End Try
    End Sub
End Class
