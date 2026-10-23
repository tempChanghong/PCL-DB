Imports System.Windows
Imports System.IO
Imports System.ComponentModel
Imports System.Threading
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Sub VerifyNameChecks(check As Action(Of Boolean, String))
        SelectVersion(_versions(0))
        InstanceName.Text = "bad/name"
        check(Not InstallButton.IsEnabled AndAlso InstanceName.ValidationMessage.Length > 0, "invalid instance names immediately show a PCL validation error and disable installation")
        InstanceName.ApplyTemplate()
        check(DirectCast(InstanceName.Template.FindName("labWrong", InstanceName), TextBlock).Visibility = Visibility.Collapsed AndAlso NameHint.Visibility = Visibility.Visible, "the PCL name card suppresses inline validation text and shows only its separate hint")
        SpeedLimitBox.Text = "不是速度"
        SpeedLimitBox.ApplyTemplate()
        check(SpeedLimitBox.ShowValidateResult AndAlso DirectCast(SpeedLimitBox.Template.FindName("labWrong", SpeedLimitBox), TextBlock).Visibility = Visibility.Visible, "settings text boxes retain their inline validation messages")
        SpeedLimitBox.Text = "0"
        InstanceName.Text = _selectedEntry.Id
        Dim edited = InstanceName.Text
        SelectComponent(_componentEntries(ComponentKind.Fabric)(0))
        check(InstanceName.Text = edited, "editing back to the default name still preserves the explicit PCL manual-edit state")
        VerifyNameKeyboard(check)
    End Sub

    Private Class NameKeySource
        Inherits PresentationSource
        Public Overrides Property RootVisual As Visual
        Public Overrides ReadOnly Property IsDisposed As Boolean
            Get
                Return False
            End Get
        End Property
        Protected Overrides Function GetCompositionTargetCore() As CompositionTarget
            Return Nothing
        End Function
    End Class

    Private Sub PressNameEnter()
        InstanceName.RaiseEvent(New KeyEventArgs(Keyboard.PrimaryDevice, New NameKeySource(), Environment.TickCount, Key.Enter) With {.RoutedEvent = Keyboard.KeyDownEvent})
    End Sub

    Private Sub VerifyNameKeyboard(check As Action(Of Boolean, String))
        Dim previous = SynchronizationContext.Current
        SynchronizationContext.SetSynchronizationContext(New DispatcherSynchronizationContext(Dispatcher))
        Try
            Dim work = VerifyNameKeyboardAsync(check)
            If Not work.IsCompleted Then
                Dim frame As New DispatcherFrame()
                Dim timer As New DispatcherTimer With {.Interval = TimeSpan.FromSeconds(10)}
                AddHandler timer.Tick, Sub() frame.Continue = False
                Dim completion = work.ContinueWith(Sub(finished) Dispatcher.BeginInvoke(New Action(Sub() frame.Continue = False)), TaskScheduler.Default)
                timer.Start() : Dispatcher.PushFrame(frame) : timer.Stop()
            End If
            If Not work.IsCompleted Then Throw New TimeoutException("Name keyboard workflow exceeded its deadline.")
            work.GetAwaiter().GetResult()
        Finally
            SynchronizationContext.SetSynchronizationContext(previous)
        End Try
    End Sub

    Private Async Function WaitForNameInstallAsync() As Task
        Dim watch = System.Diagnostics.Stopwatch.StartNew()
        While _busy
            If watch.ElapsedMilliseconds > 2500 Then Throw New TimeoutException("Name install fixture did not finish.")
            Await Task.Delay(10)
        End While
    End Function

    Private Async Function VerifyNameKeyboardAsync(check As Action(Of Boolean, String)) As Task
        Dim originalFolders = _folders.ToList(), originalFolder = _selectedFolder
        Dim originalRepair = RepairBox.IsChecked
        Dim originalIsolation = IsolationBox.SelectedIndex
        Dim fixtureRoot = Path.Combine(Path.GetTempPath(), "PclDownloadBridge-NameUi-" & Guid.NewGuid().ToString("N"), ".minecraft")
        Dim existing = Path.Combine(fixtureRoot, "versions", "existing")
        Directory.CreateDirectory(existing)
        File.WriteAllText(Path.Combine(existing, "existing.json"), "{}")
        File.WriteAllText(Path.Combine(existing, "keep.txt"), "unchanged")
        Try
            SelectVersion(_versions(0))
            SelectManualFolder(fixtureRoot)
            RepairBox.IsChecked = False
            For Each invalid In {"", " leading", "tail.", "bad!name", "COM0", "abc~1"}
                InstanceName.Text = invalid
                check(Not InstallButton.IsEnabled AndAlso NameHint.Visibility = Visibility.Visible AndAlso NameHint.Text = InstanceName.ValidationMessage, "name validation updates the actual hint and install state for '" & invalid & "'")
            Next
            check(NameHint.BorderThickness = New Thickness(3, 0, 0, 0) AndAlso NameHint.CornerRadius.TopLeft = 2 AndAlso
                DirectCast(NameHint.Background, SolidColorBrush).Color = Color.FromRgb(255, 221, 223), "name errors use PCL's red hint geometry and background")
            InstanceName.Text = "新的中文名称"
            check(InstallButton.IsEnabled AndAlso InstanceName.ValidationMessage = "" AndAlso NameHint.Visibility = Visibility.Collapsed, "correcting the name clears validation styling and restores installation")
            InstanceName.Text = "existing"
            check(Not InstallButton.IsEnabled AndAlso NameHint.Text.Contains("已存在"), "an existing instance name is rejected immediately in new-install mode")
            RepairBox.IsChecked = True
            check(InstallButton.IsEnabled AndAlso NameHint.Visibility = Visibility.Collapsed, "explicit repair mode permits an existing instance with a version JSON")
            SelectComponent(_componentEntries(ComponentKind.Fabric)(0))
            check(Not InstallButton.IsEnabled AndAlso CStr(InstallButton.ToolTip).Contains("组件") AndAlso ComponentHint.Text.Contains("组件"), "repair-mode availability still rejects selected loaders with a visible reason")
            ClearComponent(ComponentKind.Fabric)
            check(InstallButton.IsEnabled, "clearing the loader restores a valid repair request")
            RepairBox.IsChecked = False
            check(Not InstallButton.IsEnabled, "leaving repair mode restores the duplicate-name warning")
            Dim secondRoot = Path.Combine(Path.GetDirectoryName(fixtureRoot), "other-root")
            SelectManualFolder(secondRoot)
            check(InstallButton.IsEnabled AndAlso InstanceName.Text = "existing", "changing game folders rechecks a duplicate while preserving the chosen name")
            SelectManualFolder(fixtureRoot)
            check(Not InstallButton.IsEnabled AndAlso File.ReadAllText(Path.Combine(existing, "keep.txt")) = "unchanged", "returning to the original folder restores its collision without altering user files")
            InstanceName.Text = "missing-repair"
            RepairBox.IsChecked = True
            check(Not InstallButton.IsEnabled AndAlso NameHint.Text.Contains("JSON"), "repair mode immediately reports a missing version JSON")
            RepairBox.IsChecked = False
            File.WriteAllText(Path.Combine(fixtureRoot, "versions", "blocked"), "file marker")
            InstanceName.Text = "blocked"
            check(Not InstallButton.IsEnabled AndAlso NameHint.Text.Contains("同名文件"), "a same-name file is shown as an occupied installation target")

            Dim requests = 0, submittedName As String = Nothing, submittedRoot As String = Nothing, submittedIsolation As InstanceIsolationMode, submittedDefault As Integer?
            Dim gate As New TaskCompletionSource(Of InstallResult)(TaskCreationOptions.RunContinuationsAsynchronously)
            _installRunner = Function(entry, root, name, selection, isolation, defaultIsolation, token)
                requests += 1 : submittedName = name : submittedRoot = root
                submittedIsolation = isolation : submittedDefault = defaultIsolation
                Return gate.Task
            End Function
            InstanceName.Text = "bad/name"
            PressNameEnter()
            check(requests = 0 AndAlso Not _busy AndAlso StatusText.Text = NameHint.Text, "Enter on an invalid name reports the same validation reason without starting installation")
            InstanceName.Text = "键盘安装样本"
            IsolationBox.SelectedIndex = 5
            ShowSettings(Nothing, Nothing)
            PressNameEnter()
            check(requests = 0, "a hidden install name cannot submit from the settings page")
            ShowDownload(Nothing, Nothing)
            PressNameEnter()
            check(requests = 1 AndAlso _busy AndAlso submittedName = "键盘安装样本" AndAlso submittedRoot = fixtureRoot AndAlso DownloadProgress.Visibility = Visibility.Visible, "Enter submits the actual selected name and folder through the shared install workflow")
            IsolationBox.SelectedIndex = 1
            check(submittedIsolation = InstanceIsolationMode.FollowPcl AndAlso submittedDefault.GetValueOrDefault(-1) = 4 AndAlso Not IsolationBox.IsEnabled, "actual install submission captures its global isolation strategy before later UI changes")
            PressNameEnter()
            check(requests = 1, "repeated Enter during installation cannot submit a second request")
            gate.SetResult(New InstallResult With {.InstancePath = Path.Combine(fixtureRoot, "versions", submittedName), .FileCount = 3})
            Await WaitForNameInstallAsync()
            check(TaskTitle.Text.StartsWith("安装完成", StringComparison.Ordinal) AndAlso InstanceName.IsEnabled AndAlso _cancellation Is Nothing, "keyboard installation completion restores controls and drains its cancellation source")
            ResumeInstallation(Nothing, Nothing)
            RefreshInstallAvailability()
            Directory.CreateDirectory(Path.Combine(fixtureRoot, "versions", submittedName))
            StartInstall(Nothing, Nothing)
            check(requests = 1 AndAlso Not _busy AndAlso NameHint.Text.Contains("已存在"), "the click entry rechecks a destination created after the last name edit")
        Finally
            _installRunner = Nothing
            _folders = originalFolders
            FolderList.ItemsSource = Nothing : FolderList.ItemsSource = _folders
            FolderList.SelectedItem = originalFolder
            RepairBox.IsChecked = originalRepair
            IsolationBox.SelectedIndex = originalIsolation
            SelectVersion(_versions(0))
        End Try
    End Function
End Class
