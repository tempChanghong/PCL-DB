Imports System.IO
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Newtonsoft.Json.Linq

' Local instances are distinct from remote VersionEntry catalogue records.
Public NotInheritable Class InstalledInstance
    Public ReadOnly Property Name As String
    Public ReadOnly Property InstancePath As String
    Public ReadOnly Property Target As LauncherInstallTarget
    Public Property MinecraftVersion As String
    Public Property LoaderSummary As String = "未知"
    Public Property RunDirectory As String
    Public Property IsolationSummary As String = "无法确定"
    Public Property IsIsolated As Boolean?
    Public Property IsModpack As Boolean
    Public Property IsVanillaRepairCandidate As Boolean
    Public Property MetadataSha256 As String
    Public Property Problem As String
    Public ReadOnly Property Warnings As New List(Of String)
    Friend Property ConfigurationFingerprint As String
    Friend Sub New(target As LauncherInstallTarget, name As String)
        Me.Target = target : Me.Name = name
        InstancePath = Path.Combine(target.Root, "versions", name)
    End Sub
End Class

' PCL 5ef81e4 McInstance / PathIndie / JsonObject and PCL-CE McInstance.PathIndie.
' This reader deliberately omits their state/cache writes and isolation migration.
Public NotInheritable Class InstalledInstanceCatalog
    Private Sub New()
    End Sub

    Public Shared Function Scan(target As LauncherInstallTarget, token As CancellationToken) As List(Of InstalledInstance)
        If target Is Nothing Then Throw New ArgumentNullException(NameOf(target))
        token.ThrowIfCancellationRequested()
        target.ValidateDestination(target.Root)
        Dim versions = SafeFiles.UnderRoot(target.Root, "versions/entry")
        versions = Path.GetDirectoryName(versions)
        ValidateDirectory(versions)
        Dim result As New List(Of InstalledInstance)
        If Not Directory.Exists(versions) Then Return result
        For Each directory In IO.Directory.EnumerateDirectories(versions)
            token.ThrowIfCancellationRequested()
            result.Add(Read(target, Path.GetFileName(directory), token))
        Next
        token.ThrowIfCancellationRequested()
        Return result.OrderBy(Function(item) item.Name, StringComparer.CurrentCultureIgnoreCase).ToList()
    End Function

    Public Shared Function Read(target As LauncherInstallTarget, name As String, token As CancellationToken) As InstalledInstance
        If target Is Nothing Then Throw New ArgumentNullException(NameOf(target))
        token.ThrowIfCancellationRequested()
        Dim item As New InstalledInstance(target, name)
        Try
            SafeFiles.ValidateInstanceName(name)
            target.ValidateDestination(target.Root)
            ValidateDirectory(item.InstancePath)
            Dim inputs As New InstanceConfigurationInputs(), visited As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Dim raw As JObject = Nothing, components As Boolean = False, modable As Boolean = False
            Dim metadata = ReadProfile(target.Root, name, inputs, visited, token, raw)
            Dim version = If(raw.Property("inheritsFrom") IsNot Nothing, GameVersion(metadata), GameVersion(raw))
            If version Is Nothing Then version = GameVersion(metadata)
            item.MinecraftVersion = version
            If version Is Nothing Then item.Warnings.Add("无法确定 Minecraft 版本。")
            Dim loaders = DetectLoaders(metadata, raw)
            item.LoaderSummary = If(loaders.Count = 0, "原版", String.Join(" + ", loaders))
            components = loaders.Count > 0
            modable = loaders.Any(Function(value) Not value.StartsWith("OptiFine", StringComparison.Ordinal))
            Dim original = If(TryCast(raw("patches"), JArray)?.OfType(Of JObject)().SingleOrDefault(Function(patch) Scalar(patch("id")) = "game"), raw)
            Dim mainClass = Scalar(metadata("mainClass"))
            If Not components AndAlso mainClass <> "net.minecraft.client.main.Main" AndAlso mainClass <> "net.minecraft.client.Minecraft" AndAlso mainClass <> "com.mojang.rubydung.RubyDung" Then
                item.LoaderSummary = "自定义组件" : components = True
                item.Warnings.Add("启动入口暂不识别，不提供原版补全。")
            End If
            Dim instanceRelative = "versions/" & name & "/"
            For Each marker In {"modpack.cfg", "PCL/ModrinthInstall.json", "PCL/CurseForgeInstall.json", "modrinth.index.json", "manifest.json"}
                ' Receipts can contain every pack file. Identity only needs their
                ' presence, so do not load arbitrarily large receipt bodies.
                If File.Exists(SafeFiles.UnderRoot(target.Root, instanceRelative & marker)) Then item.IsModpack = True
            Next
            Dim entry As New VersionEntry With {.Id = If(version, name), .Kind = Scalar(metadata("type"))}
            If target.Kind = ExternalLauncherKind.Pcl Then
                ' Fixed PCL ModLoaders flags contain exactly these four types.
                ' CE's extra loader support must not change PCL's default policy.
                Dim pclModable = loaders.Any(Function(loader) Regex.IsMatch(loader, "\A(?:Forge|NeoForge|Fabric|LiteLoader)(?: |\z)"))
                Dim pclComponents = pclModable OrElse loaders.Any(Function(loader) loader.StartsWith("OptiFine", StringComparison.Ordinal))
                Dim isolated = ReadPclIsolation(target, name, entry, metadata, pclComponents, pclModable, inputs)
                item.IsIsolated = isolated
                item.RunDirectory = If(isolated, item.InstancePath, target.Root)
            Else
                Dim isolation = NativeInstanceIsolation.Inspect(target, name, entry, metadata, components, modable, inputs)
                item.IsIsolated = isolation.Isolated : item.RunDirectory = isolation.RunDirectory
            End If
            ValidateDirectory(item.RunDirectory)
            item.IsolationSummary = If(item.IsIsolated.Value, "版本隔离", If(Same(item.RunDirectory, target.Root), "共享游戏目录", "自定义运行目录"))
            If Not item.IsIsolated.Value Then item.Warnings.Add("此实例使用共享或自定义运行目录，文件可能同时被其他实例使用。")
            Dim hasPatches = raw.Property("patches") IsNot Nothing
            Dim simplePatch = Not hasPatches OrElse (target.Kind = ExternalLauncherKind.Hmcl AndAlso DirectCast(raw("patches"), JArray).Count = 1 AndAlso Scalar(original("id")) = "game")
            item.IsVanillaRepairCandidate = version IsNot Nothing AndAlso Not components AndAlso Not item.IsModpack AndAlso simplePatch AndAlso
                raw.Property("inheritsFrom") Is Nothing AndAlso original.Property("inheritsFrom") Is Nothing AndAlso
                Regex.IsMatch(If(Scalar(original.SelectToken("downloads.client.sha1")), ""), "\A[0-9a-fA-F]{40}\z") AndAlso
                TypeOf original("libraries") Is JArray AndAlso TypeOf original("assetIndex") Is JObject
            item.MetadataSha256 = HashBytes(inputs.Read(SafeFiles.UnderRoot(target.Root, instanceRelative & name & ".json")))
            item.ConfigurationFingerprint = inputs.Fingerprint()
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is ArgumentException OrElse
            TypeOf ex Is NotSupportedException OrElse TypeOf ex Is Newtonsoft.Json.JsonException OrElse TypeOf ex Is InvalidOperationException
            item.Problem = ex.Message
            item.IsVanillaRepairCandidate = False
        End Try
        token.ThrowIfCancellationRequested()
        Return item
    End Function

    ' Re-read the exact frozen launcher context before an action. This also
    ' detects inherited JSON, source configuration and previously absent markers.
    Public Shared Sub EnsureUnchanged(instance As InstalledInstance, token As CancellationToken)
        If instance Is Nothing Then Throw New ArgumentNullException(NameOf(instance))
        Dim current = Read(instance.Target, instance.Name, token)
        If current.Problem IsNot Nothing OrElse instance.ConfigurationFingerprint Is Nothing OrElse
            current.ConfigurationFingerprint <> instance.ConfigurationFingerprint OrElse current.MetadataSha256 <> instance.MetadataSha256 OrElse
            Not Same(current.RunDirectory, instance.RunDirectory) OrElse current.IsIsolated <> instance.IsIsolated OrElse current.IsModpack <> instance.IsModpack Then
            Throw New InvalidDataException("实例或启动器设置已变化，请刷新列表后重试。")
        End If
    End Sub

    Private Shared Function ReadProfile(root As String, name As String, inputs As InstanceConfigurationInputs, visited As HashSet(Of String), token As CancellationToken, ByRef raw As JObject) As JObject
        token.ThrowIfCancellationRequested()
        SafeFiles.ValidateInstanceName(name)
        If visited.Count >= 32 OrElse Not visited.Add(name) Then Throw New InvalidDataException("版本继承循环或层级过多。")
        Dim bytes = inputs.Read(SafeFiles.UnderRoot(root, "versions/" & name & "/" & name & ".json"))
        If bytes Is Nothing Then Throw New InvalidDataException("缺少版本配置：" & name & ".json")
        raw = JObject.Parse(InstanceConfigurationInputs.Text(bytes), New JsonLoadSettings With {.DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error})
        Dim own = DirectCast(raw.DeepClone(), JObject), patchesToken = own("patches")
        If patchesToken IsNot Nothing Then
            Dim patches = TryCast(patchesToken, JArray)
            If patches Is Nothing OrElse patches.Count = 0 OrElse patches.Any(Function(patch) Not TypeOf patch Is JObject) Then Throw New InvalidDataException("HMCL 版本分支无效。")
            Dim games = patches.OfType(Of JObject)().Where(Function(patch) Scalar(patch("id")) = "game").ToList()
            If games.Count <> 1 Then Throw New InvalidDataException("HMCL 原版分支不唯一。")
            own = DirectCast(games(0).DeepClone(), JObject)
            For Each patch In patches.OfType(Of JObject)().Where(Function(value) value IsNot games(0))
                own.Merge(patch, New JsonMergeSettings With {.MergeArrayHandling = MergeArrayHandling.Concat})
            Next
            own("clientVersion") = Scalar(games(0)("version"))
        End If
        Dim parent = Scalar(own("inheritsFrom"))
        If parent IsNot Nothing Then
            Dim parentRaw As JObject = Nothing
            Dim parentJson = ReadProfile(root, parent, inputs, visited, token, parentRaw)
            Dim parentVersion = GameVersion(parentJson)
            parentJson.Merge(own, New JsonMergeSettings With {.MergeArrayHandling = MergeArrayHandling.Concat})
            If Scalar(raw("clientVersion")) Is Nothing AndAlso parentVersion IsNot Nothing Then parentJson("clientVersion") = parentVersion
            own = parentJson
        End If
        visited.Remove(name)
        token.ThrowIfCancellationRequested()
        Return own
    End Function

    Private Shared Function GameVersion(metadata As JObject) As String
        Dim explicitVersion = Scalar(metadata("clientVersion"))
        If Not String.IsNullOrWhiteSpace(explicitVersion) Then Return explicitVersion
        Dim patches = TryCast(metadata("patches"), JArray)
        If patches IsNot Nothing Then
            Dim game = patches.OfType(Of JObject)().FirstOrDefault(Function(patch) Scalar(patch("id")) = "game")
            Dim version = Scalar(game?("version"))
            If Not String.IsNullOrWhiteSpace(version) Then Return version
        End If
        Dim jar = Scalar(metadata("jar"))
        If IsVersionId(jar) Then Return jar
        Dim id = Scalar(metadata("id"))
        Return If(IsVersionId(id), id, Nothing)
    End Function
    Private Shared Function IsVersionId(value As String) As Boolean
        Return value IsNot Nothing AndAlso Regex.IsMatch(value, "\A(?:\d+\.\d+(?:\.\d+)?(?:(?:-pre|-rc)\d+| Pre-Release \d+| Release Candidate \d+|_(?:blue|red|purple))?|\d{2}w\d{2}[a-z].*|(?:rd-|inf-|c0\.|a\d|b\d).*)\z", RegexOptions.IgnoreCase)
    End Function
    Private Shared Function DetectLoaders(metadata As JObject, raw As JObject) As List(Of String)
        Dim found As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        If metadata.Property("labymod_data") IsNot Nothing Then
            Dim data = TryCast(metadata("labymod_data"), JObject)
            If data Is Nothing Then Throw New InvalidDataException("LabyMod 版本信息无效。")
            found("LabyMod") = ("LabyMod " & Scalar(data("version"))).TrimEnd()
        End If
        Dim libraries = TryCast(metadata("libraries"), JArray)
        If metadata("libraries") IsNot Nothing AndAlso libraries Is Nothing Then Throw New InvalidDataException("版本支持库列表无效。")
        If libraries IsNot Nothing Then
            If libraries.Any(Function(library) Not TypeOf library Is JObject) Then Throw New InvalidDataException("版本支持库条目无效。")
            For Each library In libraries.OfType(Of JObject)()
                Dim name = Scalar(library("name"))
                If name Is Nothing Then Continue For
                Dim parts = name.Split(":"c)
                If parts.Length < 3 Then Continue For
                Dim loader As String = Nothing
                Select Case (parts(0) & ":" & parts(1)).ToLowerInvariant()
                    Case "net.fabricmc:fabric-loader", "net.legacyfabric:fabric-loader" : loader = "Fabric"
                    Case "org.quiltmc:quilt-loader" : loader = "Quilt"
                    Case "net.minecraftforge:forge", "net.minecraftforge:minecraftforge" : loader = "Forge"
                    Case "net.neoforged:neoforge", "net.neoforged:forge" : loader = "NeoForge"
                    Case "com.mumfrey:liteloader" : loader = "LiteLoader"
                    Case "optifine:optifine" : loader = "OptiFine"
                    Case "com.cleanroommc:cleanroom" : loader = "Cleanroom"
                End Select
                If loader IsNot Nothing Then found(loader) = loader & " " & parts(2)
            Next
        End If
        Dim patches = TryCast(raw("patches"), JArray)
        If patches IsNot Nothing Then
            For Each patch In patches.OfType(Of JObject)()
                Dim id = Scalar(patch("id")), label As String = Nothing
                Select Case If(id, "").ToLowerInvariant()
                    Case "fabric", "fabric-loader" : label = "Fabric"
                    Case "forge" : label = "Forge"
                    Case "neoforge" : label = "NeoForge"
                    Case "quilt", "quilt-loader" : label = "Quilt"
                    Case "liteloader" : label = "LiteLoader"
                    Case "optifine" : label = "OptiFine"
                    Case "cleanroom" : label = "Cleanroom"
                End Select
                If label IsNot Nothing AndAlso Not found.ContainsKey(label) Then found(label) = (label & " " & Scalar(patch("version"))).TrimEnd()
            Next
        End If
        Dim main = If(Scalar(metadata("mainClass")), "")
        If main.IndexOf("fabricmc", StringComparison.OrdinalIgnoreCase) >= 0 AndAlso Not found.ContainsKey("Fabric") Then found("Fabric") = "Fabric"
        If main.IndexOf("quiltmc", StringComparison.OrdinalIgnoreCase) >= 0 AndAlso Not found.ContainsKey("Quilt") Then found("Quilt") = "Quilt"
        If main.IndexOf("neoforged", StringComparison.OrdinalIgnoreCase) >= 0 AndAlso Not found.ContainsKey("NeoForge") Then found("NeoForge") = "NeoForge"
        Dim arguments = If(metadata("arguments"), metadata("minecraftArguments"))?.ToString()
        If arguments IsNot Nothing Then
            If arguments.IndexOf("optifine.OptiFineTweaker", StringComparison.OrdinalIgnoreCase) >= 0 AndAlso Not found.ContainsKey("OptiFine") Then found("OptiFine") = "OptiFine"
            If arguments.IndexOf("fml", StringComparison.OrdinalIgnoreCase) >= 0 AndAlso Not found.ContainsKey("Forge") AndAlso Not found.ContainsKey("NeoForge") Then found("Forge") = "Forge"
            If arguments.IndexOf("liteloader", StringComparison.OrdinalIgnoreCase) >= 0 AndAlso Not found.ContainsKey("LiteLoader") Then found("LiteLoader") = "LiteLoader"
        End If
        Return found.Values.ToList()
    End Function

    Private Shared Function ReadPclIsolation(target As LauncherInstallTarget, name As String, entry As VersionEntry, metadata As JObject, components As Boolean, modable As Boolean, inputs As InstanceConfigurationInputs) As Boolean
        Dim globalPath = If(target.ConfigPath, IO.Path.Combine(target.Workspace, "PCL/Setup.ini"))
        Dim globalText = InstanceConfigurationInputs.Text(inputs.Read(globalPath))
        Dim instance = SafeFiles.UnderRoot(target.Root, "versions/" & name & "/PCL/Setup.ini")
        Dim text = InstanceConfigurationInputs.Text(inputs.Read(instance))
        Dim value = IniValue(text, "VersionArgumentIndieV2"), enabled As Boolean
        If value IsNot Nothing Then
            If Not Boolean.TryParse(value, enabled) Then Throw New InvalidDataException("已有实例的版本隔离设置无效。")
            Return enabled
        End If
        value = IniValue(text, "VersionArgumentIndie")
        If value IsNot Nothing Then
            Dim legacy As Integer
            If Not Integer.TryParse(value, legacy) OrElse legacy < -1 OrElse legacy > 2 Then Throw New InvalidDataException("已有实例的旧版隔离设置无效。")
            If legacy > 0 Then Return legacy = 1
        End If
        Dim path = IO.Path.GetDirectoryName(instance), instanceRoot = IO.Path.GetDirectoryName(path)
        Dim mods = SafeFiles.UnderRoot(instanceRoot, "mods/entry"), saves = SafeFiles.UnderRoot(instanceRoot, "saves/entry")
        mods = IO.Path.GetDirectoryName(mods) : saves = IO.Path.GetDirectoryName(saves)
        If (Directory.Exists(mods) AndAlso Directory.EnumerateFiles(mods).Any()) OrElse (Directory.Exists(saves) AndAlso Directory.EnumerateDirectories(saves).Any()) Then Return True
        Dim strategy = 4
        value = IniValue(globalText, "LaunchArgumentIndieV2")
        If value IsNot Nothing AndAlso (Not Integer.TryParse(value, strategy) OrElse strategy < 0 OrElse strategy > 4) Then Throw New InvalidDataException("PCL 默认隔离策略无效。")
        modable = modable OrElse IniValue(text, "DisplayType") = "2"
        Dim kind = If(Scalar(metadata("type")), entry.Kind)
        Dim nonRelease = kind = "old_beta" OrElse kind = "old_alpha" OrElse (Not components AndAlso Not String.Equals(kind, "release", StringComparison.OrdinalIgnoreCase))
        Select Case strategy
            Case 0 : Return False
            Case 1 : Return modable
            Case 2 : Return nonRelease
            Case 3 : Return modable OrElse nonRelease
            Case Else : Return True
        End Select
    End Function
    Private Shared Function IniValue(text As String, key As String) As String
        If text Is Nothing Then Return Nothing
        Dim value As String = Nothing
        For Each line In text.Replace(vbCrLf, vbLf).Split(ChrW(10))
            Dim split = line.IndexOf(":"c)
            If split > 0 AndAlso line.Substring(0, split) = key Then value = line.Substring(split + 1).Trim()
        Next
        Return value
    End Function
    Private Shared Function Scalar(value As JToken) As String
        If value Is Nothing OrElse value.Type = JTokenType.Null Then Return Nothing
        If value.Type <> JTokenType.String Then Throw New InvalidDataException("版本文本字段类型无效。")
        Return CStr(value)
    End Function
    Private Shared Function HashBytes(bytes As Byte()) As String
        If bytes Is Nothing Then Return Nothing
        Using hash = Security.Cryptography.SHA256.Create()
            Return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "")
        End Using
    End Function
    Private Shared Sub ValidateDirectory(full As String)
        If String.IsNullOrWhiteSpace(full) Then Throw New InvalidDataException("游戏目录为空。")
        Dim parent = Path.GetFullPath(full)
        While parent IsNot Nothing
            If File.Exists(parent) Then Throw New IOException("文件占用了游戏目录：" & parent)
            If Directory.Exists(parent) AndAlso (File.GetAttributes(parent) And FileAttributes.ReparsePoint) <> 0 Then Throw New IOException("目录包含链接，请选择真实目录：" & parent)
            parent = Path.GetDirectoryName(parent)
        End While
    End Sub
    Public Shared Function FolderPath(instance As InstalledInstance, relative As String, Optional token As CancellationToken = Nothing) As String
        EnsureUnchanged(instance, token)
        Dim full = If(String.IsNullOrEmpty(relative), instance.InstancePath, SafeFiles.UnderRoot(instance.RunDirectory, relative & "/entry"))
        If Not String.IsNullOrEmpty(relative) Then full = Path.GetDirectoryName(full)
        ValidateDirectory(full)
        If Not Directory.Exists(full) Then Throw New DirectoryNotFoundException("此文件夹尚不存在。")
        Return full
    End Function
    Private Shared Function Same(first As String, second As String) As Boolean
        Return first IsNot Nothing AndAlso second IsNot Nothing AndAlso String.Equals(Path.GetFullPath(first).TrimEnd("\"c, "/"c), Path.GetFullPath(second).TrimEnd("\"c, "/"c), StringComparison.OrdinalIgnoreCase)
    End Function
End Class
