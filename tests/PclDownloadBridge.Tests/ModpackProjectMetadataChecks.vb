Imports System.Net
Imports System.Net.Http
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module ModpackProjectMetadataChecks
    Private Sub Check(value As Boolean, label As String, passed As List(Of String))
        If Not value Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub

    Private Class Handler
        Inherits HttpMessageHandler
        Friend Property Document As JObject
        Friend ReadOnly Addresses As New List(Of String)
        Protected Overrides Function SendAsync(request As HttpRequestMessage, token As CancellationToken) As Task(Of HttpResponseMessage)
            token.ThrowIfCancellationRequested() : Addresses.Add(request.RequestUri.AbsoluteUri)
            Dim search = request.RequestUri.AbsolutePath.EndsWith("/search", StringComparison.Ordinal), body As JObject
            If request.RequestUri.Host = "api.modrinth.com" Then
                body = If(search, New JObject From {{"hits", New JArray(Document.DeepClone())}, {"total_hits", 1}}, DirectCast(Document.DeepClone(), JObject))
            Else
                body = If(search, New JObject From {{"data", New JArray(Document.DeepClone())}, {"pagination", New JObject From {{"totalCount", 1}}}}, New JObject From {{"data", Document.DeepClone()}})
            End If
            Return Task.FromResult(New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New StringContent(body.ToString(), Encoding.UTF8, "application/json")})
        End Function
    End Class

    Friend Async Function VerifyAsync(passed As List(Of String)) As Task
        Dim data = ModpackCatalogChecks.MrProject("metadata")
        data("categories") = New JArray("multiplayer", "lightweight", "optimization", "fabric")
        data("loaders") = New JArray("neoforge", "fabric", "quilt")
        Dim handler As New Handler With {.Document = data}, original = data.ToString()
        Using client As New ModpackCatalogClient(handler, "")
            Dim project = Await client.GetProjectAsync(ModpackPlatform.Modrinth, "metadata", CancellationToken.None)
            Dim tags = TryCast(JObject.FromObject(project)("Tags"), JArray)
            Check(tags IsNot Nothing AndAlso tags.Values(Of String)().SequenceEqual({"多人", "轻量", "性能优化"}), "Modrinth project categories survive the accepted catalogue response as PCL translated tags", passed)
            Check(project.Loaders.SequenceEqual({"Fabric", "NeoForge", "Quilt"}), "MR loaders combine categories and project declarations without dropping an unsupported Quilt label", passed)
            Dim page = Await client.SearchAsync(ModpackPlatform.Modrinth, New ModpackQuery(), 0, CancellationToken.None)
            Check(page.Projects.Count = 1 AndAlso page.Projects(0).Tags.SequenceEqual(project.Tags) AndAlso page.Projects(0).Loaders.SequenceEqual(project.Loaders), "search and detail parse the same optional MR project metadata", passed)
            Check(data.ToString() = original AndAlso handler.Addresses.Count = 2 AndAlso handler.Addresses.All(Function(value) Not value.Contains("/version")), "reading project labels neither mutates response JSON nor fetches versions or payloads", passed)
            Dim releaseJson = ModpackCatalogChecks.MrRelease("metadata"), release = ModpackCatalogClient.ParseRelease(project, releaseJson)
            Check(release.Loaders.SequenceEqual({"fabric"}) AndAlso release.GameVersions.SequenceEqual({"1.20.1"}) AndAlso release.Id = "version1" AndAlso release.Sha1 = New String("a"c, 40), "project-wide loader labels cannot overwrite the selected archive's loaders, versions, identity or digest", passed)
        End Using
        Check(ModpackProjectMetadata.ReadLoaders(New ModpackProject With {.Platform = ModpackPlatform.Modrinth}, New JObject From {{"categories", New JArray("forge", "fabric", "fabric")}}).SequenceEqual({"Forge", "Fabric"}), "MR search category loaders remain available when a separate loaders field is absent", passed)
        Dim mrTags = {"technology", "magic", "adventure", "utility", "optimization", "vanilla-like", "realistic", "multiplayer", "challenging", "combat", "quests", "kitchen-sink", "lightweight"}
        Dim mrLabels = {"科技", "魔法", "冒险", "实用", "性能优化", "原版风", "写实风", "多人", "硬核", "战斗", "任务", "水槽包", "轻量"}
        For index = 0 To mrTags.Length - 1
            Check(ModpackProjectMetadata.ReadTags(ModpackPlatform.Modrinth, New JArray(mrTags(index), mrTags(index), "unknown", "forge")).SequenceEqual({mrLabels(index)}), "MR pack category translation matches fixed PCL and deduplicates: " & mrTags(index), passed)
        Next
        Dim cfIds = {4484, 4479, 4483, 4478, 4472, 4473, 4475, 4476, 4477, 4474, 4736, 5128, 4487, 4480, 4481, 4482}
        Dim cfLabels = {"多人", "硬核", "战斗", "任务", "科技", "魔法", "冒险", "探索", "小游戏", "科幻", "空岛", "原版改良", "FTB", "基于地图", "轻量", "大型"}
        For index = 0 To cfIds.Length - 1
            Check(ModpackProjectMetadata.ReadTags(ModpackPlatform.CurseForge, New JArray(New JObject From {{"id", cfIds(index)}}, New JObject From {{"id", cfIds(index)}}, New JObject From {{"id", 999999}})).SequenceEqual({cfLabels(index)}), "CF pack category ID translation matches fixed PCL and ignores unknown IDs: " & cfIds(index), passed)
        Next
        Check(ModpackProjectMetadata.ReadTags(ModpackPlatform.CurseForge, New JArray(New JObject From {{"id", 4472}}, New JObject From {{"id", 4484}}, New JObject From {{"id", 5128}})).SequenceEqual({"原版改良", "多人", "科技"}), "CF categories retain PCL's descending ID order rather than their response order", passed)
        Dim mixed As New JArray(JValue.CreateNull(), True, 4484, "multiplayer", New JObject From {{"id", "4484"}}, New JObject From {{"id", 4484.5}}, New JObject From {{"id", 9999999999999999L}}, New JObject From {{"id", 4484}})
        Check(ModpackProjectMetadata.ReadTags(ModpackPlatform.CurseForge, mixed).SequenceEqual({"多人"}), "malformed CF category members are skipped without losing a recognized category", passed)
        Check(ModpackProjectMetadata.ReadTags(ModpackPlatform.Modrinth, New JArray(True, JValue.CreateNull(), New JObject(), New String("x"c, 101), "multiplayer", "multiplayer")).SequenceEqual({"多人"}), "malformed or oversized MR optional tags cannot suppress recognized badges", passed)
        Check(ModpackProjectMetadata.LoaderText({"quilt-loader", "FABRIC", Nothing, "forge", "neo-forge", "fabric-loader", "liteloader", "rift", "minecraft", "unknown", " vanilla "}) = "Forge / LiteLoader / Fabric / NeoForge / Quilt / Rift / 原版", "project loaders use canonical source order, importer aliases and explicit labels instead of inferring any-loader compatibility", passed)
        Check(ModpackProjectMetadata.LoaderText(Nothing) = "" AndAlso ModpackProjectMetadata.LoaderText({"unknown", "", Nothing}) = "", "missing or unknown loader declarations remain unspecified rather than vanilla or universal", passed)

        Dim cfData = ModpackCatalogChecks.CfProject(12)
        cfData("categories") = New JArray(New JObject From {{"id", 4481}}, New JObject From {{"id", 4472}})
        cfData("latestFiles") = New JArray(ClientFile())
        handler = New Handler With {.Document = cfData}
        Using client As New ModpackCatalogClient(handler, "")
            Dim project = Await client.GetProjectAsync(ModpackPlatform.CurseForge, "12", CancellationToken.None)
            Dim page = Await client.SearchAsync(ModpackPlatform.CurseForge, New ModpackQuery(), 0, CancellationToken.None)
            Check(project.Tags.SequenceEqual({"轻量", "科技"}) AndAlso project.Loaders.SequenceEqual({"Forge"}) AndAlso page.Projects(0).Tags.SequenceEqual(project.Tags) AndAlso page.Projects(0).Loaders.SequenceEqual(project.Loaders), "CF search and detail retain category and bound client-file loader declarations", passed)
            Check(project.GameVersions.SequenceEqual({"1.20.1"}) AndAlso project.Id = "12" AndAlso handler.Addresses.Count = 2, "CF latest-file versions remain explicit display declarations without inventing a compatibility range or extra network requests", passed)
        End Using
        Dim mutations As Tuple(Of String, JToken)() = {
            Tuple.Create(Of String, JToken)("modId", New JValue(13)), Tuple.Create(Of String, JToken)("modId", JValue.CreateNull()), Tuple.Create(Of String, JToken)("modId", New JValue(12.5)),
            Tuple.Create(Of String, JToken)("isServerPack", New JValue(True)), Tuple.Create(Of String, JToken)("isServerPack", New JValue("false")),
            Tuple.Create(Of String, JToken)("isAvailable", New JValue(False)), Tuple.Create(Of String, JToken)("isAvailable", New JObject()),
            Tuple.Create(Of String, JToken)("isEarlyAccessContent", New JValue(True)), Tuple.Create(Of String, JToken)("fileStatus", New JValue(3)), Tuple.Create(Of String, JToken)("fileStatus", New JValue("4")),
            Tuple.Create(Of String, JToken)("fileName", New JValue("server.mrpack")), Tuple.Create(Of String, JToken)("fileName", New JValue("../client.zip")),
            Tuple.Create(Of String, JToken)("gameVersions", New JObject()), Tuple.Create(Of String, JToken)("gameVersions", New JArray("1.20.1", "unknown"))}
        Dim cfProject As New ModpackProject With {.Platform = ModpackPlatform.CurseForge, .Id = "12"}
        For index = 0 To mutations.Length - 1
            Dim changed = ClientFile() : changed(mutations(index).Item1) = mutations(index).Item2.DeepClone()
            changed("gameVersions") = If(mutations(index).Item1 = "gameVersions", changed("gameVersions"), New JArray("1.20.1", "fabric"))
            Check(ModpackProjectMetadata.ReadLoaders(cfProject, New JObject From {{"latestFiles", New JArray(changed, ClientFile())}}).SequenceEqual({"Forge"}), "unusable or foreign CF file metadata cannot contribute a misleading loader: " & index, passed)
        Next
        For Each field In {"isServerPack", "isAvailable", "isEarlyAccessContent", "fileStatus"}
            For Each missing In {False, True}
                Dim file = ClientFile()
                If missing Then file.Remove(field) Else file(field) = JValue.CreateNull()
                Check(ModpackProjectMetadata.ReadLoaders(cfProject, New JObject From {{"latestFiles", New JArray(file)}}).SequenceEqual({"Forge"}), "optional CF metadata flags retain existing missing/null defaults: " & field & " / " & missing, passed)
            Next
        Next
        Check(ModpackProjectMetadata.ReadLoaders(cfProject, New JObject From {{"latestFilesIndexes", New JArray(New JObject From {{"modLoader", 4}, {"gameVersion", "1.20.1"}})}}).Length = 0, "CF numeric index metadata is not guessed into project loader support", passed)
        For Each platform In {ModpackPlatform.Modrinth, ModpackPlatform.CurseForge}
            For Each field In If(platform = ModpackPlatform.Modrinth, New String() {"categories", "loaders"}, New String() {"categories", "latestFiles"})
                Dim invalid As JToken() = {Nothing, JValue.CreateNull(), New JValue(True), New JValue(1), New JValue("bad"), New JObject()}
                For index = 0 To invalid.Length - 1
                    Dim document = If(platform = ModpackPlatform.Modrinth, ModpackCatalogChecks.MrProject("metadata"), ModpackCatalogChecks.CfProject(12))
                    If invalid(index) Is Nothing Then document.Remove(field) Else document(field) = invalid(index).DeepClone()
                    Using client As New ModpackCatalogClient(New Handler With {.Document = document}, "")
                        Dim project = Await client.GetProjectAsync(platform, If(platform = ModpackPlatform.Modrinth, "metadata", "12"), CancellationToken.None)
                        Check(project.Tags.Length = 0 AndAlso project.Loaders.Length = 0 AndAlso project.Downloads = 1000 AndAlso project.Name.StartsWith("Same pack", StringComparison.Ordinal), "malformed optional project metadata preserves a usable catalogue project: " & platform & " / " & field & " / " & index, passed)
                    End Using
                Next
            Next
        Next
    End Function

    Private Function ClientFile() As JObject
        Return New JObject From {{"modId", 12}, {"fileName", "client.zip"}, {"gameVersions", New JArray("1.20.1", "Forge")}, {"isAvailable", True}, {"isServerPack", False}, {"isEarlyAccessContent", False}, {"fileStatus", 4}}
    End Function
End Module
