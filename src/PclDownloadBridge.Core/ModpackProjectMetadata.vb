Imports System.Globalization
Imports Newtonsoft.Json.Linq

' PCL 5ef81e4 ResourceProject.FromPlatformJson tags / loader declarations.
' Optional presentation metadata must not reject an otherwise usable project.
Public NotInheritable Class ModpackProjectMetadata
    Private Sub New()
    End Sub

    Private Shared ReadOnly CurseForgeTags As New Dictionary(Of Integer, String) From {
        {4484, "多人"}, {4479, "硬核"}, {4483, "战斗"}, {4478, "任务"}, {4472, "科技"}, {4473, "魔法"}, {4475, "冒险"}, {4476, "探索"},
        {4477, "小游戏"}, {4474, "科幻"}, {4736, "空岛"}, {5128, "原版改良"}, {4487, "FTB"}, {4480, "基于地图"}, {4481, "轻量"}, {4482, "大型"},
        {406, "世界元素"}, {407, "生物群系"}, {410, "维度"}, {408, "矿物/资源"}, {409, "天然结构"}, {412, "科技"}, {415, "管道/物流"}, {4843, "自动化"},
        {417, "能源"}, {4558, "红石"}, {436, "食物/烹饪"}, {416, "农业"}, {414, "运输"}, {420, "仓储"}, {419, "魔法"}, {422, "冒险"},
        {424, "装饰"}, {411, "生物"}, {434, "装备"}, {6814, "性能优化"}, {9026, "创造模式"}, {423, "信息显示"}, {435, "服务器"}, {5191, "改良"}, {421, "支持库"},
        {403, "原版风"}, {400, "写实风"}, {401, "现代风"}, {402, "中世纪"}, {399, "蒸汽朋克"}, {5244, "含字体"}, {404, "动态效果"}, {4465, "兼容 Mod"},
        {393, "16x"}, {394, "32x"}, {395, "64x"}, {396, "128x"}, {397, "256x"}, {398, "超高清"}, {5193, "数据包"},
        {6553, "写实风"}, {6554, "幻想风"}, {6555, "原版风"}, {6948, "冒险"}, {6949, "幻想"}, {6950, "支持库"}, {6952, "魔法"}, {6946, "Mod 相关"}, {6951, "科技"}, {6953, "实用"}}
    Private Shared ReadOnly ModrinthTags As New Dictionary(Of String, String)(StringComparer.Ordinal) From {
        {"technology", "科技"}, {"magic", "魔法"}, {"adventure", "冒险"}, {"utility", "实用"}, {"optimization", "性能优化"}, {"vanilla-like", "原版风"}, {"realistic", "写实风"},
        {"multiplayer", "多人"}, {"challenging", "硬核"}, {"combat", "战斗"}, {"quests", "任务"}, {"kitchen-sink", "水槽包"}, {"lightweight", "轻量"},
        {"worldgen", "世界元素"}, {"food", "食物/烹饪"}, {"game-mechanics", "游戏机制"}, {"transportation", "运输"}, {"storage", "仓储"}, {"decoration", "装饰"},
        {"mobs", "生物"}, {"equipment", "装备"}, {"social", "服务器"}, {"library", "支持库"}, {"simplistic", "简洁"}, {"tweaks", "改良"},
        {"8x-", "极简"}, {"16x", "16x"}, {"32x", "32x"}, {"48x", "48x"}, {"64x", "64x"}, {"128x", "128x"}, {"256x", "256x"}, {"512x+", "超高清"},
        {"audio", "含声音"}, {"fonts", "含字体"}, {"models", "含模型"}, {"gui", "含 UI"}, {"locale", "含语言"}, {"core-shaders", "核心着色器"},
        {"modded", "兼容 Mod"}, {"fantasy", "幻想风"}, {"semi-realistic", "半写实风"}, {"cartoon", "卡通风"},
        {"colored-lighting", "彩色光照"}, {"path-tracing", "路径追踪"}, {"pbr", "PBR"}, {"reflections", "反射"}, {"iris", "Iris"}, {"optifine", "OptiFine"}, {"vanilla", "原版可用"}}
    ' The first four follow PCLCS/Constants.cs flag order. Quilt/Rift/vanilla are
    ' display-only extensions already represented by this application's release labels.
    Private Shared ReadOnly LoaderOrder As String() = {"Forge", "LiteLoader", "Fabric", "NeoForge", "Quilt", "Rift", "原版"}
    Private Shared ReadOnly LoaderAliases As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
        {"forge", "Forge"}, {"liteloader", "LiteLoader"}, {"fabric", "Fabric"}, {"fabric-loader", "Fabric"}, {"neoforge", "NeoForge"}, {"neo-forge", "NeoForge"},
        {"quilt", "Quilt"}, {"quilt-loader", "Quilt"}, {"rift", "Rift"}, {"minecraft", "原版"}, {"vanilla", "原版"}, {"原版", "原版"}}

    Public Shared Function ReadTags(platform As ModpackPlatform, categories As JToken, Optional kind As CommunityResourceKind = CommunityResourceKind.Modpack) As String()
        Dim result As New List(Of String)
        If platform = ModpackPlatform.Modrinth Then
            For Each category In OptionalStrings(categories)
                If kind = CommunityResourceKind.ResourcePack AndAlso {"decoration", "mobs", "equipment"}.Contains(category, StringComparer.Ordinal) Then Continue For
                Dim translated As String = Nothing
                If ModrinthTags.TryGetValue(category, translated) AndAlso Not result.Contains(translated) Then result.Add(translated)
            Next
        ElseIf platform = ModpackPlatform.CurseForge Then
            Dim array = TryCast(categories, JArray)
            If array Is Nothing Then Return {}
            Dim ids As New HashSet(Of Integer)
            For Each category In array.OfType(Of JObject)()
                Dim value = category("id"), id As Integer
                If value IsNot Nothing AndAlso value.Type = JTokenType.Integer AndAlso Integer.TryParse(value.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, id) Then ids.Add(id)
            Next
            For Each id In ids.OrderByDescending(Function(value) value)
                Dim translated As String = Nothing
                If CurseForgeTags.TryGetValue(id, translated) Then result.Add(translated)
            Next
        End If
        Return result.ToArray()
    End Function

    Public Shared Function ReadLoaders(project As ModpackProject, data As JObject) As String()
        If project.Platform = ModpackPlatform.Modrinth Then Return NormalizeLoaders(OptionalStrings(data("loaders")).Concat(OptionalStrings(data("categories"))))
        Return NormalizeLoaders(ClientFiles(project, data).SelectMany(Function(file) OptionalStrings(file("gameVersions"))))
    End Function

    Public Shared Function ReadVersions(project As ModpackProject, data As JObject) As String()
        ' PCL ResourceProject unions latestFiles and latestFilesIndexes (#3599).
        ' These are optional display declarations, never archive authorization or
        ' the version binding used by installation. Do not infer a supported range.
        If project.Platform <> ModpackPlatform.CurseForge Then Return {}
        Dim values = ClientFiles(project, data).SelectMany(Function(file) OptionalStrings(file("gameVersions"))).ToList()
        Dim indexes = TryCast(data("latestFilesIndexes"), JArray)
        If indexes IsNot Nothing Then
            For Each entry In indexes.OfType(Of JObject)()
                Dim fileId = entry("fileId"), name = entry("filename"), game = entry("gameVersion"), number As Long
                If fileId Is Nothing OrElse fileId.Type <> JTokenType.Integer OrElse Not Long.TryParse(fileId.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, number) OrElse number <= 0 Then Continue For
                If Not AcceptsFileName(project, name) OrElse game Is Nothing OrElse game.Type <> JTokenType.String OrElse CStr(game).Length > 100 Then Continue For
                values.Add(CStr(game).Trim())
            Next
        End If
        Return values.Where(AddressOf ModpackCatalogClient.IsExactMinecraftVersion).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
    End Function

    Private Shared Function AcceptsFileName(project As ModpackProject, name As JToken) As Boolean
        If name Is Nothing OrElse name.Type <> JTokenType.String Then Return False
        Dim value = CStr(name)
        Return value.Length <= 255 AndAlso value = value.Trim() AndAlso value.IndexOfAny(IO.Path.GetInvalidFileNameChars()) < 0 AndAlso
            CommunityResources.AcceptsFile(project.Kind, value) AndAlso (project.Kind <> CommunityResourceKind.Modpack OrElse value.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
    End Function

    Private Shared Iterator Function ClientFiles(project As ModpackProject, data As JObject) As IEnumerable(Of JObject)
        Dim files = TryCast(data("latestFiles"), JArray)
        If project.Platform <> ModpackPlatform.CurseForge OrElse files Is Nothing Then Return
        For Each file In files.OfType(Of JObject)()
            Dim owner = file("modId"), name = file("fileName"), status = file("fileStatus")
            If owner Is Nothing OrElse (owner.Type <> JTokenType.Integer AndAlso owner.Type <> JTokenType.String) OrElse owner.ToString() <> project.Id Then Continue For
            If Not AcceptsFileName(project, name) Then Continue For
            If Not FlagIs(file, "isServerPack", False, False) OrElse Not FlagIs(file, "isAvailable", True, True) OrElse Not FlagIs(file, "isEarlyAccessContent", False, False) Then Continue For
            If status IsNot Nothing AndAlso status.Type <> JTokenType.Null AndAlso (status.Type <> JTokenType.Integer OrElse status.ToString() <> "4") Then Continue For
            Yield file
        Next
    End Function

    Public Shared Function LoaderText(loaders As IEnumerable(Of String)) As String
        Return String.Join(" / ", NormalizeLoaders(loaders))
    End Function
    Public Shared Function CompactLoaderText(loaders As IEnumerable(Of String), versions As IEnumerable(Of String)) As String
        Dim names = NormalizeLoaders(loaders)
        Dim newest As New Version(9999, 0)
        Dim numeric As New List(Of Version)
        For Each value In If(versions, Enumerable.Empty(Of String)())
            Dim version As Version = Nothing
            If Version.TryParse(value, version) Then numeric.Add(version)
        Next
        If numeric.Count > 0 Then newest = numeric.Max()
        ' PCL ToResourceItem: omit redundant loader combinations when all
        ' mainstream loaders of that era are represented. Tooltip stays complete.
        If names.Length > 1 AndAlso names.Contains("Forge") AndAlso
            (newest < New Version(1, 14) OrElse names.Contains("Fabric")) AndAlso
            (newest < New Version(1, 20) OrElse names.Contains("NeoForge")) Then Return ""
        Return String.Join(" / ", names)
    End Function

    Private Shared Function NormalizeLoaders(loaders As IEnumerable(Of String)) As String()
        Dim accepted As New HashSet(Of String)(StringComparer.Ordinal)
        For Each value In If(loaders, Enumerable.Empty(Of String)())
            Dim canonical As String = Nothing
            If value IsNot Nothing AndAlso value.Length <= 100 AndAlso LoaderAliases.TryGetValue(value.Trim(), canonical) Then accepted.Add(canonical)
        Next
        Return LoaderOrder.Where(Function(loader) accepted.Contains(loader)).ToArray()
    End Function

    Private Shared Iterator Function OptionalStrings(value As JToken) As IEnumerable(Of String)
        Dim array = TryCast(value, JArray)
        If array Is Nothing Then Return
        For Each item In array
            If item.Type = JTokenType.String AndAlso CStr(item).Length <= 100 Then Yield CStr(item).Trim()
        Next
    End Function

    Private Shared Function FlagIs(data As JObject, field As String, expected As Boolean, fallback As Boolean) As Boolean
        Dim value = data(field)
        If value Is Nothing OrElse value.Type = JTokenType.Null Then Return fallback = expected
        Return value.Type = JTokenType.Boolean AndAlso CBool(value) = expected
    End Function
End Class
