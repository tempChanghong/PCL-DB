Imports System.IO

' PCL 5ef81e4 ModModpack selects Forge / NeoForge / Fabric from the archive.
' Catalogue tags are supported alternatives, never the source of dependencies.
Public NotInheritable Class ModpackCompatibility
    Private Sub New()
    End Sub

    Public Shared Function GetInstallBlockReason(release As ModpackRelease) As String
        Dim declared = KnownLoaders(release.Loaders)
        ' Missing or unfamiliar tags do not prove incompatibility. Parse the archive.
        If declared Is Nothing OrElse declared.Count = 0 OrElse declared.Any(Function(loader) {"Forge", "NeoForge", "Fabric", "原版"}.Contains(loader, StringComparer.Ordinal)) Then Return Nothing
        Return "此版本使用 " & String.Join("、", declared) & "，当前暂不支持自动安装。请选择 Forge、NeoForge、Fabric 或原版版本。"
    End Function

    Public Shared Sub ValidateManifest(release As ModpackRelease, minecraft As String, loader As ComponentKind?)
        If release.GameVersions.Length > 0 AndAlso Not release.GameVersions.Contains(minecraft, StringComparer.OrdinalIgnoreCase) Then Throw New InvalidDataException("整合包清单与所选版本声明的 Minecraft 版本不一致。")
        Dim declared = KnownLoaders(release.Loaders)
        If declared Is Nothing OrElse declared.Count = 0 Then Return
        Dim actual = If(loader.HasValue, loader.GetValueOrDefault().ToString(), "原版")
        If Not declared.Contains(actual, StringComparer.Ordinal) Then Throw New InvalidDataException("整合包清单中的加载器（" & actual & "）与所选版本标注（" & String.Join("、", declared) & "）不一致。")
    End Sub

    Private Shared Function KnownLoaders(values As IEnumerable(Of String)) As List(Of String)
        Dim result As New List(Of String)
        For Each value In If(values, Enumerable.Empty(Of String)())
            Dim canonical As String
            Select Case If(value, "").Trim().ToLowerInvariant()
                Case "forge" : canonical = "Forge"
                Case "neoforge", "neo-forge" : canonical = "NeoForge"
                Case "fabric", "fabric-loader" : canonical = "Fabric"
                Case "minecraft", "vanilla" : canonical = "原版"
                Case "quilt", "quilt-loader" : canonical = "Quilt"
                Case "liteloader" : canonical = "LiteLoader"
                Case "rift" : canonical = "Rift"
                Case Else : Return Nothing
            End Select
            If Not result.Contains(canonical, StringComparer.Ordinal) Then result.Add(canonical)
        Next
        Return result
    End Function
End Class
