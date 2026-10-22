Imports System.IO
Imports System.Text.RegularExpressions

' PCL logs cleanup failures separately instead of replacing the install outcome.
' Only this installation's fresh private work/payload tree may be removed here.
Friend NotInheritable Class InstallCleanup
    Private Sub New()
    End Sub

    Friend Shared Sub DeleteOwnedDirectory(root As String, relative As String)
        If Not Regex.IsMatch(relative, "\A\.vanilla-downloader/(?:components/[0-9a-f]{32}|staging/[0-9a-f]{32}/payload)\z") Then
            Throw New ArgumentException("不是安装任务拥有的临时目录。", NameOf(relative))
        End If
        Try
            Dim target = SafeFiles.UnderRoot(root, relative)
            If Not Directory.Exists(target) Then Return
            If (File.GetAttributes(target) And FileAttributes.ReparsePoint) <> 0 Then Throw New IOException("安装临时目录包含链接。")
            Directory.Delete(target, True)
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            LogIncomplete(root, relative, ex)
        End Try
    End Sub

    Private Shared Sub LogIncomplete(root As String, relative As String, failure As Exception)
        InstallLog.Append(root, "安装临时文件清理未完成：" & relative & " | " & failure.Message)
    End Sub
End Class
