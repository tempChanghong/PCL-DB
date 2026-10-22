Imports PclDownloadBridge.Core
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Sub VerifyIsolationOptions(check As Action(Of Boolean, String))
        Dim original = IsolationBox.SelectedIndex, repair = RepairBox.IsChecked
        Try
            check(IsolationBox.Items.Count = 3 AndAlso original = 0 AndAlso
                String.Join("/", IsolationBox.Items.Cast(Of PclComboBoxItem)().Select(Function(item) CStr(item.Content))) = "跟随 PCL/开启隔离/关闭隔离", "install page provides the three PCL isolation choices and defaults to follow")
            RepairBox.IsChecked = False
            For index = 0 To 2
                IsolationBox.SelectedIndex = index
                check(CInt(SelectedIsolation()) = index, "isolation choice maps to its semantic install value: " & index)
            Next
            check(IsolationHint.Text.Contains("共用 Mods") AndAlso IsolationHint.Text.Contains("不会被覆盖"), "shared Mods compatibility and content conflicts are explained inline")
            SetBusy(True)
            check(Not IsolationBox.IsEnabled, "the selected isolation mode is frozen while installing")
            SetBusy(False) : RepairBox.IsChecked = True
            check(Not IsolationBox.IsEnabled AndAlso IsolationHint.Text.Contains("保留已有实例"), "repair mode disables isolation changes and explains preservation")
            RepairBox.IsChecked = False
            check(IsolationBox.IsEnabled AndAlso SelectedIsolation() = InstanceIsolationMode.Disabled, "leaving repair mode restores the previous new-install isolation choice")
            IsolationBox.SelectedIndex = -1
            check(SelectedIsolation() = InstanceIsolationMode.FollowPcl, "an absent isolation selection safely falls back to PCL")
        Finally
            IsolationBox.SelectedIndex = original : RepairBox.IsChecked = repair
            RefreshIsolation(False)
        End Try
    End Sub
End Class
