Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyExperienceSearchUi(directory As String, passed As List(Of String))
        VerifyExperienceAuditUi(directory, passed, True)
    End Sub
    Public Sub VerifyExperiencePaletteUi(directory As String, passed As List(Of String))
        VerifyExperienceAuditUi(directory, passed, False)
    End Sub
    Private Sub VerifyExperienceAuditUi(directory As String, passed As List(Of String), search As Boolean)
        Dim owner As New MainWindow(True), original = owner.Content, appearance = _appearance.ToJson(), previous = SynchronizationContext.Current
        Dim host As New Border With {.Width = 900, .Height = 550}, layout = ControlLayout(host), observations As New JObject
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 900, 550)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                layout()
                If search Then
                    Dim work = owner.CheckExperienceSearchAsync(layout, check)
                    If Not PumpMotionUntil(Function() work.IsCompleted, 30000) Then Throw New TimeoutException("Search cache audit did not settle.")
                    work.GetAwaiter().GetResult()
                Else
                    owner.CheckExperiencePalette(directory, layout, check)
                End If
            End Using
            File.WriteAllText(Path.Combine(directory, If(search, "experience-search.json", "experience-palette.json")), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            owner._closing = True : owner.CancelResourceRequests() : owner._pclThemeCycleTimer.Stop() : owner._pclThemeMonitor.Dispose()
            owner._pageSwitch.Finish() : owner._surfaceMotion.Finish() : owner._categoryMotion.Finish() : owner.FinishPackContentMotion()
            owner.RestoreAppearance(appearance) : host.Child = Nothing : owner.Content = original
            SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub
    Private Async Function CheckExperienceSearchAsync(layout As Action, check As Action(Of Boolean, String)) As Task
        Dim handler As New BrowserSourceHandler, sourceModes As New List(Of Integer)
        _packCatalogFactory = Function()
                                  sourceModes.Add(_communityPreferences.Source)
                                  Return New ModpackCatalogClient(handler, "", _communityPreferences.Source)
                              End Function
        ShowDownload(Nothing, Nothing) : SelectResourceKind(CommunityResourceKind.Modpack) : _surfaceMotion.Finish() : _categoryMotion.Finish() : layout()
        PackSearchSource.SelectedIndex = 1 : RunPackSearch(Nothing, Nothing)
        Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : FinishPackContentMotion() : layout()
        Dim accepted = _packSearchSession, key = _packLastQuery, requests = handler.Requests
        CommunityFileNameBox.SelectedIndex = 3 : CommunityLocalModStyleBox.SelectedIndex = 1
        check(_packLastQuery = key, "presentation settings preserve the accepted search identity")
        RunPackSearch(Nothing, Nothing)
        Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : FinishPackContentMotion() : layout()
        check(_packSearchSession Is accepted AndAlso handler.Requests = requests, "filename and local Mod style changes reuse the identical completed search")

        SelectResourceKind(CommunityResourceKind.DataPack) : _categoryMotion.Finish() : layout()
        PackSearchSource.SelectedIndex = 1 : RunPackSearch(Nothing, Nothing)
        Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : FinishPackContentMotion() : layout()
        Dim cached = _packSearchSession
        SelectResourceKind(CommunityResourceKind.Modpack) : _categoryMotion.Finish() : layout()
        CommunitySourceBox.SelectedIndex = 0
        SelectResourceKind(CommunityResourceKind.DataPack) : _categoryMotion.Finish() : layout()
        Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : FinishPackContentMotion() : layout()
        check(_packSearchSession Is cached, "returning to a category preserves its accepted result until a new search is submitted")
        requests = handler.Requests : RunPackSearch(Nothing, Nothing)
        Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : FinishPackContentMotion() : layout()
        check(_packSearchSession IsNot cached AndAlso handler.Requests > requests AndAlso sourceModes.Last() = 0,
            "a changed source policy cannot reuse another category's old-policy search cache")
        accepted = _packSearchSession : requests = handler.Requests
        RunPackSearch(Nothing, Nothing)
        Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : FinishPackContentMotion() : layout()
        check(_packSearchSession Is accepted AndAlso handler.Requests = requests, "the new source policy retains PCL's identical-query cache")

        handler.Gate = New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        SelectResourceKind(CommunityResourceKind.Shader) : _categoryMotion.Finish() : layout()
        PackSearchSource.SelectedIndex = 1 : RunPackSearch(Nothing, Nothing)
        Await WaitPackUiAsync(Function() _packSearchRequest IsNot Nothing AndAlso handler.Requests > requests)
        accepted = _packSearchSession : Dim request = _packSearchRequest
        CommunityFileNameBox.SelectedIndex = 4 : CommunityLocalModStyleBox.SelectedIndex = 0
        RunPackSearch(Nothing, Nothing)
        check(_packSearchSession Is accepted AndAlso _packSearchRequest Is request AndAlso Not request.IsCancellationRequested,
            "presentation changes during loading retain the active request and cancellation owner")
        handler.Gate.TrySetResult(True)
        Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : FinishPackContentMotion() : layout()
        check(_packSearchSession.ErrorMessage = "" AndAlso PackProjectRows.Children.OfType(Of PclResourceItem)().Count() = 1,
            "the retained request completes with usable results after presentation changes")
    End Function
    Private Sub CheckExperiencePalette(directory As String, layout As Action, check As Action(Of Boolean, String))
        _followPreview = True : _developer.Enabled = True : _experimentalThemesUnlocked = True
        _appearance.FollowPclTheme = True : _appearance.PclThemeFile = "C:\audit\Setup.ini"
        ReceivePclTheme(PclThemeSnapshot.Parse(System.Text.Encoding.UTF8.GetBytes("UiLauncherTheme:12")))
        For Each dark In {False, True}
            _appearance.DarkMode = dark : ApplyAppearance(externalRefresh:=True)
            ShowDownload(Nothing, Nothing) : _surfaceMotion.Finish() : layout()
            PulseFollowedThemeCycle(0, True) : PulseFollowedThemeCycle(30000, True)
            Dim ink = DirectCast(DirectCast(DownloadTab.FindName("LabText"), TextBlock).Foreground, SolidColorBrush).Color
            check(ink = DirectCast(FindResource("ColorBrush3"), SolidColorBrush).Color,
                "selected title navigation follows live palette frames in dark=" & dark)
            Dim clicks = 0, click As RoutedEventHandler = Sub() clicks += 1
            AddHandler ManagementTab.Click, click
            PointerEvent(ManagementTab, UIElement.MouseLeftButtonDownEvent)
            PulseFollowedThemeCycle(60000, True)
            PointerEvent(ManagementTab, UIElement.MouseLeftButtonUpEvent)
            RemoveHandler ManagementTab.Click, click
            check(clicks = 1, "a live palette frame preserves an in-progress navigation click in dark=" & dark)
            _surfaceMotion.Finish() : layout()
            CheckPaletteControls(directory, layout, check, dark)
        Next
    End Sub
    Private Sub CheckPaletteControls(directory As String, layout As Action, check As Action(Of Boolean, String), dark As Boolean)
        Dim panel As New StackPanel With {.Width = 320, .HorizontalAlignment = HorizontalAlignment.Left, .VerticalAlignment = VerticalAlignment.Top, .Margin = New Thickness(0, 70, 0, 0)}
        Dim radio As New PclRadioBox With {.Content = "主题选择", .Height = 28, .GroupName = "PaletteAudit", .Style = DirectCast(FindResource("PclAppearanceRadio"), Style)}
        Dim action As New PclActionButton With {.Text = "项目链接", .Highlight = True, .Height = 28}
        Dim button As New PclButton With {.Text = "普通按钮", .Highlight = True, .Height = 28}
        Dim row As New PclListItem With {.Title = "实例", .Height = 42}
        Dim card As New PclCard With {.Title = "版本", .Collapsible = True, .Collapsed = True, .PauseArrowForChecks = True, .Body = New Border With {.Height = 80}}
        Dim resource As New PclResourceItem(New ModpackProject With {.Id = "audit", .Name = "项目", .Kind = CommunityResourceKind.Mod, .Platform = ModpackPlatform.Modrinth})
        Dim jar = Path.Combine(directory, "palette-local.jar")
        If Not File.Exists(jar) Then
            Using archive = IO.Compression.ZipFile.Open(jar, IO.Compression.ZipArchiveMode.Create)
                Using writer As New StreamWriter(archive.CreateEntry("fabric.mod.json").Open())
                    writer.Write("{""id"":""palette"",""name"":""本地 Mod"",""version"":""1""}")
                End Using
            End Using
        End If
        Dim local As New PclLocalModItem(LocalModCatalog.Read(jar, CancellationToken.None))
        For Each control In New UIElement() {radio, action, button, row, resource, local, card} : panel.Children.Add(control) : Next
        Shell.Children.Add(panel)
        Try
            layout()
            radio.MotionPolicy.PauseForChecks = True : action.MotionPolicy.PauseForChecks = True : resource.MotionPolicy.PauseForChecks = True : local.MotionPolicy.PauseForChecks = True
            radio.IsChecked = True
            Dim ring = radio.RingTransition, dot = radio.DotTransition
            row.Selected = True : Dim selection = row.SelectionTransition : selection?.Controller.Pause()
            ControlPointer(row, True) : Dim rowHover = row.HoverTransition : rowHover?.Controller.Pause()
            ControlPointer(resource, True) : Dim resourceHover = resource.HoverTransition
            ControlPointer(local, True) : Dim localHover = local.HoverTransition, inset = local.ActionInsetTransition, opacity = local.ActionOpacityTransition
            card.Collapsed = False : Dim height = card.HeightTransition, arrow = card.ArrowTransition : height?.Controller.Pause()
            ControlPointer(action, True) : PointerEvent(action, UIElement.MouseLeftButtonDownEvent)
            PulseFollowedThemeCycle(15000, True)
            Dim accent = DirectCast(FindResource("ColorBrush3"), SolidColorBrush).Color
            check(DirectCast(action.Foreground, SolidColorBrush).Color = accent AndAlso
                CBool(GetType(PclActionButton).GetField("_mouseDown", Reflection.BindingFlags.Instance Or Reflection.BindingFlags.NonPublic).GetValue(action)),
                "cycle recolors action ink without cancelling its current press in dark=" & dark)
            PointerEvent(button, UIElement.MouseLeftButtonDownEvent) : Dim press = button.ScaleTransition : press?.Controller.Pause()
            PulseFollowedThemeCycle(30000, True) : layout()
            check(DirectCast(radio.Glyph.GetValue(PclRadioGlyph.StrokeProperty), SolidColorBrush).Color = DirectCast(FindResource("ColorBrush2"), SolidColorBrush).Color AndAlso
                radio.RingTransition Is ring AndAlso radio.DotTransition Is dot AndAlso ring IsNot Nothing,
                "cycle recolors selected radio ink while retaining ring/dot clocks in dark=" & dark)
            check(button.ScaleTransition Is press AndAlso press IsNot Nothing,
                "cycle retains the current button press scale clock in dark=" & dark)
            check(row.SelectionTransition Is selection AndAlso row.HoverTransition Is rowHover AndAlso selection IsNot Nothing AndAlso rowHover IsNot Nothing AndAlso
                DirectCast(row.Foreground, SolidColorBrush).Color = DirectCast(FindResource("ColorBrush2"), SolidColorBrush).Color,
                "cycle recolors instance selection without replacing marker/hover clocks in dark=" & dark)
            check(resource.HoverTransition Is resourceHover AndAlso resourceHover IsNot Nothing AndAlso local.HoverTransition Is localHover AndAlso localHover IsNot Nothing AndAlso
                local.ActionInsetTransition Is inset AndAlso local.ActionOpacityTransition Is opacity AndAlso inset IsNot Nothing AndAlso opacity IsNot Nothing,
                "cycle retains resource and local Mod hover/action clocks in dark=" & dark)
            check(card.HeightTransition Is height AndAlso card.ArrowTransition Is arrow AndAlso height IsNot Nothing AndAlso arrow IsNot Nothing,
                "cycle retains card height and arrow clocks in dark=" & dark)
            rowHover?.Controller.SkipToFill() : resourceHover?.Controller.SkipToFill() : localHover?.Controller.SkipToFill() : layout()
            check(DirectCast(DirectCast(resource.Children(0), Border).Background, SolidColorBrush).Color = DirectCast(FindResource("ColorBrushBg1"), SolidColorBrush).Color,
                "finishing an old hover clock cannot restore its pre-cycle color in dark=" & dark)
            Shell.Children.Remove(panel) : layout()
            Dim oldInk = DirectCast(action.Foreground, SolidColorBrush).Color
            PulseFollowedThemeCycle(45000, True)
            check(DirectCast(action.Foreground, SolidColorBrush).Color = oldInk, "unloaded controls stop receiving color frames in dark=" & dark)
            Shell.Children.Add(panel) : layout() : PulseFollowedThemeCycle(0, True)
            check(DirectCast(action.Foreground, SolidColorBrush).Color = DirectCast(FindResource("ColorBrush3"), SolidColorBrush).Color,
                "reloaded controls resume following the current palette in dark=" & dark)
        Finally
            Shell.Children.Remove(panel) : layout()
        End Try
    End Sub
End Class
