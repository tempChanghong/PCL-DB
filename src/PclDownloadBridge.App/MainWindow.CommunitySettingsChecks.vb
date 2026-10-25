Imports System.IO
Imports System.Text
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyLiveCurseForgeDataPack(directory As String)
        Dim owner As New MainWindow(True), original = owner.Content, saved = _appearance.ToJson()
        Dim previous = System.Threading.SynchronizationContext.Current
        Dim host As New Border With {.Width = 900, .Height = 550}, layout = ControlLayout(host)
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 550)
                System.Threading.SynchronizationContext.SetSynchronizationContext(New System.Windows.Threading.DispatcherSynchronizationContext(owner.Dispatcher))
                owner._packCatalogFactory = Function() New ModpackCatalogClient(apiKey:="", communitySource:=2)
                owner.ShowDownload(Nothing, Nothing) : owner.SelectResourceKind(CommunityResourceKind.DataPack)
                owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : layout()
                owner.PackSearchSource.SelectedIndex = 1
                Dim watch = System.Diagnostics.Stopwatch.StartNew()
                owner.SubmitPackSearch(owner.PackSearchButton, Nothing)
                If Not PumpMotionUntil(Function() owner._packSearchRequest Is Nothing, 45000) Then Throw New TimeoutException("Live CF datapack query timed out.")
                owner.FinishPackContentMotion() : layout()
                Dim rows = owner.PackProjectRows.Children.OfType(Of PclResourceItem)().Select(Function(item) DirectCast(item.Tag, ModpackProject)).ToArray()
                Dim success = rows.Length > 0 AndAlso rows.All(Function(item) item.Platform = ModpackPlatform.CurseForge AndAlso item.Kind = CommunityResourceKind.DataPack) AndAlso owner._packSearchSession.ErrorMessage = ""
                Dim versions = 0
                If success Then
                    owner.OpenPackDetails(rows(0))
                    If Not PumpMotionUntil(Function() owner._packDetailRequest Is Nothing, 45000) Then Throw New TimeoutException("Live CF datapack details timed out.")
                    owner.FinishPackContentMotion() : layout()
                    versions = owner._packReleases.Count
                    success = owner._packVerifiedProject IsNot Nothing AndAlso versions > 0 AndAlso owner._packReleases.All(Function(item) item.Kind = CommunityResourceKind.DataPack AndAlso item.Platform = ModpackPlatform.CurseForge)
                    SaveControlVisual(host, IO.Path.Combine(directory, "live-cf-datapack-details.png"))
                    owner.BackToPackSearch(Nothing, Nothing) : owner.FinishPackContentMotion() : layout()
                End If
                File.WriteAllText(IO.Path.Combine(directory, "live-cf-ui.json"), New JObject From {{"Succeeded", success}, {"Count", rows.Length}, {"Milliseconds", watch.ElapsedMilliseconds},
                    {"Versions", versions}, {"SessionCount", owner._packSearchSession.Count}, {"Query", JObject.FromObject(owner._packSearchSession.Query)}, {"Error", owner._packSearchSession.ErrorMessage}, {"Names", New JArray(rows.Take(5).Select(Function(item) item.Name))}, {"UserGameDirectoriesWritten", False}, {"ExternalConfigurationsWritten", False}}.ToString())
                SaveControlVisual(host, IO.Path.Combine(directory, "live-cf-datapacks.png"))
                If Not success Then Throw New InvalidOperationException("Live CF datapack search did not return usable projects.")
            End Using
        Finally
            owner._closing = True : owner.CancelResourceRequests() : owner._pclThemeCycleTimer.Stop() : owner._pclThemeMonitor.Dispose()
            owner.RestoreAppearance(saved) : host.Child = Nothing : owner.Content = original
            System.Threading.SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub
    Public Sub VerifyCommunitySettingsUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, saved = _appearance.ToJson()
        Dim previous = System.Threading.SynchronizationContext.Current
        Dim host As New Border With {.Width = 900, .Height = 550}, layout = ControlLayout(host)
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     If Not ok Then Throw New InvalidOperationException(label)
                                                     passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 550)
                System.Threading.SynchronizationContext.SetSynchronizationContext(New System.Windows.Threading.DispatcherSynchronizationContext(owner.Dispatcher))
                layout()
                Dim work = owner.CheckCommunitySettingsAsync(directory, host, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 30000) Then Throw New TimeoutException("Community settings did not settle.")
                work.GetAwaiter().GetResult()
            End Using
        Finally
            owner._closing = True : owner.CancelResourceRequests() : owner._pclThemeCycleTimer.Stop() : owner._pclThemeMonitor.Dispose()
            owner._pageSwitch.Finish() : owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner.FinishPackContentMotion()
            owner.RestoreAppearance(saved) : host.Child = Nothing : owner.Content = original
            System.Threading.SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub
    Private Async Function CheckCommunitySettingsAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        check(Width = 900 AndAlso Height = 550 AndAlso MinWidth = 810 AndAlso MinHeight = 470, "default window size matches PCL's persisted-setting defaults while retaining its minimum size")
        ShowSettings(Nothing, Nothing) : _surfaceMotion.Finish() : layout()
        check(CommunitySourceBox.Items.Count = 3 AndAlso CommunityFileNameBox.Items.Count = 5 AndAlso CommunityLocalModStyleBox.Items.Count = 2 AndAlso
            CommunitySourceBox.SelectedIndex = 2 AndAlso CommunityFileNameBox.SelectedIndex = 1 AndAlso CommunityLocalModStyleBox.SelectedIndex = 0 AndAlso
            Math.Abs(CommunitySourceBox.TranslatePoint(New Point(), SettingsContent).X - SourceBox.TranslatePoint(New Point(), SettingsContent).X) < 0.5, "community resource card exposes PCL's choices and defaults with shared label-column alignment")
        CommunitySourceBox.SelectedIndex = 0 : CommunityFileNameBox.SelectedIndex = 3 : CommunityLocalModStyleBox.SelectedIndex = 1
        Dim settings = BuildSettings(0)
        check(Policy().CommunityPreference.GetValueOrDefault() = SourcePreference.MirrorFirst AndAlso Policy().Preference = SourcePreference.Automatic AndAlso
            CInt(settings("communityResources")("fileNameStyle")) = 3 AndAlso CInt(settings("communityResources")("localModStyle")) = 1, "community selectors bind independent request preferences and persisted presentation values")
        Dim prefsPath = IO.Path.Combine(directory, "community-preferences-" & Guid.NewGuid().ToString("N") & ".json")
        DownloadSettingsFile.Save(prefsPath, New JObject From {{"unrelated", "retained"}, {"communityResources", New JObject From {{"customKey", "keep"}}}})
        SavePreferenceValues(prefsPath, Nothing, 0)
        Dim stored = JObject.Parse(File.ReadAllText(prefsPath))
        RestoreSourcePreferences(stored)
        check(CStr(stored("unrelated")) = "retained" AndAlso CStr(stored("communityResources")("customKey")) = "keep" AndAlso CommunitySourceBox.SelectedIndex = 0 AndAlso CommunityFileNameBox.SelectedIndex = 3, "community settings round-trip without discarding unrelated or nested unknown preferences")
        CommunitySettingsCard.BringIntoView() : layout()
        SaveControlVisual(host, IO.Path.Combine(directory, "community-settings-blue.png"))
        Dim jar = IO.Path.Combine(directory, "community-local-style-" & Guid.NewGuid().ToString("N") & ".jar")
        Using output = IO.Compression.ZipFile.Open(jar, IO.Compression.ZipArchiveMode.Create)
            Using writer As New StreamWriter(output.CreateEntry("fabric.mod.json").Open())
                writer.Write("{""id"":""local-style"",""name"":""样式测试 Mod"",""version"":""1.2.3""}")
            End Using
        End Using
        Dim entry = LocalModCatalog.Read(jar, System.Threading.CancellationToken.None), row As New PclLocalModItem(entry, True)
        check(row.TitleLabel.Text = IO.Path.GetFileNameWithoutExtension(jar) AndAlso row.InfoLabel.Text.Contains("样式测试 Mod") AndAlso row.VersionLabel.Visibility = Visibility.Collapsed AndAlso row.Tag Is entry,
            "filename-first local style changes presentation while retaining the file action identity")

        Dim handler As New BrowserSourceHandler
        _packCatalogFactory = Function() New ModpackCatalogClient(handler, "", _communityPreferences.Source)
        ShowDownload(Nothing, Nothing) : SelectResourceKind(CommunityResourceKind.DataPack) : _surfaceMotion.Finish() : _categoryMotion.Finish() : layout()
        ResetPackSearch(Nothing, Nothing) : RunPackSearch(Nothing, Nothing)
        Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : FinishPackContentMotion() : layout()
        Dim accepted = _packSearchSession, requests = handler.Requests
        PackSearchSource.SelectedIndex = 1 : layout()
        check(_packSearchSession Is accepted AndAlso handler.Requests = requests AndAlso PackSearchInputHint.IsVisible AndAlso PackSearchInputHint.Theme = PclHintTheme.Blue AndAlso PackSearchInputHint.Text.Contains("点击「搜索」"),
            "changing the CurseForge draft source visibly asks to apply search without relabeling cached Modrinth results")
        SubmitPackSearch(PackSearchButton, Nothing) : Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : FinishPackContentMotion() : layout()
        check(_packSearchSession.Query.Platforms = ModpackPlatform.CurseForge AndAlso handler.Requests > requests AndAlso PackProjectRows.Children.OfType(Of PclResourceItem)().Count() = 1 AndAlso
            PackProjectRows.Children.OfType(Of PclResourceItem)().All(Function(item) DirectCast(item.Tag, ModpackProject).Platform = ModpackPlatform.CurseForge) AndAlso Not PackSearchInputHint.IsVisible,
            "real search submission accepts a CurseForge-only datapack query and presents only its returned projects")
        accepted = _packSearchSession : requests = handler.Requests
        ResetPackSearchFromUi(PackSearchReset, Nothing) : layout()
        check(PackSearchReset.Text = "重置条件" AndAlso PackSearchSource.SelectedIndex = 0 AndAlso _packSearchSession Is accepted AndAlso requests = handler.Requests AndAlso PackSearchInputHint.IsVisible,
            "the source-aligned reset button names its action and retains the accepted search while showing unsubmitted draft changes")

        _followPreview = True : _developer.Enabled = True : _experimentalThemesUnlocked = True
        _appearance.Theme = "deepblue" : _appearance.FollowPclTheme = True : _appearance.PclThemeFile = IO.Path.Combine(directory, "cycle-source", "Setup.ini")
        Dim local = _appearance.ToJson()
        ReceivePclTheme(PclThemeSnapshot.Parse(Encoding.UTF8.GetBytes("UiLauncherTheme:12")))
        check(_pclCycleAppearance IsNot Nothing AndAlso PclThemeStatus.Text.Contains("动态近似") AndAlso JToken.DeepEquals(local, _appearance.ToJson()), "slapstick theme ID 12 is supported as an explicit dynamic approximation without rewriting fallback preferences")
        PulseFollowedThemeCycle(0, True)
        Dim beforeColor = DirectCast(FindResource("ColorBrush3"), SolidColorBrush).Color, brush = FindResource("ColorBrush3")
        PulseFollowedThemeCycle(30000, True)
        File.WriteAllText(IO.Path.Combine(directory, "cycle-probe.json"), New JObject From {{"before", beforeColor.ToString()}, {"after", DirectCast(FindResource("ColorBrush3"), SolidColorBrush).Color.ToString()},
            {"sameBrush", FindResource("ColorBrush3") Is brush}, {"timer", _pclThemeCycleTimer.IsEnabled}, {"windowVisible", IsVisible}, {"hue", _pclCycleAppearance.Hue}}.ToString())
        check(beforeColor <> DirectCast(FindResource("ColorBrush3"), SolidColorBrush).Color AndAlso (FindResource("ColorBrush3") Is brush OrElse DirectCast(brush, SolidColorBrush).IsFrozen) AndAlso Not _pclThemeCycleTimer.IsEnabled,
            "cycle frames update mutable brushes or replace frozen resources while hidden windows do not run a periodic timer")
        SelectVersion(_versions.Single(Function(value) value.Id = "1.20.1")) : _pageSwitch.Finish()
        _motionPreview = True : _pageSwitch.CardMotion.PauseForChecks = True
        SetDownloadView(DownloadView.Versions) : _pageSwitch.Finish() : SetDownloadView(DownloadView.Install)
        PauseSelectionClock(_pageSwitch.Transition) : PauseSelectionClock(_pageSwitch.CardMotion.Transition)
        Dim pageClock = _pageSwitch.Transition, cardsClock = _pageSwitch.CardMotion.Transition, selected = _selectedEntry
        InstanceName.Text = "动态配色保留名称" : PulseFollowedThemeCycle(60000, True)
        check(_pageSwitch.Transition Is pageClock AndAlso _pageSwitch.CardMotion.Transition Is cardsClock AndAlso _selectedEntry Is selected AndAlso InstanceName.Text = "动态配色保留名称",
            "dynamic color ticks preserve active page/card clocks, selected version and edited instance name")
        _pageSwitch.Finish() : _appearance.DarkMode = True : ApplyAppearance(externalRefresh:=True) : PulseFollowedThemeCycle(15000, True)
        check(DirectCast(Shell.Background, SolidColorBrush).Color = Color.FromRgb(19, 27, 38) AndAlso DirectCast(FindResource("ColorBrush1"), SolidColorBrush).Color = Color.FromRgb(228, 234, 242),
            "dynamic theme preserves stable dark surfaces and readable primary text")
        ShowSettings(Nothing, Nothing) : _surfaceMotion.Finish() : CommunitySettingsCard.BringIntoView() : layout()
        SaveControlVisual(host, IO.Path.Combine(directory, "community-settings-cycle-dark.png"))
        _experimentalThemesUnlocked = False : ApplyAppearance(externalRefresh:=True)
        check(_pclCycleAppearance Is Nothing AndAlso Not _pclThemeCycleTimer.IsEnabled AndAlso PclThemeStatus.Text.Contains("解锁"), "locking experimental appearance stops the dynamic cycle and explains the retained permission requirement")
        _appearance.FollowPclTheme = False : ApplyAppearance(externalRefresh:=True)
        Dim stopped = DirectCast(FindResource("ColorBrush3"), SolidColorBrush).Color
        PulseFollowedThemeCycle(0, True)
        check(_pclCycleAppearance Is Nothing AndAlso DirectCast(FindResource("ColorBrush3"), SolidColorBrush).Color = stopped, "a queued cycle tick after disabling follow cannot repaint the local fallback")
    End Function
End Class
