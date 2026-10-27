Imports System.IO
Imports System.Net.Http
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module ModpackPathChecks
    Private Function Reject(action As Action) As Boolean
        Try
            action()
        Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException
            Return True
        End Try
        Return False
    End Function
    Private Async Function ObserveAsync(results As List(Of Tuple(Of String, Boolean)), label As String, action As Func(Of Task(Of Boolean))) As Task
        Try
            results.Add(Tuple.Create(label, Await action()))
        Catch ex As Exception When Not TypeOf ex Is OperationCanceledException
            results.Add(Tuple.Create(label & " [" & ex.GetType().Name & ": " & ex.Message & "]", False))
        End Try
    End Function
    Private Function Read(root As String, platform As ModpackPlatform, relative As String, Optional reference As Boolean = False) As Object
        Dim entries As New Dictionary(Of String, String) From {{"overrides/" & If(reference, "config/marker.txt", relative), "fixture"}}
        Dim label = Guid.NewGuid().ToString("N")
        If platform = ModpackPlatform.CurseForge Then Return CurseForgePackage.Read(CurseForgeChecks.Pack(root, label, entries:=entries))
        Dim manifest = ModrinthChecks.Manifest()
        If reference Then
            manifest("files") = New JArray(manifest("files")(0).DeepClone()) : manifest("files")(0)("path") = relative
        End If
        Return ModrinthPackage.Read(ModrinthChecks.Pack(root, label, manifest, entries))
    End Function
    Private Async Function ResolveAsync(root As String, fileName As String, suffix As String, classId As Long) As Task(Of CurseForgeResolvedFile)
        Using handler As New CurseForgeChecks.Handler With {.Suffix = suffix, .ProjectClass = classId}, client As New HttpClient(handler), resolver As New CurseForgeFileResolver(New SourcePolicy(), client, "")
            handler.Mutate = Sub(data)
                                 data("fileName") = fileName
                                 data("downloadUrl") = "https://edge.forgecdn.net/files/1/1/" & fileName
                             End Sub
            Dim reference = CurseForgePackage.Read(CurseForgeChecks.Pack(root, Guid.NewGuid().ToString("N"))).References(0)
            Return Await resolver.ResolveFileAsync(reference, "1.20.1", CancellationToken.None)
        End Using
    End Function
    Friend Async Function RunAsync(root As String) As Task(Of List(Of String))
        root = Path.Combine(root, "modpack-paths") : Directory.CreateDirectory(root)
        Dim results As New List(Of Tuple(Of String, Boolean))
        Dim deep = "config/" & String.Join("/", Enumerable.Repeat(New String("d"c, 80), 3)) & "/entry.txt"
        For Each platform In {ModpackPlatform.CurseForge, ModpackPlatform.Modrinth}
            For Each relative In {"config/" & New String("f"c, 146) & ".txt", deep, "config/" & New String("f"c, 251) & ".txt"}
                Dim selectedPlatform = platform, selectedPath = relative
                Await ObserveAsync(results, "pack accepts legal override path " & platform.ToString() & ": " & relative.Length,
                    Function() Task.FromResult(Read(root, selectedPlatform, selectedPath) IsNot Nothing))
            Next
            For Each relative In {"../escape", "mods/CON.jar", "mods/" & New String("f"c, 252) & ".jar", "mods/a.", "mods/x:stream", String.Join("/", Enumerable.Repeat("d", 16380)) & "/file.txt"}
                Dim selectedPlatform = platform, selectedPath = relative
                Await ObserveAsync(results, "pack still rejects invalid path " & platform.ToString() & ": " & relative.Substring(0, Math.Min(24, relative.Length)),
                    Function() Task.FromResult(Reject(Sub() Read(root, selectedPlatform, selectedPath))))
            Next
        Next
        For Each length In {150, 255}
            Dim selectedLength = length
            Await ObserveAsync(results, "MR accepts legal indexed filename length " & length,
                Function() Task.FromResult(Read(root, ModpackPlatform.Modrinth, "mods/" & New String("m"c, selectedLength - 4) & ".jar", True) IsNot Nothing))
            For Each kind In {Tuple.Create(".jar", 0L, "mods/"), Tuple.Create(".zip", 12L, "resourcepacks/"), Tuple.Create(".zip", 6552L, "shaderpacks/")}
                Dim name = New String("m"c, length - 4) & kind.Item1, suffix = kind.Item1, classId = kind.Item2, folder = kind.Item3
                Await ObserveAsync(results, "CF resolves legal filename length " & length & " into " & folder,
                    Async Function()
                        Dim resolved = Await ResolveAsync(root, name, suffix, classId)
                        Return resolved.Download.RelativePath = folder & name AndAlso resolved.Download.Sha1 IsNot Nothing
                    End Function)
            Next
        Next
        For Each fileName In {"../bad.jar", "CON.jar", New String("m"c, 252) & ".jar", "bad:mod.jar"}
            Dim name = fileName
            Await ObserveAsync(results, "CF still rejects invalid filename: " & name.Substring(0, Math.Min(24, name.Length)),
                Async Function()
                    Try
                        Await ResolveAsync(root, name, ".jar", 0)
                    Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is ArgumentException OrElse TypeOf ex Is InvalidDataException
                        Return True
                    End Try
                    Return False
                End Function)
        Next
        Await ObserveAsync(results, "instance names still accept the existing 100-character boundary", Function()
                                                                                                             SafeFiles.ValidateInstanceName(New String("i"c, 100))
                                                                                                             Return Task.FromResult(True)
                                                                                                         End Function)
        Await ObserveAsync(results, "file path changes do not relax the 100-character instance-name limit", Function() Task.FromResult(Reject(Sub() SafeFiles.ValidateInstanceName(New String("i"c, 101)))))
        For Each platform In {ModpackPlatform.CurseForge, ModpackPlatform.Modrinth}
            Dim selected = platform
            Await ObserveAsync(results, "long-name pack installs, cancels, retries and preserves shared settings: " & platform.ToString(), Function() InstallAsync(root, selected, deep))
        Next
        For Each cached In {False, True}
            Dim useCache = cached
            Await ObserveAsync(results, "255-character filenames reuse verified bytes without network: cache=" & cached, Function() ReuseAsync(root, useCache))
        Next
        Dim failed = results.Where(Function(item) Not item.Item2).ToArray()
        If failed.Length > 0 Then Throw New InvalidOperationException("Modpack path failures: " & failed.Length & "/" & results.Count & vbCrLf & String.Join(vbCrLf, failed.Select(Function(item) item.Item1)))
        Return results.Select(Function(item) item.Item1).ToList()
    End Function
    Private Async Function InstallAsync(root As String, platform As ModpackPlatform, deep As String) As Task(Of Boolean)
        Using fixture As New InstallationChecks.InstallFixture(root, "long-path-" & platform.ToString()), handler As New CurseForgeChecks.Handler, client As New HttpClient(handler), resolver As New CurseForgeFileResolver(New SourcePolicy(), client, "")
            ModrinthChecks.Configure(fixture)
            Dim fileName = New String("m"c, 251) & ".jar", policy As New ModrinthChecks.Policy(fixture.Server.Url)
            Dim entries As New Dictionary(Of String, String) From {{"overrides/" & deep, "deep override"}, {"overrides/PCL/Setup.ini", "VersionArgumentIndieV2:False" & vbCrLf & "UserSetting:retained"}}
            Dim install As Func(Of IProgress(Of TransferProgress), CancellationToken, Task(Of InstallResult)), expected As Byte()
            If platform = ModpackPlatform.Modrinth Then
                Dim manifest = ModrinthChecks.Manifest() : manifest("files") = New JArray(manifest("files")(0).DeepClone()) : manifest("files")(0)("path") = "mods/" & fileName
                Dim package = ModrinthPackage.Read(ModrinthChecks.Pack(root, "long-install", manifest, entries))
                expected = fixture.Server.Files("/required")
                install = Function(progress, token) New ModrinthInstaller(policy).InstallAsync(package, fixture.Root, "pack", {}, {}, progress, token, connections:=4)
            Else
                handler.Mutate = Sub(data)
                                     data("fileName") = fileName : data("downloadUrl") = "https://edge.forgecdn.net/files/1/1/" & fileName
                                 End Sub
                fixture.Server.Files("/files/1/1/" & fileName) = handler.Bytes(1001) : expected = handler.Bytes(1001)
                Dim manifest = CurseForgeChecks.Manifest() : manifest("files") = New JArray(manifest("files")(0).DeepClone())
                Dim package = CurseForgePackage.Read(CurseForgeChecks.Pack(root, "long-install", manifest, entries))
                install = Function(progress, token) New CurseForgeInstaller(policy, resolver:=resolver).InstallAsync(package, fixture.Root, "pack", {}, {}, progress, token, connections:=4)
            End If
            Dim sharedPath = Path.Combine(fixture.Root, "mods/user.jar"), settings = Path.Combine(fixture.Root, "PCL/Setup.ini")
            Directory.CreateDirectory(Path.GetDirectoryName(sharedPath)) : File.WriteAllText(sharedPath, "existing shared mod")
            Directory.CreateDirectory(Path.GetDirectoryName(settings)) : File.WriteAllText(settings, "LaunchArgumentIndieV2:0")
            Dim cancelled = False
            Using cancel As New CancellationTokenSource()
                Try
                    Await install(New ModrinthChecks.Progress(Sub(value)
                                                                If value.Stage = "完成安装" Then cancel.Cancel()
                                                            End Sub), cancel.Token)
                Catch ex As OperationCanceledException
                    cancelled = Not Directory.Exists(Path.Combine(fixture.Root, "versions/pack"))
                End Try
            End Using
            Dim result = Await install(Nothing, CancellationToken.None)
            If result.NetworkBytes <> 0 Then Throw New InvalidOperationException("Long-name retry downloaded " & result.NetworkBytes & " bytes despite a verified cache.")
            Dim requests = fixture.Server.Requests.Values.Sum(), conflict = False
            Try
                Await install(Nothing, CancellationToken.None)
            Catch ex As IOException
                conflict = requests = fixture.Server.Requests.Values.Sum()
            End Try
            Return cancelled AndAlso conflict AndAlso File.ReadAllBytes(Path.Combine(result.InstancePath, "mods", fileName)).SequenceEqual(expected) AndAlso
                File.ReadAllText(Path.Combine(result.InstancePath, deep)) = "deep override" AndAlso File.ReadAllText(sharedPath) = "existing shared mod" AndAlso File.ReadAllText(settings) = "LaunchArgumentIndieV2:0" AndAlso
                PclFolders.ReadIniValue(Path.Combine(result.InstancePath, "PCL/Setup.ini"), "VersionArgumentIndieV2") = "True" AndAlso PclFolders.ReadIniValue(Path.Combine(result.InstancePath, "PCL/Setup.ini"), "UserSetting") = "retained"
        End Using
    End Function
    Private Async Function ReuseAsync(root As String, cached As Boolean) As Task(Of Boolean)
        Using server As New TestServer()
            Dim bytes = Encoding.UTF8.GetBytes("verified long filename"), policy As New SourcePolicy With {.AllowLoopback = True}
            server.Files("/payload") = bytes
            Dim source = Path.Combine(root, "reuse-source-" & cached), target = Path.Combine(root, "reuse-target-" & cached)
            Dim item As New DownloadFile With {.RelativePath = "libraries/" & New String("m"c, 251) & ".jar", .Url = server.Url & "/payload", .Size = bytes.Length, .Sha1 = ModrinthChecks.Hash(bytes), .Category = "library"}
            Dim original = SafeFiles.UnderRoot(source, item.RelativePath)
            Directory.CreateDirectory(Path.GetDirectoryName(original)) : File.WriteAllBytes(original, bytes)
            If cached Then
                Using seed As New DownloadEngine(policy, Nothing, {}, 1, source, useComponentCache:=True)
                    Await seed.DownloadAsync(source, {item}, CancellationToken.None)
                End Using
                File.Delete(original)
            End If
            Using engine As New DownloadEngine(policy, Nothing, {source}, 1, target, useComponentCache:=cached)
                Await engine.DownloadAsync(target, {item}, CancellationToken.None)
                Return engine.NetworkBytes = 0 AndAlso engine.Reused = 1 AndAlso server.Requests.IsEmpty AndAlso SafeFiles.Matches(SafeFiles.UnderRoot(target, item.RelativePath), item) AndAlso
                    Not Directory.EnumerateFiles(Path.Combine(target, "libraries")).Any(Function(path) path.EndsWith(".tmp", StringComparison.Ordinal))
            End Using
        End Using
    End Function
End Module
