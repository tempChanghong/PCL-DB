Imports System.IO
Imports System.IO.Compression
Imports System.Net
Imports System.Net.Http
Imports System.Text
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Media.Imaging
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Class BrowserSourceHandler
        Inherits CommunityFixtureHandler
        Friend Gate As TaskCompletionSource(Of Boolean)
        Friend FailNext As Integer
        Friend Requests As Integer
        Protected Overrides Async Function SendAsync(request As HttpRequestMessage, token As CancellationToken) As Task(Of HttpResponseMessage)
            If request.RequestUri.Host <> "mod.mcimirror.top" Then Return Await MyBase.SendAsync(request, token)
            Interlocked.Increment(Requests)
            If Interlocked.Exchange(FailNext, 0) = 1 Then Return New HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            Dim kind = CommunityResourceKind.Modpack, query = request.RequestUri.Query
            For Each candidate In {CommunityResourceKind.Mod, CommunityResourceKind.DataPack, CommunityResourceKind.ResourcePack, CommunityResourceKind.Shader}
                If query.Contains("classId=" & CommunityResources.ClassId(candidate).ToString() & "&") Then kind = candidate
            Next
            If Gate IsNot Nothing AndAlso kind = CommunityResourceKind.Shader Then
                Await Task.WhenAny(Gate.Task, Task.Delay(5000, token))
                token.ThrowIfCancellationRequested()
                If Not Gate.Task.IsCompleted Then Throw New TimeoutException("Held CF source was not released.")
            End If
            Dim project As New JObject From {{"id", CommunityResources.ClassId(kind)}, {"classId", CommunityResources.ClassId(kind)}, {"name", "CF " & kind.ToString()}, {"slug", "fixture"}, {"summary", "Source fixture"}, {"downloadCount", 2000}, {"dateModified", "2026-10-01T00:00:00Z"}}
            Dim data As New JObject From {{"data", New JArray(project)}, {"pagination", New JObject From {{"totalCount", 1}}}}
            Return New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New StringContent(data.ToString(), Encoding.UTF8, "application/json")}
        End Function
    End Class
    Public Sub VerifyResourceBrowserFollowupUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current, appearance = _appearance.ToJson()
        Dim host As New Border With {.Width = 960, .Height = 740}, layout = ControlLayout(host)
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     If Not ok Then Throw New InvalidOperationException(label)
                                                     passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 960, 740)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckResourceBrowserFollowupAsync(directory, host, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 45000) Then Throw New TimeoutException("Resource browser followup did not settle.")
                work.GetAwaiter().GetResult()
            End Using
        Finally
            owner._closing = True : owner.CancelResourceRequests() : owner._pclThemeMonitor.Dispose()
            owner._categoryMotion.Finish() : owner._surfaceMotion.Finish() : owner.FinishPackContentMotion()
            owner.RestoreAppearance(appearance) : host.Child = Nothing : owner.Content = original
            SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub
    Private Async Function CheckResourceBrowserFollowupAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim handler As New BrowserSourceHandler With {.Gate = New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)}
        _packCatalogFactory = Function() New ModpackCatalogClient(handler, "")
        ShowDownload(Nothing, Nothing) : layout()
        _motionPreview = True : _packLoadingTimingForChecks = True : _packAutoSearchForChecks = True : _categoryMotion.PauseForChecks = True
        SelectResourceKind(CommunityResourceKind.Shader) : layout()
        check(_packSearchRequest IsNot Nothing AndAlso PackProjectRows.Children.OfType(Of PclLoadingView)().Count() = 1 AndAlso Not PackSearchHint.IsVisible AndAlso
            Not PackProjectRows.Children.OfType(Of TextBlock)().Any(), "initial category entry starts its accepted request and loading card without a manual-search prompt")
        _categoryMotion.Finish() : _surfaceMotion.Finish()
        Await WaitPackUiAsync(Function() PackProjectRows.Children.OfType(Of PclResourceItem)().Count() = 1 AndAlso PackSearchHint.IsVisible) : FinishPackContentMotion() : layout()
        check(_packSearchRequest IsNot Nothing AndAlso PackSearchHint.Theme = PclHintTheme.Blue AndAlso PackSearchHint.Text.Contains("CurseForge") AndAlso
            DirectCast(PackProjectRows.Children.OfType(Of PclResourceItem)().Single().Tag, ModpackProject).Platform = ModpackPlatform.Modrinth,
            "the fast source is readable while a held CF source keeps loading without a false failure warning")
        SaveControlVisual(host, Path.Combine(directory, "fast-source-preview.png"))
        Dim project = DirectCast(PackProjectRows.Children.OfType(Of PclResourceItem)().Single().Tag, ModpackProject)
        OpenPackDetails(project) : _categoryMotion.Finish()
        Await WaitPackUiAsync(Function() _packDetailCompletedProject Is project) : FinishPackContentMotion() : layout()
        check(_packMode = PackBrowserMode.Detail AndAlso _packReleases.Count = 1, "a source preview project opens verified details and version selection before the other source returns")
        handler.Gate.TrySetResult(True)
        Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : layout()
        check(_packMode = PackBrowserMode.Detail AndAlso _packProject Is project AndAlso _packDetailCompletedProject Is project,
            "late mixed-source completion updates only the hidden search cache and cannot replace an open detail")
        BackToPackSearch(Nothing, Nothing) : _categoryMotion.Finish() : FinishPackContentMotion() : layout()
        check(PackProjectRows.Children.OfType(Of PclResourceItem)().Count() = 2 AndAlso Not PackSearchHint.IsVisible,
            "returning from preview details shows both final sources without a stale loading or error hint")
        _categoryMotion.PauseForChecks = False : _packAutoSearchForChecks = False
        For Each kind In {CommunityResourceKind.DataPack, CommunityResourceKind.Shader}
            SelectResourceKind(kind) : _categoryMotion.Finish() : FinishPackContentMotion() : layout()
            handler.FailNext = 1 : PackSearchSource.SelectedIndex = 1 : PackSearchText.Text = "CF retry " & kind.ToString()
            SubmitPackSearch(PackSearchButton, Nothing)
            Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : FinishPackContentMotion() : layout()
            check(PackProjectRows.Children.OfType(Of PclResourceItem)().Count() = 1 AndAlso Not PackSearchHint.IsVisible AndAlso
                DirectCast(PackProjectRows.Children.OfType(Of PclResourceItem)().Single().Tag, ModpackProject).Platform = ModpackPlatform.CurseForge,
                "CF-only category recovers a temporary error and displays its own projects: " & kind.ToString())
        Next
        Dim iconFolder = Path.Combine(directory, "mod-icon-ui-" & Guid.NewGuid().ToString("N")) : IO.Directory.CreateDirectory(iconFolder)
        Dim jar = Path.Combine(iconFolder, "icon.jar"), bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAQAAAACCAYAAAB/qH1jAAAAFElEQVR4nGPUqLjznwEJMCFzQGwATRQCf8HSwvAAAAAASUVORK5CYII=")
        Using stream = File.Create(jar), zip As New ZipArchive(stream, ZipArchiveMode.Create)
            Using writer As New StreamWriter(zip.CreateEntry("fabric.mod.json").Open()) : writer.Write("{""id"":""ui_icon"",""name"":""Local icon"",""version"":""1"",""icon"":""icon.png""}") : End Using
            Using output = zip.CreateEntry("icon.png").Open() : output.Write(bytes, 0, bytes.Length) : End Using
        End Using
        Dim entry = LocalModCatalog.Read(jar, CancellationToken.None), row As New PclLocalModItem(entry), bitmap = DirectCast(row.Icon.Source, BitmapImage)
        check(bitmap.UriSource Is Nothing AndAlso bitmap.IsFrozen AndAlso bitmap.PixelWidth > 0 AndAlso row.Icon.Stretch = Stretch.Uniform,
            "local Mod rows decode and freeze their archive icon while preserving its aspect ratio")
        File.Move(jar, jar & ".disabled")
        row = New PclLocalModItem(LocalModCatalog.Read(jar & ".disabled", CancellationToken.None))
        check(DirectCast(row.Icon.Source, BitmapImage).UriSource Is Nothing AndAlso row.DisabledBadge.Visibility = Visibility.Visible,
            "disabled Mod rows retain embedded artwork alongside the existing disabled badge")
        Dim thin = Path.Combine(iconFolder, "thin.jar"), thinBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAACACAYAAADK+QP0AAAAG0lEQVR4nGP4z8Dwn4mBgYFhlGAYDQMGeocBADZfAv3iiZ/bAAAAAElFTkSuQmCC")
        Using stream = File.Create(thin), zip As New ZipArchive(stream, ZipArchiveMode.Create)
            Using writer As New StreamWriter(zip.CreateEntry("fabric.mod.json").Open()) : writer.Write("{""id"":""thin_icon"",""version"":""1"",""icon"":""icon.png""}") : End Using
            Using output = zip.CreateEntry("icon.png").Open() : output.Write(thinBytes, 0, thinBytes.Length) : End Using
        End Using
        Dim thinBitmap = DirectCast(New PclLocalModItem(LocalModCatalog.Read(thin, CancellationToken.None)).Icon.Source, BitmapImage)
        check(thinBitmap.UriSource Is Nothing AndAlso thinBitmap.PixelWidth <= 68 AndAlso thinBitmap.PixelHeight <= 68,
            "thin local artwork is bounded by its longest dimension and never amplified into a huge decoded bitmap")
        ManagedIntro.Logo = "pack://application:,,,/Images/Blocks/CommandBlock.png"
        check(ManagedIntro.Children.OfType(Of Image)().Single().Stretch = Stretch.Uniform AndAlso New PclListItem With {.Logo = ManagedIntro.Logo}.LogoStretch = Stretch.Fill,
            "the tall instance introduction preserves icon proportions without changing other PCL list defaults")
        _motionPreview = False : ShowSettings(Nothing, Nothing) : layout()
        Dim one = Path.Combine(directory, "bound-pcl-one"), two = Path.Combine(directory, "bound-pcl-two")
        IO.Directory.CreateDirectory(Path.Combine(one, "PCL")) : IO.Directory.CreateDirectory(Path.Combine(two, "PCL"))
        Dim first = Path.Combine(one, "PCL", "Setup.ini"), second = Path.Combine(two, "PCL", "Setup.ini")
        File.WriteAllText(first, "UiLauncherTheme:0", New UTF8Encoding(False)) : File.WriteAllText(second, "UiLauncherTheme:1", New UTF8Encoding(False))
        Dim firstBytes = File.ReadAllBytes(first), secondBytes = File.ReadAllBytes(second)
        Dim firstBinding As New ExternalLauncherBinding With {.Workspace = one}, secondBinding As New ExternalLauncherBinding With {.Workspace = two}
        _launcherState = New LauncherBindingsState() : _launcherState.Bindings.Add(firstBinding) : _launcherState.Bindings.Add(secondBinding)
        _launcherState.SelectedRows(ExternalLauncherKind.Pcl) = firstBinding.Id : RenderLauncherBindings()
        _followPreview = True : FollowPclThemeBox.IsChecked = True
        check(PumpMotionUntil(Function() _pclThemeSnapshot IsNot Nothing, 2500) AndAlso CurrentPclThemeFile() = first AndAlso
            DirectCast(PclThemePathBox.Parent, Grid).Visibility = Visibility.Collapsed, "theme following uses the PCL selected in the launcher card without a second visible selector")
        PclBindingList.SelectedItem = secondBinding
        check(PumpMotionUntil(Function() _pclThemeSnapshot IsNot Nothing AndAlso _pclThemeSnapshot.ThemeId = 1, 2500) AndAlso CurrentPclThemeFile() = second,
            "switching the selected PCL row rebinds live theme reads and retires the previous installation")
        File.WriteAllText(second, "UiLauncherTheme:2", New UTF8Encoding(False))
        check(PumpMotionUntil(Function() _pclThemeSnapshot IsNot Nothing AndAlso _pclThemeSnapshot.ThemeId = 2, 2500), "the selected launcher's theme continues refreshing after the selection changes")
        check(File.ReadAllBytes(first).SequenceEqual(firstBytes) AndAlso File.ReadAllText(second).Trim() = "UiLauncherTheme:2", "theme binding and polling never rewrite external launcher files")
        _appearance.PclThemeFile = first : _launcherState.SelectedRows(ExternalLauncherKind.Pcl) = "missing-binding" : RenderLauncherBindings()
        Dim missingRetired = CurrentPclThemeFile() = "" AndAlso Not _pclThemeMonitor.Active AndAlso _pclThemeSnapshot Is Nothing
        secondBinding.ConfigPath = first : _launcherState.SelectedRows(ExternalLauncherKind.Pcl) = secondBinding.Id : RenderLauncherBindings()
        check(missingRetired AndAlso CurrentPclThemeFile() = "" AndAlso Not _pclThemeMonitor.Active AndAlso _pclThemeSnapshot Is Nothing,
            "missing or mismatched explicit PCL bindings fall back locally without selecting another saved source or throwing")
        secondBinding.ConfigPath = Nothing
        _launcherState.SelectedRows(ExternalLauncherKind.Pcl) = secondBinding.Id : RenderLauncherBindings()
        FollowPclThemeBox.IsChecked = False
        check(Not _pclThemeMonitor.Active AndAlso AppearanceThemes.IsEnabled AndAlso _pclThemeSnapshot Is Nothing, "turning follow off stops polling and restores local theme controls")
        SaveControlVisual(host, Path.Combine(directory, "theme-single-source.png"))
    End Function
End Class
