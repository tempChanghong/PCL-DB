Imports System.Windows.Controls
Imports System.Windows.Input
Imports PclDownloadBridge.Core
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Sub PreviewSpecialVersions(selectInfinite As Boolean)
        _versions.AddRange({BrowserEntry("20w14infinite", "snapshot", "2020-04-01T12:00:00Z"), BrowserEntry("3D Shareware v1.34", "snapshot", "2019-04-01T12:00:00Z")})
        UpdateVersions()
        VersionCards.Children.OfType(Of PclCard)().Single(Function(c) c.Title.StartsWith("愚人节版 (", StringComparison.Ordinal)).Collapsed = False
        If selectInfinite Then SelectVersion(_versions.Single(Function(v) v.Id = "20w14infinite"))
    End Sub

    Private Function BrowserRows(title As String) As List(Of PclListItem)
        Dim card = VersionCards.Children.OfType(Of PclCard)().SingleOrDefault(Function(c) c.Title = title OrElse c.Title.StartsWith(title & " (", StringComparison.Ordinal))
        If card Is Nothing Then Return New List(Of PclListItem)
        card.Collapsed = False
        ' Classification assertions deliberately inspect all entries. Viewport loading is
        ' checked separately against the untouched production list in VerifyLongVersionLists.
        For Each pending In DirectCast(card.Body, StackPanel).Children.OfType(Of PclLazyRow)().ToList()
            pending.Materialize()
        Next
        Return DirectCast(card.Body, StackPanel).Children.OfType(Of PclListItem)().ToList()
    End Function

    Private Shared Function BrowserEntry(id As String, kind As String, time As String) As VersionEntry
        Return New VersionEntry With {.Id = id, .Kind = kind, .Released = DateTimeOffset.Parse(time, Globalization.CultureInfo.InvariantCulture), .Url = "https://example.invalid/" & id & ".json", .Sha1 = New String("a"c, 40)}
    End Function

    Private Sub VerifyVersionBrowser(check As Action(Of Boolean, String))
        Dim saved = _versions
        Try
            Dim older = BrowserEntry("1.20.1", "release", "2023-06-12T12:00:00Z")
            Dim newer = BrowserEntry("1.21.1", "release", "2024-08-08T12:00:00Z")
            Dim snapshot = BrowserEntry("24w33a", "snapshot", "2024-08-14T12:00:00Z")
            Dim corrected = BrowserEntry("1.2.3", "snapshot", "2012-03-02T12:00:00Z")
            Dim special = BrowserEntry("custom-special", "special", "2020-02-02T12:00:00Z")
            _versions = New List(Of VersionEntry) From {older, BrowserEntry("24w32a", "snapshot", "2024-08-07T12:00:00Z"), special, corrected, newer, snapshot, BrowserEntry("b1.7.3", "old_beta", "2011-04-01T12:00:00Z")}
            UpdateVersions()
            Dim latest = BrowserRows("最新版本")
            check(latest.Count = 2 AndAlso latest(0).Tag Is newer AndAlso latest(1).Tag Is snapshot, "latest recommendations sort release times before choosing release and snapshot")
            check(BrowserRows("正式版").Select(Function(r) r.Title).SequenceEqual({"1.21.1", "1.20.1", "1.2.3"}), "PCL corrects plain 1.x snapshots into sorted release rows")
            check(BrowserRows("远古版").Count = 1 AndAlso BrowserRows("愚人节版").Single().Tag Is special, "special entries belong only to the April Fools category and old versions keep their category")
            check(BrowserRows("正式版").Last().Logo.EndsWith("/Grass.png", StringComparison.Ordinal) AndAlso BrowserRows("预览版").First().Logo.EndsWith("/CommandBlock.png", StringComparison.Ordinal), "corrected releases and ordinary previews use PCL category icons")
            check(BrowserRows("正式版").First().Info = newer.Released.ToString("yyyy/MM/dd HH:mm") AndAlso latest(0).Info.StartsWith("最新正式版，发布于 ", StringComparison.Ordinal) AndAlso latest(1).Info.StartsWith("最新预览版，发布于 ", StringComparison.Ordinal), "ordinary version rows show PCL dates while recommended rows explain their latest status")
            check(corrected.Kind = "snapshot" AndAlso _versions(0) Is older, "presentation correction and sorting do not mutate manifest kind or input order")
            BrowserRows("正式版").Last().Activate()
            check(_selectedEntry Is corrected AndAlso _selectedEntry.Kind = "snapshot" AndAlso DirectCast(SelectedVersionIcon.Source, System.Windows.Media.Imaging.BitmapImage).UriSource.OriginalString.EndsWith("/Grass.png", StringComparison.Ordinal), "selecting a corrected release retains raw metadata and uses the corrected grass icon")

            Dim knownIds = {"20w14infinite", "20w14∞", "3D Shareware v1.34", "1.RV-Pre1", "15w14a", "2.0", "22w13oneblockatatime", "23w13a_or_b", "24w14potato", "25w14craftmine", "26w14a"}
            _versions = knownIds.Select(Function(id, i) BrowserEntry(id, "snapshot", (2000 + i).ToString() & "-03-29T12:00:00Z")).ToList()
            Dim identities = _versions.Select(Function(v) v.Id & "|" & v.Kind & "|" & v.Url & "|" & v.Sha1 & "|" & v.Released.ToString("O")).ToArray()
            UpdateVersions()
            Dim fools = BrowserRows("愚人节版")
            check(VersionCards.Children.Count = 1 AndAlso fools.Count = knownIds.Length, "all fixed PCL April Fools identifiers are recognized even outside April 1")
            check(fools.Select(Function(r) DirectCast(r.Tag, VersionEntry).Released).SequenceEqual(_versions.OrderByDescending(Function(v) v.Released).Select(Function(v) v.Released)), "April Fools entries are sorted newest first")
            check(fools.All(Function(r) r.Logo.EndsWith("/GoldBlock.png", StringComparison.Ordinal) AndAlso r.Info.Contains(" | ")), "all named April Fools rows use gold icons and the fixed PCL descriptions")
            check(fools.Single(Function(r) DirectCast(r.Tag, VersionEntry).Id = "24w14potato").Info = "2024 | 毒马铃薯一直都被大家忽视和低估，于是我们超级加强了它！", "the potato update description matches PCL source text")
            Dim infinite = fools.Single(Function(r) DirectCast(r.Tag, VersionEntry).Id = "20w14infinite")
            check(infinite.Title = "20w14∞", "infinite update uses PCL's infinity display alias")
            PointerEvent(infinite, Mouse.MouseDownEvent) : PointerEvent(infinite, Mouse.MouseUpEvent)
            check(_selectedEntry Is infinite.Tag AndAlso _selectedEntry.Id = "20w14infinite" AndAlso _selectedEntry.Url.EndsWith("/20w14infinite.json", StringComparison.Ordinal), "clicking the infinity alias retains the original download identity and URL")
            check(SelectedVersionText.Text = "20w14∞" AndAlso InstanceName.Text = "20w14∞", "the selected version and automatic instance name keep PCL's infinity display alias")
            UpdateInstanceName()
            check(InstanceName.Text = "20w14∞", "automatic component-name refresh cannot revert the infinity display alias to its wire identifier")
            check(DirectCast(SelectedVersionIcon.Source, System.Windows.Media.Imaging.BitmapImage).UriSource.OriginalString.EndsWith("/GoldBlock.png", StringComparison.Ordinal), "the selected April Fools version keeps its gold icon")
            check(DirectCast(VanillaCard.HeaderContent, StackPanel).Children.OfType(Of Image)().Single().Source.ToString().EndsWith("/GoldBlock.png", StringComparison.Ordinal), "the Minecraft summary uses the selected base version icon instead of a fixed grass block")
            SelectComponent(_componentEntries(ComponentKind.Fabric)(0))
            check(_componentSelection.Has(ComponentKind.Fabric) AndAlso SelectedVersionIcon.Source.ToString().EndsWith("/Fabric.png", StringComparison.Ordinal) AndAlso VanillaVersionIcon.Source.ToString().EndsWith("/GoldBlock.png", StringComparison.Ordinal), "adding Fabric changes the instance icon while preserving the selected base game's gold summary icon")
            UpdateVersions()
            check(_versions.Select(Function(v) v.Id & "|" & v.Kind & "|" & v.Url & "|" & v.Sha1 & "|" & v.Released.ToString("O")).SequenceEqual(identities), "opening and rebuilding the version browser preserves every original manifest declaration")

            _versions = New List(Of VersionEntry) From {
                BrowserEntry("boundary-in", "snapshot", "2024-03-31T22:30:00Z"),
                BrowserEntry("boundary-out", "snapshot", "2024-04-01T22:30:00Z"),
                BrowserEntry("same-instant", "snapshot", "2024-04-01T06:30:00+08:00"),
                BrowserEntry("1.21-pre1", "snapshot", "2024-06-01T12:00:00Z"),
                BrowserEntry("1.21-rc1", "snapshot", "2024-06-02T12:00:00Z"),
                BrowserEntry("1.14_combat-1", "snapshot", "2024-06-03T12:00:00Z"),
                BrowserEntry("1.18_experimental-1", "snapshot", "2024-06-04T12:00:00Z"),
                BrowserEntry("1.99", "release", "2024-04-01T12:00:00Z")}
            UpdateVersions()
            check(BrowserRows("愚人节版").Select(Function(r) r.Title).OrderBy(Function(id) id).SequenceEqual({"boundary-in", "same-instant"}), "automatic April Fools classification uses PCL UTC plus two hours rather than the manifest offset date")
            check(BrowserRows("预览版").Any(Function(r) r.Title = "boundary-out"), "April 2 in PCL's time zone remains a preview even if its UTC date is April 1")
            check(BrowserRows("正式版").Single().Title = "1.99" AndAlso BrowserRows("预览版").Count = 5, "pre rc combat and experimental snapshots stay previews and explicit releases stay releases")

            _versions = New List(Of VersionEntry) From {newer, BrowserEntry("old-preview", "snapshot", "2024-07-01T12:00:00Z")}
            UpdateVersions()
            check(BrowserRows("最新版本").Single().Tag Is newer, "a preview older than the latest release is not recommended")
            _versions(1).Released = newer.Released
            UpdateVersions()
            check(BrowserRows("最新版本").Count = 1, "a preview tied with the latest release is not recommended")
            _versions = New List(Of VersionEntry) From {snapshot}
            UpdateVersions()
            check(BrowserRows("最新版本").Single().Tag Is snapshot AndAlso BrowserRows("正式版").Count = 0, "a snapshot-only catalogue remains usable and recommends its newest preview")
            _versions = New List(Of VersionEntry) From {newer}
            UpdateVersions()
            check(BrowserRows("最新版本").Single().Tag Is newer AndAlso BrowserRows("预览版").Count = 0, "a release-only catalogue remains usable without a preview category")
            _versions = New List(Of VersionEntry)
            UpdateVersions()
            check(VersionCards.Children.Count = 0, "an empty catalogue leaves no stale recommendations or version rows")
        Finally
            _versions = saved
            BackToVersions(Nothing, Nothing)
            UpdateVersions()
        End Try
    End Sub
End Class
