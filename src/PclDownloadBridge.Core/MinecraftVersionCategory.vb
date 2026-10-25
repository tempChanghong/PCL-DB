Imports System.Globalization
Imports System.Text.RegularExpressions
Imports Newtonsoft.Json.Linq

' PCL McVersion.IsFoolName / jsonType and McInstance.Load. Presentation only;
' no cached State, launcher configuration or isolation setting is rewritten.
Public NotInheritable Class MinecraftVersionCategory
    Private Sub New()
    End Sub
    Public Shared Function IsAprilFools(id As String) As Boolean
        Dim name = If(id, "").ToLowerInvariant()
        Return name.StartsWith("2.0", StringComparison.Ordinal) OrElse name.StartsWith("20w14inf", StringComparison.Ordinal) OrElse
            {"15w14a", "1.rv-pre1", "3d shareware v1.34", "20w14∞", "22w13oneblockatatime", "23w13a_or_b", "24w14potato", "25w14craftmine", "26w14a"}.Contains(name)
    End Function
    Public Shared Function Classify(id As String, metadata As JObject) As String
        Dim typeToken = metadata?("type"), kind = If(typeToken?.Type = JTokenType.String, CStr(typeToken), "")
        If IsAprilFools(id) OrElse kind = "fool" Then Return "special"
        If kind = "old_alpha" OrElse kind = "old_beta" Then Return kind
        If Regex.IsMatch(If(id, ""), "\A(?:rd-|in-|inf-|c0\.|[ab]\d)", RegexOptions.IgnoreCase Or RegexOptions.CultureInvariant) Then Return "old_alpha"
        If kind = "snapshot" Then
            Dim released As DateTimeOffset, time = metadata?("releaseTime")
            Dim stamp = If(time?.Type = JTokenType.Date, time.ToString(Newtonsoft.Json.Formatting.None).Trim(""""c), time?.ToString())
            If DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, released) Then
                Dim fixedTime = released.ToUniversalTime().AddHours(2)
                If fixedTime.Month = 4 AndAlso fixedTime.Day = 1 Then Return "special"
            End If
            Return "snapshot"
        End If
        If kind = "pending" OrElse Regex.IsMatch(If(id, ""), "\A\d{2}w\d{2}[a-z]|(?:-pre\d| pre-release | release candidate |-rc\d|experimental)", RegexOptions.IgnoreCase Or RegexOptions.CultureInvariant) Then Return "snapshot"
        Return If(kind = "release", "release", "unknown")
    End Function
    Public Shared Function Label(kind As String) As String
        Select Case kind
            Case "special" : Return "愚人节版本"
            Case "old_alpha", "old_beta" : Return "远古版本"
            Case "snapshot" : Return "快照版"
            Case "release" : Return "正式版"
            Case Else : Return "原版"
        End Select
    End Function
End Class
