Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq

Public Class CurseForgeInstaller
    Private ReadOnly _policy As SourcePolicy
    Private ReadOnly _pclDirectory As String
    Private ReadOnly _resolver As CurseForgeFileResolver
    Private ReadOnly _target As LauncherInstallTarget
    Public Sub New(policy As SourcePolicy, Optional pclDirectory As String = Nothing, Optional resolver As CurseForgeFileResolver = Nothing, Optional launcherTarget As LauncherInstallTarget = Nothing)
        _policy = policy : _pclDirectory = pclDirectory : _resolver = resolver
        _target = launcherTarget
    End Sub
    Public Async Function InstallAsync(package As CurseForgePackage, root As String, instanceName As String, reuseRoots As IEnumerable(Of String), optionalFileIds As IEnumerable(Of Long), progress As IProgress(Of TransferProgress), token As CancellationToken, Optional connections As Integer = 16, Optional limit As Long = 0, Optional javaPath As String = Nothing) As Task(Of InstallResult)
        token.ThrowIfCancellationRequested()
        If package Is Nothing Then Throw New ArgumentNullException(NameOf(package))
        If reuseRoots Is Nothing Then Throw New ArgumentNullException(NameOf(reuseRoots))
        _target?.ValidateDestination(root)
        InstallPreflight.ValidateDestination(root, instanceName, False)
        Dim reuse = reuseRoots.ToList(), selected = If(optionalFileIds, Enumerable.Empty(Of Long)()).ToArray()
        Using plan As New CurseForgeInstallPlan(package, token)
            plan.ValidateTargets(instanceName)
            If _resolver Is Nothing Then
                Using resolver As New CurseForgeFileResolver(_policy)
                    plan.SetFiles(Await resolver.ResolveAsync(package, selected, token, progress).ConfigureAwait(False))
                End Using
            Else
                plan.SetFiles(Await _resolver.ResolveAsync(package, selected, token, progress).ConfigureAwait(False))
            End If
            plan.ValidateTargets(instanceName)
            Return Await ModpackInstallation.InstallAsync(plan, _policy, _pclDirectory, root, instanceName, reuse, progress, token, package.LoaderKind, package.LoaderVersion, connections, limit, javaPath, _target).ConfigureAwait(False)
        End Using
    End Function
End Class

Friend NotInheritable Class CurseForgeInstallPlan
    Inherits ModpackInstallPlan
    Private ReadOnly _package As CurseForgePackage
    Private _resolved As New List(Of CurseForgeResolvedFile)
    Friend Overrides ReadOnly Property Minecraft As String
        Get
            Return _package.Minecraft
        End Get
    End Property
    Protected Overrides ReadOnly Property OverrideEntries As IEnumerable(Of KeyValuePair(Of String, ZipArchiveEntry))
        Get
            Dim prefix = _package.BaseFolder & _package.OverridesFolder & "/"
            Return Archive.Entries.Where(Function(entry) Not CurseForgePackage.IsDirectory(entry) AndAlso entry.FullName.Replace("\"c, "/"c).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(
                Function(entry) New KeyValuePair(Of String, ZipArchiveEntry)(entry.FullName.Replace("\"c, "/"c).Substring(prefix.Length), entry))
        End Get
    End Property
    Friend Sub New(package As CurseForgePackage, token As CancellationToken)
        MyBase.New(package.SourcePath, package.Sha256, token)
        _package = package
    End Sub
    Friend Sub SetFiles(resolved As List(Of CurseForgeResolvedFile))
        _resolved = resolved
        Files.AddRange(DownloadPlan.Merge(resolved.Select(Function(item) item.Download)))
    End Sub
    Friend Overrides Sub WriteLedger(stage As String, token As CancellationToken)
        ValidatePayload(stage, token)
        Dim ledgerFiles As New JArray()
        For Each item In _resolved
            ledgerFiles.Add(New JObject From {{"projectId", item.Reference.ProjectId}, {"fileId", item.Reference.FileId},
                {"path", item.Download.RelativePath}, {"size", item.Download.Size}, {"sha1", item.Download.Sha1}, {"md5", item.Download.Md5},
                {"s3SinglePartETag", item.Download.S3SinglePartETag}, {"informationSource", item.InformationSource}, {"checksumSource", item.ChecksumSource}})
        Next
        Dim target = SafeFiles.UnderRoot(stage, "PCL/CurseForgeInstall.json")
        Directory.CreateDirectory(Path.GetDirectoryName(target))
        File.WriteAllText(target, New JObject From {{"formatVersion", 1}, {"name", _package.Name}, {"version", _package.Version}, {"archiveSha256", _package.Sha256},
            {"minecraft", _package.Minecraft}, {"loader", _package.LoaderKind.ToString()}, {"loaderVersion", _package.LoaderVersion}, {"files", ledgerFiles}}.ToString(), New UTF8Encoding(False))
    End Sub
End Class
