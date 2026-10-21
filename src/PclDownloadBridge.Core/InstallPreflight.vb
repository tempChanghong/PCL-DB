Imports System.IO
Imports System.Text.RegularExpressions
Imports Newtonsoft.Json.Linq

Public Module InstallPreflight
    Public Sub ValidatePaths(root As String, instanceName As String)
        ValidateName(instanceName)
        ' PCL PageSelectLeft also rejects ! and ;: Java expands these in launch paths.
        If root.IndexOfAny({"!"c, ";"c}) >= 0 OrElse instanceName.IndexOfAny({"!"c, ";"c}) >= 0 Then Throw New ArgumentException("为兼容 PCL 与 Java，游戏路径和实例名称不能含有 ! 或 ;。")
        SafeFiles.UnderRoot(root, "versions/" & instanceName & "/" & instanceName & ".json")
    End Sub

    Public Sub ValidateName(instanceName As String)
        ' PCL ValidateFolderName, with the existing Windows reserved-name checks.
        If String.IsNullOrWhiteSpace(instanceName) Then Throw New ArgumentException("版本名称不能为空。")
        If instanceName.StartsWith(" ", StringComparison.Ordinal) OrElse instanceName.EndsWith(" ", StringComparison.Ordinal) Then Throw New ArgumentException("版本名称不能以空格开头或结尾。")
        If instanceName.EndsWith(".", StringComparison.Ordinal) Then Throw New ArgumentException("版本名称不能以小数点结尾。")
        If instanceName.Length > 100 Then Throw New ArgumentException("版本名称不能超过 100 个字符。")
        If instanceName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 Then Throw New ArgumentException("版本名称不能包含 /、\、:、*、?、""、<、>、| 等字符。")
        If instanceName.IndexOfAny({"!"c, ";"c}) >= 0 Then Throw New ArgumentException("为兼容 PCL 与 Java，版本名称不能含有 ! 或 ;。")
        If Regex.IsMatch(instanceName, "^(CLOCK\$|COM0|LPT0)(\.|$)", RegexOptions.IgnoreCase) Then Throw New ArgumentException("版本名称是 PCL 不支持的保留名称。")
        If Regex.IsMatch(instanceName, ".{2,}~\d") Then Throw New ArgumentException("版本名称不能包含 NTFS 短文件名格式（如 ABC~1）。")
        SafeFiles.ValidateInstanceName(instanceName)
    End Sub

    Public Sub ValidateDestination(root As String, instanceName As String, repairExisting As Boolean)
        ValidatePaths(root, instanceName)
        Dim instancePath = SafeFiles.UnderRoot(root, "versions/" & instanceName)
        Dim jsonTarget = SafeFiles.UnderRoot(root, "versions/" & instanceName & "/" & instanceName & ".json")
        If File.Exists(instancePath) Then Throw New IOException("已有同名文件占用了版本目录，请选择新名称。")
        If Directory.Exists(instancePath) AndAlso Not repairExisting Then Throw New IOException("该实例已存在。请选择新名称，或明确选择「校验补全现有原版」。")
        If repairExisting AndAlso Not File.Exists(jsonTarget) Then Throw New IOException("未找到待补全实例的版本 JSON。")
    End Sub

    Public Sub VerifyJava(selection As ComponentSelection, metadata As JObject)
        If selection Is Nothing Then Return
        Dim forge = selection.Item(ComponentKind.Forge)
        Dim forgeMajor As Integer
        Dim modernForge = forge IsNot Nothing AndAlso (Not Integer.TryParse(If(forge.Version, "").Split("."c)(0), forgeMajor) OrElse forgeMajor >= 20)
        If modernForge OrElse selection.Has(ComponentKind.NeoForge) Then
            Dim minimum = Math.Max(8, CInt(If(metadata.SelectToken("javaVersion.majorVersion"), New JValue(8))))
            selection.JavaPath = JavaRuntime.Find(selection.JavaPath, minimum)
        ElseIf selection.Has(ComponentKind.OptiFine) AndAlso Not selection.Has(ComponentKind.Forge) AndAlso Not selection.Has(ComponentKind.Fabric) AndAlso Not selection.Has(ComponentKind.LiteLoader) Then
            ' Detect a missing runtime early. OptiFine's class version is checked again
            ' after fetching its installer, so automatic selection can choose a newer Java.
            JavaRuntime.Find(selection.JavaPath, 8)
        End If
    End Sub
End Module
