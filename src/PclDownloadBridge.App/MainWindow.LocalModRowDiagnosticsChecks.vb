Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Shared Function LocalModDiagnosticsArchiveForChecks(full As String, id As String, brokenToml As Boolean, brokenManifest As Boolean) As String
        IO.Directory.CreateDirectory(Path.GetDirectoryName(full))
        Using stream As New FileStream(full, FileMode.Create, FileAccess.Write), zip As New ZipArchive(stream, ZipArchiveMode.Create)
            Using writer As New StreamWriter(zip.CreateEntry("fabric.mod.json").Open(), New UTF8Encoding(False))
                writer.Write(New JObject From {{"schemaVersion", 1}, {"id", id}, {"name", "已读取的 Mod · " & id}, {"version", "2.3.4"},
                    {"description", "诊断描述：" & New String("长"c, 4200)}}.ToString())
            End Using
            If brokenToml Then
                Using writer As New StreamWriter(zip.CreateEntry("META-INF/mods.toml").Open(), New UTF8Encoding(False))
                    writer.Write("modLoader=""javafml""" & vbLf & "[[mods]]" & vbLf & "modId=[]" & vbLf)
                End Using
            End If
            If brokenManifest Then
                Using writer As New StreamWriter(zip.CreateEntry("META-INF/MANIFEST.MF").Open(), New UTF8Encoding(False))
                    writer.Write("Manifest-Version: 1.0" & vbCrLf & "not a valid header" & vbCrLf & vbCrLf)
                End Using
            End If
        End Using
        Return full
    End Function

    Private Sub VerifyLocalModRowDiagnosticsUi(directory As String, check As Action(Of Boolean, String))
        Dim nativeFocus = LocalModCheckGetNativeFocus(), owner As New MainWindow(True), original = owner.Content
        Dim host As New Border With {.Width = 850, .Height = 700}, layout = ControlLayout(host)
        FocusManager.SetIsFocusScope(host, True)
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using source = PreviewSurface.Attach(host, 850, 700)
                layout()
                Dim work = owner.VerifyLocalModRowDiagnosticsWorkflowAsync(directory, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 25000) Then Throw New TimeoutException("Local Mod row diagnostics did not settle.")
                work.GetAwaiter().GetResult()
            End Using
        Finally
            owner._closing = True : owner.CancelLocalModRequest() : owner.CancelManagementRequest()
            owner._managementMotion.Finish() : owner._surfaceMotion.Finish()
            host.Child = Nothing : owner.Content = original
            LocalModCheckSetNativeFocus(nativeFocus)
        End Try
    End Sub

    Private Async Function VerifyLocalModRowDiagnosticsWorkflowAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim folders = PrepareLocalModFixture(directory), game = folders(0).Location, mods = Path.Combine(game, "versions/Fabric 实例/mods")
        Dim mixedPath = LocalModDiagnosticsArchiveForChecks(Path.Combine(mods, "mixed-diagnostics.jar"), "mixed_diagnostics", True, False)
        Dim warningPath = LocalModDiagnosticsArchiveForChecks(Path.Combine(mods, "warning-diagnostics.jar"), "warning_diagnostics", False, True)
        Dim mixedBytes = File.ReadAllBytes(mixedPath), warningBytes = File.ReadAllBytes(warningPath), brokenBytes = File.ReadAllBytes(Path.Combine(mods, "broken.jar"))
        Dim protectedSettings = IO.Directory.GetFiles(Path.GetDirectoryName(game), "*", SearchOption.AllDirectories).
            Where(Function(full) Not full.Contains(Path.DirectorySeparatorChar & "mods" & Path.DirectorySeparatorChar)).
            ToDictionary(Function(full) full, Function(full) File.ReadAllBytes(full))
        ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(False) : layout()
        SelectManagedInstance(_managementInstances.Single(Function(item) item.Name = "Fabric 实例")) : layout()
        ShowLocalMods(Nothing, Nothing) : Await LoadLocalModsAsync(False) : layout()
        Dim mixed = _localMods.Single(Function(entry) entry.FileName = "mixed-diagnostics.jar")
        Dim mixedRow = DirectCast(_localModRowViews(mixed), PclLocalModItem), mixedTip = CStr(mixedRow.ToolTip)
        check(mixed.IsArchiveValid AndAlso mixed.Recognized AndAlso mixed.CanToggle AndAlso mixed.Version = "2.3.4" AndAlso mixed.LoaderSummary = "Fabric" AndAlso
            mixed.DisplayName.Contains("mixed_diagnostics") AndAlso mixed.Problem.Contains("mods.toml") AndAlso mixed.Description.StartsWith("诊断描述：", StringComparison.Ordinal),
            "real mixed-loader metadata retains its readable Fabric identity while recording the malformed TOML problem")
        check(mixedRow.TitleLabel.Text = mixed.DisplayName AndAlso mixedRow.VersionLabel.IsVisible AndAlso mixedRow.VersionLabel.Text.Contains("2.3.4") AndAlso
            mixedRow.InfoLabel.Text.Contains("mods.toml") AndAlso Not mixedRow.InfoLabel.Text.Contains("诊断描述："),
            "a mixed-loader error row retains its read name and version while giving the metadata problem visible priority")
        check(mixedTip.Contains(mixed.FileName) AndAlso mixedTip.Contains("已启用") AndAlso mixedTip.Contains("2.3.4") AndAlso mixedTip.Contains("Fabric") AndAlso mixedTip.Contains("声明"),
            "error-row diagnostics retain the physical enabled state read version and declared loader without replacing them with compatibility claims")
        check(mixedTip.Contains(mixed.Problem) AndAlso mixedTip.IndexOf(mixed.Problem, StringComparison.Ordinal) < mixedTip.IndexOf("诊断描述：", StringComparison.Ordinal) AndAlso mixedTip.Length <= 4096,
            "the full metadata problem appears before a long description in a bounded row tooltip")
        check(mixedRow.Tag Is mixed AndAlso mixedRow.ToggleButton.Tag Is mixed AndAlso mixedRow.ToggleButton.IsEnabled AndAlso
            mixedRow.Children.OfType(Of PclIconButton)().Single() Is mixedRow.ToggleButton,
            "a diagnosed archive keeps its exact catalogue identity and one enabled direct-child toggle action")
        Dim warning = _localMods.Single(Function(entry) entry.FileName = "warning-diagnostics.jar")
        Dim warningRow = DirectCast(_localModRowViews(warning), PclLocalModItem), warningTip = CStr(warningRow.ToolTip)
        check(warning.Recognized AndAlso String.IsNullOrEmpty(warning.Problem) AndAlso warning.Warnings.Count > 0 AndAlso warningRow.VersionLabel.Text.Contains("2.3.4") AndAlso
            warningRow.InfoLabel.Text.Contains("warning-diagnostics") AndAlso warningRow.InfoLabel.Text.Contains("诊断描述："),
            "an optional manifest warning preserves the normal filename and description row instead of becoming a fatal metadata error")
        check(warningTip.Contains(warning.Warnings(0)) AndAlso warningTip.IndexOf(warning.Warnings(0), StringComparison.Ordinal) < warningTip.IndexOf("诊断描述：", StringComparison.Ordinal) AndAlso warningTip.Length <= 4096,
            "a real manifest warning is not hidden behind a long Mod description")
        Dim broken = _localMods.Single(Function(entry) entry.FileName = "broken.jar"), brokenRow = DirectCast(_localModRowViews(broken), PclLocalModItem)
        check(Not broken.IsArchiveValid AndAlso broken.CanToggle AndAlso broken.Problem IsNot Nothing AndAlso brokenRow.ToggleButton.IsEnabled AndAlso brokenRow.InfoLabel.Text.Length > 0,
            "a genuinely damaged local archive remains visible with its exact disable action")
        Dim unknown = _localMods.Single(Function(entry) entry.FileName = "unknown.jar"), unknownRow = DirectCast(_localModRowViews(unknown), PclLocalModItem)
        check(unknown.IsArchiveValid AndAlso Not unknown.Recognized AndAlso unknown.Problem Is Nothing AndAlso unknown.Warnings.Count > 0 AndAlso
            unknownRow.ToggleButton.IsEnabled AndAlso CStr(unknownRow.ToolTip).Contains(unknown.Warnings(0)),
            "an unknown archive remains distinct from a diagnosed damaged archive without losing its warning or toggle")
        Dim rejectedSource = Path.Combine(directory, "local-mod-row-diagnostics-source-" & Guid.NewGuid().ToString("N"), "rejected-mixed.jar")
        IO.Directory.CreateDirectory(Path.GetDirectoryName(rejectedSource)) : File.WriteAllBytes(rejectedSource, mixedBytes)
        Await ImportLocalModFilesAsync({rejectedSource}) : layout()
        mixed = _localMods.Single(Function(entry) entry.FileName = "mixed-diagnostics.jar")
        mixedRow = DirectCast(_localModRowViews(mixed), PclLocalModItem)
        check(LocalModStatus.Text.Contains("无法导入") AndAlso Not File.Exists(Path.Combine(mods, "rejected-mixed.jar")) AndAlso File.ReadAllBytes(rejectedSource).SequenceEqual(mixedBytes) AndAlso
            mixedRow.VersionLabel.Text.Contains("2.3.4") AndAlso mixedRow.InfoLabel.Text.Contains("mods.toml"),
            "readable mixed-loader identity does not loosen the actual import rejection boundary or hide its error after refresh")
        Await ToggleLocalModAsync(mixed) : layout()
        Dim disabled = _localMods.Single(Function(entry) entry.FileName = "mixed-diagnostics.jar.disabled"), disabledRow = DirectCast(_localModRowViews(disabled), PclLocalModItem)
        Dim disabledTip = CStr(disabledRow.ToolTip)
        check(Not disabled.Enabled AndAlso disabled.CanToggle AndAlso disabled.Problem IsNot Nothing AndAlso disabled.Version = "2.3.4" AndAlso
            File.ReadAllBytes(mixedPath & ".disabled").SequenceEqual(mixedBytes) AndAlso Not File.Exists(mixedPath),
            "a real error-row disable keeps the same metadata and source bytes under PCL's disabled suffix")
        check(disabledRow.TitleLabel.TextDecorations IsNot Nothing AndAlso disabledRow.TitleLabel.TextDecorations.Any(Function(item) item.Location = TextDecorationLocation.Strikethrough) AndAlso
            disabledRow.DisabledBadge.IsVisible AndAlso disabledRow.ToggleButton.IsEnabled AndAlso disabledTip.Contains("已禁用") AndAlso Not disabledTip.Contains("已启用"),
            "an error-row disabled state remains explicit through strike decoration badge tooltip and a live enable action")
        check(disabledRow.VersionLabel.IsVisible AndAlso disabledRow.VersionLabel.Text.Contains("2.3.4") AndAlso disabledRow.InfoLabel.Text.Contains("mods.toml") AndAlso
            disabledTip.Contains("2.3.4") AndAlso disabledTip.Contains("Fabric") AndAlso disabledTip.Contains("声明"),
            "disabled error rows retain read version declared-loader diagnostics and visible problem priority")
        Await ToggleLocalModAsync(disabled) : layout()
        Dim restored = _localMods.Single(Function(entry) entry.FileName = "mixed-diagnostics.jar"), restoredRow = DirectCast(_localModRowViews(restored), PclLocalModItem)
        check(restored.Enabled AndAlso restoredRow.ToggleButton.IsEnabled AndAlso Not restoredRow.DisabledBadge.IsVisible AndAlso
            (restoredRow.TitleLabel.TextDecorations Is Nothing OrElse Not restoredRow.TitleLabel.TextDecorations.Any(Function(item) item.Location = TextDecorationLocation.Strikethrough)) AndAlso
            CStr(restoredRow.ToolTip).Contains("已启用") AndAlso File.ReadAllBytes(mixedPath).SequenceEqual(mixedBytes) AndAlso Not File.Exists(mixedPath & ".disabled"),
            "reenabling a diagnosed archive restores its original bytes and retires every disabled presentation marker")
        warning = _localMods.Single(Function(entry) entry.FileName = "warning-diagnostics.jar")
        Await ToggleLocalModAsync(warning) : layout()
        Dim disabledWarning = _localMods.Single(Function(entry) entry.FileName = "warning-diagnostics.jar.disabled")
        warningRow = DirectCast(_localModRowViews(disabledWarning), PclLocalModItem)
        check(disabledWarning.Problem Is Nothing AndAlso disabledWarning.Warnings.Count > 0 AndAlso CStr(warningRow.ToolTip).Contains(disabledWarning.Warnings(0)) AndAlso
            CStr(warningRow.ToolTip).Contains("已禁用") AndAlso warningRow.InfoLabel.Text.Contains("诊断描述：") AndAlso File.ReadAllBytes(warningPath & ".disabled").SequenceEqual(warningBytes),
            "a real warning-row disable preserves its warning normal description and exact archive bytes")
        Await ToggleLocalModAsync(disabledWarning) : layout()
        broken = _localMods.Single(Function(entry) entry.FileName = "broken.jar")
        Await ToggleLocalModAsync(broken) : layout()
        Dim disabledBroken = _localMods.Single(Function(entry) entry.FileName = "broken.jar.disabled")
        brokenRow = DirectCast(_localModRowViews(disabledBroken), PclLocalModItem)
        check(disabledBroken.CanToggle AndAlso Not disabledBroken.Enabled AndAlso disabledBroken.Problem IsNot Nothing AndAlso brokenRow.DisabledBadge.IsVisible AndAlso
            brokenRow.ToggleButton.IsEnabled AndAlso File.ReadAllBytes(Path.Combine(mods, "broken.jar.disabled")).SequenceEqual(brokenBytes),
            "damaged ZIP rows can be disabled through the real UI without discarding their problem or enable access")
        Await ToggleLocalModAsync(disabledBroken) : layout()
        check(File.ReadAllBytes(mixedPath).SequenceEqual(mixedBytes) AndAlso File.ReadAllBytes(warningPath).SequenceEqual(warningBytes) AndAlso
            File.ReadAllBytes(Path.Combine(mods, "broken.jar")).SequenceEqual(brokenBytes) AndAlso protectedSettings.All(Function(pair) File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),
            "diagnostic rendering failed import and real toggle roundtrips preserve all original Mod and external configuration bytes")
        InstalledInstanceCatalog.EnsureUnchanged(_localModInstance, CancellationToken.None)
    End Function
End Class
