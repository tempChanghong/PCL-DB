Imports System.IO
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Sub OpenExperimentalPreview()
        If Not _preview Then Throw New InvalidOperationException("Test fixtures require preview mode.")
        _developer.Enabled = True
        _experimentalThemesUnlocked = True
        RefreshDeveloperUi()
    End Sub

    Public Function VerifyDeveloperUi(directory As String, Optional passed As List(Of String) = Nothing) As List(Of String)
        If Not _preview Then Throw New InvalidOperationException("Developer checks require isolated preview mode.")
        passed = If(passed, New List(Of String))
        Dim check As Action(Of Boolean, String) = Sub(condition, label)
                                                     If Not condition Then Throw New InvalidOperationException(label)
                                                     passed.Add(label)
                                                 End Sub
        VerifyDeveloper(check, directory)
        Return passed
    End Function

    Private Sub VerifyDeveloper(check As Action(Of Boolean, String), artifactDirectory As String)
        Dim saved = _appearance.ToJson(), savedDeveloper = _developer.ToJson(), savedUnlocked = _experimentalThemesUnlocked
        Try
            RestoreDeveloperSettings(Nothing) : RestoreAppearance(Nothing)
            check(Not _developer.Enabled AndAlso Not ExperimentalThemesAvailable AndAlso DeveloperOptions.Visibility = Visibility.Collapsed, "developer settings start disabled with experimental themes locked")
            check(AppearanceThemes.Children.OfType(Of RadioButton)().Where(Function(r) r.Visibility = Visibility.Visible).Select(Function(r) CStr(r.Tag)).SequenceEqual(AppearancePreferences.PublicThemes), "ordinary personalization shows only the three blue palettes")
            RestoreAppearance(New JObject From {{"theme", "green"}, {"future", "keep"}})
            check(_appearance.Theme = "green" AndAlso _appearance.Effective(False).Theme = "blue" AndAlso DirectCast(Application.Current.Resources("ColorBrush3"), SolidColorBrush).Color = PclTheme.Hsl2(210, 85, 55), "legacy non-blue preference is retained but startup applies blue while locked")
            OpacitySlider.Value = 500
            check(_appearance.Theme = "green" AndAlso CStr(BuildSettings(0)("appearance")("theme")) = "green", "unrelated appearance edits preserve the locked legacy theme preference")
            AppearanceThemes.Children.OfType(Of RadioButton)().Single(Function(r) CStr(r.Tag) = "yellow").IsChecked = True
            check(_appearance.Theme = "green" AndAlso AppearanceThemes.Children.OfType(Of RadioButton)().Single(Function(r) r.IsChecked.GetValueOrDefault()).Tag.ToString() = "blue", "hidden radio events cannot select an experimental theme while locked")
            RestoreDeveloperSettings(New JObject From {{"enabled", True}, {"showDiagnostics", True}, {"unlocked", True}})
            check(_developer.Enabled AndAlso Not ExperimentalThemesAvailable AndAlso DeveloperOptions.Visibility = Visibility.Visible, "persisted developer enablement never restores an unlock flag")
            check(Not _developer.ToJson().Properties().Any(Function(p) p.Name.Contains("unlock") OrElse p.Name.Contains("code") OrElse p.Name.Contains("password")), "developer preferences contain neither credentials nor persistent unlock state")
            DeveloperCodeBox.Password = "invalid fixture code"
            UnlockDeveloperThemes(Me, New RoutedEventArgs())
            check(Not ExperimentalThemesAvailable AndAlso DeveloperCodeBox.Password.Length = 0 AndAlso DeveloperUnlockStatus.Text = "验证码不正确。", "wrong verification code is cleared and leaves experimental choices locked")
            check(DeveloperAccess.MatchesDigest("abc", "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD"), "verification uses the standard SHA-256 UTF-8 test vector")
            check(Not DeveloperAccess.MatchesDigest("ABC", "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD") AndAlso Not DeveloperAccess.Verify("") AndAlso Not DeveloperAccess.Verify(New String("x"c, 129)), "verification is case-sensitive and rejects empty or overlong inputs")
            check(Not DeveloperAccess.MatchesDigest("abc", New String("z"c, 64)), "malformed digest input is rejected without throwing")
            Dim suppliedCode = Environment.GetEnvironmentVariable("PCL_DB_TEST_CODE")
            If suppliedCode IsNot Nothing Then
                DeveloperCodeBox.Password = suppliedCode
                UnlockDeveloperThemes(Me, New RoutedEventArgs())
                If Not ExperimentalThemesAvailable OrElse DeveloperCodeBox.Password.Length <> 0 Then Throw New InvalidOperationException("Configured code did not unlock and clear through the real UI handler.")
            Else
                OpenExperimentalPreview()
            End If
            check(ExperimentalThemesAvailable AndAlso DeveloperUnlockRow.Visibility = Visibility.Collapsed AndAlso DeveloperLockButton.Visibility = Visibility.Visible, "unlocked session replaces the code entry with a relock action")
            check(AppearanceThemes.Children.OfType(Of RadioButton)().All(Function(r) r.Visibility = Visibility.Visible) AndAlso CustomThemeGrid.Parent Is AppearanceGrid, "experimental choices join the same personalization list rather than a second theme editor")
            SelectAppearanceTheme("custom")
            check(CustomThemeGrid.Visibility = Visibility.Visible AndAlso _appearance.Theme = "custom", "unlocked custom controls remain inside personalization")
            SelectAppearanceTheme("deepblue") : SelectAppearanceTheme("colorful")
            LockDeveloperThemes(Me, New RoutedEventArgs())
            check(Not ExperimentalThemesAvailable AndAlso _appearance.Theme = "colorful" AndAlso _appearance.Effective(False).Theme = "deepblue" AndAlso TitleTexture.Visibility = Visibility.Collapsed, "relocking preserves the experimental preference and restores the last public blue palette")
            OpenExperimentalPreview()
            DeveloperEnabledBox.IsChecked = False
            check(Not ExperimentalThemesAvailable AndAlso DeveloperOptions.Visibility = Visibility.Collapsed AndAlso DeveloperCodeBox.Password.Length = 0, "disabling developer settings clears session access and collapses its sections")
            DeveloperEnabledBox.IsChecked = True
            check(Not ExperimentalThemesAvailable, "reenabling developer settings requires verification again")
            SelectAppearanceTheme("blue") : DarkModeBox.IsChecked = True
            check(_appearance.DarkMode AndAlso DirectCast(Shell.Background, SolidColorBrush).Color = Color.FromRgb(19, 27, 38), "dark mode applies a dark neutral shell while preserving a blue accent")
            check(DirectCast(Application.Current.Resources("CardSurfaceBrush"), SolidColorBrush).Color.R < 50 AndAlso DirectCast(Application.Current.Resources("ColorBrush1"), SolidColorBrush).Color.R > 200, "dark mode uses light text and dark card surfaces")
            check(CBool(AppearancePreferences.Read(_appearance.ToJson()).ToJson()("darkMode")), "dark mode survives appearance serialization")
            DarkModeBox.IsChecked = False
            check(DirectCast(Application.Current.Resources("ColorBrushGray1"), SolidColorBrush).Color = Color.FromRgb(64, 64, 64), "returning to light mode restores the original neutral palette")
            DeveloperDiagnosticsBox.IsChecked = True
            UpdateDeveloperDiagnostics(New TransferProgress With {.Phase = "files", .ActiveConnections = 7, .Retries = 2})
            check(DeveloperTaskInfo.Visibility = Visibility.Visible AndAlso DeveloperTaskInfo.Text.Contains("7") AndAlso DeveloperTaskInfo.Text.Contains("2"), "general developer diagnostics display actual transfer sampling without experimental access")
            DeveloperEnabledBox.IsChecked = False
            check(DeveloperTaskInfo.Visibility = Visibility.Collapsed, "developer diagnostics disappear when developer mode is disabled")
            check(AboutContent.Children.IndexOf(AboutAuthorCard) < AboutContent.Children.IndexOf(AboutCreditsCard) AndAlso
                Grid.GetRow(AboutAuthorItem) < Grid.GetRow(AboutProductText) AndAlso CStr(AboutPclSponsor.Tag) = "pcl-sponsor", "the original author and sponsorship action precede project information")
            VerifyDeveloperLayout(check, artifactDirectory)
            VerifyBridgeMigration(check, artifactDirectory)
            VerifyDeveloperSaving(check, artifactDirectory)
        Finally
            RestoreDeveloperSettings(savedDeveloper)
            _experimentalThemesUnlocked = savedUnlocked
            RestoreAppearance(saved)
        End Try
    End Sub

    Private Sub VerifyDeveloperSaving(check As Action(Of Boolean, String), directory As String)
        Dim savedBaseline = _preferenceBaseline, savedPath = _preferenceBaselinePath
        Dim file = Path.Combine(directory, "developer-save-" & Guid.NewGuid().ToString("N") & ".json")
        Try
            _developer = New DeveloperPreferences()
            _preferenceBaseline = New JObject From {{"developer", _developer.ToJson()}}
            _preferenceBaselinePath = IO.Path.GetFullPath(file)
            DownloadSettingsFile.Save(file, New JObject From {{"developer", New JObject From {{"enabled", False}, {"showDiagnostics", False}, {"future", True}}}})
            DownloadSettingsFile.SaveChanges(file, file & ".old", New JObject From {{"developer", New JObject From {{"showDiagnostics", True}}}})
            _developer.Enabled = True
            SaveDeveloperValues(file, file & ".old")
            Dim result = JObject.Parse(IO.File.ReadAllText(file))
            check(CBool(result("developer")("enabled")) AndAlso CBool(result("developer")("showDiagnostics")) AndAlso CBool(result("developer")("future")), "a developer toggle merges only edited fields and preserves another window's diagnostics and unknown fields")
            Dim stamp As New DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc)
            IO.File.SetLastWriteTimeUtc(file, stamp)
            SaveDeveloperValues(file, file & ".old")
            check(IO.File.GetLastWriteTimeUtc(file) = stamp, "an unchanged developer window does not rewrite newer preferences")
        Finally
            _preferenceBaseline = savedBaseline : _preferenceBaselinePath = savedPath
        End Try
    End Sub

    Private Sub VerifyDeveloperLayout(check As Action(Of Boolean, String), directory As String)
        Dim content = DirectCast(Me.Content, FrameworkElement), host As New Border With {.Width = 810, .Height = 470}
        Dim source As Interop.HwndSource = Nothing
        Try
            Me.Content = Nothing : host.Child = content
            source = PreviewSurface.Attach(host, 810, 470)
            SwitchMainPage(True)
            SettingsPage.BeginAnimation(OpacityProperty, Nothing) : SettingsPage.Opacity = 1
            DeveloperEnabledBox.IsChecked = True
            LockDeveloperThemes(Me, New RoutedEventArgs())
            DarkModeBox.IsChecked = True
            host.Measure(New Size(810, 470)) : host.Arrange(New Rect(0, 0, 810, 470)) : host.UpdateLayout()
            DeveloperCard.BringIntoView() : host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
            check(DeveloperCodeBox.ActualWidth > 180 AndAlso DeveloperUnlockButton.TranslatePoint(New Point(DeveloperUnlockButton.ActualWidth, 0), DeveloperCard).X < DeveloperCard.ActualWidth, "developer code entry and unlock action fit the minimum window without horizontal clipping")
            Dim border = DirectCast(DeveloperCodeBox.Template.FindName("CodeBorder", DeveloperCodeBox), Border)
            check(border.CornerRadius.TopLeft = 3 AndAlso DirectCast(DeveloperCodeBox.Background, SolidColorBrush).Color.R < 60, "verification entry uses the shared rounded dark input styling")
            check(DirectCast(DeveloperUnlockButton.PanFore.Background, SolidColorBrush).Color.R < 80, "PCL buttons refresh their surfaces when dark mode is enabled")
            SaveControlVisual(host, Path.Combine(directory, "developer-dark.png"))
        Finally
            If source IsNot Nothing Then
                source.RootVisual = Nothing : source.Dispose()
            End If
            host.Child = Nothing
            If Me.Content Is Nothing Then Me.Content = content
        End Try
    End Sub

    Private Sub VerifyBridgeMigration(check As Action(Of Boolean, String), directory As String)
        Dim root = Path.Combine(directory, "migration-" & Guid.NewGuid().ToString("N")), previous = Path.Combine(root, "pcd.json"), oldest = Path.Combine(root, "vanilla.json"), current = Path.Combine(root, "bridge", "settings.json")
        IO.Directory.CreateDirectory(root)
        File.WriteAllText(oldest, "{""source"":2,""future"":true}")
        check(DownloadSettingsFile.ChooseLegacyPath(previous, oldest) = oldest, "absent PCD settings fall back to the oldest settings format")
        Dim text = "{""source"":1,""future"":true,""appearance"":{""theme"":""green""}}"
        File.WriteAllText(previous, text)
        Dim choice = DownloadSettingsFile.ChooseLegacyPath(previous, oldest)
        Dim migrated = DownloadSettingsFile.LoadCompatible(current, choice)
        check(CInt(migrated("source")) = 1 AndAlso File.ReadAllText(previous) = text AndAlso File.ReadAllText(current) = text, "PCD settings take precedence and migrate byte-preserving without altering the old profile")
        File.WriteAllText(previous, "{invalid")
        check(CInt(DownloadSettingsFile.LoadCompatible(current, choice)("source")) = 1, "an existing bridge profile wins even when the old PCD profile is corrupt")
        Dim failed = False
        Try
            DownloadSettingsFile.LoadCompatible(Path.Combine(root, "other", "settings.json"), DownloadSettingsFile.ChooseLegacyPath(previous, oldest))
        Catch ex As Newtonsoft.Json.JsonException
            failed = True
        End Try
        check(failed AndAlso File.ReadAllText(previous) = "{invalid", "a corrupt newest legacy profile is reported without silently importing older settings")
        File.WriteAllText(previous, text)
        failed = False
        Using held As New FileStream(previous, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            Try
                DownloadSettingsFile.LoadCompatible(Path.Combine(root, "occupied", "settings.json"), DownloadSettingsFile.ChooseLegacyPath(previous, oldest))
            Catch ex As IOException
                failed = True
            End Try
        End Using
        check(failed AndAlso Not IO.Directory.Exists(Path.Combine(root, "occupied")), "an occupied PCD profile fails before publishing partial migrated settings")
        Dim blocked = Path.Combine(root, "blocked.json")
        IO.Directory.CreateDirectory(blocked) : failed = False
        Try
            DownloadSettingsFile.LoadCompatible(Path.Combine(root, "blocked-target", "settings.json"), DownloadSettingsFile.ChooseLegacyPath(blocked, oldest))
        Catch ex As InvalidDataException
            failed = True
        End Try
        check(failed AndAlso IO.Directory.Exists(blocked) AndAlso Not IO.Directory.Exists(Path.Combine(root, "blocked-target")), "a directory occupying legacy settings is preserved and reported rather than bypassed")
        check(DownloadSettingsFile.PreviousDirectoryName = "PlainCraftDownloader" AndAlso GetType(MainWindow).FullName = "PclDownloadBridge.MainWindow", "legacy storage keys remain separate from the current application identity")
    End Sub
End Class
