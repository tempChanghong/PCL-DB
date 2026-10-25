Imports System.Globalization
Imports System.Text.RegularExpressions

' PCL 5ef81e4 ResourceProject.ToResourceItem. Keep project statistics distinct
' from per-file statistics: project downloads use floor(10k) / rounded(100m).
Public NotInheritable Class ModpackProjectStatistics
    Private Sub New()
    End Sub

    Public Shared Function DownloadText(count As Long) As String
        If count < 0 Then Return "未知"
        If count > 100000000 Then Return Math.Round(CDec(count) / 100000000D, 2, MidpointRounding.ToEven).ToString("0.##", CultureInfo.InvariantCulture) & " 亿"
        If count > 100000 Then Return Decimal.Floor(CDec(count) / 10000D).ToString(CultureInfo.InvariantCulture) & " 万"
        Return count.ToString(CultureInfo.InvariantCulture)
    End Function

    Public Shared Function VersionText(versions As IEnumerable(Of String), Optional knownVersions As IEnumerable(Of String) = Nothing) As String
        Dim values = If(versions, Enumerable.Empty(Of String)()).Where(Function(value) Not String.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        If values.Length = 0 Then Return "版本未知"
        Dim families = values.Select(AddressOf Family).Where(Function(value) value IsNot Nothing)
        Dim ordered = families.Distinct().OrderByDescending(Function(value) value).ToArray()
        If ordered.Length = 0 Then Return If(values.All(Function(value) Regex.IsMatch(value, "\A[0-9]{2}w[0-9]{2}[a-z]\z", RegexOptions.IgnoreCase Or RegexOptions.CultureInvariant)), "仅快照版本", "其他版本")
        Dim known = If(knownVersions, Enumerable.Empty(Of String)()).Select(AddressOf Family).Where(Function(value) value IsNot Nothing).Distinct().OrderByDescending(Function(value) value).ToList()
        If known.Count > 0 Then Return Ranges(ordered, known)
        ' PCL's AllDrops=Nothing fallback: no trustworthy global family list.
        ' Like PCL, omit pre-1.12 families when modern families are available.
        If ordered(0) >= New Version(1, 12) Then ordered = ordered.Where(Function(value) value >= New Version(1, 12)).ToArray()
        Return String.Join(", ", ordered.Select(Function(value) value.ToString(2)))
    End Function
    Private Shared Function Family(value As String) As Version
        If value Is Nothing Then Return Nothing
        Dim match = Regex.Match(value, "\A(?<major>1|[2-9][0-9])\.(?<minor>[0-9]{1,3})(?:\.[0-9]{1,3})?\z", RegexOptions.CultureInvariant)
        If Not match.Success Then Return Nothing
        Return New Version(Integer.Parse(match.Groups("major").Value, CultureInfo.InvariantCulture), Integer.Parse(match.Groups("minor").Value, CultureInfo.InvariantCulture))
    End Function
    Private Shared Function Ranges(values As Version(), known As List(Of Version)) As String
        Dim segments As New List(Of String), threshold As New Version(1, 12), old = False, i = 0
        While i < values.Length
            Dim first = values(i), last = first, startIndex = known.IndexOf(first), count = 1
            If first < threshold Then
                If segments.Count > 0 AndAlso Not old Then Exit While
                old = True
            End If
            While i + 1 < values.Length AndAlso startIndex >= 0 AndAlso known.IndexOf(values(i + 1)) = startIndex + count
                i += 1 : last = values(i) : count += 1
            End While
            If count = 1 Then
                segments.Add(first.ToString(2))
            ElseIf startIndex = 0 Then
                If last < threshold Then Return "全版本"
                segments.Add(last.ToString(2) & "+")
            ElseIf last < threshold Then
                segments.Add(first.ToString(2) & "-") : Exit While
            ElseIf count = 2 Then
                segments.Add(first.ToString(2) & ", " & last.ToString(2))
            Else
                segments.Add(first.ToString(2) & "~" & last.ToString(2))
            End If
            i += 1
        End While
        Return String.Join(", ", segments)
    End Function
End Class
