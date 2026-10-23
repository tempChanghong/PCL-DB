Imports System.Globalization
Imports System.IO
Imports System.Text
Imports Newtonsoft.Json.Linq

' PCL 5ef81e4 ModLaunch splits legacy arguments before replacing variables.
' LaunchWrapper 1.5/1.6 use jopt-simple 4.5: known single-dash long options and
' unambiguous abbreviations are options even after an unknown game argument.
Friend NotInheritable Class SupplementalLaunchRecipe
    Private Const LaunchClass As String = "net.minecraft.launchwrapper.Launch"
    Private Const Vanilla As String = "net.minecraft.launchwrapper.VanillaTweaker"
    Private Const Alpha As String = "net.minecraft.launchwrapper.AlphaVanillaTweaker"
    Private Shared ReadOnly OptionNames As String() = {"gameDir", "assetsDir", "tweakClass", "version"}

    Private Class Recipe
        Friend Version As String
        Friend Sha1 As String
        Friend Size As Long
        Friend Tweaker As String
    End Class

    Private Shared Function ForVersion(id As String) As Recipe
        If SupplementalVersions.IsHistoricalSnapshot(id) Then Return VanillaRecipe()
        Select Case id
            Case "c0.0.12a_03", "1.0.0-rc1", "inf-20100630-1"
                Return New Recipe With {.Version = "1.6", .Sha1 = "4ea0aca9c022a234ebaf14b51fb119055955fc9d", .Size = 27583, .Tweaker = Alpha}
            Case "2.0_blue", "2.0_red", "2.0_purple", "a1.1.1", "b1.9-pre6"
                Return VanillaRecipe()
            Case Else
                Throw New InvalidDataException("该补充版本尚未配置经过验证的启动配方。")
        End Select
    End Function

    Private Shared Function VanillaRecipe() As Recipe
        Return New Recipe With {.Version = "1.5", .Sha1 = "5150b9c2951f0fde987ce9c33496e26add1de224", .Size = 27787, .Tweaker = Vanilla}
    End Function

    Private Shared Function Text(value As JToken) As String
        Return If(value IsNot Nothing AndAlso value.Type = JTokenType.String, CStr(value), Nothing)
    End Function

    Private Shared Sub Reject(field As String, existing As Boolean)
        Throw New InvalidDataException(If(existing,
            "已有实例的启动配方不兼容（" & field & "）。请先在 PCL 中确认，或使用新名称安装；不会改写已有设置。",
            "补充版本启动配方无效（" & field & "）。请刷新列表后重试。"))
    End Sub

    Friend Shared Sub ValidateSource(id As String, profile As JObject)
        ValidateArguments(ForVersion(id), profile, False)
    End Sub

    Friend Shared Sub ValidateExisting(id As String, profile As JObject)
        ' An installed JSON uses the user's instance name, not the catalogue ID.
        ' Identity, clientVersion, libraries and index equality remain the installer's job.
        If profile("inheritsFrom") IsNot Nothing OrElse profile("jar") IsNot Nothing Then Reject("继承版本或自定义 JAR", True)
        ValidateArguments(ForVersion(id), profile, True)
    End Sub

    Private Shared Sub ValidateArguments(expected As Recipe, profile As JObject, existing As Boolean)
        If Text(profile("mainClass")) <> LaunchClass Then Reject("mainClass", existing)
        ' PCL appends arguments.game after minecraftArguments; it can override a
        ' correct directory or add a second tweaker. Known legacy recipes use neither branch.
        If profile.Property("arguments") IsNot Nothing Then Reject("混合的新旧 arguments", existing)
        Dim arguments = Text(profile("minecraftArguments"))
        If String.IsNullOrWhiteSpace(arguments) OrElse arguments.Length > If(existing, 65536, 4096) Then Reject("minecraftArguments 类型或长度", existing)
        Dim rawTokens As New List(Of String)
        Dim tokens = Tokenize(arguments, existing, rawTokens)
        If tokens.Count < 2 OrElse tokens(0) <> "${auth_player_name}" OrElse
            Not {"${auth_session}", "${auth_access_token}", "${access_token}"}.Contains(tokens(1), StringComparer.Ordinal) Then Reject("玩家名或会话参数", existing)
        ValidateQuotedVariable(rawTokens(0), tokens(0), existing)
        ValidateQuotedVariable(rawTokens(1), tokens(1), existing)
        Dim values As New Dictionary(Of String, String)(StringComparer.Ordinal), rawValues As New Dictionary(Of String, String)(StringComparer.Ordinal)
        Dim i = 2
        While i < tokens.Count
            Dim token = tokens(i)
            If token = "--" Then Reject("提前结束选项解析", existing)
            Dim prefix = If(token.StartsWith("--", StringComparison.Ordinal), 2, If(token.StartsWith("-", StringComparison.Ordinal), 1, 0))
            Dim key = token.Substring(prefix), value As String = Nothing, rawValue = rawTokens(i)
            Dim equals = key.IndexOf("="c)
            If equals >= 0 Then
                value = key.Substring(equals + 1) : key = key.Substring(0, equals)
            End If
            Dim known As String = Nothing
            If prefix > 0 AndAlso key.Length > 0 Then known = OptionNames.FirstOrDefault(Function(name) name.StartsWith(key, StringComparison.Ordinal))
            If known Is Nothing Then
                If Not existing Then Reject("未知的游戏参数", False)
                ' Unknown options do not consume the next token in jopt-simple.
                ' Scan it independently so -gameDir=... cannot hide as their value.
                i += 1 : Continue While
            End If
            If Not existing AndAlso (prefix <> 2 OrElse key <> known OrElse known = "version") Then Reject("非标准选项 " & key, False)
            If values.ContainsKey(known) Then Reject("重复选项 " & known, existing)
            If value Is Nothing Then
                i += 1
                If i >= tokens.Count Then Reject("缺少选项值 " & known, existing)
                value = tokens(i) : rawValue = rawTokens(i)
            End If
            If String.IsNullOrWhiteSpace(value) Then Reject("空选项值 " & known, existing)
            values.Add(known, value)
            rawValues.Add(known, rawValue)
            i += 1
        End While
        Dim game As String = Nothing, assets As String = Nothing, tweaker As String = Nothing
        If Not values.TryGetValue("gameDir", game) OrElse game <> "${game_directory}" Then Reject("gameDir 目录变量", existing)
        If Not values.TryGetValue("assetsDir", assets) OrElse assets <> "${game_assets}" Then Reject("assetsDir 目录变量", existing)
        ValidateQuotedVariable(rawValues("gameDir"), game, existing)
        ValidateQuotedVariable(rawValues("assetsDir"), assets, existing)
        If Not values.TryGetValue("tweakClass", tweaker) Then tweaker = Vanilla
        If tweaker <> expected.Tweaker Then Reject("tweakClass", existing)
    End Sub

    Private Shared Sub ValidateQuotedVariable(raw As String, variable As String, existing As Boolean)
        ' PCL only adds outer quotes when neither edge is already quoted. Empty
        ' quote prefixes/suffixes can otherwise leave a substituted path unquoted.
        If Not raw.StartsWith("""", StringComparison.Ordinal) AndAlso Not raw.EndsWith("""", StringComparison.Ordinal) Then
            If raw.Contains("""") Then Reject("变量中的歧义引号", existing)
            Return
        End If
        Dim position = raw.IndexOf(variable, StringComparison.Ordinal), quoted = False, i = 0
        If position < 0 Then Reject("变量引号", existing)
        While i < position
            If raw(i) = "\"c AndAlso i + 1 < position AndAlso raw(i + 1) = """"c Then
                i += 1
            ElseIf raw(i) = """"c Then
                quoted = Not quoted
            End If
            i += 1
        End While
        If Not quoted Then Reject("未包围变量的引号", existing)
    End Sub

    Private Shared Function Tokenize(arguments As String, existing As Boolean, rawTokens As List(Of String)) As List(Of String)
        Dim result As New List(Of String), current As New StringBuilder(), raw As New StringBuilder(), quoted = False, started = False
        Dim i = 0
        While i < arguments.Length
            Dim c = arguments(i)
            If Char.IsControl(c) AndAlso c <> ChrW(13) AndAlso c <> ChrW(10) Then Reject("参数控制字符", existing)
            If c = "\"c AndAlso i + 1 < arguments.Length AndAlso arguments(i + 1) = """"c Then
                current.Append(""""c) : raw.Append(c).Append(""""c) : started = True : i += 1
            ElseIf c = """"c Then
                quoted = Not quoted : raw.Append(c) : started = True
            ElseIf (c = " "c OrElse c = ChrW(13) OrElse c = ChrW(10)) AndAlso Not quoted Then
                AddToken(result, current, rawTokens, raw, started, existing)
            Else
                If c = ChrW(13) OrElse c = ChrW(10) Then c = " "c
                current.Append(c) : raw.Append(c) : started = True
            End If
            i += 1
        End While
        If quoted Then Reject("未闭合的引号", existing)
        AddToken(result, current, rawTokens, raw, started, existing)
        Return result
    End Function

    Private Shared Sub AddToken(tokens As List(Of String), current As StringBuilder, rawTokens As List(Of String), raw As StringBuilder, ByRef started As Boolean, existing As Boolean)
        If Not started Then Return
        If tokens.Count >= If(existing, 1024, 64) Then Reject("参数数量", existing)
        tokens.Add(current.ToString()) : rawTokens.Add(raw.ToString()) : current.Clear() : raw.Clear() : started = False
    End Sub

    Friend Shared Sub ValidateLibraries(id As String, profile As JObject)
        Dim expected = ForVersion(id), libraries = TryCast(profile("libraries"), JArray)
        If libraries Is Nothing Then Reject("libraries", False)
        Dim wrappers = libraries.OfType(Of JObject)().Where(Function(item) If(Text(item("name")), "").StartsWith("net.minecraft:launchwrapper:", StringComparison.Ordinal)).ToList()
        If wrappers.Count <> 1 Then Reject("LaunchWrapper 数量", False)
        Dim wrapper = wrappers(0), artifact = TryCast(wrapper.SelectToken("downloads.artifact"), JObject)
        If Text(wrapper("name")) <> "net.minecraft:launchwrapper:" & expected.Version OrElse
            wrapper.Property("rules") IsNot Nothing OrElse wrapper.Property("natives") IsNot Nothing OrElse artifact Is Nothing Then Reject("LaunchWrapper 声明", False)
        Dim sizeToken = artifact("size"), size As Long
        If sizeToken Is Nothing OrElse (sizeToken.Type <> JTokenType.Integer AndAlso sizeToken.Type <> JTokenType.String) OrElse
            Not Long.TryParse(sizeToken.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, size) OrElse size <> expected.Size OrElse
            Not String.Equals(Text(artifact("sha1")), expected.Sha1, StringComparison.OrdinalIgnoreCase) Then Reject("LaunchWrapper 工件校验", False)
    End Sub
End Class
