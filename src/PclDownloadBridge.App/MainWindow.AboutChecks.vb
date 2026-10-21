Imports System.IO
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Sub VerifyAbout(check As Action(Of Boolean, String), artifactDirectory As String)
        Dim savedAppearance = _appearance.ToJson()
        Dim content = DirectCast(Me.Content, FrameworkElement)
        Dim host As New Border With {.Width = 850, .Height = 700}
        Dim source As Interop.HwndSource = Nothing
        Dim layout As Action = Sub()
                                   host.Measure(New Size(host.Width, host.Height))
                                   host.Arrange(New Rect(0, 0, host.Width, host.Height))
                                   host.UpdateLayout()
                                   FlushControlLayout()
                                   host.UpdateLayout()
                               End Sub
        Dim click As Action(Of UIElement) = Sub(control)
                                               PointerEvent(control, Mouse.MouseDownEvent)
                                               PointerEvent(control, Mouse.MouseUpEvent)
                                           End Sub
        Dim actions As New List(Of KeyValuePair(Of String, String))
        Try
            _aboutAction = Sub(kind, value) actions.Add(New KeyValuePair(Of String, String)(kind, value))
            Me.Content = Nothing : host.Child = content
            source = PreviewSurface.Attach(host, 850, 700)
            SwitchMainPage(True)
            SettingsPage.BeginAnimation(OpacityProperty, Nothing) : SettingsPage.Opacity = 1
            layout()
            click(AboutEntry) : layout()
            check(AboutPage.Visibility = Visibility.Visible AndAlso SettingsPage.Visibility = Visibility.Collapsed AndAlso DownloadPage.Visibility = Visibility.Collapsed AndAlso SettingsTab.Checked AndAlso Not DownloadTab.Checked AndAlso TopTabs.Children.Count = 2, "the settings about button opens a dedicated page while retaining the two primary tabs")
            check(Title = ProductInfo.DisplayName AndAlso ProductTitle.Text = ProductInfo.DisplayName AndAlso AboutMonogram.Text = "DB" AndAlso AboutProductItem.Title = ProductInfo.FullName, "window title and titlebar identify PCL-DB while the about page shows its full name")
            Dim assembly = Reflection.Assembly.GetExecutingAssembly()
            Dim version = assembly.GetName().Version.ToString(3)
            check(AboutVersionItem.Info.Contains(version) AndAlso DirectCast(assembly.GetCustomAttributes(GetType(Reflection.AssemblyProductAttribute), False).Single(), Reflection.AssemblyProductAttribute).Product = ProductInfo.FullName AndAlso DirectCast(assembly.GetCustomAttributes(GetType(Reflection.AssemblyTitleAttribute), False).Single(), Reflection.AssemblyTitleAttribute).Title = ProductInfo.FullName, "about version and executable product metadata come from the current build")
            click(AboutCopyVersion)
            check(actions.Count = 1 AndAlso actions(0).Key = "copy" AndAlso actions(0).Value.Contains(ProductInfo.DisplayName & " " & version) AndAlso actions(0).Value.Contains(ProductInfo.PclReference) AndAlso Not actions(0).Value.Contains(_settingsDirectory), "copy-version action includes build and source information without personal settings paths")
            click(AboutPclSource) : click(AboutBmclapi)
            check(actions.Skip(1).Select(Function(a) a.Value).SequenceEqual({"https://github.com/Meloong-Git/PCL", "https://github.com/bangbang93/openbmclapi"}), "source buttons dispatch the actual PCL and BMCLAPI project links")
            For Each documentName As String In {"README.md", "SOURCE_REFERENCES.md", "THIRD_PARTY_NOTICES.md"}
                OpenAboutDocument(New FrameworkElement With {.Tag = documentName}, Nothing)
            Next
            check(actions.Skip(3).Count() = 3 AndAlso actions.Skip(3).All(Function(a) a.Key = "document" AndAlso File.Exists(a.Value)) AndAlso actions.Skip(3).Select(Function(a) Path.GetFileName(a.Value)).SequenceEqual({"README.md", "SOURCE_REFERENCES.md", "THIRD_PARTY_NOTICES.md"}), "about documentation actions resolve all three real bundled or development documents")
            Dim count = actions.Count
            OpenAboutDocument(New FrameworkElement With {.Tag = "../settings.json"}, Nothing)
            OpenAboutLink(New FrameworkElement With {.Tag = "https://example.invalid"}, Nothing)
            check(actions.Count = count, "unsupported document and link tags cannot dispatch arbitrary shell targets")
            _aboutAction = Sub(kind, value)
                               Throw New IOException("controlled action failure")
                           End Sub
            click(AboutCopyVersion)
            check(AboutActionStatus.Text.Contains("controlled action failure") AndAlso AboutPage.Visibility = Visibility.Visible, "an unavailable external action reports an inline error without closing the page")
            _aboutAction = Nothing
            AboutActionStatus.Text = "preview sentinel"
            click(AboutCopyVersion) : click(AboutPclSource)
            check(AboutActionStatus.Text = "preview sentinel", "preview mode suppresses real clipboard and browser side effects")
            AboutActionStatus.Text = ""
            AboutPage.ScrollToEnd() : layout()
            check(AboutPage.VerticalOffset > 0, "the complete about content remains reachable through vertical scrolling")
            click(AboutBack) : layout()
            check(SettingsPage.Visibility = Visibility.Visible AndAlso AboutPage.Visibility = Visibility.Collapsed AndAlso SettingsTab.Checked, "the about back button returns to the settings page")
            SettingsPage.ScrollToEnd() : layout()
            Dim offset = SettingsPage.VerticalOffset
            ShowAbout(Nothing, Nothing) : layout()
            check(AboutPage.VerticalOffset = 0, "reopening about resets its own scroll position")
            click(AboutBack) : layout()
            check(offset > 0 AndAlso Math.Abs(SettingsPage.VerticalOffset - offset) < 0.5, "returning from about preserves the prior settings scroll position")
            SelectVersion(_versions(0))
            SelectComponent(_componentEntries(ComponentKind.Fabric)(0))
            InstanceName.Text = "关于页切换前的名称"
            ResetTaskView("关于页导航测试", False)
            RenderTaskProgress(New TransferProgress With {.Phase = "files", .Total = 100, .Completed = 40, .StageFraction = 0.4})
            Dim progress = TaskProgress.Value
            _busy = True : SetBusy(True)
            ShowAbout(Nothing, Nothing) : layout()
            check(AboutPage.Visibility = Visibility.Visible AndAlso AboutCopyVersion.IsEnabled AndAlso Not DownloadSettingsCard.IsEnabled, "about remains available during a task while transfer settings stay protected")
            click(DownloadTab) : layout()
            check(AboutPage.Visibility = Visibility.Collapsed AndAlso DownloadPage.Visibility = Visibility.Visible AndAlso DownloadProgress.Visibility = Visibility.Visible AndAlso TaskProgress.Value = progress AndAlso InstanceName.Text = "关于页切换前的名称" AndAlso _componentSelection.Has(ComponentKind.Fabric), "leaving about during a task restores progress without losing the install plan")
            _busy = False : SetBusy(False)
            ShowAbout(Nothing, Nothing) : layout()
            click(SettingsTab) : layout()
            check(AboutPage.Visibility = Visibility.Visible AndAlso SettingsPage.Visibility = Visibility.Collapsed, "pressing the already selected settings tab preserves the current about subpage")
            click(AboutBack) : layout()
            RestoreAppearance(New JObject From {{"theme", "colorful"}})
            ShowAbout(Nothing, Nothing)
            host.Width = 810 : host.Height = 470 : layout()
            Dim buttons = AboutCreditsGrid.Children.OfType(Of PclButton)().ToList()
            check(buttons.All(Function(b) b.ActualWidth = 150 AndAlso b.TranslatePoint(New Point(b.ActualWidth, 0), AboutCreditsGrid).X <= AboutCreditsGrid.ActualWidth + 0.5) AndAlso AboutPage.ScrollableHeight > 0, "about action buttons fit the minimum window with a reachable scrollable body")
            AboutPage.ScrollToTop() : layout()
            SaveControlVisual(host, Path.Combine(artifactDirectory, "align-about-minimum-after-0.27.png"))
            host.Width = 850 : host.Height = 700 : layout()
            SaveControlVisual(host, Path.Combine(artifactDirectory, "align-about-colorful-after-0.27.png"))
        Finally
            _aboutAction = Nothing
            _busy = False : SetBusy(False)
            SwitchMainPage(False)
            If source IsNot Nothing Then
                source.RootVisual = Nothing
                source.Dispose()
            End If
            host.Child = Nothing
            If Me.Content Is Nothing Then Me.Content = content
            RestoreAppearance(savedAppearance)
        End Try
    End Sub
End Class
