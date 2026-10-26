Imports System.IO
Imports System.Threading
Imports Newtonsoft.Json.Linq

Partial Public NotInheritable Class ModpackCatalogClient
    ' PCL 5ef81e4 LocalResourceLoaders: identify the actual installed bytes;
    ' never use a display name or a guessed project ID to offer an update.
    Public Async Function CheckLocalModsAsync(instance As InstalledInstance, files As IEnumerable(Of LocalModFingerprint), token As CancellationToken) As Task(Of LocalModUpdateCheckResult)
        Dim entries = files.ToList(), matches As New List(Of LocalModUpdate), errors As New List(Of String)
        Dim checks = {CheckLocalPlatformAsync(ModpackPlatform.Modrinth, instance, entries, token), CheckLocalPlatformAsync(ModpackPlatform.CurseForge, instance, entries, token)}
        ' Observe both platform tasks even when their shared caller cancels.
        Dim results = Await Task.WhenAll(checks).ConfigureAwait(False)
        For Each result In results
            matches.AddRange(result.Matches) : errors.AddRange(result.Errors)
        Next
        token.ThrowIfCancellationRequested()
        Return New LocalModUpdateCheckResult(matches.GroupBy(Function(item) item.Entry.FilePath, StringComparer.OrdinalIgnoreCase).
            Select(Function(group) group.OrderByDescending(Function(item) item.HasUpdate).ThenByDescending(Function(item) If(item.Release, item.Current).Published).
                ThenByDescending(Function(item) item.Project.Platform = ModpackPlatform.Modrinth).First()), errors)
    End Function

    Private Async Function CheckLocalPlatformAsync(platform As ModpackPlatform, instance As InstalledInstance, entries As List(Of LocalModFingerprint), token As CancellationToken) As Task(Of LocalModUpdateCheckResult)
        Dim matches As New List(Of LocalModUpdate)
        Try
            For offset = 0 To entries.Count - 1 Step 40
                token.ThrowIfCancellationRequested()
                Dim batch = entries.Skip(offset).Take(40).ToList()
                matches.AddRange(Await If(platform = ModpackPlatform.Modrinth, CheckRinthModsAsync(instance, batch, token), CheckCurseModsAsync(instance, batch, token)).ConfigureAwait(False))
            Next
            Return New LocalModUpdateCheckResult(matches)
        Catch ex As OperationCanceledException When token.IsCancellationRequested
            Throw
        Catch ex As Exception
            Return New LocalModUpdateCheckResult(matches, {platform.ToString() & "：" & ex.Message})
        End Try
    End Function

    Private Async Function CheckRinthModsAsync(instance As InstalledInstance, batch As List(Of LocalModFingerprint), token As CancellationToken) As Task(Of List(Of LocalModUpdate))
        Dim identified = TryCast(Await TokenAsync("https://api.modrinth.com/v2/version_files", token,
            New JObject From {{"hashes", New JArray(batch.Select(Function(item) item.Sha1).Distinct())}, {"algorithm", "sha1"}}).ConfigureAwait(False), JObject)
        If identified Is Nothing Then Throw New InvalidDataException("Modrinth 文件信息无效。")
        Dim known As New List(Of LocalModUpdate)
        For Each file In batch
            Dim raw = TryCast(identified(file.Sha1), JObject)
            If raw Is Nothing Then Continue For
            Dim project As New ModpackProject With {.Kind = CommunityResourceKind.Mod, .Platform = ModpackPlatform.Modrinth, .Id = Text(raw, "project_id"), .Name = file.Entry.DisplayName}
            Dim current = ParseRelease(project, raw)
            If Not file.Matches(current) Then Continue For
            known.Add(New LocalModUpdate(file.Entry, project, current, Nothing))
        Next
        Dim results As New List(Of LocalModUpdate)
        For Each group In known.GroupBy(Function(item) String.Join(",", item.Current.Loaders.Select(Function(value) value.ToLowerInvariant()).Distinct().OrderBy(Function(value) value)))
            Dim hashes = batch.Where(Function(file) group.Any(Function(item) item.Entry Is file.Entry)).ToList()
            Dim updated = TryCast(Await TokenAsync("https://api.modrinth.com/v2/version_files/update", token,
                New JObject From {{"hashes", New JArray(hashes.Select(Function(item) item.Sha1).Distinct())}, {"algorithm", "sha1"},
                    {"loaders", New JArray(group.First().Current.Loaders)}, {"game_versions", New JArray(instance.MinecraftVersion)}}).ConfigureAwait(False), JObject)
            If updated Is Nothing Then Throw New InvalidDataException("Modrinth 更新信息无效。")
            For Each item In group
                Dim file = hashes.First(Function(value) value.Entry Is item.Entry), raw = TryCast(updated(file.Sha1), JObject), candidate As ModpackRelease = Nothing
                If raw IsNot Nothing AndAlso Text(raw, "project_id") = item.Project.Id Then candidate = ParseRelease(item.Project, raw)
                results.Add(New LocalModUpdate(item.Entry, item.Project, item.Current, If(LocalModUpdate.Compatible(instance, item.Current, candidate), candidate, Nothing)))
            Next
        Next
        Return results
    End Function

    Private Async Function CheckCurseModsAsync(instance As InstalledInstance, batch As List(Of LocalModFingerprint), token As CancellationToken) As Task(Of List(Of LocalModUpdate))
        Dim root = TryCast(Await TokenAsync("https://api.curseforge.com/v1/fingerprints/432", token,
            New JObject From {{"fingerprints", New JArray(batch.Select(Function(item) item.CurseForge).Distinct())}}).ConfigureAwait(False), JObject)
        If root Is Nothing Then Throw New InvalidDataException("CurseForge 文件信息无效。")
        Dim known As New List(Of LocalModUpdate)
        For Each match In ArrayValue(ObjectValue(root, "data"), "exactMatches").OfType(Of JObject)()
            Dim raw = ObjectValue(match, "file"), fingerprint = Number(raw, "fileFingerprint")
            Dim project As New ModpackProject With {.Kind = CommunityResourceKind.Mod, .Platform = ModpackPlatform.CurseForge, .Id = Text(match, "id")}
            Dim current = ParseRelease(project, raw)
            For Each file In batch.Where(Function(item) CLng(item.CurseForge) = fingerprint AndAlso item.Matches(current))
                project.Name = file.Entry.DisplayName
                known.Add(New LocalModUpdate(file.Entry, project, current, Nothing))
            Next
        Next
        If known.Count = 0 Then Return known
        Dim projectsRoot = TryCast(Await TokenAsync("https://api.curseforge.com/v1/mods", token,
            New JObject From {{"modIds", New JArray(known.Select(Function(item) Long.Parse(item.Project.Id)).Distinct())}}).ConfigureAwait(False), JObject)
        If projectsRoot Is Nothing Then Throw New InvalidDataException("CurseForge 项目信息无效。")
        Dim ids As New HashSet(Of Long), projects As New Dictionary(Of String, ModpackProject)(StringComparer.Ordinal)
        For Each raw In ArrayValue(projectsRoot, "data").OfType(Of JObject)()
            Dim id = Text(raw, "id")
            If Not known.Any(Function(item) item.Project.Id = id) OrElse Not CommunityResources.Matches(ModpackPlatform.CurseForge, raw, CommunityResourceKind.Mod) Then Continue For
            projects(id) = ParseProject(ModpackPlatform.CurseForge, raw, False, CommunityResourceKind.Mod)
            For Each index In ArrayValue(raw, "latestFilesIndexes").OfType(Of JObject)()
                If Text(index, "gameVersion") <> instance.MinecraftVersion Then Continue For
                Dim loader = CurseLoader(Number(index, "modLoader"))
                If loader IsNot Nothing AndAlso known.Any(Function(item) item.Project.Id = id AndAlso item.Current.Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase)) Then ids.Add(Number(index, "fileId"))
            Next
        Next
        Dim candidates As New List(Of ModpackRelease)
        For offset = 0 To ids.Count - 1 Step 40
            Dim rawFiles = TryCast(Await TokenAsync("https://api.curseforge.com/v1/mods/files", token,
                New JObject From {{"fileIds", New JArray(ids.Skip(offset).Take(40))}}).ConfigureAwait(False), JObject)
            If rawFiles Is Nothing Then Throw New InvalidDataException("CurseForge 更新信息无效。")
            For Each raw In ArrayValue(rawFiles, "data").OfType(Of JObject)()
                Dim project As ModpackProject = Nothing
                If Not ids.Contains(Number(raw, "id")) OrElse Not projects.TryGetValue(Text(raw, "modId"), project) Then Continue For
                Dim candidate = ParseRelease(project, raw)
                If candidate IsNot Nothing Then candidates.Add(candidate)
            Next
        Next
        Return known.Select(Function(item) New LocalModUpdate(item.Entry, If(projects.ContainsKey(item.Project.Id), projects(item.Project.Id), item.Project), item.Current,
            candidates.Where(Function(candidate) LocalModUpdate.Compatible(instance, item.Current, candidate)).OrderByDescending(Function(candidate) candidate.Published).FirstOrDefault())).ToList()
    End Function
    Private Shared Function CurseLoader(value As Long) As String
        Select Case value
            Case 1 : Return "forge"
            Case 3 : Return "liteloader"
            Case 4 : Return "fabric"
            Case 5 : Return "quilt"
            Case 6 : Return "neoforge"
            Case Else : Return Nothing
        End Select
    End Function
End Class
