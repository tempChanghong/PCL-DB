Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyManagementStatusUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), original = owner.Content, previous = SynchronizationContext.Current
        Dim host As New Border With {.Width = 850, .Height = 650}, layout = ControlLayout(host), observations As New JObject
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        owner.Content = Nothing : host.Child = DirectCast(original, UIElement)
        Try
            Using surface = PreviewSurface.Attach(host, 850, 650)
                SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(owner.Dispatcher))
                Dim task = owner.CheckManagementStatusAsync(directory, layout, check)
                If Not PumpMotionUntil(Function() task.IsCompleted, 30000) Then Throw New TimeoutException("Management status did not settle.")
                task.GetAwaiter().GetResult()
            End Using
            File.WriteAllText(Path.Combine(directory, "management-status.json"), observations.ToString())
            Dim failures = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failures.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failures))
        Finally
            owner._closing = True : owner.CancelManagementRequest() : owner.CancelLocalModRequest()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._managementMotion.Finish() : owner._surfaceMotion.Finish() : owner._categoryMotion.Finish()
            host.Child = Nothing : owner.Content = original : SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub

    Private Async Function CheckManagementStatusAsync(directory As String, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim folders = PrepareManagementFixture(directory), root = Path.GetDirectoryName(folders(0).Location)
        Dim originalBytes = IO.Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(Function(name) name, Function(name) File.ReadAllBytes(name))
        ShowManagement(Nothing, Nothing) : Await ScanManagementAsync(False)
        _surfaceMotion.Finish() : _managementMotion.Finish() : layout()
        Dim accepted = _managementInstances
        Dim gate As New TaskCompletionSource(Of List(Of InstalledInstance))(TaskCreationOptions.RunContinuationsAsynchronously)
        _managementProvider = Function(target, token) gate.Task
        Dim pending = ScanManagementAsync(True)
        ManagementSearch.Text = "not-present" : layout()
        check(_managementScanning AndAlso ManagementRows.Children.Count = 0 AndAlso ManagementStatus.Text = "正在读取已安装版本…",
            "filtering cached rows cannot replace an active scan with a false empty-result status")
        ManagementSearch.Text = "" : layout()
        check(_managementScanning AndAlso ManagementStatus.Text = "正在读取已安装版本…", "clearing a filter preserves the active scan status")
        gate.SetException(New IOException("controlled scan failure")) : Await pending : layout()
        check(_managementInstances Is accepted AndAlso _managementDirty AndAlso Not _managementScanning AndAlso ManagementRefresh.IsEnabled AndAlso ManagementStatus.Text.Contains("controlled scan failure"),
            "failed refresh retains usable cached rows and exposes its error with retry available")
        ManagementSearch.Text = "Fabric" : layout()
        check(ManagementRows.Children.Count = 1 AndAlso ManagementStatus.Text.Contains("controlled scan failure"), "matching a cached row cannot hide the failed refresh")
        ManagementSearch.Text = "not-present" : layout()
        check(ManagementRows.Children.Count = 0 AndAlso ManagementStatus.Text.Contains("controlled scan failure"), "an empty local filter cannot disguise an unreadable directory as a successful scan")
        ManagementSearch.Text = "" : layout()
        check(ManagementStatus.Text.Contains("controlled scan failure"), "clearing local search retains a scan failure until retry or target change")

        gate = New TaskCompletionSource(Of List(Of InstalledInstance))(TaskCreationOptions.RunContinuationsAsynchronously)
        _managementProvider = Function(target, token) gate.Task
        pending = ScanManagementAsync(True) : layout()
        check(ManagementStatus.Text = "正在读取已安装版本…" AndAlso _managementScanning, "retry replaces the previous failure with its own loading status")
        ManagementSearch.Text = "not-present"
        gate.SetResult(accepted) : Await pending : _managementMotion.Finish() : layout()
        check(Not _managementDirty AndAlso Not _managementScanning AndAlso ManagementStatus.Text = "没有找到匹配的版本。", "a successful retry restores the empty-filter explanation for the accepted list")
        ManagementSearch.Text = "" : layout()
        check(ManagementRows.Children.Count = accepted.Count AndAlso ManagementStatus.Text = "", "clearing a successful filter restores rows without reviving an old failure")

        gate = New TaskCompletionSource(Of List(Of InstalledInstance))(TaskCreationOptions.RunContinuationsAsynchronously)
        _managementProvider = Function(target, token) gate.Task
        pending = ScanManagementAsync(True)
        SelectManagedInstance(accepted.First()) : _managementMotion.Finish()
        BackFromManagement(Nothing, Nothing) : _managementMotion.Finish() : layout()
        check(Not _managementScanning AndAlso ManagementListPage.IsVisible AndAlso ManagementStatus.Text = "", "selecting a cached instance and returning retires the obsolete loading status immediately")
        gate.SetException(New IOException("retired scan failure")) : Await pending : layout()
        check(_managementInstances Is accepted AndAlso ManagementStatus.Text = "", "a retired scan failure cannot publish an error after returning from detail")

        gate = New TaskCompletionSource(Of List(Of InstalledInstance))(TaskCreationOptions.RunContinuationsAsynchronously)
        _managementProvider = Function(target, token) gate.Task : pending = ScanManagementAsync(True)
        _managementProvider = Function(target, token) Task.FromResult(InstalledInstanceCatalog.Scan(target, token))
        Await ScanManagementAsync(True) : Dim newer = _managementInstances
        gate.SetException(New IOException("superseded failure")) : Await pending : _managementMotion.Finish() : layout()
        check(_managementInstances Is newer AndAlso ManagementStatus.Text = "" AndAlso Not _managementDirty, "a superseded failure cannot overwrite a newer successful status or catalogue")

        _managementProvider = Function(target, token) Task.FromException(Of List(Of InstalledInstance))(New IOException("previous launcher failure"))
        Await ScanManagementAsync(True)
        _managementProvider = Function(target, token) Task.FromResult(InstalledInstanceCatalog.Scan(target, token))
        ManagementFolders.SelectedItem = folders(1) : Await ScanManagementAsync(False) : _managementMotion.Finish() : layout()
        check(_selectedFolder Is folders(1) AndAlso ManagementStatus.Text = "" AndAlso _managementInstances.Count = 3,
            "a same-path switch from PCL to HMCL cannot carry the old launcher's scan error")
        ManagementFolders.SelectedItem = folders(2) : Await ScanManagementAsync(False) : _managementMotion.Finish() : layout()
        check(_selectedFolder Is folders(2) AndAlso ManagementStatus.Text = "" AndAlso _managementInstances.Count = 3, "PCL-CE retains its explicit target identity and accepted list status")
        ManagementFolders.SelectedItem = folders(3) : Await ScanManagementAsync(False) : _managementMotion.Finish() : layout()
        check(ManagementStatus.Text = "这个目录中还没有已安装版本。" AndAlso _managementInstances.Count = 0, "a successful empty directory remains distinct from a failed or filtered scan")
        FolderList.SelectedItem = Nothing : layout()
        check(ManagementStatus.Text = "请选择游戏目录。" AndAlso Not _managementScanning, "clearing the target displays selection guidance instead of an old scan status")
        check(originalBytes.All(Function(item) File.ReadAllBytes(item.Key).SequenceEqual(item.Value)), "management scan status filtering retry and navigation preserve all instance and external configuration bytes")
    End Function
End Class
