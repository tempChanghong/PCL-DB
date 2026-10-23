Imports System.Text.RegularExpressions
Imports System.Windows
Imports System.Windows.Controls
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private _packTargetGame As String = ""
    Private _packGroupedDrop As Boolean
    Private _packGroupedOld As Boolean
    Private ReadOnly _packVersionGroups As New Dictionary(Of String, List(Of ModpackRelease))(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _packVersionCache As New Dictionary(Of String, PclCard)(StringComparer.OrdinalIgnoreCase)

    ' PCL 5ef81e4 PageDownloadCompDetail.Load_OnFinish / UpdateFilterResult:
    ' compact the filters at nine groups, but retain exact MC cards and file IDs.
    Private Sub InitializePackVersions()
        _packVersionCache.Clear() : _packVersionGroups.Clear()
        PackVersionFilters.Children.Clear()
        PackVersionFilterViewport.ScrollToHorizontalOffset(0)
        Dim games = _packReleases.SelectMany(Function(release) release.GameVersions).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        Dim filters As New List(Of String)
        For Each mode In {0, 1, 2, 3}
            _packGroupedDrop = (mode And 1) <> 0 : _packGroupedOld = (mode And 2) <> 0
            filters = games.Select(Function(game) PackGroupedVersionName(game, _packGroupedDrop, _packGroupedOld)).Distinct(StringComparer.OrdinalIgnoreCase).
                OrderByDescending(Function(game) game, New MinecraftGameComparer()).ToList()
            If filters.Count < 9 Then Exit For
        Next
        ' Only select the requested version if a real downloadable file supports it.
        _packGameFilter = If(filters.Count > 1 AndAlso _packTargetGame <> "" AndAlso games.Contains(_packTargetGame, StringComparer.OrdinalIgnoreCase), PackGroupedVersionName(_packTargetGame, _packGroupedDrop, _packGroupedOld), "")
        PackVersionFilterCard.Visibility = If(filters.Count > 1, Visibility.Visible, Visibility.Collapsed)
        For Each game In New String() {""}.Concat(filters)
            Dim button As New PclRadioButton With {.Text = If(game = "", "全部", game), .Tag = game, .Margin = New Thickness(2, 0, 2, 0), .ColorType = PclRadioButton.ColorState.Highlight,
                .Checked = String.Equals(game, _packGameFilter, StringComparison.OrdinalIgnoreCase)}
            button.LabText.Margin = New Thickness(-2, 0, 10, 0)
            AddHandler button.Click, Sub() SelectPackVersionFilter(button)
            PackVersionFilters.Children.Add(button)
        Next

        For Each release In _packReleases
            ' A file can belong to several MC cards, but only once in each card.
            Dim names = release.GameVersions.DefaultIfEmpty("").Select(Function(game) PackGroupedVersionName(game, False, False)).Distinct(StringComparer.OrdinalIgnoreCase)
            For Each groupName In names
                If Not _packVersionGroups.ContainsKey(groupName) Then _packVersionGroups(groupName) = New List(Of ModpackRelease)
                _packVersionGroups(groupName).Add(release)
            Next
        Next
        If _packTargetGame <> "" AndAlso games.Contains(_packTargetGame, StringComparer.OrdinalIgnoreCase) Then
            _packVersionGroups.Remove(_packTargetGame)
            _packVersionGroups(_packTargetGame & "（所选版本）") = _packReleases.Where(Function(release) release.GameVersions.Contains(_packTargetGame, StringComparer.OrdinalIgnoreCase)).ToList()
        End If
        RenderPackVersions()
    End Sub

    Private Sub SelectPackVersionFilter(button As PclRadioButton)
        If _busy OrElse _choosingOnline OrElse _closing OrElse _packMode <> PackBrowserMode.Detail OrElse _downloadView <> DownloadView.Modpacks OrElse Not PackDetailSurface.IsVisible OrElse Not PageSurface.IsHitTestVisible OrElse Not DownloadViewHost.IsHitTestVisible OrElse Not PackDetailResultsHost.IsHitTestVisible OrElse Not PackVersionFilters.Children.Contains(button) Then Return
        Dim selected = CStr(button.Tag)
        If _packGameFilter = selected Then Return
        ModpackPage.StopScrolling()
        _packGameFilter = selected
        RenderPackVersions()
    End Sub

    Private Sub RenderPackVersions()
        For Each button In PackVersionFilters.Children.OfType(Of PclRadioButton)()
            Dim highlighted = String.Equals(CStr(button.Tag), _packGameFilter, StringComparison.OrdinalIgnoreCase)
            If button.Checked <> highlighted Then button.Checked = highlighted
        Next
        Dim target = _packTargetGame & "（所选版本）"
        Dim names = _packVersionGroups.Keys.Where(Function(name) _packGameFilter = "" OrElse
            String.Equals(PackGroupedVersionName(If(name = target, _packTargetGame, name), _packGroupedDrop, _packGroupedOld), _packGameFilter, StringComparison.OrdinalIgnoreCase)).
            OrderBy(Function(name) If(name = target, 0, 1)).ThenByDescending(Function(name) name, New MinecraftGameComparer()).ToList()
        PackVersionCards.Children.Clear()
        For Each groupName In names
            Dim card As PclCard = Nothing
            If Not _packVersionCache.TryGetValue(groupName, card) Then
                Dim releases = _packVersionGroups(groupName).OrderByDescending(Function(release) release.Published).ToList()
                card = New PclCard With {.Title = groupName, .Tag = groupName, .Collapsible = True, .BodyMargin = New Thickness(20, 40, 18, 18), .Collapsed = True}
                card.Builder = Function() BuildPackVersionRows(releases)
                card.Collapsed = names.Count > 1 AndAlso groupName <> target
                _packVersionCache(groupName) = card
            ElseIf names.Count = 1 Then
                card.Collapsed = False
            End If
            PackVersionCards.Children.Add(card)
        Next
        PackDetailHint.Theme = PclHintTheme.Blue : PackDetailHint.ToolTip = Nothing
        PackDetailHint.Text = "没有适用于当前筛选条件、且具备下载地址及校验信息的客户端整合包。"
        PackDetailHint.Visibility = If(names.Count = 0, Visibility.Visible, Visibility.Collapsed)
    End Sub

    Private Function BuildPackVersionRows(releases As List(Of ModpackRelease)) As UIElement
        Dim rows As New StackPanel
        Dim titles = PackReleaseTitles(releases)
        Dim now = DateTimeOffset.UtcNow
        For index = 0 To releases.Count - 1
            Dim selected = releases(index), title = titles(index)
            Dim row As New PclListItem With {.Height = 42, .Title = title, .Info = PackReleaseInfo(selected, title, now), .ToolTip = PackReleaseToolTip(selected), .Tag = selected,
                .Logo = "pack://application:,,,/Images/Blocks/" & If(selected.ReleaseType = "release", "Release", If(selected.ReleaseType = "beta", "Beta", "Alpha")) & ".png"}
            Dim blocked = ModpackCompatibility.GetInstallBlockReason(selected)
            If blocked IsNot Nothing Then
                row.IsEnabled = False : row.Focusable = False : row.Cursor = Input.Cursors.Arrow
                row.Info &= "，暂不支持自动安装" : row.ToolTip = blocked & vbCrLf & vbCrLf & row.ToolTip.ToString()
                ToolTipService.SetShowOnDisabled(row, True)
                row.SetResourceReference(PclListItem.ForegroundProperty, "ColorBrushGray4")
            End If
            AddHandler row.Click, Sub()
                                      If PackVersionCards.Children.OfType(Of PclCard)().Any(Function(card) Not card.Collapsed AndAlso card.Body Is rows) Then InstallOnlinePack(selected, title)
                                  End Sub
            rows.Children.Add(row)
        Next
        Return rows
    End Function

    Private Shared Function PackGroupedVersionName(value As String, byDrop As Boolean, foldOld As Boolean) As String
        If String.IsNullOrEmpty(value) Then Return "其他"
        If value = "其他" OrElse value = "快照版" OrElse value = "远古版" Then Return value
        Dim family = MinecraftVersionRules.ReleaseFamily(value)
        If value.IndexOf("w", StringComparison.OrdinalIgnoreCase) >= 0 OrElse family = 209 Then Return "快照版"
        Dim standard = Regex.IsMatch(value, "^1\.\d") OrElse (Regex.IsMatch(value, "^[2-9]\d\.\d+") AndAlso family >= 260)
        If Not standard OrElse (foldOld AndAlso family < 120) Then Return "远古版"
        If Not byDrop Then Return value
        Return If(family >= 250, (family \ 10).ToString(Globalization.CultureInfo.InvariantCulture) & "." & (family Mod 10).ToString(Globalization.CultureInfo.InvariantCulture), "1." & (family \ 10).ToString(Globalization.CultureInfo.InvariantCulture))
    End Function

    Private Class MinecraftGameComparer
        Implements IComparer(Of String)
        Public Function Compare(x As String, y As String) As Integer Implements IComparer(Of String).Compare
            If String.Equals(x, y, StringComparison.OrdinalIgnoreCase) Then Return 0
            Dim xs = x.Contains("."), ys = y.Contains(".")
            If xs <> ys Then Return If(xs, 1, -1)
            If Not xs Then Return -StringComparer.OrdinalIgnoreCase.Compare(x, y)
            Return ComponentSelection.CompareMc(x, y)
        End Function
    End Class
End Class
