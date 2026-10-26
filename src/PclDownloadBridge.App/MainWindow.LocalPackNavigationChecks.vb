Imports System.IO
Imports System.IO.Compression
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyLocalPackNavigationUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim host As New Border With {.Width = 900, .Height = 600}, layout = ControlLayout(host), observations As New JObject
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 600)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckLocalPackNavigationAsync(directory, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 30000) Then Throw New TimeoutException("Local pack navigation did not settle.")
                work.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "local-pack-navigation.json"), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            owner._closing = True : owner.CancelPackRead() : owner.CancelResourceRequests() : owner.CancelManagementRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner._managementMotion.Finish()
            host.Child = Nothing : owner.Content = original : SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub

    Private Async Function CheckLocalPackNavigationAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim root = Path.Combine(directory, "local-pack-game-" & Guid.NewGuid().ToString("N"))
        IO.Directory.CreateDirectory(root) : SelectManualFolder(root)
        _managementProvider = Function(target, token) Task.FromResult(New List(Of InstalledInstance)())
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion() : _managementMotion.Finish() : layout()
                               End Sub
        For Each modrinth In {False, True}
            Dim source = Path.Combine(directory, "navigation-" & Guid.NewGuid().ToString("N") & If(modrinth, ".mrpack", ".zip"))
            WriteLocalPackNavigationArchive(source, modrinth)
            Dim sourceBytes = File.ReadAllBytes(source)
            For Each animated In {False, True}
                For Each dark In {False, True}
                    _motionPreview = animated : RestoreAppearance(New JObject From {{"darkMode", dark}})
                    Await ReadLocalPackAsync(source) : settle()
                    Dim name As TextBox = If(modrinth, ModrinthInstanceName, PackInstanceName)
                    Dim options = If(modrinth, ModrinthOptionalFiles, PackOptionalFiles)
                    If options.Children.Count <> 1 Then Throw New InvalidDataException("Navigation fixture was not read: " & If(modrinth, ModrinthReadStatus.Text, PackReadStatus.Text))
                    Dim optionBox = DirectCast(options.Children(0), PclCheckBox)
                    ' A resolved optional-file query permits this real user choice.
                    optionBox.IsEnabled = True : optionBox.Content = "optional.jar" : optionBox.IsChecked = True
                    name.Text = "keep-local-plan"
                    Dim generation = _taskGeneration, folder = _selectedFolder
                    Dim label = "/format=" & If(modrinth, "Modrinth", "CurseForge") & "/motion=" & animated & "/dark=" & dark

                    ' ReadLocalPackAsync yields before opening the actual archive.
                    ' Navigate synchronously before allowing that continuation to run.
                    Dim reading = ReadLocalPackAsync(source)
                    SelectResourceKind(CommunityResourceKind.Mod) : PackSearchText.Text = "keep-mod-query"
                    Await reading : settle()
                    check(_resourceKind = CommunityResourceKind.Mod AndAlso _packMode = PackBrowserMode.Search AndAlso PackSearchText.Text = "keep-mod-query" AndAlso DownloadPage.IsVisible,
                        "a completed local archive read cannot take over another resource category" & label)
                    optionBox = DirectCast(options.Children(0), PclCheckBox)
                    check(name.Text = "keep-local-plan" AndAlso optionBox.IsChecked.GetValueOrDefault() AndAlso Not _readingPack AndAlso _packReadCancellation Is Nothing,
                        "off-category completion preserves the accepted custom name and optional choice and releases its reader" & label)

                    SelectResourceKind(CommunityResourceKind.Modpack) : OpenLocalPacks(Nothing, Nothing) : settle()
                    reading = ReadLocalPackAsync(source)
                    BackToPackSearch(Nothing, Nothing) : PackSearchText.Text = "keep-pack-query"
                    Await reading : settle()
                    check(_resourceKind = CommunityResourceKind.Modpack AndAlso _packMode = PackBrowserMode.Search AndAlso PackSearchText.Text = "keep-pack-query",
                        "returning to modpack search while reading is not reversed by archive completion" & label)

                    Dim project As New ModpackProject With {.Platform = ModpackPlatform.Modrinth, .Kind = CommunityResourceKind.Modpack, .Id = "cached", .Name = "Retained online detail"}
                    Dim onlineReleases As New List(Of ModpackRelease) From {New ModpackRelease With {.Platform = project.Platform, .Kind = project.Kind, .ProjectId = project.Id,
                        .Id = "online", .Name = "Online release", .FileName = "online.mrpack", .GameVersions = {"1.20.1"}, .Loaders = {"fabric"}}}
                    _packProject = project : _packVerifiedProject = project : _packDetailCompletedProject = project : _packReleases = onlineReleases
                    InitializePackVersions()
                    OpenLocalPacks(Nothing, Nothing) : settle()
                    reading = ReadLocalPackAsync(source)
                    BackToPackSearch(Nothing, Nothing) : _categoryMotion.Finish()
                    OpenPackDetails(project)
                    Await reading : settle()
                    check(_packMode = PackBrowserMode.Detail AndAlso _packProject Is project AndAlso _packDetailCompletedProject Is project AndAlso _resourceBack.Count = 0 AndAlso
                        _packReleases Is onlineReleases AndAlso _packReleases.Single().Id = "online",
                        "opening an online detail during an archive read keeps the current detail and return stack" & label)

                    OpenLocalPacks(Nothing, Nothing) : settle()
                    reading = ReadLocalPackAsync(source)
                    ShowManagement(Nothing, Nothing)
                    Await reading : settle()
                    check(ManagementPage.IsVisible AndAlso Not DownloadPage.IsVisible AndAlso name.Text = "keep-local-plan" AndAlso _selectedFolder Is folder,
                        "background archive completion preserves the management surface folder and imported plan" & label)

                    reading = ReadLocalPackAsync(source) : CancelPackRead()
                    Await reading : settle()
                    Dim status = If(modrinth, ModrinthReadStatus, PackReadStatus)
                    check(Not _readingPack AndAlso _packReadCancellation Is Nothing AndAlso name.Text = "keep-local-plan" AndAlso status.Text.Contains("取消"),
                        "cancelled archive reading releases its request and retains the previous valid plan" & label)
                    Await ReadLocalPackAsync(source) : settle()
                    optionBox = DirectCast(options.Children(0), PclCheckBox)
                    check(_resourceKind = CommunityResourceKind.Modpack AndAlso _packMode = PackBrowserMode.Local AndAlso DownloadPage.IsVisible AndAlso
                        name.Text = "keep-local-plan" AndAlso optionBox.IsChecked.GetValueOrDefault() AndAlso If(modrinth, ModrinthOptionsCard.IsVisible, PackOptionsCard.IsVisible),
                        "retry in the actual import page still presents the selected format and retained choices" & label)
                    check(_taskGeneration = generation AndAlso Not _busy AndAlso Not _hasTask AndAlso _selectedFolder Is folder AndAlso
                        PageSurface.IsHitTestVisible AndAlso DownloadViewHost.IsHitTestVisible AndAlso sourceBytes.SequenceEqual(File.ReadAllBytes(source)) AndAlso Not IO.Directory.Exists(Path.Combine(root, "versions")),
                        "archive navigation does not start installation publish files mutate the source or leave stale input gates" & label)
                Next
            Next
        Next
    End Function

    Private Shared Sub WriteLocalPackNavigationArchive(path As String, modrinth As Boolean)
        Dim manifest As JObject
        If modrinth Then
            manifest = New JObject From {{"game", "minecraft"}, {"formatVersion", 1}, {"name", "Local MR navigation"}, {"versionId", "1.0"},
                {"dependencies", New JObject From {{"minecraft", "1.20.1"}, {"fabric-loader", "0.16.5"}}},
                {"files", New JArray(New JObject From {{"path", "mods/optional.jar"}, {"fileSize", 42}, {"hashes", New JObject From {{"sha1", New String("a"c, 40)}, {"sha512", New String("b"c, 128)}}},
                    {"downloads", New JArray("https://cdn.modrinth.com/optional.jar")}, {"env", New JObject From {{"client", "optional"}, {"server", "unsupported"}}}})}}
        Else
            manifest = New JObject From {{"manifestType", "minecraftModpack"}, {"manifestVersion", 1}, {"name", "Local CF navigation"}, {"version", "1.0"}, {"overrides", "overrides"},
                {"minecraft", New JObject From {{"version", "1.20.1"}, {"modLoaders", New JArray(New JObject From {{"id", "forge-47.2.0"}})}}},
                {"files", New JArray(New JObject From {{"projectID", 1}, {"fileID", 1001}, {"required", True}}, New JObject From {{"projectID", 2}, {"fileID", 1002}, {"required", False}})}}
        End If
        Using archive = ZipFile.Open(path, ZipArchiveMode.Create), writer As New StreamWriter(archive.CreateEntry(If(modrinth, "modrinth.index.json", "manifest.json")).Open())
            writer.Write(manifest.ToString())
        End Using
    End Sub
End Class
