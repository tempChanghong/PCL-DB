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
        Dim combat As New VersionEntry With {.Id = "1.16_combat-6", .SupplementalId = "1_16_combat-6", .Kind = "pending", .Released = DateTimeOffset.Parse("2035-04-01T12:00:00Z")}
        Dim experiment As New VersionEntry With {.Id = "1.18_experimental-snapshot-7", .SupplementalId = "1.18_experimental-snapshot-7", .Kind = "pending", .Released = combat.Released}
        Try
            _versionProvider = Function(policy, token) Task.FromResult(New List(Of VersionEntry) From {normal})
            Dim pending = ListGate(Of VersionEntry)()
            _supplementalProvider = Function(policy, token) pending.Task
            Dim loading = LoadVersionsAsync(True)
            Dim originalRow = BrowserRows("正式版").Single()
            results("normalReadyBeforeSupplement") = Not _loading AndAlso Not loading.IsCompleted AndAlso BrowserRows("最新版本").Single().Tag Is normal
            pending.SetResult(New List(Of VersionEntry) From {combat, experiment})
            Await loading
            results("normalRowRetained") = BrowserRows("正式版").Single() Is originalRow
            Dim battleRow = BrowserRows("战斗测试版").Single()
            results("categoriesAndLatest") = battleRow.Tag Is combat AndAlso battleRow.Info.Contains("UVMC") AndAlso BrowserRows("实验快照").Single().Tag Is experiment AndAlso
                BrowserRows("愚人节版").Count = 0 AndAlso BrowserRows("远古版").Count = 0 AndAlso BrowserRows("预览版").Count = 0 AndAlso BrowserRows("最新版本").Single().Tag Is normal
            AddSupplementalVersionCards()
            results("appendIdempotent") = VersionCards.Children.OfType(Of PclCard)().Count() = 4
            Dim componentCalls = 0
            _componentProvider = Function(policy, kind, mc, token)
                                     componentCalls += 1
                                     Return Task.FromResult(New List(Of ComponentEntry)())
                                 End Function
            battleRow.Activate()
            SelectComponent(New ComponentEntry With {.Kind = ComponentKind.Fabric, .Minecraft = combat.Id, .Version = "fixture"})
            Await RefreshComponentListsAsync()
            results("canonicalVanillaSelection") = _selectedEntry Is combat AndAlso InstanceName.Text = combat.Id AndAlso SelectedVersionText.Text = combat.Id AndAlso ComponentHint.Text.Contains("UVMC") AndAlso
                componentCalls = 0 AndAlso _componentRequests.Count = 0 AndAlso _componentSelection.Entries.Count = 0 AndAlso _componentViews.Values.All(Function(view) view.Card.Visibility = Windows.Visibility.Collapsed)
            SetDownloadView(DownloadView.Versions)

            Dim older = ListGate(Of VersionEntry)(), current = ListGate(Of VersionEntry)(), requests = 0
            _supplementalProvider = Function(policy, token)
                                        requests += 1
                                        Return If(requests = 1, older.Task, current.Task)
                                    End Function
            Dim oldWork = LoadVersionsAsync(True), newWork = LoadVersionsAsync(True)
            current.SetResult(New List(Of VersionEntry) From {experiment}) : Await newWork
            older.SetResult(New List(Of VersionEntry) From {combat}) : Await oldWork
            results("staleSupplementIgnored") = _versions.Count = 2 AndAlso _versions.Contains(experiment) AndAlso Not _versions.Contains(combat) AndAlso BrowserRows("战斗测试版").Count = 0

            pending = ListGate(Of VersionEntry)()
            _supplementalProvider = Function(policy, token) pending.Task
            loading = LoadVersionsAsync(True)
            Dim before = _versions
            _busy = True : StatusText.Text = "正在安装测试实例"
            pending.SetResult(New List(Of VersionEntry) From {combat}) : Await loading
            results("busySupplementIgnored") = _versions Is before AndAlso _versions.Count = 1 AndAlso StatusText.Text = "正在安装测试实例"
            _busy = False

            pending = ListGate(Of VersionEntry)()
            loading = LoadVersionsAsync(True) : before = _versions
            _closing = True
            pending.SetResult(New List(Of VersionEntry) From {combat}) : Await loading
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
            _supplementalProvider = Function(policy, token) Task.FromResult(New List(Of VersionEntry) From {experiment})
            Await LoadVersionsAsync(True)
            results("optionalExceptionThenRetry") = Not _versionsDirty AndAlso _versions.Count = 2 AndAlso _versions.Contains(experiment) AndAlso BrowserRows("实验快照").Single().Tag Is experiment
            results("statusReportsActualCategory") = StatusText.Text.Contains("含 1 个补充版本：实验快照") AndAlso Not StatusText.Text.Contains("战斗测试版")
            Dim officialCombat = BrowserEntry(combat.Id, "snapshot", "2020-08-26T12:00:00Z")
            _versionProvider = Function(policy, token) Task.FromResult(New List(Of VersionEntry) From {normal, officialCombat})
            _supplementalProvider = Function(policy, token) Task.FromResult(New List(Of VersionEntry) From {combat})
            Await LoadVersionsAsync(True)
            results("statusReportsCollisionWithoutAddition") = _versions.Count = 2 AndAlso _versions.Contains(officialCombat) AndAlso Not _versions.Contains(combat) AndAlso
                BrowserRows("战斗测试版").Count = 0 AndAlso StatusText.Text.Contains("无新增补充版本")
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
            current.SetResult(New List(Of VersionEntry) From {experiment}) : Await newWork
            before = _versions
            Dim acceptedStatus = StatusText.Text
            older.SetException(New InvalidOperationException("stale optional failure")) : Await oldWork
            results("optionalStaleExceptionKeepsNormal") = _versions Is before AndAlso _versions.Contains(experiment) AndAlso Not _versionsDirty AndAlso StatusText.Text = acceptedStatus
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
