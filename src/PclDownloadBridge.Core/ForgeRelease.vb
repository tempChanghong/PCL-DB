Imports System.Text.RegularExpressions

' PCL 5ef81e4 ModDownload.DlForgeVersionEntry: display VersionName and file
' FileVersion are different identities; historical branches are repaired here.
Friend NotInheritable Class ForgeRelease
    Friend Property VersionName As String
    Friend Property Branch As String
    Friend ReadOnly Property FileVersion As String
        Get
            Return VersionName & If(Branch Is Nothing, "", "-" & Branch)
        End Get
    End Property

    Friend Shared Function Parse(value As String, branch As String, mc As String) As ForgeRelease
        If String.IsNullOrWhiteSpace(value) Then Return Nothing
        Dim split = value.IndexOf("-"c), embedded As String = Nothing
        If split >= 0 Then
            embedded = value.Substring(split + 1)
            value = value.Substring(0, split)
        End If
        branch = If(String.IsNullOrWhiteSpace(branch), Nothing, branch.Trim())
        If embedded IsNot Nothing Then
            If branch IsNot Nothing AndAlso branch <> embedded Then Return Nothing
            branch = embedded
        End If
        Dim numeric As Version = Nothing
        If Not Regex.IsMatch(value, "\A[0-9]+(?:\.[0-9]+){1,3}\z") OrElse Not Version.TryParse(value, numeric) Then Return Nothing
        If {"11.15.1.2318", "11.15.1.1902", "11.15.1.1890"}.Contains(value) Then branch = "1.8.9"
        If branch Is Nothing AndAlso mc = "1.7.10" AndAlso numeric.Revision >= 1300 Then branch = "1.7.10"
        If branch IsNot Nothing AndAlso Not Regex.IsMatch(branch, "\A[0-9A-Za-z_.-]+\z") Then Return Nothing
        Return New ForgeRelease With {.VersionName = value, .Branch = branch}
    End Function
End Class
