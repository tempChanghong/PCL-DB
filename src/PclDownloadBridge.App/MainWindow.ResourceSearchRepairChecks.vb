Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyResourceSearchRepairUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim host As New Border With {.Width = 960, .Height = 740}, layout = ControlLayout(host), nativeFocus = LocalModCheckGetNativeFocus()
        Dim appearance = owner._appearance.ToJson()
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     If Not ok Then Throw New InvalidOperationException(label)
                                                     passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, CInt(host.Width), CInt(host.Height))
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim work = owner.CheckResourceSearchRepairAsync(directory, host, layout, check)
                If Not PumpMotionUntil(Function() work.IsCompleted, 60000) Then Throw New TimeoutException("Resource search repair checks timed out.")
                work.GetAwaiter().GetResult()
            End Using
        Finally
            owner._closing = True : owner.CancelResourceRequests()
            owner._packSearchLoading.Reset() : owner._packDetailLoading.Reset()
            owner._categoryMotion.Finish() : owner._surfaceMotion.Finish()
            owner.RestoreAppearance(appearance)
            host.Child = Nothing : owner.Content = original
            LocalModCheckSetNativeFocus(nativeFocus)
            SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub

    Private Async Function CheckResourceSearchRepairAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim handler As New CommunityFixtureHandler(), now As Double = 0
        _packCatalogFactory = Function() New ModpackCatalogClient(handler, "")
        Dim settle As Action = Sub()
                                   _surfaceMotion.Finish() : _categoryMotion.Finish() : FinishPackContentMotion() : layout()
                               End Sub
        ShowDownload(Nothing, Nothing) : SetDownloadView(DownloadView.Modpacks) : settle()
        _motionPreview = True : _packLoadingTimingForChecks = True
        _packSearchLoading.NowForChecks = Function() now
        PackSearchSource.SelectedIndex = 2
        SubmitPackSearch(PackSearchButton, Nothing)
        Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : settle()
        check(PackProjectRows.Children.OfType(Of PclResourceItem)().Count() = 1 AndAlso PackResultsCard.IsVisible, "real search button accepts the initial modpack query with animations enabled")
        For Each kind In {CommunityResourceKind.Mod, CommunityResourceKind.Modpack, CommunityResourceKind.ResourcePack, CommunityResourceKind.Shader, CommunityResourceKind.DataPack}
            SelectResourceKind(kind) : settle()
            PackSearchSource.SelectedIndex = 2 : PackSearchText.Text = kind.ToString()
            SubmitPackSearch(PackSearchButton, Nothing)
            Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : settle()
            check(CanUsePackSearchInput(PackSearchButton) AndAlso PackProjectRows.Children.OfType(Of PclResourceItem)().Count() = 1 AndAlso PackResultsCard.IsVisible AndAlso PackResultsCard.Opacity = 1,
                "category entry restores visible results and a usable real search button: " & kind.ToString())
        Next
        SelectResourceKind(CommunityResourceKind.Mod) : settle()
        PackSearchText.Text = "" : layout()
        Dim grid = DirectCast(VisualTreeHelper.GetParent(PackSearchText), Grid), glyph = grid.Children.OfType(Of Shapes.Path)().Single()
        Dim content = DirectCast(PackSearchText.Template.FindName("PART_ContentHost", PackSearchText), FrameworkElement)
        Dim hint = DirectCast(PackSearchText.Template.FindName("Placeholder", PackSearchText), TextBlock)
        Dim edge = glyph.TranslatePoint(New Point(), grid).X + glyph.ActualWidth
        check(content.TranslatePoint(New Point(), grid).X - edge >= 8 AndAlso hint.TranslatePoint(New Point(), grid).X - edge >= 8 AndAlso CStr(PackSearchText.Tag) = "搜索 Mod",
            "search text and placeholder retain a visible gap after the glyph and a spaced Mod label")
        handler.Delay = 700 : PackSearchText.Text = "slow mod"
        SubmitPackSearch(PackSearchButton, Nothing) : layout()
        check(_packSearchRequest IsNot Nothing AndAlso _packSearchLoading.Phase = PclLoadingPresentation.Phases.Waiting, "slow Mod search enters the real deferred loading state")
        now += 400 : _packSearchLoading.Pulse() : settle()
        check(PackResultsCard.IsVisible AndAlso PackProjectRows.Children.OfType(Of PclLoadingView)().Count() = 1, "slow Mod search displays the loading card after its delay")
        check(PackProjectRows.Children.OfType(Of PclLoadingView)().Single().Children.OfType(Of TextBlock)().Single().Text = "正在获取 Mod 列表", "the real Mod loading label uses PCL TypeNameSpaced wording")
        SaveControlVisual(host, Path.Combine(directory, "search-mod-loading.png"))
        SelectResourceKind(CommunityResourceKind.Modpack) : settle()
        handler.Delay = 0 : PackSearchText.Text = "retry modpack"
        SubmitPackSearch(PackSearchButton, Nothing)
        Await WaitPackUiAsync(Function() _packSearchRequest Is Nothing) : settle()
        Await Task.Delay(800) : settle()
        check(_resourceKind = CommunityResourceKind.Modpack AndAlso _packSearchLoading.Phase = PclLoadingPresentation.Phases.Idle AndAlso PackResultsCard.IsVisible AndAlso PackProjectRows.Children.OfType(Of PclResourceItem)().Count() = 1 AndAlso CanUsePackSearchInput(PackSearchButton),
            "switching during the loading entry retires old callbacks and a button retry displays only the new category")
        SaveControlVisual(host, Path.Combine(directory, "search-category-return.png"))
        _packSearchLoading.NowForChecks = Nothing
        _resourceBrowsers.Clear() : _packAutoSearchForChecks = True
        SelectResourceKind(CommunityResourceKind.Mod)
        Await Task.Delay(1700) : layout()
        check(_packSearchRequest Is Nothing AndAlso _packSearchSession IsNot Nothing AndAlso PackResultsCard.IsVisible AndAlso PackResultsCard.Opacity = 1 AndAlso CanUsePackSearchInput(PackSearchButton),
            "automatic category search settles visible content without manually finishing its animation")
        SelectResourceKind(CommunityResourceKind.ResourcePack) : Await Task.Delay(30)
        SelectResourceKind(CommunityResourceKind.Modpack) : Await Task.Delay(30)
        SelectResourceKind(CommunityResourceKind.Shader)
        Await Task.Delay(2000) : layout()
        check(_resourceKind = CommunityResourceKind.Shader AndAlso _packSearchRequest Is Nothing AndAlso _packSearchSession IsNot Nothing AndAlso PackResultsCard.IsVisible AndAlso PackResultsCard.Opacity = 1 AndAlso CanUsePackSearchInput(PackSearchButton),
            "rapid animated category reversals settle the final automatic search and release its input gates")
        SelectResourceKind(CommunityResourceKind.Mod)
        Await Task.Delay(1700) : layout()
        check(PackResultsCard.IsVisible AndAlso PackResultsCard.Opacity = 1 AndAlso PackSearchResultsSurface.Opacity = 1 AndAlso CanUsePackSearchInput(PackSearchButton),
            "restoring a cached category inside its parent transition never stores a child animation's zero opacity")
        PackSearchText.Text = "after cached restore" : SubmitPackSearch(PackSearchButton, Nothing)
        Await Task.Delay(1200) : layout()
        check(_packSearchSession.Query.Text = "after cached restore" AndAlso PackProjectRows.Children.OfType(Of PclResourceItem)().Count() = 1 AndAlso PackResultsCard.Opacity = 1,
            "submitting the real search button after a cached restore displays its accepted replacement results")
        For Each dark In {False, True}
            RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", dark}})
            host.Width = 810 : host.Height = 510 : layout()
            SelectResourceKind(CommunityResourceKind.Modpack) : Await Task.Delay(1200)
            SelectResourceKind(CommunityResourceKind.Mod) : Await Task.Delay(1200) : layout()
            check(PackResultsCard.Opacity = 1 AndAlso PackSearchResultsSurface.Opacity = 1 AndAlso PackSearchText.ActualWidth > 150 AndAlso CanUsePackSearchInput(PackSearchButton),
                "cached category restoration retains visible content and input in compact layout: dark=" & dark.ToString())
            SaveControlVisual(host, Path.Combine(directory, "search-compact-" & If(dark, "dark", "light") & ".png"))
        Next
    End Function
End Class
