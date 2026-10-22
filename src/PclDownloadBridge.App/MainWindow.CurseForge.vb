Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Media
Imports PclDownloadBridge.Core
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Public Sub PreviewCurseForge(path As String)
        If _preview Then SelectCurseForgePackage(CurseForgePackage.Read(path))
    End Sub
    Private _packSelection As CurseForgePackage
    Private _readingPack As Boolean
    Private _packReadCancellation As CancellationTokenSource
    Private Async Sub ChooseCurseForgePack(sender As Object, e As RoutedEventArgs)
        If _busy OrElse _readingPack Then Return
        Dim dialog As New Microsoft.Win32.OpenFileDialog With {.Title = "导入 CurseForge 整合包", .Filter = "CurseForge 整合包 (*.zip)|*.zip", .CheckFileExists = True}
        If dialog.ShowDialog(Me) = True Then Await ReadCurseForgePackAsync(dialog.FileName)
    End Sub
    Private Async Function ReadCurseForgePackAsync(path As String) As Task
        If _busy OrElse _readingPack Then Return
        _packReadCancellation?.Cancel()
        Dim request As New CancellationTokenSource()
        _packReadCancellation = request
        _readingPack = True : ImportPackButton.IsEnabled = False
        Try
            StatusText.Text = "正在读取整合包清单…"
            Dim package = Await Task.Run(Function() CurseForgePackage.Read(path, request.Token))
            If _closing OrElse _busy OrElse request.IsCancellationRequested Then Return
            SelectCurseForgePackage(package)
            _readingPack = False : ImportPackButton.IsEnabled = True
            If Not _preview Then
                Using resolver As New CurseForgeFileResolver(Policy())
                    For Each box In PackOptionalFiles.Children.OfType(Of PclCheckBox)().ToArray()
                        Try
                            Dim reference = package.References.Single(Function(item) item.FileId = CLng(box.Tag))
                            Dim file = Await resolver.ResolveFileAsync(reference, package.Minecraft, request.Token)
                            If request.IsCancellationRequested OrElse _closing OrElse _packSelection IsNot package Then Return
                            box.Content = IO.Path.GetFileName(file.Download.RelativePath) & "（可选）"
                            box.IsEnabled = True
                        Catch ex As Exception When Not TypeOf ex Is OperationCanceledException
                            If _closing OrElse _packSelection IsNot package Then Return
                            box.Content = "可选文件暂时无法查询（重新导入可重试）"
                            box.ToolTip = ex.Message
                        End Try
                    Next
                End Using
            End If
        Catch ex As OperationCanceledException
            If Not _closing AndAlso Not _busy AndAlso _packReadCancellation Is request Then StatusText.Text = "整合包读取已取消。"
        Catch ex As Exception
            If Not _closing AndAlso Not _busy AndAlso _packReadCancellation Is request Then
                StatusText.Text = ex.Message
                If Not _preview Then MessageBox.Show(Me, ex.Message, "无法读取整合包", MessageBoxButton.OK, MessageBoxImage.Information)
            End If
        Finally
            If _packReadCancellation Is request Then
                _packReadCancellation = Nothing
                _readingPack = False
                If Not _closing Then ImportPackButton.IsEnabled = Not _busy
            End If
            request.Dispose()
        End Try
    End Function
    Private Sub SelectCurseForgePackage(package As CurseForgePackage)
        CancelComponentRequests()
        _packSelection = package
        _componentSelection = New ComponentSelection()
        _componentsDirty = False
        _selectedEntry = New VersionEntry With {.Id = package.Minecraft, .Kind = "release"}
        RepairBox.IsChecked = False
        _nameEdited = False
        SetAutomaticInstanceName(package.Name)
        SelectedVersionText.Text = package.Minecraft & " · 整合包指定版本"
        SelectedVersionIcon.Source = New Imaging.BitmapImage(New Uri("pack://application:,,,/Images/Blocks/Grass.png"))
        VanillaVersionIcon.Source = SelectedVersionIcon.Source
        PackSummary.Text = package.Name & " · " & package.Version & vbCrLf & "Minecraft " & package.Minecraft &
            If(package.LoaderKind.HasValue, " + " & package.LoaderKind.ToString() & " " & package.LoaderVersion, "") & vbCrLf &
            package.References.Where(Function(item) item.Required).Count() & " 个必需文件 · " & package.References.Where(Function(item) Not item.Required).Count() & " 个可选文件 · " & package.OverrideFiles.Count & " 个附带配置文件"
        PackSource.Text = package.SourcePath : PackSource.ToolTip = package.SourcePath
        PackOptionalFiles.Children.Clear()
        For Each reference In package.References.Where(Function(item) Not item.Required)
            PackOptionalFiles.Children.Add(New PclCheckBox With {.Content = "正在查询可选文件…", .Tag = reference.FileId, .IsEnabled = False, .Margin = New Thickness(0, 0, 0, 6)})
        Next
        SetPackVisibility(True)
        RefreshInstallAvailability()
        SetDownloadView(DownloadView.Install)
        InstallScroll.ScrollToHome()
        SwitchMainPage(False)
        StatusText.Text = "已读取 CurseForge 清单。开始安装时将重新查询并校验指定文件。"
    End Sub
    Private Sub LeavePackMode()
        _packReadCancellation?.Cancel()
        _packSelection = Nothing
        SetPackVisibility(False)
    End Sub
    Private Sub SetPackVisibility(enabled As Boolean)
        PackOptionsCard.Visibility = If(enabled, Visibility.Visible, Visibility.Collapsed)
        For Each element As FrameworkElement In New FrameworkElement() {IsolationCard, ComponentCards, ComponentHint, RepairCard}
            element.Visibility = If(enabled, Visibility.Collapsed, Visibility.Visible)
        Next
    End Sub
    Private Sub PackDragOver(sender As Object, e As DragEventArgs)
        If Not e.Data.GetDataPresent(DataFormats.FileDrop) Then Return
        Dim files = TryCast(e.Data.GetData(DataFormats.FileDrop), String())
        e.Effects = If(Not _busy AndAlso Not _readingPack AndAlso files IsNot Nothing AndAlso files.Length = 1 AndAlso String.Equals(Path.GetExtension(files(0)), ".zip", StringComparison.OrdinalIgnoreCase), DragDropEffects.Copy, DragDropEffects.None)
        e.Handled = True
    End Sub
    Private Async Sub PackDrop(sender As Object, e As DragEventArgs)
        If Not e.Data.GetDataPresent(DataFormats.FileDrop) Then Return
        e.Handled = True
        Dim files = TryCast(e.Data.GetData(DataFormats.FileDrop), String())
        If _busy OrElse _readingPack OrElse files Is Nothing OrElse files.Length <> 1 OrElse Not String.Equals(Path.GetExtension(files(0)), ".zip", StringComparison.OrdinalIgnoreCase) Then Return
        Await ReadCurseForgePackAsync(files(0))
    End Sub
End Class
