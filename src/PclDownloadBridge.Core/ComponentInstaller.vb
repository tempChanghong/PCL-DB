Imports System.Diagnostics
Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Newtonsoft.Json.Linq

' Adapted from PCL McInstallLoadersGet / McDownloadForgelikeLoader /
' McDownloadLiteLoaderLoader / MergeJson. Uses the shared PCL-style downloader
' for installer dependencies, then runs the official install_profile processors.
Public Class ComponentInstaller
    Private ReadOnly _policy As SourcePolicy
    Private ReadOnly _selection As ComponentSelection
    Private ReadOnly _progress As IProgress(Of TransferProgress)
    Private ReadOnly _root As String
    Private ReadOnly _reuse As List(Of String)
    Private ReadOnly _connections As Integer
    Private ReadOnly _limit As Long
    Private ReadOnly _budget As DownloadBudget
    Public Property NetworkBytes As Long
    Public Property Reused As Integer
    Public Property FileCount As Integer
    Public Property DynamicSplits As Integer
    Public Property Retries As Integer
    Private ReadOnly _processorClock As Stopwatch = Stopwatch.StartNew()
    Private _lastProcessorReport As Long
    Private _publishedRuntimes As New List(Of DownloadFile)
    Friend Function LockPublishedRuntimes(token As CancellationToken) As ComponentRuntimePublication.PublishedLease
        Return New ComponentRuntimePublication.PublishedLease(_root, _publishedRuntimes, token)
    End Function
    Public Sub New(policy As SourcePolicy, selection As ComponentSelection, root As String, reuse As IEnumerable(Of String), progress As IProgress(Of TransferProgress), connections As Integer, limit As Long, Optional budget As DownloadBudget = Nothing)
        If selection Is Nothing Then Throw New ArgumentNullException(NameOf(selection))
        _policy = policy : _selection = selection.Copy() : _root = root : _progress = progress
        _reuse = reuse.Concat({root}).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        _connections = connections : _limit = limit : _budget = budget
    End Sub

    Public Async Function ApplyAsync(metadata As JObject, mc As String, stage As String, name As String, token As CancellationToken, Optional isolated As Boolean = True, Optional writePclSettings As Boolean = True) As Task(Of JObject)
        _publishedRuntimes.Clear()
        _selection.Validate(mc)
        ModFiles(_selection, mc) ' Reject ambiguous or escaping add-on names before downloading.
        Dim workRelative = ".vanilla-downloader/components/" & Guid.NewGuid().ToString("N")
        Dim work = SafeFiles.UnderRoot(_root, workRelative)
        Directory.CreateDirectory(work)
        Dim merged = DirectCast(metadata.DeepClone(), JObject)
        ' Every generated file remains in the staging tree until all processors finish.
        ' No global launcher_profiles.json or existing instance settings are touched.
        Try
            Dim vanilla = SafeFiles.UnderRoot(work, "versions/" & mc & "/" & mc)
            Directory.CreateDirectory(Path.GetDirectoryName(vanilla))
            File.Copy(Path.Combine(stage, name & ".jar"), vanilla & ".jar")
            File.WriteAllText(vanilla & ".json", metadata.ToString(), New UTF8Encoding(False))
            File.WriteAllText(Path.Combine(work, "launcher_profiles.json"), "{""profiles"":{}}")
            For Each kind In {ComponentKind.Forge, ComponentKind.NeoForge, ComponentKind.LiteLoader, ComponentKind.Fabric}
                Dim entry = _selection.Item(kind)
                If entry Is Nothing Then Continue For
                Dim profile As JObject
                If kind = ComponentKind.Fabric Then
                    Emit("components", "下载 Fabric", "读取启动配置", 0.1)
                    profile = Await _policy.GetJsonAsync("https://meta.fabricmc.net/v2/versions/loader/" & Uri.EscapeDataString(mc) & "/" & Uri.EscapeDataString(entry.Version) & "/profile/json", token).ConfigureAwait(False)
                    If CStr(profile("inheritsFrom")) <> mc Then Throw New InvalidDataException("Fabric 配置指向其他游戏版本。")
                ElseIf kind = ComponentKind.LiteLoader Then
                    profile = LiteProfile(entry)
                Else
                    profile = Await InstallForgelikeAsync(entry, metadata, work, vanilla & ".jar", token).ConfigureAwait(False)
                End If
                merged = MergeProfile(merged, profile)
            Next
            Dim opt = _selection.Item(ComponentKind.OptiFine)
            If opt IsNot Nothing Then
                SafeFiles.ValidateInstanceName(opt.FileName)
                If _selection.Has(ComponentKind.Forge) OrElse _selection.Has(ComponentKind.Fabric) OrElse _selection.Has(ComponentKind.LiteLoader) Then
                    ' PCL OptiFineAsMod: LiteLoader stores OptiFine in mods/<mc>/.
                    Dim relative = "mods/" & If(_selection.Has(ComponentKind.LiteLoader), mc & "/", "") & opt.FileName
                    Await FetchOptiFineAsync(stage, relative, opt, token).ConfigureAwait(False)
                Else
                    Dim installer = "installers/optifine.jar"
                    Await FetchOptiFineAsync(work, installer, opt, token).ConfigureAwait(False)
                    Dim jar = SafeFiles.UnderRoot(work, installer)
                    Using archive = ZipFile.OpenRead(jar)
                        If archive.GetEntry("optifine/Installer.class") Is Nothing Then Throw New InvalidDataException("OptiFine 下载文件不是安装器。")
                    End Using
                    Using input = GetType(ComponentInstaller).Assembly.GetManifestResourceStream("OptiFineBridge.class"), output = File.Create(Path.Combine(work, "OptiFineBridge.class"))
                        input.CopyTo(output)
                    End Using
                    Emit("processors", "安装 OptiFine", "正在生成 OptiFine 客户端", 0.35)
                    Dim minimumJava As Integer
                    Using archive = ZipFile.OpenRead(jar), reader As New BinaryReader(archive.GetEntry("optifine/Installer.class").Open())
                        Dim header = reader.ReadBytes(8)
                        If header.Length <> 8 OrElse header(0) <> &HCA OrElse header(1) <> &HFE OrElse header(2) <> &HBA OrElse header(3) <> &HBE Then Throw New InvalidDataException("OptiFine 安装器 class 文件头无效。")
                        minimumJava = Math.Max(8, header(6) * 256 + header(7) - 44)
                    End Using
                    Dim java = JavaRuntime.Find(_selection.JavaPath, minimumJava)
                    Await JavaRuntime.RunAsync(java, {"-Djava.awt.headless=true", "-Duser.home=" & work, "-cp", work & Path.PathSeparator & jar, "OptiFineBridge", work}, work, AddressOf ProcessorLine, token).ConfigureAwait(False)
                    Dim profiles = Directory.EnumerateFiles(Path.Combine(work, "versions"), "*.json", SearchOption.AllDirectories).Where(Function(p) Not String.Equals(p, vanilla & ".json", StringComparison.OrdinalIgnoreCase)).ToList()
                    If profiles.Count <> 1 Then Throw New InvalidDataException("OptiFine 未生成唯一的启动配置。")
                    merged = MergeProfile(merged, JObject.Parse(File.ReadAllText(profiles(0))))
                End If
            End If
            For Each kind In {ComponentKind.FabricApi, ComponentKind.OptiFabric}
                Dim entry = _selection.Item(kind)
                If entry Is Nothing Then Continue For
                SafeFiles.ValidateInstanceName(entry.FileName)
                Await FetchAsync(stage, {New DownloadFile With {.RelativePath = "mods/" & entry.FileName, .Url = entry.Url, .Sha1 = entry.Sha1, .Md5 = entry.Md5, .Size = entry.Size, .Category = "component"}}, "下载 " & ComponentCatalog.Label(kind), token).ConfigureAwait(False)
                If kind = ComponentKind.OptiFabric Then
                    Dim info = ReadFabricMod(SafeFiles.UnderRoot(stage, "mods/" & entry.FileName))
                    If CStr(info("id")) <> "optifabric" Then Throw New InvalidDataException("下载的文件不是 OptiFabric 模组。")
                End If
            Next
            ' Download all final launch libraries, including legacy Forge/LiteLoader.
            merged = MinecraftInstaller.PreparePclLibraryPaths(merged)
            Dim libraries = MinecraftInstaller.BuildBasePlan(merged, name).Where(Function(f) f.Category = "library").ToList()
            Dim missing As New List(Of DownloadFile)
            For Each libraryInfo In libraries
                Dim local = SafeFiles.UnderRoot(work, libraryInfo.RelativePath)
                If Not SafeFiles.Matches(local, libraryInfo) Then
                    If String.IsNullOrWhiteSpace(libraryInfo.Url) Then Throw New FileNotFoundException("安装器缺少生成的支持库：" & libraryInfo.RelativePath)
                    missing.Add(libraryInfo)
                End If
            Next
            Await FetchAsync(work, missing.Where(Function(item) ComponentRuntimePublication.HasDigest(item)), "补全组件支持库", token).ConfigureAwait(False)
            ' A same-sized shared hashless file is not a trusted source. Fetch these
            ' into this fresh private work tree without root donors or weak caches.
            Await FetchAsync(work, missing.Where(Function(item) Not ComponentRuntimePublication.HasDigest(item)), "补全组件支持库", token, allowReuse:=False).ConfigureAwait(False)
            token.ThrowIfCancellationRequested()
            Emit("processors", "校验安装结果", "校验处理器输出与启动支持库", 0.95)
            For Each libraryInfo In libraries
                If Not SafeFiles.Matches(SafeFiles.UnderRoot(work, libraryInfo.RelativePath), libraryInfo) Then Throw New InvalidDataException("组件支持库校验失败：" & libraryInfo.RelativePath)
            Next
            ' Publish only the checked runtime plan. Embedded processor dependencies and
            ' unrelated installer files remain private, even if their paths collide.
            Using publication As New ComponentRuntimePublication(_root)
                publication.Publish(work, libraries, token)
                _publishedRuntimes = publication.FrozenFiles
            End Using
            If IsModable(_selection) Then Directory.CreateDirectory(Path.Combine(stage, "mods"))
            Directory.CreateDirectory(Path.Combine(stage, "resourcepacks"))
            Directory.CreateDirectory(Path.Combine(stage, "PCL"))
            If writePclSettings Then File.WriteAllText(Path.Combine(stage, "PCL", "Setup.ini"), "VersionArgumentIndie:" & If(isolated, "1", "2") & vbCrLf & "VersionArgumentIndieV2:" & isolated.ToString() & vbCrLf, New UTF8Encoding(False))
            Dim record As New JObject From {{"minecraft", mc}, {"components", JArray.FromObject(_selection.Entries.Select(Function(v) New With {.kind = ComponentCatalog.Label(v.Key), .version = v.Value.Version}))}}
            File.WriteAllText(Path.Combine(stage, "installation.json"), record.ToString(), New UTF8Encoding(False))
            Return merged
        Finally
            InstallCleanup.DeleteOwnedDirectory(_root, workRelative)
        End Try
    End Function

    Friend Shared Function IsModable(selection As ComponentSelection) As Boolean
        Return selection IsNot Nothing AndAlso {ComponentKind.Forge, ComponentKind.NeoForge, ComponentKind.Fabric, ComponentKind.LiteLoader}.Any(Function(kind) selection.Has(kind))
    End Function
    Friend Shared Function ModFiles(selection As ComponentSelection, mc As String) As List(Of DownloadFile)
        Dim result As New List(Of DownloadFile)
        If selection Is Nothing Then Return result
        For Each kind In {ComponentKind.OptiFine, ComponentKind.FabricApi, ComponentKind.OptiFabric}
            Dim entry = selection.Item(kind)
            If entry Is Nothing Then Continue For
            If kind = ComponentKind.OptiFine AndAlso Not (selection.Has(ComponentKind.Forge) OrElse selection.Has(ComponentKind.Fabric) OrElse selection.Has(ComponentKind.LiteLoader)) Then Continue For
            SafeFiles.ValidateInstanceName(entry.FileName)
            Dim relative = "mods/" & If(kind = ComponentKind.OptiFine AndAlso selection.Has(ComponentKind.LiteLoader), mc & "/", "") & entry.FileName
            MinecraftInstaller.ValidateResourceName(relative, "组件 Mods")
            If result.Any(Function(item) String.Equals(item.RelativePath, relative, StringComparison.OrdinalIgnoreCase)) Then Throw New InvalidDataException("组件 Mods 文件名冲突：" & relative)
            result.Add(New DownloadFile With {.RelativePath = relative, .Url = entry.Url, .Sha1 = entry.Sha1, .Md5 = entry.Md5, .Size = entry.Size, .Category = "component"})
        Next
        Return result
    End Function

    Private Async Function InstallForgelikeAsync(entry As ComponentEntry, metadata As JObject, work As String, clientJar As String, token As CancellationToken) As Task(Of JObject)
        Dim installerRelative = "installers/" & entry.Kind.ToString().ToLowerInvariant() & ".jar"
        Dim download As New DownloadFile With {.RelativePath = installerRelative, .Url = entry.Url, .Sha1 = entry.Sha1, .Md5 = entry.Md5, .Category = "component"}
        Dim label = ComponentCatalog.Label(entry.Kind)
        If String.IsNullOrWhiteSpace(download.Sha1) AndAlso String.IsNullOrWhiteSpace(download.Md5) Then Emit("components", "读取 " & label & " 安装器校验信息", "校验信息就绪后下载安装器", 0.05)
        Await ForgelikeInstallerChecksum.ResolveAsync(_policy, download, token, label).ConfigureAwait(False)
        Await FetchAsync(work, {download}, "下载 " & ComponentCatalog.Label(entry.Kind) & " 安装器", token).ConfigureAwait(False)
        Dim installerPath = SafeFiles.UnderRoot(work, installerRelative)
        Dim profile As JObject, version As JObject
        Using zip = ZipFile.OpenRead(installerPath)
            profile = ReadJson(zip, "install_profile.json")
            If profile("minecraft") IsNot Nothing AndAlso CStr(profile("minecraft")) <> entry.Minecraft Then Throw New InvalidDataException("安装器不适用于所选游戏版本。")
            If zip.GetEntry("version.json") Is Nothing Then
                ' PCL Forge installation method B: versionInfo + bundled universal JAR.
                version = TryCast(profile("versionInfo"), JObject)
                If version Is Nothing Then Throw New InvalidDataException("Forge 安装器缺少版本配置。")
                Dim install = TryCast(profile("install"), JObject)
                If install IsNot Nothing Then
                    Dim bundled = zip.GetEntry(CStr(install("filePath")))
                    If bundled Is Nothing Then Throw New InvalidDataException("旧 Forge 安装器缺少主文件。")
                    ExtractEntry(bundled, SafeFiles.UnderRoot(work, "libraries/" & MinecraftInstaller.MavenPath(CStr(install("path")))))
                End If
            Else
                version = ReadJson(zip, "version.json")
            End If
            For Each embedded In zip.Entries.Where(Function(e) e.FullName.StartsWith("maven/", StringComparison.Ordinal) AndAlso Not e.FullName.EndsWith("/", StringComparison.Ordinal))
                ExtractEntry(embedded, SafeFiles.UnderRoot(work, "libraries/" & embedded.FullName.Substring(6)))
            Next
            For Each data As JProperty In If(TryCast(profile("data"), JObject), New JObject()).Properties()
                Dim value = CStr(data.Value("client"))
                If value IsNot Nothing AndAlso value.StartsWith("/", StringComparison.Ordinal) Then
                    Dim embedded = zip.GetEntry(value.TrimStart("/"c))
                    If embedded Is Nothing Then Throw New InvalidDataException("安装器缺少数据：" & value)
                    ExtractEntry(embedded, SafeFiles.UnderRoot(work, "data/" & value.TrimStart("/"c)))
                End If
            Next
        End Using
        Dim files As New List(Of DownloadFile)
        For Each libraryInfo In If(TryCast(profile("libraries"), JArray), New JArray()).Concat(If(TryCast(version("libraries"), JArray), New JArray())).OfType(Of JObject)()
            If Not MinecraftInstaller.RulesAllow(libraryInfo("rules")) Then Continue For
            Dim artifact = TryCast(libraryInfo.SelectToken("downloads.artifact"), JObject)
            Dim relative = If(CStr(artifact?("path")), MinecraftInstaller.MavenPath(CStr(libraryInfo("name"))))
            Dim url = If(CStr(artifact?("url")), If(CStr(libraryInfo("url")), "https://libraries.minecraft.net/").TrimEnd("/"c) & "/" & relative)
            Dim item As New DownloadFile With {.RelativePath = "libraries/" & relative, .Url = url, .Sha1 = CStr(artifact?("sha1")), .Size = If(artifact?("size") Is Nothing, -1, CLng(artifact("size"))), .Category = "library"}
            If Not String.IsNullOrWhiteSpace(url) Then files.Add(item)
        Next
        Dim variables = ProcessorVariables(profile, work, installerPath, clientJar)
        Dim mappings = TryCast(metadata.SelectToken("downloads.client_mappings"), JObject)
        If variables.ContainsKey("MOJMAPS") AndAlso mappings IsNot Nothing Then
            Dim relative = variables("MOJMAPS").Substring(work.TrimEnd("\"c).Length + 1)
            files.Add(New DownloadFile With {.RelativePath = relative, .Url = CStr(mappings("url")), .Sha1 = CStr(mappings("sha1")), .Size = CLng(mappings("size")), .Category = "meta"})
        End If
        ' Combine all integrity declarations before filtering embedded/reusable files.
        ' Otherwise an earlier weak match can hide conflicts or change the first URL.
        files = DownloadPlan.Merge(files, work).Where(Function(item) Not SafeFiles.Matches(SafeFiles.UnderRoot(work, item.RelativePath), item)).ToList()
        Await FetchAsync(work, files, "下载 " & ComponentCatalog.Label(entry.Kind) & " 支持库", token).ConfigureAwait(False)
        Dim processors = If(TryCast(profile("processors"), JArray), New JArray()).OfType(Of JObject)().Where(Function(p) p("sides") Is Nothing OrElse p("sides").Values(Of String)().Contains("client")).ToList()
        If processors.Count > 0 Then
            Dim java = JavaRuntime.Find(_selection.JavaPath, Math.Max(8, CInt(If(metadata.SelectToken("javaVersion.majorVersion"), New JValue(8)))))
            Dim outputCache As New VerifiedComponentCache(_root, _reuse, processorOutputs:=True)
            For i = 0 To processors.Count - 1
                token.ThrowIfCancellationRequested()
                Dim processor = processors(i)
                Dim arguments = DirectCast(processor("args"), JArray).Values(Of String)().Select(Function(a) Resolve(a, variables, work)).ToList()
                Dim outputs = ProcessorOutputs(TryCast(processor("outputs"), JObject), variables, work)
                ' Only a complete, nonempty SHA-1 declaration can discharge execution.
                ' Some modern NeoForge profiles have no outputs: always run those.
                Dim cacheable = outputs.Count > 0 AndAlso outputs.All(Function(item) Regex.IsMatch(If(item.Value.Sha1, ""), "\A[0-9a-fA-F]{40}\z"))
                Emit("processors", "安装 " & ComponentCatalog.Label(entry.Kind), "处理器 " & (i + 1) & "/" & processors.Count & " · " & CStr(processor("jar")), i / CDbl(processors.Count))
                token.ThrowIfCancellationRequested()
                Dim reusedOutputs = False
                If cacheable Then
                    For Each output In outputs
                        token.ThrowIfCancellationRequested()
                        If SafeFiles.Matches(output.Key, output.Value) Then Continue For
                        Directory.CreateDirectory(Path.GetDirectoryName(output.Key))
                        outputCache.TryRestore(output.Key, output.Value, token)
                    Next
                    reusedOutputs = outputs.All(Function(item) SafeFiles.Matches(item.Key, item.Value))
                    If reusedOutputs Then Emit("processors", "安装 " & ComponentCatalog.Label(entry.Kind), "处理器 " & (i + 1) & "/" & processors.Count & " · 已校验输出，跳过重复执行", (i + 1) / CDbl(processors.Count))
                End If
                token.ThrowIfCancellationRequested()
                ' PCL predownloads official mappings: skip their redundant network processor.
                Dim mapsTask = arguments.Contains("DOWNLOAD_MOJMAPS") AndAlso variables.ContainsKey("MOJMAPS") AndAlso File.Exists(variables("MOJMAPS"))
                If Not mapsTask AndAlso Not reusedOutputs Then
                    Dim jar = SafeFiles.UnderRoot(work, "libraries/" & MinecraftInstaller.MavenPath(CStr(processor("jar"))))
                    Dim classpath = DirectCast(processor("classpath"), JArray).Values(Of String)().Select(Function(c) SafeFiles.UnderRoot(work, "libraries/" & MinecraftInstaller.MavenPath(c))).Concat({jar}).Distinct().ToList()
                    For Each file In classpath
                        If Not System.IO.File.Exists(file) Then Throw New FileNotFoundException("处理器支持库缺失：" & file)
                    Next
                    Dim args As New List(Of String) From {"-Djava.awt.headless=true", "-cp", String.Join(Path.PathSeparator.ToString(), classpath), ManifestMainClass(jar)}
                    args.AddRange(arguments)
                    Await JavaRuntime.RunAsync(java, args, work, AddressOf ProcessorLine, token).ConfigureAwait(False)
                End If
                For Each output In outputs
                    token.ThrowIfCancellationRequested()
                    SafeFiles.UnderRoot(work, output.Value.RelativePath) ' Recheck links after Java has run.
                    If Not SafeFiles.Matches(output.Key, output.Value) Then Throw New InvalidDataException("处理器输出校验失败：" & output.Key)
                Next
                ' Verify every output before persisting any of them. Intermediates may
                ' share a path with a later stage; their content keys stay independent.
                If cacheable Then
                    For Each output In outputs
                        outputCache.Save(output.Key, output.Value, token)
                    Next
                End If
            Next
        End If
        Return version
    End Function

    Private Shared Function ProcessorOutputs(declarations As JObject, variables As Dictionary(Of String, String), work As String) As List(Of KeyValuePair(Of String, DownloadFile))
        Dim outputs As New List(Of KeyValuePair(Of String, DownloadFile))
        Dim prefix = Path.GetFullPath(work).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) & Path.DirectorySeparatorChar
        For Each declaration In If(declarations, New JObject()).Properties()
            Dim resolved = Resolve(declaration.Name, variables, work)
            ' Java resolves relative paths against work, never the app's current directory.
            Dim destination = Path.GetFullPath(If(Path.IsPathRooted(resolved), resolved, Path.Combine(work, resolved)))
            If Not destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) Then Throw New InvalidDataException("处理器输出超出安装临时目录：" & resolved)
            Dim relative = destination.Substring(prefix.Length)
            destination = SafeFiles.UnderRoot(work, relative)
            outputs.Add(New KeyValuePair(Of String, DownloadFile)(destination, New DownloadFile With {.RelativePath = relative, .Sha1 = Resolve(CStr(declaration.Value), variables, work)}))
        Next
        Return outputs
    End Function

    Private Async Function FetchAsync(root As String, files As IEnumerable(Of DownloadFile), title As String, token As CancellationToken, Optional allowReuse As Boolean = True) As Task
        Dim items = DownloadPlan.Merge(files, root)
        If items.Count = 0 Then Return
        Dim reporter As New InlineProgress(Sub(value)
                                              value.Phase = "components" : value.Stage = title
                                              value.StageFraction = value.Fraction
                                              value.NetworkBytes += NetworkBytes : value.Reused += Reused
                                              value.DynamicSplits += DynamicSplits : value.Retries += Retries
                                              _progress?.Report(value)
                                          End Sub)
        ' Installer-profile dependencies can include the final runtime too, before
        ' the final missing-library pass. Apply the same donor rule at every fetch
        ' boundary, not only in that final pass, or a bad shared file could already
        ' have been copied into work and then acquire a self-consistent local hash.
        For Each batch In items.GroupBy(Function(item) allowReuse AndAlso ComponentRuntimePublication.HasDigest(item))
            Dim reusable = batch.Key
            Using engine As New DownloadEngine(_policy, reporter, If(reusable, _reuse, Enumerable.Empty(Of String)()), _connections, If(reusable, _root, Nothing), useComponentCache:=reusable, budget:=_budget)
                engine.BytesPerSecondLimit = _limit
                Await engine.DownloadAsync(root, batch, token).ConfigureAwait(False)
                NetworkBytes += engine.NetworkBytes : Reused += engine.Reused : DynamicSplits += engine.DynamicSplits
                Retries += engine.Retries
                FileCount += batch.Count()
            End Using
        Next
    End Function
    Private Async Function FetchOptiFineAsync(root As String, relative As String, entry As ComponentEntry, token As CancellationToken) As Task
        Dim mirror As Func(Of Task) = Async Function()
            Await FetchAsync(root, {New DownloadFile With {.RelativePath = relative, .Url = entry.Url, .Category = "component"}}, "下载 OptiFine（BMCLAPI）", token).ConfigureAwait(False)
            ValidateOptiFine(SafeFiles.UnderRoot(root, relative))
        End Function
        Dim official As Func(Of Task) = Function() FetchOptiFineOfficialAsync(root, relative, entry, token)
        Dim failures As New List(Of Exception)
        For Each download In If(_policy.PreferOfficial, {official, mirror}, {mirror, official})
            Try
                Await download().ConfigureAwait(False)
                Return
            Catch ex As Exception When Not token.IsCancellationRequested
                failures.Add(ex)
                Dim invalid = SafeFiles.UnderRoot(root, relative)
                If File.Exists(invalid) Then File.Delete(invalid) ' owned private payload only
            End Try
        Next
        token.ThrowIfCancellationRequested()
        Throw New AggregateException("OptiFine 官方源与 BMCLAPI 均下载失败。", failures)
    End Function
    Private Async Function FetchOptiFineOfficialAsync(root As String, relative As String, entry As ComponentEntry, token As CancellationToken) As Task
        ' PCL's official adloadx -> downloadx resolution, still using the shared engine.
        Dim page = Await _policy.GetTextAsync("https://optifine.net/adloadx?f=" & Uri.EscapeDataString(entry.FileName), token).ConfigureAwait(False)
        Dim match = Regex.Match(page, "downloadx\?f=[^""'\s<>]+")
        If Not match.Success Then Throw New InvalidDataException("OptiFine 官方下载链接解析失败。")
        Dim source = "https://optifine.net/" & Net.WebUtility.HtmlDecode(match.Value)
        Dim path = SafeFiles.UnderRoot(root, relative)
        If File.Exists(path) Then File.Delete(path) ' only this private staging payload
        Await FetchAsync(root, {New DownloadFile With {.RelativePath = relative, .Url = source, .Category = "component"}}, "下载 OptiFine（官方源）", token).ConfigureAwait(False)
        ValidateOptiFine(path)
    End Function
    Private Sub ValidateOptiFine(path As String)
        ' PCL McDownloadOptiFineSaveLoader uses 64 KiB for a mod, while
        ' McDownloadOptiFineLoader requires 300 KiB for standalone installation.
        Dim asMod = _selection.Has(ComponentKind.Forge) OrElse _selection.Has(ComponentKind.Fabric) OrElse _selection.Has(ComponentKind.LiteLoader)
        Dim minimum = If(asMod, 64, 300) * 1024L
        If New FileInfo(path).Length < minimum Then Throw New InvalidDataException("OptiFine 下载文件过小，至少需要 " & (minimum \ 1024).ToString() & " KiB。")
        Using archive = ZipFile.OpenRead(path)
            If archive.GetEntry("optifine/Installer.class") Is Nothing Then Throw New InvalidDataException("下载文件不是有效的 OptiFine 安装器。")
        End Using
    End Sub
    Public Shared Function ReadFabricMod(path As String) As JObject
        Using archive = ZipFile.OpenRead(path)
            Return ReadJson(archive, "fabric.mod.json")
        End Using
    End Function
    Private Sub Emit(phase As String, stage As String, detail As String, fraction As Double)
        _progress?.Report(New TransferProgress With {.Phase = phase, .Stage = stage, .Detail = detail, .StageFraction = fraction, .NetworkBytes = NetworkBytes, .Reused = Reused, .DynamicSplits = DynamicSplits, .Retries = Retries})
    End Sub
    Private Sub ProcessorLine(line As String)
        ' Logging is best effort; a blocked log must not stop Java or UI progress.
        SyncLock Me
            InstallLog.Append(_root, line)
            If _processorClock.ElapsedMilliseconds - _lastProcessorReport < 120 Then Return
            _lastProcessorReport = _processorClock.ElapsedMilliseconds
        End SyncLock
        Emit("processors", "运行安装处理器", line, -1)
    End Sub
    Public Shared Function MergeProfile(basis As JObject, addition As JObject) As JObject
        Dim result = DirectCast(basis.DeepClone(), JObject)
        Dim overlay = DirectCast(addition.DeepClone(), JObject)
        overlay.Remove("time") : overlay.Remove("releaseTime") : overlay.Remove("id")
        Dim oldArguments = CStr(result("minecraftArguments"))
        Dim newArguments = CStr(overlay("minecraftArguments"))
        If oldArguments IsNot Nothing AndAlso newArguments IsNot Nothing Then
            ' A full legacy launch string already includes vanilla arguments.
            overlay("minecraftArguments") = If(newArguments.Contains("${auth_player_name}"), newArguments, oldArguments & " " & newArguments)
        End If
        ' PCL reads legacy and structured game arguments together. Keep conditional
        ' objects intact instead of flattening them or dropping the whole JVM section.
        If oldArguments IsNot Nothing AndAlso overlay.SelectToken("arguments.jvm") IsNot Nothing AndAlso result.SelectToken("arguments.jvm") Is Nothing Then
            ' The presence of arguments.jvm switches PCL out of its legacy fallback.
            ' Carry that fallback forward when a loader adds JVM options to old MC.
            Dim structured = TryCast(result("arguments"), JObject)
            If structured Is Nothing Then
                structured = New JObject()
                result("arguments") = structured
            End If
            structured("jvm") = New JArray("-XX:HeapDumpPath=MojangTricksIntelDriversForPerformance_javaw.exe_minecraft.exe.heapdump", "-Djava.library.path=${natives_directory}", "-cp", "${classpath}")
        End If
        ' Arguments are ordered tokens: equal option names or values may repeat.
        result.Merge(overlay, New Newtonsoft.Json.Linq.JsonMergeSettings With {.MergeArrayHandling = MergeArrayHandling.Concat})
        Dim libraries = TryCast(result("libraries"), JArray)
        If libraries IsNot Nothing Then
            Dim ordered As New List(Of JObject)
            For Each libraryInfo As JObject In libraries
                For Each part In LibraryParts(libraryInfo)
                    ' PCL McLibListGetWithJson keys ordinary and native entries separately.
                    Dim key = PclLibrarySelection.Identity(part, part("natives") IsNot Nothing) & ":" & If(part("rules")?.ToString(), "")
                    Dim previous = ordered.FindIndex(Function(v) CStr(v("_mergeKey")) = key)
                    part("_mergeKey") = key
                    If previous < 0 Then
                        ordered.Add(part)
                    ElseIf Not MinecraftInstaller.RulesAllow(part("rules")) OrElse (part("natives") IsNot Nothing AndAlso CStr(part.SelectToken("natives.windows")) Is Nothing) Then
                        ordered(previous) = part ' Preserve inactive-platform metadata without resolving its files.
                    ElseIf PclLibrarySelection.CompareVersion(PclLibrarySelection.ProfileVersion(part), PclLibrarySelection.ProfileVersion(ordered(previous))) >= 0 Then
                        ordered(previous) = part
                    End If
                Next
            Next
            For Each libraryInfo In ordered : libraryInfo.Remove("_mergeKey") : Next
            result("libraries") = New JArray(ordered)
        End If
        result.Remove("inheritsFrom") : result.Remove("jar") : result.Remove("_comment_")
        Return result
    End Function

    Private Shared Iterator Function LibraryParts(libraryInfo As JObject) As IEnumerable(Of JObject)
        Dim copy = DirectCast(libraryInfo.DeepClone(), JObject)
        If copy("natives") Is Nothing OrElse copy("natives").Type = JTokenType.Null Then
            copy.Remove("natives") ' PCL also clears explicit null native markers.
            Yield copy
            Return
        End If
        Dim downloads = TryCast(copy("downloads"), JObject)
        If downloads?("artifact") IsNot Nothing AndAlso downloads("artifact").Type <> JTokenType.Null Then
            ' A combined declaration represents two independently overridable files.
            ' Emit separate PCL-readable entries, so upgrading one preserves the other.
            Dim runtime = DirectCast(copy.DeepClone(), JObject)
            runtime.Remove("natives") : runtime.Remove("extract")
            DirectCast(runtime("downloads"), JObject).Remove("classifiers")
            Yield runtime
        End If
        downloads?.Remove("artifact")
        Yield copy
    End Function

    Public Shared Function LiteProfile(entry As ComponentEntry) As JObject
        Dim libs = DirectCast(entry.Data("libraries").DeepClone(), JArray)
        libs.Add(New JObject From {{"name", "com.mumfrey:liteloader:" & entry.Version}, {"url", "https://dl.liteloader.com/versions/"}})
        Return New JObject From {{"libraries", libs}, {"mainClass", "net.minecraft.launchwrapper.Launch"}, {"arguments", New JObject From {{"game", New JArray("--tweakClass", CStr(entry.Data("tweakClass")))}}}}
    End Function
    Public Shared Function ProcessorVariables(profile As JObject, work As String, installer As String, clientJar As String) As Dictionary(Of String, String)
        Dim values As New Dictionary(Of String, String)(StringComparer.Ordinal) From {{"SIDE", "client"}, {"ROOT", work}, {"LIBRARY_DIR", Path.Combine(work, "libraries")}, {"INSTALLER", installer}, {"MINECRAFT_JAR", clientJar}, {"MINECRAFT_VERSION", CStr(profile("minecraft"))}}
        For Each data As JProperty In If(TryCast(profile("data"), JObject), New JObject()).Properties()
            Dim value = CStr(data.Value("client"))
            If value Is Nothing Then Continue For
            If value.StartsWith("'", StringComparison.Ordinal) Then
                value = value.Trim("'"c)
            ElseIf value.StartsWith("[", StringComparison.Ordinal) Then
                value = SafeFiles.UnderRoot(work, "libraries/" & MinecraftInstaller.MavenPath(value.Trim("["c, "]"c)))
            ElseIf value.StartsWith("/", StringComparison.Ordinal) Then
                value = SafeFiles.UnderRoot(work, "data/" & value.TrimStart("/"c))
            End If
            values(data.Name) = value
        Next
        Return values
    End Function
    Public Shared Function Resolve(value As String, variables As Dictionary(Of String, String), work As String) As String
        If value.StartsWith("[", StringComparison.Ordinal) AndAlso value.EndsWith("]", StringComparison.Ordinal) Then Return SafeFiles.UnderRoot(work, "libraries/" & MinecraftInstaller.MavenPath(value.Trim("["c, "]"c)))
        Return Regex.Replace(value, "\{([A-Z_]+)\}", Function(m)
                                                                   If Not variables.ContainsKey(m.Groups(1).Value) Then Throw New InvalidDataException("未知安装器变量：" & m.Value)
                                                                   Return variables(m.Groups(1).Value)
                                                               End Function)
    End Function
    Private Shared Function ReadJson(zip As ZipArchive, name As String) As JObject
        Dim entry = zip.GetEntry(name)
        If entry Is Nothing OrElse entry.Length > 32 * 1024 * 1024 Then Throw New InvalidDataException("安装器元数据无效：" & name)
        Using reader As New StreamReader(entry.Open())
            Return JObject.Parse(reader.ReadToEnd())
        End Using
    End Function
    Private Shared Sub ExtractEntry(entry As ZipArchiveEntry, target As String)
        Directory.CreateDirectory(Path.GetDirectoryName(target))
        Using input = entry.Open(), output = File.Create(target)
            input.CopyTo(output)
        End Using
    End Sub
    Private Shared Function ManifestMainClass(jar As String) As String
        Using zip = ZipFile.OpenRead(jar), reader As New StreamReader(zip.GetEntry("META-INF/MANIFEST.MF").Open())
            Dim manifest = reader.ReadToEnd().Replace(vbCrLf & " ", "").Replace(vbLf & " ", "")
            Dim match = Regex.Match(manifest, "(?m)^Main-Class:\s*([^\r\n]+)")
            If Not match.Success Then Throw New InvalidDataException("安装处理器缺少 Main-Class。")
            Return match.Groups(1).Value.Trim()
        End Using
    End Function
