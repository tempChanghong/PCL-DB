Imports System.IO
Imports System.Text
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Sub VerifyPclThemeFollow(directory As String, check As Action(Of Boolean, String))
        Dim count = 0
        Dim verify As Action(Of Boolean, String) = Sub(ok, label)
                                                      check(ok, label) : count += 1
                                                  End Sub
        VerifyPclThemeReader(directory, verify)
        VerifyPclThemeMonitor(verify)
        VerifyPclThemeIntegration(directory, verify)
        File.WriteAllText(Path.Combine(directory, "pcl-theme-follow-check-count.json"), New JObject From {{"checks", count}, {"source", "PCL 5ef81e4 Settings/ModBase/PageSetupUI"}}.ToString())
    End Sub

    Private Shared Sub VerifyPclThemeReader(directory As String, check As Action(Of Boolean, String))
        Dim parse As Func(Of String, PclThemeSnapshot) = Function(text) PclThemeSnapshot.Parse(New UTF8Encoding(False).GetBytes(text))
        Dim sample = parse("UiLauncherTheme:1" & vbCrLf & "LaunchFolderSelect:C:\Games\.minecraft" & vbCrLf)
        check(sample.Success AndAlso sample.ThemeId = 1 AndAlso sample.Name = "甜柠青" AndAlso sample.Preset = "cyan" AndAlso Not sample.IsDefault,
            "PCL theme reader uses the ordinary colon-separated theme ID and keeps colons inside other values")
        check(parse("UiLauncherTheme:2" & vbLf & "UiLauncherTheme:4").ThemeId = 4,
            "PCL theme reader preserves IniGetContent's last-duplicate-key rule")
        Dim defaults = parse("WindowHeight:550" & vbCrLf)
        check(defaults.Success AndAlso defaults.IsDefault AndAlso defaults.ThemeId = 0 AndAlso defaults.Preset = "blue",
            "a complete nonempty PCL configuration without a theme uses Settings' default zero")
        For Each invalid In {"", "UiLauncherTheme:", "UiLauncherTheme:NaN", "UiLauncherTheme:1.5", "UiLauncherTheme:2147483648", "UiLauncherTheme=1", "UiLauncherTheme", "UiLauncherTheme:2" & vbLf & "UiLauncherTheme"}
            check(Not parse(invalid).Success, "invalid or partial PCL theme text falls back without guessing: " & invalid.Replace(vbLf, "/"))
        Next
        check(parse("unrelated note" & vbLf & "UiLauncherTheme:3").ThemeId = 3, "unrelated non-record lines are ignored like PCL IniGetContent")
        check(Not PclThemeSnapshot.Parse({CByte(&HC3), CByte(&H28)}).Success AndAlso Not parse("UiLauncherTheme:1" & ChrW(0)).Success,
            "invalid UTF8 and embedded nulls are rejected before an external palette can be applied")
        Dim unicode = New UnicodeEncoding(False, True, True)
        Dim unicodeBytes = unicode.GetPreamble().Concat(unicode.GetBytes("UiLauncherTheme:3" & vbCrLf)).ToArray()
        check(PclThemeSnapshot.Parse(unicodeBytes).ThemeId = 3, "PCL theme reader accepts a BOM-marked UTF16 configuration")
        check(Not PclThemeSnapshot.Parse(New Byte(PclThemeSnapshot.MaximumBytes) {}).Success,
            "oversized PCL theme configuration is bounded before decoding")
        For Each id In {6, 12, 14, 999, -1}
            Dim unknown = parse("UiLauncherTheme:" & id)
            check(unknown.Success AndAlso unknown.ThemeId = id AndAlso unknown.Preset Is Nothing,
                "unsupported PCL theme IDs remain identifiable and never become an arbitrary preset: " & id)
        Next
        Dim root = Path.Combine(directory, "pcl-theme-sources")
        Dim first = Path.Combine(root, "一号 PCL", "PCL", "Setup.ini"), second = Path.Combine(root, "二号 PCL", "PCL", "Setup.ini")
        IO.Directory.CreateDirectory(Path.GetDirectoryName(first)) : IO.Directory.CreateDirectory(Path.GetDirectoryName(second))
        File.WriteAllText(first, "UiLauncherTheme:1" & vbCrLf, New UTF8Encoding(False))
        File.WriteAllText(second, "UiLauncherTheme:2" & vbCrLf, New UTF8Encoding(False))
        Dim before = File.ReadAllBytes(first), time = File.GetLastWriteTimeUtc(first), attributes = File.GetAttributes(first)
        check(PclThemeSnapshot.FromSelection(Path.Combine(root, "一号 PCL", "PCL2.exe")) = first AndAlso
            PclThemeSnapshot.FromSelection(second) = second, "PCL program and explicit Setup.ini selections bind one exact installation path")
        check(PclThemeSnapshot.Read(first).ThemeId = 1 AndAlso PclThemeSnapshot.Read(second).ThemeId = 2,
            "separate PCL installations keep their own current theme instead of selecting a process or game folder")
        File.SetAttributes(first, attributes Or FileAttributes.ReadOnly)
        Try
            check(PclThemeSnapshot.Read(first).Success AndAlso File.ReadAllBytes(first).SequenceEqual(before) AndAlso File.GetLastWriteTimeUtc(first) = time,
                "reading a read-only PCL configuration leaves its bytes and modification time intact")
        Finally
            File.SetAttributes(first, attributes)
        End Try
        Using locked As New FileStream(first, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            check(Not PclThemeSnapshot.Read(first).Success AndAlso File.ReadAllBytes(second).Length > 0,
                "an occupied configuration becomes a retryable read failure without redirecting to another PCL")
        End Using
        check(Not PclThemeSnapshot.Read(Path.Combine(root, "missing", "PCL", "Setup.ini")).Success AndAlso Not PclThemeSnapshot.Read("relative\Setup.ini").Success,
            "missing and drive-relative bindings report failure rather than searching other installations")
        Dim snapshot = PclThemeSnapshot.Read(first)
        File.Delete(first)
        check(Not PclThemeSnapshot.Read(first).Success AndAlso snapshot.Success AndAlso snapshot.ThemeId = 1,
            "a sampled PCL theme is immutable and deletion does not manufacture a new source")
        File.WriteAllText(first, "UiLauncherTheme:0" & vbCrLf)
        check(PclThemeSnapshot.Read(first).ThemeId = 0 AndAlso File.ReadAllBytes(second).SequenceEqual(New UTF8Encoding(False).GetBytes("UiLauncherTheme:2" & vbCrLf)),
            "recreating the selected PCL file is detected while the other installation remains byte-identical")
    End Sub

    Private Sub VerifyPclThemeMonitor(check As Action(Of Boolean, String))
        Dim outputs As New List(Of PclThemeSnapshot), current = "UiLauncherTheme:1", reads = 0
        Dim parse As Func(Of String, PclThemeSnapshot) = Function(value) PclThemeSnapshot.Parse(Encoding.UTF8.GetBytes(value))
        Using monitor As New PclThemeMonitor(Dispatcher, Sub(value) outputs.Add(value),
            Function(path)
                Interlocked.Increment(reads)
                Return parse(Volatile.Read(current))
            End Function, 60)
            monitor.Bind("first")
            check(PumpMotionUntil(Function() outputs.Count = 1, 1500) AndAlso outputs(0).ThemeId = 1,
                "live PCL monitoring publishes a new theme only after consistent background observations")
            Dim target = Volatile.Read(reads) + 3
            check(PumpMotionUntil(Function() Volatile.Read(reads) >= target, 1500) AndAlso outputs.Count = 1,
                "unchanged PCL observations do not repeatedly publish or rebuild the palette")
            Volatile.Write(current, "UiLauncherTheme:1" & vbCrLf & "WindowHeight:700")
            target = Volatile.Read(reads) + 3
            check(PumpMotionUntil(Function() Volatile.Read(reads) >= target, 1500) AndAlso outputs.Count = 1,
                "unrelated PCL configuration edits leave current-theme notifications unchanged")
            Volatile.Write(current, "UiLauncherTheme:2")
            check(PumpMotionUntil(Function() outputs.Count = 2, 1500) AndAlso outputs.Last().ThemeId = 2,
                "a stable live PCL theme change is delivered without restarting the launcher")
            monitor.Bind(Nothing)
            check(Not monitor.Active, "disabling PCL follow stops the monitoring timer immediately")
            Dim count = outputs.Count
            Volatile.Write(current, "UiLauncherTheme:3")
            monitor.Refresh() : FlushControlLayout()
            check(outputs.Count = count, "manual or queued refresh after unbinding cannot publish a different PCL theme")
        End Using
        Dim staleOutputs As New List(Of PclThemeSnapshot)
        Using started As New ManualResetEventSlim(False), release As New ManualResetEventSlim(False)
            Dim monitor As New PclThemeMonitor(Dispatcher, Sub(value) staleOutputs.Add(value),
                Function(path)
                    If path = "slow" Then
                        started.Set() : release.Wait(2000)
                        Return parse("UiLauncherTheme:1")
                    End If
                    Return parse("UiLauncherTheme:4")
                End Function, 60)
            Try
                monitor.Bind("slow")
                check(PumpMotionUntil(Function() started.IsSet, 1000), "a slow source read runs outside the UI dispatcher")
                monitor.Bind("new")
                check(PumpMotionUntil(Function() staleOutputs.Count = 1, 1500) AndAlso staleOutputs(0).ThemeId = 4,
                    "switching PCL installations can sample the new binding while a retired read is outstanding")
                release.Set()
                check(PumpMotionUntil(Function() monitor.PendingReads = 0, 1500) AndAlso staleOutputs.Count = 1,
                    "a late result from the old PCL path cannot overwrite the new installation's theme")
                monitor.Dispose()
                check(Not monitor.Active, "disposing PCL theme monitoring releases its active timer")
            Finally
                release.Set() : monitor.Dispose()
                PumpMotionUntil(Function() monitor.PendingReads = 0, 2000)
            End Try
        End Using
        Using started As New ManualResetEventSlim(False), release As New ManualResetEventSlim(False)
            Dim retired As New List(Of PclThemeSnapshot)
            Dim monitor As New PclThemeMonitor(Dispatcher, Sub(value) retired.Add(value),
                Function(path)
                    started.Set() : release.Wait(2000)
                    Return parse("UiLauncherTheme:0")
                End Function, 60)
            Try
                monitor.Bind("closing")
                check(PumpMotionUntil(Function() started.IsSet, 1000) AndAlso monitor.PendingReads = 1,
                    "closing-window fixture owns an outstanding background PCL read")
                monitor.Dispose() : release.Set()
                check(PumpMotionUntil(Function() monitor.PendingReads = 0, 1500) AndAlso retired.Count = 0 AndAlso Not monitor.Active,
                    "a PCL read completing after disposal drains without publishing or restarting polling")
            Finally
                release.Set() : monitor.Dispose()
                PumpMotionUntil(Function() monitor.PendingReads = 0, 2000)
            End Try
        End Using
    End Sub

    Private Sub VerifyPclThemeIntegration(directory As String, check As Action(Of Boolean, String))
        Dim owner As New MainWindow(True), content = DirectCast(owner.Content, FrameworkElement)
        Dim host As New Border With {.Width = 850, .Height = 700, .Child = Nothing}
        Dim saved = _appearance.ToJson()
        owner.Content = Nothing : host.Child = content
        Dim root = Path.Combine(directory, "pcl-theme-sources")
        Dim first = Path.Combine(root, "一号 PCL", "PCL", "Setup.ini"), second = Path.Combine(root, "二号 PCL", "PCL", "Setup.ini")
        Dim layout = ControlLayout(host)
        Try
            Using surface = PreviewSurface.Attach(host, 850, 700)
                owner.RestoreAppearance(New JObject From {{"followPclTheme", True}, {"pclThemeFile", second}})
                check(Not owner._pclThemeMonitor.Active AndAlso owner._pclThemeSnapshot Is Nothing,
                    "ordinary deterministic previews do not start disk monitoring from saved follow settings")
                owner.RestoreAppearance(New JObject From {{"theme", "deepblue"}, {"darkMode", True}, {"transparency", 450}})
                owner._followPreview = True
                owner.FollowPclThemeBox.IsChecked = True
                check(owner._appearance.FollowPclTheme AndAlso owner.PclThemeStatus.Text.Contains("请先选择") AndAlso owner._appearance.Theme = "deepblue",
                    "enabling unbound PCL follow retains the saved local fallback and asks for an explicit installation")
                owner.BindPclThemeSource(first)
                check(PumpMotionUntil(Function() owner._pclThemeSnapshot IsNot Nothing, 2500) AndAlso owner._pclThemeSnapshot.ThemeId = 0 AndAlso owner.PclThemeStatus.Text.Contains("正在跟随"),
                    "binding PCL applies its saved default theme after stable file reads")
                check(owner._appearance.Theme = "deepblue" AndAlso owner._appearance.DarkMode AndAlso owner._appearance.Transparency = 450 AndAlso Math.Abs(owner.Opacity - 0.85) < 0.001,
                    "following PCL overlays only the theme while retaining local dark mode opacity and fallback preferences")
                check(Not owner.AppearanceThemes.IsEnabled AndAlso Not owner.CustomThemeGrid.IsEnabled AndAlso owner.AppearanceThemeLabel.Text = "回退主题",
                    "follow mode identifies the retained fallback controls without offering misleading local theme edits")
                owner.BindPclThemeSource(second)
                check(PumpMotionUntil(Function() owner._pclThemeSnapshot IsNot Nothing, 2500) AndAlso owner._pclThemeSnapshot.ThemeId = 2 AndAlso owner.PclThemeStatus.Text.Contains("实验性外观") AndAlso Not owner._experimentalThemesUnlocked,
                    "a followed colour cannot silently unlock the existing experimental appearance gate")
                owner._developer.Enabled = True : owner._experimentalThemesUnlocked = True : owner.ApplyAppearance()
                check(owner.PclThemeStatus.Text.Contains("近似配色") AndAlso owner.FollowedAppearance(owner._appearance.Effective(True)).Theme = "green",
                    "supported PCL colour IDs use the existing preset adaptation and clearly identify approximate matching")
                Dim retained = owner._appearance.ToJson(), firstBytes = File.ReadAllBytes(first)
                owner.SelectVersion(owner._versions.Single(Function(item) item.Id = "1.20.1")) : owner._pageSwitch.Finish() : owner._installActionMotion.Finish()
                owner.SelectComponent(owner._componentEntries(ComponentKind.Fabric)(0))
                owner.InstanceName.Text = "跟随主题保留名称"
                Dim selected = owner._componentSelection.Item(ComponentKind.Fabric), card = owner._componentViews(ComponentKind.Fabric).Card, body = card.Body
                owner._motionPreview = True : owner._pageSwitch.CardMotion.PauseForChecks = True
                owner.SetDownloadView(DownloadView.Versions) : owner._pageSwitch.Finish() : owner.SetDownloadView(DownloadView.Install)
                PauseSelectionClock(owner._pageSwitch.Transition) : PauseSelectionClock(owner._pageSwitch.CardMotion.Transition)
                Dim page = owner._pageSwitch.Transition, cards = owner._pageSwitch.CardMotion.Transition
                Dim oldKey = owner._appliedPaletteKey
                File.WriteAllText(second, "UiLauncherTheme:3" & vbCrLf)
                check(PumpMotionUntil(Function() owner._pclThemeSnapshot.ThemeId = 3, 2500) AndAlso owner._appliedPaletteKey <> oldKey,
                    "a real PCL configuration edit updates the active palette during page motion")
                check(owner._pageSwitch.Transition Is page AndAlso owner._pageSwitch.CardMotion.Transition Is cards AndAlso owner._componentSelection.Item(ComponentKind.Fabric) Is selected AndAlso
                    card.Body Is body AndAlso owner.InstanceName.Text = "跟随主题保留名称" AndAlso JToken.DeepEquals(retained, owner._appearance.ToJson()),
                    "external palette changes retain page clocks component identity card body custom name and all saved appearance values")
                owner._pageSwitch.Finish() : owner._installActionMotion.Finish()
                owner.ShowSettings(Nothing, Nothing) : owner._surfaceMotion.Finish() : layout()
                SaveControlVisual(host, Path.Combine(directory, "follow-pcl-theme-dark.png"))
                File.WriteAllText(second, "UiLauncherTheme:14" & vbCrLf)
                check(PumpMotionUntil(Function() owner._pclThemeSnapshot.ThemeId = 14, 2500) AndAlso owner.PclThemeStatus.Text.Contains("暂不支持") AndAlso owner.FollowedAppearance(owner._appearance.Effective(True)).Theme = "deepblue",
                    "unsupported custom or dynamic PCL themes fall back locally without inventing hidden colour parameters")
                File.WriteAllText(second, "UiLauncherTheme:999" & vbCrLf)
                Dim unknownReady = PumpMotionUntil(Function() owner._pclThemeSnapshot.ThemeId = 999, 2500)
                File.WriteAllText(Path.Combine(directory, "pcl-theme-unknown-observation.json"), New JObject From {
                    {"ready", unknownReady}, {"theme", owner._pclThemeSnapshot.ThemeId}, {"status", owner.PclThemeStatus.Text},
                    {"success", owner._pclThemeSnapshot.Success}, {"active", owner._pclThemeMonitor.Active}, {"pending", owner._pclThemeMonitor.PendingReads},
                    {"bound", owner._appearance.PclThemeFile}, {"actual", File.ReadAllText(second)}}.ToString())
                check(unknownReady AndAlso owner.PclThemeStatus.Text.Contains("未知主题"),
                    "unknown future PCL theme IDs explain their fallback without dropping the source binding")
                Using locked As New FileStream(second, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                    check(PumpMotionUntil(Function() Not owner._pclThemeSnapshot.Success, 2500) AndAlso owner.PclThemeStatus.Text.Contains("本机外观") AndAlso owner._appearance.PclThemeFile = second,
                        "persistent sharing failure uses the local fallback while retaining the exact PCL binding for retry")
                End Using
                Dim temp = second & ".replace"
                File.WriteAllText(temp, "UiLauncherTheme:1" & vbCrLf)
                File.Replace(temp, second, Nothing)
                check(PumpMotionUntil(Function() owner._pclThemeSnapshot.Success AndAlso owner._pclThemeSnapshot.ThemeId = 1, 2500) AndAlso owner.PclThemeStatus.Text.Contains("正在跟随"),
                    "atomic PCL configuration replacement recovers automatically from a prior sharing failure")
                File.Delete(second)
                check(PumpMotionUntil(Function() Not owner._pclThemeSnapshot.Success, 2500), "deleting the bound configuration falls back instead of sampling another installation")
                File.WriteAllText(second, "UiLauncherTheme:4" & vbCrLf)
                check(PumpMotionUntil(Function() owner._pclThemeSnapshot.Success AndAlso owner._pclThemeSnapshot.ThemeId = 4, 2500),
                    "recreating the bound configuration resumes live follow without rebinding")
                check(File.ReadAllBytes(first).SequenceEqual(firstBytes), "all follow refreshes leave the other PCL installation's bytes unchanged")
                Dim preferencesPath = Path.Combine(root, "bridge-settings.json"), legacy = Path.Combine(root, "old-settings.json")
                DownloadSettingsFile.Save(preferencesPath, New JObject From {{"gameRoot", "keep-game"}, {"appearance", New JObject From {{"unknown", "keep"}}}})
                owner.SaveAppearanceValues(preferencesPath, legacy)
                Dim stored = JObject.Parse(File.ReadAllText(preferencesPath))
                check(CStr(stored("gameRoot")) = "keep-game" AndAlso CStr(stored("appearance")("unknown")) = "keep" AndAlso
                    CStr(stored("appearance")("theme")) = "deepblue" AndAlso CStr(stored("appearance")("pclThemeFile")) = second AndAlso CBool(stored("appearance")("followPclTheme")),
                    "saving follow preferences merges only local appearance data and preserves unrelated settings and future fields")
                Dim roundTrip = AppearancePreferences.Read(stored("appearance"))
                check(roundTrip.FollowPclTheme AndAlso roundTrip.PclThemeFile = second AndAlso roundTrip.Theme = "deepblue",
                    "follow binding and local fallback survive a settings round trip without persisting sampled PCL colours")
                owner.FollowPclThemeBox.IsChecked = False
                check(Not owner._pclThemeMonitor.Active AndAlso owner._pclThemeSnapshot Is Nothing AndAlso owner.AppearanceThemes.IsEnabled AndAlso owner._appearance.PclThemeFile = second AndAlso owner._appearance.Theme = "deepblue",
                    "turning follow off stops polling and restores editable local appearance while retaining the chosen PCL path")
                owner.RestoreAppearance(New JObject From {{"followPclTheme", True}, {"pclThemeFile", "relative\Setup.ini"}})
                check(PumpMotionUntil(Function() owner._pclThemeSnapshot IsNot Nothing, 2500) AndAlso Not owner._pclThemeSnapshot.Success,
                    "invalid restored bindings fail conservatively rather than resolving against the launch working directory")
                owner.RestoreAppearance(New JObject From {{"followPclTheme", True}, {"pclThemeFile", second}, {"darkMode", False}})
                check(PumpMotionUntil(Function() owner._pclThemeSnapshot IsNot Nothing AndAlso owner._pclThemeSnapshot.Success, 2500),
                    "restoring a valid explicit binding restarts only its own monitor")
                host.Width = 810 : host.Height = 470 : layout()
                owner.SettingsPage.ScrollToVerticalOffset(160) : layout()
                SaveControlVisual(host, Path.Combine(directory, "follow-pcl-theme-minimum.png"))
                check(owner.PclThemePathBox.ActualWidth > 150 AndAlso owner.PclThemePathBox.ActualHeight >= 28 AndAlso owner.PclThemeStatus.Text.Length > 0,
                    "follow path buttons and wrapping status remain usable at the minimum window size")
                owner._pclThemeMonitor.Dispose()
                check(Not owner._pclThemeMonitor.Active, "window shutdown can dispose the follow monitor without restarting any page animation")
            End Using
        Finally
            owner._followPreview = False : owner._pclThemeMonitor.Dispose()
            owner._pageSwitch.Finish() : owner._installActionMotion.Finish() : owner._surfaceMotion.Finish() : owner._categoryMotion.Finish()
            owner.CancelComponentRequests()
            host.Child = Nothing : owner.Content = content
            RestoreAppearance(saved)
        End Try
    End Sub
End Class
