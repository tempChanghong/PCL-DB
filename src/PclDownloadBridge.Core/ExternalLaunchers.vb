Imports System.IO
Imports System.Text
Imports System.Text.RegularExpressions
Imports Newtonsoft.Json.Linq

Public Enum ExternalLauncherKind
    Pcl
    Hmcl
    PclCe
End Enum

' Directory discovery is read-only. Paths are resolved against an explicitly
' bound workspace, never against this process's current working directory.
Public NotInheritable Class ExternalLauncherBinding
    Public Property Id As String = Guid.NewGuid().ToString("N")
    Public Property Kind As ExternalLauncherKind
    Public Property Workspace As String
    Public Property ConfigPath As String
    Public Property SharedConfigPath As String
    Public ReadOnly Property DisplayName As String
        Get
            Return If(Kind = ExternalLauncherKind.Pcl, "PCL", If(Kind = ExternalLauncherKind.Hmcl, "HMCL", "PCL-CE"))
        End Get
    End Property
    Public Overrides Function ToString() As String
        Return Workspace & If(String.IsNullOrEmpty(ConfigPath), "", " · " & ConfigPath) & If(String.IsNullOrEmpty(SharedConfigPath), "", " · " & SharedConfigPath)
    End Function
    Public Function Copy() As ExternalLauncherBinding
        Return New ExternalLauncherBinding With {.Id = Id, .Kind = Kind, .Workspace = Workspace, .ConfigPath = ConfigPath, .SharedConfigPath = SharedConfigPath}
    End Function
    Public Function ToJson() As JObject
        Return New JObject From {{"kind", Kind.ToString()}, {"workspace", Workspace}, {"configPath", If(ConfigPath, "")}, {"sharedConfigPath", If(SharedConfigPath, "")}}
    End Function
    Public Shared Function FromJson(id As String, value As JObject) As ExternalLauncherBinding
        Dim kind As ExternalLauncherKind
        If String.IsNullOrWhiteSpace(id) OrElse id.Length > 80 OrElse value Is Nothing OrElse
            Not [Enum].TryParse(CStr(value("kind")), True, kind) OrElse Not [Enum].IsDefined(GetType(ExternalLauncherKind), kind) Then Throw New InvalidDataException("启动器绑定类型无效。")
        Dim result = New ExternalLauncherBinding With {.Id = id, .Kind = kind, .Workspace = LauncherDirectoryReader.AbsolutePath(CStr(value("workspace")), Nothing),
            .ConfigPath = OptionalPath(CStr(value("configPath"))), .SharedConfigPath = OptionalPath(CStr(value("sharedConfigPath")))}
        result.ValidatePclConfiguration()
        Return result
    End Function
    Public Sub ValidatePclConfiguration()
        If Kind = ExternalLauncherKind.Pcl AndAlso ConfigPath IsNot Nothing AndAlso Not String.Equals(LauncherDirectoryReader.AbsolutePath(ConfigPath, Nothing), LauncherDirectoryReader.AbsolutePath(Path.Combine(Workspace, "PCL/Setup.ini"), Nothing), StringComparison.OrdinalIgnoreCase) Then
            Throw New InvalidDataException("PCL 配置与工作目录不匹配，请选择该位置下的 PCL/Setup.ini。")
        End If
    End Sub
    Private Shared Function OptionalPath(value As String) As String
        Return If(String.IsNullOrWhiteSpace(value), Nothing, LauncherDirectoryReader.AbsolutePath(value, Nothing))
    End Function
End Class

Public NotInheritable Class LauncherDirectoryResult
    Public ReadOnly Folders As New List(Of GameFolder)
    Public ReadOnly Warnings As New List(Of String)
    Public Property Succeeded As Boolean
End Class

