Imports System.IO
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module ExternalLauncherChecks
    Friend Async Function RunAsync(testRoot As String) As Task(Of List(Of String))
        Dim passed As New List(Of String)
        Dim root = Path.Combine(testRoot, "external-launchers-" & Guid.NewGuid().ToString("N"))
        Dim pcl = Path.Combine(root, "PCL work"), hmcl = Path.Combine(root, "HMCL work"), ce = Path.Combine(root, "CE work"), game = Path.Combine(root, "共同游戏目录")
        For Each work In {pcl, hmcl, ce, game}
            Directory.CreateDirectory(work)
        Next
        Dim write As Action(Of String, String) = Sub(filePath, text)
                                                    Directory.CreateDirectory(Path.GetDirectoryName(filePath)) : File.WriteAllText(filePath, text, New UTF8Encoding(False))
                                                End Sub
        Dim check As Action(Of Boolean, String) = Sub(ok, label)
                                                     If Not ok Then Throw New InvalidOperationException(label)
                                                     passed.Add(label)
                                                 End Sub
        Dim reader As New LauncherDirectoryReader
        Dim p As New ExternalLauncherBinding With {.Kind = ExternalLauncherKind.Pcl, .Workspace = pcl}
        write(Path.Combine(pcl, "PCL/Setup.ini"), "LaunchFolderSelect:" & game)
        Dim h As New ExternalLauncherBinding With {.Kind = ExternalLauncherKind.Hmcl, .Workspace = hmcl}
        Dim oldHmcl As New JObject From {{"last", "生存"}, {"configurations", New JObject From {{"生存", New JObject From {{"gameDir", "../共同游戏目录"}}}}}}
        write(Path.Combine(hmcl, "hmcl.json"), oldHmcl.ToString())
        Dim c As New ExternalLauncherBinding With {.Kind = ExternalLauncherKind.PclCe, .Workspace = ce, .SharedConfigPath = Path.Combine(root, "shared/config.v1.json")}
        write(Path.Combine(ce, "PCL/config.v1.yml"), "LaunchFolderSelect: " & JToken.FromObject(game).ToString(Newtonsoft.Json.Formatting.None))
        write(c.SharedConfigPath, New JObject From {{"LaunchFolders", "共享目录>" & game & "|失效目录>" & Path.Combine(root, "missing")}}.ToString())
        Dim original = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(Function(path) path, Function(path) File.ReadAllBytes(path))
        Dim found As New List(Of GameFolder)
        For Each binding In {p, h, c}
            Dim result = reader.Read(binding, False)
            Dim folder = result.Folders.SingleOrDefault(Function(item) item.Location = game)
            check(result.Succeeded AndAlso folder IsNot Nothing AndAlso folder.IsSelected, "read selected shared root from " & binding.Kind.ToString())
            check(folder.LauncherBindingId = binding.Id AndAlso folder.LauncherKind.GetValueOrDefault() = binding.Kind AndAlso folder.LauncherWorkspace = binding.Workspace, "directory preserves launcher identity and workspace for " & binding.Kind.ToString())
            found.Add(folder)
        Next
        check(found.Select(Function(folder) folder.LauncherBindingId).Distinct().Count() = 3 AndAlso found.Select(Function(folder) folder.Location).Distinct().Count() = 1, "one physical root retains three distinct launcher contexts")
        check(original.All(Function(item) item.Value.SequenceEqual(File.ReadAllBytes(item.Key))) AndAlso Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length = original.Count, "discovery preserves every external configuration byte and creates no files")
        Dim snapshot = New LauncherInstallTarget(found(0), "wrong fallback")
        found(0).Location = root : found(0).LauncherWorkspace = hmcl : p.Workspace = hmcl
        check(snapshot.Root = game AndAlso snapshot.PclDirectory = pcl, "immutable task context survives mutation of discovered folders and source bindings")
        Dim mismatched As New ExternalLauncherBinding With {.Kind = ExternalLauncherKind.Pcl, .Workspace = pcl, .ConfigPath = Path.Combine(hmcl, "hmcl.json")}
        check(Not reader.Read(mismatched, False).Succeeded, "PCL config from a different workspace is rejected instead of mixing directory and isolation sources")
        mismatched.ConfigPath = Path.Combine(pcl, "PCL/Setup.ini")
        check(reader.Read(mismatched, False).Succeeded, "explicit PCL config matches the workspace with normalized directory separators")

        Dim newConfig = Path.Combine(hmcl, ".hmcl/config/game-directories.json")
        write(newConfig, New JObject From {{"directories", New JArray(New JObject From {{"id", "first"}, {"path", "../共同游戏目录"}, {"name", "新目录"}}, New JObject From {{"id", "duplicate"}, {"path", game.ToUpperInvariant()}}, New JObject From {{"path", "C:relative"}})}}.ToString())
        write(Path.Combine(hmcl, ".hmcl/config/launcher-settings.json"), "{""selectedGameDirectory"":""first""}")
        Dim current = reader.Read(h, False)
        check(current.Succeeded AndAlso current.Folders.Count = 1 AndAlso current.Folders(0).Name = "新目录" AndAlso current.Folders(0).IsSelected, "new HMCL directory JSON wins over stale legacy config and resolves paths against workspace")
        check(current.Warnings.Count > 0, "invalid HMCL neighbor path produces a warning without losing a valid root")
        h.SharedConfigPath = Path.Combine(root, "user-game-directories.json")
        write(h.SharedConfigPath, "{""directories"":[{""id"":""global"",""path"":" + JToken.FromObject(ce).ToString(Newtonsoft.Json.Formatting.None) + "}]}")
        check(reader.Read(h, False).Folders.Count = 2, "explicit HMCL user store merges into its bound workspace")
        Dim sharedFile = h.SharedConfigPath : h.SharedConfigPath = Path.Combine(root, "missing-shared.json")
        Dim missingShared = reader.Read(h, False)
        check(missingShared.Succeeded AndAlso missingShared.Folders.Single().Location = game AndAlso missingShared.Warnings.Any(Function(value) value.Contains("共享")), "a missing explicit shared store reports fallback while retaining a valid local root")
        h.SharedConfigPath = sharedFile
        For Each malformed In {"{", "{""directories"":{}}", "[]"}
            write(newConfig, malformed)
            check(Not reader.Read(h, False).Succeeded, "malformed primary directory store cannot become a successful empty refresh: " & malformed)
        Next
        h.ConfigPath = Path.Combine(hmcl, "hmcl.json")
        check(reader.Read(h, False).Succeeded, "explicit legacy HMCL binding remains independent from a broken automatic candidate")
        h.ConfigPath = newConfig
        Dim collision = Path.Combine(root, "shadowed-global") : Directory.CreateDirectory(collision)
        write(newConfig, New JObject From {{"$schema", "https://schemas.glavo.site/hmcl/game-directories/1.1.0"}, {"directories", New JArray(New JObject From {{"id", "first"}, {"path", game}})}}.ToString())
        write(h.SharedConfigPath, New JObject From {{"directories", New JArray(New JObject From {{"id", "first"}, {"path", collision}}, New JObject From {{"id", "global"}, {"path", ce}})}}.ToString())
        write(Path.Combine(hmcl, ".hmcl/config/launcher-settings.json"), "{""selectedGameDirectory"":""global""}")
        Dim merged = reader.Read(h, False)
        check(merged.Succeeded AndAlso merged.Folders.Single(Function(folder) folder.IsSelected).Location = ce, "HMCL workspace selection also identifies a directory from the user store")
        check(merged.Folders.Count = 2 AndAlso Not merged.Folders.Any(Function(folder) folder.Location = collision), "HMCL local directory IDs take precedence over conflicting user records")
        write(newConfig, "{""$schema"":""https://schemas.glavo.site/hmcl/game-directories/2.0.0"",""directories"":[]}")
        check(Not reader.Read(h, False).Succeeded, "unknown HMCL schema major fails without migrating the external file")
        For Each scalar In {"'" & game.Replace("'", "''") & "'", game & " # note", JToken.FromObject(game).ToString(Newtonsoft.Json.Formatting.None)}
            write(Path.Combine(ce, "PCL/config.v1.yml"), "LaunchFolderSelect: " & scalar)
            check(reader.Read(c, False).Succeeded AndAlso reader.Read(c, False).Folders.Any(Function(folder) folder.IsSelected AndAlso folder.Location = game), "CE YAML scalar resolves safely: " & scalar)
        Next
        For Each yaml In {"LaunchFolderSelect: *alias", "LaunchFolderSelect: |" & vbLf & "  " & game, "LaunchFolderSelect: " & game & vbLf & "LaunchFolderSelect: " & ce, "LaunchFolderSelect: ""unfinished", "LocalFileVersion: 2" & vbLf & "LaunchFolderSelect: " & game}
            write(Path.Combine(ce, "PCL/config.v1.yml"), yaml)
            check(Not reader.Read(c, False).Succeeded, "unsupported or ambiguous YAML fails without guessing a root")
        Next
        c.ConfigPath = Path.Combine(ce, "PCL/legacy.ini") : write(c.ConfigPath, "LaunchFolderSelect:" & game)
        check(reader.Read(c, False).Succeeded AndAlso reader.Read(c, False).Folders.Single().IsSelected, "explicit CE legacy configuration supports old installations without migration")
        For Each invalid In {"C:relative", "\root-relative", "https://example.org", ChrW(0).ToString()}
            Dim failed = False
            Try
                LauncherDirectoryReader.AbsolutePath(invalid, ce)
            Catch ex As Exception When TypeOf ex Is InvalidDataException OrElse TypeOf ex Is IOException
                failed = True
            End Try
            check(failed, "reject ambiguous or non-filesystem target path")
        Next
        check(LauncherDirectoryReader.AbsolutePath(Path.GetPathRoot(root), Nothing) = Path.GetPathRoot(root), "drive root never becomes a drive-relative path")
        write(c.ConfigPath, New String("a"c, 2 * 1024 * 1024 + 1))
        check(Not reader.Read(c, False).Succeeded, "oversized external files are bounded before parsing")

        Dim legacy As New JObject From {{"pclBase", pcl}, {"gameRoot", game}, {"appearance", New JObject From {{"theme", "blue"}}}, {"unknown", "keep"}}
        Dim state = LauncherBindingsState.Restore(legacy, root)
        check(state.Bindings.Single().Workspace = pcl AndAlso state.TargetRoot = game AndAlso state.TargetBindingId = "legacy-pcl", "legacy PCL preferences migrate to a bound target without changing their directory")
        state.Bindings.Add(h) : state.Bindings.Add(c) : state.SelectedRows(h.Kind) = h.Id : state.TargetBindingId = h.Id
        Dim json = DirectCast(state.ToJson(), JObject)
        json("futureField") = "keep" : json("bindings")(h.Id)("futureBindingField") = 7
        Dim restored = LauncherBindingsState.Restore(New JObject From {{"externalLaunchers", json}}, root)
        check(restored.Bindings.Count = 3 AndAlso restored.TargetBindingId = h.Id AndAlso restored.SelectedRows(h.Kind) = h.Id, "multiple launchers and independent selected rows round trip")
        check(CStr(restored.ToJson()("futureField")) = "keep" AndAlso CInt(restored.ToJson()("bindings")(h.Id)("futureBindingField")) = 7, "recognized settings retain unknown fields on save")
        json("bindings")("unsupported") = New JObject From {{"kind", "future-launcher"}, {"workspace", hmcl}}
        Dim mixed = LauncherBindingsState.Restore(New JObject From {{"externalLaunchers", json}}, root)
        check(mixed.Bindings.Count = 3 AndAlso mixed.ToJson()("bindings")("unsupported") IsNot Nothing, "an unrecognized binding record remains preserved beside usable records")
        For Each raw As JToken In New JToken() {New JValue("future"), New JObject From {{"format", 999}, {"bindings", New JObject()}}}
            Dim future = LauncherBindingsState.Restore(New JObject From {{"externalLaunchers", raw}}, root)
            check(future.ReadOnlyFormat AndAlso JToken.DeepEquals(raw, future.ToJson()), "unrecognized preference format is preserved instead of overwritten")
        Next
        For Each folder In found.Where(Function(item) item.LauncherKind.GetValueOrDefault() <> ExternalLauncherKind.Pcl)
            Dim target As New LauncherInstallTarget(folder, pcl)
            check(target.PclDirectory Is Nothing, "foreign target never inherits a PCL configuration source")
            For Each flags In New Boolean()() {New Boolean() {True, False, False, False}, New Boolean() {False, True, False, False}, New Boolean() {False, False, True, False}, New Boolean() {False, False, False, True}}
                Dim blocked = False
                Try
                    target.ValidateFirstBatch(flags(0), flags(1), flags(2), flags(3))
                Catch ex As InvalidOperationException
                    blocked = True
                End Try
                check(blocked, "deferred native isolation workflows are gated for " & folder.LauncherKind.ToString())
            Next
        Next
        For Each kind In {ExternalLauncherKind.Hmcl, ExternalLauncherKind.PclCe}
            Using fixture As New InstallationChecks.InstallFixture(testRoot, "external-" & kind.ToString() & Guid.NewGuid().ToString("N"))
                Dim result = Await fixture.InstallAsync("vanilla", directoryOnly:=True)
                check(File.Exists(Path.Combine(result.InstancePath, "vanilla.json")) AndAlso File.Exists(Path.Combine(result.InstancePath, "vanilla.jar")), "standard vanilla publishes complete files to " & kind.ToString() & " root")
                check(Not Directory.Exists(Path.Combine(result.InstancePath, "PCL")), "foreign vanilla installation creates no PCL instance preferences")
                Dim cancelled = False
                Try
                    Await fixture.InstallAsync("cancelled", token:=New CancellationToken(True), directoryOnly:=True)
                Catch ex As OperationCanceledException
                    cancelled = True
                End Try
                check(cancelled AndAlso Not Directory.Exists(Path.Combine(fixture.Root, "versions/cancelled")), "cancelled directory-mode install publishes no partial instance")
                Dim retry = Await fixture.InstallAsync("cancelled", directoryOnly:=True)
                check(File.Exists(Path.Combine(retry.InstancePath, "cancelled.json")), "directory-mode cancellation can retry the same name")
            End Using
        Next
        Using fixture As New InstallationChecks.InstallFixture(testRoot, "external-mapped-" & Guid.NewGuid().ToString("N"))
            Dim index As New JObject From {{"map_to_resources", True}, {"objects", New JObject From {{"legacy.txt", New JObject From {{"hash", New String("a"c, 40)}, {"size", 1}}}}}}
            fixture.Server.Files("/index") = Encoding.UTF8.GetBytes(index.ToString())
            Dim metadata = JObject.Parse(Encoding.UTF8.GetString(fixture.Server.Files("/version")))
            metadata("assetIndex")("sha1") = Digest(fixture.Server.Files("/index"))
            metadata("assetIndex")("size") = fixture.Server.Files("/index").Length
            fixture.Server.Files("/version") = Encoding.UTF8.GetBytes(metadata.ToString())
            fixture.Entry.Sha1 = Digest(fixture.Server.Files("/version"))
            Dim blocked = False
            Try
                Await fixture.InstallAsync("mapped", directoryOnly:=True)
            Catch ex As NotSupportedException
                blocked = True
            End Try
            check(blocked AndAlso Not Directory.Exists(Path.Combine(fixture.Root, "versions/mapped")), "directory-only mode rejects old mapped assets before publishing a PCL-shaped instance")
            check(Not fixture.Server.Requests.ContainsKey("/client"), "unsupported mapped-resource mode fails before client content downloads")
        End Using
        Return passed
    End Function
    Private Function Digest(bytes As Byte()) As String
        Using sha = Security.Cryptography.SHA1.Create()
            Return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function
End Module
