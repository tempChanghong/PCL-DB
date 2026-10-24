Imports System.IO
Imports System.Text
Imports System.Threading
Imports System.Text.RegularExpressions
Imports Newtonsoft.Json.Linq

' HMCL HMCLGameRepository.computeRunDirectory / LegacyGameSettingsMigrator and
' CE McInstance.PathIndie. Existing instance bytes are never initialized by repair.
Friend NotInheritable Class NativeInstanceIsolation
    Implements IInstanceIsolationPlan
    Private ReadOnly _target As LauncherInstallTarget
    Private ReadOnly _root As String, _name As String, _instance As String
    Private ReadOnly _inputs As New InstanceConfigurationInputs()
    Private ReadOnly _written As New Dictionary(Of String, Byte())(StringComparer.OrdinalIgnoreCase)
    Private _folderSignal As Boolean?
    Private _hmclModern As Boolean
    Private _legacyGlobal As JObject
    Friend ReadOnly Property Isolated As Boolean Implements IInstanceIsolationPlan.Isolated
    Friend ReadOnly Property RunDirectory As String
    Friend ReadOnly Property WrittenPaths As IEnumerable(Of String)
        Get
            Return _written.Keys
        End Get
    End Property
    Private Sub New(target As LauncherInstallTarget, name As String, entry As VersionEntry, metadata As JObject, repair As Boolean, components As Boolean, mode As InstanceIsolationMode, modable As Boolean, strategy As Integer?)
        _target = target : _root = target.Root : _name = name
        _instance = SafeFiles.UnderRoot(_root, "versions/" & name)
        If strategy.HasValue AndAlso (strategy.Value < 0 OrElse strategy.Value > 4) Then Throw New ArgumentOutOfRangeException(NameOf(strategy))
        Dim choice As Boolean?, custom As String = Nothing
        If target.Kind = ExternalLauncherKind.PclCe Then
            Dim local = CeReader(If(target.ConfigPath, Path.Combine(target.Workspace, "PCL/config.v1.yml")))
            If repair Then
                Dim read = CeReader(Path.Combine(_instance, "PCL/config.v1.yml")), value = read("VersionArgumentIndieV2")
                If value IsNot Nothing Then
                    choice = ParseBool(value)
                Else
                    Dim old = ParseInt(read("VersionArgumentIndie"), -1, -1, 2)
                    If old > 0 Then
                        choice = old = 1
                    Else
                        _folderSignal = HasUserData()
                        If _folderSignal.Value Then choice = True
                    End If
                End If
            End If
            If Not choice.HasValue Then
                Dim nonRelease = entry.IsSupplemental OrElse CStr(metadata("type")) = "old_alpha" OrElse CStr(metadata("type")) = "old_beta" OrElse
                    (Not components AndAlso Not String.Equals(CStr(metadata("type")), "release", StringComparison.OrdinalIgnoreCase))
                choice = If(Not repair AndAlso mode <> InstanceIsolationMode.FollowPcl, mode = InstanceIsolationMode.Enabled,
                    StrategyValue(If(Not repair AndAlso strategy.HasValue, strategy.Value, ParseInt(local("LaunchArgumentIndieV2"), 4, 0, 4)), modable, nonRelease))
            End If
        ElseIf target.Kind = ExternalLauncherKind.Hmcl Then
            Dim primary = target.ConfigPath
            Dim newDirectories As JObject = Nothing
            If primary Is Nothing Then newDirectories = ReadJson(Path.Combine(target.Workspace, ".hmcl/config/game-directories.json"))
            _hmclModern = If(primary IsNot Nothing, Path.GetFileName(primary).Equals("game-directories.json", StringComparison.OrdinalIgnoreCase), newDirectories IsNot Nothing)
            Dim globalSettings As JObject = Nothing, instanceSettings As JObject = Nothing
            If _hmclModern Then
                Dim configDirectory = If(primary Is Nothing, Path.Combine(target.Workspace, ".hmcl/config"), Path.GetDirectoryName(primary))
                If primary IsNot Nothing Then newDirectories = ReadJson(primary)
                Dim launcher = ReadJson(Path.Combine(configDirectory, "launcher-settings.json"))
                Dim presets = ReadJson(Path.Combine(configDirectory, "game-settings.json"))
                CheckSchema(presets, "game-settings")
                If repair Then
                    instanceSettings = ReadJson(Path.Combine(_instance, ".hmcl/config/instance-game-settings.json"))
                    CheckSchema(instanceSettings, "instance-game-settings")
                End If
                Dim values = TryCast(presets?("presets"), JArray)
                If presets?("presets") IsNot Nothing AndAlso values Is Nothing Then Throw New InvalidDataException("HMCL 预设列表格式无效。")
                If values IsNot Nothing AndAlso values.OfType(Of JObject)().Select(Function(item) Scalar(item("id"))).GroupBy(Function(id) id).Any(Function(group) group.Count() > 1) Then Throw New InvalidDataException("HMCL 预设标识不唯一。")
                Dim parent = Scalar(instanceSettings?("parent")), defaultParent = Scalar(launcher?("defaultGameSettingsPreset"))
                Dim legacyParent As String = Nothing
                If repair AndAlso instanceSettings Is Nothing Then
                    Dim sharedPath = If(target.SharedConfigPath, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".hmcl/config/user-game-directories.json"))
                    Dim sharedDirectories = ReadJson(sharedPath), matchedParent = False
                    Dim directorySources = {newDirectories, sharedDirectories}, seenIds As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
                    For Each source In directorySources
                      Dim directories = TryCast(source?("directories"), JArray)
                      If directories IsNot Nothing Then
                        For Each item In directories.OfType(Of JObject)()
                            Dim id = Scalar(item("id"))
                            If id IsNot Nothing AndAlso Not seenIds.Add(id) Then Continue For
                            Dim directory = Scalar(item("path"))
                            If directory Is Nothing OrElse Not Same(LauncherDirectoryReader.AbsolutePath(directory, target.Workspace), _root) Then Continue For
                            Dim value = Scalar(item("legacyGameSettings"))
                            If matchedParent AndAlso legacyParent <> value Then Throw New InvalidDataException("HMCL 同目录的历史设置来源不唯一。")
                            legacyParent = value : matchedParent = True
                        Next
                      End If
                    Next
                    parent = legacyParent
                End If
                Dim legacyPresetResolved = False
                If values IsNot Nothing Then
                    globalSettings = values.OfType(Of JObject)().FirstOrDefault(Function(item) Scalar(item("id")) = parent AndAlso parent IsNot Nothing)
                    legacyPresetResolved = legacyParent IsNot Nothing AndAlso globalSettings IsNot Nothing
                    If globalSettings Is Nothing Then globalSettings = values.OfType(Of JObject)().FirstOrDefault(Function(item) Scalar(item("id")) = defaultParent AndAlso defaultParent IsNot Nothing)
                    If globalSettings Is Nothing Then globalSettings = values.OfType(Of JObject)().FirstOrDefault()
                End If
                If repair Then
                    If instanceSettings IsNot Nothing Then
                        Dim overrideNames = TryCast(instanceSettings("overrideProperties"), JArray)
                        If instanceSettings("overrideProperties") IsNot Nothing AndAlso overrideNames Is Nothing Then Throw New InvalidDataException("HMCL 实例覆盖属性格式无效。")
                        If overrideNames IsNot Nothing AndAlso overrideNames.Values(Of String)().Contains("runningDirectory", StringComparer.Ordinal) Then
                            custom = Scalar(instanceSettings("runningDirectory"))
                            choice = String.IsNullOrWhiteSpace(custom)
                        End If
                    Else
                        Dim old = ReadJson(Path.Combine(_instance, "hmclversion.cfg"))
                        If old IsNot Nothing AndAlso _inputs.Read(Path.Combine(_instance, ".hmcl/state/instance-game-settings.migration-receipt.json")) IsNot Nothing Then Throw New InvalidDataException("HMCL 旧配置迁移状态不明确，请先用 HMCL 保存实例设置后重试。")
                        If old IsNot Nothing AndAlso Not BooleanValue(old("usesGlobal"), False) Then
                            Dim oldMode = HmclMode(old("gameDirType"))
                            choice = oldMode = 1
                            If oldMode = 2 Then
                                custom = Scalar(old("gameDir")) : choice = String.IsNullOrWhiteSpace(custom)
                            End If
                        ElseIf old IsNot Nothing AndAlso legacyPresetResolved AndAlso Scalar(globalSettings?("defaultIsolationType")) = "ALWAYS" AndAlso String.IsNullOrWhiteSpace(Scalar(globalSettings?("runningDirectory"))) Then
                            choice = True
                        End If
                    End If
                    ' A modern instance inherits the preset running directory when
                    ' it does not override that property; installation defaults do
                    ' not retroactively isolate already installed vanilla versions.
                    If Not choice.HasValue Then
                        choice = False : custom = Scalar(globalSettings?("runningDirectory"))
                    End If
                Else
                    Dim policy = If(Scalar(globalSettings?("defaultIsolationType")), "MODDED")
                    If strategy.HasValue OrElse mode <> InstanceIsolationMode.FollowPcl Then policy = "NEVER"
                    Select Case policy
                        Case "ALWAYS" : choice = True
                        Case "NEVER" : choice = False
                        Case "MODDED" : choice = modable
                        Case Else : Throw New InvalidDataException("HMCL 默认隔离策略无法识别。")
                    End Select
                    If Not choice.Value Then custom = Scalar(globalSettings?("runningDirectory"))
                End If
            Else
                Dim legacy = If(primary, FirstLegacyHmcl())
                Dim config = If(legacy Is Nothing, Nothing, ReadJson(legacy))
                Dim profiles = TryCast(config?("configurations"), JObject)
                If config IsNot Nothing AndAlso profiles Is Nothing Then Throw New InvalidDataException("HMCL 旧配置格式无法识别。")
                If profiles IsNot Nothing Then
                    Dim matchedProfile = False
                    For Each profile In profiles.Properties().Select(Function(item) TryCast(item.Value, JObject)).Where(Function(item) item IsNot Nothing)
                        Dim directory = Scalar(profile("gameDir"))
                        If directory Is Nothing OrElse Not Same(LauncherDirectoryReader.AbsolutePath(directory, target.Workspace), _root) Then Continue For
                        Dim value = TryCast(profile("global"), JObject)
                        If matchedProfile AndAlso Not JToken.DeepEquals(globalSettings, value) Then Throw New InvalidDataException("多个 HMCL 配置使用同一目录但隔离规则不同，请明确选择配置后重试。")
                        globalSettings = value : matchedProfile = True
                    Next
                End If
                _legacyGlobal = If(globalSettings, New JObject())
                Dim effective = globalSettings
                If repair Then
                    instanceSettings = ReadJson(Path.Combine(_instance, "hmclversion.cfg"))
                    If instanceSettings IsNot Nothing AndAlso Not BooleanValue(instanceSettings("usesGlobal"), False) Then effective = instanceSettings
                End If
                Dim directoryType = HmclMode(effective?("gameDirType"))
                choice = directoryType = 1
                If directoryType = 2 Then custom = Scalar(effective?("gameDir"))
            End If
            If Not repair AndAlso strategy.HasValue Then
                choice = StrategyValue(strategy.Value, modable, entry.IsSupplemental OrElse Not String.Equals(CStr(metadata("type")), "release", StringComparison.OrdinalIgnoreCase))
                custom = Nothing
            End If
            If repair AndAlso _inputs.Read(Path.Combine(_instance, "modpack.cfg")) IsNot Nothing Then choice = True : custom = Nothing
        Else
            Throw New ArgumentException("未知的原生隔离目标。")
        End If
        If Not repair AndAlso mode <> InstanceIsolationMode.FollowPcl Then choice = mode = InstanceIsolationMode.Enabled : custom = Nothing
        Dim run = If(choice.GetValueOrDefault(), _instance, If(String.IsNullOrWhiteSpace(custom), _root, LauncherDirectoryReader.AbsolutePath(custom, target.Workspace)))
        RunDirectory = run : Isolated = Same(run, _instance)
    End Sub

    Friend Shared Function Resolve(target As LauncherInstallTarget, name As String, entry As VersionEntry, metadata As JObject, repair As Boolean, components As Boolean, mode As InstanceIsolationMode, modable As Boolean, strategy As Integer?) As NativeInstanceIsolation
        Return New NativeInstanceIsolation(target, name, entry, metadata, repair, components, mode, modable, strategy)
    End Function
    Friend Sub ValidateDataLocation(needsRunFiles As Boolean)
        If needsRunFiles AndAlso Not Same(RunDirectory, _root) AndAlso Not Same(RunDirectory, _instance) Then Throw New NotSupportedException("此实例使用自定义运行目录；为保留原设置，暂不向该目录自动写入 Mods 或旧资源。")
    End Sub
    Private Function FirstLegacyHmcl() As String
        For Each relative In {".hmcl/hmcl.json", "hmcl.json", ".hmcl.json"}
            Dim full = Path.Combine(_target.Workspace, relative)
            If _inputs.Read(full) IsNot Nothing Then Return full
        Next
        Return Nothing
    End Function
    Private Function ReadJson(full As String) As JObject
        Dim text = InstanceConfigurationInputs.Text(_inputs.Read(full))
        If text Is Nothing Then Return Nothing
        Return JObject.Parse(text, New Newtonsoft.Json.Linq.JsonLoadSettings With {.DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error})
    End Function
    Private Function CeReader(preferred As String) As Func(Of String, String)
        Dim ext = Path.GetExtension(preferred).ToLowerInvariant(), bytes = _inputs.Read(preferred)
        If bytes IsNot Nothing Then
            Dim text = InstanceConfigurationInputs.Text(bytes)
            If ext = ".yml" OrElse ext = ".yaml" Then Return Function(key) LauncherDirectoryReader.ReadYamlScalar(text, key)
            If ext = ".json" Then
                Dim json = JObject.Parse(text, New JsonLoadSettings With {.DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error})
                Return Function(key) Scalar(json(key))
            End If
            Return Function(key) IniValue(text, key)
        End If
        If ext = ".yml" OrElse ext = ".yaml" Then
            Dim jsonPath = Path.ChangeExtension(preferred, ".json"), json = ReadJson(jsonPath)
            If json IsNot Nothing Then Return Function(key) Scalar(json(key))
            Dim ini = InstanceConfigurationInputs.Text(_inputs.Read(Path.Combine(Path.GetDirectoryName(preferred), "Setup.ini")))
            If ini IsNot Nothing Then Return Function(key) IniValue(ini, key)
        End If
        Return Function(key) Nothing
    End Function
    Private Shared Function IniValue(text As String, key As String) As String
        Dim value As String = Nothing
        For Each line In text.Replace(vbCrLf, vbLf).Split(ChrW(10))
            Dim split = line.IndexOf(":"c)
            If split > 0 AndAlso line.Substring(0, split) = key Then value = line.Substring(split + 1).Trim()
        Next
        Return value
    End Function
    Private Shared Function Scalar(value As JToken) As String
        If value Is Nothing OrElse value.Type = JTokenType.Null Then Return Nothing
        If value.Type <> JTokenType.String AndAlso value.Type <> JTokenType.Boolean AndAlso value.Type <> JTokenType.Integer Then Throw New InvalidDataException("实例配置值类型无效。")
        Return CStr(value)
    End Function
    Private Shared Function ParseBool(value As String) As Boolean
        If String.Equals(value, "true", StringComparison.OrdinalIgnoreCase) OrElse value = "1" Then Return True
        If String.Equals(value, "false", StringComparison.OrdinalIgnoreCase) OrElse value = "0" Then Return False
        Throw New InvalidDataException("实例隔离开关无效。")
    End Function
    Private Shared Function BooleanValue(value As JToken, fallback As Boolean) As Boolean
        Return If(value Is Nothing, fallback, ParseBool(Scalar(value)))
    End Function
    Private Shared Function ParseInt(value As String, fallback As Integer, minimum As Integer, maximum As Integer) As Integer
        If value Is Nothing Then Return fallback
        Dim result As Integer
        If Not Integer.TryParse(value, result) OrElse result < minimum OrElse result > maximum Then Throw New InvalidDataException("实例隔离策略无效。")
        Return result
    End Function
    Private Shared Function HmclMode(value As JToken) As Integer
        Dim text = Scalar(value)
        Select Case text
            Case Nothing, "ROOT_FOLDER" : Return 0
            Case "VERSION_FOLDER" : Return 1
            Case "CUSTOM" : Return 2
            Case Else : Return ParseInt(text, 0, 0, 2)
        End Select
    End Function
    Private Shared Function StrategyValue(value As Integer, modable As Boolean, nonRelease As Boolean) As Boolean
        Select Case value
            Case 0 : Return False
            Case 1 : Return modable
            Case 2 : Return nonRelease
            Case 3 : Return modable OrElse nonRelease
            Case Else : Return True
        End Select
    End Function
    Private Shared Sub CheckSchema(json As JObject, name As String)
        If json Is Nothing OrElse json("$schema") Is Nothing Then Return
        If Not Regex.IsMatch(Scalar(json("$schema")), "\Ahttps://schemas\.glavo\.site/hmcl/" & name & "/1\.\d+(?:\.\d+)?\z") Then Throw New InvalidDataException("HMCL 实例配置版本暂不支持。")
    End Sub
    Private Shared Function Same(a As String, b As String) As Boolean
        Return String.Equals(Path.GetFullPath(a).TrimEnd("\"c, "/"c), Path.GetFullPath(b).TrimEnd("\"c, "/"c), StringComparison.OrdinalIgnoreCase)
    End Function
    Private Function HasUserData() As Boolean
        Dim mods = Path.GetDirectoryName(SafeFiles.UnderRoot(_root, "versions/" & _name & "/mods/entry")), saves = Path.GetDirectoryName(SafeFiles.UnderRoot(_root, "versions/" & _name & "/saves/entry"))
        Return (Directory.Exists(mods) AndAlso Directory.EnumerateFiles(mods).Any()) OrElse (Directory.Exists(saves) AndAlso Directory.EnumerateDirectories(saves).Any())
    End Function

    Public Sub WriteNewSettings(stage As String) Implements IInstanceIsolationPlan.WriteNewSettings
        If _target.Kind = ExternalLauncherKind.PclCe Then
            Dim yaml = SafeFiles.UnderRoot(stage, "PCL/config.v1.yml"), jsonPath = SafeFiles.UnderRoot(stage, "PCL/config.v1.json")
            If Not File.Exists(yaml) AndAlso File.Exists(jsonPath) Then
                Dim json = JObject.Parse(StageText(jsonPath), New JsonLoadSettings With {.DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error})
                json("VersionArgumentIndie") = If(Isolated, 1, 2) : json("VersionArgumentIndieV2") = Isolated
                Save(stage, "PCL/config.v1.json", Encoding.UTF8.GetBytes(json.ToString()))
            Else
                Dim text = StageText(yaml)
                LauncherDirectoryReader.ReadYamlScalar(text, "VersionArgumentIndie")
                LauncherDirectoryReader.ReadYamlScalar(text, "VersionArgumentIndieV2")
                ' Only the two isolation keys are replaced; every other archive value
                ' and comment remains in its original order.
                Dim lines = text.Replace(vbCrLf, vbLf).Split(ChrW(10)).Where(Function(line) Not line.StartsWith("VersionArgumentIndie:", StringComparison.Ordinal) AndAlso Not line.StartsWith("VersionArgumentIndieV2:", StringComparison.Ordinal))
                Save(stage, "PCL/config.v1.yml", Encoding.UTF8.GetBytes(String.Join(vbLf, lines) & vbLf & "VersionArgumentIndie: " & If(Isolated, "1", "2") & vbLf & "VersionArgumentIndieV2: " & If(Isolated, "true", "false") & vbLf))
            End If
            Dim iniPath = SafeFiles.UnderRoot(stage, "PCL/Setup.ini")
            Dim ini = StageText(iniPath)
            Dim iniLines = ini.Replace(vbCrLf, vbLf).Split(ChrW(10)).Where(Function(line) Not Regex.IsMatch(line, "^\s*VersionArgumentIndie(?:V2)?\s*:", RegexOptions.IgnoreCase))
            Save(stage, "PCL/Setup.ini", Encoding.UTF8.GetBytes(String.Join(vbCrLf, iniLines) & vbCrLf & "VersionArgumentIndie:" & If(Isolated, "1", "2") & vbCrLf & "VersionArgumentIndieV2:" & Isolated.ToString() & vbCrLf))
        ElseIf _hmclModern Then
            Dim relative = ".hmcl/config/instance-game-settings.json", filePath = SafeFiles.UnderRoot(stage, relative)
            Dim json = If(File.Exists(filePath), JObject.Parse(StageText(filePath), New JsonLoadSettings With {.DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error}), New JObject())
            CheckSchema(json, "instance-game-settings")
            json("$schema") = "https://schemas.glavo.site/hmcl/instance-game-settings/1.0.1"
            Dim overrideNames = TryCast(json("overrideProperties"), JArray)
            If json("overrideProperties") IsNot Nothing AndAlso overrideNames Is Nothing Then Throw New InvalidDataException("整合包的 HMCL 覆盖属性无效。")
            If overrideNames Is Nothing Then overrideNames = New JArray()
            If Not overrideNames.Values(Of String)().Contains("runningDirectory") Then overrideNames.Add("runningDirectory")
            json("overrideProperties") = overrideNames : json("runningDirectory") = If(Isolated, "", RunDirectory)
            Save(stage, relative, Encoding.UTF8.GetBytes(json.ToString()))
        Else
            Dim relative = "hmclversion.cfg", filePath = SafeFiles.UnderRoot(stage, relative)
            Dim json = If(File.Exists(filePath), JObject.Parse(StageText(filePath), New JsonLoadSettings With {.DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error}), DirectCast(_legacyGlobal.DeepClone(), JObject))
            json("usesGlobal") = False : json("gameDirType") = If(Isolated, 1, If(Same(RunDirectory, _root), 0, 2))
            If Not Isolated AndAlso Not Same(RunDirectory, _root) Then json("gameDir") = RunDirectory
            Save(stage, relative, Encoding.UTF8.GetBytes(json.ToString()))
        End If
    End Sub
    Private Shared Function StageText(full As String) As String
        If Not File.Exists(full) Then Return ""
        If New FileInfo(full).Length > 2 * 1024 * 1024 Then Throw New InvalidDataException("整合包实例配置超过大小限制。")
        Return File.ReadAllText(full)
    End Function
    Private Sub Save(stage As String, relative As String, bytes As Byte())
        Dim full = SafeFiles.UnderRoot(stage, relative)
        Directory.CreateDirectory(Path.GetDirectoryName(full)) : File.WriteAllBytes(full, bytes)
        _written(relative) = bytes
    End Sub
    Friend Sub ValidateStaged(stage As String, token As CancellationToken)
        For Each item In _written
            token.ThrowIfCancellationRequested()
            Dim full = SafeFiles.UnderRoot(stage, item.Key)
            If Not File.ReadAllBytes(full).SequenceEqual(item.Value) Then Throw New InvalidDataException("原生实例配置在发布前发生变化，请重试。")
        Next
    End Sub
    Public Function LockForRepair() As IInstanceConfigurationGuard Implements IInstanceIsolationPlan.LockForRepair
        Return _inputs.Hold(Sub()
                                If _folderSignal.HasValue AndAlso HasUserData() <> _folderSignal.Value Then Throw New InvalidDataException("实例数据目录在补全期间发生变化，请重试。")
                            End Sub)
    End Function
End Class
