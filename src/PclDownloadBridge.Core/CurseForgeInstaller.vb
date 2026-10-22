Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq

Public Class CurseForgeInstaller
    Private ReadOnly _policy As SourcePolicy
    Private ReadOnly _pclDirectory As String
    Private ReadOnly _resolver As CurseForgeFileResolver
    Public Sub New(policy As SourcePolicy, Optional pclDirectory As String = Nothing, Optional resolver As CurseForgeFileResolver = Nothing)
        _policy = policy : _pclDirectory = pclDirectory : _resolver = resolver
    End Sub
    Public Async Function InstallAsync(package As CurseForgePackage, root As String, instanceName As String, reuseRoots As IEnumerable(Of String), optionalFileIds As IEnumerable(Of Long), progress As IProgress(Of TransferProgress), token As CancellationToken, Optional connections As Integer = 16, Optional limit As Long = 0, Optional javaPath As String = Nothing) As Task(Of InstallResult)
        token.ThrowIfCancellationRequested()
        If package Is Nothing Then Throw New ArgumentNullException(NameOf(package))
        If reuseRoots Is Nothing Then Throw New ArgumentNullException(NameOf(reuseRoots))
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
            Dim installer As New MinecraftInstaller(_policy, _pclDirectory)
            Dim versions = Await installer.VersionsAsync(token).ConfigureAwait(False)
            Dim entry = versions.SingleOrDefault(Function(item) item.Id = package.Minecraft)
            If entry Is Nothing Then Throw New InvalidDataException("未找到整合包指定的 Minecraft 版本：" & package.Minecraft)
            Dim components As New ComponentSelection With {.JavaPath = javaPath}
            If package.LoaderKind.HasValue Then
                Dim catalogue = Await New ComponentCatalog(_policy).LoadAsync(package.LoaderKind.Value, package.Minecraft, token).ConfigureAwait(False)
                Dim loader = catalogue.SingleOrDefault(Function(item) item.Version = package.LoaderVersion)
                If loader Is Nothing Then Throw New InvalidDataException("未找到整合包指定的加载器版本，不会替换为其他版本：" & package.LoaderKind.ToString() & " " & package.LoaderVersion)
                components.Entries.Add(package.LoaderKind.Value, loader)
            End If
            Return Await installer.InstallPackAsync(entry, root, instanceName, reuse, progress, token, components, connections, limit, plan).ConfigureAwait(False)
        End Using
    End Function
End Class

