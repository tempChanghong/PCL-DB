Imports System.Collections
Imports System.IO
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module InstallInputChecks
    Private Sub Check(value As Boolean, label As String, passed As List(Of String))
        If Not value Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub
    Private Class Policy
        Inherits SourcePolicy
        Private ReadOnly _server As String
        Friend Sub New(server As String)
            _server = server : AllowLoopback = True
        End Sub
        Public Overrides Function Candidates(url As String, category As String) As List(Of String)
            Return New List(Of String) From {_server & New Uri(url).AbsolutePath}
        End Function
    End Class
    Private Class Progress
        Implements IProgress(Of TransferProgress)
        Private ReadOnly _action As Action(Of TransferProgress)
        Friend Sub New(action As Action(Of TransferProgress))
            _action = action
        End Sub
        Public Sub Report(value As TransferProgress) Implements IProgress(Of TransferProgress).Report
            _action(value)
        End Sub
    End Class
    Private Class SingleUseRoots
        Implements IEnumerable(Of String)
        Private ReadOnly _values As List(Of String)
        Friend Enumerations As Integer
        Friend Sub New(ParamArray values As String())
            _values = values.ToList()
        End Sub
        Public Function GetEnumerator() As IEnumerator(Of String) Implements IEnumerable(Of String).GetEnumerator
            Enumerations += 1
            If Enumerations > 1 Then Throw New InvalidOperationException("Reuse roots were enumerated again after request initialization.")
            Return _values.GetEnumerator()
        End Function
        Private Function NonGenericEnumerator() As IEnumerator Implements IEnumerable.GetEnumerator
            Return GetEnumerator()
        End Function
    End Class
    Private Function Start(session As IsolationResourceChecks.Session, roots As IEnumerable(Of String), Optional selection As ComponentSelection = Nothing, Optional progress As IProgress(Of TransferProgress) = Nothing, Optional repair As Boolean = False, Optional token As CancellationToken = Nothing, Optional isolation As InstanceIsolationMode = InstanceIsolationMode.FollowPcl) As Task(Of InstallResult)
        Return New MinecraftInstaller(New Policy(session.Fixture.Server.Url), session.PclBase).InstallAsync(session.Fixture.Entry, session.Root, "legacy", roots, progress, token, repair, 4, components:=selection, isolationMode:=isolation)
    End Function
    Friend Async Function RunAsync(root As String, Optional onlyCase As String = Nothing) As Task(Of List(Of String))
        If onlyCase = "install-input-all" Then onlyCase = Nothing
        Dim passed As New List(Of String)
        If onlyCase Is Nothing OrElse onlyCase = "install-input-copy" Then Copy(passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-input-components" Then Await ComponentsAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-input-version" Then Await VersionsAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-input-roots" Then Await RootsAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-input-direct" Then Await DirectAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-input-retry" Then Await RetryAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-input-repair" Then Await RepairAsync(root, passed)
        If onlyCase Is Nothing OrElse onlyCase = "install-input-invalid" Then Await InvalidAsync(root, passed)
        If passed.Count = 0 Then Throw New ArgumentException(onlyCase)
        Return passed
    End Function
    Private Sub Copy(passed As List(Of String))
        Dim selected As New ComponentSelection With {.JavaPath = "original-java"}, originals As New Dictionary(Of ComponentKind, ComponentEntry), before As New Dictionary(Of ComponentKind, JObject)
        For Each kind As ComponentKind In [Enum].GetValues(GetType(ComponentKind))
            Dim entry As New ComponentEntry With {.Kind = kind, .Version = "fixture", .Minecraft = "1.20.1", .Stable = False,
                .Url = "https://example.invalid/fixture", .FileName = "original.jar", .Sha1 = New String("a"c, 40), .Md5 = New String("b"c, 32), .Size = 123,
                .RequiredForge = "47.2.18", .Data = New JObject From {{"name", "original title"}, {"nested", New JArray(New JObject From {{"value", "original"}})}}}
            selected.Entries.Add(kind, entry) : originals.Add(kind, entry) : before.Add(kind, JObject.FromObject(entry))
        Next
        Dim description = selected.Description, copy = selected.Copy()
        For Each pair In originals
            Dim entry = pair.Value, cloned = copy.Item(pair.Key)
            Check(Not Object.ReferenceEquals(entry, cloned), "component snapshot owns its entry object: " & pair.Key, passed)
            Check(Not Object.ReferenceEquals(entry.Data, cloned.Data) AndAlso Not Object.ReferenceEquals(entry.Data("nested")(0), cloned.Data("nested")(0)), "component snapshot owns nested JSON values: " & pair.Key, passed)
            entry.Version = "changed" : entry.Minecraft = "changed" : entry.Kind = ComponentKind.Forge : entry.Stable = True
            entry.Url = "https://example.invalid/changed" : entry.FileName = "changed.jar" : entry.Sha1 = "changed" : entry.Md5 = "changed" : entry.Size = 999 : entry.RequiredForge = "changed"
            entry.Data("nested")(0)("value") = "changed" : entry.Data("name") = "changed title"
            Check(JToken.DeepEquals(before(pair.Key), JObject.FromObject(cloned)), "component snapshot preserves every serialized field after caller mutation: " & pair.Key, passed)
        Next
        selected.JavaPath = "changed-java" : selected.Entries.Clear()
        Check(copy.Entries.Count = originals.Count AndAlso copy.JavaPath = "original-java" AndAlso copy.Description = description, "component snapshot keeps its dictionary, Java choice and description", passed)
        Dim empty As New ComponentSelection
        Check(empty.Copy().Entries.Count = 0, "an empty component snapshot remains valid", passed)
        empty.Entries.Add(ComponentKind.Fabric, New ComponentEntry With {.Kind = ComponentKind.Fabric})
        Check(empty.Copy().Item(ComponentKind.Fabric).Data Is Nothing, "component snapshots preserve absent optional JSON", passed)
    End Sub
    Private Async Function ComponentsAsync(root As String, passed As List(Of String)) As Task
        For Each isolated In {False, True}
            For Each field In {"filename", "integrity", "loader"}
                Using session As New IsolationResourceChecks.Session(root, "inputs-component-" & isolated & "-" & field)
                    Dim selection = IsolationModeChecks.Components(session), api = selection.Item(ComponentKind.FabricApi), loader = selection.Item(ComponentKind.Fabric)
                    Dim marker = Path.Combine(session.Root, "mods", "user.jar") : session.Put(marker, session.Bad)
                    Dim mutated = False
                    Dim progress As New Progress(Sub(value)
                        If mutated OrElse value.Detail <> "读取并校验版本元数据" Then Return
                        mutated = True
                        Select Case field
                            Case "filename" : api.FileName = "user.jar"
                            Case "integrity" : api.Sha1 = New String("0"c, 40) : api.Size = 1
                            Case "loader" : loader.Version = "changed"
                        End Select
                    End Sub)
                    Await Start(session, {}, selection, progress, isolation:=If(isolated, InstanceIsolationMode.Enabled, InstanceIsolationMode.Disabled))
                    Dim target = Path.Combine(If(isolated, Path.GetDirectoryName(session.Json), session.Root), "mods", "fabric-api.jar")
                    Console.WriteLine("INPUT_COMPONENT field=" & field & " isolated=" & isolated & " originalMod=" & File.Exists(target))
                    Check(mutated AndAlso File.Exists(target) AndAlso File.ReadAllBytes(target).SequenceEqual(session.Fixture.Server.Files("/api")), "active installation retains the selected component inputs: " & isolated & "-" & field, passed)
                    Check(File.ReadAllBytes(marker).SequenceEqual(session.Bad) AndAlso PclFolders.ReadIniValue(session.Ini, "VersionArgumentIndieV2") = isolated.ToString(), "caller changes cannot redirect Mods into a user's file or change isolation: " & isolated & "-" & field, passed)
                    Check(session.Released() AndAlso JObject.Parse(File.ReadAllText(session.Json))("mainClass").ToString() = "fixture.FabricLoader", "component input mutation leaves a complete profile and releases staging: " & isolated & "-" & field, passed)
                End Using
            Next
        Next
        Using session As New IsolationResourceChecks.Session(root, "inputs-nested", "1.12.2")
            Dim entry As New ComponentEntry With {.Kind = ComponentKind.LiteLoader, .Minecraft = "1.12.2", .Version = "1.12.2", .Data = New JObject From {{"libraries", New JArray()}, {"tweakClass", "fixture.OriginalTweaker"}}}
            Dim selection As New ComponentSelection() : selection.Entries.Add(ComponentKind.LiteLoader, entry)
            session.Fixture.Server.Files("/versions/com/mumfrey/liteloader/1.12.2/liteloader-1.12.2.jar") = Encoding.UTF8.GetBytes("fixture LiteLoader runtime")
            Dim changed = False
            Await Start(session, {}, selection, New Progress(Sub(value)
                If changed OrElse value.Detail <> "读取并校验版本元数据" Then Return
                changed = True : entry.Data("tweakClass") = "fixture.ChangedTweaker"
            End Sub))
            Dim profile = JObject.Parse(File.ReadAllText(session.Json)), args = DirectCast(profile.SelectToken("arguments.game"), JArray).Select(Function(item) CStr(item)).ToList()
            Check(changed AndAlso args.Contains("fixture.OriginalTweaker") AndAlso Not args.Contains("fixture.ChangedTweaker"), "active LiteLoader profile keeps its original nested component metadata", passed)
            Check(entry.Data("tweakClass").ToString() = "fixture.ChangedTweaker" AndAlso session.Released(), "snapshotting does not revert the caller's newer catalogue data", passed)
        End Using
    End Function
    Private Async Function VersionsAsync(root As String, passed As List(Of String)) As Task
        For Each phase In {"commit", "prepare"}
            Using session As New IsolationResourceChecks.Session(root, "inputs-version-" & phase)
                Dim original = session.Fixture.Entry.Id, changed = False
                Await Start(session, {}, progress:=New Progress(Sub(value)
                    If changed OrElse If(phase = "prepare", value.Detail <> "读取并校验版本元数据", value.Stage <> "完成安装") Then Return
                    changed = True : session.Fixture.Entry.Id = "changed-version"
                End Sub))
                Dim profile = JObject.Parse(File.ReadAllText(session.Json))
                Console.WriteLine("INPUT_VERSION phase=" & phase & " clientVersion=" & CStr(profile("clientVersion")))
                Check(changed AndAlso CStr(profile("clientVersion")) = original, "active install publishes the version identity captured at entry: " & phase, passed)
                Check(session.Fixture.Entry.Id = "changed-version" AndAlso session.Released(), "version input snapshots leave caller updates intact: " & phase, passed)
            End Using
        Next
        Using session As New IsolationResourceChecks.Session(root, "inputs-version-source")
            Dim alternate = JObject.Parse(Encoding.UTF8.GetString(session.Fixture.Server.Files("/version"))) : alternate("mainClass") = "changed.Main"
            session.Fixture.Server.Files("/alternate") = Encoding.UTF8.GetBytes(alternate.ToString())
            Dim changed = False
            Await Start(session, {}, progress:=New Progress(Sub(value)
                If changed OrElse value.Detail <> "读取并校验版本元数据" Then Return
                changed = True : session.Fixture.Entry.Url = session.Fixture.Server.Url & "/alternate"
                Using sha As New Security.Cryptography.SHA1CryptoServiceProvider()
                    session.Fixture.Entry.Sha1 = BitConverter.ToString(sha.ComputeHash(session.Fixture.Server.Files("/alternate"))).Replace("-", "").ToLowerInvariant()
                End Using
            End Sub))
            Check(changed AndAlso CStr(JObject.Parse(File.ReadAllText(session.Json))("mainClass")) = "vanilla.Main" AndAlso Not session.Fixture.Server.Requests.ContainsKey("/alternate"), "version URL and checksum remain bound to the original installation request", passed)
        End Using
    End Function
    Private Async Function RootsAsync(root As String, passed As List(Of String)) As Task
        For Each action In {"list", "corrupt", "single"}
            Using session As New IsolationResourceChecks.Session(root, "inputs-roots-" & action)
                Dim selection = session.Fixture.Fabric(), source = session.Root & "-source", relative = "libraries/fixture/shared/1/shared-1.jar"
                Dim sourceFile = Path.Combine(source, relative), bytes = session.Fixture.Server.Files("/shared")
                session.Put(sourceFile, bytes)
                Dim values As New List(Of String) From {source}, oneShot As New SingleUseRoots(source), changed = False
                Dim inputs As IEnumerable(Of String) = If(action = "single", DirectCast(oneShot, IEnumerable(Of String)), values)
                Dim result = Await Start(session, inputs, selection, New Progress(Sub(value)
                    If changed OrElse value.Detail <> "读取并校验版本元数据" Then Return
                    changed = True
                    If action = "list" Then values.Clear()
                    If action = "corrupt" Then session.Put(sourceFile, session.Bad)
                End Sub))
                Console.WriteLine("INPUT_ROOTS action=" & action & " enumerations=" & oneShot.Enumerations & " sharedRequested=" & session.Fixture.Server.Requests.ContainsKey("/shared"))
                Check(changed AndAlso session.Fixture.Server.Requests.ContainsKey("/shared") = (action = "corrupt") AndAlso File.ReadAllBytes(Path.Combine(session.Root, relative)).SequenceEqual(bytes), "reuse roots are captured once while reused content is still checked: " & action, passed)
                Check(session.Released() AndAlso If(action = "single", oneShot.Enumerations = 1, If(action = "list", values.Count = 0, File.ReadAllBytes(sourceFile).SequenceEqual(session.Bad))), "install never re-enumerates or rewrites caller-owned reuse inputs: " & action, passed)
            End Using
        Next
    End Function
    Private Async Function DirectAsync(root As String, passed As List(Of String)) As Task
        Using session As New IsolationResourceChecks.Session(root, "inputs-direct")
            Dim selection = IsolationModeChecks.Components(session), engine As New ComponentInstaller(New Policy(session.Fixture.Server.Url), selection, session.Root, {}, Nothing, 4, 0)
            selection.Item(ComponentKind.Fabric).Version = "changed" : selection.Item(ComponentKind.FabricApi).FileName = "changed.jar" : selection.Entries.Clear()
            Dim stage = Path.Combine(session.Root, "private-stage") : Directory.CreateDirectory(stage)
            File.WriteAllBytes(Path.Combine(stage, "direct.jar"), session.Fixture.Server.Files("/client"))
            Dim profile = Await engine.ApplyAsync(JObject.Parse(Encoding.UTF8.GetString(session.Fixture.Server.Files("/version"))), "1.20.1", stage, "direct", CancellationToken.None)
            Check(CStr(profile("mainClass")) = "fixture.FabricLoader" AndAlso File.Exists(Path.Combine(stage, "mods", "fabric-api.jar")), "direct component installer owns the request captured by its constructor", passed)
            Check(selection.Entries.Count = 0 AndAlso Not Directory.Exists(Path.Combine(session.Root, "versions")), "direct component snapshot neither restores caller state nor publishes an instance", passed)
        End Using
    End Function
    Private Async Function RetryAsync(root As String, passed As List(Of String)) As Task
        Using session As New IsolationResourceChecks.Session(root, "inputs-retry"), cancel As New CancellationTokenSource()
            Dim selection = IsolationModeChecks.Components(session), api = selection.Item(ComponentKind.FabricApi), originalId = session.Fixture.Entry.Id, cancelled = False
            Try
                Await Start(session, {}, selection, New Progress(Sub(value)
                    If value.Stage <> "完成安装" Then Return
                    api.FileName = "next-request.jar" : session.Fixture.Entry.Id = "next-request-id" : cancel.Cancel()
                End Sub), token:=cancel.Token, isolation:=InstanceIsolationMode.Disabled)
            Catch ex As OperationCanceledException
                cancelled = True
            End Try
            Check(cancelled AndAlso session.Released() AndAlso Not File.Exists(session.Json) AndAlso Not File.Exists(Path.Combine(session.Root, "mods", "fabric-api.jar")), "cancelled snapshot-bound install drains work and publishes no shared Mods or instance", passed)
            Check(session.Fixture.Entry.Id = "next-request-id" AndAlso api.FileName = "next-request.jar", "cancellation does not restore stale caller inputs", passed)
            session.Fixture.Entry.Id = originalId ' Keep the next request consistent with its loader catalogue.
            Dim result = Await Start(session, {}, selection, isolation:=InstanceIsolationMode.Disabled)
            Check(result.NetworkBytes = 0 AndAlso File.Exists(Path.Combine(session.Root, "mods", "next-request.jar")) AndAlso CStr(JObject.Parse(File.ReadAllText(session.Json))("clientVersion")) = originalId, "retry captures the current caller request and reuses checked content across a changed filename", passed)
            Check(Not File.Exists(Path.Combine(session.Root, "mods", "fabric-api.jar")) AndAlso PclFolders.ReadIniValue(session.Ini, "VersionArgumentIndieV2") = "False", "a new task does not retain the cancelled task's filename", passed)
        End Using
    End Function
    Private Async Function RepairAsync(root As String, passed As List(Of String)) As Task
        Using session As New IsolationResourceChecks.Session(root, "inputs-repair")
            Await session.SeedAsync() : session.SetIni("VersionArgumentIndieV2:False", Encoding.Unicode)
            File.SetAttributes(session.Ini, FileAttributes.ReadOnly)
            Dim originalIni = File.ReadAllBytes(session.Ini), originalJson = File.ReadAllBytes(session.Json), changed = False
            session.Put(session.Resource(False), session.Bad) : session.Put(session.Resource(True), session.Bad)
            Dim result = Await Start(session, {}, repair:=True, isolation:=InstanceIsolationMode.Enabled, progress:=New Progress(Sub(value)
                If changed OrElse value.Detail <> "读取并校验版本元数据" Then Return
                changed = True : session.Fixture.Entry.Id = "caller-new-version" : session.Fixture.Entry.Url = session.Fixture.Server.Url & "/missing"
                session.Fixture.Entry.SupplementalId = "caller-new-origin"
            End Sub))
            Check(changed AndAlso result.NetworkBytes = 0 AndAlso Not File.ReadAllBytes(session.Resource(False)).SequenceEqual(session.Bad) AndAlso File.ReadAllBytes(session.Resource(True)).SequenceEqual(session.Bad), "repair keeps the original selected version and existing isolation after caller changes", passed)
            Check(originalIni.SequenceEqual(File.ReadAllBytes(session.Ini)) AndAlso originalJson.SequenceEqual(File.ReadAllBytes(session.Json)) AndAlso (File.GetAttributes(session.Ini) And FileAttributes.ReadOnly) <> 0 AndAlso session.Released(), "repair input snapshots preserve raw UTF16 preferences, JSON, read-only attributes and cleanup", passed)
        End Using
    End Function
    Private Async Function InvalidAsync(root As String, passed As List(Of String)) As Task
        For Each field In {"entry", "roots", "component", "direct"}
            Using session As New IsolationResourceChecks.Session(root, "inputs-invalid-" & field)
                Dim failure As Exception = Nothing
                Try
                    Select Case field
                        Case "entry"
                            Await New MinecraftInstaller(New Policy(session.Fixture.Server.Url)).InstallAsync(Nothing, session.Root, "legacy", {}, Nothing, CancellationToken.None)
                        Case "roots"
                            Await Start(session, Nothing)
                        Case "component"
                            Dim selection As New ComponentSelection() : selection.Entries.Add(ComponentKind.Fabric, Nothing)
                            Await Start(session, {}, selection)
                        Case "direct"
                            Dim engine As New ComponentInstaller(New Policy(session.Fixture.Server.Url), Nothing, session.Root, {}, Nothing, 4, 0)
                    End Select
                Catch ex As Exception
                    failure = ex
                End Try
                Check(If(field = "component", TypeOf failure Is InvalidDataException, TypeOf failure Is ArgumentNullException), "invalid snapshot inputs fail with an explicit argument error: " & field, passed)
                Check(Not Directory.Exists(session.Root) AndAlso session.Fixture.Server.Requests.Count = 0, "invalid snapshot inputs fail before filesystem or network writes: " & field, passed)
            End Using
        Next
    End Function
End Module
