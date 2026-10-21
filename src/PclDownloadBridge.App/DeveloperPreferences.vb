Imports System.Security.Cryptography
Imports System.Text
Imports Newtonsoft.Json.Linq

Friend Class DeveloperPreferences
    Public Property Enabled As Boolean
    Public Property ShowDiagnostics As Boolean
    Friend Shared Function Read(token As JToken) As DeveloperPreferences
        Dim result As New DeveloperPreferences, value = TryCast(token, JObject)
        If value Is Nothing Then Return result
        If value("enabled")?.Type = JTokenType.Boolean Then result.Enabled = CBool(value("enabled"))
        If value("showDiagnostics")?.Type = JTokenType.Boolean Then result.ShowDiagnostics = CBool(value("showDiagnostics"))
        Return result
    End Function
    Friend Function ToJson() As JObject
        Return New JObject From {{"enabled", Enabled}, {"showDiagnostics", ShowDiagnostics}}
    End Function
End Class

Friend Module DeveloperAccess
    Private Const VerificationSha256 As String = "C2A91F926BD7FE631312F315D8D3128275BC49CC7062ADB33A963C6FAE078449"
    Friend Function Verify(value As String) As Boolean
        Return MatchesDigest(value, VerificationSha256)
    End Function
    Friend Function MatchesDigest(value As String, digest As String) As Boolean
        If String.IsNullOrEmpty(value) OrElse value.Length > 128 OrElse digest Is Nothing OrElse digest.Length <> 64 Then Return False
        Dim expected(31) As Byte
        For index = 0 To 31
            If Not Byte.TryParse(digest.Substring(index * 2, 2), Globalization.NumberStyles.HexNumber, Globalization.CultureInfo.InvariantCulture, expected(index)) Then Return False
        Next
        Using sha = SHA256.Create()
            Dim actual = sha.ComputeHash(Encoding.UTF8.GetBytes(value)), difference As Integer
            For index = 0 To 31
                difference = difference Or (actual(index) Xor expected(index))
            Next
            Return difference = 0
        End Using
    End Function
End Module
