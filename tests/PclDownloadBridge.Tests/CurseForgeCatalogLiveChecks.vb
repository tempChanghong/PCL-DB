Imports System.IO
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module CurseForgeCatalogLiveChecks
    ' Read-only live companion to the controlled catalogue fixtures. Exercise
    ' the same search -> project -> complete versions path as the detail page.
    Friend Async Function RunAsync(output As String) As Task
        output = Path.GetFullPath(output)
        Directory.CreateDirectory(output)
        Dim projects As New JArray()
        Using client As New ModpackCatalogClient(), deadline As New CancellationTokenSource(TimeSpan.FromMinutes(2))
            Dim page = Await client.SearchAsync(ModpackPlatform.CurseForge, New ModpackQuery With {.Platforms = ModpackPlatform.CurseForge}, 0, deadline.Token)
            If page.Projects.Count < 2 Then Throw New InvalidDataException("Live CF search returned fewer than two packs.")
            For Each hit In page.Projects.Take(2)
                Dim project = Await client.GetProjectAsync(hit.Platform, hit.Id, deadline.Token)
                Dim versions = Await client.GetVersionsAsync(project, deadline.Token)
                If versions.Count = 0 Then Throw New InvalidDataException("Live CF project has no verified client archives: " & project.Id)
                If versions.Any(Function(release) release.ProjectId <> project.Id OrElse (release.Sha1 Is Nothing AndAlso release.Md5 Is Nothing)) Then Throw New InvalidDataException("Live CF archive identity or digest missing.")
                projects.Add(New JObject From {{"id", project.Id}, {"name", project.Name}, {"versions", versions.Count},
                    {"latestFileId", versions(0).Id}, {"latestFileName", versions(0).FileName}, {"latestSha1", versions(0).Sha1}, {"latestMd5", versions(0).Md5}})
            Next
        End Using
        Dim report As New JObject From {{"succeeded", True}, {"checkedAtUtc", DateTimeOffset.UtcNow.ToString("O")},
            {"path", "search -> project -> complete versions"}, {"projects", projects}, {"archivesDownloaded", False}, {"gameFilesWritten", False}}
        File.WriteAllText(Path.Combine(output, "report.json"), report.ToString())
        Console.WriteLine(report.ToString())
    End Function
End Module