Public NotInheritable Class LauncherDirectoryReader
    Public Shared Function AbsolutePath(value As String, workspace As String) As String
        If String.IsNullOrWhiteSpace(value) OrElse value.IndexOf(ChrW(0)) >= 0 OrElse value.Contains("://") Then Throw New InvalidDataException("目录路径无效。")
        If value.Length >= 2 AndAlso value(1) = ":"c AndAlso (value.Length < 3 OrElse (value(2) <> "\"c AndAlso value(2) <> "/"c)) Then Throw New InvalidDataException("不接受依赖当前盘符目录的路径。")
        If Not Path.IsPathRooted(value) Then
            If workspace Is Nothing Then Throw New InvalidDataException("启动器位置必须为绝对路径。")
            value = Path.Combine(workspace, value)
        End If
        If Path.GetPathRoot(value).Length = 1 Then Throw New InvalidDataException("不接受依赖当前盘符的根相对路径。")
        Dim full = Path.GetFullPath(value), root = Path.GetPathRoot(full)
        Return If(full.Length = root.Length, full, full.TrimEnd("\"c, "/"c))
    End Function

    Public Function Read(binding As ExternalLauncherBinding, Optional readUserConfiguration As Boolean = True) As LauncherDirectoryResult
        Dim result As New LauncherDirectoryResult
        Try
            Dim source = binding.Copy()
            source.Workspace = AbsolutePath(source.Workspace, Nothing)
            If Not Directory.Exists(source.Workspace) Then Throw New DirectoryNotFoundException("启动器工作目录不存在。")
            If source.Kind = ExternalLauncherKind.Pcl Then
                source.ValidatePclConfiguration()
                Dim reader As New PclFolders()
                If source.ConfigPath IsNot Nothing AndAlso Not File.Exists(source.ConfigPath) Then Throw New FileNotFoundException("绑定的 PCL 配置不存在。")
                result.Folders.AddRange(reader.Discover(source.Workspace, readUserConfiguration, source.ConfigPath))
                result.Warnings.AddRange(reader.Warnings)
            ElseIf source.Kind = ExternalLauncherKind.Hmcl Then
                ReadHmcl(source, result, readUserConfiguration)
            ElseIf source.Kind = ExternalLauncherKind.PclCe Then
                ReadCe(source, result, readUserConfiguration)
            Else
                Throw New InvalidDataException("未知的启动器类型。")
            End If
            For Each folder In result.Folders
                folder.LauncherBindingId = source.Id : folder.LauncherKind = source.Kind
                folder.LauncherWorkspace = source.Workspace
                folder.Origin = source.DisplayName & " · " & source.Workspace & " · " & folder.Origin
            Next
            result.Succeeded = True
        Catch ex As Exception When TypeOf ex Is InvalidDataException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is ArgumentException OrElse TypeOf ex Is NotSupportedException OrElse TypeOf ex Is Newtonsoft.Json.JsonException OrElse TypeOf ex Is System.Security.SecurityException OrElse TypeOf ex Is InvalidCastException OrElse TypeOf ex Is FormatException
            result.Warnings.Add("读取启动器目录失败：" & ex.Message)
        End Try
        Return result
    End Function

    Private Shared Function ReadText(path As String) As String
        Using stream As New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite Or FileShare.Delete)
            If stream.Length > 2 * 1024 * 1024 Then Throw New InvalidDataException("目录配置超过大小限制。")
            Using reader As New StreamReader(stream, Encoding.UTF8, True)
                Dim chars(2 * 1024 * 1024) As Char, count = 0
                While count < chars.Length
                    Dim read = reader.Read(chars, count, chars.Length - count)
                    If read = 0 Then Exit While
                    count += read
                End While
                If count = chars.Length Then Throw New InvalidDataException("目录配置超过大小限制。")
                Return New String(chars, 0, count)
            End Using
        End Using
    End Function

    Private Shared Function FirstFile(ParamArray paths As String()) As String
        Return paths.FirstOrDefault(Function(path) Not String.IsNullOrEmpty(path) AndAlso File.Exists(path))
    End Function

    Private Shared Sub Add(result As LauncherDirectoryResult, binding As ExternalLauncherBinding, raw As String, name As String, origin As String, Optional selected As Boolean = False, Optional warnMissing As Boolean = True)
        Try
            Dim full = AbsolutePath(raw, binding.Workspace)
            If Not Directory.Exists(full) Then
                If warnMissing Then result.Warnings.Add("已跳过不存在的目录：" & full)
                Return
            End If
            Dim prior = result.Folders.FirstOrDefault(Function(folder) String.Equals(folder.Location, full, StringComparison.OrdinalIgnoreCase))
            If prior Is Nothing Then
                result.Folders.Add(New GameFolder With {.Name = If(String.IsNullOrWhiteSpace(name), binding.DisplayName & " 游戏目录", name), .Location = full, .Origin = origin, .IsSelected = selected})
            Else
                prior.IsSelected = prior.IsSelected OrElse selected
            End If
        Catch ex As Exception When TypeOf ex Is InvalidDataException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is ArgumentException OrElse TypeOf ex Is NotSupportedException OrElse TypeOf ex Is UnauthorizedAccessException
            result.Warnings.Add("忽略无效的游戏目录。")
        End Try
    End Sub

    Private Shared Sub ReadHmcl(binding As ExternalLauncherBinding, result As LauncherDirectoryResult, user As Boolean)
        Dim work = binding.Workspace
        Dim primary = If(binding.ConfigPath, FirstFile(Path.Combine(work, ".hmcl/config/game-directories.json"), Path.Combine(work, ".hmcl/hmcl.json"), Path.Combine(work, "hmcl.json"), Path.Combine(work, ".hmcl.json")))
        Dim selectedId As String = Nothing, ids As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim localSettings = If(primary IsNot Nothing AndAlso Path.GetFileName(primary).Equals("game-directories.json", StringComparison.OrdinalIgnoreCase), Path.Combine(Path.GetDirectoryName(primary), "launcher-settings.json"), Path.Combine(work, ".hmcl/config/launcher-settings.json"))
        If File.Exists(localSettings) Then
            Try
                selectedId = CStr(JObject.Parse(ReadText(localSettings))("selectedGameDirectory"))
            Catch ex As Exception When TypeOf ex Is InvalidDataException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is Newtonsoft.Json.JsonException
                result.Warnings.Add("无法读取 HMCL 当前目录选择。")
            End Try
        End If
        If primary IsNot Nothing Then
            Dim json = JObject.Parse(ReadText(primary))
            If TypeOf json("directories") Is JArray Then
                ReadHmclDirectories(json, binding, result, primary, selectedId, ids)
            ElseIf TypeOf json("configurations") Is JObject Then
                If DirectCast(json("configurations"), JObject).Count > 2048 Then Throw New InvalidDataException("HMCL 目录条目过多。")
                Dim selected = CStr(json("last"))
                For Each item In DirectCast(json("configurations"), JObject).Properties()
                    Dim profile = TryCast(item.Value, JObject)
                    If profile Is Nothing OrElse profile("gameDir")?.Type <> JTokenType.String Then Continue For
                    Add(result, binding, CStr(profile("gameDir")), item.Name, primary, item.Name = selected)
                Next
            Else
                Throw New InvalidDataException("无法识别 HMCL 目录配置格式。")
            End If
        End If
        Dim sharedFile = binding.SharedConfigPath
        If sharedFile IsNot Nothing AndAlso Not File.Exists(sharedFile) Then result.Warnings.Add("绑定的 HMCL 共享目录配置不存在，继续保留本地目录。")
        If String.IsNullOrEmpty(sharedFile) AndAlso user Then sharedFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".hmcl/config/user-game-directories.json")
        If Not String.IsNullOrEmpty(sharedFile) AndAlso File.Exists(sharedFile) Then
            Try
                ReadHmclDirectories(JObject.Parse(ReadText(sharedFile)), binding, result, sharedFile, selectedId, ids)
            Catch ex As Exception When TypeOf ex Is InvalidDataException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is Newtonsoft.Json.JsonException
                result.Warnings.Add("HMCL 共享目录配置读取失败：" & ex.Message)
            End Try
        End If
        Add(result, binding, ".minecraft", "HMCL 默认游戏目录", "工作目录", selected:=selectedId = "7105bc1f-490e-5e8c-878c-f5844c3d4bc3", warnMissing:=False)
        If user Then Add(result, binding, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft"), "HMCL 用户游戏目录", "用户目录", selected:=selectedId = "f3eafde8-506e-5a77-bc88-f24b4728dfb2", warnMissing:=False)
    End Sub

    Private Shared Sub ReadHmclDirectories(json As JObject, binding As ExternalLauncherBinding, result As LauncherDirectoryResult, origin As String, selected As String, ids As HashSet(Of String))
        Dim directories = TryCast(json("directories"), JArray)
        If directories Is Nothing Then Throw New InvalidDataException("HMCL 目录列表缺失。")
        If directories.Count > 2048 Then Throw New InvalidDataException("HMCL 目录条目过多。")
        Dim schema = json("$schema")
        If schema IsNot Nothing AndAlso (schema.Type <> JTokenType.String OrElse Not Regex.IsMatch(CStr(schema), "\Ahttps://schemas\.glavo\.site/hmcl/game-directories/1\.\d+(?:\.\d+)?\z")) Then Throw New InvalidDataException("HMCL 目录配置版本暂不支持。")
        For Each item In directories.OfType(Of JObject)()
            If item("path")?.Type <> JTokenType.String Then Continue For
            Dim id = If(item("id")?.Type = JTokenType.String, CStr(item("id")), Nothing)
            If id IsNot Nothing AndAlso Not ids.Add(id) Then Continue For
            Dim label = If(item("name")?.Type = JTokenType.String, CStr(item("name")), "HMCL 游戏目录")
            Add(result, binding, CStr(item("path")), label, origin, selected IsNot Nothing AndAlso id = selected)
        Next
    End Sub

    Private Shared Sub ReadCe(binding As ExternalLauncherBinding, result As LauncherDirectoryResult, user As Boolean)
        Dim work = binding.Workspace, selected As String = Nothing
        Dim primary = If(binding.ConfigPath, FirstFile(Path.Combine(work, "PCL/config.v1.yml"), Path.Combine(work, "PCL/config.v1.json"), Path.Combine(work, "PCL/Setup.ini")))
        If primary IsNot Nothing Then
            Dim content = ReadText(primary)
            Select Case Path.GetExtension(primary).ToLowerInvariant()
                Case ".yml", ".yaml"
                    Dim version = ReadYamlScalar(content, "LocalFileVersion")
                    If version IsNot Nothing AndAlso version <> "1" Then Throw New InvalidDataException("PCL-CE 本地配置版本暂不支持。")
                    selected = ReadYamlScalar(content, "LaunchFolderSelect")
                Case ".json"
                    Dim json = JObject.Parse(content)
                    If json("LocalFileVersion") IsNot Nothing AndAlso CStr(json("LocalFileVersion")) <> "1" Then Throw New InvalidDataException("PCL-CE 本地配置版本暂不支持。")
                    selected = CStr(json("LaunchFolderSelect"))
                Case ".ini" : selected = PclFolders.ReadIniValue(primary, "LaunchFolderSelect")
                Case Else : Throw New InvalidDataException("不支持的 PCL-CE 配置格式。")
            End Select
        End If
        Dim sharedFile = binding.SharedConfigPath
        If sharedFile IsNot Nothing AndAlso Not File.Exists(sharedFile) Then result.Warnings.Add("绑定的 PCL-CE 共享目录配置不存在，继续保留本地目录。")
        If String.IsNullOrEmpty(sharedFile) AndAlso user Then
            Dim app = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            sharedFile = FirstFile(Path.Combine(app, "PCLCE/config.v1.json"), Path.Combine(app, "PCLCE/config.json"), Path.Combine(app, ".PCLCE/Config.json"))
        End If
        If sharedFile IsNot Nothing AndAlso File.Exists(sharedFile) Then
            Try
                Dim json = JObject.Parse(ReadText(sharedFile))
                If json("FileVersion") IsNot Nothing AndAlso CStr(json("FileVersion")) <> "1" Then Throw New InvalidDataException("PCL-CE 共享配置版本暂不支持。")
                Dim raw = CStr(json("LaunchFolders"))
                If raw IsNot Nothing Then
                    If raw.Split("|"c).Length > 2048 Then Throw New InvalidDataException("PCL-CE 目录条目过多。")
                    For Each item In raw.Split("|"c)
                        Dim split = item.IndexOf(">"c)
                        If split < 1 Then Continue For
                        Add(result, binding, PclFolders.ExpandPath(item.Substring(split + 1), work), item.Substring(0, split), sharedFile)
                    Next
                End If
            Catch ex As Exception When TypeOf ex Is InvalidDataException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is Newtonsoft.Json.JsonException
                result.Warnings.Add("PCL-CE 共享目录配置读取失败：" & ex.Message)
            End Try
        End If
        If Not String.IsNullOrWhiteSpace(selected) Then
            Dim full = AbsolutePath(PclFolders.ExpandPath(selected, work), work)
            Add(result, binding, full, "PCL-CE 当前选择", primary, True)
            For Each folder In result.Folders
                folder.IsSelected = String.Equals(folder.Location, full, StringComparison.OrdinalIgnoreCase)
            Next
        End If
        Add(result, binding, ".minecraft", "PCL-CE 默认游戏目录", "启动器目录", warnMissing:=False)
    End Sub

    ' Read only the flat scalar used by CE's YamlFileProvider. Unsupported YAML
    ' constructs fail visibly instead of guessing or migrating the foreign file.
    Private Shared Function ReadYamlScalar(text As String, key As String) As String
        Dim found = False, value As String = Nothing
        For Each line In text.Replace(vbCrLf, vbLf).Split(ChrW(10))
            If Not line.StartsWith(key & ":", StringComparison.Ordinal) Then Continue For
            If found Then Throw New InvalidDataException("PCL-CE 目录配置包含重复键。")
            found = True : value = line.Substring(key.Length + 1).Trim()
            If value.StartsWith("""", StringComparison.Ordinal) Then
                value = JToken.Parse(value).Value(Of String)()
            ElseIf value.StartsWith("'", StringComparison.Ordinal) Then
                If Not value.EndsWith("'", StringComparison.Ordinal) OrElse value.Length < 2 Then Throw New InvalidDataException("PCL-CE 目录字符串不完整。")
                value = value.Substring(1, value.Length - 2).Replace("''", "'")
            Else
                If value.StartsWith("|", StringComparison.Ordinal) OrElse value.StartsWith(">", StringComparison.Ordinal) OrElse value.StartsWith("*", StringComparison.Ordinal) OrElse value.StartsWith("&", StringComparison.Ordinal) OrElse value.StartsWith("!", StringComparison.Ordinal) OrElse value.StartsWith("{", StringComparison.Ordinal) OrElse value.StartsWith("[", StringComparison.Ordinal) Then Throw New InvalidDataException("此 YAML 目录写法暂不支持，请手动选择游戏目录。")
                Dim comment = value.IndexOf(" #", StringComparison.Ordinal)
                If comment >= 0 Then value = value.Substring(0, comment).TrimEnd()
                If value = "null" OrElse value = "~" Then value = Nothing
            End If
        Next
        Return value
    End Function
End Class

' Immutable task context: discovery and preference changes cannot retarget work.
Public NotInheritable Class LauncherInstallTarget
    Public ReadOnly Root As String
    Public ReadOnly BindingId As String
    Public ReadOnly Kind As ExternalLauncherKind
    Public ReadOnly Workspace As String
    Public ReadOnly Property PclDirectory As String
        Get
            Return If(Kind = ExternalLauncherKind.Pcl, Workspace, Nothing)
        End Get
    End Property
    Public Sub New(folder As GameFolder, fallbackPcl As String)
        If folder Is Nothing Then Throw New ArgumentNullException(NameOf(folder))
        Root = LauncherDirectoryReader.AbsolutePath(folder.Location, Nothing)
        BindingId = folder.LauncherBindingId
        Kind = folder.LauncherKind.GetValueOrDefault(ExternalLauncherKind.Pcl)
        Workspace = If(folder.LauncherWorkspace, fallbackPcl)
    End Sub
    Public Sub ValidateFirstBatch(repair As Boolean, components As Boolean, pack As Boolean, supplemental As Boolean)
        If Kind <> ExternalLauncherKind.Pcl AndAlso (repair OrElse components OrElse pack OrElse supplemental) Then
            Throw New InvalidOperationException("此启动器当前支持标准原版新安装；组件、整合包、历史特供及已有实例补全将在隔离适配完成后开放。")
        End If
    End Sub
End Class

' Stored inside the bridge's preferences only; the original object is retained
' so future fields and unrecognized binding records survive a current save.
Public NotInheritable Class LauncherBindingsState
    Private _original As JToken
    Public ReadOnly Bindings As New List(Of ExternalLauncherBinding)
    Public ReadOnly SelectedRows As New Dictionary(Of ExternalLauncherKind, String)
    Public ReadOnly Warnings As New List(Of String)
    Public Property TargetBindingId As String
    Public Property TargetRoot As String
    Public Property ReadOnlyFormat As Boolean

    Public Shared Function Restore(settings As JObject, fallbackPcl As String) As LauncherBindingsState
        Dim result As New LauncherBindingsState With {.TargetRoot = If(CStr(settings?("gameRoot")), "")}
        Dim raw = settings?("externalLaunchers")
        If raw Is Nothing Then
            Dim binding As New ExternalLauncherBinding With {.Id = "legacy-pcl", .Kind = ExternalLauncherKind.Pcl,
                .Workspace = LauncherDirectoryReader.AbsolutePath(If(CStr(settings?("pclBase")), fallbackPcl), Nothing)}
            result.Bindings.Add(binding) : result.SelectedRows(binding.Kind) = binding.Id : result.TargetBindingId = binding.Id
            Return result
        End If
        result._original = raw.DeepClone()
        If Not TypeOf result._original Is JObject OrElse result._original("format")?.Type <> JTokenType.Integer OrElse result._original("format").ToString() <> "1" OrElse
            Not TypeOf result._original("bindings") Is JObject Then
            result.ReadOnlyFormat = True : result.Warnings.Add("启动器绑定格式无法识别，已保留原设置，请手动选择游戏目录。") : Return result
        End If
        result.TargetRoot = If(CStr(result._original("targetRoot")), result.TargetRoot)
        result.TargetBindingId = CStr(result._original("targetBindingId"))
        For Each record In DirectCast(result._original("bindings"), JObject).Properties()
            Try
                result.Bindings.Add(ExternalLauncherBinding.FromJson(record.Name, TryCast(record.Value, JObject)))
            Catch ex As Exception When TypeOf ex Is InvalidDataException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is ArgumentException OrElse TypeOf ex Is InvalidCastException OrElse TypeOf ex Is FormatException
                result.Warnings.Add("保留但跳过无效的启动器绑定：" & record.Name)
            End Try
        Next
        Dim rows = TryCast(result._original("selectedRows"), JObject)
        For Each kind As ExternalLauncherKind In [Enum].GetValues(GetType(ExternalLauncherKind))
            Dim id = CStr(rows?(kind.ToString()))
            Dim selected = result.Bindings.FirstOrDefault(Function(binding) binding.Kind = kind AndAlso binding.Id = id)
            If selected Is Nothing Then selected = result.Bindings.FirstOrDefault(Function(binding) binding.Kind = kind)
            If selected IsNot Nothing Then result.SelectedRows(kind) = selected.Id
        Next
        Return result
    End Function

    Public Function ToJson() As JToken
        If ReadOnlyFormat Then Return If(_original?.DeepClone(), JValue.CreateNull())
        Dim result = If(TryCast(_original?.DeepClone(), JObject), New JObject())
        result("format") = 1
        Dim records = TryCast(result("bindings"), JObject)
        If records Is Nothing Then records = New JObject() : result("bindings") = records
        For Each binding In Bindings
            Dim item = TryCast(records(binding.Id), JObject)
            If item Is Nothing Then item = New JObject()
            item.Merge(binding.ToJson()) : records(binding.Id) = item
        Next
        Dim rows As New JObject
        For Each row In SelectedRows
            rows(row.Key.ToString()) = row.Value
        Next
        result("selectedRows") = rows : result("targetBindingId") = If(TargetBindingId, "") : result("targetRoot") = If(TargetRoot, "")
        Return result
    End Function
End Class
