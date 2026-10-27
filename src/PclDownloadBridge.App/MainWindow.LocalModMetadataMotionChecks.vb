Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyLocalModMetadataMotionUi(directory As String, passed As List(Of String))
        Dim failures As New List(Of String), observed = 0
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observed += 1
                                                     If ok Then passed.Add(label) Else failures.Add(label)
                                                 End Sub
        Dim saved = _appearance.ToJson(), previous = SynchronizationContext.Current, nativeFocus = LocalModCheckGetNativeFocus()
        Dim owner As New MainWindow(True), original = owner.Content
        Dim host As New Border With {.Width = 900, .Height = 700}, layout = ControlLayout(host)
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 700)
                owner.PrepareLocalModStylePage(Path.Combine(directory, "metadata-motion"), layout)
                For Each dark In {False, True}
                    owner.RestoreAppearance(New JObject From {{"darkMode", dark}}) : layout()
                    For Each enabled In {False, True}
                        Dim entry = owner._localMods.Single(Function(item) item.FileName = If(enabled, "first.jar", "second.jar.disabled"))
                        Dim row = DirectCast(owner._localModRowViews(entry), PclLocalModItem)
                        Dim marker = row.Children.OfType(Of Border)().Single(Function(item) item.Width = 5)
                        Dim project As New ModpackProject With {.Kind = CommunityResourceKind.Mod, .Platform = ModpackPlatform.Modrinth, .Id = "resource1", .Name = "fixture"}
                        Dim current As New ModpackRelease With {.Kind = project.Kind, .Platform = project.Platform, .ProjectId = project.Id, .Id = "before01", .Name = "before", .FileName = entry.FileName,
                            .Published = DateTimeOffset.Parse("2026-01-01Z"), .GameVersions = {owner._localModInstance.MinecraftVersion}, .Loaders = {"fabric"}, .Size = entry.Length, .Sha1 = New String("a"c, 40)}
                        Dim release = current.Copy() : release.Id = "after001" : release.Name = "after" : release.Published = current.Published.AddDays(1)
                        Dim update As New LocalModUpdate(entry, project, current, release)
                        For Each added In {False, True}
                            Dim suffix = "; dark=" & dark & "; enabled=" & enabled & "; updateAdded=" & added
                            row.MotionPolicy.EnabledForChecks = False
                            row.SetUpdateInfo(If(Not added, update, Nothing)) : row.Checked = True : layout()
                            row.MotionPolicy.EnabledForChecks = True : row.MotionPolicy.PauseForChecks = True
                            row.Checked = False
                            Dim exiting = LocalModSelectionClock(row)
                            If exiting Is Nothing Then Throw New InvalidOperationException("Metadata motion fixture requires a visible deselection clock" & suffix)
                            PauseSelectionClock(exiting)
                            exiting.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(60), TimeSeekOrigin.BeginTime) : layout()
                            Dim height = marker.Height, alpha = marker.Opacity
                            row.SetUpdateInfo(If(added, update, Nothing)) : layout()
                            check(LocalModSelectionClock(row) Is exiting AndAlso Math.Abs(marker.Height - height) < 0.001 AndAlso Math.Abs(marker.Opacity - alpha) < 0.001 AndAlso Not row.Checked,
                                "metadata-only changes retain the same partially exited Mod marker" & suffix)
                            Dim key = If(added, "ColorBrush3", If(enabled, "ColorBrush1", "ColorBrushGray4"))
                            check(row.HasUpdate = added AndAlso DirectCast(row.TitleLabel.Foreground, SolidColorBrush).Color = PclAnimation.Brush(key).Color,
                                "accepted update metadata refreshes title ink without waiting for selection geometry" & suffix)
                            exiting.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(100), TimeSeekOrigin.BeginTime) : layout()
                            Dim expectedHeight = 32 * (1 - Math.Pow(100.0 / 120, 2)), expectedAlpha = 1 - (100.0 - 40) / 70
                            check(Math.Abs(marker.Height - expectedHeight) < 0.001 AndAlso Math.Abs(marker.Opacity - expectedAlpha) < 0.001,
                                "the original Mod exit continues on its original hundred-ms sample after metadata refresh" & suffix)
                            exiting.Controller.Resume() : exiting.Controller.SkipToFill()
                            check(PumpMotionUntil(Function() LocalModSelectionClock(row) Is Nothing AndAlso marker.Height = 0 AndAlso marker.Opacity = 0, 500),
                                "the original Mod exit completes without a replacement marker clock or extended tail" & suffix)
                            row.MotionPolicy.EnabledForChecks = False : row.SetUpdateInfo(If(Not added, update, Nothing))
                            row.MotionPolicy.EnabledForChecks = True : row.SetUpdateInfo(If(added, update, Nothing))
                            check(LocalModSelectionClock(row) Is Nothing AndAlso row.HasUpdate = added AndAlso marker.Height = 0 AndAlso marker.Opacity = 0 AndAlso
                                DirectCast(row.TitleLabel.Foreground, SolidColorBrush).Color = PclAnimation.Brush(key).Color,
                                "idle metadata refreshes create no empty selection timeline" & suffix)
                            row.MotionPolicy.EnabledForChecks = False
                        Next
                    Next
                Next
            End Using
        Finally
            owner._closing = True : owner.CancelLocalModRequest() : owner.CancelManagementRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._managementMotion.Finish() : owner._categoryMotion.Finish()
            host.Child = Nothing : owner.Content = original
            RestoreAppearance(saved) : SynchronizationContext.SetSynchronizationContext(previous) : LocalModCheckSetNativeFocus(nativeFocus)
        End Try
        If failures.Count > 0 Then Throw New InvalidOperationException("Mod metadata motion failures: " & failures.Count & "/" & observed & vbCrLf & String.Join(vbCrLf, failures))
    End Sub
End Class
