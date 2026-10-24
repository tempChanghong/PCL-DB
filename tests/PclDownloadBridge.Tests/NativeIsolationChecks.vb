Imports System.IO
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

' Integration fixtures exercise the real downloader/publication on loopback.
' These are not evidence of launching Minecraft in a real external launcher.
Friend Module NativeIsolationChecks
    Private Sub Check(value As Boolean, label As String, passed As List(Of String))
        If Not value Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub
    Private Sub Put(full As String, text As String)
        Directory.CreateDirectory(Path.GetDirectoryName(full)) : File.WriteAllText(full, text, New UTF8Encoding(False))
    End Sub
    Private Function Target(session As IsolationResourceChecks.Session, kind As String, isolated As Boolean, Optional custom As String = Nothing) As LauncherInstallTarget
        Dim work = session.Root & "-" & kind, config As String
        If kind = "CE" Then
            config = Path.Combine(work, "PCL/config.v1.yml")
            Put(config, "LaunchArgumentIndieV2: " & If(isolated, "4", "0") & vbLf & "OtherValue: retained")
        ElseIf kind = "HMCL" Then
            config = Path.Combine(work, ".hmcl/config/game-directories.json")
            Put(config, New JObject From {{"directories", New JArray(New JObject From {{"path", session.Root}, {"legacyGameSettings", "preset"}})}}.ToString())
            Put(Path.Combine(Path.GetDirectoryName(config), "launcher-settings.json"), "{""defaultGameSettingsPreset"":""preset""}")
            Put(Path.Combine(Path.GetDirectoryName(config), "game-settings.json"), New JObject From {{"presets", New JArray(New JObject From {{"id", "preset"}, {"defaultIsolationType", If(isolated, "ALWAYS", "NEVER")}, {"runningDirectory", If(custom, "")}, {"maxMemory", 4096}})}}.ToString())
        Else
            config = Path.Combine(work, "hmcl.json")
            Put(config, New JObject From {{"last", "unrelated"}, {"configurations", New JObject From {{"selected-root", New JObject From {{"gameDir", session.Root}, {"global", New JObject From {{"gameDirType", If(isolated, 1, If(custom Is Nothing, 0, 2))}, {"gameDir", If(custom, "")}, {"maxMemory", 4096}, {"unknown", "keep"}}}}}, {"unrelated", New JObject From {{"gameDir", session.Root & "-other"}, {"global", New JObject From {{"gameDirType", 1}}}}}}}}.ToString())
        End If
        Return New LauncherInstallTarget(New GameFolder With {.Location = session.Root, .LauncherKind = If(kind = "CE", ExternalLauncherKind.PclCe, ExternalLauncherKind.Hmcl), .LauncherWorkspace = work, .LauncherConfigPath = config, .LauncherSharedConfigPath = Path.Combine(work, "shared/user-game-directories.json")}, session.PclBase)
    End Function
    Private Function NativePath(target As LauncherInstallTarget, Optional name As String = "legacy") As String
        Dim relative = If(target.Kind = ExternalLauncherKind.PclCe, "PCL/config.v1.yml", If(Path.GetFileName(target.ConfigPath) = "hmcl.json", "hmclversion.cfg", ".hmcl/config/instance-game-settings.json"))
        Return Path.Combine(target.Root, "versions", name, relative)
    End Function
    Private Function Snapshot(work As String) As Dictionary(Of String, Byte())
        Return Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories).ToDictionary(Function(full) full, Function(full) File.ReadAllBytes(full))
    End Function
    Private Function Unchanged(before As Dictionary(Of String, Byte()), work As String) As Boolean
        Return before.Count = Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories).Count() AndAlso before.All(Function(pair) File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value))
    End Function
    Friend Async Function RunAsync(root As String) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        For Each kind In {"CE", "HMCL", "HMCL-legacy"}
            For Each isolated In {False, True}
                For Each withComponents In {False, True}
                    Dim label = kind & "-" & isolated & "-" & withComponents
                    Using session As New IsolationResourceChecks.Session(root, "native-" & label)
                        Dim launcher = Target(session, kind, isolated), before = Snapshot(launcher.Workspace), selection = If(withComponents, IsolationModeChecks.Components(session), Nothing)
                        Await session.StartAsync(components:=selection, launcherTarget:=launcher)
                        Check(File.Exists(NativePath(launcher)) AndAlso File.Exists(session.Json) AndAlso File.Exists(session.Jar), "native config and complete instance publish together: " & label, passed)
                        Check(File.Exists(session.Resource(isolated)) AndAlso Not File.Exists(session.Resource(Not isolated)), "native isolation chooses the correct legacy resources directory: " & label, passed)
                        If withComponents Then
                            Check(File.Exists(Path.Combine(If(isolated, Path.GetDirectoryName(session.Json), session.Root), "mods/fabric-api.jar")) AndAlso Not File.Exists(Path.Combine(If(isolated, session.Root, Path.GetDirectoryName(session.Json)), "mods/fabric-api.jar")), "component Mods follow native isolation without duplication: " & label, passed)
                        Else
                            Dim config = NativePath(launcher), bytes = File.ReadAllBytes(config), json = File.ReadAllBytes(session.Json)
                            session.Put(session.Resource(isolated), session.Bad) : session.Put(session.Resource(Not isolated), session.Bad)
                            File.SetAttributes(config, FileAttributes.ReadOnly)
                            Try
                                Await session.StartAsync(repair:=True, isolation:=If(isolated, InstanceIsolationMode.Disabled, InstanceIsolationMode.Enabled), newInstanceIsolationStrategy:=If(isolated, 0, 4), launcherTarget:=launcher)
                                Check(File.ReadAllBytes(config).SequenceEqual(bytes) AndAlso File.ReadAllBytes(session.Json).SequenceEqual(json), "repair ignores new-install preferences and preserves read-only native config/manifest bytes: " & label, passed)
                                Check(Not File.ReadAllBytes(session.Resource(isolated)).SequenceEqual(session.Bad) AndAlso File.ReadAllBytes(session.Resource(Not isolated)).SequenceEqual(session.Bad), "repair updates only the existing native run directory: " & label, passed)
                            Finally
                                File.SetAttributes(config, FileAttributes.Normal)
                            End Try
                        End If
                        Check(Unchanged(before, launcher.Workspace) AndAlso session.Released(), "external settings stay read-only and task locks release: " & label, passed)
                    End Using
                Next
            Next
            Await FailuresAsync(root, kind, passed)
            For Each packFormat In {"MR", "CF"}
                Await PackAsync(root, kind, packFormat, passed)
            Next
        Next
        Await HmclPatchAsync(root, passed)
        ResolverChecks(root, passed)
        EdgeChecks(root, passed)
        Return passed
    End Function
    Private Async Function FailuresAsync(root As String, kind As String, passed As List(Of String)) As Task
        Using session As New IsolationResourceChecks.Session(root, "native-retry-" & kind), cancel As New CancellationTokenSource()
            Dim launcher = Target(session, kind, False), before = Snapshot(launcher.Workspace), selected = IsolationModeChecks.Components(session), cancelled = False
            Try
                Await session.StartAsync(components:=selected, launcherTarget:=launcher, token:=cancel.Token, progress:=New ModrinthChecks.Progress(Sub(value)
                    If value.Stage = "完成安装" Then cancel.Cancel()
                End Sub))
            Catch ex As OperationCanceledException
                cancelled = True
            End Try
            Check(cancelled AndAlso Not File.Exists(session.Json) AndAlso Not Directory.Exists(Path.Combine(session.Root, "mods")) AndAlso Not File.Exists(session.Resource(False)) AndAlso session.Released(), "cancellation rolls back shared Mods/resources and native config before retry: " & kind, passed)
            Await session.StartAsync(components:=selected, launcherTarget:=launcher)
            Check(File.Exists(session.Json) AndAlso Unchanged(before, launcher.Workspace) AndAlso session.Released(), "cancelled native task retries with verified cache and unchanged external settings: " & kind, passed)
        End Using
        Using session As New IsolationResourceChecks.Session(root, "native-conflict-" & kind)
            Dim launcher = Target(session, kind, False), selected = IsolationModeChecks.Components(session), modFile = Path.Combine(session.Root, "mods/fabric-api.jar"), rejected = False
            session.Put(modFile, session.Bad)
            Try
                Await session.StartAsync(components:=selected, launcherTarget:=launcher)
            Catch ex As IOException
                rejected = True
            End Try
            Check(rejected AndAlso File.ReadAllBytes(modFile).SequenceEqual(session.Bad) AndAlso Not File.Exists(session.Json) AndAlso session.Released(), "native shared Mods conflict preserves existing files and publishes no instance: " & kind, passed)
        End Using
        Using session As New IsolationResourceChecks.Session(root, "native-changed-" & kind)
            Dim launcher = Target(session, kind, True), rejected = False
            Try
                Await session.StartAsync(launcherTarget:=launcher, progress:=New ModrinthChecks.Progress(Sub(value)
                    If value.Stage = "完成安装" Then File.AppendAllText(launcher.ConfigPath, " ")
                End Sub))
            Catch ex As InvalidDataException
                rejected = True
            End Try
            Check(rejected AndAlso Not File.Exists(session.Json) AndAlso Not File.Exists(session.Resource(True)) AndAlso session.Released(), "configuration changed after planning aborts native publication: " & kind, passed)
            Await session.StartAsync(launcherTarget:=launcher)
            Check(File.Exists(session.Json) AndAlso session.Released(), "native configuration change can be retried after re-reading: " & kind, passed)
        End Using
    End Function
    Private Async Function PackAsync(root As String, kind As String, format As String, passed As List(Of String)) As Task
        Using session As New IsolationResourceChecks.Session(root, "native-pack-" & kind & "-" & format)
            Dim launcher = Target(session, kind, False), before = Snapshot(launcher.Workspace), fixture = session.Fixture
            ModrinthChecks.Configure(fixture)
            Dim entries As New Dictionary(Of String, String) From {{"overrides/config/user.txt", "preserved pack config"}, {"overrides/mods/pack.jar", "pack mod"}}
            If kind = "CE" Then
                entries("overrides/PCL/config.v1.yml") = "VersionArgumentIndieV2: false" & vbLf & "UserSetting: retained"
            ElseIf kind = "HMCL" Then
                entries("overrides/.hmcl/config/instance-game-settings.json") = "{""runningDirectory"":""ignored"",""overrideProperties"":[""maxMemory""],""maxMemory"":8192}"
            Else
                entries("overrides/hmclversion.cfg") = "{""usesGlobal"":true,""gameDirType"":0,""maxMemory"":8192}"
            End If
            Dim result As InstallResult, archivePath As String
            If format = "MR" Then
                Dim manifest As New JObject From {{"formatVersion", 1}, {"game", "minecraft"}, {"versionId", "fixture"}, {"name", "native pack"}, {"dependencies", New JObject From {{"minecraft", "1.20.1"}, {"fabric-loader", "0.16.9"}}}, {"files", New JArray()}}
                archivePath = ModrinthChecks.Pack(root, Guid.NewGuid().ToString("N"), manifest, entries)
                Dim package = ModrinthPackage.Read(archivePath)
                result = Await New ModrinthInstaller(New ModrinthChecks.Policy(fixture.Server.Url), launcherTarget:=launcher).InstallAsync(package, fixture.Root, "pack", {}, {}, Nothing, CancellationToken.None)
            Else
                Dim manifest = CurseForgeChecks.Manifest() : manifest("files") = New JArray()
                archivePath = CurseForgeChecks.Pack(root, Guid.NewGuid().ToString("N"), manifest, entries)
                Dim package = CurseForgePackage.Read(archivePath)
                result = Await New CurseForgeInstaller(New ModrinthChecks.Policy(fixture.Server.Url), launcherTarget:=launcher).InstallAsync(package, fixture.Root, "pack", {}, {}, Nothing, CancellationToken.None)
            End If
            Check(File.Exists(Path.Combine(result.InstancePath, "mods/pack.jar")) AndAlso Not File.Exists(Path.Combine(session.Root, "mods/pack.jar")) AndAlso File.ReadAllText(Path.Combine(result.InstancePath, "config/user.txt")) = "preserved pack config", "pack overrides and Mods always remain isolated: " & kind & "-" & format, passed)
            Dim settings = File.ReadAllText(NativePath(launcher, "pack"))
            Check(If(kind = "CE", settings.Contains("VersionArgumentIndieV2: true") AndAlso settings.Contains("UserSetting: retained"), settings.Contains("8192") AndAlso If(kind = "HMCL", CStr(JObject.Parse(settings)("runningDirectory")) = "", CInt(JObject.Parse(settings)("gameDirType")) = 1)), "pack native settings force isolation while retaining other instance preferences: " & kind & "-" & format, passed)
            Check(Unchanged(before, launcher.Workspace) AndAlso session.Released(), "pack native isolation does not modify external launcher settings: " & kind & "-" & format, passed)
            Dim archive = File.ReadAllBytes(archivePath), fileName = "native" & If(format = "MR", ".mrpack", ".zip")
            fixture.Server.Files("/" & fileName) = archive
            Dim release As New ModpackRelease With {.Platform = If(format = "MR", ModpackPlatform.Modrinth, ModpackPlatform.CurseForge), .ProjectId = "fixture", .Id = "native-version", .Name = "native archive", .FileName = fileName,
                .Url = fixture.Server.Url & "/" & fileName, .Size = archive.Length, .Sha1 = ModrinthChecks.Hash(archive), .Sha512 = ModrinthChecks.Hash(archive, True), .GameVersions = {"1.20.1"}, .Loaders = {"Fabric"}}
            Dim online = Await ModpackArchiveInstaller.InstallAsync(release, fixture.Root, "online", New ModrinthChecks.Policy(fixture.Server.Url), Nothing, {}, Nothing, CancellationToken.None, launcherTarget:=launcher)
            Check(File.Exists(NativePath(launcher, "online")) AndAlso File.Exists(Path.Combine(online.InstancePath, "mods/pack.jar")) AndAlso Unchanged(before, launcher.Workspace), "verified online archive handoff retains native target through the common importer: " & kind & "-" & format, passed)
        End Using
    End Function
    Private Async Function HmclPatchAsync(root As String, passed As List(Of String)) As Task
        Using session As New IsolationResourceChecks.Session(root, "native-hmcl-patch")
            Dim launcher = Target(session, "HMCL", True)
            Await session.StartAsync(launcherTarget:=launcher)
            Dim game = JObject.Parse(File.ReadAllText(session.Json)) : game("id") = "game" : game("version") = session.Fixture.Entry.Id
            Put(session.Json, New JObject From {{"id", "legacy"}, {"patches", New JArray(game)}, {"userField", "preserve"}}.ToString())
            Dim bytes = File.ReadAllBytes(session.Json) : File.Delete(session.Jar)
            Await session.StartAsync(repair:=True, launcherTarget:=launcher)
            Check(File.Exists(session.Jar) AndAlso bytes.SequenceEqual(File.ReadAllBytes(session.Json)), "HMCL single-game-patch manifest repairs without flattening or rewriting", passed)
            DirectCast(game("libraries"), JArray).Add(New JObject From {{"name", "unknown:modified:1"}})
            Put(session.Json, New JObject From {{"patches", New JArray(game)}}.ToString())
            Dim rejected = False
            Try
                Await session.StartAsync(repair:=True, launcherTarget:=launcher)
            Catch ex As InvalidDataException
                rejected = True
            End Try
            Check(rejected AndAlso session.Released(), "HMCL modified game patch cannot bypass vanilla repair validation", passed)
        End Using
    End Function
    Private Sub ResolverChecks(root As String, passed As List(Of String))
        For Each kind In {"CE", "HMCL", "HMCL-legacy"}
            Using session As New IsolationResourceChecks.Session(root, "native-rules-" & kind)
                Dim launcher = Target(session, kind, False), meta = JObject.Parse(Encoding.UTF8.GetString(session.Fixture.Server.Files("/version")))
                For strategy = 0 To 4
                    For Each modable In {False, True}
                        Dim plan = NativeInstanceIsolation.Resolve(launcher, "legacy", session.Fixture.Entry, meta, False, modable, InstanceIsolationMode.FollowPcl, modable, strategy)
                        Check(plan.Isolated = (strategy = 4 OrElse (modable AndAlso (strategy = 1 OrElse strategy = 3))), "explicit global strategy follows native target without changing external defaults: " & kind & "-" & strategy & "-" & modable, passed)
                    Next
                Next
            End Using
        Next
        For Each kind In {"HMCL", "HMCL-legacy"}
            Using session As New IsolationResourceChecks.Session(root, "native-custom-" & kind)
                Dim custom = session.Root & "-custom", launcher = Target(session, kind, False, custom), meta = JObject.Parse(Encoding.UTF8.GetString(session.Fixture.Server.Files("/version")))
                Dim plan = NativeInstanceIsolation.Resolve(launcher, "legacy", session.Fixture.Entry, meta, True, False, InstanceIsolationMode.Enabled, False, 4), rejected = False
                plan.ValidateDataLocation(False)
                Try
                    plan.ValidateDataLocation(True)
                Catch ex As NotSupportedException
                    rejected = True
                End Try
                Check(plan.RunDirectory = custom AndAlso rejected AndAlso Not Directory.Exists(custom), "custom run directory is preserved and writes outside the atomic transaction are refused: " & kind, passed)
            End Using
        Next
    End Sub
    Private Sub EdgeChecks(root As String, passed As List(Of String))
        For Each kind In {"CE", "HMCL", "HMCL-legacy"}
            Using session As New IsolationResourceChecks.Session(root, "native-edge-" & kind)
                Dim launcher = Target(session, kind, False), meta = JObject.Parse(Encoding.UTF8.GetString(session.Fixture.Server.Files("/version")))
                Dim plan = NativeInstanceIsolation.Resolve(launcher, "legacy", session.Fixture.Entry, meta, True, False, InstanceIsolationMode.FollowPcl, False, Nothing)
                Dim config = NativePath(launcher), changed = False
                Put(config, If(kind = "CE", "VersionArgumentIndieV2: true", "{}"))
                Try
                    Using guard = plan.LockForRepair()
                    End Using
                Catch ex As InvalidDataException
                    changed = True
                End Try
                Check(changed, "a previously absent native instance config appearing before commit invalidates the plan: " & kind, passed)
                File.Delete(config)
                plan = NativeInstanceIsolation.Resolve(launcher, "legacy", session.Fixture.Entry, meta, False, False, InstanceIsolationMode.Enabled, False, Nothing)
                Using guard = plan.LockForRepair()
                    Dim blocked = False
                    Try
                        File.AppendAllText(launcher.ConfigPath, " ")
                    Catch ex As IOException
                        blocked = True
                    End Try
                    Check(blocked, "native input byte lock blocks concurrent external config edits during publication: " & kind, passed)
                End Using
                Dim stage = Path.Combine(session.Root, "stage")
                plan.WriteNewSettings(stage)
                Dim native = Path.Combine(stage, plan.WrittenPaths.First())
                File.AppendAllText(native, " ") : changed = False
                Try
                    plan.ValidateStaged(stage, CancellationToken.None)
                Catch ex As InvalidDataException
                    changed = True
                End Try
                Check(changed, "native staged config mutation is detected before atomic rename: " & kind, passed)
            End Using
        Next
        Using session As New IsolationResourceChecks.Session(root, "native-ce-fallback")
            Dim launcher = Target(session, "CE", True), meta = JObject.Parse(Encoding.UTF8.GetString(session.Fixture.Server.Files("/version"))), config = NativePath(launcher)
            Put(Path.ChangeExtension(config, ".json"), "{""VersionArgumentIndieV2"":false,""UnknownPreference"":""keep""}")
            Dim plan = NativeInstanceIsolation.Resolve(launcher, "legacy", session.Fixture.Entry, meta, True, False, InstanceIsolationMode.Enabled, False, 4)
            Check(Not plan.Isolated, "CE existing JSON fallback takes precedence over defaults and new-install overrides", passed)
            File.Delete(Path.ChangeExtension(config, ".json")) : Put(session.Ini, "VersionArgumentIndie:2" & vbCrLf & "Unknown:keep")
            plan = NativeInstanceIsolation.Resolve(launcher, "legacy", session.Fixture.Entry, meta, True, False, InstanceIsolationMode.Enabled, False, 4)
            Check(Not plan.Isolated, "CE existing legacy INI is read without migration", passed)
            File.Delete(session.Ini) : session.Put(Path.Combine(Path.GetDirectoryName(session.Json), "mods/user.jar"), session.Bad)
            plan = NativeInstanceIsolation.Resolve(launcher, "legacy", session.Fixture.Entry, meta, True, False, InstanceIsolationMode.FollowPcl, False, Nothing)
            Check(plan.Isolated, "CE uninitialized instance preserves the upstream Mods/saves inference", passed)
            Dim stage = Path.Combine(session.Root, "json-stage")
            Put(Path.Combine(stage, "PCL/config.v1.json"), "{""VersionArgumentIndieV2"":false,""UnknownPreference"":{""nested"":true}}")
            plan.WriteNewSettings(stage)
            Check(Not File.Exists(Path.Combine(stage, "PCL/config.v1.yml")) AndAlso CBool(JObject.Parse(File.ReadAllText(Path.Combine(stage, "PCL/config.v1.json")))("UnknownPreference")("nested")), "CE pack JSON is merged in place so unrelated effective settings are not hidden by a new YAML file", passed)
            Put(config, "'VersionArgumentIndieV2': false")
            Dim rejected = False
            Try
                NativeInstanceIsolation.Resolve(launcher, "legacy", session.Fixture.Entry, meta, True, False, InstanceIsolationMode.FollowPcl, False, Nothing)
            Catch ex As InvalidDataException
                rejected = True
            End Try
            Check(rejected, "unsupported CE YAML isolation key cannot silently fall back to another data directory", passed)
        End Using
        Using session As New IsolationResourceChecks.Session(root, "native-modern-inherit")
            Dim launcher = Target(session, "HMCL", True), meta = JObject.Parse(Encoding.UTF8.GetString(session.Fixture.Server.Files("/version"))), config = NativePath(launcher)
            Dim resolve As Func(Of NativeInstanceIsolation) = Function() NativeInstanceIsolation.Resolve(launcher, "legacy", session.Fixture.Entry, meta, True, False, InstanceIsolationMode.FollowPcl, False, Nothing)
            Put(config, New JObject From {{"parent", "preset"}, {"overrideProperties", New JArray()}, {"runningDirectory", "ignored-local"}, {"unknown", True}}.ToString())
            Check(Not resolve().Isolated, "HMCL inherited running directory ignores a dormant local value and installation-only ALWAYS default", passed)
            Put(config, New JObject From {{"overrideProperties", New JArray("runningDirectory")}, {"runningDirectory", ""}}.ToString())
            Check(resolve().Isolated, "HMCL blank explicitly overridden running directory means instance root", passed)
            File.Delete(config) : Put(Path.Combine(Path.GetDirectoryName(session.Json), "hmclversion.cfg"), "{""usesGlobal"":true}")
            Check(resolve().Isolated, "HMCL lazy legacy usesGlobal migration retains its directory-specific ALWAYS isolation", passed)
            Dim directories = JObject.Parse(File.ReadAllText(launcher.ConfigPath))
            directories("directories")(0)("legacyGameSettings") = "missing-preset" : Put(launcher.ConfigPath, directories.ToString())
            Check(Not resolve().Isolated, "HMCL missing historical preset cannot accidentally inherit installation-only ALWAYS isolation from another preset", passed)
            directories("directories")(0)("legacyGameSettings") = "preset" : Put(launcher.ConfigPath, directories.ToString())
            Put(Path.Combine(Path.GetDirectoryName(session.Json), "hmclversion.cfg"), "{""usesGlobal"":false,""gameDirType"":2,""gameDir"":""""}")
            Check(resolve().Isolated, "HMCL migrated CUSTOM with blank path is an explicit instance-root override", passed)
            Put(Path.Combine(Path.GetDirectoryName(session.Json), "hmclversion.cfg"), "{""usesGlobal"":false,""gameDirType"":0}")
            Check(Not resolve().Isolated, "HMCL explicit legacy ROOT stays shared when the preset defaults to ALWAYS", passed)
            Put(Path.Combine(Path.GetDirectoryName(session.Json), "modpack.cfg"), "{}")
            Check(resolve().Isolated, "HMCL existing modpack marker forces native isolation regardless of legacy ROOT preference", passed)
        End Using
    End Sub
End Module