End Class

Public Class InlineProgress
    Implements IProgress(Of TransferProgress)
    Private ReadOnly _report As Action(Of TransferProgress)
    Public Sub New(report As Action(Of TransferProgress))
        _report = report
    End Sub
    Public Sub Report(value As TransferProgress) Implements IProgress(Of TransferProgress).Report
        _report(value)
    End Sub
End Class

Public Module JavaRuntime
    Public Function Find(explicitPath As String, minimum As Integer) As String
        If Not String.IsNullOrWhiteSpace(explicitPath) Then
            Dim full = Path.GetFullPath(explicitPath)
            If Not File.Exists(full) Then Throw New FileNotFoundException("找不到所选 Java：" & full)
            If Major(full) < minimum Then Throw New InvalidOperationException("安装器需要 Java " & minimum & " 或更高版本。")
            Return full
        End If
        Dim candidates As New List(Of String)
        Dim javaHome = Environment.GetEnvironmentVariable("JAVA_HOME")
        If Not String.IsNullOrEmpty(javaHome) Then candidates.Add(Path.Combine(javaHome, "bin", "java.exe"))
        For Each directory In Environment.GetEnvironmentVariable("PATH").Split(Path.PathSeparator)
            If Not String.IsNullOrWhiteSpace(directory) Then candidates.Add(Path.Combine(directory.Trim(""""c), "java.exe"))
        Next
        For Each base In {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jdks")}
            If Directory.Exists(base) Then
                For Each directory In System.IO.Directory.EnumerateDirectories(base)
                    candidates.Add(Path.Combine(directory, "bin", "java.exe"))
                Next
            End If
        Next
        Dim available = candidates.Distinct(StringComparer.OrdinalIgnoreCase).Where(Function(p) File.Exists(p)).Select(Function(p) New With {.Path = p, .Version = Major(p)}).Where(Function(j) j.Version >= minimum).OrderBy(Function(j) j.Version).ToList()
        If available.Count = 0 Then Throw New InvalidOperationException("未发现可用的 Java " & minimum & " 或更高版本。请在下载设置中选择 java.exe。")
        Return available(0).Path
    End Function
    Private Function Major(java As String) As Integer
        Try
            Using process As New Process With {.StartInfo = New ProcessStartInfo(java, "-version") With {.UseShellExecute = False, .CreateNoWindow = True, .RedirectStandardError = True, .RedirectStandardOutput = True}}
                Dim clock = Stopwatch.StartNew()
                process.Start()
                Using output = process.StandardOutput, errors = process.StandardError
                    ' Both pipes must drain before waiting; JVM option notices can fill either.
                    Dim stdout = ReadMajorAsync(output), stderr = ReadMajorAsync(errors)
                    If Not process.WaitForExit(CInt(Math.Max(0L, 5000L - clock.ElapsedMilliseconds))) Then
                        process.Kill()
                        process.WaitForExit(1000)
                        Return 0
                    End If
                    If Not Task.WhenAll(stdout, stderr).Wait(CInt(Math.Max(0L, 5000L - clock.ElapsedMilliseconds))) Then Return 0
                    If process.ExitCode <> 0 Then Return 0
                    Return If(stderr.Result > 0, stderr.Result, stdout.Result)
                End Using
            End Using
        Catch
            Return 0
        End Try
    End Function
    Private Async Function ReadMajorAsync(reader As StreamReader) As Task(Of Integer)
        Dim buffer(1023) As Char, line As New StringBuilder(), overflow As Boolean, version As Integer
        Try
            Do
                Dim count = Await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(False)
                If count = 0 Then Exit Do
                For index = 0 To count - 1
                    If buffer(index) = ControlChars.Cr OrElse buffer(index) = ControlChars.Lf Then
                        If version = 0 AndAlso Not overflow Then version = ParseMajor(line.ToString())
                        line.Clear() : overflow = False
                    ElseIf version = 0 AndAlso Not overflow Then
                        If line.Length = 4096 Then
                            line.Clear() : overflow = True
                        Else
                            line.Append(buffer(index))
                        End If
                    End If
                Next
            Loop
            If version = 0 AndAlso Not overflow Then version = ParseMajor(line.ToString())
            Return version
        Catch ex As IOException
            Return 0
        Catch ex As ObjectDisposedException
            Return 0
        End Try
    End Function
    Private Function ParseMajor(line As String) As Integer
        ' PCL also recognizes the unquoted OpenJDK banner. Ignore option echoes.
        Dim match = Regex.Match(line, "\A\s*(?:(?:java|openjdk)(?: full)? version ""(?<version>[0-9][0-9A-Za-z._+\-]{0,127})""|openjdk (?<version>[0-9][0-9A-Za-z._+\-]{0,127})(?=\s|\z))", RegexOptions.IgnoreCase Or RegexOptions.CultureInvariant)
        If Not match.Success Then Return 0
        Dim value = match.Groups("version").Value
        If value.StartsWith("1.", StringComparison.Ordinal) Then value = value.Substring(2)
        Dim result As Integer
        Return If(Integer.TryParse(value.Split("."c, "_"c, "+"c, "-"c)(0), result) AndAlso result > 0, result, 0)
    End Function
    Public Async Function RunAsync(java As String, arguments As IEnumerable(Of String), work As String, report As Action(Of String), token As CancellationToken) As Task
        Dim info As New ProcessStartInfo With {.FileName = java, .Arguments = String.Join(" ", arguments.Select(AddressOf Quote)), .WorkingDirectory = work, .UseShellExecute = False, .CreateNoWindow = True, .RedirectStandardOutput = True, .RedirectStandardError = True}
        Dim completion As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Using process As New Process With {.StartInfo = info, .EnableRaisingEvents = True}
            AddHandler process.Exited, Sub() completion.TrySetResult(True)
            Dim lines As New Queue(Of String)
            Dim outputEnd As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
            Dim errorEnd As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
            Dim cancelled As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
            Dim accepting = True
            Dim consume As Action(Of String) = Sub(line)
                                                   SyncLock lines
                                                       If Not accepting Then Return
                                                       lines.Enqueue(line)
                                                       If lines.Count > 12 Then lines.Dequeue()
                                                       Try
                                                           report?.Invoke(line)
                                                       Catch ex As IOException
                                                           ' A full/removed log destination must not crash a stream callback.
                                                       End Try
                                                   End SyncLock
                                               End Sub
            Dim output As DataReceivedEventHandler = Sub(sender, e)
                                                         If e.Data Is Nothing Then outputEnd.TrySetResult(True) Else consume(e.Data)
                                                     End Sub
            Dim errors As DataReceivedEventHandler = Sub(sender, e)
                                                         If e.Data Is Nothing Then errorEnd.TrySetResult(True) Else consume(e.Data)
                                                     End Sub
            AddHandler process.OutputDataReceived, output : AddHandler process.ErrorDataReceived, errors
            Try
                token.ThrowIfCancellationRequested()
                process.Start() : process.BeginOutputReadLine() : process.BeginErrorReadLine()
                Using registration = token.Register(Sub()
                                                        cancelled.TrySetResult(True)
                                                        Try
                                                            If Not process.HasExited Then process.Kill()
                                                        Catch ex As InvalidOperationException
                                                        End Try
                                                    End Sub)
                    Await completion.Task.ConfigureAwait(False)
                    token.ThrowIfCancellationRequested()
                    ' A descendant can retain these pipes after the owned process exits.
                    ' Bound EOF draining, and keep cancellation responsive during it.
                    Dim streams = Task.WhenAll(outputEnd.Task, errorEnd.Task)
                    Dim finished = Await Task.WhenAny(streams, Task.Delay(5000), cancelled.Task).ConfigureAwait(False)
                    token.ThrowIfCancellationRequested()
                    Dim tail As String
                    SyncLock lines
                        tail = String.Join(vbCrLf, lines)
                    End SyncLock
                    If finished IsNot streams Then Throw New IOException("Java 安装处理器已退出 (" & process.ExitCode & ")，但输出流未在 5 秒内结束，请重试。" & vbCrLf & tail)
                    If process.ExitCode <> 0 Then Throw New IOException("Java 安装处理器失败 (" & process.ExitCode & ")：" & tail)
                End Using
            Finally
                SyncLock lines
                    accepting = False
                End SyncLock
                RemoveHandler process.OutputDataReceived, output : RemoveHandler process.ErrorDataReceived, errors
                Try
                    process.CancelOutputRead()
                Catch ex As InvalidOperationException
                End Try
                Try
                    process.CancelErrorRead()
                Catch ex As InvalidOperationException
                End Try
            End Try
        End Using
    End Function
    Public Function Quote(value As String) As String
        If value.Contains(vbCr) OrElse value.Contains(vbLf) Then Throw New ArgumentException("进程参数含换行。")
        ' Windows CommandLineToArgvW escaping, including trailing backslashes.
        Return """" & Regex.Replace(value, "(\\*)""", Function(m) New String("\"c, m.Groups(1).Length * 2 + 1) & """") & If(value.EndsWith("\", StringComparison.Ordinal), New String("\"c, value.Reverse().TakeWhile(Function(c) c = "\"c).Count()), "") & """"
    End Function
End Module
