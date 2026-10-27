Imports System.IO
Imports System.Text
Imports System.Threading
Imports PclDownloadBridge.Core

Friend Module ArchiveCancellationChecks
    Private Function Release(platform As ModpackPlatform, kind As CommunityResourceKind, bytes As Byte()) As ModpackRelease
        Dim fileName = "fixture" & If(kind = CommunityResourceKind.Mod, ".jar", If(kind = CommunityResourceKind.Modpack AndAlso platform = ModpackPlatform.Modrinth, ".mrpack", ".zip"))
        Return New ModpackRelease With {.Kind = kind, .Platform = platform, .ProjectId = "123", .Id = "1234567", .FileName = fileName,
            .Url = If(platform = ModpackPlatform.Modrinth, "https://cdn.modrinth.com/data/", "https://edge.forgecdn.net/files/1234/567/") & fileName,
            .Size = bytes.Length, .Sha1 = ModrinthChecks.Hash(bytes)}
    End Function

    Private Async Function CancelledAcquireAsync(release As ModpackRelease, cache As String, policy As SourcePolicy, token As CancellationToken, progress As IProgress(Of TransferProgress)) As Task(Of Boolean)
        Try
            Await ModpackArchiveInstaller.AcquireAsync(release, cache, policy, progress, token)
        Catch ex As OperationCanceledException
            Return ex.CancellationToken = token
        Catch ex As IOException
            Return False
        End Try
        Return False
    End Function

    Friend Async Function RunAsync(root As String) As Task(Of List(Of String))
        root = Path.Combine(root, "archive-cancellation") : Directory.CreateDirectory(root)
        Dim results As New List(Of Tuple(Of String, Boolean))
        Using server As New TestServer(), cancel As New CancellationTokenSource()
            cancel.Cancel()
            Dim policy As New ModrinthChecks.Policy(server.Url), bytes = Encoding.UTF8.GetBytes("unchanged fixture content")
            For Each platform In {ModpackPlatform.CurseForge, ModpackPlatform.Modrinth}
                For Each kind In {CommunityResourceKind.Modpack, CommunityResourceKind.Mod, CommunityResourceKind.DataPack, CommunityResourceKind.ResourcePack, CommunityResourceKind.Shader}
                    Dim release = ArchiveCancellationChecks.Release(platform, kind, bytes)
                    For Each blocked In {False, True}
                        Dim cache = Path.Combine(root, platform.ToString() & "-" & kind.ToString() & "-" & blocked), reports = 0
                        If blocked Then File.WriteAllBytes(cache, bytes)
                        Dim cancelled = Await CancelledAcquireAsync(release, cache, policy, cancel.Token, New ModrinthChecks.Progress(Sub(value) reports += 1))
                        Dim unchanged = If(blocked, File.Exists(cache) AndAlso File.ReadAllBytes(cache).SequenceEqual(bytes), Not Directory.Exists(cache) AndAlso Not File.Exists(cache))
                        results.Add(Tuple.Create("cancelled archive acquisition preserves token, cache and progress: " & platform.ToString() & "/" & kind.ToString() & "/blocked=" & blocked, cancelled AndAlso unchanged AndAlso reports = 0))
                    Next
                Next
            Next
            results.Add(Tuple.Create("cancelled acquisitions issue no metadata or payload requests", server.Requests.IsEmpty))

            Dim missing = Path.Combine(root, "missing.zip"), broken = Path.Combine(root, "broken.zip")
            File.WriteAllBytes(broken, bytes)
            Dim cf = CurseForgeChecks.Pack(root, "cancelled-read"), mr = ModrinthChecks.Pack(root, "cancelled-read")
            For Each source In {missing, broken, cf, mr}
                Dim before = If(File.Exists(source), File.ReadAllBytes(source), Nothing), cancelled = False
                Try
                    ModpackArchiveInstaller.ReadPackage(source, cancel.Token)
                Catch ex As OperationCanceledException
                    cancelled = ex.CancellationToken = cancel.Token
                Catch ex As IOException
                Catch ex As InvalidDataException
                End Try
                results.Add(Tuple.Create("cancelled local import keeps cancellation semantics and source bytes: " & Path.GetFileName(source), cancelled AndAlso If(before Is Nothing, Not File.Exists(source), File.ReadAllBytes(source).SequenceEqual(before))))
            Next
            For Each platform In {ModpackPlatform.CurseForge, ModpackPlatform.Modrinth}
                Dim archive = File.ReadAllBytes(If(platform = ModpackPlatform.Modrinth, mr, cf)), release = ArchiveCancellationChecks.Release(platform, CommunityResourceKind.Modpack, archive)
                server.Files(New Uri(release.Url).AbsolutePath) = archive
                Dim cache = Path.Combine(root, "retry-" & platform.ToString())
                Await CancelledAcquireAsync(release, cache, policy, cancel.Token, Nothing)
                Dim downloaded = Await ModpackArchiveInstaller.AcquireAsync(release, cache, policy, Nothing, CancellationToken.None)
                Dim requests = server.Requests.Values.Sum(), observed = False
                Dim cancelled = Await CancelledAcquireAsync(release, cache, policy, cancel.Token, New ModrinthChecks.Progress(Sub(value) observed = True))
                results.Add(Tuple.Create("cancelled retry preserves a verified archive and remains silent: " & platform.ToString(), cancelled AndAlso Not observed AndAlso File.ReadAllBytes(downloaded).SequenceEqual(archive) AndAlso requests = server.Requests.Values.Sum()))
                Dim reused = Await ModpackArchiveInstaller.AcquireAsync(release, cache, policy, Nothing, CancellationToken.None)
                results.Add(Tuple.Create("fresh retry reuses verified content without another request: " & platform.ToString(), reused = downloaded AndAlso File.ReadAllBytes(reused).SequenceEqual(archive) AndAlso requests = server.Requests.Values.Sum()))
                Dim parsed = ModpackArchiveInstaller.ReadPackage(reused, CancellationToken.None)
                results.Add(Tuple.Create("fresh retry detects and reads the original archive format: " & platform.ToString(), If(platform = ModpackPlatform.Modrinth, TypeOf parsed Is ModrinthPackage, TypeOf parsed Is CurseForgePackage)))
            Next
        End Using
        Dim failed = results.Where(Function(item) Not item.Item2).ToArray()
        If failed.Length > 0 Then Throw New InvalidOperationException("Archive cancellation failures: " & failed.Length & "/" & results.Count & vbCrLf & String.Join(vbCrLf, failed.Select(Function(item) item.Item1)))
        Return results.Select(Function(item) item.Item1).ToList()
    End Function
End Module
