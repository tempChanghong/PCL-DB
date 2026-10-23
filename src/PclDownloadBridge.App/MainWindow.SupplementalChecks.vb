Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    ' Controlled dispatcher completions exercise production catalogue/selection paths.
    Private Async Function VerifySupplementalRefreshAsync(directory As String) As Task(Of Boolean)
        Dim savedVersions = _versions, savedProvider = _versionProvider, savedExtra = _supplementalProvider, savedComponents = _componentProvider
        Dim savedSelection = _selectedEntry
        Dim results As New JObject()
        Dim normal = BrowserEntry("1.21.1", "release", "2024-08-08T12:00:00Z")
        Dim blue As New VersionEntry With {.Id = "2.0_blue", .SupplementalId = "2point0_blue", .Kind = "snapshot", .Released = DateTimeOffset.Parse("2035-04-01T12:00:00Z")}
        Dim red As New VersionEntry With {.Id = "2.0_red", .SupplementalId = "2point0_red", .Kind = "snapshot", .Released = blue.Released}
        Try
            _versionProvider = Function(policy, token) Task.FromResult(New List(Of VersionEntry) From {normal})
            Dim pending = ListGate(Of VersionEntry)()
            _supplementalProvider = Function(policy, token) pending.Task
            Dim loading = LoadVersionsAsync(True)
            Dim originalRow = BrowserRows("正式版").Single()
            results("normalReadyBeforeSupplement") = Not _loading AndAlso Not loading.IsCompleted AndAlso BrowserRows("最新版本").Single().Tag Is normal
            pending.SetResult(New List(Of VersionEntry) From {blue, red})
            Await loading
            results("normalRowRetained") = BrowserRows("正式版").Single() Is originalRow
            Dim blueRow = BrowserRows("特供版本").First()
            results("categoriesAndLatest") = blueRow.Tag Is blue AndAlso blueRow.Info.Contains("UVMC") AndAlso BrowserRows("特供版本").Any(Function(row) row.Tag Is red) AndAlso
                BrowserRows("愚人节版").Count = 0 AndAlso BrowserRows("远古版").Count = 0 AndAlso BrowserRows("预览版").Count = 0 AndAlso BrowserRows("最新版本").Single().Tag Is normal
            AddSupplementalVersionCards()
            results("appendIdempotent") = VersionCards.Children.OfType(Of PclCard)().Count() = 3
            Dim componentCalls = 0
            _componentProvider = Function(policy, kind, mc, token)
                                     componentCalls += 1
                                     Return Task.FromResult(New List(Of ComponentEntry)())
                                 End Function
            blueRow.Activate()
            SelectComponent(New ComponentEntry With {.Kind = ComponentKind.Fabric, .Minecraft = blue.Id, .Version = "fixture"})
            Await RefreshComponentListsAsync()
            results("canonicalVanillaSelection") = _selectedEntry Is blue AndAlso InstanceName.Text = SupplementalVersions.DisplayName(blue) AndAlso SelectedVersionText.Text = SupplementalVersions.DisplayName(blue) AndAlso ComponentHint.Text.Contains("UVMC") AndAlso
                componentCalls = 0 AndAlso _componentRequests.Count = 0 AndAlso _componentSelection.Entries.Count = 0 AndAlso _componentViews.Values.All(Function(view) view.Card.Visibility = Windows.Visibility.Collapsed)
            SetDownloadView(DownloadView.Versions)

            Dim older = ListGate(Of VersionEntry)(), current = ListGate(Of VersionEntry)(), requests = 0
            _supplementalProvider = Function(policy, token)
                                        requests += 1
                                        Return If(requests = 1, older.Task, current.Task)
                                    End Function
            Dim oldWork = LoadVersionsAsync(True), newWork = LoadVersionsAsync(True)
            current.SetResult(New List(Of VersionEntry) From {red}) : Await newWork
            older.SetResult(New List(Of VersionEntry) From {blue}) : Await oldWork
            results("staleSupplementIgnored") = _versions.Count = 2 AndAlso _versions.Contains(red) AndAlso Not _versions.Contains(blue) AndAlso BrowserRows("特供版本").All(Function(row) row.Tag IsNot blue)

            pending = ListGate(Of VersionEntry)()
            _supplementalProvider = Function(policy, token) pending.Task
            loading = LoadVersionsAsync(True)
            Dim before = _versions
            _busy = True : StatusText.Text = "正在安装测试实例"
            pending.SetResult(New List(Of VersionEntry) From {blue}) : Await loading
            results("busySupplementIgnored") = _versions Is before AndAlso _versions.Count = 1 AndAlso StatusText.Text = "正在安装测试实例"
            _busy = False

            pending = ListGate(Of VersionEntry)()
            loading = LoadVersionsAsync(True) : before = _versions
            _closing = True
            pending.SetResult(New List(Of VersionEntry) From {blue}) : Await loading
            results("closingSupplementIgnored") = _versions Is before AndAlso _versions.Count = 1 AndAlso _versionCancellation Is Nothing
            _closing = False

            _supplementalProvider = Function(policy, token) Task.FromResult(New List(Of VersionEntry)())
            Await LoadVersionsAsync(True)
            results("optionalFailureKeepsNormal") = _versions.Count = 1 AndAlso _versions(0) Is normal AndAlso Not _versionsDirty AndAlso Not _loading AndAlso StatusText.Text.Contains("补充清单暂不可用") AndAlso BrowserRows("正式版").Single().Tag Is normal
            _supplementalProvider = Function(policy, token) Task.FromCanceled(Of List(Of VersionEntry))(New CancellationToken(True))
            Await LoadVersionsAsync(True)
            results("optionalCancellationKeepsNormal") = _versions.Count = 1 AndAlso _versions(0) Is normal AndAlso Not _versionsDirty AndAlso Not _loading AndAlso Not StatusText.Text.Contains("刷新失败")
            _supplementalProvider = Function(policy, token) Task.FromException(Of List(Of VersionEntry))(New InvalidOperationException("optional response decoding failed"))
            Await LoadVersionsAsync(True)
            results("optionalExceptionKeepsNormal") = _versions.Count = 1 AndAlso _versions(0) Is normal AndAlso Not _versionsDirty AndAlso Not _loading AndAlso Not StatusText.Text.Contains("刷新失败") AndAlso BrowserRows("正式版").Single().Tag Is normal
            _supplementalProvider = Function(policy, token) Task.FromResult(New List(Of VersionEntry) From {red})
            Await LoadVersionsAsync(True)
            results("optionalExceptionThenRetry") = Not _versionsDirty AndAlso _versions.Count = 2 AndAlso _versions.Contains(red) AndAlso BrowserRows("特供版本").First().Tag Is red
            results("statusReportsActualCategory") = StatusText.Text.Contains("含 1 个补充版本：愚人节版") AndAlso Not StatusText.Text.Contains("战斗测试版")
            Dim officialBlue = BrowserEntry(blue.Id, "snapshot", "2020-08-26T12:00:00Z")
            _versionProvider = Function(policy, token) Task.FromResult(New List(Of VersionEntry) From {normal, officialBlue})
            _supplementalProvider = Function(policy, token) Task.FromResult(New List(Of VersionEntry) From {blue})
            Await LoadVersionsAsync(True)
            results("statusReportsCollisionWithoutAddition") = _versions.Count = 2 AndAlso _versions.Contains(officialBlue) AndAlso Not _versions.Contains(blue) AndAlso
                BrowserRows("特供版本").All(Function(row) row.Tag IsNot blue) AndAlso StatusText.Text.Contains("无新增补充版本")
            _versionProvider = Function(policy, token) Task.FromResult(New List(Of VersionEntry) From {normal})
            pending = ListGate(Of VersionEntry)()
            _supplementalProvider = Function(policy, token) pending.Task
            loading = LoadVersionsAsync(True) : before = _versions
            _busy = True : StatusText.Text = "正在安装测试实例"
            pending.SetException(New InvalidOperationException("late optional failure while installing")) : Await loading
            results("optionalBusyExceptionKeepsNormal") = _versions Is before AndAlso Not _versionsDirty AndAlso StatusText.Text = "正在安装测试实例"
            _busy = False
            pending = ListGate(Of VersionEntry)()
            loading = LoadVersionsAsync(True) : before = _versions
            _closing = True
            pending.SetException(New InvalidOperationException("late optional failure while closing")) : Await loading
            results("optionalClosingExceptionKeepsNormal") = _versions Is before AndAlso Not _versionsDirty AndAlso _versionCancellation Is Nothing
            _closing = False
            older = ListGate(Of VersionEntry)() : current = ListGate(Of VersionEntry)() : requests = 0
            _supplementalProvider = Function(policy, token)
                                        requests += 1
                                        Return If(requests = 1, older.Task, current.Task)
                                    End Function
            oldWork = LoadVersionsAsync(True) : newWork = LoadVersionsAsync(True)
            current.SetResult(New List(Of VersionEntry) From {red}) : Await newWork
            before = _versions
            Dim acceptedStatus = StatusText.Text
            older.SetException(New InvalidOperationException("stale optional failure")) : Await oldWork
            results("optionalStaleExceptionKeepsNormal") = _versions Is before AndAlso _versions.Contains(red) AndAlso Not _versionsDirty AndAlso StatusText.Text = acceptedStatus
            Dim rd = BrowserEntry("rd-132211", "old_alpha", "2009-05-13T20:11:00Z")
            Dim fool = BrowserEntry("15w14a", "snapshot", "2015-04-01T12:00:00Z")
            _versionProvider = Function(policy, token) Task.FromResult(New List(Of VersionEntry) From {normal, rd, fool})
            _supplementalProvider = Function(policy, token) Task.FromResult(New List(Of VersionEntry) From {blue, red})
            Await LoadVersionsAsync(True)
            results("officialRdAndFoolsRetained") = BrowserRows("远古版").Single().Tag Is rd AndAlso Not rd.IsSupplemental AndAlso BrowserRows("愚人节版").Single().Tag Is fool AndAlso
                BrowserRows("特供版本").Count = 2 AndAlso BrowserRows("特供版本").First().Title.Contains("2.0（蓝色版）") AndAlso BrowserRows("战斗测试版").Count = 0 AndAlso BrowserRows("实验快照").Count = 0
            Dim history As New List(Of VersionEntry)
            For Each id In {"c0.0.12a_03", "a1.1.1", "b1.9-pre6", "1.0.0-rc1", "inf-20100630-1"}.Concat(SupplementalVersions.HistoricalSnapshotIds)
                history.Add(New VersionEntry With {.Id = id, .SupplementalId = id, .Kind = If(SupplementalVersions.HistoricalSnapshotIds.Contains(id), "snapshot", If(id = "b1.9-pre6" OrElse id = "1.0.0-rc1", "old_beta", "old_alpha")), .Released = blue.Released})
            Next
            Dim oldSnapshot = history.Single(Function(entry) entry.Id = "13w12~")
            pending = ListGate(Of VersionEntry)()
            _supplementalProvider = Function(policy, token) pending.Task
            loading = LoadVersionsAsync(True)
            originalRow = BrowserRows("正式版").Single()
            pending.SetResult(history.Concat({blue, red}).ToList()) : Await loading
            results("historyDoesNotReplaceNormalOrLatest") = BrowserRows("正式版").Single() Is originalRow AndAlso BrowserRows("最新版本").Single().Tag Is normal AndAlso BrowserRows("远古版").Single().Tag Is rd
            AddSupplementalVersionCards()
            results("historyCategoriesAndIcons") = BrowserRows("远古版本补充").Count = 5 AndAlso BrowserRows("历史快照补充").Count = 36 AndAlso BrowserRows("历史快照补充").Any(Function(row) row.Tag Is oldSnapshot) AndAlso
                BrowserRows("特供版本").Count = 2 AndAlso New VersionPresentation(history(0)).Icon.Contains("CobbleStone") AndAlso New VersionPresentation(oldSnapshot).Icon.Contains("CommandBlock") AndAlso
                VersionCards.Children.OfType(Of PclCard)().Count() = 7 AndAlso StatusText.Text.Contains("远古版") AndAlso StatusText.Text.Contains("旧快照")
            For Each item In history
                Dim row = BrowserRows(New VersionPresentation(item).Category).Single(Function(value) value.Tag Is item)
                row.Activate() : Await RefreshComponentListsAsync()
                If _selectedEntry IsNot item OrElse InstanceName.Text <> item.Id OrElse _componentRequests.Count <> 0 OrElse _componentSelection.Entries.Count <> 0 OrElse Not ComponentHint.Text.Contains(SupplementalVersions.Label(item)) Then Throw New InvalidOperationException("Historical vanilla selection lost its identity or requested components.")
                SetDownloadView(DownloadView.Versions)
            Next
            results("historyVanillaSelection") = componentCalls = 0
            If directory IsNot Nothing Then IO.File.WriteAllText(IO.Path.Combine(directory, "supplemental-workflow.json"), results.ToString())
            Return results.Properties().All(Function(item) CBool(item.Value))
        Finally
            _busy = False : _closing = False
            _versionProvider = savedProvider : _supplementalProvider = savedExtra : _componentProvider = savedComponents
            _versions = savedVersions
            If savedSelection IsNot Nothing Then SelectVersion(savedSelection)
            SetDownloadView(DownloadView.Versions) : UpdateVersions()
        End Try
    End Function
End Class
