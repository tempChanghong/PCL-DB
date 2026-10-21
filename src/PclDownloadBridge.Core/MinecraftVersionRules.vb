Imports System.Globalization
Imports System.Text.RegularExpressions

' Adapted from fixed PCL 5ef81e4 McVersion.NameToVersion / VersionToDrop.
Public NotInheritable Class MinecraftVersionRules
    Private Sub New()
    End Sub

    ' PCL McVersion.NameToVersion / VersionToDrop; card visibility uses release families,
    ' while component compatibility uses the full pre-release-aware ordering.
    Public Shared Function ReleaseFamily(value As String) As Integer
        If value Is Nothing Then Return 209
        Dim name = value.ToLowerInvariant().Replace("_unobfuscated", "").Replace(" unobfuscated", "")
        If name.StartsWith("2.0", StringComparison.Ordinal) Then
            name = "1.5.1"
        ElseIf name = "15w14a" Then
            name = "1.8.3"
        ElseIf name.Contains(".rv-pre") Then
            name = "1.9.2"
        ElseIf name.Contains("shareware") Then
            name = "1.13.2"
        ElseIf name.StartsWith("20w14", StringComparison.Ordinal) AndAlso name <> "20w14a" Then
            name = "1.15.2"
        ElseIf name.Contains("oneblockatatime") Then
            name = "1.18.2"
        ElseIf name.Contains("23w13a") Then
            name = "1.19.4"
        ElseIf name = "24w14potato" Then
            name = "1.20.4"
        ElseIf name = "25w14craftmine" Then
            name = "1.21.4"
        ElseIf name = "26w14a" Then
            name = "26.1.1"
        End If
        Dim parts = name.Split({" "c, "_"c, "-"c, "."c}, StringSplitOptions.None)
        Dim drop As Long
        If name.StartsWith("1.", StringComparison.Ordinal) Then
            drop = CLng(VersionSegment(parts, 1)) * 10
        ElseIf Regex.IsMatch(name, "^[2-9][0-9]\.") Then
            drop = CLng(VersionSegment(parts, 0)) * 10 + VersionSegment(parts, 1)
        Else
            Return 209
        End If
        Return If(drop > Integer.MaxValue, 209, CInt(drop))
    End Function

    Private Shared Function VersionSegment(parts As String(), index As Integer) As Integer
        If index >= parts.Length Then Return 0
        Dim number As Integer
        Return If(Integer.TryParse(Regex.Match(parts(index), "^\d+").Value, NumberStyles.Integer, CultureInfo.InvariantCulture, number), number, 0)
    End Function

End Class
