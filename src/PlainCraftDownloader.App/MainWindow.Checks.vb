Imports PlainCraftDownloader.Core

Partial Public Class MainWindow
    Private Sub VerifyComponentArrivals(check As Action(Of Boolean, String))
        SelectVersion(_versions(0))
        Dim entry = _selectedEntry
        Dim generation = _componentGeneration
        Dim api = _componentEntries(ComponentKind.FabricApi)(0)
        _componentEntries.Remove(ComponentKind.FabricApi)
        SelectComponent(_componentEntries(ComponentKind.Fabric)(0))
        check(Not _componentSelection.Has(ComponentKind.FabricApi), "Fabric may be selected while API list is still pending")
        AcceptComponentList(ComponentKind.FabricApi, entry, generation, New List(Of ComponentEntry) From {api})
        check(_componentSelection.Has(ComponentKind.FabricApi), "late API result selects the dependency after Fabric")
        ClearComponent(ComponentKind.FabricApi)
        AcceptComponentList(ComponentKind.FabricApi, entry, generation, New List(Of ComponentEntry) From {api})
        check(Not _componentSelection.Has(ComponentKind.FabricApi), "late refresh respects an explicitly cleared dependency")
        _componentGeneration += 1
        AcceptComponentList(ComponentKind.FabricApi, entry, generation, New List(Of ComponentEntry))
        check(_componentEntries(ComponentKind.FabricApi).Count = 1, "stale request cannot overwrite a newer result for the same Minecraft entry")
        _suppressedDependencies.Clear()
        _busy = True
        Try
            AcceptComponentList(ComponentKind.FabricApi, entry, _componentGeneration, New List(Of ComponentEntry) From {api})
        Finally
            _busy = False
        End Try
        check(Not _componentSelection.Has(ComponentKind.FabricApi), "late dependency cannot change the selection once installation started")
        SelectVersion(_versions.Single(Function(v) v.Id = "1.20.1"))
        SelectComponent(_componentEntries(ComponentKind.Fabric)(0))
        SelectComponent(_componentEntries(ComponentKind.OptiFine)(0))
        Dim bridge As New ComponentEntry With {.Kind = ComponentKind.OptiFabric, .Minecraft = "1.20.1", .Version = "1.14.3"}
        AcceptComponentList(ComponentKind.OptiFabric, _selectedEntry, _componentGeneration, New List(Of ComponentEntry) From {bridge})
        check(_componentSelection.Has(ComponentKind.OptiFabric), "late OptiFabric result completes a Fabric OptiFine selection")
        ClearComponent(ComponentKind.OptiFabric)
        AcceptComponentList(ComponentKind.OptiFabric, _selectedEntry, _componentGeneration, New List(Of ComponentEntry) From {bridge})
        check(Not _componentSelection.Has(ComponentKind.OptiFabric), "refresh keeps a manually cleared OptiFabric choice")
    End Sub
End Class
