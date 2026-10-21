Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Sub VerifyForgeRefreshUi(check As Action(Of Boolean, String), directory As String)
        Dim original = _selectedEntry
        Try
            Dim entry = BrowserEntry("1.8.9", "release", "2015-12-09T12:00:00Z")
            SelectVersion(entry)
            Dim forge = ForgeRefreshEntry("1.8.9")
            AcceptComponentList(ComponentKind.Forge, entry, _componentGeneration, New List(Of ComponentEntry) From {forge})
            Dim view = _componentViews(ComponentKind.Forge)
            view.Card.Collapsed = False
            Dim row = MaterializeComponentRows(DirectCast(view.Card.Body, StackPanel)).Single()
            PointerEvent(row, Mouse.MouseDownEvent) : PointerEvent(row, Mouse.MouseUpEvent)
            check(_componentSelection.Item(ComponentKind.Forge) Is forge AndAlso InstanceName.Text = "1.8.9-Forge_11.15.1.2318", "a branched Forge row uses only the numeric loader version in the PCL instance name")
            Dim opt As New ComponentEntry With {.Kind = ComponentKind.OptiFine, .Minecraft = entry.Id, .Version = "I7", .RequiredForge = forge.Version}
            AcceptComponentList(ComponentKind.OptiFine, entry, _componentGeneration, New List(Of ComponentEntry) From {opt})
            view = _componentViews(ComponentKind.OptiFine) : view.Card.Collapsed = False
            row = MaterializeComponentRows(DirectCast(view.Card.Body, StackPanel)).Single()
            PointerEvent(row, Mouse.MouseDownEvent) : PointerEvent(row, Mouse.MouseUpEvent)
            check(_componentSelection.Item(ComponentKind.OptiFine) Is opt AndAlso InstallButton.IsEnabled, "a compatible OptiFine row stays selectable for a branched Forge installer")
            InstanceName.Text = "保留 Forge 分支方案"
            Dim other = ForgeRefreshEntry("alternate"), current = ForgeRefreshEntry("1.8.9")
            current.Sha1 = New String("a"c, 40)
            AcceptComponentList(ComponentKind.Forge, entry, _componentGeneration, New List(Of ComponentEntry) From {other, current})
            check(_componentSelection.Item(ComponentKind.Forge) Is current AndAlso InstanceName.Text = "保留 Forge 分支方案" AndAlso InstallButton.IsEnabled,
                  "Forge catalogue refresh rebinds the same installer coordinate rather than a different branch with the same version")
            AcceptComponentList(ComponentKind.Forge, entry, _componentGeneration, New List(Of ComponentEntry) From {other})
            check(_componentSelection.Item(ComponentKind.Forge) Is current AndAlso _unresolvedComponents.Contains(ComponentKind.Forge) AndAlso Not InstallButton.IsEnabled,
                  "a missing Forge branch preserves the original choice and blocks installation instead of silently substituting another installer")
            Dim returned = ForgeRefreshEntry("1.8.9")
            AcceptComponentList(ComponentKind.Forge, entry, _componentGeneration, New List(Of ComponentEntry) From {returned})
            check(_componentSelection.Item(ComponentKind.Forge) Is returned AndAlso Not _unresolvedComponents.Contains(ComponentKind.Forge) AndAlso
                  _componentSelection.Item(ComponentKind.OptiFine) Is opt AndAlso InstallButton.IsEnabled AndAlso InstanceName.Text = "保留 Forge 分支方案",
                  "the original Forge branch can return and resume the retained OptiFine plan without renaming it")
            SaveVersionFormatVisual(directory, "forge-branch-refresh.png")
            ClearComponent(ComponentKind.Forge)
            check(Not _componentSelection.Has(ComponentKind.Forge) AndAlso _componentSelection.Item(ComponentKind.OptiFine) Is opt AndAlso
                  Not _unresolvedComponents.Contains(ComponentKind.Forge) AndAlso InstallButton.IsEnabled AndAlso InstanceName.Text = "保留 Forge 分支方案",
                  "clearing the Forge branch leaves a valid standalone OptiFine selection and the custom name")
        Finally
            If original IsNot Nothing Then SelectVersion(original)
        End Try
    End Sub

    Private Shared Function ForgeRefreshEntry(branch As String) As ComponentEntry
        Dim coordinate = "1.8.9-11.15.1.2318-" & branch
        Return New ComponentEntry With {.Kind = ComponentKind.Forge, .Minecraft = "1.8.9", .Version = "11.15.1.2318",
            .Url = "https://maven.minecraftforge.net/net/minecraftforge/forge/" & coordinate & "/forge-" & coordinate & "-installer.jar"}
    End Function
End Class
