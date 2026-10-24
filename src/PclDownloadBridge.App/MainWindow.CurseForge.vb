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
        Dim external = ExternalInstallProblem(True)
        If external IsNot Nothing Then Return external
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
        If _busy OrElse _readingPack OrElse _choosingOnline Then Return
        CancelPackRead()
        Dim request As New CancellationTokenSource()
        _packReadCancellation = request
        _readingPack = True : RefreshPackAvailability()
        ChangePackMode(PackBrowserMode.Local)
        SetDownloadView(DownloadView.Modpacks)
        SwitchMainPage(False)
        Dim status = If(modrinthIntent OrElse String.Equals(IO.Path.GetExtension(path), ".mrpack", StringComparison.OrdinalIgnoreCase), ModrinthReadStatus, PackReadStatus)
        Try
            status.Text = "正在读取整合包清单…"
            Await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background)
            request.Token.ThrowIfCancellationRequested()
            Dim parsed = Await Task.Run(Function() As Object
                                            Return ModpackArchiveInstaller.ReadPackage(path, request.Token)
                                        End Function)
            If _closing OrElse request.IsCancellationRequested Then Return
            If _busy Then
                status.Text = "请在当前任务结束后重新导入。"
                Return
            End If
            If TypeOf parsed Is ModrinthPackage Then
                SelectModrinthPackage(DirectCast(parsed, ModrinthPackage), _downloadView = DownloadView.Modpacks AndAlso DownloadPage.Visibility = Visibility.Visible, request)
                If status IsNot ModrinthReadStatus Then status.Text = "已识别为 Modrinth。"
                Return
            End If
            Dim package = DirectCast(parsed, CurseForgePackage)
            SelectCurseForgePackage(package, request, _downloadView = DownloadView.Modpacks AndAlso DownloadPage.Visibility = Visibility.Visible)
            If status IsNot PackReadStatus Then status.Text = "已识别为 CurseForge。"
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
            box.ToolTip = file.Download.RelativePath
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
        Dim sameArchive = _packSelection IsNot Nothing AndAlso _packSelection.Sha256 = package.Sha256
        Dim editedName = PackInstanceName.Text
        Dim prior = If(sameArchive, PackOptionalFiles.Children.OfType(Of PclCheckBox)().ToDictionary(Function(box) CLng(box.Tag)), New Dictionary(Of Long, PclCheckBox)())
        _packSelection = package : _activeModrinth = False
        PackInstanceName.Text = If(sameArchive, editedName, package.Name)
        PackTitle.Text = package.Name & " · " & package.Version : PackTitle.ToolTip = PackTitle.Text
        PackSource.Text = package.SourcePath : PackSource.ToolTip = package.SourcePath
        PackOptionalFiles.Children.Clear()
        For Each reference In package.References.Where(Function(item) Not item.Required)
            Dim previous As PclCheckBox = Nothing
            prior.TryGetValue(reference.FileId, previous)
            Dim resolved = previous IsNot Nothing AndAlso CBool(previous.ReadLocalValue(IsEnabledProperty))
            ' Retain display information only. Installation always resolves again.
            ' A previously selectable option must remain possible to deselect if
            ' its fresh query fails; inherited form freezing is not metadata state.
            Dim box As New PclCheckBox With {.Content = If(resolved, previous?.Content, "正在查询可选文件 " & reference.FileId & "…"), .Tag = reference.FileId,
                .IsChecked = previous IsNot Nothing AndAlso previous.IsChecked.GetValueOrDefault(), .IsEnabled = resolved, .ToolTip = If(resolved, previous?.ToolTip, Nothing), .Margin = New Thickness(0, 0, 0, 6)}
            Dim update As RoutedEventHandler = Sub()
                                                  If _packSelection IsNot package Then Return
                                                  _activeModrinth = False : RefreshCurseForgeSummary()
                                              End Sub
            AddHandler box.Checked, update : AddHandler box.Unchecked, update
            PackOptionalFiles.Children.Add(box)
        Next
        PackOptionalHeader.Visibility = If(PackOptionalFiles.Children.Count > 0, Visibility.Visible, Visibility.Collapsed)
        RefreshCurseForgeSummary()
        PackOptionsCard.Visibility = Visibility.Visible
        RefreshPackAvailability()
        If navigate Then
            ChangePackMode(PackBrowserMode.Local)
            SetDownloadView(DownloadView.Modpacks)
            SwitchMainPage(False)
            If Not sameArchive Then CurseForgeCard.BringIntoView()
        End If
        PackReadStatus.Text = "请确认版本名称和可选文件。"
    End Sub
    Private Sub RefreshCurseForgeSummary()
        Dim package = _packSelection
        If package Is Nothing Then Return
        Dim count = package.References.Where(Function(item) Not item.Required).Count()
        Dim selected = PackOptionalFiles.Children.OfType(Of PclCheckBox)().Count(Function(box) box.IsChecked.GetValueOrDefault())
        PackSummary.Text = "Minecraft " & package.Minecraft & If(package.LoaderKind.HasValue, " + " & package.LoaderKind.ToString() & " " & package.LoaderVersion, "") & vbCrLf &
            package.References.Where(Function(item) item.Required).Count() & " 个必需文件 · " & If(count > 0, "已选 " & selected & " / " & count & " 个可选文件", "无可选文件") & " · " & package.OverrideFiles.Count & " 个附带文件"
    End Sub
    Private Sub PackDragOver(sender As Object, e As DragEventArgs)
        If Not e.Data.GetDataPresent(DataFormats.FileDrop) Then Return
        Dim files = TryCast(e.Data.GetData(DataFormats.FileDrop), String())
        e.Effects = If(Not _busy AndAlso Not _readingPack AndAlso Not _choosingOnline AndAlso files IsNot Nothing AndAlso files.Length = 1 AndAlso IsPackFile(files(0)), DragDropEffects.Copy, DragDropEffects.None)
        e.Handled = True
    End Sub
    Private Async Sub PackDrop(sender As Object, e As DragEventArgs)
        If Not e.Data.GetDataPresent(DataFormats.FileDrop) Then Return
        e.Handled = True
        Dim files = TryCast(e.Data.GetData(DataFormats.FileDrop), String())
        If _busy OrElse _readingPack OrElse _choosingOnline OrElse files Is Nothing OrElse files.Length <> 1 OrElse Not IsPackFile(files(0)) Then Return
        Await ReadLocalPackAsync(files(0))
    End Sub
End Class
