Imports System.IO
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Sub VerifyIsolationOptions(check As Action(Of Boolean, String), directory As String)
        Dim original = IsolationBox.SelectedIndex, repair = RepairBox.IsChecked
        Dim baseline = _preferenceBaseline, baselinePath = _preferenceBaselinePath
        Dim root = IO.Path.Combine(directory, "isolation-preferences-" & Guid.NewGuid().ToString("N"))
        IO.Directory.CreateDirectory(root)
        Dim path = IO.Path.Combine(root, "settings.json"), legacy = IO.Path.Combine(root, "legacy.json")
        Try
            check(IsolationBox.Items.Count = 6 AndAlso original = 0 AndAlso
                String.Join("/", IsolationBox.Items.Cast(Of PclComboBoxItem)().Select(Function(item) CStr(item.Content))) =
                "跟随 PCL/关闭/隔离可安装 Mod 的版本/隔离非正式版/隔离可安装 Mod 的版本与非正式版/隔离所有版本",
                "global settings offer follow PCL plus its five default isolation strategies")
            check(IsolationBox.Parent Is FolderSettingsGrid AndAlso GameFolderCard.Parent Is SettingsContent AndAlso Grid.GetRow(IsolationBox) = 5,
                "default isolation is a single settings row instead of an installation card")
            check(Not DirectCast(InstallScroll.Content, StackPanel).Children.OfType(Of PclCard)().Any(Function(card) card.Title.Contains("隔离")),
                "the vanilla install page has no per-instance isolation selector or isolation card")
            For index = 0 To 5
                IsolationBox.SelectedIndex = index
                check(SelectedIsolationStrategy().GetValueOrDefault(-1) = index - 1 AndAlso CInt(BuildSettings(0)("defaultIsolation")) = index - 1,
                    "global isolation selection persists PCL semantics without reusing the three-state install enum: " & index)
                SavePreferenceValues(path, legacy, 0, True)
                IsolationBox.SelectedIndex = If(index = 0, 5, 0)
                RestoreIsolationPreference(DownloadSettingsFile.LoadCompatible(path, legacy))
                check(IsolationBox.SelectedIndex = index, "global isolation survives settings save and restore: " & index)
            Next
            RestoreIsolationPreference(New JObject())
            check(IsolationBox.SelectedIndex = 0 AndAlso Not SelectedIsolationStrategy().HasValue, "legacy settings without isolation keep following the selected PCL")
            For Each invalid As JToken In New JToken() {New JValue(-2), New JValue(5), New JValue(True), New JValue(1.5), New JValue("bad"), New JObject(), New JArray(), JValue.CreateNull()}
                IsolationBox.SelectedIndex = 5 : RestoreIsolationPreference(New JObject From {{"defaultIsolation", invalid}})
                check(Not SelectedIsolationStrategy().HasValue, "invalid global isolation safely falls back to follow PCL: " & invalid.Type.ToString() & "/" & invalid.ToString(Newtonsoft.Json.Formatting.None))
            Next
            RestoreIsolationPreference(New JObject From {{"defaultIsolation", "3"}})
            check(SelectedIsolationStrategy().GetValueOrDefault(-1) = 3 AndAlso IsolationBox.SelectedIndex = 4, "a numeric persisted strategy string restores the same PCL default")
            IsolationBox.SelectedIndex = 1
            check(IsolationHint.Text.Contains("共用") AndAlso IsolationHint.Text.Contains("不会被覆盖") AndAlso IsolationHint.Text.Contains("已有实例"),
                "the global shared-folder hint explains conflicts and preserves existing instances")
            SetBusy(True)
            check(Not IsolationBox.IsEnabled AndAlso Not GameFolderCard.IsEnabled, "global isolation settings are frozen while installing")
            SetBusy(False) : RepairBox.IsChecked = True
            check(IsolationBox.IsEnabled AndAlso IsolationHint.Text.Contains("校验补全保留原设置"), "repair selection does not disable the future global default or change repair preservation")
            RepairBox.IsChecked = False
            check(IsolationBox.IsEnabled AndAlso SelectedIsolationStrategy().GetValueOrDefault(-1) = 0, "leaving repair mode preserves the shared global default")
            IsolationBox.SelectedIndex = -1
            check(Not SelectedIsolationStrategy().HasValue, "an absent global isolation selection safely follows PCL")
            IsolationBox.SelectedIndex = 1 : SavePreferenceValues(path, legacy, 0, True)
            DownloadSettingsFile.SaveChanges(path, legacy, New JObject From {{"defaultIsolation", 4}})
            SavePreferenceValues(path, legacy, 1.25)
            Dim saved = DownloadSettingsFile.LoadCompatible(path, legacy)
            check(CInt(saved("defaultIsolation")) = 4 AndAlso CStr(saved("speed")) = "1.25", "saving unrelated preferences cannot revert a global isolation default changed by another window")
            IsolationBox.SelectedIndex = 3 : SavePreferenceValues(path, legacy, 1.25)
            check(CInt(DownloadSettingsFile.LoadCompatible(path, legacy)("defaultIsolation")) = 2, "an intentional global isolation edit participates in the existing atomic settings merge")
        Finally
            IsolationBox.SelectedIndex = original : RepairBox.IsChecked = repair
            _preferenceBaseline = baseline : _preferenceBaselinePath = baselinePath
            RefreshIsolation(False)
        End Try
        VerifyIsolationVisuals(check, directory)
    End Sub

    Private Sub VerifyIsolationVisuals(check As Action(Of Boolean, String), directory As String)
        Dim owner As New MainWindow(True), original = owner.Content, appearance = _appearance.ToJson()
        Dim host As New Border With {.Width = 850, .Height = 600}, layout = ControlLayout(host)
        Try
            owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
            Using source = PreviewSurface.Attach(host, 850, 600)
                owner.DownloadTab.MotionPolicy.EnabledForChecks = False
                owner.SettingsTab.MotionPolicy.EnabledForChecks = False
                layout() : owner.ShowSettings(Nothing, Nothing) : owner._surfaceMotion.Finish() : layout()
                owner.GameFolderCard.BringIntoView() : layout()
                owner.SettingsPage.ScrollToVerticalOffset(owner.SettingsPage.VerticalOffset + owner.GameFolderCard.TranslatePoint(New Point(), owner.SettingsPage).Y - 15) : layout()
                Dim top = owner.IsolationBox.TranslatePoint(New Point(), owner.SettingsPage).Y
                check(owner.SettingsPage.IsAncestorOf(owner.IsolationBox) AndAlso top >= 0 AndAlso top + owner.IsolationBox.ActualHeight <= owner.SettingsPage.ViewportHeight,
                    "the global isolation row is visible within the settings viewport and outside the download surface")
                SaveControlVisual(host, IO.Path.Combine(directory, "global-isolation-settings-light.png"))

                owner.SelectVersion(owner._versions.Single(Function(entry) entry.Id = "1.20.1"))
                owner.ShowDownload(Nothing, Nothing) : owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner._pageSwitch.Finish() : layout()
                Dim vanillaBottom = owner.VanillaCard.TranslatePoint(New Point(0, owner.VanillaCard.ActualHeight), owner.InstallScroll).Y
                Dim componentTop = owner._componentViews(PclDownloadBridge.Core.ComponentKind.Forge).Card.TranslatePoint(New Point(), owner.InstallScroll).Y
                check(Math.Abs(componentTop - vanillaBottom - 12) < 0.5 AndAlso Not owner.InstallPage.IsAncestorOf(owner.IsolationBox),
                    "the vanilla install page restores the direct PCL spacing from Minecraft to component cards")
                SaveControlVisual(host, IO.Path.Combine(directory, "global-isolation-install.png"))

                host.Width = 810 : host.Height = 470
                owner.RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", True}})
                owner.IsolationBox.SelectedIndex = 4
                owner.ShowSettings(Nothing, Nothing) : owner._surfaceMotion.Finish() : layout()
                owner.GameFolderCard.BringIntoView() : layout()
                owner.SettingsPage.ScrollToVerticalOffset(owner.SettingsPage.VerticalOffset + owner.GameFolderCard.TranslatePoint(New Point(), owner.SettingsPage).Y - 15) : layout()
                top = owner.IsolationBox.TranslatePoint(New Point(), owner.SettingsPage).Y
                check(owner._appearance.DarkMode AndAlso top >= 0 AndAlso top + owner.IsolationBox.ActualHeight <= owner.SettingsPage.ViewportHeight AndAlso owner.IsolationBox.ActualWidth > 300,
                    "the longest default isolation choice remains visible in dark settings at minimum window size")
                Dim mark = DirectCast(owner.DarkModeBox.Template.FindName("ShapeCheck", owner.DarkModeBox), Shapes.Path)
                If Not PumpMotionUntil(Function() Math.Abs(DirectCast(mark.RenderTransform, ScaleTransform).ScaleX - 1) < 0.01, 1500) Then Throw New TimeoutException("Dark checkbox did not settle for the isolation preview.")
                SaveControlVisual(host, IO.Path.Combine(directory, "global-isolation-settings-dark.png"))
            End Using
        Finally
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner._pageSwitch.Finish()
            host.Child = Nothing : owner.Content = original
            RestoreAppearance(appearance)
        End Try
    End Sub
End Class
