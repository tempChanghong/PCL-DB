Imports System.Globalization
Imports System.IO
Imports System.Text.RegularExpressions
Imports Newtonsoft.Json.Linq

' PCL 5ef81e4 McLibListGetWithJson: rules first, ordinary/native identities,
' then compare the saved file's parent folder using MCUtils.CompareVersion.
Friend NotInheritable Class PclLibrarySelection
    Private Sub New()
    End Sub

    Friend Shared Function Identity(libraryInfo As JObject, native As Boolean) As String
        Dim coordinate = CStr(libraryInfo("name"))
        MinecraftInstaller.MavenPath(coordinate)
        Dim artifact = coordinate.Split("@"c)
        Dim parts = artifact(0).Split(":"c)
        ' Retain this tool's existing classifier/extension separation.
        Return parts(0) & ":" & parts(1) & ":" & If(parts.Length > 3, parts(3), "") & "@" & If(artifact.Length > 1, artifact(1), "jar") & If(native, ":native", ":java")
    End Function

    Friend Shared Function ProfileVersion(libraryInfo As JObject) As String
        Dim classifier = CStr(libraryInfo.SelectToken("natives.windows"))
        Dim data As JObject
        If libraryInfo("natives") Is Nothing Then
            data = TryCast(libraryInfo.SelectToken("downloads.artifact"), JObject)
        Else
            If classifier IsNot Nothing Then classifier = classifier.Replace("${arch}", If(Environment.Is64BitOperatingSystem, "64", "32"))
            data = If(classifier Is Nothing, Nothing, TryCast(libraryInfo("downloads")?("classifiers")?(classifier), JObject))
        End If
        Dim relative = CStr(data?("path"))
        If relative Is Nothing Then relative = MinecraftInstaller.MavenPath(CStr(libraryInfo("name")), classifier)
        Return FileVersion("libraries/" & relative)
    End Function

    Private Shared Function FileVersion(relative As String) As String
        Return Path.GetFileName(Path.GetDirectoryName(relative.Replace("/"c, Path.DirectorySeparatorChar)))
    End Function

    Friend Shared Sub Add(selected As Dictionary(Of String, List(Of DownloadFile)), libraryInfo As JObject, item As DownloadFile, native As Boolean)
        Dim key = Identity(libraryInfo, native)
        Dim existing As List(Of DownloadFile) = Nothing
        If Not selected.TryGetValue(key, existing) Then
            selected.Add(key, New List(Of DownloadFile) From {item})
        ElseIf String.Equals(Path.GetFullPath(existing(0).RelativePath), Path.GetFullPath(item.RelativePath), StringComparison.OrdinalIgnoreCase) Then
            ' One actual file must still merge all compatible sizes/hashes and reject
            ' conflicts through DownloadPlan, rather than losing a stronger checker.
            existing.Add(item)
        ElseIf CompareVersion(FileVersion(item.RelativePath), FileVersion(existing(0).RelativePath)) >= 0 Then
            selected(key) = New List(Of DownloadFile) From {item}
        End If
    End Sub

    Friend Shared Function CompareVersion(left As String, right As String) As Integer
        If left = right Then Return 0
        If left = "未知版本" OrElse right = "未知版本" Then Return If(left = "未知版本", 1, -1)
        left = left.ToLowerInvariant() : right = right.ToLowerInvariant()
        Dim leftParts = Regex.Matches(left.Replace("快照", "snapshot").Replace("预览版", "pre"), "[a-z]+|[0-9]+").Cast(Of Match)().Select(Function(item) item.Value).ToList()
        Dim rightParts = Regex.Matches(right.Replace("快照", "snapshot").Replace("预览版", "pre"), "[a-z]+|[0-9]+").Cast(Of Match)().Select(Function(item) item.Value).ToList()
        Dim index As Integer
        Do
            If index >= leftParts.Count AndAlso index >= rightParts.Count Then Return String.Compare(left, right, StringComparison.Ordinal)
            Dim leftValue = If(index < leftParts.Count, leftParts(index), "0")
            Dim rightValue = If(index < rightParts.Count, rightParts(index), "0")
            index += 1
            If leftValue = rightValue Then Continue Do
            Dim leftNumber = VersionToken(leftValue), rightNumber = VersionToken(rightValue)
            If leftNumber = 0 AndAlso rightNumber = 0 Then Return String.Compare(leftValue, rightValue, StringComparison.Ordinal)
            If leftNumber <> rightNumber Then Return leftNumber.CompareTo(rightNumber)
        Loop
    End Function

    Private Shared Function VersionToken(value As String) As Integer
        Select Case value
            Case "rc" : Return -1
            Case "pre" : Return -2
            Case "snapshot" : Return -3
            Case "experimental" : Return -4
        End Select
        Dim number As Integer
        Return If(Integer.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, number), number, 0)
    End Function
End Class
