Imports System.IO
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

' Developer verification only. Every installation still enters the production
' supplemental allowlist and uses a new private game root, with no external reuse.
Friend NotInheritable Class SupplementalCandidateVerification
    Private Class Progress
        Implements IProgress(Of TransferProgress)
        Private ReadOnly _cancel As CancellationTokenSource
        Friend Sub New(cancel As CancellationTokenSource)
            _cancel = cancel
        End Sub
        Public Sub Report(value As TransferProgress) Implements IProgress(Of TransferProgress).Report
            If value.Phase = "commit" Then _cancel.Cancel()
        End Sub
    End Class

    Friend Shared Function SelectEntries(ids As String(), normal As IEnumerable(Of VersionEntry), extra As IEnumerable(Of VersionEntry)) As List(Of VersionEntry)
        ValidateIds(ids)
        Dim merged = SupplementalVersions.Merge(normal, extra), selected As New List(Of VersionEntry)
        For Each id In ids
            Dim matches = merged.Where(Function(entry) String.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase)).ToList()
            If matches.Count <> 1 OrElse Not matches(0).IsSupplemental Then Throw New InvalidDataException("未找到唯一的已接入补充版本，或官方清单已优先提供该版本：" & id)
            selected.Add(matches(0).Copy())
        Next
        Return selected
    End Function

    Private Shared Sub ValidateIds(ids As String())
        If ids Is Nothing OrElse ids.Length = 0 OrElse ids.Length > 32 OrElse ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() <> ids.Length Then Throw New ArgumentException("请指定 1 到 32 个不重复的 canonical 版本 ID。")
        For Each id In ids
            SafeFiles.ValidateInstanceName(id)
        Next
    End Sub

    Friend Shared Async Function LiveAsync(output As String, ids As String(), install As Boolean) As Task
        ValidateIds(ids)
        Using timeout As New CancellationTokenSource(TimeSpan.FromMinutes(12))
            Dim policy As New SourcePolicy(), normal = Await New MinecraftInstaller(policy).VersionsAsync(timeout.Token)
            Dim extra = Await SupplementalVersions.LoadAsync(policy, timeout.Token)
            Dim selected = SelectEntries(ids, normal, extra)
            Await VerifyAsync(output, selected, policy, install, timeout.Token)
        End Using
    End Function

    Friend Shared Async Function VerifyAsync(output As String, entries As IEnumerable(Of VersionEntry), policy As SourcePolicy, install As Boolean, token As CancellationToken) As Task(Of JObject)
        Dim selected = entries.Select(Function(entry) entry.Copy()).ToList()
        ValidateIds(selected.Select(Function(entry) entry.Id).ToArray())
        If selected.Any(Function(entry) Not entry.IsSupplemental) Then Throw New InvalidDataException("验证入口只接受已接入的补充版本。")
        output = Path.GetFullPath(output)
        If Directory.Exists(output) AndAlso Directory.EnumerateFileSystemEntries(output).Any() Then Throw New IOException("验证输出目录必须为空，不能复用旧回执或已有游戏目录。")
        Directory.CreateDirectory(output)
        Dim records As New JArray(), report As New JObject From {{"succeeded", False}, {"installationRequested", install}, {"records", records},
            {"gameLaunched", False}, {"pclGuiVerified", False}, {"externalReuseRoots", New JArray()}}
        Try
            For Each entry In selected
                token.ThrowIfCancellationRequested()
                Dim row As New JObject From {{"id", entry.Id}, {"kind", entry.Kind}, {"url", entry.Url}, {"supplementalId", entry.SupplementalId}, {"succeeded", False}}
                records.Add(row)
                Dim source = Await policy.GetJsonAsync(entry.Url, token, entry.Sha1)
                SupplementalVersions.ValidateMetadata(entry, source)
                Dim prepared = Await SupplementalVersions.PrepareLibrariesAsync(entry, source, policy, token)
                Dim basePlan = MinecraftInstaller.BuildBasePlan(prepared, entry.Id), index = DirectCast(prepared("assetIndex"), JObject)
                SupplementalVersions.ValidatePlan(basePlan.Concat({IndexFile(index)}))
                Dim assets = Await policy.GetJsonAsync(CStr(index("url")), token, CStr(index("sha1")))
                Dim assetPlan = MinecraftInstaller.BuildAssetsPlan(assets)
                SupplementalVersions.ValidatePlan(assetPlan)
                row("publishedMetadataSha1") = If(entry.Sha1 Is Nothing, JValue.CreateNull(), New JValue(entry.Sha1))
                row("clientSha1") = prepared.SelectToken("downloads.client.sha1").DeepClone()
                row("assetIndexSha1") = index("sha1").DeepClone()
                row("mappedResources") = assetPlan.Where(Function(file) file.RelativePath.StartsWith("resources/", StringComparison.Ordinal)).Count()
                row("baseFiles") = basePlan.Count : row("assetFiles") = assetPlan.Count
                row("metadataChecksPassed") = True
                ' Parsed views are diagnostic artifacts, not the original network bytes
                ' and not a substitute for an unpublished upstream metadata checksum.
                File.WriteAllText(SafeFiles.UnderRoot(output, entry.Id & ".source.parsed.json"), source.ToString(), New UTF8Encoding(False))
                File.WriteAllText(SafeFiles.UnderRoot(output, entry.Id & ".prepared.parsed.json"), prepared.ToString(), New UTF8Encoding(False))
                If install Then Await InstallAsync(entry, prepared, assets, output, policy, row, token)
                row("succeeded") = True
                Console.WriteLine("PASS supplemental candidate " & entry.Id & If(install, " installation, cancellation, repair and file verification", " metadata and hashed plans"))
            Next
            token.ThrowIfCancellationRequested()
            report("succeeded") = True
            Return report
        Catch ex As Exception
            report("failure") = ex.ToString()
            Throw
        Finally
            File.WriteAllText(SafeFiles.UnderRoot(output, "report.json"), report.ToString(), New UTF8Encoding(False))
        End Try
    End Function

    Private Shared Function IndexFile(index As JObject) As DownloadFile
        SafeFiles.ValidateInstanceName(CStr(index("id")))
        Return New DownloadFile With {.RelativePath = "assets/indexes/" & CStr(index("id")) & ".json", .Url = CStr(index("url")), .Sha1 = CStr(index("sha1")), .Size = If(index("size") Is Nothing, -1, CLng(index("size")))}
    End Function

    Private Shared Function VerifyFiles(root As String, name As String, profile As JObject, assets As JObject) As Integer
        Dim plan = MinecraftInstaller.BuildBasePlan(profile, name)
        plan.Add(IndexFile(DirectCast(profile("assetIndex"), JObject)))
        Dim resourcePlan = MinecraftInstaller.BuildAssetsPlan(assets)
        For Each file In resourcePlan
            If file.RelativePath.StartsWith("resources/", StringComparison.Ordinal) Then file.RelativePath = "versions/" & name & "/" & file.RelativePath
        Next
        plan.AddRange(resourcePlan)
        SupplementalVersions.ValidatePlan(plan)
        For Each file In plan
            If Not SafeFiles.Matches(SafeFiles.UnderRoot(root, file.RelativePath), file) Then Throw New InvalidDataException("已安装文件校验失败：" & file.RelativePath)
        Next
        Return plan.Count
    End Function

    Private Shared Sub ValidateInstalledIdentity(entry As VersionEntry, expected As JObject, actual As JObject)
        If CStr(actual("id")) <> entry.Id OrElse CStr(actual("clientVersion")) <> entry.Id OrElse
            Not JToken.DeepEquals(expected("mainClass"), actual("mainClass")) OrElse Not JToken.DeepEquals(expected("minecraftArguments"), actual("minecraftArguments")) OrElse
            Not JToken.DeepEquals(expected.SelectToken("assetIndex.sha1"), actual.SelectToken("assetIndex.sha1")) OrElse
            Not JToken.DeepEquals(expected.SelectToken("assetIndex.url"), actual.SelectToken("assetIndex.url")) Then Throw New InvalidDataException("检查与安装期间的版本声明发生变化。")
        Dim left = MinecraftInstaller.BuildBasePlan(expected, entry.Id), right = MinecraftInstaller.BuildBasePlan(actual, entry.Id)
        If left.Count <> right.Count OrElse left.Any(Function(file) Not right.Any(Function(other) file.RelativePath = other.RelativePath AndAlso file.Url = other.Url AndAlso file.Sha1 = other.Sha1 AndAlso file.Size = other.Size)) Then Throw New InvalidDataException("检查与安装期间的文件计划发生变化。")
    End Sub

    Private Shared Async Function InstallAsync(entry As VersionEntry, expected As JObject, assets As JObject, output As String, policy As SourcePolicy, row As JObject, token As CancellationToken) As Task
        Dim root = SafeFiles.UnderRoot(output, "game/.minecraft"), installer As New MinecraftInstaller(policy)
        ' No external reuse: the first version exercises fresh network downloads.
        ' Later versions share only files created and checked by this invocation.
        Dim result = Await installer.InstallAsync(entry, root, entry.Id, {}, Nothing, token, connections:=12, isolationMode:=InstanceIsolationMode.Enabled)
        row("initialNetworkBytes") = result.NetworkBytes : row("initialReused") = result.Reused
        Dim jsonPath = Path.Combine(result.InstancePath, entry.Id & ".json"), iniPath = Path.Combine(result.InstancePath, "PCL/Setup.ini")
        Dim installed = JObject.Parse(File.ReadAllText(jsonPath))
        ValidateInstalledIdentity(entry, expected, installed)
        If PclFolders.ReadIniValue(iniPath, "VersionArgumentIndieV2") <> "True" Then Throw New InvalidDataException("新实例未保存开启隔离设置。")
        row("verifiedPlanFiles") = VerifyFiles(root, entry.Id, installed, assets)
        Dim cancelName = "retry-" & entry.Id
        Using cancel = CancellationTokenSource.CreateLinkedTokenSource(token)
            Dim cancelled = False
            Try
                Await installer.InstallAsync(entry, root, cancelName, {root}, New Progress(cancel), cancel.Token, connections:=12, isolationMode:=InstanceIsolationMode.Enabled)
            Catch ex As OperationCanceledException When Not token.IsCancellationRequested
                cancelled = True
            End Try
            If Not cancelled OrElse Directory.Exists(SafeFiles.UnderRoot(root, "versions/" & cancelName)) Then Throw New InvalidOperationException("发布前取消没有保持实例原子性。")
        End Using
        Dim retried = Await installer.InstallAsync(entry, root, cancelName, {root}, Nothing, token, connections:=12, isolationMode:=InstanceIsolationMode.Enabled)
        Dim retryProfile = JObject.Parse(File.ReadAllText(Path.Combine(retried.InstancePath, cancelName & ".json")))
        VerifyFiles(root, cancelName, retryProfile, assets)
        row("cancelThenRetry") = True : row("retryNetworkBytes") = retried.NetworkBytes
        installed("minecraftArguments") = CStr(installed("minecraftArguments")) & " --width 1280"
        File.WriteAllText(jsonPath, installed.ToString(), Encoding.Unicode)
        File.AppendAllText(iniPath, "VerificationUserSetting:preserve" & vbCrLf)
        Dim json = File.ReadAllBytes(jsonPath), ini = File.ReadAllBytes(iniPath), clientPath = Path.Combine(result.InstancePath, entry.Id & ".jar")
        Using client = New FileStream(clientPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            Dim first = client.ReadByte()
            If first < 0 Then Throw New InvalidDataException("客户端为空。")
            client.Position = 0 : client.WriteByte(CByte(first Xor 1))
        End Using
        Dim repaired = Await installer.InstallAsync(entry, root, entry.Id, {root}, Nothing, token, repairExisting:=True, connections:=12, isolationMode:=InstanceIsolationMode.Disabled)
        If Not File.ReadAllBytes(jsonPath).SequenceEqual(json) OrElse Not File.ReadAllBytes(iniPath).SequenceEqual(ini) Then Throw New InvalidDataException("补全改变了已有 JSON 或隔离设置。")
        VerifyFiles(root, entry.Id, JObject.Parse(File.ReadAllText(jsonPath)), assets)
        row("repairNetworkBytes") = repaired.NetworkBytes : row("utf16AndIsolationPreserved") = True : row("corruptClientRepaired") = True
    End Function
End Class
