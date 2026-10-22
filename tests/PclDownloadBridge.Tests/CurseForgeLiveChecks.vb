Imports System.IO
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module CurseForgeLiveChecks
    Friend Async Function RunAsync(path As String, output As String) As Task
        output = IO.Path.GetFullPath(output) : Directory.CreateDirectory(output)
        Dim package = CurseForgePackage.Read(path), files As New List(Of CurseForgeResolvedFile), errors As New List(Of String)
        Dim policy As New SourcePolicy With {.Preference = SourcePreference.OfficialFirst, .VersionPreference = SourcePreference.OfficialFirst}
        Using resolver As New CurseForgeFileResolver(policy), gate As New SemaphoreSlim(4)
            Dim tasks = package.References.Select(Async Function(reference)
                Await gate.WaitAsync().ConfigureAwait(False)
                Try
                    Dim file = Await resolver.ResolveFileAsync(reference, package.Minecraft, CancellationToken.None).ConfigureAwait(False)
                    SyncLock files
                        files.Add(file)
                    End SyncLock
                Catch ex As Exception
                    SyncLock errors
                        errors.Add($"{reference.ProjectId}/{reference.FileId} required={reference.Required}: {ex.Message}")
                    End SyncLock
                Finally
                    gate.Release()
                End Try
            End Function).ToArray()
            Await Task.WhenAll(tasks).ConfigureAwait(False)
        End Using
        Console.WriteLine($"LIVE CF queried {package.References.Count}; resolved {files.Count}; failures {errors.Count}")
        Dim payload = IO.Path.Combine(output, "content"), network As Long, reused As Integer
        Using engine As New DownloadEngine(policy, Nothing, {}, 8, output, True)
            Await engine.DownloadAsync(payload, files.Select(Function(item) item.Download), CancellationToken.None).ConfigureAwait(False)
            network = engine.NetworkBytes : reused = engine.Reused
        End Using
        Dim checked As New JArray()
        For Each file In files.OrderBy(Function(item) item.Reference.FileId)
            If Not SafeFiles.Matches(SafeFiles.UnderRoot(payload, file.Download.RelativePath), file.Download) Then Throw New InvalidDataException("Live CF content mismatch: " & file.Reference.FileId)
            checked.Add(New JObject From {{"projectId", file.Reference.ProjectId}, {"fileId", file.Reference.FileId}, {"required", file.Reference.Required},
                {"path", file.Download.RelativePath}, {"size", file.Download.Size}, {"sha1", file.Download.Sha1}, {"md5", file.Download.Md5},
                {"s3SinglePartETag", file.Download.S3SinglePartETag}, {"informationSource", file.InformationSource}, {"checksumSource", file.ChecksumSource}})
        Next
        Dim gameRoot = IO.Path.Combine(output, "import-target"), blocked = False, failureMessage As String = Nothing
        Try
            Await New CurseForgeInstaller(policy).InstallAsync(package, gameRoot, "BetterMC-validation", {}, {}, Nothing, CancellationToken.None).ConfigureAwait(False)
        Catch ex As CurseForgeQueryException
            failureMessage = ex.Message
            blocked = errors.Count > 0 AndAlso Not Directory.Exists(gameRoot)
        End Try
        Dim report As New JObject From {{"sourcePath", package.SourcePath}, {"archiveSha256", package.Sha256}, {"name", package.Name}, {"minecraft", package.Minecraft},
            {"loader", package.LoaderKind.ToString()}, {"loaderVersion", package.LoaderVersion}, {"declaredFiles", package.References.Count}, {"resolvedFiles", files.Count},
            {"errors", New JArray(errors)}, {"verifiedFiles", checked}, {"networkBytes", network}, {"reused", reused}, {"originalImportBlockedBeforeGameRootWrites", blocked},
            {"originalImportFailure", failureMessage}, {"gameLaunched", False}}
        File.WriteAllText(IO.Path.Combine(output, "report.json"), report.ToString())
        If Not blocked AndAlso errors.Count > 0 Then Throw New InvalidOperationException("Original pack's missing required content did not block publication.")
        Console.WriteLine($"LIVE CF verified {checked.Count} payload files; transferred {network} bytes; original import blocked={blocked}; report={IO.Path.Combine(output, "report.json")}")
    End Function
End Module
