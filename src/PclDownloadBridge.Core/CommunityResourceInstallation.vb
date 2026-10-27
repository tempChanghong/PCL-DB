Imports System.IO
Imports System.Security.Cryptography
Imports System.Text.RegularExpressions
Imports System.Threading

Public NotInheritable Class InstalledWorld
    Public ReadOnly Property Name As String
    Public ReadOnly Property Folder As String
    Friend ReadOnly Identity As String, MarkerHash As String
    Friend Sub New(name As String, folder As String, identity As String, hash As String)
        Me.Name = name : Me.Folder = folder : Me.Identity = identity : MarkerHash = hash
    End Sub
    Public Overrides Function ToString() As String
        Return Name
    End Function
End Class

' A private fresh instance snapshot binds launcher identity, native isolation,
' resource type and (for datapacks) an explicitly selected existing world.
Public NotInheritable Class ResourceInstallationPlan
    Private ReadOnly _instance As InstalledInstance, _release As ModpackRelease, _world As InstalledWorld
    Private ReadOnly _runIdentity As String, _directoryIdentity As String
    Public ReadOnly Property InstanceName As String
        Get
            Return _instance.Name
        End Get
    End Property
    Public ReadOnly Property RunDirectory As String
        Get
            Return _instance.RunDirectory
        End Get
    End Property
    Public ReadOnly Property Destination As String
    Public ReadOnly Property WorldName As String
        Get
            Return _world?.Name
        End Get
    End Property
    Public ReadOnly Property IsShared As Boolean
        Get
            Return Not _instance.IsIsolated.GetValueOrDefault()
        End Get
    End Property
    Public ReadOnly Property Release As ModpackRelease
        Get
            Return _release.Copy()
        End Get
    End Property
    Public ReadOnly Property LauncherTarget As LauncherInstallTarget
        Get
            Return _instance.Target
        End Get
    End Property
    Friend Sub New(instance As InstalledInstance, release As ModpackRelease, world As InstalledWorld, destination As String)
        _instance = instance : _release = release.Copy() : _world = world : Me.Destination = destination
        Using handle = LocalModActions.NativeLease.OpenDirectory(instance.RunDirectory, _runIdentity)
        End Using
        If Directory.Exists(Path.GetDirectoryName(destination)) Then
            Using handle = LocalModActions.NativeLease.OpenDirectory(Path.GetDirectoryName(destination), _directoryIdentity)
            End Using
        End If
    End Sub
    Private Sub Validate(token As CancellationToken)
        InstalledInstanceCatalog.EnsureUnchanged(_instance, token)
        Dim identity As String = Nothing
        Using handle = LocalModActions.NativeLease.OpenDirectory(_instance.RunDirectory, identity)
            If identity <> _runIdentity Then Throw New IOException("运行目录已被替换，请重新选择。")
        End Using
        If _directoryIdentity IsNot Nothing Then
            Using handle = LocalModActions.NativeLease.OpenDirectory(Path.GetDirectoryName(Destination), identity)
                If identity <> _directoryIdentity Then Throw New IOException("资源目录已被替换，请重新选择。")
            End Using
        End If
        Dim problem = CommunityResourceInstallation.CompatibilityProblem(_release, _instance)
        If problem IsNot Nothing Then Throw New InvalidDataException(problem)
        If _world IsNot Nothing Then CommunityResourceInstallation.EnsureWorld(_instance, _world, token)
    End Sub
    Public Async Function InstallAsync(cache As String, policy As SourcePolicy, progress As IProgress(Of TransferProgress), token As CancellationToken, Optional connections As Integer = 16, Optional limit As Long = 0) As Task(Of Boolean)
        token.ThrowIfCancellationRequested() : Validate(token)
        progress?.Report(New TransferProgress With {.Phase = "archive", .Stage = "检查已有文件", .StageFraction = 0})
        ' PCL ModNet.CheckExistingFile checks the target before transferring it.
        ' Keep native locks on one worker thread; never hold a mutex across Await.
        If Await Task.Run(Function() ReuseExisting(token), token).ConfigureAwait(False) Then
            progress?.Report(New TransferProgress With {.Phase = "archive", .Stage = "已有文件校验通过", .StageFraction = 1, .Completed = 1, .Total = 1})
            progress?.Report(New TransferProgress With {.Phase = "commit", .Stage = "已存在相同文件", .StageFraction = 1})
            Return False
        End If
        Dim acquisition As New InlineProgress(Sub(value)
            value.Phase = "archive" : value.Stage = "下载并校验文件"
            value.StageFraction = If(value.Completed >= value.Total AndAlso value.Total > 0, 1, Math.Min(0.99, value.NetworkBytes / CDbl(_release.Size)))
            progress?.Report(value)
        End Sub)
        Dim source = Await ModpackArchiveInstaller.AcquireAsync(_release, cache, policy, acquisition, token, connections, limit).ConfigureAwait(False)
        progress?.Report(New TransferProgress With {.Phase = "commit", .Stage = "安装到 " & InstanceName, .StageFraction = 0})
        Dim changed = Await Task.Run(Function() Publish(source, token), token).ConfigureAwait(False)
        progress?.Report(New TransferProgress With {.Phase = "commit", .Stage = If(changed, "资源已安装", "已存在相同文件"), .StageFraction = 1})
        Return changed
    End Function
    Private Function ReuseExisting(token As CancellationToken) As Boolean
        Dim directory = Path.GetDirectoryName(Destination)
        Using gate As New LocalModActions.OperationGate(_instance, directory, token, If(_release.Kind = CommunityResourceKind.Mod, "MODS", "RESOURCES"))
            Validate(token)
            If _world IsNot Nothing Then gate.HoldModsDirectory(_world.Folder)
            Using marker = If(_world Is Nothing, Nothing, LocalModActions.NativeLease.Open(Path.Combine(_world.Folder, "level.dat"), False))
                Validate(token)
                Return CheckExisting(token)
            End Using
        End Using
    End Function
    Private Function CheckExisting(token As CancellationToken) As Boolean
        token.ThrowIfCancellationRequested()
        If _release.Kind = CommunityResourceKind.Mod Then LocalModActions.EnsureAliases(Path.GetDirectoryName(Destination), _release.FileName, Destination)
        If Directory.Exists(Destination) Then Throw DestinationConflict()
        If Not File.Exists(Destination) Then Return False
        Using existing = LocalModActions.NativeLease.Open(Destination, False)
            Try
                CommunityResourceDownload.VerifySource(existing.Stream, _release, token)
            Catch ex As InvalidDataException
                ' This is the existing target, not a corrupt download cache.
                Throw DestinationConflict()
            End Try
            If _release.Kind = CommunityResourceKind.Mod Then
                Dim entry = LocalModCatalog.Read(Destination, token)
                If Not entry.IsArchiveValid OrElse Not String.IsNullOrEmpty(entry.Problem) Then Throw New InvalidDataException("已有 Mod 无法读取，文件已保留：" & _release.FileName)
            End If
            Validate(token) : Return True
        End Using
    End Function
    Private Function DestinationConflict() As IOException
        Return New IOException("目标已被不同内容的文件或文件夹占用，已有内容已保留：" & _release.FileName)
    End Function
    Friend Function Publish(source As String, token As CancellationToken, Optional beforePublish As Action = Nothing) As Boolean
        Validate(token)
        If _release.Kind = CommunityResourceKind.Mod Then
            beforePublish?.Invoke() : Validate(token)
            Return LocalModActions.ImportResource(_instance, source, _release, token, Sub() Validate(token)).ChangedCount > 0
        End If
        Dim directory = Path.GetDirectoryName(Destination)
        Using gate As New LocalModActions.OperationGate(_instance, directory, token, "RESOURCES")
            Validate(token)
            If _world IsNot Nothing Then gate.HoldModsDirectory(_world.Folder)
            ' Only the selected resource subdirectory may be created. Never create
            ' or select a world, change isolation, or edit options/level.dat.
            If Not IO.Directory.Exists(directory) Then IO.Directory.CreateDirectory(directory)
            gate.HoldModsDirectory(directory)
            Dim marker As LocalModActions.NativeLease = Nothing
            Try
                If _world IsNot Nothing Then
                    marker = LocalModActions.NativeLease.Open(Path.Combine(_world.Folder, "level.dat"), False)
                    Validate(token)
                End If
                If CheckExisting(token) Then Return False
                CommunityResourceDownload.Publish(_release, source, Destination, token, _instance,
                    Sub()
                        beforePublish?.Invoke() : Validate(token)
                    End Sub, Sub() Validate(token))
                Return True
            Finally
                marker?.Dispose()
            End Try
        End Using
    End Function
