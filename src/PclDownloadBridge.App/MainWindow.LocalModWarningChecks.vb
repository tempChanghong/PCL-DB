Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Windows
Imports System.Windows.Controls
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    ' PCL 5ef81e4 PageInstanceMod.Info_Click keeps file identity and local
    ' metadata available in the detail flow. Our bounded tooltip must retain
    ' local diagnostics even when a Mod supplies the maximum description size.
    Private Sub VerifyLocalModWarningUi(directory As String, check As Action(Of Boolean, String))
        Dim owner As New MainWindow(True), original = owner.Content
        Dim host As New Border With {.Width = 850, .Height = 700}, layout = ControlLayout(host)
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using source = PreviewSurface.Attach(host, 850, 700)
                layout()
                Dim folders = owner.PrepareLocalModFixture(directory), game = folders(0).Location
                Dim mods = Path.Combine(game, "versions/Fabric 实例/mods")
                For Each hasProblem In {False, True}
                    Dim name = If(hasProblem, "long-problem.jar", "long-warning.jar")
                    Dim full = Path.Combine(mods, name)
                    Using stream As New FileStream(full, FileMode.CreateNew, FileAccess.Write), zip As New ZipArchive(stream, ZipArchiveMode.Create)
                        Using output = zip.CreateEntry("fabric.mod.json").Open(), writer As New StreamWriter(output, New UTF8Encoding(False))
                            writer.Write(New JObject From {{"schemaVersion", 1}, {"id", If(hasProblem, "long_problem", "long_warning")},
                                {"version", "1.0"}, {"name", name}, {"description", New String("D"c, 4096)}}.ToString())
                        End Using
                        If hasProblem Then
                            Using output = zip.CreateEntry("META-INF/mods.toml").Open(), writer As New StreamWriter(output, New UTF8Encoding(False))
                                writer.Write("no valid mods table")
                            End Using
                        End If
                    End Using
                    File.Copy(full, full & ".disabled")
                Next
                Dim protectedBytes = IO.Directory.GetFiles(Path.GetDirectoryName(game), "*", SearchOption.AllDirectories).
                    ToDictionary(Function(full) full, Function(full) File.ReadAllBytes(full))
                owner.ShowManagement(Nothing, Nothing) : owner.ScanManagementAsync(False).GetAwaiter().GetResult() : layout()
                owner.SelectManagedInstance(owner._managementInstances.Single(Function(item) item.Name = "Fabric 实例")) : layout()
                owner.ShowLocalMods(Nothing, Nothing) : owner.LoadLocalModsAsync(False).GetAwaiter().GetResult() : layout()
                For Each hasProblem In {False, True}
                    Dim name = If(hasProblem, "long-problem.jar", "long-warning.jar")
                    Dim entry = owner._localMods.Single(Function(item) item.FileName = name)
                    Dim itemView = DirectCast(owner._localModRowViews(entry), PclLocalModItem)
                    Dim tooltip = TryCast(itemView.ToolTip, String)
                    check(entry.Recognized AndAlso entry.Description.Length = 4096 AndAlso entry.Warnings.Count >= 2 AndAlso
                        (entry.Problem IsNot Nothing) = hasProblem,
                        "real local metadata retains a maximum-length description and both state-alias and duplicate-ID diagnostics: " & name)
                    check(tooltip IsNot Nothing AndAlso tooltip.Length <= 4096 AndAlso tooltip.Contains(entry.FileName) AndAlso
                        entry.Warnings.All(Function(warning) tooltip.Contains(warning)) AndAlso (entry.Problem Is Nothing OrElse tooltip.Contains(entry.Problem)),
                        "the actual bounded row tooltip preserves file identity and every diagnostic before optional description text: " & name)
                    check(tooltip.IndexOf(entry.Warnings(0), StringComparison.Ordinal) < tooltip.IndexOf("DDDD", StringComparison.Ordinal) AndAlso
                        (entry.Problem Is Nothing OrElse tooltip.IndexOf(entry.Problem, StringComparison.Ordinal) < tooltip.IndexOf("DDDD", StringComparison.Ordinal)),
                        "long Mod descriptions follow the actionable problem and warning text in the rendered row: " & name)
                Next
                check(protectedBytes.All(Function(pair) File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),
                    "rendering long-description diagnostics preserves every source archive and external launcher or instance setting byte")
            End Using
        Finally
            owner._closing = True : owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._managementMotion.Finish() : owner._surfaceMotion.Finish()
            host.Child = Nothing : owner.Content = original
        End Try
    End Sub
End Class
