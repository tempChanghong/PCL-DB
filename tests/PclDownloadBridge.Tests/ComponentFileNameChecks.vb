Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module ComponentFileNameChecks
    Private Async Function ObserveAsync(results As List(Of Tuple(Of String, Boolean)), label As String, action As Func(Of Task(Of Boolean))) As Task
        Try
            results.Add(Tuple.Create(label, Await action()))
        Catch ex As Exception When Not TypeOf ex Is OperationCanceledException
            results.Add(Tuple.Create(label & " [" & ex.GetType().Name & ": " & ex.Message & "]", False))
        End Try
    End Function
    Private Function Selection(kind As ComponentKind, fileName As String) As ComponentSelection
        Dim selected As New ComponentSelection
        selected.Entries(ComponentKind.Fabric) = New ComponentEntry With {.Kind = ComponentKind.Fabric, .Minecraft = "1.20.1", .Version = "fixture"}
        selected.Entries(kind) = New ComponentEntry With {.Kind = kind, .Minecraft = "1.20.1", .Version = "fixture", .FileName = fileName}
        Return selected
    End Function
    Private Function Rejected(selection As ComponentSelection) As Boolean
        Try
            ComponentInstaller.ModFiles(selection, "1.20.1")
        Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is InvalidDataException
            Return True
        End Try
        Return False
    End Function
    Friend Async Function RunAsync(root As String) As Task(Of List(Of String))
        Dim results As New List(Of Tuple(Of String, Boolean))
        For Each length In {150, 255}
            Dim fileName = New String("a"c, length - 4) & ".jar"
            Await ObserveAsync(results, "Fabric API catalogue retains a legal filename of length " & length,
                Async Function()
                    Using server As New TestServer()
                        Dim row = FabricApiCatalogueChecks.Row() : row("files") = New JArray(FabricApiCatalogueChecks.ApiFile(fileName, True))
                        Dim entries = Await FabricApiCatalogueChecks.LoadAsync(server, New JArray(row))
                        Return entries.Count = 1 AndAlso entries(0).FileName = fileName AndAlso entries(0).Sha1 IsNot Nothing
                    End Using
                End Function)
            For Each kind In {ComponentKind.FabricApi, ComponentKind.OptiFabric, ComponentKind.OptiFine}
                Dim selectedKind = kind
                Await ObserveAsync(results, "component Mods plan retains the original filename: " & kind.ToString() & "/" & length,
                    Function()
                        Dim files = ComponentInstaller.ModFiles(Selection(selectedKind, fileName), "1.20.1")
                        Return Task.FromResult(files.Count = 1 AndAlso files(0).RelativePath = "mods/" & fileName)
                    End Function)
                For Each invalid In {"../outside.jar", "CON.jar", New String("a"c, 252) & ".jar", "a:b.jar"}
                    If length <> 150 Then Continue For
                    Dim selectedName = invalid
                    Await ObserveAsync(results, "component Mods plan rejects unsafe filename: " & kind.ToString() & "/" & invalid.Substring(0, Math.Min(20, invalid.Length)),
                        Function() Task.FromResult(Rejected(Selection(selectedKind, selectedName))))
                Next
            Next
            For Each isolated In {False, True}
                Dim useIsolation = isolated
                Await ObserveAsync(results, "long-name Fabric API installs and retries in the correct Mods directory: " & length & "/isolated=" & isolated,
                    Function() InstallAsync(root, fileName, useIsolation))
            Next
        Next
        Dim conflict = Selection(ComponentKind.FabricApi, "same.jar")
        conflict.Entries(ComponentKind.OptiFabric) = New ComponentEntry With {.Kind = ComponentKind.OptiFabric, .FileName = "SAME.jar"}
        Await ObserveAsync(results, "component Mods plan still rejects case-insensitive destination conflicts", Function() Task.FromResult(Rejected(conflict)))
        Dim failed = results.Where(Function(item) Not item.Item2).ToArray()
        If failed.Length > 0 Then Throw New InvalidOperationException("Component filename failures: " & failed.Length & "/" & results.Count & vbCrLf & String.Join(vbCrLf, failed.Select(Function(item) item.Item1)))
        Return results.Select(Function(item) item.Item1).ToList()
    End Function
    Private Async Function InstallAsync(root As String, fileName As String, isolated As Boolean) As Task(Of Boolean)
        Using session As New IsolationResourceChecks.Session(root, "component-name-" & fileName.Length & "-" & isolated)
            session.SetGlobal("0")
            Dim globalBytes = File.ReadAllBytes(session.GlobalPath), jar As Byte()
            Using buffer As New MemoryStream()
                Using archive As New ZipArchive(buffer, ZipArchiveMode.Create, True), output = archive.CreateEntry("fabric.mod.json").Open()
                    Dim metadata = Encoding.UTF8.GetBytes("{""schemaVersion"":1,""id"":""fabric-api"",""version"":""fixture""}")
                    output.Write(metadata, 0, metadata.Length)
                End Using
                jar = buffer.ToArray()
            End Using
            session.Fixture.Server.Files("/long-api") = jar
            Dim row = FabricApiCatalogueChecks.Row(), apiFile = FabricApiCatalogueChecks.ApiFile(fileName, True, jar)
            apiFile("url") = session.Fixture.Server.Url & "/long-api" : row("files") = New JArray(apiFile)
            Dim api = (Await FabricApiCatalogueChecks.LoadAsync(session.Fixture.Server, New JArray(row))).Single()
            Dim selection = session.Fixture.Fabric() : selection.Entries(ComponentKind.FabricApi) = api
            Dim sharedPath = Path.Combine(session.Root, "mods", "existing.jar")
            Directory.CreateDirectory(Path.GetDirectoryName(sharedPath)) : File.WriteAllText(sharedPath, "existing shared mod")
            Dim mode = If(isolated, InstanceIsolationMode.Enabled, InstanceIsolationMode.Disabled), cancelled = False
            Dim expected = Path.Combine(If(isolated, Path.Combine(session.Root, "versions/legacy"), session.Root), "mods", fileName)
            Using cancel As New CancellationTokenSource()
                Try
                    Await session.StartAsync(components:=selection, isolation:=mode, token:=cancel.Token,
                        progress:=New ModrinthChecks.Progress(Sub(value)
                                                               If value.Stage = "完成安装" Then cancel.Cancel()
                                                           End Sub))
                Catch ex As OperationCanceledException
                    cancelled = Not File.Exists(session.Json) AndAlso Not File.Exists(expected)
                End Try
            End Using
            Dim result = Await session.StartAsync(components:=selection, isolation:=mode)
            If result.NetworkBytes <> 0 Then Throw New InvalidOperationException("Verified component retry still used network bytes: " & result.NetworkBytes)
            Dim opposite = Path.Combine(If(isolated, session.Root, result.InstancePath), "mods", fileName)
            Dim requests = session.Fixture.Server.Requests.Values.Sum(), conflict = False
            Try
                Await session.StartAsync(components:=selection, isolation:=mode)
            Catch ex As IOException
                conflict = requests = session.Fixture.Server.Requests.Values.Sum()
            End Try
            Return cancelled AndAlso conflict AndAlso File.ReadAllBytes(expected).SequenceEqual(jar) AndAlso Not File.Exists(opposite) AndAlso
                File.ReadAllBytes(session.GlobalPath).SequenceEqual(globalBytes) AndAlso File.ReadAllText(sharedPath) = "existing shared mod" AndAlso
                PclFolders.ReadIniValue(session.Ini, "VersionArgumentIndieV2") = If(isolated, "True", "False") AndAlso session.Released()
        End Using
    End Function
End Module
