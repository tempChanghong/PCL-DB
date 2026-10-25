Imports System.IO
Imports System.IO.Compression
Imports System.Net
Imports System.Net.Http
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module ResourceBrowserFollowupChecks
    Friend Const Png As String = "iVBORw0KGgoAAAANSUhEUgAAAAQAAAACCAYAAAB/qH1jAAAAFElEQVR4nGPUqLjznwEJMCFzQGwATRQCf8HSwvAAAAAASUVORK5CYII="
    Private Sub Check(ok As Boolean, label As String, passed As List(Of String))
        If Not ok Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub
    Private Function IconJar(folder As String, metadataName As String, metadata As String, iconName As String, bytes As Byte()) As String
        Directory.CreateDirectory(folder)
        Dim path = IO.Path.Combine(folder, Guid.NewGuid().ToString("N") & ".jar")
        Using stream = File.Create(path), zip As New ZipArchive(stream, ZipArchiveMode.Create)
            Using writer As New StreamWriter(zip.CreateEntry(metadataName).Open(), New UTF8Encoding(False)) : writer.Write(metadata) : End Using
            Using output = zip.CreateEntry(iconName).Open() : output.Write(bytes, 0, bytes.Length) : End Using
        End Using
        Return path
    End Function
    Private Class Handler
        Inherits HttpMessageHandler
        Friend Kind As CommunityResourceKind = CommunityResourceKind.Modpack
        Friend Gate As TaskCompletionSource(Of Boolean)
        Friend Attempts As Integer
        Friend FirstFailure As Boolean
        Friend Permanent As Boolean
        Protected Overrides Async Function SendAsync(request As HttpRequestMessage, token As CancellationToken) As Task(Of HttpResponseMessage)
            Dim data As JObject
            If request.RequestUri.Host = "mod.mcimirror.top" Then
                Interlocked.Increment(Attempts)
                If Permanent OrElse (FirstFailure AndAlso Attempts = 1) Then Return New HttpResponseMessage(If(Permanent, HttpStatusCode.NotFound, HttpStatusCode.ServiceUnavailable))
                If Gate IsNot Nothing Then
                    Await Task.WhenAny(Gate.Task, Task.Delay(5000, token))
                    token.ThrowIfCancellationRequested()
                    If Not Gate.Task.IsCompleted Then Throw New TimeoutException("Held source was not released.")
                End If
                Dim project = ModpackCatalogChecks.CfProject(12) : project("classId") = CommunityResources.ClassId(Kind)
                data = New JObject From {{"data", New JArray(project)}, {"pagination", New JObject From {{"totalCount", 1}}}}
            Else
                Dim project = If(Kind = CommunityResourceKind.Modpack, ModpackCatalogChecks.MrProject("first"), CommunityResourceChecks.ProjectData(Kind, "first"))
                data = New JObject From {{"hits", New JArray(project)}, {"total_hits", 1}}
            End If
            Return New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New StringContent(data.ToString(), Encoding.UTF8, "application/json")}
        End Function
    End Class
    Private Class PreviewProgress
        Implements IProgress(Of ModpackSearchPreview)
        Friend Ready As New TaskCompletionSource(Of ModpackSearchPreview)(TaskCreationOptions.RunContinuationsAsynchronously)
        Public Sub Report(value As ModpackSearchPreview) Implements IProgress(Of ModpackSearchPreview).Report
            If value.Projects.Count > 0 Then Ready.TrySetResult(value)
        End Sub
    End Class
    Friend Async Function VerifyAsync(root As String, passed As List(Of String)) As Task
        Dim bytes = Convert.FromBase64String(Png), folder = IO.Path.Combine(root, "local-icon-followup")
        Dim metadata = New JObject From {{"id", "icon_fixture"}, {"name", "Icon Fixture"}, {"version", "1.0"}, {"icon", "assets/icon.png"}}
        Dim path = IconJar(folder, "fabric.mod.json", metadata.ToString(), "assets/icon.png", bytes), before = File.ReadAllBytes(path)
        Dim entry = LocalModCatalog.Read(path, CancellationToken.None)
        Check(entry.Recognized AndAlso entry.IconData.SequenceEqual(bytes) AndAlso entry.IconWidth = 4 AndAlso entry.IconHeight = 2, "Fabric reads the icon and its dimensions inside an arbitrary local archive without download provenance", passed)
        Dim copy = entry.IconData : copy(0) = 0
        Check(entry.IconData.SequenceEqual(bytes) AndAlso File.ReadAllBytes(path).SequenceEqual(before), "icon consumers cannot mutate metadata snapshots or the source archive", passed)
        metadata("icon") = New JObject From {{"16", "missing.png"}, {"64", "assets/icon.png"}, {"256", "other.png"}}
        Check(LocalModCatalog.Read(IconJar(folder, "fabric.mod.json", metadata.ToString(), "assets/icon.png", bytes), CancellationToken.None).IconData.SequenceEqual(bytes), "Fabric icon-size dictionaries select the nearest useful declared resolution", passed)
        Dim quilt = New JObject From {{"quilt_loader", New JObject From {{"id", "quilt_icon"}, {"version", "1"}, {"metadata", New JObject From {{"icon", "icon.png"}}}}}}
        Check(LocalModCatalog.Read(IconJar(folder, "quilt.mod.json", quilt.ToString(), "icon.png", bytes), CancellationToken.None).IconData.SequenceEqual(bytes), "Quilt metadata resolves its own embedded icon", passed)
        For Each neo In {False, True}
            Dim toml = "[[mods]]" & vbLf & "modId='icon_fixture'" & vbLf & "version='1'" & vbLf & "logoFile='icon.png'"
            Check(LocalModCatalog.Read(IconJar(folder, If(neo, "META-INF/neoforge.mods.toml", "META-INF/mods.toml"), toml, "icon.png", bytes), CancellationToken.None).IconData.SequenceEqual(bytes), "TOML local icons preserve native loader metadata: NeoForge=" & neo, passed)
        Next
        Dim legacy As New JArray(New JObject From {{"modid", "legacy_icon"}, {"version", "1"}, {"logoFile", "icon.png"}})
        Check(LocalModCatalog.Read(IconJar(folder, "mcmod.info", legacy.ToString(), "icon.png", bytes), CancellationToken.None).IconData.SequenceEqual(bytes), "legacy Forge reads logoFile independently of modern metadata", passed)
        For Each badPath In {"../icon.png", "/icon.png", "C:/icon.png", "missing.png"}
            metadata("icon") = badPath
            entry = LocalModCatalog.Read(IconJar(folder, "fabric.mod.json", metadata.ToString(), "icon.png", bytes), CancellationToken.None)
            Check(entry.Recognized AndAlso entry.Problem Is Nothing AndAlso entry.IconData Is Nothing, "unsafe or missing optional icons retain valid Mod metadata: " & badPath, passed)
        Next
        Dim badToml = "[[mods]]" & vbLf & "modId='safe_mod'" & vbLf & "logoFile=true"
        entry = LocalModCatalog.Read(IconJar(folder, "META-INF/mods.toml", badToml, "icon.png", bytes), CancellationToken.None)
        Check(entry.Recognized AndAlso entry.Problem Is Nothing AndAlso entry.IconData Is Nothing, "malformed optional TOML artwork cannot reject an otherwise valid Mod", passed)
        metadata("icon") = "icon.png"
        Dim oversized = DirectCast(bytes.Clone(), Byte()) : oversized(16) = 127
        entry = LocalModCatalog.Read(IconJar(folder, "fabric.mod.json", metadata.ToString(), "icon.png", oversized), CancellationToken.None)
        Check(entry.Recognized AndAlso entry.IconData Is Nothing, "oversized image dimensions are rejected before UI decoding without disabling the Mod", passed)
        Dim jpeg(23) As Byte
        jpeg(0) = 255 : jpeg(1) = 216 : jpeg(2) = 255 : jpeg(3) = 192 : jpeg(5) = 8 : jpeg(6) = 8 : jpeg(8) = 2 : jpeg(10) = 4
        Check(LocalModCatalog.SafeIconDimensions(jpeg), "JPEG dimensions are checked by content even for icons named png", passed)
        File.Move(path, path & ".disabled")
        Check(LocalModCatalog.Read(path & ".disabled", CancellationToken.None).IconData.SequenceEqual(bytes), "disabled local Mods retain embedded artwork", passed)
        For Each kind In {CommunityResourceKind.DataPack, CommunityResourceKind.Shader}
            Dim handler As New Handler With {.Kind = kind, .FirstFailure = True}
            Using client As New ModpackCatalogClient(handler, "")
                Dim page = Await client.SearchAsync(ModpackPlatform.CurseForge, New ModpackQuery With {.Kind = kind}, 0, CancellationToken.None)
                Check(page.Projects.Count = 1 AndAlso handler.Attempts = 2 AndAlso page.Projects(0).Kind = kind, "temporary CF failure retries its exact data/shader category once: " & kind.ToString(), passed)
            End Using
        Next
        Dim permanent As New Handler With {.Permanent = True}
        Using client As New ModpackCatalogClient(permanent, "")
            Dim rejected = False
            Try
                Await client.SearchAsync(ModpackPlatform.CurseForge, New ModpackQuery(), 0, CancellationToken.None)
            Catch ex As IOException
                rejected = ex.Message.Contains("404") AndAlso ex.Message.Contains("Not Found")
            End Try
            Check(rejected AndAlso permanent.Attempts = 1, "permanent CF errors do not become retry loops", passed)
        End Using
        Dim held As New Handler With {.Gate = New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)}
        Dim session As New ModpackSearchSession(New ModpackQuery()), progress As New PreviewProgress()
        Using client As New ModpackCatalogClient(held, "")
            Dim pending = session.GetPageAsync(0, client, CancellationToken.None, progress)
            If Await Task.WhenAny(progress.Ready.Task, Task.Delay(3000)) IsNot progress.Ready.Task Then Throw New TimeoutException("No source preview arrived.")
            Dim preview = Await progress.Ready.Task
            Check(Not pending.IsCompleted AndAlso preview.Projects.Single().Platform = ModpackPlatform.Modrinth AndAlso preview.Pending.SequenceEqual({ModpackPlatform.CurseForge}), "a ready source produces usable results while CF remains pending", passed)
            Check(session.Count = 0, "source previews do not advance canonical cursors or publish half-committed session results", passed)
            held.Gate.TrySetResult(True)
            Dim result = Await pending
            Check(result.Count = 2 AndAlso result(0).Platform = ModpackPlatform.CurseForge AndAlso session.Count = 2, "final mixed-source ordering and pagination remain deterministic after a fast-source preview", passed)
        End Using
        held = New Handler With {.Gate = New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)}
        session = New ModpackSearchSession(New ModpackQuery()) : progress = New PreviewProgress()
        Using client As New ModpackCatalogClient(held, ""), cancellation As New CancellationTokenSource()
            Dim pending = session.GetPageAsync(0, client, cancellation.Token, progress)
            If Await Task.WhenAny(progress.Ready.Task, Task.Delay(3000)) IsNot progress.Ready.Task Then Throw New TimeoutException("No cancellable preview arrived.")
            cancellation.Cancel() : Dim cancelled = False
            Try
                Await pending
            Catch ex As OperationCanceledException
                cancelled = True
            End Try
            Check(cancelled AndAlso session.Count = 0, "cancelling a preview retires pending sources without committing partial cursors", passed)
            held.Gate.TrySetResult(True)
            Check((Await session.GetPageAsync(0, client, CancellationToken.None)).Count = 2, "a cancelled mixed-source preview retries both sources without losing or duplicating projects", passed)
        End Using
    End Function
End Module
