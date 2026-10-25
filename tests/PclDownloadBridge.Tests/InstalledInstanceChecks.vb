Imports System.Diagnostics
Imports System.IO
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

' Temporary file fixtures for the read-only management catalogue. No launcher or
' game process is run, and no real user game directory is used by these checks.
Friend Module InstalledInstanceChecks
    Private Sub Check(value As Boolean, label As String, passed As List(Of String))
        If Not value Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub

    Private Sub Put(full As String, text As String)
        Directory.CreateDirectory(Path.GetDirectoryName(full))
        File.WriteAllText(full, text, New UTF8Encoding(False))
    End Sub

    Private Function Vanilla(name As String) As JObject
        Return New JObject From {
            {"id", name}, {"clientVersion", "1.20.1"}, {"type", "release"},
            {"mainClass", "net.minecraft.client.main.Main"}, {"libraries", New JArray()},
            {"downloads", New JObject From {{"client", New JObject From {{"url", "https://example.org/client.jar"}, {"sha1", New String("a"c, 40)}, {"size", 14}}}}},
            {"assetIndex", New JObject From {{"id", "5"}, {"url", "https://example.org/assets.json"}, {"sha1", New String("b"c, 40)}, {"size", 2}}}}
    End Function

    Private Function Instance(game As String, name As String, Optional metadata As JObject = Nothing) As String
        Dim full = Path.GetFullPath(Path.Combine(game, "versions", name))
        Put(Path.Combine(full, name & ".json"), If(metadata, Vanilla(name)).ToString())
        Put(Path.Combine(full, name & ".jar"), "client fixture")
        Return full
    End Function

    Private Function PclTarget(root As String, game As String, Optional strategy As Integer = 0, Optional id As String = "pcl") As LauncherInstallTarget
        Dim work = Path.Combine(root, id)
        Put(Path.Combine(work, "PCL/Setup.ini"), "LaunchArgumentIndieV2:" & strategy & vbCrLf & "Unknown:retained")
        Return New LauncherInstallTarget(New GameFolder With {.Location = game, .LauncherBindingId = id,
            .LauncherKind = ExternalLauncherKind.Pcl, .LauncherWorkspace = work}, work)
    End Function

    Private Function CeTarget(root As String, game As String, Optional strategy As Integer = 0) As LauncherInstallTarget
        Dim work = Path.Combine(root, "CE work"), config = Path.Combine(work, "PCL/config.v1.yml")
        Put(config, "LocalFileVersion: 1" & vbLf & "LaunchArgumentIndieV2: " & strategy & vbLf & "OtherValue: retained")
        Return New LauncherInstallTarget(New GameFolder With {.Location = game, .LauncherBindingId = "ce",
            .LauncherKind = ExternalLauncherKind.PclCe, .LauncherWorkspace = work, .LauncherConfigPath = config}, work)
    End Function

    Private Function HmclTarget(root As String, game As String, modern As Boolean, Optional custom As String = Nothing) As LauncherInstallTarget
        Dim work = Path.Combine(root, If(modern, "HMCL modern", "HMCL legacy")), config As String
        If modern Then
            config = Path.Combine(work, ".hmcl/config/game-directories.json")
            Put(config, New JObject From {{"directories", New JArray(New JObject From {{"id", "game"}, {"path", game}, {"legacyGameSettings", "preset"}})}}.ToString())
            Put(Path.Combine(Path.GetDirectoryName(config), "launcher-settings.json"), "{""defaultGameSettingsPreset"":""preset""}")
            Put(Path.Combine(Path.GetDirectoryName(config), "game-settings.json"), New JObject From {{"presets", New JArray(New JObject From {
                {"id", "preset"}, {"defaultIsolationType", "ALWAYS"}, {"runningDirectory", If(custom, "")}, {"unknown", "keep"}})}}.ToString())
        Else
            config = Path.Combine(work, "hmcl.json")
            Put(config, New JObject From {{"configurations", New JObject From {{"bound", New JObject From {
                {"gameDir", game}, {"global", New JObject From {{"gameDirType", If(custom Is Nothing, 0, 2)}, {"gameDir", If(custom, "")}, {"unknown", "keep"}}}}}}}}.ToString())
        End If
        Return New LauncherInstallTarget(New GameFolder With {.Location = game, .LauncherBindingId = If(modern, "hmcl-new", "hmcl-old"),
            .LauncherKind = ExternalLauncherKind.Hmcl, .LauncherWorkspace = work, .LauncherConfigPath = config,
            .LauncherSharedConfigPath = Path.Combine(work, "not-present/user-game-directories.json")}, work)
    End Function

    Private Function Snapshot(root As String) As Dictionary(Of String, Byte())
        Return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(Function(full) full, Function(full) File.ReadAllBytes(full))
    End Function

    Private Function SameFiles(before As Dictionary(Of String, Byte()), root As String) As Boolean
        Return before.Count = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count() AndAlso
            before.All(Function(pair) File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value))
    End Function

    Private Sub Unchanged(item As InstalledInstance, passed As List(Of String), label As String)
        InstalledInstanceCatalog.EnsureUnchanged(item, CancellationToken.None)
        Check(True, label, passed)
    End Sub

    Private Sub Changed(item As InstalledInstance, passed As List(Of String), label As String)
        Dim rejected = False
        Try
            InstalledInstanceCatalog.EnsureUnchanged(item, CancellationToken.None)
        Catch ex As Exception When TypeOf ex Is InvalidDataException OrElse TypeOf ex Is IOException
            rejected = True
        End Try
        Check(rejected, label, passed)
    End Sub

    Friend Async Function RunAsync(testRoot As String) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        Dim root = Path.Combine(testRoot, "installed-instances-" & Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root)
        CatalogueChecks(root, passed)
        IsolationChecks(root, passed)
        NativeChecks(root, passed)
        MutationChecks(root, passed)
        FolderChecks(root, passed)
        LinkChecks(root, passed)
        LongNameChecks(testRoot, passed)
        Await ActualRepairAsync(testRoot, passed)
        Return passed
    End Function

    Private Sub CatalogueChecks(root As String, passed As List(Of String))
        Dim game = Path.GetFullPath(Path.Combine(root, "catalogue/中文 游戏目录")), target = PclTarget(root, game)
        Dim missing = Path.Combine(root, "not-created")
        Dim missingTarget = New LauncherInstallTarget(New GameFolder With {.Location = missing,
            .LauncherKind = ExternalLauncherKind.Pcl, .LauncherWorkspace = target.Workspace}, target.Workspace)
        Check(InstalledInstanceCatalog.Scan(missingTarget, CancellationToken.None).Count = 0 AndAlso Not Directory.Exists(missing), "management scan leaves an absent game root absent", passed)
        Dim vanillaPath = Instance(game, "我的原版")
        Dim brokenPath = Path.Combine(game, "versions/坏 JSON")
        Put(Path.Combine(brokenPath, "坏 JSON.json"), "{")
        Directory.CreateDirectory(Path.Combine(game, "versions/只有文件夹"))
        Dim duplicatePath = Path.Combine(game, "versions/duplicate")
        Put(Path.Combine(duplicatePath, "duplicate.json"), "{""id"":""one"",""id"":""two""}")
        Dim before = Snapshot(root), dirs = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).ToArray()
        Dim all = InstalledInstanceCatalog.Scan(target, CancellationToken.None)
        Dim vanillaItem = all.Single(Function(item) item.Name = "我的原版")
        Check(all.Count = 4 AndAlso all.Select(Function(item) item.Name).Distinct().Count() = 4, "management keeps one item for every installed directory including broken entries", passed)
        Check(vanillaItem.MinecraftVersion = "1.20.1" AndAlso vanillaItem.InstancePath = vanillaPath AndAlso String.IsNullOrEmpty(vanillaItem.Problem), "custom instance names retain their actual Minecraft version and directory", passed)
        Check(vanillaItem.IsVanillaRepairCandidate AndAlso Not vanillaItem.IsModpack AndAlso vanillaItem.RunDirectory = game, "recognized standalone vanilla offers repair in its shared game directory", passed)
        Check(Not String.IsNullOrEmpty(all.Single(Function(item) item.Name = "坏 JSON").Problem) AndAlso Not String.IsNullOrEmpty(all.Single(Function(item) item.Name = "只有文件夹").Problem), "bad or missing JSON is isolated to its own visible problem item", passed)
        Check(Not String.IsNullOrEmpty(all.Single(Function(item) item.Name = "duplicate").Problem), "duplicate manifest properties do not produce an apparently healthy instance", passed)
        Check(SameFiles(before, root) AndAlso dirs.SequenceEqual(Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)), "scanning preserves all manifest and external configuration bytes without creating folders", passed)
        Unchanged(vanillaItem, passed, "unchanged catalogue snapshot can be revalidated without writes")

        For Each coordinate In {"net.fabricmc:fabric-loader:0.16.10", "net.minecraftforge:forge:1.20.1-47.4.0", "net.neoforged:neoforge:21.1.50", "org.quiltmc:quilt-loader:0.28.1", "com.mumfrey:liteloader:1.12.2", "optifine:OptiFine:1.20.1_HD_U_I6"}
            Dim name = coordinate.Split(":"c)(1), metadata = Vanilla(name)
            metadata("libraries") = New JArray(New JObject From {{"name", coordinate}})
            Instance(game, name, metadata)
            Dim item = InstalledInstanceCatalog.Read(target, name, CancellationToken.None)
            Dim expected = If(name = "fabric-loader", "Fabric", If(name = "forge", "Forge", If(name = "neoforge", "NeoForge", If(name = "quilt-loader", "Quilt", If(name = "liteloader", "LiteLoader", "OptiFine")))))
            Check(item.LoaderSummary.Contains(expected) AndAlso Not item.IsVanillaRepairCandidate, "loader metadata prevents a vanilla repair offer: " & coordinate, passed)
        Next
        Dim child = New JObject From {{"id", "inherited"}, {"inheritsFrom", "我的原版"}, {"libraries", New JArray(New JObject From {{"name", "net.fabricmc:fabric-loader:0.16.10"}})}}
        Instance(game, "inherited", child)
        Dim inherited = InstalledInstanceCatalog.Read(target, "inherited", CancellationToken.None)
        Check(inherited.MinecraftVersion = "1.20.1" AndAlso inherited.LoaderSummary.Contains("Fabric") AndAlso Not inherited.IsVanillaRepairCandidate, "read-only inheritance resolves parent game identity and child loader without offering vanilla repair", passed)
        Instance(game, "missing-parent", New JObject From {{"id", "missing-parent"}, {"inheritsFrom", "absent"}, {"libraries", New JArray()}})
        Dim noParent = InstalledInstanceCatalog.Read(target, "missing-parent", CancellationToken.None)
        Check(Not noParent.IsVanillaRepairCandidate AndAlso (Not String.IsNullOrEmpty(noParent.Problem) OrElse noParent.Warnings.Count > 0), "missing inheritance parent remains a diagnosable unsupported instance", passed)
        Instance(game, "cycle-a", New JObject From {{"id", "cycle-a"}, {"inheritsFrom", "cycle-b"}, {"libraries", New JArray()}})
        Instance(game, "cycle-b", New JObject From {{"id", "cycle-b"}, {"inheritsFrom", "cycle-a"}, {"libraries", New JArray()}})
        Dim cyclic = InstalledInstanceCatalog.Read(target, "cycle-a", CancellationToken.None)
        Check(Not cyclic.IsVanillaRepairCandidate AndAlso (Not String.IsNullOrEmpty(cyclic.Problem) OrElse cyclic.Warnings.Count > 0), "inheritance cycles terminate with an instance-level problem", passed)
        For Each marker In {"PCL/CurseForgeInstall.json", "PCL/ModrinthInstall.json"}
            Dim name = If(marker.Contains("CurseForge"), "CF pack", "MR pack"), full = Instance(game, name)
            Put(Path.Combine(full, marker), If(marker.Contains("CurseForge"), "{}", New String(" "c, 2 * 1024 * 1024 + 1) & "{}"))
            Dim item = InstalledInstanceCatalog.Read(target, name, CancellationToken.None)
            Check(item.IsModpack AndAlso Not item.IsVanillaRepairCandidate AndAlso String.IsNullOrEmpty(item.Problem), "pack receipt excludes a vanilla-looking pack from repair without reading its payload: " & marker, passed)
        Next
        Dim unsigned = Vanilla("unsigned")
        DirectCast(unsigned.SelectToken("downloads.client"), JObject).Remove("sha1")
        Instance(game, "unsigned", unsigned)
        Check(Not InstalledInstanceCatalog.Read(target, "unsigned", CancellationToken.None).IsVanillaRepairCandidate, "missing client checksum cannot become a vanilla repair candidate", passed)
        Dim customMain = Vanilla("custom-main")
        customMain("mainClass") = "example.custom.Loader"
        Instance(game, "custom-main", customMain)
        Check(Not InstalledInstanceCatalog.Read(target, "custom-main", CancellationToken.None).IsVanillaRepairCandidate, "unrecognized launch entry cannot become a vanilla repair candidate", passed)
        For Each action As Action In {
            Sub() InstalledInstanceCatalog.Scan(target, New CancellationToken(True)),
            Sub() InstalledInstanceCatalog.Read(target, "我的原版", New CancellationToken(True)),
            Sub() InstalledInstanceCatalog.EnsureUnchanged(vanillaItem, New CancellationToken(True))}
            Dim cancelled = False
            Try
                action()
            Catch ex As OperationCanceledException
                cancelled = True
            End Try
            Check(cancelled, "management cancellation is propagated instead of becoming a broken instance", passed)
        Next
    End Sub

    Private Sub IsolationChecks(root As String, passed As List(Of String))
        Dim game = Path.GetFullPath(Path.Combine(root, "isolation/game")), target = PclTarget(root, game, 0, "PCL shared")
        Dim isolatedPath = Instance(game, "isolated")
        Put(Path.Combine(isolatedPath, "PCL/Setup.ini"), "VersionArgumentIndieV2:True" & vbCrLf & "Unknown:keep")
        Dim isolated = InstalledInstanceCatalog.Read(target, "isolated", CancellationToken.None)
        Check(isolated.IsIsolated.GetValueOrDefault() AndAlso isolated.RunDirectory = isolatedPath, "PCL existing V2 isolation overrides a shared global default", passed)
        Dim sharedPath = Instance(game, "shared"), allTarget = PclTarget(root, game, 4, "PCL isolated")
        Put(Path.Combine(sharedPath, "PCL/Setup.ini"), "VersionArgumentIndieV2:False")
        Dim sharedItem = InstalledInstanceCatalog.Read(allTarget, "shared", CancellationToken.None)
        Check(sharedItem.IsIsolated.HasValue AndAlso Not sharedItem.IsIsolated.Value AndAlso sharedItem.RunDirectory = game, "PCL explicit shared instance stays shared despite a new-install isolation default", passed)
        For Each choice In {1, 2}
            Dim name = "legacy-" & choice, full = Instance(game, name)
            Put(Path.Combine(full, "PCL/Setup.ini"), "VersionArgumentIndie:" & choice)
            Check(InstalledInstanceCatalog.Read(target, name, CancellationToken.None).RunDirectory = If(choice = 1, full, game), "PCL legacy per-instance isolation is read without migration: " & choice, passed)
        Next
        Dim inferred = Instance(game, "inferred")
        Put(Path.Combine(inferred, "mods/example.jar"), "user file")
        Check(InstalledInstanceCatalog.Read(target, "inferred", CancellationToken.None).RunDirectory = inferred, "existing Mods data participates in PCL isolation inference", passed)
        Dim defaultPath = Instance(game, "default")
        Dim defaultShared = InstalledInstanceCatalog.Read(target, "default", CancellationToken.None)
        Dim defaultIsolated = InstalledInstanceCatalog.Read(allTarget, "default", CancellationToken.None)
        Check(defaultShared.RunDirectory = game AndAlso defaultIsolated.RunDirectory = defaultPath AndAlso defaultShared.Target.BindingId <> defaultIsolated.Target.BindingId, "same physical root preserves distinct launcher binding interpretations", passed)

        Dim ce = CeTarget(root, game, 4), cePath = Instance(game, "CE local")
        Put(Path.Combine(cePath, "PCL/config.v1.yml"), "VersionArgumentIndieV2: false" & vbLf & "Custom: retained")
        Check(InstalledInstanceCatalog.Read(ce, "CE local", CancellationToken.None).RunDirectory = game, "CE local YAML overrides its global isolation policy", passed)
        Dim jsonPath = Instance(game, "CE JSON")
        Put(Path.Combine(jsonPath, "PCL/config.v1.json"), "{""VersionArgumentIndieV2"":false,""Unknown"":""keep""}")
        Check(InstalledInstanceCatalog.Read(ce, "CE JSON", CancellationToken.None).RunDirectory = game, "CE JSON fallback reads existing isolation without creating YAML", passed)
        Dim iniPath = Instance(game, "CE INI")
        Put(Path.Combine(iniPath, "PCL/Setup.ini"), "VersionArgumentIndieV2:False")
        Check(InstalledInstanceCatalog.Read(ce, "CE INI", CancellationToken.None).RunDirectory = game, "CE legacy INI fallback reads isolation without migrating settings", passed)
        Dim invalidPath = Instance(game, "CE ambiguous")
        Put(Path.Combine(invalidPath, "PCL/config.v1.yml"), "VersionArgumentIndieV2: true" & vbLf & "VersionArgumentIndieV2: false")
        Dim ambiguous = InstalledInstanceCatalog.Read(ce, "CE ambiguous", CancellationToken.None)
        Check(Not String.IsNullOrEmpty(ambiguous.Problem) OrElse ambiguous.Warnings.Count > 0, "ambiguous CE YAML is reported instead of silently guessing the data location", passed)
        For Each strategy In {1, 3}
            Dim apiTarget = CeTarget(root, game, strategy), name = "CE API " & strategy, full = Instance(game, name)
            Put(Path.Combine(full, "PCL/config.v1.yml"), "DisplayType: 2" & vbLf & "Unknown: retained")
            Dim api = InstalledInstanceCatalog.Read(apiTarget, name, CancellationToken.None)
            Check(api.RunDirectory = full AndAlso api.IsIsolated.GetValueOrDefault(), "CE API display classification participates in existing-instance isolation: " & strategy, passed)
            Dim labyName = "CE LabyMod " & strategy, metadata = Vanilla(labyName), labyPath As String
            metadata("labymod_data") = New JObject From {{"version", "4.2.0"}}
            labyPath = Instance(game, labyName, metadata)
            Dim laby = InstalledInstanceCatalog.Read(apiTarget, labyName, CancellationToken.None)
            Check(laby.LoaderSummary.Contains("LabyMod") AndAlso Not laby.IsVanillaRepairCandidate AndAlso laby.RunDirectory = labyPath, "LabyMod marker prevents vanilla repair and participates in CE Mod isolation: " & strategy, passed)
        Next
        For Each patchTarget In {target, ce}
            Dim name = "foreign-patch-" & patchTarget.Kind.ToString(), metadata = Vanilla(name)
            metadata.Remove("clientVersion")
            metadata("patches") = New JArray(New JObject From {{"id", "game"}, {"version", "1.20.1"}, {"libraries", New JArray()}, {"mainClass", "net.minecraft.client.main.Main"},
                {"downloads", metadata("downloads").DeepClone()}, {"assetIndex", metadata("assetIndex").DeepClone()}})
            Instance(game, name, metadata)
            Dim item = InstalledInstanceCatalog.Read(patchTarget, name, CancellationToken.None)
            Check(item.MinecraftVersion = "1.20.1" AndAlso Not item.IsVanillaRepairCandidate, "HMCL patch metadata on another launcher is displayed without an unsupported repair offer: " & patchTarget.Kind.ToString(), passed)
        Next
        For Each strategy In {1, 3}
            Dim pclFlags = PclTarget(root, game, strategy, "PCL flags " & strategy), ceFlags = CeTarget(root, game, strategy)
            For Each loader In {"Quilt", "LabyMod"}
                Dim name = "loader-flags-" & loader & "-" & strategy, metadata = Vanilla(name)
                If loader = "Quilt" Then
                    metadata("libraries") = New JArray(New JObject From {{"name", "org.quiltmc:quilt-loader:0.28.1"}})
                Else
                    metadata("labymod_data") = New JObject From {{"version", "4.2.0"}}
                End If
                Dim full = Instance(game, name, metadata)
                Dim pclItem = InstalledInstanceCatalog.Read(pclFlags, name, CancellationToken.None)
                Dim ceItem = InstalledInstanceCatalog.Read(ceFlags, name, CancellationToken.None)
                Check(pclItem.RunDirectory = game AndAlso ceItem.RunDirectory = full AndAlso Not pclItem.IsVanillaRepairCandidate AndAlso Not ceItem.IsVanillaRepairCandidate,
                    "same metadata follows each launcher's own component isolation flags: " & loader & " strategy " & strategy, passed)
            Next
        Next
    End Sub

    Private Class RepairPolicy
        Inherits SourcePolicy
        Private ReadOnly _server As String
        Friend Sub New(server As String)
            _server = server : AllowLoopback = True
        End Sub
        Public Overrides Function Candidates(url As String, category As String) As List(Of String)
            Return New List(Of String) From {_server & New Uri(url).AbsolutePath}
        End Function
    End Class

    Private Function Digest(bytes As Byte()) As String
        Using sha = Security.Cryptography.SHA1.Create()
            Return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function

    Private Async Function ActualRepairAsync(root As String, passed As List(Of String)) As Task
        For Each kind In {"PCL", "CE", "HMCL", "HMCL-legacy"}
            For Each isolated In {False, True}
                Dim label = kind & "-" & isolated
                Using fixture As New InstallationChecks.InstallFixture(root, "management-repair-" & label)
                    IsolationResourceChecks.Configure(fixture)
                    Dim metadata = JObject.Parse(Encoding.UTF8.GetString(fixture.Server.Files("/version")))
                    metadata("mainClass") = "net.minecraft.client.main.Main"
                    Dim libraryBytes = Encoding.UTF8.GetBytes("management repair library bytes"), libraryRelative = "libraries/fixture/management/1/management-1.jar"
                    fixture.Server.Files("/management-library") = libraryBytes
                    metadata("libraries") = New JArray(New JObject From {{"name", "fixture:management:1"}, {"downloads", New JObject From {{"artifact", New JObject From {
                        {"path", libraryRelative.Substring("libraries/".Length)}, {"url", fixture.Server.Url & "/management-library"}, {"sha1", Digest(libraryBytes)}, {"size", libraryBytes.Length}}}}}})
                    fixture.Server.Files("/version") = Encoding.UTF8.GetBytes(metadata.ToString())
                    fixture.Entry.Sha1 = Digest(fixture.Server.Files("/version"))
                    Dim target As LauncherInstallTarget
                    Select Case kind
                        Case "PCL" : target = PclTarget(root, fixture.Root, 0, "actual-PCL-" & isolated)
                        Case "CE" : target = CeTarget(Path.Combine(root, "actual-CE-" & isolated), fixture.Root, 0)
                        Case Else : target = HmclTarget(Path.Combine(root, "actual-" & label), fixture.Root, kind = "HMCL")
                    End Select
                    Dim policy As New RepairPolicy(fixture.Server.Url)
                    Dim installer As New MinecraftInstaller(policy, target.PclDirectory, launcherTarget:=target)
                    Const instanceName As String = "管理原版"
                    Dim installed = Await installer.InstallAsync(fixture.Entry, target.Root, instanceName, {}, Nothing, CancellationToken.None,
                        connections:=2, isolationMode:=If(isolated, InstanceIsolationMode.Enabled, InstanceIsolationMode.Disabled))
                    Dim settingsPath = Path.Combine(installed.InstancePath, If(kind = "PCL", "PCL/Setup.ini", If(kind = "CE", "PCL/config.v1.yml", If(kind = "HMCL", ".hmcl/config/instance-game-settings.json", "hmclversion.cfg"))))
                    If kind = "PCL" OrElse kind = "CE" Then
                        Put(settingsPath, File.ReadAllText(settingsPath) & vbLf & "UnknownManagementSetting" & If(kind = "PCL", ":", ": ") & "retained")
                    Else
                        Dim settings = JObject.Parse(File.ReadAllText(settingsPath))
                        settings("UnknownManagementSetting") = New JObject From {{"nested", True}}
                        Put(settingsPath, settings.ToString())
                    End If
                    Dim item = InstalledInstanceCatalog.Scan(target, CancellationToken.None).Single(Function(value) value.Name = instanceName)
                    Check(item.IsVanillaRepairCandidate AndAlso item.MinecraftVersion = fixture.Entry.Id AndAlso item.IsIsolated.GetValueOrDefault() = isolated,
                        "actual installed vanilla is recognized as a repair candidate in its native isolation context: " & label, passed)
                    Dim protectedFiles = New Dictionary(Of String, Byte()) From {{Path.Combine(installed.InstancePath, instanceName & ".json"), File.ReadAllBytes(Path.Combine(installed.InstancePath, instanceName & ".json"))}, {settingsPath, File.ReadAllBytes(settingsPath)}}
                    Dim external = Snapshot(target.Workspace)
                    Dim index = JObject.Parse(Encoding.UTF8.GetString(fixture.Server.Files("/index")))
                    Dim resource = DirectCast(index("objects"), JObject).Properties().Single(), resourceHash = CStr(resource.Value("hash"))
                    Dim resourceBytes = fixture.Server.Files("/" & resourceHash.Substring(0, 2) & "/" & resourceHash)
                    Dim resourcePath = Path.Combine(item.RunDirectory, "resources", resource.Name.Replace("/"c, Path.DirectorySeparatorChar))
                    Dim wrongResourcePath = Path.Combine(If(isolated, target.Root, installed.InstancePath), "resources", resource.Name.Replace("/"c, Path.DirectorySeparatorChar))
                    Dim libraryPath = SafeFiles.UnderRoot(target.Root, libraryRelative), clientPath = Path.Combine(installed.InstancePath, instanceName & ".jar")
                    File.Delete(libraryPath) : File.Delete(resourcePath)
                    Put(clientPath, "damaged fixture client")
                    InstalledInstanceCatalog.EnsureUnchanged(item, CancellationToken.None)
                    Dim repairEntry = fixture.Entry.Copy()
                    repairEntry.Id = item.MinecraftVersion
                    Dim repairInstaller As New MinecraftInstaller(policy, item.Target.PclDirectory, launcherTarget:=item.Target)
                    Dim repaired = Await repairInstaller.InstallAsync(repairEntry, item.Target.Root, item.Name, {}, Nothing, CancellationToken.None, repairExisting:=True, connections:=2)
                    Check(repaired.InstancePath = item.InstancePath AndAlso File.ReadAllBytes(libraryPath).SequenceEqual(libraryBytes) AndAlso
                        File.ReadAllBytes(clientPath).SequenceEqual(fixture.Server.Files("/client")), "management snapshot hands the same root, name and game version to the real vanilla repair kernel: " & label, passed)
                    Check(File.ReadAllBytes(resourcePath).SequenceEqual(resourceBytes) AndAlso Not File.Exists(wrongResourcePath), "management-origin repair restores legacy resources only in the existing native run directory: " & label, passed)
                    Check(protectedFiles.All(Function(pair) File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)) AndAlso SameFiles(external, target.Workspace),
                        "management-origin repair preserves manifest, native isolation and every external configuration byte: " & label, passed)
                    Unchanged(item, passed, "repaired instance remains the same frozen management snapshot: " & label)
                End Using
            Next
        Next
    End Function

    Private Sub NativeChecks(root As String, passed As List(Of String))
        For Each modern In {False, True}
            Dim game = Path.GetFullPath(Path.Combine(root, "hmcl-" & modern & "/game")), target = HmclTarget(root, game, modern)
            Dim full = Instance(game, "plain"), local = If(modern, ".hmcl/config/instance-game-settings.json", "hmclversion.cfg")
            Check(InstalledInstanceCatalog.Read(target, "plain", CancellationToken.None).RunDirectory = game, "HMCL existing plain instance inherits root run directory, not new-install defaults: " & modern, passed)
            Put(Path.Combine(full, local), If(modern,
                "{""overrideProperties"": [""runningDirectory""], ""runningDirectory"":"""",""Unknown"":""keep""}",
                "{""usesGlobal"":false,""gameDirType"":1,""Unknown"":""keep""}"))
            Check(InstalledInstanceCatalog.Read(target, "plain", CancellationToken.None).RunDirectory = full, "HMCL explicit instance isolation is honored: " & modern, passed)
            Dim custom = Path.GetFullPath(Path.Combine(root, "hmcl-" & modern & "/external data"))
            Put(Path.Combine(full, local), If(modern,
                New JObject From {{"overrideProperties", New JArray("runningDirectory")}, {"runningDirectory", custom}}.ToString(),
                New JObject From {{"usesGlobal", False}, {"gameDirType", 2}, {"gameDir", custom}}.ToString()))
            Dim before = Snapshot(root), item = InstalledInstanceCatalog.Read(target, "plain", CancellationToken.None)
            Check(item.RunDirectory = custom AndAlso Not Directory.Exists(custom), "HMCL custom running directory is displayed without creating or changing it: " & modern, passed)
            Check(SameFiles(before, root), "HMCL instance inspection preserves original configuration bytes: " & modern, passed)
            Put(Path.Combine(full, "modpack.cfg"), "{}")
            Dim pack = InstalledInstanceCatalog.Read(target, "plain", CancellationToken.None)
            Check(pack.RunDirectory = full AndAlso pack.IsModpack AndAlso Not pack.IsVanillaRepairCandidate, "HMCL modpack marker enforces native isolation and disables vanilla repair: " & modern, passed)
            Dim patchName = "patched", patch = Vanilla(patchName)
            patch.Remove("clientVersion")
            patch("patches") = New JArray(New JObject From {{"id", "game"}, {"version", "1.20.1"}, {"libraries", New JArray()}, {"mainClass", "net.minecraft.client.main.Main"},
                {"downloads", patch("downloads").DeepClone()}, {"assetIndex", patch("assetIndex").DeepClone()}})
            Instance(game, patchName, patch)
            Dim patched = InstalledInstanceCatalog.Read(target, patchName, CancellationToken.None)
            Check(patched.MinecraftVersion = "1.20.1", "HMCL game patch supplies the real Minecraft version: " & modern, passed)
            DirectCast(patch("patches"), JArray).Add(New JObject From {{"id", "fabric"}, {"version", "0.16.10"}})
            Put(Path.Combine(game, "versions", patchName, patchName & ".json"), patch.ToString())
            Dim modded = InstalledInstanceCatalog.Read(target, patchName, CancellationToken.None)
            Check(Not modded.IsVanillaRepairCandidate AndAlso modded.LoaderSummary.Contains("Fabric"), "HMCL multi-patch loader cannot become a vanilla repair target: " & modern, passed)
        Next
    End Sub

    Private Sub MutationChecks(root As String, passed As List(Of String))
        Dim game = Path.GetFullPath(Path.Combine(root, "mutation/game")), target = PclTarget(root, game, 0, "mutation launcher")
        Dim full = Instance(game, "plain"), item = InstalledInstanceCatalog.Read(target, "plain", CancellationToken.None)
        Dim json = Path.Combine(full, "plain.json"), original = File.ReadAllText(json)
        Put(json, original & " ")
        Changed(item, passed, "management repair snapshot rejects a changed manifest even when JSON remains semantically equal")
        Put(json, original)
        item = InstalledInstanceCatalog.Read(target, "plain", CancellationToken.None)
        Put(Path.Combine(full, "PCL/Setup.ini"), "VersionArgumentIndieV2:True")
        Changed(item, passed, "management repair snapshot rejects an instance setting created after inspection")
        item = InstalledInstanceCatalog.Read(target, "plain", CancellationToken.None)
        Put(Path.Combine(target.Workspace, "PCL/Setup.ini"), "LaunchArgumentIndieV2:4")
        Changed(item, passed, "management repair snapshot detects external configuration changes")
        item = InstalledInstanceCatalog.Read(target, "plain", CancellationToken.None)
        Dim before = Snapshot(root)
        Unchanged(item, passed, "freshly rescanned instance is valid after a configuration change")
        Check(SameFiles(before, root), "snapshot validation reads configurations without writing initialization or migration state", passed)
        Dim marker = Path.Combine(full, "PCL/ModrinthInstall.json")
        Put(marker, "{}")
        Changed(item, passed, "new modpack receipt invalidates an earlier vanilla repair snapshot")
        item = InstalledInstanceCatalog.Read(target, "plain", CancellationToken.None)
        File.Delete(marker)
        Changed(item, passed, "removed modpack receipt invalidates the earlier pack snapshot")
        Dim parent = Instance(game, "parent"), child = New JObject From {{"id", "child"}, {"inheritsFrom", "parent"}, {"libraries", New JArray()}}
        Instance(game, "child", child)
        item = InstalledInstanceCatalog.Read(target, "child", CancellationToken.None)
        Dim parentJson = Path.Combine(parent, "parent.json")
        Put(parentJson, File.ReadAllText(parentJson) & " ")
        Changed(item, passed, "inherited parent manifest bytes participate in the frozen instance snapshot")
    End Sub

    Private Sub LinkChecks(root As String, passed As List(Of String))
        Dim game = Path.GetFullPath(Path.Combine(root, "links/game")), target = PclTarget(root, game, 0, "link launcher")
        Instance(game, "healthy")
        Dim outside = Path.Combine(root, "links/outside"), link = Path.Combine(game, "versions/linked")
        Put(Path.Combine(outside, "linked.json"), Vanilla("linked").ToString())
        MakeJunction(root, link, outside)
        Try
            Dim bytes = File.ReadAllBytes(Path.Combine(outside, "linked.json"))
            Dim all = InstalledInstanceCatalog.Scan(target, CancellationToken.None), item = all.Single(Function(value) value.Name = "linked")
            Check(Not String.IsNullOrEmpty(item.Problem) AndAlso Not item.IsVanillaRepairCandidate AndAlso all.Any(Function(value) value.Name = "healthy" AndAlso String.IsNullOrEmpty(value.Problem)), "linked instance is rejected individually without hiding healthy siblings", passed)
            Check(bytes.SequenceEqual(File.ReadAllBytes(Path.Combine(outside, "linked.json"))), "linked outside target remains byte-for-byte unchanged", passed)
        Finally
            If Directory.Exists(link) AndAlso (File.GetAttributes(link) And FileAttributes.ReparsePoint) <> 0 Then Directory.Delete(link, False)
        End Try
    End Sub

    Private Sub FolderChecks(root As String, passed As List(Of String))
        Dim game = Path.GetFullPath(Path.Combine(root, "folder-actions/game")), target = PclTarget(root, game, 0, "folder launcher")
        Dim one = Instance(game, "one"), two = Instance(game, "two"), isolated = Instance(game, "isolated")
        Put(Path.Combine(isolated, "PCL/Setup.ini"), "VersionArgumentIndieV2:True")
        Directory.CreateDirectory(Path.Combine(game, "mods"))
        Directory.CreateDirectory(Path.Combine(isolated, "resourcepacks"))
        Dim first = InstalledInstanceCatalog.Read(target, "one", CancellationToken.None)
        Dim second = InstalledInstanceCatalog.Read(target, "two", CancellationToken.None)
        Dim privateItem = InstalledInstanceCatalog.Read(target, "isolated", CancellationToken.None)
        Check(InstalledInstanceCatalog.FolderPath(first, "") = one, "open-instance action resolves the manifest directory rather than its shared run directory", passed)
        Check(InstalledInstanceCatalog.FolderPath(first, "mods") = Path.Combine(game, "mods") AndAlso
            InstalledInstanceCatalog.FolderPath(second, "mods") = Path.Combine(game, "mods"), "two shared instances resolve the same actual Mods folder", passed)
        Check(InstalledInstanceCatalog.FolderPath(privateItem, "resourcepacks") = Path.Combine(isolated, "resourcepacks"), "isolated content action resolves the instance's private directory", passed)
        Dim missing = Path.Combine(game, "screenshots"), rejected = False
        Try
            InstalledInstanceCatalog.FolderPath(first, "screenshots")
        Catch ex As DirectoryNotFoundException
            rejected = True
        End Try
        Check(rejected AndAlso Not Directory.Exists(missing), "opening a missing content folder does not create it", passed)
        rejected = False
        Try
            InstalledInstanceCatalog.FolderPath(first, "../outside")
        Catch ex As InvalidDataException
            rejected = True
        End Try
        Check(rejected, "content-folder action rejects traversal outside the frozen run directory", passed)
        Dim outside = Path.Combine(root, "folder-actions/outside")
        Directory.CreateDirectory(outside)
        MakeJunction(root, missing, outside)
        Try
            rejected = False
            Try
                InstalledInstanceCatalog.FolderPath(first, "screenshots")
            Catch ex As IOException
                rejected = True
            End Try
            Check(rejected, "content-folder action rejects a link introduced after the instance was scanned", passed)
        Finally
            If Directory.Exists(missing) AndAlso (File.GetAttributes(missing) And FileAttributes.ReparsePoint) <> 0 Then Directory.Delete(missing, False)
        End Try
    End Sub

    Private Sub MakeJunction(root As String, link As String, outside As String)
        Dim allowed = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) & Path.DirectorySeparatorChar
        If Not Path.GetFullPath(link).StartsWith(allowed, StringComparison.OrdinalIgnoreCase) OrElse Not Path.GetFullPath(outside).StartsWith(allowed, StringComparison.OrdinalIgnoreCase) Then Throw New InvalidOperationException("Junction fixture escaped its temporary root.")
        Dim info As New ProcessStartInfo("cmd.exe", "/d /c mklink /J """ & link & """ """ & outside & """") With {
            .UseShellExecute = False, .CreateNoWindow = True, .WindowStyle = ProcessWindowStyle.Hidden, .RedirectStandardOutput = True, .RedirectStandardError = True}
        Using process = System.Diagnostics.Process.Start(info)
            Dim output = process.StandardOutput.ReadToEnd(), errors = process.StandardError.ReadToEnd()
            process.WaitForExit()
            If process.ExitCode <> 0 Then Throw New IOException(output & errors)
        End Using
    End Sub

    Private Sub LongNameChecks(testRoot As String, passed As List(Of String))
        Dim game = Path.GetFullPath(Path.Combine(testRoot, "long-name-game")), target = PclTarget(testRoot, game, 0, "long-name-launcher")
        Instance(game, "healthy")
        Dim name = New String("n"c, 101)
        Directory.CreateDirectory(Path.Combine(game, "versions", name))
        Dim all = InstalledInstanceCatalog.Scan(target, CancellationToken.None)
        Check(all.Count = 2 AndAlso Not String.IsNullOrEmpty(all.Single(Function(item) item.Name = name).Problem) AndAlso
            String.IsNullOrEmpty(all.Single(Function(item) item.Name = "healthy").Problem), "unsupported existing folder name is a single problem item and cannot abort the whole catalogue", passed)
    End Sub
End Module
