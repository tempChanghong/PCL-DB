Imports System.Text.RegularExpressions

' PCL 5ef81e4 ModDownload.DlNeoForgeListEntry: keep Maven identity separate
' from the Minecraft version and the numeric loader version used for sorting.
Friend NotInheritable Class NeoForgeRelease
    Friend Const Pattern As String = "(?:1\.20\.1-)?\d+\.[A-Za-z0-9_]+\.\d+(?:\.\d+)?(?:-(?:beta|alpha)(?:\.\d+)?)?(?:\+snapshot-\d+)?"
    Friend Property ApiName As String
    Friend Property VersionName As String
    Friend Property Minecraft As String
    Friend Property NumericVersion As Version
    Friend Property Stable As Boolean

    Friend Shared Function Parse(value As String, legacy As Boolean) As NeoForgeRelease
        If value Is Nothing OrElse Not Regex.IsMatch(value, "\A" & Pattern & "\z", RegexOptions.IgnoreCase) Then Return Nothing
        Dim display = If(value.StartsWith("1.20.1-", StringComparison.Ordinal), value.Substring("1.20.1-".Length), value)
        If value = "47.1.82" Then Return Nothing ' PCL excludes this exact API name; display names are not Maven identities.
        Dim core = display.Split("-"c, "+"c)(0), parts = core.Split("."c)
        Dim numeric As Version = Nothing, game As String
        If Not legacy AndAlso value.StartsWith("0.", StringComparison.Ordinal) Then
            Dim build As Integer
            If parts.Length < 3 OrElse Not Integer.TryParse(parts.Last(), build) Then Return Nothing
            numeric = New Version(0, 0, build)
            game = parts(1)
        Else
            If Not Version.TryParse(core, numeric) OrElse numeric.Build < 0 Then Return Nothing
            If legacy Then
                If numeric.Revision >= 0 Then Return Nothing
                numeric = New Version(19, numeric.Major, numeric.Minor, numeric.Build)
                game = "1.20.1"
            Else
                game = If(numeric.Major >= 24, numeric.Major & "." & numeric.Minor & "." & numeric.Build, "1." & numeric.Major & "." & numeric.Minor)
                If game.EndsWith(".0", StringComparison.Ordinal) Then game = game.Substring(0, game.Length - 2)
                Dim plus = value.IndexOf("+"c)
                If plus >= 0 Then game &= "-" & value.Substring(plus + 1)
            End If
        End If
        Return New NeoForgeRelease With {.ApiName = value, .VersionName = display, .Minecraft = game, .NumericVersion = numeric,
            .Stable = value.IndexOf("alpha", StringComparison.OrdinalIgnoreCase) < 0 AndAlso value.IndexOf("beta", StringComparison.OrdinalIgnoreCase) < 0 AndAlso value.IndexOf("+snapshot-", StringComparison.OrdinalIgnoreCase) < 0}
    End Function
End Class
