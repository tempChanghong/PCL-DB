Imports System.IO
Imports System.IO.Compression
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

' All writes and native leases are confined to temporary fixtures.
Friend Module ResourceInstallationChecks
    Private Sub Check(ok As Boolean, label As String, passed As List(Of String))
        If Not ok Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub
    Private Sub Put(full As String, value As String)
        Directory.CreateDirectory(Path.GetDirectoryName(full)) : File.WriteAllText(full, value, New UTF8Encoding(False))
    End Sub
    Private Function Target(root As String, kind As String, isolated As Boolean, Optional custom As String = Nothing) As LauncherInstallTarget
        Dim game = Path.Combine(root, "game"), work = Path.Combine(root, "work"), config As String
        Directory.CreateDirectory(game)
        If kind = "PCL" Then
            config = Path.Combine(work, "PCL/Setup.ini") : Put(config, "LaunchArgumentIndieV2:" & If(isolated, "4", "0") & vbLf & "Unknown:keep")
        ElseIf kind = "CE" Then
            config = Path.Combine(work, "PCL/config.v1.yml") : Put(config, "LaunchArgumentIndieV2: " & If(isolated, "4", "0") & vbLf & "Unknown: keep")
        ElseIf kind = "HMCL" Then
            config = Path.Combine(work, ".hmcl/config/game-directories.json")
            Put(config, New JObject From {{"directories", New JArray(New JObject From {{"path", game}, {"legacyGameSettings", "preset"}})}}.ToString())
            Put(Path.Combine(Path.GetDirectoryName(config), "launcher-settings.json"), "{""defaultGameSettingsPreset"":""preset""}")
            Put(Path.Combine(Path.GetDirectoryName(config), "game-settings.json"), New JObject From {{"presets", New JArray(New JObject From {{"id", "preset"}, {"defaultIsolationType", If(isolated, "ALWAYS", "NEVER")}, {"runningDirectory", If(custom, "")}, {"unknown", "keep"}})}}.ToString())
        Else
            config = Path.Combine(work, "hmcl.json")
            Put(config, New JObject From {{"configurations", New JObject From {{"bound", New JObject From {{"gameDir", game}, {"global", New JObject From {{"gameDirType", If(custom Is Nothing, If(isolated, 1, 0), 2)}, {"gameDir", If(custom, "")}, {"unknown", "keep"}}}}}}}}.ToString())
        End If
        Return New LauncherInstallTarget(New GameFolder With {.Location = game, .LauncherWorkspace = work, .LauncherBindingId = kind, .LauncherKind = If(kind = "PCL", ExternalLauncherKind.Pcl, If(kind = "CE", ExternalLauncherKind.PclCe, ExternalLauncherKind.Hmcl)), .LauncherConfigPath = config, .LauncherSharedConfigPath = Path.Combine(work, "shared/not-present.json")}, work)
    End Function
    Private Function Instance(target As LauncherInstallTarget, name As String, Optional version As String = "1.20.1", Optional fabric As Boolean = True) As InstalledInstance
        Put(Path.Combine(target.Root, "versions", name, name & ".json"), New JObject From {{"id", name}, {"clientVersion", version}, {"type", "release"}, {"mainClass", "net.minecraft.client.main.Main"}, {"libraries", If(fabric, New JArray(New JObject From {{"name", "net.fabricmc:fabric-loader:0.16.0"}}), New JArray())}}.ToString())
        Return InstalledInstanceCatalog.Read(target, name, CancellationToken.None)
    End Function
    Private Function Release(kind As CommunityResourceKind, bytes As Byte(), Optional name As String = Nothing) As ModpackRelease
        Dim hash As String
        Using sha = SHA1.Create() : hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() : End Using
        Return New ModpackRelease With {.Kind = kind, .Platform = ModpackPlatform.Modrinth, .ProjectId = "resource", .Id = hash, .FileName = If(name, If(kind = CommunityResourceKind.Mod, "resource.jar", "resource.zip")), .Url = "https://cdn.modrinth.com/data/resource/file", .Size = bytes.Length, .Sha1 = hash, .GameVersions = {"1.20.1"}, .Loaders = {If(kind = CommunityResourceKind.Mod, "fabric", If(kind = CommunityResourceKind.DataPack, "datapack", "minecraft"))}}
    End Function
    Private Function ModBytes() As Byte()
        Using output As New MemoryStream()
            Using archive As New ZipArchive(output, ZipArchiveMode.Create, True)
                Using writer As New StreamWriter(archive.CreateEntry("fabric.mod.json").Open(), New UTF8Encoding(False))
                    writer.Write("{""schemaVersion"":1,""id"":""resource_fixture"",""version"":""1.0"",""name"":""Resource fixture""}")
                End Using
            End Using
            Return output.ToArray()
        End Using
    End Function
    Private Function Reject(action As Action) As Boolean
        Try
            action() : Return False
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is OperationCanceledException
            Return True
        End Try
    End Function
    Friend Async Function RunAsync(root As String) As Task(Of List(Of String))
        Dim passed As New List(Of String), bytes = Encoding.UTF8.GetBytes("verified resource fixture"), modData = ModBytes()
        Using reuseServer As New TestServer()
        For Each kind In {"PCL", "CE", "HMCL", "HMCL-legacy"}
            For Each mode In {"shared", "isolated", "custom"}
                If mode = "custom" AndAlso (kind = "PCL" OrElse kind = "CE") Then Continue For
                Dim base = Path.Combine(root, "resource-install", kind & mode), custom = If(mode = "custom", Path.Combine(base, "custom-run"), Nothing)
                If custom IsNot Nothing Then Directory.CreateDirectory(custom)
                Dim target = ResourceInstallationChecks.Target(base, kind, mode = "isolated", custom), item = Instance(target, "selected")
                If kind = "HMCL" AndAlso mode = "isolated" Then
                    ' Existing modern instances use their override, not the policy
                    ' that HMCL applies only when creating a new instance.
                    Put(Path.Combine(item.InstancePath, ".hmcl/config/instance-game-settings.json"), "{""parent"":""preset"",""overrideProperties"":[""runningDirectory""],""runningDirectory"":""""}")
                    item = InstalledInstanceCatalog.Read(target, item.Name, CancellationToken.None)
                End If
                Dim expected = If(custom, If(mode = "isolated", item.InstancePath, target.Root)), label = kind & "/" & mode
                Check(item.Problem Is Nothing AndAlso String.Equals(expected, item.RunDirectory, StringComparison.OrdinalIgnoreCase), "native isolation resolved without rewriting settings: " & label, passed)
                Dim original = Directory.EnumerateFiles(base, "*", SearchOption.AllDirectories).ToDictionary(Function(full) full, Function(full) File.ReadAllBytes(full))
                Put(Path.Combine(expected, "options.txt"), "retain options")
                Put(Path.Combine(expected, "saves/Chosen/level.dat"), "world marker") : Put(Path.Combine(expected, "saves/Other/level.dat"), "other marker")
                Directory.CreateDirectory(Path.Combine(expected, "saves/NotAWorld"))
                Dim worlds = CommunityResourceInstallation.Worlds(item, CancellationToken.None), world = worlds.Single(Function(value) value.Name = "Chosen")
                Check(worlds.Count = 2, "world picker only offers existing direct level.dat worlds: " & label, passed)
                For Each resourceKind In {CommunityResourceKind.Mod, CommunityResourceKind.ResourcePack, CommunityResourceKind.Shader, CommunityResourceKind.DataPack}
                    Dim data = If(resourceKind = CommunityResourceKind.Mod, modData, bytes), release = ResourceInstallationChecks.Release(resourceKind, data), source = Path.Combine(base, "source", release.FileName)
                    release.Url = reuseServer.Url & "/" & release.FileName
                    Directory.CreateDirectory(Path.GetDirectoryName(source)) : File.WriteAllBytes(source, data)
                    Dim plan = CommunityResourceInstallation.Create(release, item, If(resourceKind = CommunityResourceKind.DataPack, world, Nothing), CancellationToken.None)
                    Dim suffix = If(resourceKind = CommunityResourceKind.Mod, "mods", If(resourceKind = CommunityResourceKind.ResourcePack, "resourcepacks", If(resourceKind = CommunityResourceKind.Shader, "shaderpacks", "saves/Chosen/datapacks")))
                    Check(plan.Destination = Path.GetFullPath(Path.Combine(expected, suffix, release.FileName)) AndAlso plan.Publish(source, CancellationToken.None) AndAlso File.ReadAllBytes(plan.Destination).SequenceEqual(data), "resource publishes to exact native run directory: " & label & "/" & resourceKind.ToString(), passed)
                    Check(Not plan.Publish(source, CancellationToken.None), "identical selected resource is reused without overwrite: " & label & "/" & resourceKind.ToString(), passed)
                    Dim coldCache = Path.Combine(base, "unused-cache")
                    Check(Not Await plan.InstallAsync(coldCache, New ModrinthChecks.Policy(reuseServer.Url), Nothing, CancellationToken.None) AndAlso Not Directory.Exists(coldCache), "verified installed content avoids network and cold-cache creation: " & label & "/" & resourceKind.ToString(), passed)
                    If resourceKind = CommunityResourceKind.Mod Then
                        Dim alternate = ResourceInstallationChecks.Release(resourceKind, data, "another-name.jar"), alternateSource = Path.Combine(base, "source/another-name.jar")
                        File.WriteAllBytes(alternateSource, data)
                        Dim alternatePlan = CommunityResourceInstallation.Create(alternate, item, Nothing, CancellationToken.None)
                        Check(Reject(Sub() alternatePlan.Publish(alternateSource, CancellationToken.None)) AndAlso Not File.Exists(alternatePlan.Destination), "known duplicate Mod ID under another filename is retained and rejected: " & label, passed)
                    End If
                Next
                Check(original.All(Function(pair) File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)) AndAlso File.ReadAllText(Path.Combine(expected, "options.txt")) = "retain options" AndAlso Not Directory.Exists(Path.Combine(expected, "saves/Other/datapacks")), "existing settings, manifests, options and other worlds remain unchanged: " & label, passed)
            Next
        Next
        If reuseServer.Requests.Values.Sum() <> 0 Then
            Console.WriteLine("RESOURCE_REUSE_UNEXPECTED_HTTP " & New JArray(reuseServer.Requests.Select(Function(pair) New JObject From {
                {"Path", pair.Key.Split("?"c)(0).Substring(0, Math.Min(200, pair.Key.Split("?"c)(0).Length))}, {"Count", pair.Value}})).ToString(Newtonsoft.Json.Formatting.None))
        End If
        Check(reuseServer.Requests.Values.Sum() = 0, "all native isolation and resource types reuse checked destinations without any HTTP requests", passed)
        End Using
        Await EdgeAsync(root, passed)
        Await PreflightAsync(root, passed)
        Return passed
    End Function
    Private Async Function PreflightAsync(root As String, passed As List(Of String)) As Task
        Dim base = Path.Combine(root, "resource-preflight"), target = ResourceInstallationChecks.Target(base, "PCL", False), instance = ResourceInstallationChecks.Instance(target, "selected")
        Using server As New TestServer()
            Dim bytes = Encoding.UTF8.GetBytes("selected file bytes"), release = ResourceInstallationChecks.Release(CommunityResourceKind.ResourcePack, bytes)
            server.Files("/resource.zip") = bytes : release.Url = server.Url & "/resource.zip"
            Dim plan = CommunityResourceInstallation.Create(release, instance, Nothing, CancellationToken.None), cache = Path.Combine(base, "cache")
            Directory.CreateDirectory(Path.GetDirectoryName(plan.Destination)) : File.WriteAllText(plan.Destination, "existing owner's different bytes")
            Dim problem As Exception = Nothing
            Try
                Await plan.InstallAsync(cache, New ModrinthChecks.Policy(server.Url), Nothing, CancellationToken.None)
            Catch ex As Exception
                problem = ex
            End Try
            Check(TypeOf problem Is IOException AndAlso problem.Message.Contains("resource.zip") AndAlso problem.Message.Contains("保留"), "an occupied instance filename is reported as a preserved target conflict rather than poisoned download cache", passed)
            Check(server.Requests.Values.Sum() = 0 AndAlso File.ReadAllText(plan.Destination) = "existing owner's different bytes", "known target conflicts abort before network acquisition without touching existing bytes", passed)
            release.FileName = "occupied.zip" : plan = CommunityResourceInstallation.Create(release, instance, Nothing, CancellationToken.None)
            Directory.CreateDirectory(plan.Destination)
            Check(Await FailsInstallAsync(plan, cache, server) AndAlso server.Requests.Values.Sum() = 0 AndAlso Directory.Exists(plan.Destination), "a directory occupying the resource filename is retained and rejected before acquisition", passed)
            Dim modData = ModBytes(), modRelease = ResourceInstallationChecks.Release(CommunityResourceKind.Mod, modData)
            modRelease.Url = server.Url & "/resource.jar" : server.Files("/resource.jar") = modData
            plan = CommunityResourceInstallation.Create(modRelease, instance, Nothing, CancellationToken.None)
            Directory.CreateDirectory(Path.GetDirectoryName(plan.Destination)) : File.WriteAllText(plan.Destination & ".disabled", "keep disabled")
            Check(Await FailsInstallAsync(plan, cache, server) AndAlso server.Requests.Values.Sum() = 0 AndAlso Not File.Exists(plan.Destination), "disabled Mod aliases fail before network without silently enabling them", passed)
            release.FileName = "cancelled.zip" : plan = CommunityResourceInstallation.Create(release, instance, Nothing, CancellationToken.None)
            Using cancel As New CancellationTokenSource()
                Dim cancelled = False
                Try
                    Await plan.InstallAsync(cache, New ModrinthChecks.Policy(server.Url), New ModrinthChecks.Progress(Sub(value)
                        If value.Stage = "检查已有文件" Then cancel.Cancel()
                    End Sub), cancel.Token)
                Catch ex As OperationCanceledException
                    cancelled = True
                End Try
                Check(cancelled AndAlso Not File.Exists(plan.Destination) AndAlso server.Requests.Values.Sum() = 0, "cancellation at target preflight starts no network or publication", passed)
            End Using
            Check(Await plan.InstallAsync(cache, New ModrinthChecks.Policy(server.Url), Nothing, CancellationToken.None), "cancelled target preflight can retry the same resource and release all locks", passed)
            Dim requests = server.Requests.Values.Sum(), observations As New List(Of TransferProgress)
            Check(Not Await plan.InstallAsync(Path.Combine(base, "cold-cache"), New ModrinthChecks.Policy(server.Url), New ModrinthChecks.Progress(Sub(value) observations.Add(value)), CancellationToken.None) AndAlso server.Requests.Values.Sum() = requests AndAlso observations.Last().Phase = "commit" AndAlso observations.Last().StageFraction = 1, "existing-file reuse completes task progress without network or a downloader cache", passed)
            Using gate As New LocalModActions.OperationGate(instance, Path.GetDirectoryName(plan.Destination), CancellationToken.None, "RESOURCES")
                ' Keep the owning thread until this native mutex is disposed.
                Check(FailsInstallAsync(plan, cache, server).GetAwaiter().GetResult() AndAlso server.Requests.Values.Sum() = requests, "even an identical resource cannot bypass an active shared-directory operation gate", passed)
            End Using
            Put(Path.Combine(instance.RunDirectory, "saves/World/level.dat"), "initial")
            Dim world = CommunityResourceInstallation.Worlds(instance, CancellationToken.None).Single(), dataRelease = ResourceInstallationChecks.Release(CommunityResourceKind.DataPack, bytes, "world.zip")
            dataRelease.Url = server.Url & "/resource.zip"
            plan = CommunityResourceInstallation.Create(dataRelease, instance, world, CancellationToken.None)
            Dim changed = 0, rejected = False
            Try
                Await plan.InstallAsync(cache, New ModrinthChecks.Policy(server.Url), New ModrinthChecks.Progress(Sub(value)
                    If value.Stage = "下载并校验文件" AndAlso Interlocked.CompareExchange(changed, 1, 0) = 0 Then File.WriteAllText(Path.Combine(world.Folder, "level.dat"), "game saved while downloading")
                End Sub), CancellationToken.None)
            Catch ex As InvalidDataException
                rejected = True
            End Try
            Check(rejected AndAlso changed = 1 AndAlso Not File.Exists(plan.Destination) AndAlso File.ReadAllText(Path.Combine(world.Folder, "level.dat")) = "game saved while downloading", "world changes during real acquisition abort datapack publication and preserve the updated world", passed)
            requests = server.Requests.Values.Sum()
            world = CommunityResourceInstallation.Worlds(instance, CancellationToken.None).Single() : plan = CommunityResourceInstallation.Create(dataRelease, instance, world, CancellationToken.None)
            Check(Await plan.InstallAsync(cache, New ModrinthChecks.Policy(server.Url), Nothing, CancellationToken.None) AndAlso server.Requests.Values.Sum() = requests, "reselecting the updated world retries with the verified cache and no additional transfer", passed)
        End Using
    End Function
    Private Async Function FailsInstallAsync(plan As ResourceInstallationPlan, cache As String, server As TestServer) As Task(Of Boolean)
        Try
            Await plan.InstallAsync(cache, New ModrinthChecks.Policy(server.Url), Nothing, CancellationToken.None) : Return False
        Catch ex As IOException
            Return True
        End Try
    End Function
    Private Async Function EdgeAsync(root As String, passed As List(Of String)) As Task
        Dim base = Path.Combine(root, "resource-install/edges"), target = ResourceInstallationChecks.Target(base, "PCL", False), item = Instance(target, "a"), bytes = Encoding.UTF8.GetBytes(New String("x"c, 65536))
        Dim release = ResourceInstallationChecks.Release(CommunityResourceKind.ResourcePack, bytes), source = Path.Combine(base, "resource.zip") : File.WriteAllBytes(source, bytes)
        Dim plan = CommunityResourceInstallation.Create(release, item, Nothing, CancellationToken.None)
        Directory.CreateDirectory(Path.GetDirectoryName(plan.Destination)) : File.WriteAllText(plan.Destination, "foreign owner")
        Check(Reject(Sub() plan.Publish(source, CancellationToken.None)) AndAlso File.ReadAllText(plan.Destination) = "foreign owner", "different existing resource bytes are never overwritten", passed)
        release.FileName = "cancel.zip" : plan = CommunityResourceInstallation.Create(release, item, Nothing, CancellationToken.None)
        Using cancel As New CancellationTokenSource()
            Check(Reject(Sub() plan.Publish(source, cancel.Token, Sub() cancel.Cancel())) AndAlso Not File.Exists(plan.Destination), "cancel before rename leaves no resource publication", passed)
        End Using
        Check(plan.Publish(source, CancellationToken.None), "cancelled selected-instance installation can retry", passed)
        release.FileName = "rollback.zip" : Dim rollback = Path.Combine(Path.GetDirectoryName(plan.Destination), release.FileName)
        Using cancel As New CancellationTokenSource()
            Check(Reject(Sub() CommunityResourceDownload.Publish(release, source, rollback, cancel.Token, item, afterPublish:=Sub() cancel.Cancel())) AndAlso Not File.Exists(rollback), "cancel after rename removes only the native handle owned publication", passed)
        End Using
        Dim configBytes = File.ReadAllBytes(target.ConfigPath)
        release.FileName = "changed.zip" : plan = CommunityResourceInstallation.Create(release, item, Nothing, CancellationToken.None)
        Check(Reject(Sub() plan.Publish(source, CancellationToken.None, Sub() File.AppendAllText(target.ConfigPath, vbLf & "Changed:yes"))) AndAlso Not File.Exists(plan.Destination), "configuration change during transfer aborts publication", passed)
        File.WriteAllBytes(target.ConfigPath, configBytes) : plan = CommunityResourceInstallation.Create(release, item, Nothing, CancellationToken.None)
        Check(plan.Publish(source, CancellationToken.None), "reselecting native configuration permits retry", passed)
        release.FileName = "race.zip" : plan = CommunityResourceInstallation.Create(release, item, Nothing, CancellationToken.None)
        Check(Reject(Sub() plan.Publish(source, CancellationToken.None, Sub() File.WriteAllText(plan.Destination, "foreign"))) AndAlso File.ReadAllText(plan.Destination) = "foreign", "concurrent destination survives publication failure and cleanup", passed)
        Dim other = Instance(target, "b"), modData = ModBytes(), modRelease = ResourceInstallationChecks.Release(CommunityResourceKind.Mod, modData)
        Using gate As New LocalModActions.OperationGate(item, Path.Combine(item.RunDirectory, "mods"), CancellationToken.None)
            Dim otherPlan = CommunityResourceInstallation.Create(modRelease, other, Nothing, CancellationToken.None), modSource = Path.Combine(base, modRelease.FileName)
            File.WriteAllBytes(modSource, modData)
            Check(Reject(Sub() otherPlan.Publish(modSource, CancellationToken.None)), "two instances sharing Mods cannot publish concurrently", passed)
        End Using
        Put(Path.Combine(item.RunDirectory, "saves/world/level.dat"), "first")
        Dim world = CommunityResourceInstallation.Worlds(item, CancellationToken.None).Single(), dataRelease = ResourceInstallationChecks.Release(CommunityResourceKind.DataPack, bytes)
        Check(Reject(Sub() CommunityResourceInstallation.Create(dataRelease, item, Nothing, CancellationToken.None)), "datapack installation requires explicit world selection", passed)
        plan = CommunityResourceInstallation.Create(dataRelease, item, world, CancellationToken.None) : Put(Path.Combine(world.Folder, "level.dat"), "changed")
        Check(Reject(Sub() plan.Publish(source, CancellationToken.None)) AndAlso Not Directory.Exists(Path.Combine(world.Folder, "datapacks")), "changed world marker rejects stale selection before directory creation", passed)
        Dim legacy = Instance(target, "legacy", "1.5.2", False), texture = ResourceInstallationChecks.Release(CommunityResourceKind.ResourcePack, bytes) : texture.GameVersions = {"1.5.2"}
        Check({"1.0", "1.5.2", "a1.2.6", "b1.7.3"}.All(Function(version) CommunityResourceInstallation.UsesLegacyTexturePacks(version)) AndAlso
            {Nothing, "", "1.6.1", "1.16.5", "26.3", "13w24a"}.All(Function(version) Not CommunityResourceInstallation.UsesLegacyTexturePacks(version)),
            "save and install share the existing legacy texture-pack boundary with modern and unknown fallbacks", passed)
        Check(CommunityResourceInstallation.Create(texture, legacy, Nothing, CancellationToken.None).Destination = Path.GetFullPath(Path.Combine(target.Root, "texturepacks/resource.zip")), "legacy instances use texturepacks for explicitly compatible releases", passed)
        Check(CommunityResourceInstallation.CompatibilityProblem(dataRelease, legacy) IsNot Nothing AndAlso CommunityResourceInstallation.CompatibilityProblem(modRelease, legacy) IsNot Nothing, "game-version and loader mismatches reject unsuitable instances", passed)
        Dim customTarget = ResourceInstallationChecks.Target(Path.Combine(base, "missing-custom"), "HMCL-legacy", False, Path.Combine(base, "not-created")), missing = Instance(customTarget, "missing")
        Check(CommunityResourceInstallation.CompatibilityProblem(release, missing) IsNot Nothing AndAlso Not Directory.Exists(missing.RunDirectory), "missing custom run directory is never silently created", passed)
        release.FileName = "replaced.zip" : plan = CommunityResourceInstallation.Create(release, item, Nothing, CancellationToken.None)
        Dim resourceDirectory = Path.GetDirectoryName(plan.Destination), previousDirectory = resourceDirectory & "-previous"
        Directory.Move(resourceDirectory, previousDirectory) : Directory.CreateDirectory(resourceDirectory)
        Check(Reject(Sub() plan.Publish(source, CancellationToken.None)) AndAlso Not File.Exists(plan.Destination), "a physical resource-directory replacement after selection invalidates the frozen target", passed)
        Dim modSourceFile = Path.Combine(base, "resource.jar") : File.WriteAllBytes(modSourceFile, modData)
        plan = CommunityResourceInstallation.Create(modRelease, item, Nothing, CancellationToken.None)
        Directory.CreateDirectory(Path.GetDirectoryName(plan.Destination)) : File.WriteAllText(plan.Destination & ".disabled", "disabled foreign file")
        Check(Reject(Sub() plan.Publish(modSourceFile, CancellationToken.None)) AndAlso Not File.Exists(plan.Destination) AndAlso File.ReadAllText(plan.Destination & ".disabled") = "disabled foreign file", "online Mod installation never re-enables or replaces a disabled filename alias", passed)
        Dim corrupt = ResourceInstallationChecks.Release(CommunityResourceKind.Shader, bytes, "wrong.zip") : corrupt.Sha1 = New String("0"c, 40)
        plan = CommunityResourceInstallation.Create(corrupt, item, Nothing, CancellationToken.None)
        Check(Reject(Sub() plan.Publish(source, CancellationToken.None)) AndAlso Not File.Exists(plan.Destination), "selected resource digest is rechecked under the publication source lease", passed)
        Dim noLoader = Instance(target, "vanilla", "1.20.1", False), wrongLoader = modRelease.Copy() : wrongLoader.Loaders = {"neoforge"}
        Check(CommunityResourceInstallation.CompatibilityProblem(modRelease, noLoader) IsNot Nothing AndAlso CommunityResourceInstallation.CompatibilityProblem(wrongLoader, item) IsNot Nothing, "vanilla and wrong-loader instances cannot accept a declared Fabric Mod", passed)
        Using server As New TestServer()
            release = ResourceInstallationChecks.Release(CommunityResourceKind.Shader, bytes) : server.Files("/resource.zip") = bytes : release.Url = server.Url & "/resource.zip"
            plan = CommunityResourceInstallation.Create(release, item, Nothing, CancellationToken.None)
            Dim cache = Path.Combine(base, "cache"), policy As New ModrinthChecks.Policy(server.Url)
            Check(Await plan.InstallAsync(cache, policy, Nothing, CancellationToken.None), "real acquisition, digest verification and instance publication complete on loopback", passed)
            Dim requests = server.Requests.Values.Sum()
            Check(Not Await plan.InstallAsync(cache, policy, Nothing, CancellationToken.None) AndAlso server.Requests.Values.Sum() = requests, "selected-instance retry reuses verified downloader cache", passed)
        End Using
        Check(Not Directory.EnumerateFiles(target.Root, ".pcldb-resource-*.tmp", SearchOption.AllDirectories).Any(), "failed installations leave no owned resource temporary files", passed)
    End Function
End Module
