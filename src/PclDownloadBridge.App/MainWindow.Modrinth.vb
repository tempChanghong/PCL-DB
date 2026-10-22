Imports System.IO
Imports System.Windows
Imports System.Windows.Input
Imports PclDownloadBridge.Core
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private _modrinthSelection As ModrinthPackage
    Private _activeModrinth As Boolean
    Private Async Sub ChooseModrinthPack(sender As Object, e As RoutedEventArgs)
        If _busy OrElse _readingPack Then Return
        Dim dialog As New Microsoft.Win32.OpenFileDialog With {.Title = "导入 Modrinth 整合包", .Filter = "Modrinth 整合包 (*.mrpack)|*.mrpack|整合包 ZIP (*.zip)|*.zip", .CheckFileExists = True}
        If dialog.ShowDialog(Me) = True Then Await ReadLocalPackAsync(dialog.FileName, True)
    End Sub
    Private Function ModrinthNameProblem() As String
        If _modrinthSelection Is Nothing Then Return Nothing
        Try
            InstallPreflight.ValidateName(ModrinthInstanceName.Text)
            If _selectedFolder IsNot Nothing Then InstallPreflight.ValidateDestination(_selectedFolder.Location, ModrinthInstanceName.Text, False)
        Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            Return ex.Message
        End Try
        Return Nothing
    End Function
    Private Sub RefreshModrinthAvailability(Optional busy As Boolean? = Nothing)
        If ModrinthInstanceName Is Nothing Then Return
        Dim issue = ModrinthNameProblem(), active = If(busy, _busy) OrElse _readingPack
        ModrinthInstanceName.ValidationMessage = If(issue, "") : ModrinthNameHint.Text = If(issue, "")
        ModrinthNameHint.Visibility = If(issue Is Nothing, Visibility.Collapsed, Visibility.Visible)
        ModrinthOptionsCard.IsEnabled = Not active : ImportModrinthButton.IsEnabled = Not active
        ModrinthInstallButton.IsEnabled = Not active AndAlso _modrinthSelection IsNot Nothing AndAlso issue Is Nothing
        ModrinthInstallButton.ToolTip = If(active, "请等待当前任务结束。", issue)
    End Sub
    Private Sub ModrinthNameKeyDown(sender As Object, e As KeyEventArgs)
        If e.Key <> Key.Enter Then Return
        e.Handled = True
        If e.IsRepeat OrElse _busy OrElse _readingPack OrElse _closing OrElse _downloadView <> DownloadView.Modpacks OrElse Not DownloadPage.IsVisible Then Return
        StartInstall(sender, e)
    End Sub
    Private Sub SelectModrinthPackage(package As ModrinthPackage, Optional navigate As Boolean = True, Optional request As System.Threading.CancellationTokenSource = Nothing)
        If _packReadCancellation IsNot request Then CancelPackRead()
        _modrinthSelection = package : _activeModrinth = True
        ModrinthInstanceName.Text = package.Name
        Dim required = package.References.Where(Function(item) item.Client = "required").ToArray(), optionalFiles = package.References.Where(Function(item) item.Client = "optional").ToArray()
        ModrinthSummary.Text = package.Name & " · " & package.Version & vbCrLf & "Minecraft " & package.Minecraft &
            If(package.LoaderKind.HasValue, " + " & package.LoaderKind.ToString() & " " & package.LoaderVersion, "") & vbCrLf &
            required.Length & " 个必需文件 · " & optionalFiles.Length & " 个可选文件 · " & package.OverrideFiles.Count & " 个附带文件" & vbCrLf &
            "预计下载 " & FormatBytes(required.Sum(Function(item) item.EstimatedSize)) & "（不含游戏与加载器）"
        ModrinthSource.Text = package.SourcePath : ModrinthSource.ToolTip = package.SourcePath
        ModrinthOptionalFiles.Children.Clear()
        For Each reference In optionalFiles
            ModrinthOptionalFiles.Children.Add(New PclCheckBox With {.Content = reference.Path & "（可选 · " & FormatBytes(reference.EstimatedSize) & "）", .Tag = reference.Path, .Margin = New Thickness(0, 0, 0, 6)})
        Next
        ModrinthOptionsCard.Visibility = Visibility.Visible : RefreshPackAvailability()
        If navigate Then
            SetDownloadView(DownloadView.Modpacks) : SwitchMainPage(False)
        End If
        ModrinthReadStatus.Text = "清单已读取。按清单地址下载，校验 SHA-1 与 SHA-512；无需查询文件信息。"
        If navigate Then ModrinthCard.BringIntoView()
    End Sub
    Private Shared Function IsPackFile(path As String) As Boolean
        Return {".zip", ".mrpack"}.Contains(IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
    End Function
End Class
