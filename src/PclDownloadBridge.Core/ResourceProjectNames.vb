Imports System.Globalization
Imports System.IO
Imports System.Text.RegularExpressions

' Fixed PCL WikiEntry.cs / ResourceProject.GetControlTitle. Platform slug is
' the only lookup key; translated text never replaces install/project identity.
Public NotInheritable Class ResourceProjectTitle
    Public ReadOnly Property Title As String
    Public ReadOnly Property Subtitle As String
    Friend Sub New(title As String, subtitle As String)
        Me.Title = title : Me.Subtitle = subtitle
    End Sub
End Class

Public NotInheritable Class ResourceProjectNames
    Private Sub New()
    End Sub
    Private Shared ReadOnly Entries As New Lazy(Of Dictionary(Of String, String))(AddressOf ReadEntries)
    Private Shared Function ReadEntries() As Dictionary(Of String, String)
        Dim result As New Dictionary(Of String, String)(StringComparer.Ordinal)
        Using stream = GetType(ResourceProjectNames).Assembly.GetManifestResourceStream("PclWikiEntries.txt"), reader As New StreamReader(stream, System.Text.Encoding.UTF8, True)
            Dim lines = reader.ReadToEnd().Split(ChrW(10))
            ' Final nonempty line encodes popularity, not a project.
            Dim last = lines.Length - 1
            While last >= 0 AndAlso String.IsNullOrWhiteSpace(lines(last)) : last -= 1 : End While
            For i = 0 To last - 1
                For Each entry In lines(i).TrimEnd(ChrW(13)).Split("¨"c)
                    Dim parts = entry.Split("|"c)
                    If parts.Length < 2 OrElse parts(0) = "" Then Continue For
                    Dim slugs = parts(0).Split("@"c), name = parts(parts.Length - 1)
                    Dim englishSlug = slugs.FirstOrDefault(Function(slug) slug <> "")
                    If englishSlug Is Nothing OrElse name = "" Then Continue For
                    name = name.Replace("*", " (" & CultureInfo.InvariantCulture.TextInfo.ToTitleCase(englishSlug.Replace("-", " ")) & ")")
                    If slugs(0) <> "" Then Add(result, ModpackPlatform.CurseForge, slugs(0), name)
                    If slugs.Length = 2 Then Add(result, ModpackPlatform.Modrinth, If(slugs(1) = "", slugs(0), slugs(1)), name)
                Next
            Next
        End Using
        Return result
    End Function
    Private Shared Sub Add(result As Dictionary(Of String, String), platform As ModpackPlatform, slug As String, name As String)
        Dim key = platform.ToString() & ":" & slug
        If Not result.ContainsKey(key) Then result.Add(key, name)
    End Sub
    Public Shared Function GetTitle(project As ModpackProject) As ResourceProjectTitle
        If project Is Nothing Then Return New ResourceProjectTitle("", "")
        Dim raw = If(project.Name, ""), translated As String = Nothing
        If (project.Kind = CommunityResourceKind.Mod OrElse project.Kind = CommunityResourceKind.DataPack) AndAlso Not String.IsNullOrEmpty(project.Slug) Then
            Entries.Value.TryGetValue(project.Platform.ToString() & ":" & project.Slug, translated)
        End If
        If translated IsNot Nothing AndAlso translated <> raw Then
            Dim opening = translated.IndexOf(" (", StringComparison.Ordinal), closing = translated.LastIndexOf(")"c)
            If opening > 0 AndAlso closing > opening Then
                Dim title = translated.Substring(0, opening), english = translated.Substring(opening + 2, closing - opening - 2)
                Dim suffix = translated.Substring(closing + 1).Trim()
                If suffix.StartsWith("- ", StringComparison.Ordinal) Then english &= "，" & suffix.Substring(2)
                Return New ResourceProjectTitle(title, english)
            End If
            Return New ResourceProjectTitle(translated, raw)
        End If
        ' PCL separates abbreviations and loader editions from untranslated names.
        Dim parts = Regex.Split(raw, " \| | - |[()\[\]{}]").Select(Function(value) value.Trim(" "c, "/"c, "\"c)).Where(Function(value) value <> "").ToArray()
        Dim normal As New List(Of String), subtitles As New List(Of String)
        For Each part In parts
            If (part = part.ToUpperInvariant() AndAlso part <> "FPS" AndAlso part <> "HUD") OrElse
                Regex.IsMatch(part, "\A(?:forge|neoforge|fabric|quilt)(?:[ /-]*(?:forge|neoforge|fabric|quilt))*\z", RegexOptions.IgnoreCase Or RegexOptions.CultureInvariant) Then
                subtitles.Add(part)
            Else
                normal.Add(part)
            End If
        Next
        If normal.Count = 0 OrElse subtitles.Count = 0 Then Return New ResourceProjectTitle(raw, "")
        Return New ResourceProjectTitle(String.Join(" - ", normal), String.Join("，", subtitles.Distinct(StringComparer.Ordinal)))
    End Function
End Class
