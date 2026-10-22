Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Threading

' PCL 5ef81e4 ModModpack: isolated instance, exact loader and overrides.
' Both formats join the same private Minecraft payload and publication lock.
Friend MustInherit Class ModpackInstallPlan
    Implements IDisposable
    Friend ReadOnly Files As New List(Of DownloadFile)
    Protected ReadOnly Archive As ZipArchive
    Private ReadOnly _input As FileStream
    Protected ReadOnly OverrideHashes As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
    Friend MustOverride ReadOnly Property Minecraft As String
    Protected MustOverride ReadOnly Property OverrideEntries As IEnumerable(Of KeyValuePair(Of String, ZipArchiveEntry))
    Protected Overridable ReadOnly Property AllowLayeredOverrides As Boolean
        Get
            Return False
        End Get
    End Property
    Friend ReadOnly Property OverrideCount As Integer
        Get
            Return OverrideEntries.Count()
        End Get
    End Property
    Protected Sub New(path As String, digest As String, token As CancellationToken)
        _input = New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
        Try
            If CurseForgePackage.HashArchive(_input, token) <> digest Then Throw New InvalidDataException("整合包文件在读取后发生变化，请重新选择。")
            Archive = New ZipArchive(_input, ZipArchiveMode.Read, True)
        Catch
            _input.Dispose() : Throw
        End Try
    End Sub
    Friend Sub ValidateTargets(name As String)
        Dim targets As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim reserved = {name & ".jar", name & ".json", "installation.json", "PCL/CurseForgeInstall.json", "PCL/ModrinthInstall.json"}
        For Each relative In Files.Select(Function(item) item.RelativePath).Concat(OverrideEntries.Select(Function(item) item.Key))
            CurseForgePackage.ValidateRelative(relative)
            If reserved.Contains(relative, StringComparer.OrdinalIgnoreCase) Then Throw New InvalidDataException("整合包内容与生成的实例文件冲突：" & relative)
            If Not targets.Add(relative) AndAlso Not AllowLayeredOverrides Then Throw New InvalidDataException("整合包下载内容与 overrides 路径冲突：" & relative)
        Next
        If Files.Any(Function(item) String.Equals(item.RelativePath, "PCL/Setup.ini", StringComparison.OrdinalIgnoreCase)) Then Throw New InvalidDataException("隔离设置只能通过整合包 overrides 合并。")
        targets.UnionWith(reserved) : targets.Add("PCL/Setup.ini")
        For Each relative In targets
            Dim parent = relative
            Do While parent.Contains("/"c)
                parent = parent.Substring(0, parent.LastIndexOf("/"c))
                If targets.Contains(parent) Then Throw New InvalidDataException("整合包文件与目录路径冲突：" & parent)
            Loop
        Next
    End Sub
    Friend Sub CopyOverrides(stage As String, token As CancellationToken)
        ' The format parser selects the winning entry of each permitted layer.
        ' Apply only after loader work joins, so it cannot race packaged INI/config.
        For Each item In OverrideEntries
            token.ThrowIfCancellationRequested()
            Dim relative = item.Key, entry = item.Value, target = SafeFiles.UnderRoot(stage, relative)
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
                        OverrideHashes(relative) = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "")
                    End Using
                    token.ThrowIfCancellationRequested() : File.WriteAllBytes(target, bytes)
                End Using
            Else
                Using input = entry.Open(), output As New FileStream(target, If(AllowLayeredOverrides, FileMode.Create, FileMode.CreateNew), FileAccess.Write, FileShare.None), sha = Security.Cryptography.SHA256.Create()
                    Dim buffer(65535) As Byte, total As Long = 0
                    Do
                        token.ThrowIfCancellationRequested()
                        Dim count = input.Read(buffer, 0, buffer.Length)
                        If count = 0 Then Exit Do
                        total += count
                        If total > entry.Length Then Throw New InvalidDataException("整合包解压内容超过声明长度：" & relative)
                        output.Write(buffer, 0, count) : sha.TransformBlock(buffer, 0, count, buffer, 0)
                    Loop
                    If total <> entry.Length Then Throw New InvalidDataException("整合包解压内容不完整。")
                    sha.TransformFinalBlock(New Byte() {}, 0, 0)
                    OverrideHashes(relative) = BitConverter.ToString(sha.Hash).Replace("-", "")
                End Using
            End If
        Next
    End Sub
    Protected Sub ValidatePayload(stage As String, token As CancellationToken)
        PclIsolationPlan.ValidatePackSettings(stage, token)
        For Each pair In OverrideHashes
            token.ThrowIfCancellationRequested()
            Using stream = File.OpenRead(SafeFiles.UnderRoot(stage, pair.Key)), sha = Security.Cryptography.SHA256.Create()
                If BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "") <> pair.Value Then Throw New InvalidDataException("整合包覆盖文件在发布前发生变化：" & pair.Key)
            End Using
        Next
        For Each item In Files
            token.ThrowIfCancellationRequested()
            ' Deliberately replaced index files were checked before overrides, then
            ' checked above against the winning archive entry, not the old digest.
            If Not OverrideHashes.ContainsKey(item.RelativePath) AndAlso Not SafeFiles.Matches(SafeFiles.UnderRoot(stage, item.RelativePath), item) Then Throw New InvalidDataException("整合包文件在发布前校验失败：" & item.RelativePath)
        Next
    End Sub
    Friend MustOverride Sub WriteLedger(stage As String, token As CancellationToken)
    Public Sub Dispose() Implements IDisposable.Dispose
        Archive?.Dispose() : _input.Dispose()
    End Sub
End Class

Friend Module ModpackInstallation
    Friend Async Function InstallAsync(plan As ModpackInstallPlan, policy As SourcePolicy, pclDirectory As String, root As String, name As String, reuse As IEnumerable(Of String), progress As IProgress(Of TransferProgress), token As CancellationToken, loaderKind As ComponentKind?, loaderVersion As String, connections As Integer, limit As Long, javaPath As String) As Task(Of InstallResult)
        plan.ValidateTargets(name)
        Dim installer As New MinecraftInstaller(policy, pclDirectory)
        Dim versions = Await installer.VersionsAsync(token).ConfigureAwait(False)
        Dim entry = versions.SingleOrDefault(Function(item) item.Id = plan.Minecraft)
        If entry Is Nothing Then Throw New InvalidDataException("未找到整合包指定的 Minecraft 版本：" & plan.Minecraft)
        Dim components As New ComponentSelection With {.JavaPath = javaPath}
        If loaderKind.HasValue Then
            Dim catalogue = Await New ComponentCatalog(policy).LoadAsync(loaderKind.Value, plan.Minecraft, token).ConfigureAwait(False)
            Dim loader = catalogue.SingleOrDefault(Function(item) item.Version = loaderVersion)
            If loader Is Nothing Then Throw New InvalidDataException("未找到整合包指定的加载器版本，不会替换为其他版本：" & loaderKind.ToString() & " " & loaderVersion)
            components.Entries.Add(loaderKind.Value, loader)
        End If
        Return Await installer.InstallPackAsync(entry, root, name, reuse, progress, token, components, connections, limit, plan).ConfigureAwait(False)
    End Function
End Module
