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

    Public Shared Function VersionText(versions As IEnumerable(Of String)) As String
        Dim values = If(versions, Enumerable.Empty(Of String)()).Where(Function(value) Not String.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        If values.Length = 0 Then Return "版本未知"
        Dim families As New List(Of Version)
        For Each value In values
            ' The catalogue has no complete global drop list. Use PCL's
            ' AllDrops=Nothing fallback: list known families, never infer a + range.
            Dim match = Regex.Match(value, "\A(?<major>1|[2-9][0-9])\.(?<minor>[0-9]{1,3})(?:\.[0-9]{1,3})?\z", RegexOptions.CultureInvariant)
            If match.Success Then families.Add(New Version(Integer.Parse(match.Groups("major").Value, CultureInfo.InvariantCulture), Integer.Parse(match.Groups("minor").Value, CultureInfo.InvariantCulture)))
        Next
        Dim ordered = families.Distinct().OrderByDescending(Function(value) value).ToArray()
        If ordered.Length = 0 Then Return If(values.All(Function(value) Regex.IsMatch(value, "\A[0-9]{2}w[0-9]{2}[a-z]\z", RegexOptions.IgnoreCase Or RegexOptions.CultureInvariant)), "仅快照版本", "其他版本")
        ' Like PCL, omit pre-1.12 families when modern families are available.
        If ordered(0) >= New Version(1, 12) Then ordered = ordered.Where(Function(value) value >= New Version(1, 12)).ToArray()
        Return String.Join(", ", ordered.Select(Function(value) value.ToString(2)))
    End Function
End Class