' The original ZIP is held read-only throughout queries, transfers and publication.
' Pack content joins MinecraftInstaller's private payload; it is never published early.
Friend NotInheritable Class CurseForgeInstallPlan
    Implements IDisposable
    Friend ReadOnly Package As CurseForgePackage
    Friend ReadOnly Files As New List(Of DownloadFile)
    Private ReadOnly _input As FileStream
    Private ReadOnly _archive As ZipArchive
    Private _resolved As New List(Of CurseForgeResolvedFile)
    Private ReadOnly _overrideHashes As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
    Friend Sub New(package As CurseForgePackage, token As CancellationToken)
        Me.Package = package
        _input = New FileStream(package.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read)
        Try
            If CurseForgePackage.HashArchive(_input, token) <> package.Sha256 Then Throw New InvalidDataException("整合包文件在读取后发生变化，请重新选择。")
            _archive = New ZipArchive(_input, ZipArchiveMode.Read, True)
        Catch
            _input.Dispose() : Throw
        End Try
    End Sub
    Friend Sub SetFiles(resolved As List(Of CurseForgeResolvedFile))
        _resolved = resolved
        Files.AddRange(DownloadPlan.Merge(resolved.Select(Function(item) item.Download)))
    End Sub
    Friend Sub ValidateTargets(name As String)
        Dim targets As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For Each relative In Package.OverrideFiles.Concat(Files.Select(Function(item) item.RelativePath))
            CurseForgePackage.ValidateRelative(relative)
            If String.Equals(relative, name & ".jar", StringComparison.OrdinalIgnoreCase) OrElse String.Equals(relative, name & ".json", StringComparison.OrdinalIgnoreCase) OrElse String.Equals(relative, "installation.json", StringComparison.OrdinalIgnoreCase) OrElse String.Equals(relative, "PCL/CurseForgeInstall.json", StringComparison.OrdinalIgnoreCase) Then Throw New InvalidDataException("整合包内容与生成的实例文件冲突：" & relative)
            If Not targets.Add(relative) Then Throw New InvalidDataException("整合包下载内容与 overrides 路径冲突：" & relative)
        Next
        targets.Add(name & ".jar") : targets.Add(name & ".json") : targets.Add("PCL/Setup.ini") : targets.Add("PCL/CurseForgeInstall.json")
        For Each relative In targets
            Dim parent = relative
            Do While parent.Contains("/"c)
                parent = parent.Substring(0, parent.LastIndexOf("/"c))
                If targets.Contains(parent) Then Throw New InvalidDataException("整合包文件与目录路径冲突：" & parent)
            Loop
        Next
    End Sub
    Friend Sub CopyOverrides(stage As String, token As CancellationToken)
        Dim prefix = Package.BaseFolder & Package.OverridesFolder & "/"
        For Each entry In _archive.Entries
            Dim key = entry.FullName.Replace("\"c, "/"c)
            If CurseForgePackage.IsDirectory(entry) OrElse Not key.StartsWith(prefix, StringComparison.Ordinal) Then Continue For
            token.ThrowIfCancellationRequested()
            Dim relative = key.Substring(prefix.Length), target = SafeFiles.UnderRoot(stage, relative)
            Directory.CreateDirectory(Path.GetDirectoryName(target))
            If String.Equals(relative, "PCL/Setup.ini", StringComparison.OrdinalIgnoreCase) Then
                Using input = entry.Open()
                    Dim text = CurseForgePackage.ReadText(input, entry.Length, 2 * 1024 * 1024)
                    Dim lines = text.Replace(vbCrLf, vbLf).Split(ChrW(10)).Where(Function(line)
                        Dim colon = line.IndexOf(":"c)
                        Return colon < 0 OrElse Not {"VersionArgumentIndie", "VersionArgumentIndieV2", "IsStar"}.Contains(line.Substring(0, colon).Trim(), StringComparer.OrdinalIgnoreCase)
                    End Function)
                    Dim bytes = Encoding.UTF8.GetBytes(String.Join(vbCrLf, lines) & vbCrLf & "VersionArgumentIndie:1" & vbCrLf & "VersionArgumentIndieV2:True" & vbCrLf & "IsStar:False" & vbCrLf)
                    Using sha = Security.Cryptography.SHA256.Create()
                        _overrideHashes(relative) = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "")
                    End Using
                    File.WriteAllBytes(target, bytes)
                End Using
            Else
                Using input = entry.Open(), output As New FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None), sha = Security.Cryptography.SHA256.Create()
                    Dim buffer(65535) As Byte, total As Long
                    Do
                        token.ThrowIfCancellationRequested()
                        Dim count = input.Read(buffer, 0, buffer.Length)
                        If count = 0 Then Exit Do
                        total += count
                        If total > entry.Length Then Throw New InvalidDataException("整合包解压内容超过声明长度。")
                        output.Write(buffer, 0, count)
                        sha.TransformBlock(buffer, 0, count, buffer, 0)
                    Loop
                    If total <> entry.Length Then Throw New InvalidDataException("整合包解压内容不完整。")
                    sha.TransformFinalBlock(New Byte() {}, 0, 0)
                    _overrideHashes(relative) = BitConverter.ToString(sha.Hash).Replace("-", "")
                End Using
            End If
        Next
    End Sub
    Friend Sub WriteLedger(stage As String, token As CancellationToken)
        For Each pair In _overrideHashes
            token.ThrowIfCancellationRequested()
            Using input = File.OpenRead(SafeFiles.UnderRoot(stage, pair.Key)), sha = Security.Cryptography.SHA256.Create()
                If BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "") <> pair.Value Then Throw New InvalidDataException("整合包配置在最终提交前发生变化：" & pair.Key)
            End Using
        Next
        For Each item In Files
            token.ThrowIfCancellationRequested()
            If Not SafeFiles.Matches(SafeFiles.UnderRoot(stage, item.RelativePath), item) Then Throw New InvalidDataException("整合包文件在最终提交前校验失败：" & item.RelativePath)
        Next
        Dim ledgerFiles As New JArray()
        For Each item In _resolved
            ledgerFiles.Add(New JObject From {{"projectId", item.Reference.ProjectId}, {"fileId", item.Reference.FileId}, {"required", item.Reference.Required},
                {"path", item.Download.RelativePath}, {"size", item.Download.Size}, {"sha1", item.Download.Sha1}, {"md5", item.Download.Md5},
                {"s3SinglePartETag", item.Download.S3SinglePartETag}, {"informationSource", item.InformationSource}, {"checksumSource", item.ChecksumSource}})
        Next
        Dim target = SafeFiles.UnderRoot(stage, "PCL/CurseForgeInstall.json")
        Directory.CreateDirectory(Path.GetDirectoryName(target))
        File.WriteAllText(target, New JObject From {{"formatVersion", 1}, {"name", Package.Name}, {"version", Package.Version}, {"archiveSha256", Package.Sha256},
            {"minecraft", Package.Minecraft}, {"loader", Package.LoaderKind.ToString()}, {"loaderVersion", Package.LoaderVersion}, {"files", ledgerFiles}}.ToString(), New UTF8Encoding(False))
    End Sub
    Public Sub Dispose() Implements IDisposable.Dispose
        _archive?.Dispose() : _input.Dispose()
    End Sub
End Class
