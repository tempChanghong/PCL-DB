Imports System.IO
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Sub VerifyLoaderBoundaryUi(check As Action(Of Boolean, String), directory As String)
        Dim original = _selectedEntry, observations As New JArray
        Try
            Dim cases = {
                Tuple.Create("1.9-pre1", True, False), Tuple.Create("1.13-pre1", False, False),
                Tuple.Create("1.13", False, False), Tuple.Create("1.14-pre1", False, True), Tuple.Create("1.16-pre1", False, True),
                Tuple.Create("1.20.4-pre1", False, True), Tuple.Create("26.1-snapshot-1", False, True),
                Tuple.Create("2.0", True, False), Tuple.Create("15w14a", True, False),
                Tuple.Create("1.RV-Pre1", True, False), Tuple.Create("3D Shareware v1.34", False, False),
                Tuple.Create("20w14infinite", False, True), Tuple.Create("b1.7.3", True, False)}
            For Each item In cases
                SelectVersion(BrowserEntry(item.Item1, If(item.Item1.StartsWith("b", StringComparison.Ordinal), "old_beta", "snapshot"), "2026-01-15T12:00:00Z"))
                Dim lite = _componentViews(ComponentKind.LiteLoader).Card.Visibility = Visibility.Visible
                Dim fabric = _componentViews(ComponentKind.Fabric).Card.Visibility = Visibility.Visible
                observations.Add(New JObject From {{"Version", item.Item1}, {"LiteLoader", lite}, {"Fabric", fabric}})
                check(lite = item.Item2 AndAlso fabric = item.Item3, "loader cards use the PCL release family for " & item.Item1)
            Next
            For Each mc In {"1.16-pre1", "1.20.4-pre1"}
                Dim entry = BrowserEntry(mc, "snapshot", "2023-11-20T12:00:00Z")
                SelectVersion(entry)
                Dim dependency As New ComponentEntry With {.Kind = ComponentKind.OptiFabric, .Minecraft = entry.Id, .Version = "fixture", .Data = New JObject From {{"name", "OptiFabric fixture"}}}
                AcceptComponentList(ComponentKind.OptiFabric, entry, _componentGeneration, New List(Of ComponentEntry) From {dependency})
                SelectComponent(_componentEntries(ComponentKind.Fabric)(0))
                Dim optView = _componentViews(ComponentKind.OptiFine)
                optView.Card.Collapsed = False
                Dim row = MaterializeComponentRows(DirectCast(optView.Card.Body, StackPanel)).SingleOrDefault()
                If row IsNot Nothing Then
                    PointerEvent(row, Mouse.MouseDownEvent) : PointerEvent(row, Mouse.MouseUpEvent)
                End If
                check(row IsNot Nothing AndAlso _componentSelection.Has(ComponentKind.OptiFine) AndAlso
                      _componentSelection.Item(ComponentKind.OptiFabric) Is dependency AndAlso InstallButton.IsEnabled,
                      "a compatible OptiFine row selects with Fabric and auto-selects its exact bridge for " & mc)
                SaveVersionFormatVisual(directory, "loader-" & mc & ".png")
                ClearComponent(ComponentKind.Fabric)
                check(_componentSelection.Has(ComponentKind.OptiFine) AndAlso Not _componentSelection.Has(ComponentKind.OptiFabric) AndAlso
                      _componentViews(ComponentKind.OptiFabric).Card.Visibility = Visibility.Collapsed AndAlso InstallButton.IsEnabled,
                      "clearing Fabric keeps OptiFine and removes its no-longer-required bridge for " & mc)
            Next
            SelectVersion(BrowserEntry("1.14.3-pre1", "snapshot", "2019-06-15T12:00:00Z"))
            Dim forge = _componentEntries(ComponentKind.Forge)(0), opt = _componentEntries(ComponentKind.OptiFine)(0)
            opt.RequiredForge = forge.Version
            SelectComponent(forge)
            check(Not _componentViews(ComponentKind.OptiFine).Card.Collapsible AndAlso Not _componentSelection.Has(ComponentKind.OptiFine),
                  "the Forge OptiFine exclusion hides incompatible pre-release rows despite matching loader numbers")
        Finally
            File.WriteAllText(Path.Combine(directory, "loader-boundaries.json"), observations.ToString())
            If original IsNot Nothing Then SelectVersion(original)
        End Try
    End Sub
End Class
