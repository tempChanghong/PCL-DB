Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Input
Imports PclDownloadBridge.Core
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Public Sub PreviewCurseForge(path As String)
        If Not _preview Then Return
        If ModpackFormats.Detect(path) = ModpackFormat.Modrinth Then
            SelectModrinthPackage(ModrinthPackage.Read(path))
        Else
            SelectCurseForgePackage(CurseForgePackage.Read(path))
        End If
    End Sub
    Private _packSelection As CurseForgePackage
    Private _readingPack As Boolean
    Private _packReadCancellation As CancellationTokenSource
    Private Sub CancelPackRead()
        _packReadCancellation?.Cancel()
        MarkCancelledPackQueries()
    End Sub
    Private Sub MarkCancelledPackQueries()
        For Each box In PackOptionalFiles.Children.OfType(Of PclCheckBox)()
            ' IsEnabled inherits the form's temporary freeze. Preserve choices
            ' whose own metadata already succeeded even while their parent is disabled.
            If Not CBool(box.ReadLocalValue(IsEnabledProperty)) AndAlso box.Content.ToString().StartsWith("正在查询可选文件", StringComparison.Ordinal) Then
                box.Content = "可选文件 " & box.Tag.ToString() & " 查询已取消（重新导入可重试）"
            End If
        Next
    End Sub
    Private Sub RefreshPackAvailability(Optional busy As Boolean? = Nothing)
        RefreshModrinthAvailability(busy)
        If PackInstanceName Is Nothing Then Return
        Dim issue = PackNameProblem(), active = If(busy, _busy) OrElse _readingPack
        PackInstanceName.ValidationMessage = If(issue, "")
        PackNameHint.Text = If(issue, "")
        PackNameHint.Visibility = If(issue Is Nothing, Visibility.Collapsed, Visibility.Visible)
        PackOptionsCard.IsEnabled = Not active
        ImportPackButton.IsEnabled = Not active
        PackInstallButton.IsEnabled = Not active AndAlso _packSelection IsNot Nothing AndAlso issue Is Nothing
        PackInstallButton.ToolTip = If(active, "请等待当前任务结束。", issue)
    End Sub
    Private Function PackNameProblem() As String
        If _packSelection Is Nothing Then Return Nothing
        Try
            InstallPreflight.ValidateName(PackInstanceName.Text)
            If _selectedFolder IsNot Nothing Then InstallPreflight.ValidateDestination(_selectedFolder.Location, PackInstanceName.Text, False)
        Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            Return ex.Message
        End Try
        Return Nothing
    End Function
    Private Sub PackNameKeyDown(sender As Object, e As KeyEventArgs)
        If e.Key <> Key.Enter Then Return
        e.Handled = True
        If e.IsRepeat OrElse _busy OrElse _readingPack OrElse _closing OrElse _downloadView <> DownloadView.Modpacks OrElse Not DownloadPage.IsVisible Then Return
        StartInstall(sender, e)
    End Sub
    Private Async Sub ChooseCurseForgePack(sender As Object, e As RoutedEventArgs)
        If _busy OrElse _readingPack Then Return
        Dim dialog As New Microsoft.Win32.OpenFileDialog With {.Title = "导入 CurseForge 整合包", .Filter = "CurseForge 整合包 (*.zip)|*.zip", .CheckFileExists = True}
        If dialog.ShowDialog(Me) = True Then Await ReadLocalPackAsync(dialog.FileName)
    End Sub
    Private Function ReadCurseForgePackAsync(path As String) As Task
        Return ReadLocalPackAsync(path)
    End Function
    Private Async Function ReadLocalPackAsync(path As String, Optional modrinthIntent As Boolean = False) As Task
        If _busy OrElse _readingPack Then Return
        CancelPackRead()
        Dim request As New CancellationTokenSource()
        _packReadCancellation = request
        _readingPack = True : RefreshPackAvailability()
        SetDownloadView(DownloadView.Modpacks)
        SwitchMainPage(False)
        Dim status = If(modrinthIntent OrElse String.Equals(IO.Path.GetExtension(path), ".mrpack", StringComparison.OrdinalIgnoreCase), ModrinthReadStatus, PackReadStatus)
        Try
            status.Text = "正在读取整合包清单…"
            Await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background)
            request.Token.ThrowIfCancellationRequested()
            Dim parsed = Await Task.Run(Function() As Object
                                            If ModpackFormats.Detect(path) = ModpackFormat.Modrinth Then Return ModrinthPackage.Read(path, request.Token)
                                            Return CurseForgePackage.Read(path, request.Token)
                                        End Function)
            If _closing OrElse request.IsCancellationRequested Then Return
            If _busy Then
                status.Text = "当前安装已开始，请在任务结束后重新选择整合包。"
                Return
            End If
            If TypeOf parsed Is ModrinthPackage Then
                SelectModrinthPackage(DirectCast(parsed, ModrinthPackage), _downloadView = DownloadView.Modpacks AndAlso DownloadPage.Visibility = Visibility.Visible, request)
                If status IsNot ModrinthReadStatus Then status.Text = "已识别为 Modrinth，清单已显示在对应卡片。"
                Return
            End If
            Dim package = DirectCast(parsed, CurseForgePackage)
            SelectCurseForgePackage(package, request, _downloadView = DownloadView.Modpacks AndAlso DownloadPage.Visibility = Visibility.Visible)
            If status IsNot PackReadStatus Then status.Text = "已识别为 CurseForge，清单已显示在对应卡片。"
            _readingPack = False : RefreshPackAvailability()
            If Not _preview Then
                Using resolver As New CurseForgeFileResolver(Policy())
                    Using slots As New SemaphoreSlim(4)
                        Await Task.WhenAll(PackOptionalFiles.Children.OfType(Of PclCheckBox)().Select(
                            Function(box) QueryPackOptionalAsync(package, box, resolver, slots, request)).ToArray())
                    End Using
                End Using
            End If
        Catch ex As OperationCanceledException
            If Not _closing AndAlso Not _busy AndAlso _packReadCancellation Is request Then status.Text = "整合包读取已取消。"
        Catch ex As Exception
            If Not _closing AndAlso Not _busy AndAlso _packReadCancellation Is request Then
                status.Text = "无法读取整合包：" & ex.Message
            End If
        Finally
            If _packReadCancellation Is request Then
                _packReadCancellation = Nothing
                _readingPack = False
                If Not _closing Then
                    If request.IsCancellationRequested Then
                        MarkCancelledPackQueries()
                    End If
                    RefreshPackAvailability()
                End If
            End If
            request.Dispose()
        End Try
    End Function
    Private Async Function QueryPackOptionalAsync(package As CurseForgePackage, box As PclCheckBox, resolver As CurseForgeFileResolver, slots As SemaphoreSlim, request As CancellationTokenSource) As Task
        Await slots.WaitAsync(request.Token)
        Try
            Dim reference = package.References.Single(Function(item) item.FileId = CLng(box.Tag))
            Dim file = Await resolver.ResolveFileAsync(reference, package.Minecraft, request.Token)
            If Not CurrentPackRead(package, request) Then Return
            box.Content = IO.Path.GetFileName(file.Download.RelativePath) & "（可选）"
            box.IsEnabled = True
        Catch ex As Exception When Not TypeOf ex Is OperationCanceledException
            If Not CurrentPackRead(package, request) Then Return
            box.Content = "可选文件 " & box.Tag.ToString() & " 暂时无法查询（重新导入可重试）"
            box.ToolTip = ex.Message
        Finally
            slots.Release()
        End Try
    End Function
    Private Function CurrentPackRead(package As CurseForgePackage, request As CancellationTokenSource) As Boolean
        Return Not _closing AndAlso Not request.IsCancellationRequested AndAlso _packSelection Is package AndAlso _packReadCancellation Is request
    End Function
    Private Sub SelectCurseForgePackage(package As CurseForgePackage, Optional request As CancellationTokenSource = Nothing, Optional navigate As Boolean = True)
        If _packReadCancellation IsNot request Then CancelPackRead()
        _packSelection = package : _activeModrinth = False
        PackInstanceName.Text = package.Name
        PackSummary.Text = package.Name & " · " & package.Version & vbCrLf & "Minecraft " & package.Minecraft &
            If(package.LoaderKind.HasValue, " + " & package.LoaderKind.ToString() & " " & package.LoaderVersion, "") & vbCrLf &
            package.References.Where(Function(item) item.Required).Count() & " 个必需文件 · " & package.References.Where(Function(item) Not item.Required).Count() & " 个可选文件 · " & package.OverrideFiles.Count & " 个附带配置文件"
        PackSource.Text = package.SourcePath : PackSource.ToolTip = package.SourcePath
        PackOptionalFiles.Children.Clear()
        For Each reference In package.References.Where(Function(item) Not item.Required)
            PackOptionalFiles.Children.Add(New PclCheckBox With {.Content = "正在查询可选文件 " & reference.FileId & "…", .Tag = reference.FileId, .IsEnabled = False, .Margin = New Thickness(0, 0, 0, 6)})
        Next
        PackOptionsCard.Visibility = Visibility.Visible
        RefreshPackAvailability()
        ModpackPage.ScrollToHome()
        If navigate Then
            SetDownloadView(DownloadView.Modpacks)
            SwitchMainPage(False)
        End If
        PackReadStatus.Text = "清单已读取。开始安装时将重新查询并校验指定文件。"
    End Sub
    Private Sub PackDragOver(sender As Object, e As DragEventArgs)
        If Not e.Data.GetDataPresent(DataFormats.FileDrop) Then Return
        Dim files = TryCast(e.Data.GetData(DataFormats.FileDrop), String())
        e.Effects = If(Not _busy AndAlso Not _readingPack AndAlso files IsNot Nothing AndAlso files.Length = 1 AndAlso IsPackFile(files(0)), DragDropEffects.Copy, DragDropEffects.None)
        e.Handled = True
    End Sub
    Private Async Sub PackDrop(sender As Object, e As DragEventArgs)
        If Not e.Data.GetDataPresent(DataFormats.FileDrop) Then Return
        e.Handled = True
        Dim files = TryCast(e.Data.GetData(DataFormats.FileDrop), String())
        If _busy OrElse _readingPack OrElse files Is Nothing OrElse files.Length <> 1 OrElse Not IsPackFile(files(0)) Then Return
        Await ReadLocalPackAsync(files(0))
    End Sub
End Class
