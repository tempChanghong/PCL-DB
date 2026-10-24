Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq

Public NotInheritable Class ModrinthInstaller
    Private ReadOnly _policy As SourcePolicy
    Private ReadOnly _pclDirectory As String
    Private ReadOnly _target As LauncherInstallTarget
    Public Sub New(policy As SourcePolicy, Optional pclDirectory As String = Nothing, Optional launcherTarget As LauncherInstallTarget = Nothing)
        _policy = policy : _pclDirectory = pclDirectory
        _target = launcherTarget
    End Sub
    Public Async Function InstallAsync(package As ModrinthPackage, root As String, instanceName As String, reuseRoots As IEnumerable(Of String), optionalPaths As IEnumerable(Of String), progress As IProgress(Of TransferProgress), token As CancellationToken, Optional connections As Integer = 16, Optional limit As Long = 0, Optional javaPath As String = Nothing) As Task(Of InstallResult)
        token.ThrowIfCancellationRequested()
        If package Is Nothing Then Throw New ArgumentNullException(NameOf(package))
        If reuseRoots Is Nothing Then Throw New ArgumentNullException(NameOf(reuseRoots))
        _target?.ValidateDestination(root)
        InstallPreflight.ValidateDestination(root, instanceName, False)
        Dim reuse = reuseRoots.ToArray(), selected = If(optionalPaths, Enumerable.Empty(Of String)()).ToArray()
        Using plan As New ModrinthInstallPlan(package, selected, token)
            Return Await ModpackInstallation.InstallAsync(plan, _policy, _pclDirectory, root, instanceName, reuse, progress, token, package.LoaderKind, package.LoaderVersion, connections, limit, javaPath, _target).ConfigureAwait(False)
        End Using
    End Function
End Class

Friend NotInheritable Class ModrinthInstallPlan
    Inherits ModpackInstallPlan
    Private ReadOnly _package As ModrinthPackage
    Friend Overrides ReadOnly Property Minecraft As String
        Get
            Return _package.Minecraft
        End Get
    End Property
    Protected Overrides ReadOnly Property AllowLayeredOverrides As Boolean
        Get
            Return True
        End Get
    End Property
    Protected Overrides ReadOnly Property OverrideEntries As IEnumerable(Of KeyValuePair(Of String, ZipArchiveEntry))
        Get
            Return _package.OverrideSources.Select(Function(pair) New KeyValuePair(Of String, ZipArchiveEntry)(pair.Key, Archive.GetEntry(pair.Value)))
        End Get
    End Property
    Friend Sub New(package As ModrinthPackage, selected As IEnumerable(Of String), token As CancellationToken)
        MyBase.New(package.SourcePath, package.Sha256, token)
        _package = package
        Try
            Dim choices As New HashSet(Of String)(selected, StringComparer.OrdinalIgnoreCase)
            If choices.Any(Function(path) Not package.References.Any(Function(item) item.Client = "optional" AndAlso String.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))) Then Throw New InvalidDataException("可选文件不属于当前整合包。")
            Files.AddRange(package.References.Where(Function(item) item.Client = "required" OrElse (item.Client = "optional" AndAlso choices.Contains(item.Path))).Select(Function(item) item.ToDownload()))
        Catch
            Dispose() : Throw
        End Try
    End Sub
    Friend Overrides Sub WriteLedger(stage As String, token As CancellationToken)
        ValidatePayload(stage, token)
        Dim records As New JArray(Files.Select(Function(item) New JObject From {{"path", item.RelativePath}, {"estimatedSize", item.EstimatedSize}, {"sha1", item.Sha1}, {"sha512", item.Sha512}, {"overridden", OverrideHashes.ContainsKey(item.RelativePath)}}))
        Dim overrideRecords As New JArray(_package.OverrideSources.Select(Function(pair) New JObject From {{"path", pair.Key}, {"archiveEntry", pair.Value}, {"sha256", OverrideHashes(pair.Key)}}))
        Dim target = SafeFiles.UnderRoot(stage, "PCL/ModrinthInstall.json")
        Directory.CreateDirectory(Path.GetDirectoryName(target))
        File.WriteAllText(target, New JObject From {{"formatVersion", 1}, {"name", _package.Name}, {"version", _package.Version}, {"archiveSha256", _package.Sha256},
            {"minecraft", _package.Minecraft}, {"loader", _package.LoaderKind.ToString()}, {"loaderVersion", _package.LoaderVersion}, {"files", records}, {"overrides", overrideRecords}}.ToString(), New UTF8Encoding(False))
    End Sub
End Class
