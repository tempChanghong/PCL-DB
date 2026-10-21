Imports System.IO
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Sub VerifyVersionFormatUi(check As Action(Of Boolean, String), directory As String)
        Dim original = _selectedEntry
        Dim observations As New JArray
        Dim modern = BrowserEntry("26.1", "release", "2026-01-15T12:00:00Z")
        Dim forge As New ComponentEntry With {.Kind = ComponentKind.Forge, .Minecraft = modern.Id, .Version = "62.0.0", .Url = "https://example.invalid/forge-new-numbering.jar"}
        Try
            SelectVersion(modern)
            AcceptComponentList(ComponentKind.Forge, modern, _componentGeneration, New List(Of ComponentEntry) From {forge})
            Dim view = _componentViews(ComponentKind.Forge)
            view.Card.Collapsed = False
            SaveVersionFormatVisual(directory)
            check(view.Card.Visibility = Visibility.Visible AndAlso view.Card.Collapsible, "new-numbered Minecraft exposes Forge when its catalogue has an applicable installer")
            Dim cases As New Dictionary(Of String, Boolean) From {
                {"26.1.2", True}, {"26.1-snapshot-1", True}, {"26.2-pre-1", True},
                {"1.21.1", True}, {"1.21.1-pre1", True}, {"1.14 Pre-Release 2", True},
                {"25w14craftmine", False}, {"26w14a", False}, {"20w14infinite", False},
                {"b1.7.3", False}, {"25.1", False}}
            For Each item In cases
                SelectVersion(BrowserEntry(item.Key, If(item.Key.StartsWith("b", StringComparison.Ordinal), "old_beta", "snapshot"), "2026-01-15T12:00:00Z"))
                Dim visible = _componentViews(ComponentKind.Forge).Card.Visibility = Visibility.Visible
                observations.Add(New JObject From {{"Version", item.Key}, {"ForgeVisible", visible}, {"Expected", item.Value}})
                check(visible = item.Value, "Forge card visibility follows PCL standard-format classification for " & item.Key)
            Next
            SelectVersion(modern)
            AcceptComponentList(ComponentKind.Forge, modern, _componentGeneration, New List(Of ComponentEntry) From {forge})
            view = _componentViews(ComponentKind.Forge)
            view.Card.Collapsed = False
            Dim row = MaterializeComponentRows(DirectCast(view.Card.Body, StackPanel)).Single()
            PointerEvent(row, Mouse.MouseDownEvent) : PointerEvent(row, Mouse.MouseUpEvent)
            check(_componentSelection.Item(ComponentKind.Forge) Is forge AndAlso InstanceName.Text = "26.1-Forge_62.0.0" AndAlso InstallButton.IsEnabled,
                "a new-numbered Forge row selects its exact metadata and updates the installation name")
            ClearComponent(ComponentKind.Forge)
            AcceptComponentList(ComponentKind.Forge, modern, _componentGeneration, New List(Of ComponentEntry))
            check(view.Card.Visibility = Visibility.Visible AndAlso Not view.Card.Collapsible AndAlso view.Summary.Text = "无适用版本" AndAlso Not _componentSelection.Has(ComponentKind.Forge),
                "a new-numbered version with no Forge installer shows the empty state without inventing a selection")
        Finally
            File.WriteAllText(Path.Combine(directory, "forge-version-format.json"), observations.ToString())
            If original IsNot Nothing Then SelectVersion(original)
        End Try
    End Sub

    Private Sub SaveVersionFormatVisual(directory As String, Optional fileName As String = "forge-new-numbering.png")
        Dim content = DirectCast(Me.Content, FrameworkElement), host As New Border With {.Width = 850, .Height = 700}
        Dim source As Interop.HwndSource = Nothing
        Try
            Me.Content = Nothing : host.Child = content
            source = PreviewSurface.Attach(host, 850, 700)
            DownloadPage.BeginAnimation(OpacityProperty, Nothing) : DownloadPage.Opacity = 1
            host.Measure(New Size(850, 700)) : host.Arrange(New Rect(0, 0, 850, 700)) : host.UpdateLayout() : FlushControlLayout() : host.UpdateLayout()
            SaveControlVisual(host, Path.Combine(directory, fileName))
        Finally
            If source IsNot Nothing Then
                source.RootVisual = Nothing : source.Dispose()
            End If
            host.Child = Nothing
            If Me.Content Is Nothing Then Me.Content = content
        End Try
    End Sub
End Class
