Imports System.Globalization
Imports Newtonsoft.Json.Linq

Friend Class AppearancePreferences
    Friend Shared ReadOnly PublicThemes As String() = {"blue", "lightblue", "deepblue"}
    Friend Shared ReadOnly ThemeIds As String() = {"blue", "lightblue", "deepblue", "cyan", "green", "yellow", "brown", "black", "colorful", "custom"}
    Friend Shared ReadOnly ThemeNames As String() = {"龙猫蓝", "浅蓝", "深蓝", "甜柠青", "小草绿", "菠萝黄", "橡木棕", "玄素黑", "彩色渐变", "自定义"}
    Public Property Theme As String = "blue"
    Public Property PublicTheme As String = "blue"
    Public Property DarkMode As Boolean
    Public Property Transparency As Integer = 600
    Public Property Hue As Double = 180
    Public Property Saturation As Double = 80
    Public Property Lightness As Double
    Public Property ColorfulBackground As Boolean = True
    Public Property TitleTexture As Boolean = True

    Friend Shared Function Read(token As JToken) As AppearancePreferences
        Dim value = TryCast(token, JObject)
        Dim result As New AppearancePreferences
        If value Is Nothing Then Return result
        Dim theme = value("theme")
        If theme?.Type = JTokenType.String AndAlso ThemeIds.Contains(CStr(theme)) Then result.Theme = CStr(theme)
        If PublicThemes.Contains(result.Theme) Then result.PublicTheme = result.Theme
        Dim publicChoice = value("publicTheme")
        If publicChoice?.Type = JTokenType.String AndAlso PublicThemes.Contains(CStr(publicChoice)) Then result.PublicTheme = CStr(publicChoice)
        If value("darkMode")?.Type = JTokenType.Boolean Then result.DarkMode = CBool(value("darkMode"))
        result.Transparency = CInt(Number(value, "transparency", 600, 0, 600))
        result.Hue = Number(value, "hue", 180, 0, 360)
        result.Saturation = Number(value, "saturation", 80, 0, 100)
        result.Lightness = Number(value, "lightness", 0, -20, 20)
        If value("colorfulBackground")?.Type = JTokenType.Boolean Then result.ColorfulBackground = CBool(value("colorfulBackground"))
        If value("titleTexture")?.Type = JTokenType.Boolean Then result.TitleTexture = CBool(value("titleTexture"))
        Return result
    End Function

    Friend Function Effective(unlocked As Boolean) As AppearancePreferences
        Dim result = Read(ToJson())
        If Not unlocked AndAlso Not PublicThemes.Contains(result.Theme) Then result.Theme = result.PublicTheme
        Return result
    End Function

    Private Shared Function Number(value As JObject, key As String, fallback As Double, minimum As Double, maximum As Double) As Double
        Dim token = value(key)
        If token Is Nothing OrElse (token.Type <> JTokenType.Integer AndAlso token.Type <> JTokenType.Float) Then Return fallback
        Dim parsed As Double
        If Not Double.TryParse(CStr(token), NumberStyles.Float, CultureInfo.InvariantCulture, parsed) OrElse Double.IsNaN(parsed) OrElse Double.IsInfinity(parsed) Then Return fallback
        Return Math.Max(minimum, Math.Min(maximum, parsed))
    End Function

    Friend Function ToJson() As JObject
        Return New JObject From {{"theme", Theme}, {"publicTheme", PublicTheme}, {"darkMode", DarkMode}, {"transparency", Transparency}, {"hue", Hue}, {"saturation", Saturation}, {"lightness", Lightness}, {"colorfulBackground", ColorfulBackground}, {"titleTexture", TitleTexture}}
    End Function

    Friend Function Merge(existing As JObject) As JObject
        Return DownloadSettingsFile.MergeValues(existing, New JObject From {{"appearance", ToJson()}})
    End Function
End Class
