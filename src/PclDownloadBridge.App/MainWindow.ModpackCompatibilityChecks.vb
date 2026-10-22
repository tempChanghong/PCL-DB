Imports System.IO
Imports System.Windows.Controls
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Async Function VerifyPackCompatibilityUiAsync(directory As String, host As Border, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim project = DirectCast(DirectCast(PackProjectRows.Children(0), PclResourceItem).Tag, ModpackProject)
        OpenPackDetails(project) : Await WaitPackUiAsync(Function() _packDetailRequest Is Nothing) : layout()
        Dim quilt = _packReleases(0).Copy(), fabric = _packReleases(1).Copy()
        quilt.Id = "quilt-only" : quilt.Name = "Fixture Quilt" : quilt.Loaders = {"quilt"}
        fabric.Id = "fabric-ok" : fabric.Name = "Fixture Fabric" : fabric.Loaders = {"fabric"}
        Dim mixed = fabric.Copy(), unlabelled = fabric.Copy(), unfamiliar = fabric.Copy()
        mixed.Id = "mixed" : mixed.Name = "Fixture Fabric / Quilt" : mixed.Loaders = {"fabric", "quilt"}
        unlabelled.Id = "unlabelled" : unlabelled.Name = "Fixture 无加载器标签" : unlabelled.Loaders = {}
        unfamiliar.Id = "unfamiliar" : unfamiliar.Name = "Fixture 未知加载器标签" : unfamiliar.Loaders = {"future-loader"}
        _packReleases = New List(Of ModpackRelease) From {quilt, fabric, mixed, unlabelled, unfamiliar}
        InitializePackVersions() : layout()
        Dim card = DirectCast(PackVersionCards.Children(0), PclCard), rows = DirectCast(card.Body, StackPanel)
        Dim arrow = card.Children.OfType(Of Grid)().Single().Children.OfType(Of System.Windows.Shapes.Path)().Single()
        check(Not card.Collapsed AndAlso DirectCast(arrow.RenderTransform, System.Windows.Media.RotateTransform).Angle = 180, "a newly expanded version card starts with PCL's upward arrow rather than the collapsed direction")
        Dim quiltRow = DirectCast(rows.Children(0), PclListItem), fabricRow = DirectCast(rows.Children(1), PclListItem)
        check(Not quiltRow.IsEnabled AndAlso fabricRow.IsEnabled, "known unsupported Quilt-only packs are visible but cannot enter automatic installation")
        check(quiltRow.Info.Contains("暂不支持") AndAlso quiltRow.ToolTip.ToString().Contains("Quilt") AndAlso ToolTipService.GetShowOnDisabled(quiltRow), "disabled pack rows explain compatibility in the PCL info line and an accessible tooltip")
        check(rows.Children.Count = 5 AndAlso rows.Children.OfType(Of PclListItem)().Skip(2).All(Function(row) row.IsEnabled), "mixed, absent and unfamiliar loader labels are retained for archive-based validation")
        Dim generation = _taskGeneration
        quiltRow.Activate() : InstallOnlinePack(quilt) : layout()
        check(_onlinePrompt Is Nothing AndAlso Not _choosingOnline AndAlso _taskGeneration = generation AndAlso StatusText.Text.Contains("Quilt"), "both disabled-row input and direct install dispatch reject known incompatibility before naming or task creation")
        fabricRow.Activate() : layout()
        check(_onlinePrompt IsNot Nothing, "a supported alternative still enters the existing name confirmation flow")
        CancelOnlinePackName(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _choosingOnline)
        DirectCast(rows.Children(2), PclListItem).Activate() : layout()
        check(_onlinePrompt IsNot Nothing AndAlso _taskGeneration = generation, "a Fabric/Quilt alternative remains selectable after cancelling the supported version prompt")
        CancelOnlinePackName(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _choosingOnline) : layout()
        SaveControlVisual(host, Path.Combine(directory, "pack-search-compatibility.png"))
    End Function
End Class
