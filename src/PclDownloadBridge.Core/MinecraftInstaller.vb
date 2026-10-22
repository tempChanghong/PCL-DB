Imports System.IO
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Newtonsoft.Json.Linq

' Main behavioral reference: PCL 5ef81e4 McDownloadClientLoader, McLibListGetWithJson,
' McJsonRuleCheck and McAssetsListGet. Keeps the selected PCL root and shared stores.
Public Class MinecraftInstaller
    Private ReadOnly _policy As SourcePolicy
    Private ReadOnly _pclDirectory As String
    Public Sub New(policy As SourcePolicy, Optional pclDirectory As String = Nothing)
        _policy = policy : _pclDirectory = pclDirectory
    End Sub

    Public Async Function VersionsAsync(token As CancellationToken) As Task(Of List(Of VersionEntry))
        Dim manifest = Await _policy.GetVersionManifestAsync(token).ConfigureAwait(False)
        Return DirectCast(manifest("versions"), JArray).OfType(Of JObject)().Select(Function(v) New VersionEntry With {
            .Id = CStr(v("id")), .Kind = CStr(v("type")), .Url = CStr(v("url")), .Sha1 = CStr(v("sha1")),
            .Released = DateTimeOffset.Parse(CStr(v("releaseTime")), Globalization.CultureInfo.InvariantCulture)}).ToList()
    End Function

    Public Async Function InstallAsync(entry As VersionEntry, root As String, instanceName As String, reuseRoots As IEnumerable(Of String), progress As IProgress(Of TransferProgress), token As CancellationToken, Optional repairExisting As Boolean = False, Optional connections As Integer = 16, Optional limit As Long = 0, Optional components As ComponentSelection = Nothing, Optional isolationMode As InstanceIsolationMode = InstanceIsolationMode.FollowPcl) As Task(Of InstallResult)
        token.ThrowIfCancellationRequested()
        If Not [Enum].IsDefined(GetType(InstanceIsolationMode), isolationMode) Then Throw New ArgumentOutOfRangeException(NameOf(isolationMode))
        InstallPreflight.ValidateDestination(root, instanceName, repairExisting)
        If components IsNot Nothing Then
            components = components.Copy()
            If entry.IsSupplemental AndAlso components.Entries.Count > 0 Then Throw New InvalidOperationException("补充版本目前仅支持原版安装，请取消加载器选择。")
            components.Validate(entry.Id)
            If repairExisting AndAlso components.Entries.Count > 0 Then Throw New InvalidOperationException("组件安装请使用新实例名称；原版校验不会修改加载器实例。")
        End If
        root = Path.GetFullPath(root)
        Directory.CreateDirectory(root)
        Dim cache = SafeFiles.UnderRoot(root, ".vanilla-downloader/install.lock")
        Directory.CreateDirectory(Path.GetDirectoryName(cache))
        Dim instancePath = SafeFiles.UnderRoot(root, "versions/" & instanceName)
        Dim jsonTarget = SafeFiles.UnderRoot(root, "versions/" & instanceName & "/" & instanceName & ".json")
        ' One writer per selected game root protects shared libraries/assets across processes.
        Using rootLock As New FileStream(cache, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
            Report(progress, "准备安装", "读取并校验版本元数据")
            Dim metadata = Await _policy.GetJsonAsync(entry.Url, token, entry.Sha1).ConfigureAwait(False)
            SupplementalVersions.ValidateMetadata(entry, metadata)
            metadata = PreparePclLibraryPaths(metadata)
            Report(progress, "准备安装", "检查组件安装所需的 Java 环境")
            InstallPreflight.VerifyJava(components, metadata)
            Dim existing As JObject = Nothing
            Dim existingBytes As Byte() = Nothing
            If repairExisting Then
                existingBytes = File.ReadAllBytes(jsonTarget)
                Using reader As New StreamReader(New MemoryStream(existingBytes), Encoding.UTF8, True)
                    existing = JObject.Parse(reader.ReadToEnd())
                End Using
            End If
            Dim legacySupplementalIndex = entry.IsSupplemental AndAlso PrepareSupplementalAssetIndex(metadata, existing)
            If repairExisting Then
                If existing("inheritsFrom") IsNot Nothing OrElse CStr(existing("mainClass")) <> CStr(metadata("mainClass")) OrElse CStr(existing.SelectToken("downloads.client.sha1")) <> CStr(metadata.SelectToken("downloads.client.sha1")) OrElse String.IsNullOrEmpty(CStr(metadata.SelectToken("downloads.client.sha1"))) Then Throw New InvalidDataException("只支持校验同版本的原版实例；不会修改加载器或继承版本。")
                If Not JToken.DeepEquals(existing("libraries"), metadata("libraries")) Then Throw New InvalidDataException("现有实例支持库经过修改，请使用新名称安装原版。")
                If existing("clientVersion") IsNot Nothing AndAlso CStr(existing("clientVersion")) <> entry.Id Then Throw New InvalidDataException("现有实例的 PCL 游戏版本与所选版本不一致，请使用新名称安装。")
                If Not JToken.DeepEquals(existing("assetIndex"), metadata("assetIndex")) OrElse Not JToken.DeepEquals(existing("assets"), metadata("assets")) Then Throw New InvalidDataException("现有实例的资源索引经过修改，请使用新名称安装原版。")
                If Not JToken.DeepEquals(existing.SelectToken("logging.client.file"), metadata.SelectToken("logging.client.file")) Then Throw New InvalidDataException("现有实例的日志文件声明经过修改，请使用新名称安装原版。")
            End If
            Dim plan = BuildBasePlan(metadata, instanceName)
            If entry.IsSupplemental Then SupplementalVersions.ValidatePlan(plan)
            Dim index = TryCast(metadata("assetIndex"), JObject)
            If index Is Nothing Then Throw New NotSupportedException("这个早期版本没有资源索引，首版暂不支持安装。")
            Dim indexId = CStr(index("id"))
            If entry.IsSupplemental Then SupplementalVersions.ValidatePlan({ToFile(index, "assets/indexes/index.json", "meta")})
            SafeFiles.ValidateInstanceName(indexId)
            Dim indexRelative = "assets/indexes/" & indexId & ".json"
            If legacySupplementalIndex Then
                Dim indexTarget = SafeFiles.UnderRoot(root, indexRelative)
                If File.Exists(indexTarget) AndAlso Not SafeFiles.Matches(indexTarget, ToFile(index, indexRelative, "meta")) Then Throw New InvalidDataException("旧补充实例的共享资源索引与当前文件不一致。请使用新名称重新安装，以免覆盖其他版本的索引。")
            End If
            Report(progress, "准备安装", "解析资源索引与支持库")
            Dim assets = Await _policy.GetJsonAsync(CStr(index("url")), token, CStr(index("sha1"))).ConfigureAwait(False)
            plan.Add(New DownloadFile With {.RelativePath = indexRelative, .Url = CStr(index("url")), .Size = If(index("size") Is Nothing, -1, CLng(index("size"))), .Sha1 = CStr(index("sha1"))})
            Dim resourceFiles = BuildAssetsPlan(assets)
            Dim mappedFiles = resourceFiles.Where(Function(item) item.RelativePath.StartsWith("resources/", StringComparison.Ordinal)).ToList()
            Dim withComponents = components IsNot Nothing AndAlso components.Entries.Count > 0
            Dim modable = ComponentInstaller.IsModable(components)
            Dim componentMods = ComponentInstaller.ModFiles(components, entry.Id)
            Dim isolation As PclIsolationPlan = Nothing
            If mappedFiles.Count > 0 OrElse (Not repairExisting AndAlso (withComponents OrElse isolationMode <> InstanceIsolationMode.FollowPcl)) Then
                isolation = PclIsolationPlan.Resolve(root, instanceName, entry, metadata, _pclDirectory, repairExisting, withComponents, isolationMode, modable)
                If isolation.Isolated Then
                    For Each item In mappedFiles
                        item.RelativePath = "versions/" & instanceName & "/" & item.RelativePath
                    Next
                End If
            End If
            Dim mappedTargets As New HashSet(Of String)(mappedFiles.Select(Function(item) SafeFiles.UnderRoot(root, item.RelativePath)), StringComparer.OrdinalIgnoreCase)
            If plan.Concat(resourceFiles.Except(mappedFiles)).Any(Function(item) mappedTargets.Contains(SafeFiles.UnderRoot(root, item.RelativePath))) Then Throw New InvalidDataException("旧资源映射与其他下载目标冲突。")
            plan.AddRange(resourceFiles)
            If entry.IsSupplemental Then SupplementalVersions.ValidatePlan(plan)
            ' Merge and validate every destination across all branches before content
            ' downloads start, so duplicate checks cannot disappear between stages.
            plan = DownloadPlan.Merge(plan, root)
            mappedFiles = plan.Where(Function(item) mappedTargets.Contains(SafeFiles.UnderRoot(root, item.RelativePath))).ToList()
            If mappedFiles.Count > 0 Then
                LegacyResourcePublication.ValidateLayout(root, plan)
                LegacyResourcePublication.Validate(root, mappedFiles, repairExisting)
            End If
            Dim sharedComponents = withComponents AndAlso Not isolation.Isolated
            If sharedComponents Then
                SharedModsPublication.Preflight(root, componentMods, modable)
                LegacyResourcePublication.ValidateLayout(root, plan.Concat(componentMods))
            End If
            Dim sharedMods As New List(Of DownloadFile)
            Dim stageRelative = ".vanilla-downloader/staging/" & Guid.NewGuid().ToString("N") & "/payload"
            Dim stage = SafeFiles.UnderRoot(root, stageRelative)
            Directory.CreateDirectory(stage)
            Try
              Using publication As New LegacyResourcePublication(root, stage, instanceName, mappedFiles, isolation IsNot Nothing AndAlso isolation.Isolated, repairExisting),
                    modsPublication As New LegacyResourcePublication(root, stage, instanceName, sharedMods, False, False, "Mods")
                Dim newSettings = isolation IsNot Nothing AndAlso Not repairExisting
                If newSettings Then isolation.WriteNewSettings(stage)
                ' Client JAR stays outside versions/ until the entire install succeeds.
                Dim client = plan.Single(Function(f) f.RelativePath = "versions/" & instanceName & "/" & instanceName & ".jar")
                Dim supportFiles = plan.Where(Function(f) f IsNot client AndAlso Not mappedFiles.Contains(f) AndAlso Not f.RelativePath.StartsWith("assets/objects/", StringComparison.Ordinal) AndAlso
                    Not f.RelativePath.StartsWith("assets/virtual/", StringComparison.Ordinal) AndAlso Not f.RelativePath.StartsWith("resources/", StringComparison.Ordinal) AndAlso f.RelativePath <> indexRelative).ToList()
                Dim assetFiles = plan.Where(Function(f) f IsNot client AndAlso Not supportFiles.Contains(f) AndAlso Not mappedFiles.Contains(f)).ToList()
                Dim networkBytes As Long
                Dim reused As Integer
                Dim splits As Integer
                Using budget As New DownloadBudget(connections, limit), linked = CancellationTokenSource.CreateLinkedTokenSource(token)
                    Dim reporting As New InstallationProgress(progress, budget, supportFiles.Count + 1, assetFiles.Count + mappedFiles.Count)
                    Dim stagedClient As New DownloadFile With {.RelativePath = instanceName & ".jar", .Url = client.Url, .Size = client.Size, .Sha1 = client.Sha1, .Category = client.Category}
                    Using clientEngine As New DownloadEngine(_policy, reporting.Branch("client"), reuseRoots, connections, root, True, budget),
                          supportEngine As New DownloadEngine(_policy, reporting.Branch("libraries"), reuseRoots, connections, root, budget:=budget),
                          assetsEngine As New DownloadEngine(_policy, reporting.Branch("assets"), reuseRoots, connections, root, budget:=budget),
                          mappedEngine As New DownloadEngine(_policy, reporting.Branch("mapped"), reuseRoots.Concat({If(isolation IsNot Nothing AndAlso isolation.Isolated, instancePath, root)}), connections, root, budget:=budget)
                        Dim extension As ComponentInstaller = Nothing
                        ' PCL's nonblocking library/resource LoaderCombos share ModNet.
                        ' Components need the verified client/support branch, not all assets.
                        Dim supportWork As Func(Of Task) = Async Function()
                            Dim clients = CancelPeersAsync(Function() clientEngine.DownloadAsync(stage, {stagedClient}, linked.Token), linked, Sub() reporting.Failed("files"))
                            Dim libraries = CancelPeersAsync(Function() supportEngine.DownloadAsync(root, supportFiles, linked.Token), linked, Sub() reporting.Failed("files"))
                            Await Task.WhenAll(clients, libraries).ConfigureAwait(False)
                            If components IsNot Nothing AndAlso components.Entries.Count > 0 Then
                                extension = New ComponentInstaller(_policy, components, root, reuseRoots, reporting.Branch("components"), connections, limit, budget)
                                Try
                                    metadata = Await extension.ApplyAsync(metadata, entry.Id, stage, instanceName, linked.Token, isolation.Isolated).ConfigureAwait(False)
                                Catch ex As Exception When Not TypeOf ex Is OperationCanceledException
                                    reporting.Failed("components")
                                    Throw
                                End Try
                                reporting.ComponentsComplete()
                            End If
                        End Function
                        Dim assetWork As Func(Of Task) = Async Function()
                            Await assetsEngine.DownloadAsync(root, assetFiles, linked.Token).ConfigureAwait(False)
                            If mappedFiles.Count > 0 Then Await mappedEngine.DownloadAsync(stage, publication.StagedFiles(), linked.Token).ConfigureAwait(False)
                        End Function
                        Dim branches = {CancelPeersAsync(supportWork, linked), CancelPeersAsync(assetWork, linked, Sub() reporting.Failed("files"))}
                        Try
                            Await Task.WhenAll(branches).ConfigureAwait(False)
                        Catch
                            token.ThrowIfCancellationRequested()
                            InstallLog.Failure(root, "安装失败", branches.Select(Function(task) CType(task.Exception, Exception)))
                            Dim failure = branches.Where(Function(t) t.Exception IsNot Nothing).SelectMany(Function(t) t.Exception.Flatten().InnerExceptions).FirstOrDefault(Function(e) Not TypeOf e Is OperationCanceledException)
                            If failure IsNot Nothing Then Throw failure
                            Throw
                        End Try
                        networkBytes = clientEngine.NetworkBytes + supportEngine.NetworkBytes + assetsEngine.NetworkBytes + mappedEngine.NetworkBytes
                        reused = clientEngine.Reused + supportEngine.Reused + assetsEngine.Reused + mappedEngine.Reused
                        splits = clientEngine.DynamicSplits + supportEngine.DynamicSplits + assetsEngine.DynamicSplits + mappedEngine.DynamicSplits
                        If extension IsNot Nothing Then
                            networkBytes += extension.NetworkBytes : reused += extension.Reused : splits += extension.DynamicSplits
                            plan.AddRange(Enumerable.Repeat(New DownloadFile(), extension.FileCount))
                        End If
                    End Using
                End Using
                If sharedComponents Then sharedMods.AddRange(SharedModsPublication.CheckedFiles(stage, componentMods, token))
                token.ThrowIfCancellationRequested()
                Report(progress, "完成安装", "写入版本信息")
                token.ThrowIfCancellationRequested()
                If repairExisting Then
                    ' Recheck the exact metadata accepted above, then keep readers allowed
                    ' while blocking external edits through the final client replacement.
                    Using jsonLock As New FileStream(SafeFiles.UnderRoot(root, "versions/" & instanceName & "/" & instanceName & ".json"), FileMode.Open, FileAccess.Read, FileShare.Read)
                        Using current As New MemoryStream()
                            If jsonLock.Length <> existingBytes.LongLength Then Throw New InvalidDataException("待补全实例的版本 JSON 在校验期间发生变化，请确认后重试。")
                            jsonLock.CopyTo(current)
                            If Not existingBytes.SequenceEqual(current.ToArray()) Then Throw New InvalidDataException("待补全实例的版本 JSON 在校验期间发生变化，请确认后重试。")
                        End Using
                      Using isolationGuard = If(isolation Is Nothing, Nothing, isolation.LockForRepair())
                        publication.Publish(token, isolationGuard)
                        Dim clientTarget = SafeFiles.UnderRoot(root, client.RelativePath)
                        Dim needsReplacement = Not SafeFiles.Matches(clientTarget, client)
                        token.ThrowIfCancellationRequested()
                        isolationGuard?.Verify()
                        If needsReplacement Then SafeFiles.CommitFile(Path.Combine(stage, instanceName & ".jar"), clientTarget)
                      End Using
                    End Using
                Else
                    metadata("id") = instanceName
                    metadata("clientVersion") = entry.Id ' Same PCL convention as McDownloadClientLoader.
                    File.WriteAllText(Path.Combine(stage, instanceName & ".json"), metadata.ToString(), New UTF8Encoding(False))
                    ' Same-volume rename publishes the entire payload at once. If another
                    ' application created this instance, failure leaves stage owned by our
                    ' Finally cleanup instead of stranding it in an intermediate folder.
                    token.ThrowIfCancellationRequested()
                    Directory.CreateDirectory(Path.GetDirectoryName(instancePath))
                    token.ThrowIfCancellationRequested()
                    publication.Publish(token)
                    modsPublication.Publish(token)
                    If sharedComponents Then SharedModsPublication.FinishFolders(root, stage, modable)
                    token.ThrowIfCancellationRequested()
                    Directory.Move(stage, instancePath)
                End If
                publication.Complete()
                modsPublication.Complete()
                ' After the filesystem commit, return its real result even if cancellation
                ' arrives during optional cache cleanup; never undo a published instance.
                VerifiedComponentCache.ReleasePublishedClient(root, client)
                Return New InstallResult With {.InstancePath = instancePath, .FileCount = plan.Count + 1 + If(newSettings, 1, 0), .NetworkBytes = networkBytes, .Reused = reused, .DynamicSplits = splits}
              End Using
            Finally
                ' Preserve transfer caches and existing versions. A locked private payload
                ' is logged separately and cannot replace cancellation or the real error.
                InstallCleanup.DeleteOwnedDirectory(root, stageRelative)
            End Try
        End Using
    End Function

    Private Shared Async Function CancelPeersAsync(operation As Func(Of Task), linked As CancellationTokenSource, Optional failed As Action = Nothing) As Task
        Try
            Await operation().ConfigureAwait(False)
        Catch ex As Exception
            linked.Cancel()
            If Not TypeOf ex Is OperationCanceledException Then failed?.Invoke()
            Throw
        End Try
    End Function

    Public Shared Function BuildBasePlan(metadata As JObject, instanceName As String) As List(Of DownloadFile)
        Dim result As New List(Of DownloadFile)
        Dim client = TryCast(metadata.SelectToken("downloads.client"), JObject)
        If client Is Nothing Then Throw New InvalidDataException("缺少客户端 JAR 信息。")
        result.Add(ToFile(client, "versions/" & instanceName & "/" & instanceName & ".jar", "meta"))
        Dim selected As New Dictionary(Of String, List(Of DownloadFile))(StringComparer.Ordinal)
        For Each library In DirectCast(metadata("libraries"), JArray).OfType(Of JObject)()
            If Not RulesAllow(library("rules")) Then Continue For
            Dim artifact = TryCast(library.SelectToken("downloads.artifact"), JObject)
            If artifact IsNot Nothing Then
                PclLibrarySelection.Add(selected, library, LibraryFile(library, artifact), False)
            ElseIf library("natives") Is Nothing OrElse library("natives").Type = JTokenType.Null Then
                PclLibrarySelection.Add(selected, library, LibraryFile(library, New JObject()), False)
            End If
            Dim nativeName = CStr(library.SelectToken("natives.windows"))
            If nativeName IsNot Nothing Then
                nativeName = nativeName.Replace("${arch}", If(Environment.Is64BitOperatingSystem, "64", "32"))
                Dim native = TryCast(library("downloads")?("classifiers")?(nativeName), JObject)
                PclLibrarySelection.Add(selected, library, LibraryFile(library, If(native, New JObject()), nativeName), True)
            End If
        Next
        result.AddRange(selected.Values.SelectMany(Function(items) items))
        Dim logging = TryCast(metadata.SelectToken("logging.client.file"), JObject)
        If logging IsNot Nothing Then
            ValidateResourceName(CStr(logging("id")), "日志文件")
            result.Add(ToFile(logging, "assets/log_configs/" & CStr(logging("id")), "meta"))
        End If
        Return DownloadPlan.Merge(result)
    End Function

    Public Shared Function BuildAssetsPlan(index As JObject) As List(Of DownloadFile)
        Dim result As New List(Of DownloadFile)
        Dim objects = If(index Is Nothing, Nothing, TryCast(index("objects"), JObject))
        If objects Is Nothing Then Throw New InvalidDataException("资源索引缺少有效的 objects 清单。")
        For Each item In objects.Properties()
            Dim entry = TryCast(item.Value, JObject)
            If entry Is Nothing Then Throw New InvalidDataException("资源条目无效：" & item.Name)
            Dim hashToken = entry("hash"), sizeToken = entry("size")
            If hashToken Is Nothing OrElse hashToken.Type <> JTokenType.String OrElse Not Regex.IsMatch(CStr(hashToken), "\A[0-9a-fA-F]{40}\z") Then Throw New InvalidDataException("资源哈希无效：" & item.Name)
            ' Normalize object addresses only; retain the verified index and logical names.
            Dim hash = CStr(hashToken).ToLowerInvariant()
            Dim size As Long
            If sizeToken Is Nothing OrElse sizeToken.Type <> JTokenType.Integer OrElse
                Not Long.TryParse(sizeToken.ToString(Newtonsoft.Json.Formatting.None), Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture, size) Then Throw New InvalidDataException("资源大小无效：" & item.Name)
            If CBool(If(index("virtual"), New JValue(False))) OrElse CBool(If(index("map_to_resources"), New JValue(False))) Then ValidateResourceName(item.Name)
            Dim source = "https://resources.download.minecraft.net/" & hash.Substring(0, 2) & "/" & hash
            Dim file = New DownloadFile With {.RelativePath = "assets/objects/" & hash.Substring(0, 2) & "/" & hash, .Url = source, .Size = size, .Sha1 = hash, .Category = "asset"}
            result.Add(file)
            ' PCL McAssetsListGet: old virtual/resource mappings. Also retain the object
            ' store, making future reuse and modern launcher checks straightforward.
            If CBool(If(index("virtual"), New JValue(False))) Then result.Add(New DownloadFile With {.RelativePath = "assets/virtual/legacy/" & item.Name, .Url = source, .Size = file.Size, .Sha1 = hash, .Category = "asset"})
            If CBool(If(index("map_to_resources"), New JValue(False))) Then result.Add(New DownloadFile With {.RelativePath = "resources/" & item.Name, .Url = source, .Size = file.Size, .Sha1 = hash, .Category = "asset"})
        Next
        Return DownloadPlan.Merge(result)
    End Function

    Friend Shared Sub ValidateResourceName(name As String, Optional description As String = "旧资源映射")
        If name Is Nothing Then Throw New InvalidDataException(description & "名称为空。")
        For Each part In name.Replace("\"c, "/"c).Split("/"c)
            If String.IsNullOrEmpty(part) OrElse part = "." OrElse part = ".." OrElse part.Length > 255 OrElse part.EndsWith(".", StringComparison.Ordinal) OrElse part.EndsWith(" ", StringComparison.Ordinal) OrElse
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 OrElse Regex.IsMatch(part, "\A(?:CON|PRN|AUX|NUL|CLOCK\$|CONIN\$|CONOUT\$|COM[0-9]|LPT[0-9])(?:\.|\z)", RegexOptions.IgnoreCase Or RegexOptions.CultureInvariant) Then Throw New InvalidDataException(description & "名称无效：" & name)
        Next
    End Sub

    Public Shared Function RulesAllow(rules As JToken) As Boolean
        If rules Is Nothing OrElse rules.Type = JTokenType.Null Then Return True
        Dim required = False
        For Each rule In rules
            Dim matches = True
            Dim os = rule("os")
            If os IsNot Nothing Then
                If os("name") IsNot Nothing Then matches = CStr(os("name")) = "windows"
                If os("version") IsNot Nothing Then matches = matches AndAlso Regex.IsMatch(Environment.OSVersion.Version.ToString(), CStr(os("version")))
                If os("arch") IsNot Nothing Then matches = matches AndAlso Regex.IsMatch(If(Environment.Is64BitOperatingSystem, "amd64", "x86"), CStr(os("arch")))
            End If
            Dim features = TryCast(rule("features"), JObject)
            If features IsNot Nothing Then
                For Each feature In features.Properties()
                    ' No demo/custom-resolution/quick-play launch features are enabled.
                    If CBool(feature.Value) Then matches = False
                Next
            End If
            If matches Then required = CStr(rule("action")) = "allow"
        Next
        Return required
    End Function

    Public Shared Function MavenPath(coordinate As String, Optional classifier As String = Nothing) As String
        Dim extension = "jar"
        Dim parts = coordinate.Split("@"c)
        If parts.Length > 2 Then Throw New InvalidDataException("支持库扩展名无效：" & coordinate)
        If parts.Length = 2 Then extension = parts(1)
        Dim elements = parts(0).Split(":"c)
        If elements.Length < 3 OrElse elements.Length > 4 OrElse Not Regex.IsMatch(elements(0), "\A[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+)*\z") OrElse Not Regex.IsMatch(extension, "\A[A-Za-z0-9]+\z") Then Throw New InvalidDataException("支持库坐标无效：" & coordinate)
        For Each component In elements.Skip(1)
            If Not Regex.IsMatch(component, "\A[A-Za-z0-9_.+\-]+\z") OrElse component = "." OrElse component = ".." Then Throw New InvalidDataException("支持库坐标含无效路径：" & coordinate)
        Next
        If classifier Is Nothing AndAlso elements.Length > 3 Then classifier = elements(3)
        Return elements(0).Replace("."c, "/"c) & "/" & elements(1) & "/" & elements(2) & "/" & elements(1) & "-" & elements(2) & If(classifier Is Nothing, "", "-" & classifier) & "." & extension
    End Function

    ' PCL McLibListGetWithJson uses Maven paths when an artifact omits its path.
    ' Destination and source are independent: a custom local path does not change
    ' the Maven coordinate used to resolve a missing URL in the declared repository.
    Private Shared Function LibraryFile(libraryInfo As JObject, data As JObject, Optional classifier As String = Nothing) As DownloadFile
        Dim relative = CStr(data("path"))
        If relative Is Nothing Then relative = MavenPath(CStr(libraryInfo("name")), classifier)
        If String.IsNullOrWhiteSpace(relative) Then Throw New InvalidDataException("支持库下载路径为空：" & CStr(libraryInfo("name")))
        ValidateResourceName(relative, "支持库")
        Dim file = ToFile(data, "libraries/" & relative, "library")
        If file.Url Is Nothing Then
            Dim repo = If(CStr(libraryInfo("url")), "https://libraries.minecraft.net/")
            file.Url = repo.TrimEnd("/"c) & "/" & MavenPath(CStr(libraryInfo("name")), classifier)
        End If
        Return file
    End Function

    ' Pinned PCL McLibGet always appends .jar and does not parse @extension.
    ' Its artifact branch does honor an explicit path. Materialize that path only
    ' for extended coordinates, on a clone, before saving or comparing for repair.
    Friend Shared Function PreparePclLibraryPaths(profile As JObject) As JObject
        Dim result = DirectCast(profile.DeepClone(), JObject)
        For Each libraryInfo In DirectCast(result("libraries"), JArray).OfType(Of JObject)()
            Dim coordinate = CStr(libraryInfo("name"))
            If coordinate Is Nothing OrElse Not coordinate.Contains("@") OrElse Not RulesAllow(libraryInfo("rules")) Then Continue For
            CompletePclArtifactPath(libraryInfo, TryCast(libraryInfo.SelectToken("downloads.artifact"), JObject), Nothing)
            Dim nativeName = CStr(libraryInfo.SelectToken("natives.windows"))
            If nativeName Is Nothing Then Continue For
            nativeName = nativeName.Replace("${arch}", If(Environment.Is64BitOperatingSystem, "64", "32"))
            Dim native = TryCast(libraryInfo("downloads")?("classifiers")?(nativeName), JObject)
            If native Is Nothing Then Continue For
            CompletePclArtifactPath(libraryInfo, native, nativeName)
            DirectCast(libraryInfo("natives"), JObject)("windows") = nativeName
        Next
        Return result
    End Function

    ' PCL McAssetsGetIndexName and ${assets_index_name} prefer assetIndex.id.
    ' Supplemental releases can reuse one upstream ID for different index contents;
    ' keep their content-addressed names separate from the ordinary shared index.
    Private Shared Function PrepareSupplementalAssetIndex(metadata As JObject, existing As JObject) As Boolean
        Dim index = DirectCast(metadata("assetIndex"), JObject)
        SafeFiles.ValidateInstanceName(CStr(index("id")))
        Dim isolated = "pcldb-" & CStr(index("sha1")).ToLowerInvariant()
        ' Repair preserves existing JSON bytes. Keep legacy names, but the caller
        ' refuses to overwrite an existing index that no longer matches this version.
        If existing IsNot Nothing AndAlso CStr(existing.SelectToken("assetIndex.id")) <> isolated Then Return True
        index("id") = isolated
        Return False
    End Function

    Private Shared Sub CompletePclArtifactPath(libraryInfo As JObject, data As JObject, classifier As String)
        If data Is Nothing Then Return
        Dim item = LibraryFile(libraryInfo, data, classifier)
        If data("path") Is Nothing OrElse data("path").Type = JTokenType.Null Then data("path") = item.RelativePath.Substring("libraries/".Length)
        If data("url") Is Nothing OrElse data("url").Type = JTokenType.Null Then data("url") = item.Url
        ' PCL reads size with ToString even when it has an explicit path.
        If data("size") Is Nothing Then data("size") = -1
    End Sub

    Private Shared Function ToFile(data As JObject, relative As String, category As String) As DownloadFile
        Return New DownloadFile With {.RelativePath = relative, .Url = CStr(data("url")), .Size = If(data("size") Is Nothing OrElse data("size").Type = JTokenType.Null, -1, CLng(data("size"))), .Sha1 = CStr(data("sha1")), .Category = category}
    End Function

    Private Shared Sub Report(progress As IProgress(Of TransferProgress), stage As String, detail As String)
        progress?.Report(New TransferProgress With {.Phase = If(stage = "完成安装", "commit", "metadata"), .Stage = stage, .Detail = detail})
    End Sub
End Class
