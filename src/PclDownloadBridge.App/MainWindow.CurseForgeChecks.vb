Imports System.IO
Imports System.IO.Compression
Imports System.Windows
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Sub VerifyCurseForge(check As Action(Of Boolean, String), directory As String)
        Dim source = Path.Combine(directory, "ui-curseforge-" & Guid.NewGuid().ToString("N") & ".zip")
        Dim root = Path.Combine(directory, "cf-game-" & Guid.NewGuid().ToString("N"))
        Dim manifest As New JObject From {{"manifestType", "minecraftModpack"}, {"manifestVersion", 1}, {"name", " BetterMC preview "}, {"version", "1.5"}, {"overrides", "overrides"},
            {"minecraft", New JObject From {{"version", "1.20.1"}, {"modLoaders", New JArray(New JObject From {{"id", "forge-47.2.0"}})}}},
            {"files", New JArray(New JObject From {{"projectID", 1}, {"fileID", 1001}, {"required", True}}, New JObject From {{"projectID", 2}, {"fileID", 1002}, {"required", False}})}}
        Using archive = ZipFile.Open(source, ZipArchiveMode.Create), writer As New StreamWriter(archive.CreateEntry("manifest.json").Open())
            writer.Write(manifest.ToString())
        End Using
        Dim originalFolder = _selectedFolder, originalEntry = _selectedEntry, isolation = IsolationBox.SelectedIndex
        Try
            _selectedFolder = New GameFolder With {.Location = root, .Name = "CF fixture"}
            Dim package = CurseForgePackage.Read(source)
            SelectCurseForgePackage(package)
            check(ImportPackButton.Text = "导入整合包" AndAlso ImportPackButton.IsEnabled AndAlso AllowDrop, "CF import uses the PCL button and accepts ZIP drops")
            check(_downloadView = DownloadView.Install AndAlso PackOptionsCard.Visibility = Visibility.Visible AndAlso PackSummary.Text.Contains("47.2.0"), "CF selection opens install options with its exact loader version")
            check(InstanceName.Text = "BetterMC preview" AndAlso _selectedEntry.Id = "1.20.1", "CF preview trims the suggested name and fixes the game identity")
            check(IsolationCard.Visibility = Visibility.Collapsed AndAlso ComponentCards.Visibility = Visibility.Collapsed AndAlso RepairCard.Visibility = Visibility.Collapsed, "CF options expose fixed isolation without unrelated component or repair controls")
            check(PackOptionalFiles.Children.Count = 1 AndAlso TypeOf PackOptionalFiles.Children(0) Is PclCheckBox AndAlso Not DirectCast(PackOptionalFiles.Children(0), PclCheckBox).IsChecked.GetValueOrDefault(), "CF optional items use the PCL checkbox and start unselected")
            check(RefreshButton.Visibility = Visibility.Collapsed AndAlso _componentSelection.Entries.Count = 0, "CF import cannot replace its loader with an interactive catalogue selection")
            InstanceName.Text = "custom-pack"
            ShowSettings(Me, New RoutedEventArgs()) : ShowDownload(Me, New RoutedEventArgs())
            check(_packSelection Is package AndAlso InstanceName.Text = "custom-pack" AndAlso _downloadView = DownloadView.Install, "CF choices and edited name survive settings navigation")
            VersionSourceChanged(VersionSourceBox, New System.Windows.Controls.SelectionChangedEventArgs(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent, New Object() {}, New Object() {}))
            check(_packSelection Is package AndAlso Not _componentsDirty AndAlso _componentSelection.Entries.Count = 0, "CF source changes do not start mutable component catalogue selection")
            SetBusy(True)
            check(Not ImportPackButton.IsEnabled AndAlso Not PackOptionsCard.IsEnabled, "CF import and optional choices freeze while a task runs")
            SetBusy(False)
            check(ImportPackButton.IsEnabled AndAlso PackOptionsCard.IsEnabled, "CF controls are restored after a task finishes")
            IO.Directory.CreateDirectory(Path.Combine(root, "versions", "custom-pack"))
            File.WriteAllText(Path.Combine(root, "versions", "custom-pack", "custom-pack.json"), "{}")
            RepairBox.IsChecked = True
            check(RefreshInstallAvailability() IsNot Nothing AndAlso Not InstallButton.IsEnabled, "CF cannot use a hidden repair flag to overwrite an existing instance")
            InstanceName.Text = "new-pack" : RepairBox.IsChecked = False
            ResetTaskView("new-pack", True, root) : ShowProgress(Me, New RoutedEventArgs()) : ResumeInstallation(Me, New RoutedEventArgs())
            check(_packSelection Is package AndAlso PackOptionsCard.Visibility = Visibility.Visible AndAlso InstanceName.Text = "new-pack", "CF task return restores the same import options for retry")
            SelectVersion(_versions(0))
            check(_packSelection Is Nothing AndAlso PackOptionsCard.Visibility = Visibility.Collapsed AndAlso IsolationCard.Visibility = Visibility.Visible AndAlso RepairCard.Visibility = Visibility.Visible, "selecting vanilla restores the ordinary PCL install controls")
            check(IsolationBox.SelectedIndex = isolation AndAlso RefreshButton.Visibility = Visibility.Visible, "CF import preserves the independent vanilla isolation choice")
        Finally
            LeavePackMode()
            _selectedFolder = originalFolder
            If originalEntry IsNot Nothing Then SelectVersion(originalEntry)
        End Try
    End Sub
End Class
