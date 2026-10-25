Imports System.IO
Imports Newtonsoft.Json.Linq

' PCL PageSetupSystem / ToolDownloadMod, ToolDownloadTranslateV2 and
' ToolModLocalNameStyle. These are our own settings; never write PCL's settings.
Public NotInheritable Class CommunityResourcePreferences
    Public Property Source As Integer = 2
    Public Property FileNameStyle As Integer = 1
    Public Property LocalModStyle As Integer
    Public Shared Function Read(value As JToken) As CommunityResourcePreferences
        Dim result As New CommunityResourcePreferences, json = TryCast(value, JObject)
        If json Is Nothing Then Return result
        result.Source = ReadIndex(json("source"), 2, 2)
        result.FileNameStyle = ReadIndex(json("fileNameStyle"), 1, 4)
        result.LocalModStyle = ReadIndex(json("localModStyle"), 0, 1)
        Return result
    End Function
    Private Shared Function ReadIndex(value As JToken, fallback As Integer, maximum As Integer) As Integer
        Dim number As Integer
        If value Is Nothing OrElse value.Type <> JTokenType.Integer OrElse Not Integer.TryParse(value.ToString(), number) OrElse number < 0 OrElse number > maximum Then Return fallback
        Return number
    End Function
    Public Function ToJson() As JObject
        Return New JObject From {{"source", Source}, {"fileNameStyle", FileNameStyle}, {"localModStyle", LocalModStyle}}
    End Function
    Public Function SuggestedFileName(project As ModpackProject, release As ModpackRelease) As String
        CommunityResources.ValidateFileName(release.Kind, release.FileName)
        Dim original = If(release.Kind = CommunityResourceKind.Mod, release.FileName.Replace("~", "-"), release.FileName)
        Dim translated = ResourceProjectNames.GetTranslation(project)
        If translated Is Nothing OrElse FileNameStyle = 4 Then Return original
        Dim name = ResourceProjectNames.GetTitle(project).Title
        Dim fromChars = "\/:|<>*?" & ChrW(34), toChars = "＼／：｜＜＞＊？" & ChrW(&HFF02)
        For i = 0 To fromChars.Length - 1 : name = name.Replace(fromChars(i), toChars(i)) : Next
        name = New String(name.Where(Function(ch) Not Char.IsControl(ch)).ToArray()).Trim().TrimEnd("."c)
        Dim ext = Path.GetExtension(original), stem = Path.GetFileNameWithoutExtension(original)
        Dim room = 255 - original.Length - If(FileNameStyle <= 1, 3, 1)
        If room <= 0 Then Return original
        If name.Length > room Then
            If Char.IsHighSurrogate(name(room - 1)) Then room -= 1
            name = name.Substring(0, room)
        End If
        If name = "" Then Return original
        Dim result As String
        Select Case FileNameStyle
            Case 0 : result = "【" & name & "】" & stem & ext
            Case 1 : result = "[" & name & "] " & stem & ext
            Case 2 : result = name & "-" & stem & ext
            Case 3 : result = stem & "-" & name & ext
            Case Else : Return original
        End Select
        CommunityResources.ValidateFileName(release.Kind, result)
        Return result
    End Function
End Class
