Imports System.IO
Imports System.Windows.Controls
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Async Function VerifyPackReleaseLabelsAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim first = _packReleases(1).Copy(), second = first.Copy()
        check(Not PackReleaseInfo(first, PackReleaseFallbackTitle(first)).Contains("更新于 " & first.Published.ToLocalTime().ToString("yyyy/MM/dd")),
            "release rows use PCL relative update text rather than a fixed calendar date")
        first.Id = "same-forge" : first.Name = "同名整合包 2.3" : first.Version = "2.3" : first.FileName = "pack-forge-2.3.mrpack" : first.Loaders = {"forge"}
        second.Id = "same-fabric" : second.Name = first.Name : second.Version = "2.3" : second.FileName = "pack-fabric-2.3.mrpack" : second.Loaders = {"fabric"} : second.ReleaseType = "beta"
        first.Downloads = 1234567 : second.Downloads = 88715
        first.Published = DateTimeOffset.UtcNow.AddDays(-2).AddHours(-13) : second.Published = first.Published
        _packReleases = New List(Of ModpackRelease) From {first, second}
        InitializePackVersions() : layout()
        Dim card = DirectCast(PackVersionCards.Children(0), PclCard), rows = DirectCast(card.Body, StackPanel)
        Dim firstRow = DirectCast(rows.Children(0), PclListItem), secondRow = DirectCast(rows.Children(1), PclListItem)
        check(firstRow.Title = "pack-forge-2.3" AndAlso secondRow.Title = "pack-fabric-2.3", "same-name releases use PCL's filename fallback without stripping dotted version numbers")
        check(firstRow.Height = 42 AndAlso secondRow.Height = 42 AndAlso firstRow.Info.StartsWith("Forge", StringComparison.Ordinal) AndAlso Not firstRow.Info.Contains("release") AndAlso secondRow.Info.StartsWith("Fabric", StringComparison.Ordinal) AndAlso secondRow.Info.Contains("更新于") AndAlso secondRow.Info.Contains("测试版"), "release information keeps PCL's compact rows, canonical loader labels and localized update and preview text")
        Dim tip = secondRow.ToolTip.ToString()
        check(firstRow.Info.Contains("下载 123 万次") AndAlso secondRow.Info.Contains("下载 88715 次") AndAlso secondRow.Info.Contains("更新于 2 天 13 小时前"),
            "release rows render PCL platform download counts and detailed relative update times")
        check(tip.Contains("平台下载次数：88,715 次") AndAlso tip.Contains(second.Published.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss")),
            "release tooltips retain exact platform counts and absolute local update timestamps")
        check(tip.Contains(second.Name) AndAlso tip.Contains(second.FileName) AndAlso tip.Contains("版本号：2.3") AndAlso tip.Contains("1.20.1") AndAlso tip.Contains("归档大小：") AndAlso tip.Contains(second.Id) AndAlso Not tip.Contains(second.Url), "full release tooltips expose the file, version, MC, size and ID without copying download URLs")
        SaveControlVisual(host, Path.Combine(directory, "pack-search-release-labels.png"))
        Dim appearance = _appearance.ToJson()
        Try
            RestoreAppearance(New Newtonsoft.Json.Linq.JObject From {{"theme", "blue"}, {"darkMode", True}}) : layout()
            SaveControlVisual(host, Path.Combine(directory, "pack-search-release-labels-dark.png"))
        Finally
            RestoreAppearance(appearance)
        End Try
        Dim countProbe = first.Copy(), now As New DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero)
        countProbe.Downloads = Nothing : countProbe.Published = now.AddMinutes(-2).AddSeconds(-3)
        check(Not PackReleaseInfo(countProbe, countProbe.Name, now).Contains("下载 ") AndAlso Not PackReleaseToolTip(countProbe).Contains("平台下载次数"),
            "unknown release counters stay absent from both compact labels and exact tooltips")
        countProbe.Downloads = 0 : countProbe.Platform = ModpackPlatform.CurseForge
        check(Not PackReleaseInfo(countProbe, countProbe.Name, now).Contains("下载 0") AndAlso Not PackReleaseToolTip(countProbe).Contains("平台下载次数"),
            "a zero CurseForge counter does not imply that the selected release has never been downloaded")
        check(PackReleaseInfo(countProbe, countProbe.Name, now).Contains("更新于 2 分 3 秒前"), "release relative-time rendering accepts a fixed instant independently of wall-clock execution")
        countProbe.Published = now.AddHours(2).AddMinutes(3)
        check(PackReleaseInfo(countProbe, countProbe.Name, now).Contains("更新于 2 小时 3 分钟后"), "future platform dates use the PCL future suffix without negative elapsed values")
        check(firstRow.Tag Is first AndAlso secondRow.Tag Is second AndAlso card.Body Is rows AndAlso firstRow.Height = 42 AndAlso secondRow.Height = 42,
            "release statistics preserve existing row identity, card ownership and fixed installation hit geometry")

        Dim unique = first.Copy() : unique.Name = " 独立名称 2.3.MRPACK " : unique.FileName = "different-name.zip"
        Dim uniqueRow = DirectCast(DirectCast(BuildPackVersionRows(New List(Of ModpackRelease) From {unique}), StackPanel).Children(0), PclListItem)
        check(uniqueRow.Title = "独立名称 2.3" AndAlso uniqueRow.Info.Contains("different-name"), "unique display names retain dotted versions while the secondary line identifies a different archive filename")
        unique.Name = ".mrpack"
        check(PackReleaseTitles(New List(Of ModpackRelease) From {unique})(0) = "different-name", "an empty normalized display name falls back to a usable archive title")
        unique.GameVersions = {"24w14a", "1.21-pre1"} : unique.ReleaseType = "alpha"
        uniqueRow = DirectCast(DirectCast(BuildPackVersionRows(New List(Of ModpackRelease) From {unique}), StackPanel).Children(0), PclListItem)
        check(uniqueRow.Info.Contains("游戏版本 24w14a、1.21-pre1") AndAlso uniqueRow.Info.Contains("早期测试版"), "snapshot and prerelease rows identify their exact MC targets within grouped cards and use PCL's alpha label")
        Dim alternate = first.Copy() : alternate.Id = "alternate" : alternate.Name = first.Name & " .ZIP " : alternate.FileName = first.FileName : alternate.Version = "2.4"
        Dim labels = PackReleaseTitles(New List(Of ModpackRelease) From {first, alternate})
        check(labels(0).Contains("2.3") AndAlso labels(1).EndsWith("2.4", StringComparison.Ordinal) AndAlso labels(0) <> labels(1), "colliding normalized names and archive names remain distinguishable by their declared version numbers")
        alternate.Version = first.Version
        labels = PackReleaseTitles(New List(Of ModpackRelease) From {first, alternate})
        check(labels(0).Contains(first.Id) AndAlso labels(1).Contains(alternate.Id) AndAlso labels(0) <> labels(1), "platform release IDs distinguish reused names, filenames and version numbers")
        Dim generated = first.Copy() : generated.Id = "generated" : generated.FileName = labels(1) & ".mrpack"
        labels = PackReleaseTitles(New List(Of ModpackRelease) From {first, alternate, generated})
        check(labels.Distinct(StringComparer.OrdinalIgnoreCase).Count() = 3, "a generated fallback label cannot collide with another release's real filename")

        Dim generation = _taskGeneration, calls = 0, selected As ModpackRelease = Nothing, chosenName As String = Nothing, chosenRoot As String = Nothing
        Dim installGate As New TaskCompletionSource(Of InstallResult)(TaskCreationOptions.RunContinuationsAsynchronously)
        _onlineInstallRunner = Function(release, root, name, progress, token)
                                   calls += 1 : selected = release : chosenName = name : chosenRoot = root
                                   Return installGate.Task
                               End Function
        firstRow.Activate() : layout()
        check(_onlinePrompt IsNot Nothing AndAlso PackNameDescription.Text.Contains(firstRow.Title) AndAlso PackNameDescription.Text.Contains(first.FileName) AndAlso _taskGeneration = generation, "selecting the Forge alternative confirms its visible label and real file before creating a download task")
        CancelOnlinePackName(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _choosingOnline)
        secondRow.Activate() : layout()
        check(_onlinePrompt IsNot Nothing AndAlso PackNameDescription.Text.Contains(secondRow.Title) AndAlso PackNameDescription.Text.Contains(second.FileName) AndAlso Not PackNameDescription.Text.Contains(first.FileName), "cancelling and selecting a same-name Fabric alternative replaces the confirmed file identity")
        OnlinePackName.Text = "Distinct-Fabric-Instance" : layout()
        SaveControlVisual(host, Path.Combine(directory, "pack-search-release-confirmation.png"))
        Dim frozen = second.Copy()
        second.Id = "changed-after-prompt" : second.FileName = "changed-after-prompt.mrpack" : second.Loaders(0) = "quilt" : second.GameVersions(0) = "1.21.1" : second.Downloads = 1
        ConfirmOnlinePackName(Nothing, Nothing)
        Await WaitPackUiAsync(Function() _busy AndAlso selected IsNot Nothing) : layout()
        check(calls = 1 AndAlso selected IsNot second AndAlso selected.Id = frozen.Id AndAlso selected.FileName = frozen.FileName AndAlso selected.Loaders.SequenceEqual(frozen.Loaders) AndAlso selected.GameVersions.SequenceEqual(frozen.GameVersions), "confirmation downloads the exact copied release despite mutations to the shared catalogue object")
        check(selected.Downloads.Equals(frozen.Downloads) AndAlso selected.Published = frozen.Published, "installation confirmation retains the frozen release statistics without changing the selected archive")
        check(chosenName = "Distinct-Fabric-Instance" AndAlso chosenRoot = _selectedFolder.Location AndAlso _onlineLastRelease = _packProject.Key & ":" & frozen.Id, "instance naming and retry ownership use the frozen platform ID rather than the duplicate name or a mutated catalogue ID")
        check(_taskComponents.Contains(secondRow.Title) AndAlso _taskComponents.Contains(frozen.FileName) AndAlso Not _taskComponents.Contains("changed-after-prompt"), "the progress task retains the same selected label and archive filename as the confirmation")
        second.Id = frozen.Id : second.FileName = frozen.FileName : second.Loaders = frozen.Loaders : second.GameVersions = frozen.GameVersions : second.Downloads = frozen.Downloads
        installGate.SetResult(New InstallResult With {.InstancePath = Path.Combine(chosenRoot, "versions", chosenName), .FileCount = 10})
        Await WaitPackUiAsync(Function() Not _busy AndAlso Not _choosingOnline)
        ResumeInstallation(Nothing, Nothing) : layout()
        check(PackVersionCards.Children(0) Is card AndAlso card.Body Is rows AndAlso firstRow.Title = "pack-forge-2.3" AndAlso secondRow.Title = "pack-fabric-2.3", "returning from installation reuses the same card, row instances and disambiguated labels")
        generation = _taskGeneration
        firstRow.Activate() : layout()
        check(OnlinePackName.Text = SuggestedPackName(PackProjectTitle.Text), "a different same-name release does not inherit the Fabric instance's chosen name")
        CancelOnlinePackName(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _choosingOnline)
        secondRow.Activate() : layout()
        check(OnlinePackName.Text = "Distinct-Fabric-Instance" AndAlso _taskGeneration = generation AndAlso calls = 1, "reselecting the confirmed release retains its chosen name without starting another task before confirmation")
        CancelOnlinePackName(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _choosingOnline) : layout()
    End Function
End Class
