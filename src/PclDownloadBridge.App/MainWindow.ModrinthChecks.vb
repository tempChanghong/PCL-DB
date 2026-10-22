Imports System.IO
Imports System.IO.Compression
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Sub VerifyModrinth(check As Action(Of Boolean, String), directory As String)
        Dim owner As New MainWindow(True), previous = SynchronizationContext.Current
        Dim content = DirectCast(owner.Content, FrameworkElement), host As New Border With {.Width = 850, .Height = 600}
        owner.Content = Nothing : host.Child = content
        Try
            Using surface = PreviewSurface.Attach(host, 850, 600)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.VerifyModrinthAsync(check, directory, host)
                If Not work.IsCompleted AndAlso Not PumpMotionUntil(Function() work.IsCompleted) Then Throw New TimeoutException("MR UI workflow exceeded its deadline.")
                work.GetAwaiter().GetResult()
            End Using
        Finally
            owner._packReadCancellation?.Cancel()
            owner._categoryMotion.Finish() : owner._pageSwitch.Finish() : owner._surfaceMotion.Finish()
            host.Child = Nothing : owner.Content = content
            SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub
    Private Async Function VerifyModrinthAsync(check As Action(Of Boolean, String), directory As String, host As Border) As Task
        Dim source = Path.Combine(directory, "ui-mr-" & Guid.NewGuid().ToString("N") & ".mrpack"), root = Path.Combine(directory, "mr-game-" & Guid.NewGuid().ToString("N"))
        Dim manifest As New JObject From {{"game", "minecraft"}, {"formatVersion", 1}, {"name", "Zombie preview"}, {"versionId", "2.3"},
            {"dependencies", New JObject From {{"minecraft", "1.20.1"}, {"forge", "47.4.10"}}},
            {"files", New JArray(New JObject From {{"path", "mods/optional.jar"}, {"fileSize", 42}, {"hashes", New JObject From {{"sha1", New String("a"c, 40)}, {"sha512", New String("b"c, 128)}}},
                {"downloads", New JArray("https://cdn.modrinth.com/mod.jar")}, {"env", New JObject From {{"client", "optional"}, {"server", "unsupported"}}}})}}
        Using zip = ZipFile.Open(source, ZipArchiveMode.Create), writer As New StreamWriter(zip.CreateEntry("modrinth.index.json").Open())
            writer.Write(manifest.ToString())
        End Using
        Dim layout As Action = Sub()
                                   host.Measure(New Size(host.Width, host.Height)) : host.Arrange(New Rect(0, 0, host.Width, host.Height))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        _selectedFolder = New GameFolder With {.Location = root, .Name = "MR fixture"}
        ShowModpackDownloads(Me, New RoutedEventArgs()) : layout()
        check(DownloadNav.Children.Count = 2 AndAlso CurseForgeCard.Title = "CurseForge" AndAlso ModrinthCard.Title = "Modrinth" AndAlso ModrinthCard.Margin.Equals(CurseForgeCard.Margin), "MR joins the existing modpack page without adding sidebar destinations")
        check(ModrinthOptionsCard.Visibility = Visibility.Collapsed AndAlso ImportModrinthButton.IsEnabled AndAlso IsPackFile(source) AndAlso IsPackFile("test.ZIP") AndAlso Not IsPackFile("test.exe"), "MR landing uses its own card and accepts ZIP/MRPACK drop formats")
        Await ReadLocalPackAsync(source) : layout()
        Dim package = _modrinthSelection, box = DirectCast(ModrinthOptionalFiles.Children(0), PclCheckBox)
        check(package IsNot Nothing AndAlso _activeModrinth AndAlso _downloadView = DownloadView.Modpacks AndAlso ModrinthOptionsCard.IsVisible, "MR content detection reads asynchronously into the Modrinth card")
        check(ModrinthSummary.Text.Contains("47.4.10") AndAlso ModrinthSource.Text = source AndAlso _packSelection Is Nothing AndAlso _selectedEntry Is Nothing, "MR preview keeps exact dependencies independently of vanilla and CurseForge")
        check(box.IsEnabled AndAlso Not box.IsChecked.GetValueOrDefault() AndAlso CStr(box.Tag) = "mods/optional.jar", "MR optional paths are available offline and initially unselected")
        box.IsChecked = True : ModrinthInstanceName.Text = "mr-custom"
        ShowSettings(Me, New RoutedEventArgs()) : ShowDownload(Me, New RoutedEventArgs()) : layout()
        check(ModrinthInstanceName.Text = "mr-custom" AndAlso box.IsChecked.GetValueOrDefault() AndAlso _modrinthSelection Is package, "MR settings navigation retains package identity name and choices")
        ModrinthInstanceName.Text = "bad/name"
        check(Not ModrinthInstallButton.IsEnabled AndAlso ModrinthNameHint.Visibility = Visibility.Visible, "MR inline name validation rejects escaping instance names")
        ModrinthInstanceName.Text = "existing" : IO.Directory.CreateDirectory(Path.Combine(root, "versions", "existing")) : RefreshPackAvailability()
        check(Not ModrinthInstallButton.IsEnabled, "MR existing instance is refused even if vanilla repair is enabled")
        ModrinthInstanceName.Text = "mr-custom" : RepairBox.IsChecked = True
        Dim invalid = Path.Combine(directory, "invalid-mr-" & Guid.NewGuid().ToString("N") & ".mrpack") : File.WriteAllText(invalid, "not a zip")
        Await ReadLocalPackAsync(invalid)
        check(_modrinthSelection Is package AndAlso ModrinthInstanceName.Text = "mr-custom" AndAlso box.IsChecked.GetValueOrDefault() AndAlso ModrinthReadStatus.Text.Contains("无法读取"), "MR failed replacement preserves the previous plan and shows an error")
        Dim reading = ReadLocalPackAsync(source) : _packReadCancellation.Cancel() : Await reading
        check(_modrinthSelection Is package AndAlso Not _readingPack AndAlso ImportModrinthButton.IsEnabled AndAlso ModrinthInstallButton.IsEnabled, "MR cancelled parsing releases both import entries and preserves the previous plan")
        Await ReadLocalPackAsync(source) : ModrinthInstanceName.Text = "mr-custom"
        check(_modrinthSelection IsNot package AndAlso ModrinthInstallButton.IsEnabled, "MR parsing succeeds on retry after cancellation")
        reading = ReadLocalPackAsync(source) : ShowSettings(Me, New RoutedEventArgs()) : Await reading
        check(SettingsPage.IsVisible AndAlso _modrinthSelection IsNot Nothing, "MR late parsing does not hijack settings navigation")
        ShowDownload(Me, New RoutedEventArgs()) : layout() : ModrinthInstanceName.Text = "mr-custom"
        Dim starts = 0, gate As New TaskCompletionSource(Of InstallResult)(), seenName As String = Nothing, seenMode As InstanceIsolationMode, seenGame As VersionEntry = Nothing
        _installRunner = Function(game, gameRoot, name, components, isolation, token)
                             starts += 1 : seenName = name : seenMode = isolation : seenGame = game
                             Return gate.Task
                         End Function
        _motionPreview = True : _categoryMotion.Finish() : _surfaceMotion.Finish()
        DownloadViewHost.IsHitTestVisible = False
        StartInstall(ModrinthInstallButton, New RoutedEventArgs())
        check(starts = 0, "MR animation input gate prevents hidden card installation")
        DownloadViewHost.IsHitTestVisible = True
        StartInstall(ModrinthInstallButton, New RoutedEventArgs()) : layout()
        check(_busy AndAlso starts = 1 AndAlso seenName = "mr-custom" AndAlso seenMode = InstanceIsolationMode.Enabled AndAlso seenGame.Id = "1.20.1", "MR starts its own exact isolated instance despite vanilla repair preferences")
        check(Not ImportModrinthButton.IsEnabled AndAlso Not ImportPackButton.IsEnabled AndAlso Not ModrinthOptionsCard.IsEnabled AndAlso Not DownloadNav.IsEnabled, "MR active installation freezes both format cards and category switching")
        check(_taskComponents.StartsWith("Modrinth Zombie preview", StringComparison.Ordinal) AndAlso _taskRows("metadata").Label.Text.Contains("校验整合包"), "MR progress describes the selected format without CurseForge metadata queries")
        ResumeInstallation(Me, New RoutedEventArgs()) : layout()
        check(_downloadView = DownloadView.Modpacks AndAlso ModrinthInstanceName.Text = "mr-custom" AndAlso Not ModrinthInstallButton.IsEnabled, "MR task return retains the frozen import form")
        CancelInstall(Me, New RoutedEventArgs()) : gate.TrySetCanceled() : Await WaitForNameInstallAsync()
        check(Not _busy AndAlso ModrinthInstallButton.IsEnabled AndAlso ImportPackButton.IsEnabled AndAlso _taskOutcome = TaskOutcome.Cancelled, "MR cancellation restores controls for retry")
        gate = New TaskCompletionSource(Of InstallResult)()
        _categoryMotion.Finish() : _surfaceMotion.Finish()
        PressControlKey(ModrinthInstanceName, Key.Enter)
        check(starts = 2 AndAlso seenName = "mr-custom", "MR Enter retries the same import through PCL input controls")
        gate.TrySetResult(New InstallResult With {.InstancePath = Path.Combine(root, "versions", "mr-custom"), .FileCount = 1}) : Await WaitForNameInstallAsync()
        check(_taskOutcome = TaskOutcome.Succeeded AndAlso _selectedEntry Is Nothing AndAlso RepairBox.IsChecked.GetValueOrDefault(), "MR completion preserves unrelated vanilla form state")
        ResumeInstallation(Me, New RoutedEventArgs()) : _categoryMotion.Finish() : layout()
        Dim cfSource = Path.Combine(directory, "ui-dual-" & Guid.NewGuid().ToString("N") & ".zip")
        Using zip = ZipFile.Open(cfSource, ZipArchiveMode.Create), writer As New StreamWriter(zip.CreateEntry("manifest.json").Open())
            writer.Write(New JObject From {{"manifestType", "minecraftModpack"}, {"manifestVersion", 1}, {"name", "CF independent"}, {"version", "1"}, {"minecraft", New JObject From {{"version", "1.20.1"}, {"modLoaders", New JArray()}}}, {"files", New JArray()}}.ToString())
        End Using
        SelectCurseForgePackage(CurseForgePackage.Read(cfSource)) : PackInstanceName.Text = "cf-custom"
        check(Not _activeModrinth AndAlso ModrinthInstanceName.Text = "mr-custom" AndAlso _modrinthSelection IsNot Nothing, "CF selection preserves the independent MR form")
        SelectModrinthPackage(_modrinthSelection) : ModrinthInstanceName.Text = "mr-custom"
        check(_activeModrinth AndAlso PackInstanceName.Text = "cf-custom" AndAlso _packSelection.Name = "CF independent", "MR selection preserves the independent CF form")
        Dim disguised = Path.ChangeExtension(source, ".zip") : File.Copy(source, disguised)
        Await ReadLocalPackAsync(disguised) : ModrinthInstanceName.Text = "mr-custom"
        check(_activeModrinth AndAlso ModrinthSource.Text = disguised AndAlso PackReadStatus.Text.Contains("已识别为 Modrinth") AndAlso PackInstanceName.Text = "cf-custom", "MR ZIP content is routed to the correct card without a stuck reading label or loss of CF choices")
        _categoryMotion.Finish() : _surfaceMotion.Finish()
        gate = New TaskCompletionSource(Of InstallResult)()
        StartInstall(PackInstallButton, New RoutedEventArgs())
        check(starts = 3 AndAlso seenName = "cf-custom" AndAlso Not _activeModrinth, "explicit CF install button cannot accidentally install the last selected MR pack")
        gate.TrySetCanceled() : Await WaitForNameInstallAsync() : ResumeInstallation(Me, New RoutedEventArgs())
        host.Width = 810 : host.Height = 470 : layout()
        check(ModrinthCard.ActualWidth > 500 AndAlso ImportModrinthButton.ActualWidth = 120 AndAlso ModpackPage.ScrollableHeight > 0, "MR card and PCL controls remain usable at minimum window size")
        SaveControlVisual(host, Path.Combine(directory, "download-modrinth-card.png"))
        _installRunner = Nothing : _motionPreview = False
    End Function
End Class
