Imports System.IO
Imports System.IO.Compression
Imports System.Net
Imports System.Net.Http
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Sub VerifyCurseForge(check As Action(Of Boolean, String), directory As String)
        Dim owner As New MainWindow(True), previous = SynchronizationContext.Current
        Dim content = DirectCast(owner.Content, FrameworkElement), host As New Border With {.Width = 850, .Height = 600}
        owner.Content = Nothing : host.Child = content
        Try
            Using surface = PreviewSurface.Attach(host, 850, 600)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.VerifyCurseForgeAsync(check, directory, host)
                If Not work.IsCompleted AndAlso Not PumpMotionUntil(Function() work.IsCompleted) Then Throw New TimeoutException("CF UI workflow exceeded its deadline.")
                work.GetAwaiter().GetResult()
            End Using
        Finally
            Dim pending = owner._packReadCancellation : owner._packReadCancellation = Nothing
            If pending IsNot Nothing Then
                Try
                    pending.Cancel()
                Catch ex As ObjectDisposedException
                    ' A failed query fixture may already have disposed its source.
                End Try
            End If
            owner._categoryMotion.Finish() : owner._pageSwitch.Finish() : owner._surfaceMotion.Finish()
            host.Child = Nothing : owner.Content = content
            SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub
    Private Async Function VerifyCurseForgeAsync(check As Action(Of Boolean, String), directory As String, host As Border) As Task
        Dim source = Path.Combine(directory, "ui-curseforge-" & Guid.NewGuid().ToString("N") & ".zip"), root = Path.Combine(directory, "cf-game-" & Guid.NewGuid().ToString("N"))
        Dim manifest As New JObject From {{"manifestType", "minecraftModpack"}, {"manifestVersion", 1}, {"name", " BetterMC preview "}, {"version", "1.5"}, {"overrides", "overrides"},
            {"minecraft", New JObject From {{"version", "1.20.1"}, {"modLoaders", New JArray(New JObject From {{"id", "forge-47.2.0"}})}}},
            {"files", New JArray(New JObject From {{"projectID", 1}, {"fileID", 1001}, {"required", True}}, New JObject From {{"projectID", 2}, {"fileID", 1002}, {"required", False}})}}
        Using archive = ZipFile.Open(source, ZipArchiveMode.Create), writer As New StreamWriter(archive.CreateEntry("manifest.json").Open())
            writer.Write(manifest.ToString())
        End Using
        Dim layout As Action = Sub()
                                   host.Measure(New Size(host.Width, host.Height)) : host.Arrange(New Rect(0, 0, host.Width, host.Height))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        _selectedFolder = New GameFolder With {.Location = root, .Name = "CF fixture"}
        layout()
        check(DownloadNav.Children.Count = 7 AndAlso DownloadNav.Children.OfType(Of PclListItem)().Select(Function(item) item.Title).SequenceEqual({"原版游戏", "Mod", "整合包", "数据包", "资源包", "光影包"}), "download sidebar retains PCL vanilla and the five community resource entries in source order")
        check(DownloadSidebar.ActualWidth = 135 AndAlso VanillaNav.Height = 36 AndAlso ModpackNav.Height = 36 AndAlso VanillaNav.IsSidebar AndAlso ModpackNav.IsSidebar, "sidebar uses PCL dimensions and animated list controls")
        SelectVersion(_versions(0)) : _pageSwitch.Finish()
        SelectComponent(_componentEntries(ComponentKind.Fabric)(0))
        InstanceName.Text = "保留原版方案" : IsolationBox.SelectedIndex = 1 : RepairBox.IsChecked = True
        Dim entry = _selectedEntry, selection = _componentSelection, fabric = selection.Item(ComponentKind.Fabric), card = _componentViews(ComponentKind.Fabric).Card
        card.Collapsed = False : layout()
        Dim body = card.Body
        ModpackNav.Activate() : layout()
        check(ModpackNav.Selected AndAlso Not VanillaNav.Selected AndAlso ModpackPage.IsVisible AndAlso Not InstallPage.IsVisible, "modpack radio selection switches the right pane")
        check(CurseForgeCard.Title = "CurseForge" AndAlso PackOptionsCard.Visibility = Visibility.Collapsed AndAlso ImportPackButton.IsEnabled AndAlso AllowDrop, "empty modpack page contains the CurseForge import card and ZIP drop entry")
        check(RefreshButton.Visibility = Visibility.Collapsed AndAlso Not _hasTask, "modpack landing has no vanilla refresh or empty task")
        SaveControlVisual(host, Path.Combine(directory, "download-modpacks-empty.png"))
        Dim package = CurseForgePackage.Read(source)
        SelectCurseForgePackage(package) : layout()
        check(_downloadView = DownloadView.Modpacks AndAlso PackOptionsCard.IsVisible AndAlso PackSummary.Text.Contains("47.2.0"), "CF selection remains in its card with the exact loader")
        check(PackInstanceName.Text = "BetterMC preview" AndAlso _selectedEntry Is entry AndAlso InstanceName.Text = "保留原版方案", "pack identity and name do not replace the vanilla form")
        check(PackTitle.Text = "BetterMC preview · 1.5" AndAlso PackTitle.FontSize = 14 AndAlso PackSummary.FontSize = 12 AndAlso PackOptionalHeader.IsVisible, "CF import separates its PCL-style title statistics and optional heading")
        check(_componentSelection Is selection AndAlso selection.Item(ComponentKind.Fabric) Is fabric AndAlso card.Body Is body AndAlso Not card.Collapsed, "import retains vanilla components and expanded row bodies")
        check(IsolationBox.SelectedIndex = 1 AndAlso RepairBox.IsChecked.GetValueOrDefault(), "import retains the global default isolation and separate repair preference")
        Dim box = DirectCast(PackOptionalFiles.Children(0), PclCheckBox)
        check(PackOptionalFiles.Children.Count = 1 AndAlso Not box.IsChecked.GetValueOrDefault() AndAlso Not box.IsEnabled, "optional files start unselected until metadata is available")
        box.Content = "optional.jar（可选）" : box.ToolTip = "mods/optional.jar"
        box.IsEnabled = True : box.IsChecked = True : PackInstanceName.Text = "custom-pack"
        check(PackSummary.Text.Contains("已选 1 / 1"), "CF optional choices immediately update the selected count")
        Await ReadCurseForgePackAsync(source)
        check(PackInstanceName.Text = "custom-pack" AndAlso DirectCast(PackOptionalFiles.Children(0), PclCheckBox).IsChecked.GetValueOrDefault() AndAlso DirectCast(PackOptionalFiles.Children(0), PclCheckBox).IsEnabled AndAlso CStr(DirectCast(PackOptionalFiles.Children(0), PclCheckBox).Content) = "optional.jar（可选）", "CF rereading the same archive retains the custom name and chosen optional file")
        package = _packSelection : box = DirectCast(PackOptionalFiles.Children(0), PclCheckBox)
        Dim copied = Path.Combine(directory, "copied-cf-" & Guid.NewGuid().ToString("N") & ".zip") : File.Copy(source, copied)
        Await ReadCurseForgePackAsync(copied)
        check(PackInstanceName.Text = "custom-pack" AndAlso PackSource.Text = copied AndAlso DirectCast(PackOptionalFiles.Children(0), PclCheckBox).IsChecked.GetValueOrDefault(), "CF an identical archive at another path retains choices while updating its source")
        package = _packSelection : box = DirectCast(PackOptionalFiles.Children(0), PclCheckBox)
        ShowSettings(Me, New RoutedEventArgs()) : ShowDownload(Me, New RoutedEventArgs()) : layout()
        check(_packSelection Is package AndAlso PackInstanceName.Text = "custom-pack" AndAlso box.IsChecked.GetValueOrDefault() AndAlso _downloadView = DownloadView.Modpacks, "settings navigation retains the package name and optional choices")
        VanillaNav.Activate() : _pageSwitch.Finish() : layout()
        check(_downloadView = DownloadView.Install AndAlso VanillaNav.Selected AndAlso Not ModpackNav.Selected AndAlso _selectedEntry Is entry, "vanilla radio returns to its previous install form")
        check(InstanceName.Text = "保留原版方案" AndAlso _componentSelection Is selection AndAlso card.Body Is body, "returning to vanilla preserves name selection and body identity")
        BackToVersions(Me, New RoutedEventArgs()) : _pageSwitch.Finish() : ModpackNav.Activate() : VanillaNav.Activate() : layout()
        check(_downloadView = DownloadView.Versions AndAlso VersionPage.IsVisible, "a vanilla catalogue remains a catalogue across category changes")
        ModpackNav.Activate() : layout()
        check(_packSelection Is package AndAlso PackInstanceName.Text = "custom-pack" AndAlso box.IsChecked.GetValueOrDefault(), "returning to modpacks retains its independent form")
        PackInstanceName.Text = "bad/name"
        check(Not PackInstallButton.IsEnabled AndAlso PackNameHint.Visibility = Visibility.Visible AndAlso PackInstanceName.ValidationMessage.Length > 0, "invalid pack names disable install and show a PCL hint")
        PackInstanceName.Text = "existing-pack" : IO.Directory.CreateDirectory(Path.Combine(root, "versions", "existing-pack"))
        File.WriteAllText(Path.Combine(root, "versions", "existing-pack", "keep.txt"), "existing") : RefreshPackAvailability()
        check(Not PackInstallButton.IsEnabled AndAlso File.ReadAllText(Path.Combine(root, "versions", "existing-pack", "keep.txt")) = "existing", "pack validation refuses existing targets even when vanilla repair is checked")
        PackInstanceName.Text = "custom-pack"
        check(PackInstallButton.IsEnabled AndAlso PackNameHint.Visibility = Visibility.Collapsed, "correcting the pack name clears its error immediately")
        Dim reading = ReadCurseForgePackAsync(source)
        check(_readingPack AndAlso Not ImportPackButton.IsEnabled AndAlso Not PackInstallButton.IsEnabled AndAlso Not PackOptionsCard.IsEnabled, "replacement parsing freezes the old package form before asynchronous work")
        ShowVanillaDownloads(Me, New RoutedEventArgs()) : Await reading
        check(_downloadView = DownloadView.Versions AndAlso Not _readingPack AndAlso PackInstanceName.Text = "custom-pack", "late ZIP parsing updates the hidden card without hijacking navigation or replacing its edited name")
        Dim loaded = _packSelection, invalid = Path.Combine(directory, "invalid-cf-" & Guid.NewGuid().ToString("N") & ".zip")
        PackInstanceName.Text = "keep-on-failure" : File.WriteAllText(invalid, "not a zip")
        Await ReadCurseForgePackAsync(invalid)
        check(_packSelection Is loaded AndAlso PackInstanceName.Text = "keep-on-failure" AndAlso PackReadStatus.Text.Contains("无法读取"), "failed replacement retains the last package and edited name with visible feedback")
        check(ImportPackButton.IsEnabled AndAlso PackInstallButton.IsEnabled AndAlso Not _readingPack, "failed ZIP parsing restores controls for retry")
        reading = ReadCurseForgePackAsync(source) : ShowSettings(Me, New RoutedEventArgs()) : Await reading
        check(SettingsPage.Visibility = Visibility.Visible AndAlso Not DownloadPage.IsVisible, "late ZIP parsing cannot pull the user out of settings")
        ShowDownload(Me, New RoutedEventArgs()) : loaded = _packSelection
        Dim preserved = DirectCast(PackOptionalFiles.Children(0), PclCheckBox)
        preserved.Content = "已查询的可选文件" : preserved.IsEnabled = True : preserved.IsChecked = True
        reading = ReadCurseForgePackAsync(source) : _packReadCancellation.Cancel() : Await reading
        check(_packSelection Is loaded AndAlso Not _readingPack AndAlso ImportPackButton.IsEnabled AndAlso preserved.IsEnabled AndAlso preserved.IsChecked.GetValueOrDefault() AndAlso preserved.Content.ToString() = "已查询的可选文件", "cancelling parsing preserves the package and resolved optional choices despite the temporary form freeze")
        Await ReadCurseForgePackAsync(source)
        check(_packSelection IsNot loaded AndAlso PackInstallButton.IsEnabled, "ZIP parsing succeeds on retry after cancellation")
        Await VerifyStalePackMetadataAsync(check)
        PackInstanceName.Text = "pack-retry"
        Dim starts = 0, seenName As String = Nothing, seenEntry As VersionEntry = Nothing, seenSelection As ComponentSelection = Nothing, seenIsolation As InstanceIsolationMode, seenDefault As Integer?
        Dim gate As New TaskCompletionSource(Of InstallResult)()
        _installRunner = Function(game, gameRoot, name, components, isolation, defaultIsolation, token)
                             starts += 1 : seenName = name : seenEntry = game : seenSelection = components : seenIsolation = isolation : seenDefault = defaultIsolation
                             Return gate.Task
                         End Function
        StartInstall(Me, New RoutedEventArgs()) : layout()
        check(_busy AndAlso starts = 1 AndAlso seenName = "pack-retry" AndAlso seenEntry.Id = "1.20.1" AndAlso _taskMinecraft = "1.20.1" AndAlso _selectedEntry.Id <> _taskMinecraft, "pack install and task summary capture its own game version and name independently of vanilla")
        check(seenSelection.Entries.Count = 0 AndAlso seenIsolation = InstanceIsolationMode.Enabled AndAlso Not seenDefault.HasValue AndAlso _componentSelection Is selection, "pack install ignores global defaults, vanilla components and repair and forces isolation")
        check(Not DownloadNav.IsEnabled AndAlso Not ImportPackButton.IsEnabled AndAlso Not PackOptionsCard.IsEnabled AndAlso Not PackInstallButton.IsEnabled, "active task freezes category switching and pack edits")
        ShowVanillaDownloads(Me, New RoutedEventArgs()) : StartInstall(Me, New RoutedEventArgs())
        check(_downloadView = DownloadView.Progress AndAlso starts = 1 AndAlso ModpackNav.Selected, "active task retains category and rejects duplicate starts")
        ResumeInstallation(Me, New RoutedEventArgs()) : layout()
        check(_downloadView = DownloadView.Modpacks AndAlso PackInstanceName.Text = "pack-retry" AndAlso Not PackInstallButton.IsEnabled, "task return restores the same disabled pack options")
        CancelInstall(Me, New RoutedEventArgs()) : gate.TrySetCanceled() : Await WaitForNameInstallAsync()
        check(Not _busy AndAlso DownloadNav.IsEnabled AndAlso ImportPackButton.IsEnabled AndAlso PackInstallButton.IsEnabled AndAlso _taskOutcome = TaskOutcome.Cancelled, "cancellation restores navigation and pack edits for retry")
        gate = New TaskCompletionSource(Of InstallResult)() : StartInstall(Me, New RoutedEventArgs())
        check(_busy AndAlso starts = 2 AndAlso seenName = "pack-retry", "retry retains the package identity and edited name")
        gate.TrySetResult(New InstallResult With {.InstancePath = Path.Combine(root, "versions", "pack-retry"), .FileCount = 1}) : Await WaitForNameInstallAsync()
        check(_taskOutcome = TaskOutcome.Succeeded AndAlso Not _busy AndAlso _selectedEntry Is entry AndAlso InstanceName.Text = "保留原版方案", "finishing a pack task leaves vanilla inputs intact")
        ResumeInstallation(Me, New RoutedEventArgs()) : layout()
        check(_downloadView = DownloadView.Modpacks AndAlso ProgressShortcut.Visibility = Visibility.Visible AndAlso _hasTask, "finished task can be revisited from the pack card")
        VersionSourceChanged(VersionSourceBox, New SelectionChangedEventArgs(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent, New Object() {}, New Object() {}))
        check(_componentsDirty AndAlso _selectedEntry Is entry AndAlso _packSelection IsNot Nothing, "source changes mark hidden vanilla catalogues stale while retaining both selections")
        _componentsDirty = False : SaveControlVisual(host, Path.Combine(directory, "download-curseforge-card.png"))
        host.Width = 810 : host.Height = 470 : layout()
        check(ImportPackButton.ActualWidth = 120 AndAlso CurseForgeCard.ActualWidth > 500 AndAlso ModpackPage.ScrollableHeight > 0, "minimum window keeps import controls usable and scrolls pack options")
        SaveControlVisual(host, Path.Combine(directory, "download-curseforge-minimum.png"))
        _installRunner = Nothing
        _motionPreview = True : _categoryMotion.PauseForChecks = True
        ShowVanillaDownloads(Me, New RoutedEventArgs()) : _categoryMotion.Finish()
        ShowModpackDownloads(Me, New RoutedEventArgs()) : layout()
        Dim keyboardStarts = 0
        _installRunner = Function(game, gameRoot, name, components, isolation, defaultIsolation, token)
                             keyboardStarts += 1
                             Return Task.FromResult(New InstallResult With {.InstancePath = Path.Combine(gameRoot, "versions", name), .FileCount = 1})
                         End Function
        PressControlKey(PackInstanceName, System.Windows.Input.Key.Enter)
        check(keyboardStarts = 0 AndAlso _categoryMotion.Transition IsNot Nothing, "a pack name Enter cannot start installation behind a category transition input gate")
        _categoryMotion.Finish()
        PressControlKey(PackInstanceName, System.Windows.Input.Key.Enter) : Await WaitForNameInstallAsync()
        check(keyboardStarts = 1 AndAlso _taskOutcome = TaskOutcome.Succeeded, "pack name Enter becomes available again after category entry settles")
        _motionPreview = False : _installRunner = Nothing
        manifest("minecraft")("modLoaders") = New JArray()
        Dim vanillaPack = Path.Combine(directory, "ui-vanilla-pack-" & Guid.NewGuid().ToString("N") & ".zip")
        Using archive = ZipFile.Open(vanillaPack, ZipArchiveMode.Create), writer As New StreamWriter(archive.CreateEntry("manifest.json").Open())
            writer.Write(manifest.ToString())
        End Using
        Dim cold As New MainWindow(True)
        cold._selectedFolder = _selectedFolder
        cold.SelectCurseForgePackage(CurseForgePackage.Read(vanillaPack)) : cold.PackInstanceName.Text = "cold-pack"
        cold._installRunner = Function(game, gameRoot, name, components, isolation, defaultIsolation, token) Task.FromResult(New InstallResult With {.InstancePath = Path.Combine(gameRoot, "versions", name), .FileCount = 1})
        cold.StartInstall(cold, New RoutedEventArgs()) : Await cold.WaitForNameInstallAsync()
        check(cold._taskOutcome = TaskOutcome.Succeeded AndAlso cold._taskMinecraft = package.Minecraft AndAlso cold._selectedEntry Is Nothing AndAlso cold._taskRows.Count = 3 AndAlso Not cold._taskHasComponents, "a pack without loaders works on a cold window without a vanilla selection or phantom processor phases")
        Dim changed = Path.Combine(directory, "changed-cf-" & Guid.NewGuid().ToString("N") & ".zip")
        manifest("version") = "1.6"
        Using archive = ZipFile.Open(changed, ZipArchiveMode.Create), writer As New StreamWriter(archive.CreateEntry("manifest.json").Open())
            writer.Write(manifest.ToString())
        End Using
        SelectCurseForgePackage(CurseForgePackage.Read(changed))
        check(PackInstanceName.Text = "BetterMC preview" AndAlso PackOptionalFiles.Children.OfType(Of PclCheckBox)().All(Function(item) Not item.IsChecked.GetValueOrDefault() AndAlso Not item.IsEnabled) AndAlso PackSummary.Text.Contains("已选 0 / 1"), "CF changed archive content resets the previous name choices and display metadata")
    End Function
    Private Async Function VerifyStalePackMetadataAsync(check As Action(Of Boolean, String)) As Task
        For Each obsolete In {False, True}
        Using handler As New DeferredPackHandler(), client As New HttpClient(handler), resolver As New CurseForgeFileResolver(Policy(), client, ""), slots As New SemaphoreSlim(4), old As New CancellationTokenSource(), replacement As New CancellationTokenSource()
            Dim optionalBox = DirectCast(PackOptionalFiles.Children(0), PclCheckBox)
            optionalBox.Content = "等待查询" : optionalBox.IsEnabled = False : optionalBox.IsChecked = False
            Dim package = _packSelection, box = DirectCast(PackOptionalFiles.Children(0), PclCheckBox), content = box.Content
            _packReadCancellation = old
            Dim query = QueryPackOptionalAsync(package, box, resolver, slots, old)
            If obsolete Then _packReadCancellation = replacement
            handler.Response.TrySetResult(New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New StringContent("{""data"":{""id"":1002,""modId"":2,""fileName"":""optional.jar"",""fileLength"":3,""gameVersions"":[""1.20.1""],""hashes"":[{""algo"":1,""value"":""aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa""}]}}")})
            Await query
            If obsolete Then
                check(box.Content Is content AndAlso Not box.IsEnabled, "obsolete optional metadata cannot enable or relabel current package choices")
            Else
                check(box.Content.ToString() = "optional.jar（可选）" AndAlso box.IsEnabled AndAlso Not box.IsChecked.GetValueOrDefault(), "validated optional metadata reveals the exact filename while keeping the choice unselected")
            End If
            _packReadCancellation = Nothing
        End Using
        Next
        Using handler As New DeferredPackHandler(), client As New HttpClient(handler), resolver As New CurseForgeFileResolver(Policy(), client, ""), slots As New SemaphoreSlim(4), request As New CancellationTokenSource()
            Dim box = DirectCast(PackOptionalFiles.Children(0), PclCheckBox)
            box.Content = "optional.jar（可选）" : box.IsEnabled = True : box.IsChecked = True
            _packReadCancellation = request
            handler.Response.TrySetException(New HttpRequestException("受控文件信息查询失败"))
            Await QueryPackOptionalAsync(_packSelection, box, resolver, slots, request)
            check(box.IsEnabled AndAlso box.IsChecked.GetValueOrDefault() AndAlso box.Content.ToString().Contains("暂时无法查询"), "CF a failed fresh query preserves an already available choice for deselection without trusting cached download metadata")
            box.IsChecked = False
            check(PackSummary.Text.Contains("已选 0 / 1"), "CF an unavailable optional file can be removed from a restored selection")
            _packReadCancellation = Nothing
        End Using
    End Function
    Private Class DeferredPackHandler
        Inherits HttpMessageHandler
        Friend ReadOnly Response As New TaskCompletionSource(Of HttpResponseMessage)()
        Protected Overrides Function SendAsync(request As HttpRequestMessage, token As CancellationToken) As Task(Of HttpResponseMessage)
            Return Response.Task
        End Function
    End Class
End Class
