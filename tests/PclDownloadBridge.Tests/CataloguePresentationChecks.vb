Imports System.IO
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module CataloguePresentationChecks
    Private Sub Check(ok As Boolean, label As String, passed As List(Of String))
        If Not ok Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub
    Friend Sub Verify(root As String, passed As List(Of String))
        Dim sodium As New ModpackProject With {.Kind = CommunityResourceKind.Mod, .Platform = ModpackPlatform.Modrinth, .Id = "AANobbMI", .Slug = "sodium", .Name = "Sodium"}
        Dim title = ResourceProjectNames.GetTitle(sodium)
        Check(title.Title = "钠" AndAlso title.Subtitle = "Sodium" AndAlso sodium.Name = "Sodium" AndAlso sodium.Id = "AANobbMI", "fixed wiki slug translation keeps Chinese title, English subtitle and immutable raw identity", passed)
        Dim jei As New ModpackProject With {.Kind = CommunityResourceKind.Mod, .Platform = ModpackPlatform.CurseForge, .Id = "238222", .Slug = "jei", .Name = "Just Enough Items (JEI)"}
        Check(ResourceProjectNames.GetTitle(jei).Title = "JEI物品管理器" AndAlso ResourceProjectNames.GetTitle(jei).Subtitle = "Just Enough Items", "CurseForge project uses its own platform slug in the fixed PCL name database", passed)
        Dim mouse As New ModpackProject With {.Kind = CommunityResourceKind.Mod, .Platform = ModpackPlatform.CurseForge, .Slug = "mouse-tweaks", .Name = "Mouse Tweaks"}
        Check(ResourceProjectNames.GetTitle(mouse).Title = "鼠标手势" AndAlso Not ResourceProjectNames.GetTitle(mouse).Subtitle.Contains("仅部分"), "same slug on different platforms cannot pick another platform's wiki variant", passed)
        mouse.Platform = ModpackPlatform.Modrinth
        Check(ResourceProjectNames.GetTitle(mouse).Subtitle.Contains("仅部分"), "Modrinth-specific wiki suffix is retained without changing project ownership", passed)
        sodium.Kind = CommunityResourceKind.ResourcePack
        Check(ResourceProjectNames.GetTitle(sodium).Title = "Sodium", "resource packs cannot inherit a mod's translation just because a slug matches", passed)
        sodium.Kind = CommunityResourceKind.DataPack
        Check(ResourceProjectNames.GetTitle(sodium).Title = "钠", "dual mod/datapack presentation follows PCL's wiki-supported types", passed)
        sodium.Slug = Nothing
        Check(ResourceProjectNames.GetTitle(sodium).Title = "Sodium", "missing slugs fall back to original names without guessing from mutable display text", passed)
        Dim fallback = ResourceProjectNames.GetTitle(New ModpackProject With {.Name = "Just Enough Items (JEI)"})
        Check(fallback.Title = "Just Enough Items" AndAlso fallback.Subtitle = "JEI", "untranslated abbreviation is separated using PCL's title hierarchy", passed)
        Check(ModpackProjectMetadata.ReadTags(ModpackPlatform.CurseForge, New JArray(New JObject From {{"id", 423}}, New JObject From {{"id", 421}}), CommunityResourceKind.Mod).SequenceEqual({"信息显示", "支持库"}), "CurseForge mod category IDs produce Chinese badges", passed)
        Check(ModpackProjectMetadata.ReadTags(ModpackPlatform.Modrinth, New JArray("library", "decoration", "library"), CommunityResourceKind.Mod).SequenceEqual({"支持库", "装饰"}) AndAlso
            Not ModpackProjectMetadata.ReadTags(ModpackPlatform.Modrinth, New JArray("decoration", "mobs", "gui"), CommunityResourceKind.ResourcePack).Contains("生物"), "mixed category labels respect resource type and deduplicate badges", passed)
        Dim supported = {"26.3", "26.2", "26.1", "1.21", "1.20", "1.19", "1.18", "1.17", "1.16", "1.15", "1.14", "1.13", "1.12", "1.11", "1.10"}
        Check(ModpackProjectStatistics.VersionText(supported.Take(9).Concat({"1.12"}), supported) = "1.16+, 1.12", "version ranges follow actual global adjacency and preserve a disconnected old family", passed)
        Check(ModpackProjectStatistics.VersionText({"1.20.1", "1.19.4", "1.18.2"}, supported) = "1.20~1.18", "three consecutive non-latest families form a bounded range", passed)
        Check(ModpackProjectStatistics.VersionText({"26.3", "26.1", "1.20"}, supported) = "26.3, 26.1, 1.20", "gaps cannot be compressed into fabricated support", passed)
        Check(ModpackProjectStatistics.VersionText({"27.1", "26.3", "26.2"}, supported) = "27.1, 26.2+", "an unknown future family does not become adjacent to the known global sequence", passed)
        Check(ModpackProjectStatistics.VersionText(supported, supported) = "全版本" AndAlso ModpackProjectStatistics.VersionText({"1.20", "1.12"}) = "1.20, 1.12", "PCL full-era compression requires a known sequence and fallback remains explicit", passed)
        Check(ModpackProjectMetadata.CompactLoaderText({"forge", "fabric", "neoforge"}, {"1.21.1"}) = "" AndAlso
            ModpackProjectMetadata.LoaderText({"forge", "fabric", "neoforge"}) = "Forge / Fabric / NeoForge", "redundant mainstream loader labels are compacted while full tooltip data stays available", passed)
        Check(ModpackProjectMetadata.CompactLoaderText({"forge", "fabric"}, {"1.21"}) <> "" AndAlso ModpackProjectMetadata.CompactLoaderText({"forge", "fabric"}, {"1.16"}) = "", "loader compression follows the era and cannot omit a missing modern loader", passed)
        For Each id In {"26w14a", "3D Shareware v1.34", "2.0_blue", "2.0_red", "2.0_purple", "20w14infinite", "1.RV-Pre1"}
            Check(MinecraftVersionCategory.Classify(id, New JObject From {{"type", "release"}}) = "special", "known April Fools identity wins over generic JSON release type: " & id, passed)
        Next
        Check(MinecraftVersionCategory.Classify("rd-132211", New JObject()) = "old_alpha" AndAlso MinecraftVersionCategory.Classify("b1.7.3", New JObject From {{"type", "old_beta"}}) = "old_beta", "historical ids and explicit beta metadata retain their old-version category", passed)
        Check(MinecraftVersionCategory.Classify("future-snapshot", JObject.Parse("{""type"":""snapshot"",""releaseTime"":""2027-03-31T22:30:00Z""}")) = "special" AndAlso
            MinecraftVersionCategory.Classify("future-snapshot", JObject.Parse("{""type"":""snapshot"",""releaseTime"":""2027-04-01T22:30:00Z""}")) = "snapshot", "April 1 date classification uses PCL UTC plus two hours rather than local calendar date", passed)
        VerifyInstalled(root, passed)
    End Sub
    Private Sub VerifyInstalled(root As String, passed As List(Of String))
        Dim work = Path.Combine(root, "presentation-" & Guid.NewGuid().ToString("N")), game = Path.Combine(work, ".minecraft")
        Directory.CreateDirectory(Path.Combine(work, "PCL"))
        File.WriteAllText(Path.Combine(work, "PCL/Setup.ini"), "LaunchArgumentIndieV2:0")
        Dim target As New LauncherInstallTarget(New GameFolder With {.Location = game, .LauncherKind = ExternalLauncherKind.Pcl, .LauncherWorkspace = work}, work)
        For Each id In {"26w14a", "3D Shareware v1.34", "rd-132211"}
            Dim folder = Path.Combine(game, "versions", id)
            Directory.CreateDirectory(folder)
            File.WriteAllText(Path.Combine(folder, id & ".json"), New JObject From {{"id", id}, {"type", If(id.StartsWith("rd-"), "old_alpha", "snapshot")}, {"mainClass", "net.minecraft.client.main.Main"}, {"libraries", New JArray()}}.ToString())
        Next
        Dim child = Path.Combine(game, "versions", "custom-child") : Directory.CreateDirectory(child)
        File.WriteAllText(Path.Combine(child, "custom-child.json"), "{""id"":""custom-child"",""inheritsFrom"":""rd-132211"",""type"":""release""}")
        Dim snapshot = Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories).ToDictionary(Function(path) path, Function(path) File.ReadAllBytes(path))
        Dim items = InstalledInstanceCatalog.Scan(target, CancellationToken.None)
        Check(items.Where(Function(item) item.Name <> "custom-child").All(Function(item) item.Problem Is Nothing AndAlso item.MinecraftVersion = item.Name) AndAlso
            items.Single(Function(item) item.Name = "3D Shareware v1.34").MinecraftKind = "special", "real catalogue recognizes nonstandard Shareware identity without relying on the instance folder's display name", passed)
        Check(items.Single(Function(item) item.Name = "custom-child").MinecraftKind = "old_alpha" AndAlso items.Single(Function(item) item.Name = "custom-child").MinecraftVersion = "rd-132211", "inherited version classification keeps base metadata even when child overrides JSON type", passed)
        Check(snapshot.All(Function(pair) File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)) AndAlso snapshot.Count = Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories).Count(), "classification is read-only and preserves all instance and launcher settings", passed)
    End Sub
End Module
