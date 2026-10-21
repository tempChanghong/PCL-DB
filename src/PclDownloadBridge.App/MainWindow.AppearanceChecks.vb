Imports System.Windows
Imports System.Windows.Automation.Peers
Imports System.Windows.Automation.Provider
Imports System.Windows.Controls
Imports System.Windows.Media
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Function VerifyAppearanceUi(artifactDirectory As String, Optional checks As List(Of String) = Nothing) As List(Of String)
        If Not _preview Then Throw New InvalidOperationException("UI checks require isolated preview mode.")
        checks = If(checks, New List(Of String))
        Dim check As Action(Of Boolean, String) = Sub(condition, label)
                                                     If Not condition Then Throw New InvalidOperationException(label)
                                                     checks.Add(label)
                                                 End Sub
        VerifyDeveloper(check, artifactDirectory)
        VerifyAppearance(check, artifactDirectory)
        Return checks
    End Function

    Private Sub SelectAppearanceTheme(id As String)
        Dim radio = AppearanceThemes.Children.OfType(Of RadioButton)().Single(Function(r) CStr(r.Tag) = id)
        Dim peer = UIElementAutomationPeer.CreatePeerForElement(radio)
        DirectCast(peer.GetPattern(PatternInterface.SelectionItem), ISelectionItemProvider).Select()
    End Sub

    Private Sub VerifyAppearance(check As Action(Of Boolean, String), artifactDirectory As String)
        Dim saved = _appearance.ToJson()
        Dim savedDeveloper = _developer.ToJson(), wasUnlocked = _experimentalThemesUnlocked
        Dim content = DirectCast(Me.Content, FrameworkElement)
        Dim host As New Border With {.Width = 850, .Height = 700}
        Dim source As Interop.HwndSource = Nothing
        Dim layout As Action = Sub()
                                   host.Measure(New Size(host.Width, host.Height))
                                   host.Arrange(New Rect(0, 0, host.Width, host.Height))
                                   host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
                               End Sub
        Try
            OpenExperimentalPreview()
            RestoreAppearance(Nothing)
            check(_appearance.Theme = "blue" AndAlso OpacitySlider.Maximum = 600 AndAlso Opacity = 1 AndAlso OpacityValue.Text = "100%", "appearance defaults preserve PCL blue and the original 600-to-100-percent opacity mapping")
            check(AppearanceThemes.Children.Count = 10 AndAlso TopTabs.Children.Count = 2, "unlocked personalization contains ten choices while retaining only two main tabs")
            OpacitySlider.Value = 0
            check(Opacity = 0.4 AndAlso OpacityValue.Text = "40%", "the lowest opacity uses PCL's readable forty-percent floor")
            OpacitySlider.Value = 300
            check(Math.Abs(Opacity - 0.7) < 0.0001 AndAlso OpacityValue.Text = "70%", "opacity changes immediately apply the PCL slider formula")
            OpacitySlider.Value = 600
            Dim invalid = AppearancePreferences.Read(New JObject From {{"theme", "missing"}, {"transparency", -500}, {"hue", 999}, {"saturation", New JObject}, {"lightness", Double.NaN}, {"titleTexture", "false"}})
            check(invalid.Theme = "blue" AndAlso invalid.Transparency = 0 AndAlso invalid.Hue = 360 AndAlso invalid.Saturation = 80 AndAlso invalid.Lightness = 0 AndAlso invalid.TitleTexture, "invalid appearance settings fall back or clamp without propagating nonfinite colors")
            check(AppearancePreferences.Read(New JArray()).Theme = "blue", "non-object appearance data safely upgrades to defaults")
            RestoreAppearance(New JObject From {{"theme", "custom"}, {"hue", 285.5}, {"saturation", 62.5}, {"lightness", -3.5}, {"transparency", 450}})
            Dim directory = IO.Path.Combine(IO.Path.GetTempPath(), "PclDownloadBridgeAppearance-" & Guid.NewGuid().ToString("N"))
            Dim file = IO.Path.Combine(directory, "settings.json")
            Dim existing As New JObject From {{"source", 2}, {"versionSource", 1}, {"speed", "3.5"}, {"gameRoot", "D:\example\.minecraft"}, {"unknown", New JObject From {{"keep", True}}}}
            Dim original = existing.ToString()
            DownloadSettingsFile.Save(file, _appearance.Merge(existing))
            Dim restored = JObject.Parse(IO.File.ReadAllText(file))
            RestoreAppearance(restored("appearance"))
            check(_appearance.Theme = "custom" AndAlso HueSlider.Value = 285.5 AndAlso SaturationSlider.Value = 62.5 AndAlso LightnessSlider.Value = -3.5 AndAlso Math.Abs(Opacity - 0.85) < 0.0001, "custom colors and opacity survive an atomic settings-file round trip")
            check(existing.ToString() = original AndAlso CInt(restored("source")) = 2 AndAlso CInt(restored("versionSource")) = 1 AndAlso CStr(restored("speed")) = "3.5" AndAlso CBool(restored("unknown")("keep")), "saving appearance preserves persisted download preferences and unknown fields without mutating input JSON")
            check(JToken.DeepEquals(BuildSettings(0)("appearance"), _appearance.ToJson()), "normal preference saves include the current appearance alongside download settings")

            RestoreAppearance(Nothing)
            SelectVersion(_versions(0))
            SelectComponent(_componentEntries(ComponentKind.Fabric)(0))
            Dim view = _componentViews(ComponentKind.Fabric)
            view.Card.Collapsed = False
            Dim body = DirectCast(view.Card.Body, StackPanel)
            Dim row = MaterializeComponentRows(body).Single()
            InstanceName.Text = "保留外观切换前的名称"
            ResetTaskView("外观切换中的任务", False)
            RenderTaskProgress(New TransferProgress With {.Phase = "files", .Stage = "下载中", .StageFraction = 0.4, .Total = 100, .Completed = 40})
            Dim progress = TaskProgress.Value
            Me.Content = Nothing : host.Child = content
            source = PreviewSurface.Attach(host, 850, 700)
            layout()
            SelectAppearanceTheme("green")
            layout()
            Dim green = PclTheme.Hsl2(125, 55, 55)
            check(DirectCast(Application.Current.Resources("ColorBrush3"), SolidColorBrush).Color = green AndAlso DirectCast(TitleBar.Background, LinearGradientBrush).GradientStops(0).Color = PclTheme.Hsl2(125, 55, 48), "selecting a theme uses PCL HSL2 palette and title-bar formulas")
            check(DirectCast(DownloadTab.ShapeLogo.Fill, SolidColorBrush).Color = green, "the selected top tab immediately follows the new palette")
            check(DirectCast(InstanceName.BorderBrush, SolidColorBrush).Color = DirectCast(Application.Current.Resources("ColorBrushBg0"), SolidColorBrush).Color, "an already loaded text input refreshes its theme without hover or reconstruction")
            check(view.Card.Body Is body AndAlso row.Selected AndAlso InstanceName.Text = "保留外观切换前的名称" AndAlso _componentSelection.Has(ComponentKind.FabricApi), "changing theme preserves loader rows selection dependencies and the custom instance name")
            ' A never-loaded collapsed subtree can retain a cached DynamicResource
            ' until WPF loads it. Check its rendered contract after showing the page,
            ' rather than depending on another test having shown tasks beforehand.
            Dim hiddenStage = _taskRows("files").Icon.Fill.ToString()
            Dim hiddenLoaded = _taskRows("files").Icon.IsLoaded
            SetDownloadView(DownloadView.Progress)
            layout()
            IO.File.WriteAllText(IO.Path.Combine(artifactDirectory, "appearance-task-colors-0.26.json"), Newtonsoft.Json.JsonConvert.SerializeObject(New With {
                .Before = progress, .After = TaskProgress.Value, .Stage = _taskRows("files").Icon.Fill.ToString(), .Expected = green.ToString(),
                .BeforeRevealStage = hiddenStage, .BeforeRevealLoaded = hiddenLoaded,
                .Theme = _appearance.Theme, .TaskGeneration = _taskGeneration, .StageVisibility = _taskRows("files").Icon.Visibility.ToString(),
                .StageLoaded = _taskRows("files").Icon.IsLoaded, .StageResourceExpression = DependencyPropertyHelper.GetValueSource(_taskRows("files").Icon, Shapes.Path.FillProperty).IsExpression,
                .ActiveTextColor = _taskRows("files").State.Foreground.ToString(), .ProgressPageVisibility = DownloadProgress.Visibility.ToString()}, Newtonsoft.Json.Formatting.Indented))
            check(TaskProgress.Value = progress AndAlso DirectCast(_taskRows("files").Icon.Fill, SolidColorBrush).Color = green, "theme changes recolor existing task stages without resetting download progress")
            SaveControlVisual(host, IO.Path.Combine(artifactDirectory, "appearance-revealed.png"))
            Dim originalPolicy = Policy()
            SetBusy(True)
            SelectAppearanceTheme("colorful")
            layout()
            check(AppearanceCard.IsEnabled AndAlso Not DownloadSettingsCard.IsEnabled AndAlso Policy().Preference = originalPolicy.Preference AndAlso Policy().EffectiveVersionPreference = originalPolicy.EffectiveVersionPreference, "appearance remains adjustable during a task while transfer preferences remain locked and unchanged")
            SetBusy(False)
            check(TitleTexture.Visibility = Visibility.Visible AndAlso DirectCast(TitleBar.Background, LinearGradientBrush).GradientStops.Select(Function(s) s.Color).Distinct().Count() = 3, "the colorful preset combines three title hues with the original PCL texture")
            check(TitleTexture.Opacity = 1 AndAlso DirectCast(TitleTexture.Source, Imaging.BitmapSource).PixelWidth = 1200, "the original title texture retains its embedded low alpha without a second opacity reduction")
            TitleTextureBox.IsChecked = False
            check(TitleTexture.Visibility = Visibility.Collapsed, "title texture can be independently disabled")
            ColorfulBackgroundBox.IsChecked = False
            check(TypeOf Shell.Background Is SolidColorBrush AndAlso DirectCast(Shell.Background, SolidColorBrush).Color = Color.FromRgb(245, 245, 245), "turning off colorful background restores PCL's neutral 245 gray")
            SelectAppearanceTheme("custom")
            HueSlider.Value = 285 : SaturationSlider.Value = 65 : LightnessSlider.Value = 2
            check(CustomThemeGrid.Visibility = Visibility.Visible AndAlso DirectCast(Application.Current.Resources("ColorBrush3"), SolidColorBrush).Color = PclTheme.Hsl2(285, 65, 57), "custom hue saturation and lightness update the shared palette through the original formula")
            SwitchMainPage(True)
            SettingsPage.BeginAnimation(OpacityProperty, Nothing) : SettingsPage.Opacity = 1
            SettingsPage.ScrollToTop()
            host.Width = 810 : host.Height = 470 : layout()
            Dim lastChoice = AppearanceThemes.Children.OfType(Of RadioButton)().Last()
            check(lastChoice.ActualWidth > 70 AndAlso lastChoice.TranslatePoint(New Point(lastChoice.ActualWidth, 0), AppearanceGrid).X <= AppearanceGrid.ActualWidth + 0.5 AndAlso SettingsPage.ScrollableHeight > 0, "personalization choices stay inside the minimum window and remain scrollable")
            Dim firstChoice = AppearanceThemes.Children.OfType(Of RadioButton)().First()
            Dim ring = DirectCast(firstChoice.Template.FindName("Ring", firstChoice), Shapes.Ellipse)
            check(ring.Width = 18 AndAlso ring.StrokeThickness = 1.1 AndAlso DirectCast(firstChoice.Template.FindName("Dot", firstChoice), Shapes.Ellipse).Width = 9, "theme radio controls retain PCL's eighteen-DIP ring and nine-DIP selected dot")
            Dim colors As New HashSet(Of Color)
            For Each theme In AppearancePreferences.ThemeIds
                SelectAppearanceTheme(theme)
                colors.Add(DirectCast(Application.Current.Resources("ColorBrush3"), SolidColorBrush).Color)
            Next
            check(colors.Count = AppearancePreferences.ThemeIds.Length AndAlso AppearanceThemes.Children.OfType(Of RadioButton)().Count(Function(r) r.IsChecked.GetValueOrDefault()) = 1, "all ten theme choices produce distinct palettes with exactly one selected radio")
            ResetAppearance(Nothing, Nothing)
            check(_appearance.Theme = "blue" AndAlso Opacity = 1 AndAlso TitleTexture.Visibility = Visibility.Collapsed AndAlso CustomThemeGrid.Visibility = Visibility.Collapsed AndAlso ColorfulBackgroundBox.IsChecked.GetValueOrDefault(), "reset restores only default appearance values")
            check(InstanceName.Text = "保留外观切换前的名称" AndAlso TaskProgress.Value = progress AndAlso _componentSelection.Has(ComponentKind.Fabric), "resetting appearance retains the installation plan and task progress")
            VerifyTaskAppearanceStates(check, layout, artifactDirectory)
        Finally
            SetBusy(False)
            If source IsNot Nothing Then
                source.RootVisual = Nothing
                source.Dispose()
            End If
            host.Child = Nothing
            If Me.Content Is Nothing Then Me.Content = content
            RestoreDeveloperSettings(savedDeveloper)
            _experimentalThemesUnlocked = wasUnlocked
            RestoreAppearance(saved)
        End Try
    End Sub

    Private Sub VerifyTaskAppearanceStates(check As Action(Of Boolean, String), layout As Action, artifactDirectory As String)
        Dim observations As New JArray
        For Each scenario In {"active-visible", "active-hidden", "succeeded", "failed", "cancelled"}
            ResetTaskView("主题状态检查", True)
            RenderTaskProgress(New TransferProgress With {.Phase = "metadata", .StageFraction = 1})
            RenderTaskProgress(New TransferProgress With {.Phase = "files", .Stage = "下载中", .StageFraction = 0.4, .Total = 100, .Completed = 40})
            Select Case scenario
                Case "succeeded" : FinishTaskView(True, "已完成")
                Case "failed" : FinishTaskView(False, "保留失败原因")
                Case "cancelled"
                    _taskCancelling = True
                    FinishTaskView(False, "保留取消原因")
            End Select
            SetDownloadView(DownloadView.Progress)
            SwitchMainPage(scenario = "active-hidden")
            layout()
            Dim progress = TaskProgress.Value, percent = TaskPercent.Text, generation = _taskGeneration
            Dim outcome = _taskOutcome, detail = TaskDetail.Text
            Dim originalRows = _taskRows.ToDictionary(Function(pair) pair.Key, Function(pair) pair.Value)
            Dim state = TaskAppearanceSemantics()
            For Each theme In {"green", "colorful", "blue"}
                SelectAppearanceTheme(theme)
                layout()
                Dim expected = If(theme = "green", PclTheme.Hsl2(125, 55, 55), If(theme = "colorful", PclTheme.Hsl2(350, 65, 58), PclTheme.Hsl2(210, 85, 55)))
                Dim expectedIdle = DirectCast(Application.Current.Resources("ColorBrushGray2"), SolidColorBrush).Color
                Dim colors As New JArray
                For Each pair In _taskRows
                    colors.Add(New JObject From {{"phase", pair.Key}, {"icon", pair.Value.Icon.Fill.ToString()}, {"text", pair.Value.State.Foreground.ToString()},
                        {"iconLoaded", pair.Value.Icon.IsLoaded}, {"iconExpression", DependencyPropertyHelper.GetValueSource(pair.Value.Icon, Shapes.Path.FillProperty).IsExpression}})
                Next
                observations.Add(New JObject From {{"scenario", scenario}, {"theme", theme}, {"expected", expected.ToString()}, {"before", progress}, {"after", TaskProgress.Value}, {"rows", colors}})
                IO.File.WriteAllText(IO.Path.Combine(artifactDirectory, "appearance-task-states.json"), observations.ToString())
                Dim paletteMatches = _taskRows.All(Function(pair) DirectCast(pair.Value.Icon.Fill, SolidColorBrush).Color = expected AndAlso
                    DirectCast(pair.Value.State.Foreground, SolidColorBrush).Color = If(scenario = "cancelled" AndAlso Not pair.Value.Complete, expectedIdle, expected))
                check(paletteMatches, "task theme updates every existing active waiting completed and terminal color: " & scenario & "/" & theme)
                check(TaskProgress.Value = progress AndAlso TaskPercent.Text = percent AndAlso _taskGeneration = generation AndAlso _taskOutcome = outcome AndAlso TaskDetail.Text = detail,
                    "task theme preserves progress generation outcome and explanation: " & scenario & "/" & theme)
                check(TaskAppearanceSemantics() = state AndAlso _taskRows.All(Function(pair) pair.Value Is originalRows(pair.Key)),
                    "task theme retains phase rows icons labels and terminal state: " & scenario & "/" & theme)
            Next
        Next
    End Sub

    Private Function TaskAppearanceSemantics() As String
        Return New JArray(_taskRows.Select(Function(pair) New JObject From {{"phase", pair.Key}, {"complete", pair.Value.Complete},
            {"iconVisibility", pair.Value.Icon.Visibility.ToString()}, {"textVisibility", pair.Value.State.Visibility.ToString()},
            {"geometry", pair.Value.Icon.Data.ToString()}, {"state", pair.Value.State.Text}, {"label", pair.Value.Label.Text}})).ToString()
    End Function
End Class
