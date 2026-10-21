Imports System.Windows
Imports System.Windows.Controls
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Sub PreviewForgeCompatibility()
        SelectVersion(_versions.Single(Function(v) v.Id = "1.20.1"))
        Dim forge As New ComponentEntry With {.Kind = ComponentKind.Forge, .Minecraft = "1.20.1", .Version = "47.2.18"}
        Dim replacement As New ComponentEntry With {.Kind = ComponentKind.Forge, .Minecraft = "1.20.1", .Version = "47.2.19"}
        Dim opt As New ComponentEntry With {.Kind = ComponentKind.OptiFine, .Minecraft = "1.20.1", .Version = "I6", .RequiredForge = forge.Version, .Data = New JObject()}
        _componentEntries(ComponentKind.Forge) = New List(Of ComponentEntry) From {replacement, forge}
        _componentEntries(ComponentKind.OptiFine) = New List(Of ComponentEntry) From {opt}
        SelectComponent(forge)
        SelectComponent(opt)
        InstanceName.Text = "保留我的组合方案"
    End Sub

    Private Function ChangedOptiFineRequirement() As ComponentEntry
        Dim selected = _componentSelection.Item(ComponentKind.OptiFine)
        Return New ComponentEntry With {.Kind = selected.Kind, .Minecraft = selected.Minecraft, .Version = selected.Version, .RequiredForge = "47.2.19", .Data = New JObject()}
    End Function

    Private Sub PreviewBridgeRequirement()
        SelectVersion(_versions.Single(Function(v) v.Id = "1.20.1"))
        Dim api = _componentEntries(ComponentKind.FabricApi)(0)
        api.Version = "0.92.2+1.20.1" : api.Data("name") = "Fabric API " & api.Version
        _componentEntries(ComponentKind.OptiFine)(0).Version = "I6"
        SelectComponent(_componentEntries(ComponentKind.Fabric)(0))
        SelectComponent(_componentEntries(ComponentKind.OptiFine)(0))
    End Sub

    Private Sub VerifyCompatibilityWorkflow(check As Action(Of Boolean, String))
        PreviewForgeCompatibility()
        check(InstallButton.IsEnabled, "a compatible Forge OptiFine plan can be installed")
        Dim forge = _componentSelection.Item(ComponentKind.Forge)
        Dim opt = ChangedOptiFineRequirement()
        AcceptComponentList(ComponentKind.OptiFine, _selectedEntry, _componentGeneration, New List(Of ComponentEntry) From {opt})
        check(Not InstallButton.IsEnabled AndAlso ComponentHint.Text.Contains("Forge") AndAlso ComponentHint.Text.Contains("OptiFine"), "refreshed Forge requirements immediately block an incompatible plan with a visible reason")
        check(ToolTipService.GetShowOnDisabled(InstallButton) AndAlso CStr(InstallButton.ToolTip).Contains("Forge"), "a disabled install button retains its blocking reason even when the hint is below the fold")
        SetBusy(True)
        SetBusy(False)
        check(Not InstallButton.IsEnabled AndAlso CStr(InstallButton.ToolTip).Contains("OptiFine"), "unlocking settings cannot reenable an incompatible installation plan")
        check(_componentSelection.Item(ComponentKind.Forge) Is forge AndAlso _componentSelection.Item(ComponentKind.OptiFine) Is opt AndAlso InstanceName.Text = "保留我的组合方案", "compatibility failure retains both choices and the custom name for correction")
        Dim view = _componentViews(ComponentKind.Forge)
        check(view.Summary.Text.Contains("不兼容") AndAlso CStr(view.Summary.ToolTip).Contains("OptiFine") AndAlso view.Clear.Visibility = Visibility.Visible, "incompatible selected component header explains the issue and keeps its clear action")
        view.Card.Collapsed = False
        check(view.Card.Collapsible, "compatible alternatives remain accessible while the current plan is invalid")
        SelectComponent(_componentEntries(ComponentKind.Forge).First(Function(v) v.Version = "47.2.19"))
        check(InstallButton.IsEnabled AndAlso Not view.Summary.Text.Contains("不兼容") AndAlso InstanceName.Text = "保留我的组合方案", "choosing a compatible Forge clears the warning and restores install without renaming")

        AcceptComponentList(ComponentKind.OptiFine, _selectedEntry, _componentGeneration,
            New List(Of ComponentEntry) From {New ComponentEntry With {.Kind = opt.Kind, .Minecraft = opt.Minecraft, .Version = opt.Version, .RequiredForge = Nothing, .Data = New JObject()}})
        check(Not InstallButton.IsEnabled, "a refreshed OptiFine declaration withdrawing Forge support also blocks installation")
        ClearComponent(ComponentKind.OptiFine)
        check(InstallButton.IsEnabled AndAlso _componentSelection.Has(ComponentKind.Forge) AndAlso InstanceName.Text = "保留我的组合方案", "clearing the conflicting component restores a valid loader-only plan")

        PreviewBridgeRequirement()
        InstanceName.Text = "保留我的 Fabric 方案"
        check(Not InstallButton.IsEnabled AndAlso ComponentHint.Text.Contains("OptiFabric"), "Fabric OptiFine without the required bridge is blocked before starting installation")
        Dim bridge As New ComponentEntry With {.Kind = ComponentKind.OptiFabric, .Minecraft = "1.20.1", .Version = "1.14.3", .Data = New JObject()}
        AcceptComponentList(ComponentKind.OptiFabric, _selectedEntry, _componentGeneration, New List(Of ComponentEntry) From {bridge})
        check(InstallButton.IsEnabled AndAlso _componentSelection.Item(ComponentKind.OptiFabric) Is bridge, "a late compatible bridge makes the retained Fabric OptiFine plan installable")
        ClearComponent(ComponentKind.OptiFabric)
        RefreshComponents()
        check(Not InstallButton.IsEnabled AndAlso Not _componentSelection.Has(ComponentKind.OptiFabric) AndAlso InstanceName.Text = "保留我的 Fabric 方案", "explicitly clearing the required bridge keeps it cleared and shows a blocking reason")
        ClearComponent(ComponentKind.OptiFine)
        check(InstallButton.IsEnabled AndAlso _componentSelection.Has(ComponentKind.Fabric) AndAlso _componentSelection.Has(ComponentKind.FabricApi), "removing OptiFine restores Fabric with its optional API selection intact")
        ClearComponent(ComponentKind.FabricApi)
        check(InstallButton.IsEnabled AndAlso ComponentHint.Text.Contains("建议"), "an optional Fabric API recommendation never blocks installation")
    End Sub
End Class
