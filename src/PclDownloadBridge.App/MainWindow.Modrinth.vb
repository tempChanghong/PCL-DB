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
        Dim external = ExternalInstallProblem(True)
        If external IsNot Nothing Then Return external
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
        Dim issue = ModrinthNameProblem(), active = If(busy, _busy) OrElse _readingPack OrElse _localModWriting
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
        Dim sameArchive = _modrinthSelection IsNot Nothing AndAlso _modrinthSelection.Sha256 = package.Sha256
        Dim editedName = ModrinthInstanceName.Text
        Dim selected As New HashSet(Of String)(If(sameArchive, ModrinthOptionalFiles.Children.OfType(Of PclCheckBox)().Where(Function(box) box.IsChecked.GetValueOrDefault()).Select(Function(box) CStr(box.Tag)), Enumerable.Empty(Of String)()), StringComparer.OrdinalIgnoreCase)
        _modrinthSelection = package : _activeModrinth = True
        ModrinthInstanceName.Text = If(sameArchive, editedName, package.Name)
        ModrinthPackTitle.Text = package.Name & " · " & package.Version : ModrinthPackTitle.ToolTip = ModrinthPackTitle.Text
        ModrinthDescription.Text = package.Summary : ModrinthDescription.ToolTip = package.Summary
        ModrinthDescription.Visibility = If(String.IsNullOrWhiteSpace(package.Summary), Visibility.Collapsed, Visibility.Visible)
        Dim optionalFiles = package.References.Where(Function(item) item.Client = "optional").ToArray()
        ModrinthSource.Text = package.SourcePath : ModrinthSource.ToolTip = package.SourcePath
        ModrinthOptionalFiles.Children.Clear()
        Dim basenameCounts = optionalFiles.GroupBy(Function(item) IO.Path.GetFileName(item.Path), StringComparer.OrdinalIgnoreCase).ToDictionary(Function(items) items.Key, Function(items) items.Count(), StringComparer.OrdinalIgnoreCase)
        For Each reference In optionalFiles
            Dim basename = IO.Path.GetFileName(reference.Path)
            Dim label = If(basenameCounts(basename) > 1, reference.Path, basename)
            Dim box As New PclCheckBox With {.Content = label, .Tag = reference.Path, .IsChecked = selected.Contains(reference.Path),
                .ToolTip = reference.Path & vbCrLf & "预计大小：" & FormatBytes(reference.EstimatedSize), .Margin = New Thickness(0, 0, 0, 6)}
            Dim update As RoutedEventHandler = Sub()
                                                  If _modrinthSelection IsNot package Then Return
                                                  _activeModrinth = True : RefreshModrinthSummary()
                                              End Sub
            AddHandler box.Checked, update : AddHandler box.Unchecked, update
            ModrinthOptionalFiles.Children.Add(box)
        Next
        ModrinthOptionalHeader.Visibility = If(optionalFiles.Length > 0, Visibility.Visible, Visibility.Collapsed)
        RefreshModrinthSummary()
        ModrinthOptionsCard.Visibility = Visibility.Visible : RefreshPackAvailability()
        If navigate Then
            ChangePackMode(PackBrowserMode.Local)
            SetDownloadView(DownloadView.Modpacks) : SwitchMainPage(False)
        End If
        ModrinthReadStatus.Text = "请确认版本名称和可选文件。"
        If navigate Then ModrinthCard.BringIntoView()
    End Sub
    Private Sub RefreshModrinthSummary()
        Dim package = _modrinthSelection
        If package Is Nothing Then Return
        Dim selected As New HashSet(Of String)(ModrinthOptionalFiles.Children.OfType(Of PclCheckBox)().Where(Function(box) box.IsChecked.GetValueOrDefault()).Select(Function(box) CStr(box.Tag)), StringComparer.OrdinalIgnoreCase)
        Dim required = package.References.Where(Function(item) item.Client = "required").ToArray()
        Dim optionalFiles = package.References.Where(Function(item) item.Client = "optional").ToArray()
        Dim chosen = optionalFiles.Where(Function(item) selected.Contains(item.Path)).ToArray()
        ModrinthSummary.Text = "Minecraft " & package.Minecraft & If(package.LoaderKind.HasValue, " + " & package.LoaderKind.ToString() & " " & package.LoaderVersion, "") & vbCrLf &
            required.Length & " 个必需文件 · " & If(optionalFiles.Length > 0, "已选 " & chosen.Length & " / " & optionalFiles.Length & " 个可选文件", "无可选文件") & " · " & package.OverrideFiles.Count & " 个附带文件" & vbCrLf &
            "预计下载 " & FormatBytes(required.Sum(Function(item) item.EstimatedSize) + chosen.Sum(Function(item) item.EstimatedSize)) & "（不含游戏与加载器）"
    End Sub
    Private Shared Function IsPackFile(path As String) As Boolean
        Return {".zip", ".mrpack"}.Contains(IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
    End Function
End Class