End Class

Public NotInheritable Class CommunityResourceInstallation
    Private Sub New()
    End Sub
    Public Shared Function CompatibilityProblem(release As ModpackRelease, instance As InstalledInstance) As String
        If release Is Nothing OrElse instance Is Nothing OrElse instance.Problem IsNot Nothing OrElse instance.RunDirectory Is Nothing Then Return "无法确定这个实例的运行目录。"
        If release.Kind = CommunityResourceKind.Modpack OrElse release.Kind = CommunityResourceKind.Any Then Return "此入口仅用于普通资源。"
        If Not Directory.Exists(instance.RunDirectory) Then Return "实际运行目录不存在，请先在启动器中确认。"
        If release.Kind = CommunityResourceKind.Mod OrElse release.Kind = CommunityResourceKind.DataPack Then
            If instance.MinecraftVersion Is Nothing OrElse Not release.GameVersions.Contains(instance.MinecraftVersion, StringComparer.OrdinalIgnoreCase) Then Return "资源未声明支持这个 Minecraft 版本。"
        End If
        If release.Kind = CommunityResourceKind.Mod Then
            Dim installed = Regex.Matches(instance.LoaderSummary, "(?:\A| \+ )(NeoForge|Forge|Fabric|Quilt|LiteLoader|Rift|ModLoader)(?: |\z)").Cast(Of Match)().Select(Function(match) match.Groups(1).Value).ToArray()
            If installed.Length = 0 Then Return "这个实例未识别到 Mod 加载器。"
            Dim required = release.Loaders.Where(Function(loader) {"neoforge", "forge", "fabric", "quilt", "liteloader", "rift", "modloader"}.Contains(loader, StringComparer.OrdinalIgnoreCase)).ToArray()
            If required.Length > 0 AndAlso Not required.Intersect(installed, StringComparer.OrdinalIgnoreCase).Any() Then Return "Mod 加载器与实例不匹配。"
        End If
        If release.Kind = CommunityResourceKind.ResourcePack AndAlso UsesLegacyTexturePacks(instance.MinecraftVersion) AndAlso Not release.GameVersions.Contains(instance.MinecraftVersion, StringComparer.OrdinalIgnoreCase) Then Return "资源未声明支持这个旧版游戏的纹理包格式。"
        Return Nothing
    End Function
    ' Save dialogs and installation must use the same legacy-format boundary.
    Public Shared Function UsesLegacyTexturePacks(version As String) As Boolean
        Return Regex.IsMatch(If(version, ""), "\A(?:1\.[0-5](?:\.|\z)|[ab]1\.)", RegexOptions.IgnoreCase Or RegexOptions.CultureInvariant)
    End Function
    Public Shared Function ResourceDirectory(release As ModpackRelease, instance As InstalledInstance, Optional world As InstalledWorld = Nothing) As String
        Dim problem = CompatibilityProblem(release, instance)
        If problem IsNot Nothing Then Throw New InvalidDataException(problem)
        Select Case release.Kind
            Case CommunityResourceKind.Mod : Return Path.GetDirectoryName(SafeFiles.UnderRoot(instance.RunDirectory, "mods/entry"))
            Case CommunityResourceKind.ResourcePack : Return Path.GetDirectoryName(SafeFiles.UnderRoot(instance.RunDirectory, If(UsesLegacyTexturePacks(instance.MinecraftVersion), "texturepacks", "resourcepacks") & "/entry"))
            Case CommunityResourceKind.Shader : Return Path.GetDirectoryName(SafeFiles.UnderRoot(instance.RunDirectory, "shaderpacks/entry"))
            Case CommunityResourceKind.DataPack
                If world Is Nothing Then Throw New InvalidOperationException("请选择要安装数据包的存档。")
                Dim expected = Path.Combine(instance.RunDirectory, "saves", world.Name)
                If Not String.Equals(Path.GetFullPath(expected), world.Folder, StringComparison.OrdinalIgnoreCase) Then Throw New InvalidDataException("存档不属于所选实例的运行目录。")
                Return Path.GetDirectoryName(SafeFiles.UnderRoot(world.Folder, "datapacks/entry"))
        End Select
        Throw New InvalidOperationException("资源类型无效。")
    End Function
    Public Shared Function Create(release As ModpackRelease, instance As InstalledInstance, world As InstalledWorld, token As CancellationToken) As ResourceInstallationPlan
        token.ThrowIfCancellationRequested()
        InstalledInstanceCatalog.EnsureUnchanged(instance, token)
        Dim frozen = InstalledInstanceCatalog.Read(instance.Target, instance.Name, token)
        InstalledInstanceCatalog.EnsureUnchanged(instance, token)
        Dim snapshot = release.Copy()
        If snapshot.Kind <> CommunityResourceKind.DataPack Then world = Nothing
        Dim directory = ResourceDirectory(snapshot, frozen, world)
        CommunityResources.ValidateFileName(snapshot.Kind, snapshot.FileName)
        If snapshot.Size <= 0 OrElse (snapshot.Sha1 Is Nothing AndAlso snapshot.Sha512 Is Nothing AndAlso snapshot.Md5 Is Nothing) Then Throw New InvalidDataException("资源缺少校验信息。")
        If world IsNot Nothing Then EnsureWorld(frozen, world, token)
        Return New ResourceInstallationPlan(frozen, snapshot, world, SafeFiles.UnderRoot(directory, snapshot.FileName))
    End Function
    Public Shared Function Worlds(instance As InstalledInstance, token As CancellationToken) As List(Of InstalledWorld)
        InstalledInstanceCatalog.EnsureUnchanged(instance, token)
        Dim saves = Path.GetDirectoryName(SafeFiles.UnderRoot(instance.RunDirectory, "saves/entry")), result As New List(Of InstalledWorld)
        If Not Directory.Exists(saves) Then Return result
        Dim identity As String = Nothing
        Using parent = LocalModActions.NativeLease.OpenDirectory(saves, identity)
            For Each directory In IO.Directory.EnumerateDirectories(saves)
                token.ThrowIfCancellationRequested()
                If Not File.Exists(Path.Combine(directory, "level.dat")) Then Continue For
                Try
                    result.Add(ReadWorld(directory, token))
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                    ' Inaccessible/link candidates are never installed into.
                End Try
            Next
        End Using
        InstalledInstanceCatalog.EnsureUnchanged(instance, token)
        Return result.OrderBy(Function(world) world.Name, StringComparer.CurrentCultureIgnoreCase).ToList()
    End Function
    Private Shared Function ReadWorld(directory As String, token As CancellationToken) As InstalledWorld
        Dim identity As String = Nothing
        Using handle = LocalModActions.NativeLease.OpenDirectory(directory, identity), marker = LocalModActions.NativeLease.Open(Path.Combine(directory, "level.dat"), False), digest = SHA256.Create()
            Dim buffer(65535) As Byte
            Do
                token.ThrowIfCancellationRequested()
                Dim count = marker.Stream.Read(buffer, 0, buffer.Length)
                If count = 0 Then Exit Do
                digest.TransformBlock(buffer, 0, count, buffer, 0)
            Loop
            digest.TransformFinalBlock(New Byte() {}, 0, 0)
            Return New InstalledWorld(Path.GetFileName(directory), Path.GetFullPath(directory), identity, BitConverter.ToString(digest.Hash).Replace("-", ""))
        End Using
    End Function
    Friend Shared Sub EnsureWorld(instance As InstalledInstance, world As InstalledWorld, token As CancellationToken)
        Dim current = ReadWorld(world.Folder, token)
        If current.Identity <> world.Identity OrElse current.MarkerHash <> world.MarkerHash OrElse Not String.Equals(current.Folder, Path.Combine(instance.RunDirectory, "saves", current.Name), StringComparison.OrdinalIgnoreCase) Then Throw New InvalidDataException("所选存档已变化，请重新选择。")
    End Sub
End Class
