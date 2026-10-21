Imports System.Diagnostics
Imports System.IO
Imports System.Windows

Partial Public Class MainWindow
    Private _settingsOffsetBeforeAbout As Double
    Private _aboutAction As Action(Of String, String)
    Private _previewDocumentCache As String

    Private Sub InitializeAbout()
        AboutMonogram.Text = "DB"
        AboutProductItem.Title = ProductInfo.FullName
        AboutProductItem.Info = "当前版本：" & ProductInfo.VersionText
        AboutRuntimeInfo.Text = ".NET Framework 4.8 · CLR " & Environment.Version.ToString()
    End Sub

    Private Sub ShowAbout(sender As Object, e As RoutedEventArgs)
        If AboutPage.Visibility = Visibility.Visible Then Return
        _settingsOffsetBeforeAbout = SettingsPage.VerticalOffset
        SwitchMainPage(True)
        SettingsPage.Visibility = Visibility.Collapsed
        AboutPage.Visibility = Visibility.Visible
        AboutPage.ScrollToTop()
    End Sub

    Private Sub BackFromAbout(sender As Object, e As RoutedEventArgs)
        SwitchMainPage(True)
        SettingsPage.ScrollToVerticalOffset(_settingsOffsetBeforeAbout)
    End Sub

    Private Sub CopyAboutVersion(sender As Object, e As RoutedEventArgs)
        DispatchAboutAction("copy", ProductInfo.VersionReport())
    End Sub

    Private Sub OpenAboutLink(sender As Object, e As RoutedEventArgs)
        Dim address As String
        Select Case CStr(DirectCast(sender, FrameworkElement).Tag)
            Case "pcl-sponsor" : address = "https://meloong.com/afd/a/LTCat"
            Case "pcl" : address = "https://github.com/Meloong-Git/PCL"
            Case "bmclapi" : address = "https://github.com/bangbang93/openbmclapi"
            Case Else : Return
        End Select
        DispatchAboutAction("link", address)
    End Sub

    Private Function DocumentationPath(name As String) As String
        Dim directory As New DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory)
        For level = 0 To 6
            If directory Is Nothing Then Exit For
            Dim candidate = Path.Combine(directory.FullName, name)
            Dim developmentRoot = File.Exists(Path.Combine(directory.FullName, "PclDownloadBridge.sln")) AndAlso
                File.Exists(Path.Combine(directory.FullName, "src", "PclDownloadBridge.App", "PclDownloadBridge.App.vbproj"))
            If (level = 0 OrElse developmentRoot) AndAlso File.Exists(candidate) Then Return candidate
            directory = directory.Parent
        Next
        Dim cache As String
        If _preview Then
            If _previewDocumentCache Is Nothing Then _previewDocumentCache = Path.Combine(Path.GetTempPath(), "PclDownloadBridge-PreviewDocuments-" & Guid.NewGuid().ToString("N"))
            cache = _previewDocumentCache
        Else
            cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PclDownloadBridge", "documents")
        End If
        Return BundledDocuments.Materialize(name, cache)
    End Function

    Private Sub OpenAboutDocument(sender As Object, e As RoutedEventArgs)
        Dim name = CStr(DirectCast(sender, FrameworkElement).Tag)
        If name <> "README.md" AndAlso name <> "SOURCE_REFERENCES.md" AndAlso name <> "THIRD_PARTY_NOTICES.md" Then Return
        Try
            DispatchAboutAction("document", DocumentationPath(name))
        Catch ex As Exception
            AboutActionStatus.Text = "无法打开文档：" & ex.Message
        End Try
    End Sub

    Private Sub DispatchAboutAction(kind As String, value As String)
        Try
            If _aboutAction IsNot Nothing Then
                _aboutAction(kind, value)
                Return
            End If
            If _preview Then Return
            Select Case kind
                Case "copy"
                    Clipboard.SetText(value)
                    AboutActionStatus.Text = "版本信息已复制。"
                Case "document"
                    Process.Start(New ProcessStartInfo("notepad.exe", """" & value & """") With {.UseShellExecute = True})
                    AboutActionStatus.Text = "已打开随附文档。"
                Case "link"
                    Process.Start(New ProcessStartInfo(value) With {.UseShellExecute = True})
                    AboutActionStatus.Text = "已在默认浏览器中打开。"
            End Select
        Catch ex As Exception
            AboutActionStatus.Text = "操作未完成：" & ex.Message
        End Try
    End Sub
End Class
