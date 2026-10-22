Imports System.IO
Imports System.IO.Compression

Public Enum ModpackFormat
    CurseForge
    Modrinth
End Enum
Public Module ModpackFormats
    ' PCL detects the manifest inside the archive, not merely its extension.
    Public Function Detect(path As String) As ModpackFormat
        Using input As New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), archive As New ZipArchive(input, ZipArchiveMode.Read)
            If archive.Entries.Count > 20000 Then Throw New InvalidDataException("整合包条目过多。")
            Dim names = archive.Entries.Select(Function(entry) entry.FullName.Replace("\"c, "/"c)).ToArray()
            Dim roots = names.Where(Function(name) {"manifest.json", "modrinth.index.json"}.Contains(name, StringComparer.Ordinal)).ToArray()
            ' PCL gives the root manifest precedence over one-folder candidates.
            ' An ordinary overrides/manifest.json is not another pack identity.
            Dim manifests = If(roots.Length > 0, roots, names.Where(Function(name) name.Count(Function(ch) ch = "/"c) = 1 AndAlso {"manifest.json", "modrinth.index.json"}.Contains(name.Split("/"c).Last(), StringComparer.Ordinal)).ToArray())
            If manifests.Length <> 1 Then Throw New InvalidDataException("未找到唯一的 CurseForge / Modrinth 整合包清单。")
            Return If(manifests(0).EndsWith("modrinth.index.json", StringComparison.Ordinal), ModpackFormat.Modrinth, ModpackFormat.CurseForge)
        End Using
    End Function
End Module
