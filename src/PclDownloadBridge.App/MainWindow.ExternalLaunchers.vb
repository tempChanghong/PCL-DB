Imports System.IO
Imports System.Windows
Imports System.Windows.Controls
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private _launcherState As New LauncherBindingsState()
    Private ReadOnly _launcherFolders As New Dictionary(Of String, List(Of GameFolder))(StringComparer.Ordinal)
    Private _restoringLaunchers As Boolean
    Private _launcherReadGeneration As Long
    Private _launcherMigrationPending As Boolean
    Private _taskInstallTarget As LauncherInstallTarget
    Private _launcherDirectoryLoader As Func(Of ExternalLauncherBinding, Task(Of LauncherDirectoryResult)) =
        Function(binding) Task.Run(Function() New LauncherDirectoryReader().Read(binding))

    Private Function LauncherRow(kind As ExternalLauncherKind) As ComboBox
        Select Case kind
            Case ExternalLauncherKind.Pcl : Return PclBindingList
            Case ExternalLauncherKind.Hmcl : Return HmclBindingList
            Case Else : Return CeBindingList
        End Select
    End Function
    Private Function LauncherEditingAllowed() As Boolean
        Return Not _busy AndAlso Not _choosingOnline AndAlso Not _localModWriting AndAlso Not _closing AndAlso Not _restoringSettings AndAlso Not _launcherState.ReadOnlyFormat AndAlso
            ExternalLauncherCard.IsVisible AndAlso ExternalLauncherCard.IsHitTestVisible
    End Function
    Private Sub InitializeExternalLaunchers()
        AddHandler SettingsPage.IsVisibleChanged, Sub()
                                                     ' Startup restoration belongs to the window, not to the
                                                     ' settings page. Manual discovery still retires on exit.
                                                     If Not SettingsPage.IsVisible AndAlso Not _restoringSettings Then _launcherReadGeneration += 1
                                                 End Sub
    End Sub
    Private Sub RenderLauncherBindings()
        _restoringLaunchers = True
        Try
            For Each kind As ExternalLauncherKind In [Enum].GetValues(GetType(ExternalLauncherKind))
                Dim row = LauncherRow(kind), id As String = Nothing
                _launcherState.SelectedRows.TryGetValue(kind, id)
                row.ItemsSource = _launcherState.Bindings.Where(Function(binding) binding.Kind = kind).ToList()
                row.SelectedItem = _launcherState.Bindings.FirstOrDefault(Function(binding) binding.Kind = kind AndAlso binding.Id = id)
            Next
        Finally
            _restoringLaunchers = False
        End Try
        RefreshFollowedLauncherTheme()
    End Sub

    Private Async Function RestoreLauncherBindingsAsync(settings As JObject) As Task(Of Boolean)
        If _closing Then Return False
        _launcherReadGeneration += 1
        Dim generation = _launcherReadGeneration
        _launcherFolders.Clear()
        _launcherState = LauncherBindingsState.Restore(settings, _pclBase)
        _launcherMigrationPending = settings IsNot Nothing AndAlso settings("externalLaunchers") Is Nothing
        RenderLauncherBindings()
        If _closing OrElse generation <> _launcherReadGeneration Then Return False
        For Each binding In _launcherState.Bindings.ToArray()
            Dim result As LauncherDirectoryResult
            Try
                result = Await _launcherDirectoryLoader(binding.Copy())
            Catch ex As Exception
                If _closing OrElse generation <> _launcherReadGeneration Then Return False
                Throw
            End Try
            If _closing OrElse generation <> _launcherReadGeneration Then Return False
            If result.Succeeded AndAlso result.Folders.Count > 0 Then _launcherFolders(binding.Id) = result.Folders
            If result.Warnings.Count > 0 Then LauncherReadStatus.Text = String.Join("；", result.Warnings)
        Next
        Dim saved = _launcherState.Bindings.FirstOrDefault(Function(binding) binding.Id = _launcherState.TargetBindingId)
        _selectedFolder = Nothing
        If Not _launcherState.ReadOnlyFormat Then GameFolderFromSavedTarget(saved)
        ' Corrupt saved preferences require an explicit target choice when no trusted target remains.
        RebuildLauncherFolders(_launcherState.Warnings.Count = 0)
        If _closing OrElse generation <> _launcherReadGeneration Then Return False
        If _launcherState.Warnings.Count > 0 Then LauncherReadStatus.Text = String.Join("；", _launcherState.Warnings)
        Return Not _closing AndAlso generation = _launcherReadGeneration
    End Function

    Private Sub GameFolderFromSavedTarget(binding As ExternalLauncherBinding)
        Dim root = _launcherState.TargetRoot
        If String.IsNullOrWhiteSpace(root) Then Return
        Try
            root = LauncherDirectoryReader.AbsolutePath(root, Nothing)
            If binding Is Nothing AndAlso Not String.IsNullOrEmpty(_launcherState.TargetBindingId) Then Return
            _selectedFolder = New GameFolder With {.Name = "已保存的游戏目录", .Location = root, .Origin = If(binding Is Nothing, "手动选择", binding.DisplayName & " · " & binding.Workspace),
                .LauncherBindingId = binding?.Id, .LauncherKind = If(binding Is Nothing, Nothing, New ExternalLauncherKind?(binding.Kind)), .LauncherWorkspace = binding?.Workspace,
                .LauncherConfigPath = binding?.ConfigPath, .LauncherSharedConfigPath = binding?.SharedConfigPath}
        Catch ex As Exception When TypeOf ex Is InvalidDataException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is ArgumentException OrElse TypeOf ex Is NotSupportedException
            LauncherReadStatus.Text = "无法读取安装目标，请重新选择游戏目录。"
        End Try
    End Sub

    Private Sub RebuildLauncherFolders(Optional chooseInitial As Boolean = False)
        Dim previous = _selectedFolder
        Dim items = _launcherFolders.Values.SelectMany(Function(folders) folders).ToList()
        ' Identity includes the launcher binding: a shared root must not silently
        ' turn an HMCL target into a PCL target or vice versa.
        For Each manual In _folders.Where(Function(folder) String.IsNullOrEmpty(folder.LauncherBindingId))
            If Not items.Any(Function(folder) String.IsNullOrEmpty(folder.LauncherBindingId) AndAlso SameGameRoot(folder.Location, manual.Location)) Then items.Add(manual)
        Next
        Dim selected = If(previous Is Nothing, Nothing, items.FirstOrDefault(Function(folder) folder.LauncherBindingId = previous.LauncherBindingId AndAlso SameGameRoot(folder.Location, previous.Location)))
        If selected Is Nothing AndAlso previous IsNot Nothing Then selected = previous : items.Add(previous)
        If selected Is Nothing AndAlso chooseInitial AndAlso Not _launcherState.ReadOnlyFormat AndAlso String.IsNullOrEmpty(_launcherState.TargetRoot) Then selected = items.FirstOrDefault(Function(folder) folder.IsSelected)
        If selected Is Nothing AndAlso chooseInitial AndAlso Not _launcherState.ReadOnlyFormat AndAlso String.IsNullOrEmpty(_launcherState.TargetRoot) Then selected = items.FirstOrDefault()
        _restoringLaunchers = True
        Try
            _folders = items : FolderList.ItemsSource = Nothing : FolderList.ItemsSource = items : FolderList.SelectedItem = selected
        Finally
            _restoringLaunchers = False
        End Try
        FolderSelected(FolderList, Nothing)
    End Sub

    Private Shared Function SameGameRoot(a As String, b As String) As Boolean
        Return String.Equals(LauncherDirectoryReader.AbsolutePath(a, Nothing), LauncherDirectoryReader.AbsolutePath(b, Nothing), StringComparison.OrdinalIgnoreCase)
    End Function

    Private Sub LauncherBindingSelected(sender As Object, e As SelectionChangedEventArgs)
        If _restoringLaunchers Then Return
        If Not LauncherEditingAllowed() Then RenderLauncherBindings() : Return
        Dim binding = TryCast(DirectCast(sender, ComboBox).SelectedItem, ExternalLauncherBinding)
        If binding Is Nothing Then Return
        _launcherReadGeneration += 1
        _launcherState.SelectedRows(binding.Kind) = binding.Id
        If binding.Kind = ExternalLauncherKind.Pcl Then RefreshFollowedLauncherTheme()
        LauncherReadStatus.Text = "已选择 " & binding.DisplayName & "，请点击「读取目录」。"
        PersistSettings()
    End Sub

    Private Sub AddLauncherLocation(sender As Object, e As RoutedEventArgs)
        If Not LauncherEditingAllowed() Then Return
        Dim kind = CType([Enum].Parse(GetType(ExternalLauncherKind), CStr(DirectCast(sender, FrameworkElement).Tag)), ExternalLauncherKind)
        Using dialog As New System.Windows.Forms.FolderBrowserDialog With {.Description = "选择启动器的工作目录（HMCL 相对路径以此为准）", .ShowNewFolderButton = False}
            If dialog.ShowDialog() <> System.Windows.Forms.DialogResult.OK Then Return
            AddLauncherBinding(New ExternalLauncherBinding With {.Kind = kind, .Workspace = LauncherDirectoryReader.AbsolutePath(dialog.SelectedPath, Nothing)})
        End Using
    End Sub
    Private Sub AddLauncherBinding(binding As ExternalLauncherBinding)
        If Not LauncherEditingAllowed() Then Return
        Dim existing = _launcherState.Bindings.FirstOrDefault(Function(item) item.Kind = binding.Kind AndAlso SameGameRoot(item.Workspace, binding.Workspace) AndAlso item.ConfigPath = binding.ConfigPath AndAlso item.SharedConfigPath = binding.SharedConfigPath)
        If existing Is Nothing Then _launcherState.Bindings.Add(binding) Else binding = existing
        _launcherState.SelectedRows(binding.Kind) = binding.Id
        _launcherReadGeneration += 1 : RenderLauncherBindings()
        LauncherReadStatus.Text = "已添加 " & binding.DisplayName & "，请点击「读取目录」。"
        PersistSettings()
    End Sub

    Private Sub ChooseLauncherConfig(sender As Object, e As RoutedEventArgs)
        If Not LauncherEditingAllowed() Then Return
        Dim kind = CType([Enum].Parse(GetType(ExternalLauncherKind), CStr(DirectCast(sender, FrameworkElement).Tag)), ExternalLauncherKind)
        Dim current = TryCast(LauncherRow(kind).SelectedItem, ExternalLauncherBinding)
        If current Is Nothing Then LauncherReadStatus.Text = "请先选择启动器工作目录。" : Return
        Dim dialog As New Microsoft.Win32.OpenFileDialog With {.Title = "选择目录配置（仅读取）", .Filter = "目录配置 (*.json;*.yml;*.yaml;*.ini)|*.json;*.yml;*.yaml;*.ini", .CheckFileExists = True, .InitialDirectory = current.Workspace}
        If dialog.ShowDialog(Me) <> True Then Return
        Dim binding = current.Copy() : binding.Id = Guid.NewGuid().ToString("N")
        Dim name = Path.GetFileName(dialog.FileName)
        If name.Equals("user-game-directories.json", StringComparison.OrdinalIgnoreCase) OrElse
            (kind = ExternalLauncherKind.PclCe AndAlso Path.GetExtension(name) = ".json" AndAlso Not SameGameRoot(Path.GetDirectoryName(dialog.FileName), Path.Combine(current.Workspace, "PCL"))) Then
            binding.SharedConfigPath = dialog.FileName
        Else
            binding.ConfigPath = dialog.FileName
        End If
        Try
            binding.ValidatePclConfiguration()
        Catch ex As InvalidDataException
            LauncherReadStatus.Text = ex.Message : Return
        End Try
        AddLauncherBinding(binding)
    End Sub

    Private Async Sub ReadLauncherDirectories(sender As Object, e As RoutedEventArgs)
        Dim kind = CType([Enum].Parse(GetType(ExternalLauncherKind), CStr(DirectCast(sender, FrameworkElement).Tag)), ExternalLauncherKind)
        Dim binding = TryCast(LauncherRow(kind).SelectedItem, ExternalLauncherBinding)
        If binding IsNot Nothing Then Await ReadLauncherBindingAsync(binding)
    End Sub
    Private Async Function ReadLauncherBindingAsync(binding As ExternalLauncherBinding) As Task
        If Not LauncherEditingAllowed() OrElse Not _launcherState.Bindings.Contains(binding) Then Return
        _launcherReadGeneration += 1
        Dim generation = _launcherReadGeneration, frozen = binding.Copy()
        LauncherReadStatus.Text = "正在读取 " & binding.DisplayName & " 的游戏目录……"
        Try
            Dim result = Await _launcherDirectoryLoader(frozen)
            If generation <> _launcherReadGeneration OrElse Not LauncherEditingAllowed() OrElse Not _launcherState.Bindings.Contains(binding) OrElse _launcherState.SelectedRows(binding.Kind) <> binding.Id Then Return
            If result.Succeeded AndAlso result.Folders.Count > 0 Then
                _launcherFolders(binding.Id) = result.Folders
                RebuildLauncherFolders(_selectedFolder Is Nothing)
                LauncherReadStatus.Text = "已读取 " & result.Folders.Count & " 个 " & binding.DisplayName & " 游戏目录。"
            Else
                LauncherReadStatus.Text = "未找到游戏目录，保留当前选择。"
            End If
            If result.Warnings.Count > 0 Then LauncherReadStatus.Text &= " " & String.Join("；", result.Warnings)
            PersistSettings()
        Catch ex As Exception
            If generation = _launcherReadGeneration AndAlso Not _closing Then LauncherReadStatus.Text = "目录读取失败：" & ex.Message
        End Try
    End Function

    Private Function LauncherSettingsJson() As JToken
        If _selectedFolder IsNot Nothing Then
            _launcherState.TargetRoot = _selectedFolder.Location : _launcherState.TargetBindingId = _selectedFolder.LauncherBindingId
        End If
        Return _launcherState.ToJson()
    End Function
    Private Function CurrentInstallTarget() As LauncherInstallTarget
        Return New LauncherInstallTarget(_selectedFolder, _pclBase)
    End Function
    Private Function ExternalInstallProblem(Optional pack As Boolean = False) As String
        If _selectedFolder Is Nothing Then Return Nothing
        Try
            CurrentInstallTarget().ValidateDestination(_selectedFolder.Location)
        Catch ex As Exception When TypeOf ex Is InvalidDataException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is ArgumentException OrElse TypeOf ex Is InvalidOperationException
            Return ex.Message
        End Try
        Return Nothing
    End Function
End Class
