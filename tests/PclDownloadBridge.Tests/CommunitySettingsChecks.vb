Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module CommunitySettingsChecks
    Private Sub Check(ok As Boolean, label As String, passed As List(Of String))
        If Not ok Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub
    Private Class RouteHandler
        Inherits HttpMessageHandler
        Friend ReadOnly Calls As New List(Of String)
        Friend ReadOnly KeyHosts As New List(Of String)
        Friend FailOfficial As Boolean
        Protected Overrides Function SendAsync(request As HttpRequestMessage, token As CancellationToken) As Task(Of HttpResponseMessage)
            token.ThrowIfCancellationRequested()
            Calls.Add(request.RequestUri.AbsoluteUri)
            If request.Headers.Contains("x-api-key") Then KeyHosts.Add(request.RequestUri.Host)
            If FailOfficial AndAlso request.RequestUri.Host.StartsWith("api.", StringComparison.Ordinal) Then Return Task.FromResult(New HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
            Dim data As JObject
            If request.RequestUri.AbsolutePath.Contains("curseforge") OrElse request.RequestUri.Host = "api.curseforge.com" Then
                data = New JObject From {{"data", New JArray(New JObject From {{"id", 123456}, {"classId", 6945}, {"name", "CF datapack"}, {"slug", "cf-data"}, {"summary", "Fixture"}, {"downloadCount", 123}, {"dateModified", "2026-10-01T00:00:00Z"}})}, {"pagination", New JObject From {{"totalCount", 1}}}}
            Else
                data = New JObject From {{"hits", New JArray(CommunityResourceChecks.ProjectData(CommunityResourceKind.DataPack))}, {"total_hits", 1}}
            End If
            Return Task.FromResult(New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New StringContent(data.ToString(), Text.Encoding.UTF8, "application/json")})
        End Function
    End Class
    Friend Async Function VerifyAsync(passed As List(Of String)) As Task
        Dim defaults = CommunityResourcePreferences.Read(Nothing)
        Check(defaults.Source = 2 AndAlso defaults.FileNameStyle = 1 AndAlso defaults.LocalModStyle = 0, "community settings preserve fixed PCL defaults without external configuration writes", passed)
        Dim invalid = CommunityResourcePreferences.Read(JObject.Parse("{""source"":999,""fileNameStyle"":""2"",""localModStyle"":-1}"))
        Check(JToken.DeepEquals(defaults.ToJson(), invalid.ToJson()), "invalid community preference types and bounds fall back independently", passed)
        For mode = 0 To 2
            Dim roundTrip = CommunityResourcePreferences.Read(New CommunityResourcePreferences With {.Source = mode, .FileNameStyle = 4, .LocalModStyle = 1}.ToJson())
            Check(roundTrip.Source = mode AndAlso roundTrip.FileNameStyle = 4 AndAlso roundTrip.LocalModStyle = 1, "all community preference modes round-trip with independent filename and local style: " & mode, passed)
        Next
        Dim project As New ModpackProject With {.Kind = CommunityResourceKind.Mod, .Platform = ModpackPlatform.Modrinth, .Slug = "sodium", .Name = "Sodium", .Id = "AANobbMI"}
        Dim release As New ModpackRelease With {.Kind = CommunityResourceKind.Mod, .FileName = "sodium.jar"}
        Dim expected = {"【钠】sodium.jar", "[钠] sodium.jar", "钠-sodium.jar", "sodium-钠.jar", "sodium.jar"}
        For mode = 0 To 4
            Dim prefs As New CommunityResourcePreferences With {.FileNameStyle = mode}
            Check(prefs.SuggestedFileName(project, release) = expected(mode) AndAlso release.FileName = "sodium.jar" AndAlso project.Id = "AANobbMI", "filename format changes only the default suggestion and retains identity/extension: " & mode, passed)
        Next
        project.Slug = "not-in-database"
        Check(defaults.SuggestedFileName(project, release) = "sodium.jar", "untranslated projects keep the original download filename", passed)
        project.Platform = ModpackPlatform.CurseForge : project.Slug = "vampirism-become-a-vampire"
        Check(Not defaults.SuggestedFileName(project, release).Contains("/") AndAlso defaults.SuggestedFileName(project, release).Contains("／"), "translated aliases cannot turn filename suggestions into paths", passed)
        release.FileName = New String("s"c, 248) & ".jar"
        Check(defaults.SuggestedFileName(project, release).Length <= 255, "long source filenames retain a valid bounded suggestion without changing archive metadata", passed)
        release.FileName = "mod~build.jar"
        Check(New CommunityResourcePreferences With {.FileNameStyle = 4}.SuggestedFileName(project, release) = "mod-build.jar", "PCL's Mod tilde workaround applies even when translation is disabled", passed)
        For Each platform In {ModpackPlatform.CurseForge, ModpackPlatform.Modrinth}
            For mode = 0 To 2
                Dim handler As New RouteHandler
                Using client As New ModpackCatalogClient(handler, "fixture-key", mode)
                    Dim result = Await client.SearchAsync(platform, New ModpackQuery With {.Kind = CommunityResourceKind.DataPack, .Platforms = platform}, 0, CancellationToken.None)
                    Check(result.Projects.Count = 1 AndAlso result.Projects.Single().Kind = CommunityResourceKind.DataPack AndAlso
                        New Uri(handler.Calls.First()).Host = If(mode = 0, "mod.mcimirror.top", If(platform = ModpackPlatform.CurseForge, "api.curseforge.com", "api.modrinth.com")), "datapack source preference orders actual requests and retains resource kind: " & platform.ToString() & "/" & mode, passed)
                    Check(handler.KeyHosts.All(Function(host) host = "api.curseforge.com"), "CurseForge credentials are never sent to mirrors or Modrinth: " & platform.ToString() & "/" & mode, passed)
                End Using
            Next
        Next
        Dim fallback As New RouteHandler With {.FailOfficial = True}
        Using client As New ModpackCatalogClient(fallback, "fixture-key", 1)
            Dim result = Await client.SearchAsync(ModpackPlatform.CurseForge, New ModpackQuery With {.Kind = CommunityResourceKind.DataPack}, 0, CancellationToken.None)
            Check(result.Projects.Count = 1 AndAlso fallback.Calls.Count = 2 AndAlso New Uri(fallback.Calls.Last()).Host = "mod.mcimirror.top", "official datapack failure falls back to the mirror while preserving usable results", passed)
        End Using
        Dim missingKey As New RouteHandler
        Using client As New ModpackCatalogClient(missingKey, "", 2)
            Await client.SearchAsync(ModpackPlatform.CurseForge, New ModpackQuery With {.Kind = CommunityResourceKind.DataPack}, 0, CancellationToken.None)
            Check(missingKey.Calls.Count = 1 AndAlso New Uri(missingKey.Calls(0)).Host = "mod.mcimirror.top" AndAlso missingKey.KeyHosts.Count = 0, "missing official credentials use the public mirror without failed unauthenticated probes", passed)
        End Using
        Dim source As New SourcePolicy With {.Preference = SourcePreference.MirrorFirst, .CommunityPreference = SourcePreference.OfficialFirst}
        Check(source.Candidates("https://cdn.modrinth.com/data/a/file.jar", "modrinth-archive").First().Contains("cdn.modrinth.com") AndAlso source.Candidates("https://libraries.minecraft.net/a.jar", "library").First().Contains("bmclapi"), "community source selection stays independent of the vanilla/library source preference", passed)
        source.CommunityPreference = SourcePreference.MirrorFirst
        Check(source.Candidates("https://edge.forgecdn.net/files/a/file.jar", "curseforge-archive").First() = "https://mod.mcimirror.top/files/a/file.jar", "community file requests use PCL's mirror path without changing the original release URL", passed)
    End Function
End Module
