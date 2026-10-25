Imports System.IO
Imports Newtonsoft.Json.Linq

' PCL 5ef81e4 ResourceTypes / ResourceSearcher / ResourceVersion. The existing
' modpack API remains the default; resource context belongs to each request.
Public Enum CommunityResourceKind
    Modpack
    [Mod]
    DataPack
    ResourcePack
    Shader
    Any
End Enum

Public NotInheritable Class ResourceDependency
    Public Property ProjectId As String
    Public Property VersionId As String
End Class

Public NotInheritable Class CommunityResources
    Private Sub New()
    End Sub
    Public Shared Function Name(kind As CommunityResourceKind) As String
        Select Case kind
            Case CommunityResourceKind.Modpack : Return "整合包"
            Case CommunityResourceKind.Mod : Return "Mod"
            Case CommunityResourceKind.DataPack : Return "数据包"
            Case CommunityResourceKind.ResourcePack : Return "资源包"
            Case CommunityResourceKind.Shader : Return "光影包"
            Case Else : Throw New ArgumentException("资源类型无效。")
        End Select
    End Function
    Public Shared Function ClassId(kind As CommunityResourceKind) As Integer
        Select Case kind
            Case CommunityResourceKind.Modpack : Return 4471
            Case CommunityResourceKind.Mod : Return 6
            Case CommunityResourceKind.DataPack : Return 6945
            Case CommunityResourceKind.ResourcePack : Return 12
            Case CommunityResourceKind.Shader : Return 6552
            Case Else : Throw New ArgumentException("资源类型无效。")
        End Select
    End Function
    Public Shared Function ProjectType(kind As CommunityResourceKind) As String
        Name(kind)
        Return kind.ToString().ToLowerInvariant()
    End Function
    Friend Shared Function Matches(platform As ModpackPlatform, data As JObject, kind As CommunityResourceKind) As Boolean
        If kind = CommunityResourceKind.Any Then Return Enumerable.Range(0, 5).Any(Function(value) Matches(platform, data, CType(value, CommunityResourceKind)))
        If platform = ModpackPlatform.CurseForge Then Return CStr(data("classId")) = ClassId(kind).ToString(Globalization.CultureInfo.InvariantCulture)
        Dim types = TryCast(data("all_project_types"), JArray)
        If types IsNot Nothing Then Return types.Values(Of String)().Contains(ProjectType(kind), StringComparer.Ordinal)
        If CStr(data("project_type")) = ProjectType(kind) Then Return True
        ' v2 project metadata may report a datapack/mod hybrid only as "mod".
        Return kind = CommunityResourceKind.DataPack AndAlso CStr(data("project_type")) = "mod"
    End Function
    Friend Shared Function Infer(platform As ModpackPlatform, data As JObject) As CommunityResourceKind
        For Each kind In {CommunityResourceKind.Modpack, CommunityResourceKind.Mod, CommunityResourceKind.ResourcePack, CommunityResourceKind.Shader, CommunityResourceKind.DataPack}
            If Matches(platform, data, kind) Then Return kind
        Next
        Throw New InvalidDataException("暂不支持这个资源类型。")
    End Function
    Friend Shared Function FileKind(project As ModpackProject, loaders As String()) As CommunityResourceKind?
        If project.Platform <> ModpackPlatform.Modrinth OrElse (project.Kind <> CommunityResourceKind.Mod AndAlso project.Kind <> CommunityResourceKind.DataPack) Then Return project.Kind
        ' PCL explicitly excludes plugin builds from mixed projects.
        Dim mods = loaders.Any(Function(value) {"fabric", "forge", "neoforge", "quilt", "liteloader", "rift", "modloader"}.Contains(value, StringComparer.OrdinalIgnoreCase))
        If project.Kind = CommunityResourceKind.DataPack Then Return If(loaders.Contains("datapack", StringComparer.OrdinalIgnoreCase), CType(CommunityResourceKind.DataPack, CommunityResourceKind?), Nothing)
        Return If(mods, CType(CommunityResourceKind.Mod, CommunityResourceKind?), Nothing)
    End Function
    Public Shared Function AcceptsFile(kind As CommunityResourceKind, fileName As String) As Boolean
        Dim extension = Path.GetExtension(fileName)
        Select Case kind
            Case CommunityResourceKind.Modpack : Return {".zip", ".mrpack"}.Contains(extension, StringComparer.OrdinalIgnoreCase)
            Case CommunityResourceKind.Mod : Return {".jar", ".litemod"}.Contains(extension, StringComparer.OrdinalIgnoreCase)
            Case CommunityResourceKind.DataPack, CommunityResourceKind.ResourcePack, CommunityResourceKind.Shader : Return extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
            Case Else : Return False
        End Select
    End Function
    Public Shared Sub ValidateFileName(kind As CommunityResourceKind, fileName As String)
        If String.IsNullOrWhiteSpace(fileName) OrElse fileName.Length > 255 OrElse fileName <> fileName.Trim() OrElse fileName <> Path.GetFileName(fileName) OrElse
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 OrElse Not AcceptsFile(kind, fileName) OrElse
            Text.RegularExpressions.Regex.IsMatch(fileName, "\A(?:CON|PRN|AUX|NUL|CLOCK\$|CONIN\$|CONOUT\$|COM[0-9]|LPT[0-9])(?:\.|\z)", Text.RegularExpressions.RegexOptions.IgnoreCase Or Text.RegularExpressions.RegexOptions.CultureInvariant) Then Throw New InvalidDataException("资源文件名无效。")
    End Sub
End Class
