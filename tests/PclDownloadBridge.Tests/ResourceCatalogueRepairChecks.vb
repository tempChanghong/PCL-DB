Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module ResourceCatalogueRepairChecks
    Private Sub Check(ok As Boolean, label As String, passed As List(Of String))
        If Not ok Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub
    Private Class Handler
        Inherits HttpMessageHandler
        Friend Document As JObject
        Protected Overrides Function SendAsync(request As HttpRequestMessage, token As CancellationToken) As Task(Of HttpResponseMessage)
            token.ThrowIfCancellationRequested()
            Dim search = request.RequestUri.AbsolutePath.EndsWith("/search", StringComparison.Ordinal)
            Dim body = If(search, New JObject From {{"data", New JArray(Document.DeepClone())}, {"pagination", New JObject From {{"totalCount", 1}}}}, New JObject From {{"data", Document.DeepClone()}})
            Return Task.FromResult(New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New StringContent(body.ToString(), Encoding.UTF8, "application/json")})
        End Function
    End Class
    Private Function FileData(kind As CommunityResourceKind) As JObject
        Return New JObject From {{"id", 9262896}, {"modId", 453763}, {"displayName", "Public client file"}, {"fileName", If(kind = CommunityResourceKind.Mod, "example+ client.jar", "example+ client.zip")},
            {"downloadUrl", JValue.CreateNull()}, {"fileLength", 776684}, {"fileStatus", 4}, {"isAvailable", True}, {"isServerPack", False}, {"isEarlyAccessContent", False},
            {"fileDate", "2026-10-20T00:00:00Z"}, {"releaseType", 1}, {"gameVersions", New JArray("1.20.1", "Forge")}, {"hashes", New JArray(New JObject From {{"algo", 1}, {"value", New String("a"c, 40)}})}}
    End Function
    Friend Async Function VerifyAsync(passed As List(Of String)) As Task
        For Each kind In {CommunityResourceKind.Mod, CommunityResourceKind.DataPack, CommunityResourceKind.ResourcePack, CommunityResourceKind.Shader, CommunityResourceKind.Modpack}
            Dim file = FileData(kind), data = ModpackCatalogChecks.CfProject(453763)
            data("classId") = CommunityResources.ClassId(kind)
            data("latestFiles") = New JArray(file.DeepClone())
            data("latestFilesIndexes") = New JArray(New JObject From {{"fileId", 9261871}, {"filename", file("fileName").DeepClone()}, {"gameVersion", "1.21.1"}},
                New JObject From {{"fileId", 9261871}, {"filename", file("fileName").DeepClone()}, {"gameVersion", "1.20.1"}},
                New JObject From {{"fileId", 9261871}, {"filename", file("fileName").DeepClone()}, {"gameVersion", "Forge 1.99"}})
            Using client As New ModpackCatalogClient(New Handler With {.Document = data}, "")
                Dim project = Await client.GetProjectAsync(ModpackPlatform.CurseForge, "453763", CancellationToken.None, kind)
                Check(project.GameVersions.OrderBy(Function(value) value).SequenceEqual({"1.20.1", "1.21.1"}), "CF versions union latest files and indexes without loader labels or duplicate tags: " & kind.ToString(), passed)
                Check(project.Loaders.SequenceEqual({"Forge"}), "CF project metadata knows its resource kind before accepting jar or zip declarations: " & kind.ToString(), passed)
                Dim page = Await client.SearchAsync(ModpackPlatform.CurseForge, New ModpackQuery With {.Kind = kind}, 0, CancellationToken.None)
                Check(page.Projects.Single().GameVersions.SequenceEqual(project.GameVersions), "CF search and detail preserve the same project version metadata: " & kind.ToString(), passed)
                Dim release = ModpackCatalogClient.ParseRelease(project, file)
                Check(release IsNot Nothing AndAlso release.Url = "https://edge.forgecdn.net/files/9262/896/" & Uri.EscapeDataString(CStr(file("fileName"))) AndAlso release.Sha1 = New String("a"c, 40) AndAlso release.Size = 776684,
                    "PCL CDN fallback binds a missing public file URL to its exact ID, escaped filename, size and digest: " & kind.ToString(), passed)
                file("downloadUrl") = "https://edge.forgecdn.net/files/9262/897/wrong.zip"
                Dim rejected = False
                Try
                    ModpackCatalogClient.ParseRelease(project, file)
                Catch ex As InvalidDataException
                    rejected = True
                End Try
                Check(rejected, "an explicitly mismatched CF URL cannot silently fall back to another file: " & kind.ToString(), passed)
            End Using
        Next
        Dim owner As New ModpackProject With {.Platform = ModpackPlatform.CurseForge, .Kind = CommunityResourceKind.ResourcePack, .Id = "453763"}
        For Each field In {"isServerPack", "isAvailable", "isEarlyAccessContent", "fileStatus", "hashes", "fileLength"}
            Dim data = FileData(owner.Kind)
            Select Case field
                Case "isServerPack", "isEarlyAccessContent" : data(field) = True
                Case "isAvailable" : data(field) = False
                Case "fileStatus" : data(field) = 3
                Case "hashes" : data(field) = New JArray()
                Case "fileLength" : data(field) = 0
            End Select
            Check(ModpackCatalogClient.ParseRelease(owner, data) Is Nothing, "a missing URL never relaxes public client file validation: " & field, passed)
        Next
        Dim invalid = FileData(owner.Kind) : invalid("modId") = 453764
        Dim wrongOwner = False
        Try
            ModpackCatalogClient.ParseRelease(owner, invalid)
        Catch ex As InvalidDataException
            wrongOwner = True
        End Try
        Check(wrongOwner, "PCL fallback cannot accept another project's release identity", passed)
    End Function
End Module
