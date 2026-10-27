Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Public Sub VerifyResourceSaveDirectoryUi(directory As String, passed As List(Of String))
        Dim owner As New MainWindow(True), observations As New JObject
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     observations(label) = ok
                                                     If ok Then passed.Add(label)
                                                 End Sub
        Try
            For launcher = 0 To 2
                For Each isolated In {False, True}
                    For Each version In {"1.5.2", "1.6.4"}
                        Dim folders = owner.PrepareManagementFixture(directory), folder = folders(launcher)
                        owner.FolderList.SelectedItem = folder
                        Select Case folder.LauncherKind
                            Case ExternalLauncherKind.Pcl
                                File.WriteAllText(Path.Combine(folder.LauncherWorkspace, "PCL/Setup.ini"), "LaunchArgumentIndieV2:" & If(isolated, "4", "0") & vbLf & "Unknown:keep")
                            Case ExternalLauncherKind.PclCe
                                File.WriteAllText(Path.Combine(folder.LauncherWorkspace, "PCL/config.v1.yml"), "LaunchArgumentIndieV2: " & If(isolated, "4", "0") & vbLf & "Unknown: keep")
                            Case ExternalLauncherKind.Hmcl
                                Dim config = JObject.Parse(File.ReadAllText(folder.LauncherConfigPath))
                                config("configurations")("fixture")("global")("gameDirType") = If(isolated, 1, 0)
                                File.WriteAllText(folder.LauncherConfigPath, config.ToString())
                        End Select
                        Dim jsonPath = Path.Combine(folder.Location, "versions/我的原版/我的原版.json"), metadata = JObject.Parse(File.ReadAllText(jsonPath))
                        metadata("clientVersion") = version : File.WriteAllText(jsonPath, metadata.ToString())
                        Dim target = owner.CurrentInstallTarget(), instance = InstalledInstanceCatalog.Read(target, "我的原版", CancellationToken.None)
                        owner._managedInstance = instance : owner._localModInstance = Nothing
                        Dim files = DirectoryFilesForModUpdateChecks(Path.GetDirectoryName(folder.Location)), generation = owner._taskGeneration
                        Dim run = instance.RunDirectory, legacy = version = "1.5.2", child = If(legacy, "texturepacks", "resourcepacks")
                        Dim label = "/launcher=" & folder.LauncherKind.ToString() & "/isolated=" & isolated & "/version=" & version
                        check(owner.ResourceSaveDirectory(CommunityResourceKind.ResourcePack) = run AndAlso
                            Not IO.Directory.Exists(Path.Combine(run, "texturepacks")) AndAlso Not IO.Directory.Exists(Path.Combine(run, "resourcepacks")),
                            "save location falls back to the native run directory without creating missing pack folders" & label)
                        For Each root In {folder.Location, run}.Distinct(StringComparer.OrdinalIgnoreCase)
                            For Each folderName As String In {"texturepacks", "resourcepacks", "mods", "shaderpacks"}
                                IO.Directory.CreateDirectory(Path.Combine(root, folderName))
                            Next
                        Next
                        Dim release As New ModpackRelease With {.Platform = ModpackPlatform.Modrinth, .Kind = CommunityResourceKind.ResourcePack, .Id = "save-file", .ProjectId = "save-project",
                            .FileName = "resource.zip", .Size = 100, .Sha1 = New String("a"c, 40), .GameVersions = {version}}
                        Dim plan = CommunityResourceInstallation.Create(release, instance, Nothing, CancellationToken.None), expected = Path.Combine(run, child)
                        check(instance.IsIsolated.HasValue AndAlso instance.IsIsolated.Value = isolated AndAlso Path.GetDirectoryName(plan.Destination) = expected AndAlso owner.ResourceSaveDirectory(release.Kind) = expected,
                            "save dialog and installation agree on legacy or modern packs in the selected native directory" & label)
                        owner._managedInstance = Nothing : owner._localModInstance = instance
                        check(owner.ResourceSaveDirectory(release.Kind) = expected AndAlso owner.ResourceSaveDirectory(CommunityResourceKind.Mod) = Path.Combine(run, "mods") AndAlso
                            owner.ResourceSaveDirectory(CommunityResourceKind.Shader) = Path.Combine(run, "shaderpacks") AndAlso owner.ResourceSaveDirectory(CommunityResourceKind.DataPack) = run,
                            "local Mod context uses the same pack rule while other resource types keep their directories" & label)
                        owner.FolderList.SelectedItem = folders((launcher + 1) Mod 3)
                        check(owner.ResourceSaveContext() Is Nothing AndAlso owner.ResourceSaveDirectory(release.Kind) = Path.Combine(folder.Location, "resourcepacks") AndAlso
                            files.All(Function(item) File.Exists(item.Key) AndAlso File.ReadAllBytes(item.Key).SequenceEqual(item.Value)) AndAlso owner._taskGeneration = generation AndAlso Not owner._busy,
                            "switching launcher ignores the old instance and preserves all native files without starting a task" & label)
                    Next
                Next
            Next
            File.WriteAllText(Path.Combine(directory, "resource-save-directory.json"), observations.ToString())
            Dim failed = observations.Properties().Where(Function(item) Not CBool(item.Value)).Select(Function(item) item.Name).ToArray()
            If failed.Length > 0 Then Throw New InvalidOperationException(String.Join(Environment.NewLine, failed))
        Finally
            owner._closing = True : owner.CancelManagementRequest() : owner.CancelLocalModRequest() : owner.CancelResourceRequests()
            owner._pclThemeMonitor.Dispose() : owner._pclThemeCycleTimer.Stop()
            owner._surfaceMotion.Finish() : owner._managementMotion.Finish() : owner._categoryMotion.Finish()
        End Try
    End Sub
End Class
