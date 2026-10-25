Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private _resourceTargetPrompt As Boolean, _resourceTargetLoading As Boolean, _resourceWorldLoading As Boolean, _resourceTargetValidating As Boolean
    Private _resourceTargetRequest As CancellationTokenSource, _resourceWorldRequest As CancellationTokenSource
    Private _resourceTargetRelease As ModpackRelease, _confirmedResourcePlan As ResourceInstallationPlan
    Private _resourceTargetProblem As String
    Private _resourceInstallRunner As Func(Of ResourceInstallationPlan, String, SourcePolicy, IProgress(Of TransferProgress), CancellationToken, Task)

    Private Async Sub InstallCommunityResource(release As ModpackRelease, owner As FrameworkElement)
        If Not CanUseResourceDetail(owner) OrElse _localModWriting OrElse _readingPack OrElse Not _packReleases.Contains(release) Then Return
        Dim project = ActivePackProject()
        If project Is Nothing OrElse project.Id <> release.ProjectId OrElse project.Platform <> release.Platform OrElse project.Kind <> release.Kind Then Return
        If _selectedFolder Is Nothing Then ChooseInstallFolder(Nothing, Nothing) : Return
        Dim target = CurrentInstallTarget(), snapshot = release.Copy()
        _choosingOnline = True
        Try
            Dim plan = Await PromptResourceTargetAsync(snapshot, target, owner)
            If plan Is Nothing OrElse _closing OrElse ActivePackProject() IsNot project OrElse Not SameManagementTarget(plan.LauncherTarget, CurrentInstallTarget()) Then Return
            Await RunResourceSaveAsync(plan.Release, plan.Destination, plan)
        Catch ex As OperationCanceledException
        Catch ex As Exception
            If Not _closing Then PackProjectActionStatus.Text = ex.GetBaseException().Message : PackProjectActionStatus.Visibility = Visibility.Visible
        Finally
            _choosingOnline = False
        End Try
    End Sub
    Private Async Function PromptResourceTargetAsync(release As ModpackRelease, target As LauncherInstallTarget, owner As FrameworkElement) As Task(Of ResourceInstallationPlan)
        _resourceTargetPrompt = True : _resourceTargetRelease = release.Copy() : _confirmedResourcePlan = Nothing
        _resourceTargetLoading = True : _resourceWorldLoading = False : _resourceTargetValidating = False : _resourceTargetProblem = Nothing
        _resourceTargetRequest = New CancellationTokenSource()
        Dim request = _resourceTargetRequest
        ResourceTargetInstance.Items.Clear() : ResourceTargetWorld.Items.Clear()
        ResourceTargetInstance.IsEnabled = False : ResourceTargetWorld.IsEnabled = False
        ResourceTargetWorldRow.Visibility = If(release.Kind = CommunityResourceKind.DataPack, Visibility.Visible, Visibility.Collapsed)
        ResourceTargetLocation.Text = "正在读取已安装版本…"
        Dim prompt = PromptOnlinePackAsync("安装到版本", CommunityResources.Name(release.Kind) & " · " & release.Name, target.Root, "", True, request.Token, owner)
        OnlinePackConfirm.Text = "安装" : OnlinePackCancel.Text = "取消"
        LoadResourceTargetsAsync(target, request)
        Try
            Dim result = Await prompt
            Return If(result = "yes", _confirmedResourcePlan, Nothing)
        Finally
            request.Cancel()
            If _resourceTargetRequest Is request Then _resourceTargetRequest = Nothing
            request.Dispose()
        End Try
    End Function
    Private Async Sub LoadResourceTargetsAsync(target As LauncherInstallTarget, request As CancellationTokenSource)
        Try
            Dim instances = Await _managementProvider(target, request.Token)
            If request.IsCancellationRequested OrElse _resourceTargetRequest IsNot request OrElse Not _resourceTargetPrompt Then Return
            Dim preferred = ResourceSaveContext(), selected As PclComboBoxItem = Nothing
            For Each instance In instances
                Dim problem = CommunityResourceInstallation.CompatibilityProblem(_resourceTargetRelease, instance)
                Dim item As New PclComboBoxItem With {.Content = instance.Name & " · " & instance.MinecraftVersion & " · " & instance.LoaderSummary, .Tag = instance, .IsEnabled = problem Is Nothing, .ToolTip = If(problem, instance.RunDirectory)}
                ResourceTargetInstance.Items.Add(item)
                If problem Is Nothing AndAlso (selected Is Nothing OrElse (preferred IsNot Nothing AndAlso preferred.Name = instance.Name AndAlso SameManagementTarget(preferred.Target, instance.Target))) Then selected = item
            Next
            _resourceTargetLoading = False : ResourceTargetInstance.IsEnabled = True
            ResourceTargetInstance.SelectedItem = selected
            If selected Is Nothing Then _resourceTargetProblem = "没有兼容的已安装版本，请更换资源版本或使用另存为。"
            ValidateResourceTarget()
            ResourceTargetInstance.Focus()
        Catch ex As OperationCanceledException
        Catch ex As Exception
            If _resourceTargetRequest Is request AndAlso _resourceTargetPrompt Then _resourceTargetLoading = False : _resourceTargetProblem = ex.GetBaseException().Message : ValidateResourceTarget()
        End Try
    End Sub
    Private Async Sub ResourceTargetInstanceChanged(sender As Object, e As SelectionChangedEventArgs)
        If Not _resourceTargetPrompt Then Return
        _resourceWorldRequest?.Cancel()
        _resourceTargetProblem = Nothing : _resourceWorldLoading = False
        ResourceTargetWorld.Items.Clear() : ResourceTargetWorld.IsEnabled = False
        Dim instance = TryCast(TryCast(ResourceTargetInstance.SelectedItem, ComboBoxItem)?.Tag, InstalledInstance)
        If instance Is Nothing OrElse _resourceTargetRelease.Kind <> CommunityResourceKind.DataPack Then ValidateResourceTarget() : Return
        Dim request = CancellationTokenSource.CreateLinkedTokenSource(_resourceTargetRequest.Token)
        _resourceWorldRequest = request : _resourceWorldLoading = True : ValidateResourceTarget()
        Try
            Dim worlds = Await Task.Run(Function() CommunityResourceInstallation.Worlds(instance, request.Token), request.Token)
            If request.IsCancellationRequested OrElse _resourceWorldRequest IsNot request OrElse Not _resourceTargetPrompt OrElse TryCast(TryCast(ResourceTargetInstance.SelectedItem, ComboBoxItem)?.Tag, InstalledInstance) IsNot instance Then Return
            For Each world In worlds : ResourceTargetWorld.Items.Add(New PclComboBoxItem With {.Content = world.Name, .Tag = world, .ToolTip = world.Folder}) : Next
            ResourceTargetWorld.SelectedIndex = -1 : ResourceTargetWorld.IsEnabled = worlds.Count > 0
            If worlds.Count = 0 Then _resourceTargetProblem = "没有可用存档，请先在游戏中创建存档，或使用另存为。"
        Catch ex As OperationCanceledException
        Catch ex As Exception
            If _resourceWorldRequest Is request AndAlso _resourceTargetPrompt Then _resourceTargetProblem = ex.GetBaseException().Message
        Finally
            If _resourceWorldRequest Is request Then _resourceWorldRequest = Nothing : _resourceWorldLoading = False : ValidateResourceTarget()
            request.Dispose()
        End Try
    End Sub
    Private Sub ResourceTargetWorldChanged(sender As Object, e As SelectionChangedEventArgs)
        If _resourceTargetPrompt Then _resourceTargetProblem = Nothing : ValidateResourceTarget()
    End Sub
    Private Sub ValidateResourceTarget()
        If Not _resourceTargetPrompt Then Return
        Dim instance = TryCast(TryCast(ResourceTargetInstance.SelectedItem, ComboBoxItem)?.Tag, InstalledInstance)
        Dim world = TryCast(TryCast(ResourceTargetWorld.SelectedItem, ComboBoxItem)?.Tag, InstalledWorld)
        Dim problem = _resourceTargetProblem
        If problem Is Nothing Then
            If _resourceTargetLoading Then
                problem = "正在读取已安装版本…"
            ElseIf _resourceWorldLoading Then
                problem = "正在读取存档…"
            ElseIf instance Is Nothing Then
                problem = "请选择安装目标版本。"
            Else
                problem = CommunityResourceInstallation.CompatibilityProblem(_resourceTargetRelease, instance)
                If problem Is Nothing AndAlso _resourceTargetRelease.Kind = CommunityResourceKind.DataPack AndAlso world Is Nothing Then problem = "请选择存档文件夹。"
            End If
        End If
        OnlinePackConfirm.IsEnabled = problem Is Nothing AndAlso Not _resourceTargetValidating
        OnlinePackNameHint.Text = If(problem, "") : OnlinePackNameHint.Visibility = If(problem Is Nothing, Visibility.Collapsed, Visibility.Visible)
        OnlinePackNameHint.Theme = If(_resourceTargetLoading OrElse _resourceWorldLoading, PclHintTheme.Blue, PclHintTheme.Red)
        If instance IsNot Nothing Then
            Try
                Dim path = CommunityResourceInstallation.ResourceDirectory(_resourceTargetRelease, instance, world)
                ResourceTargetLocation.Text = path & If(instance.IsIsolated = False, vbCrLf & "此目录与其他版本共享。", "") & If(_resourceTargetRelease.Kind = CommunityResourceKind.Shader, vbCrLf & "仅安装光影文件，需在游戏中选择并启用。", "")
            Catch ex As Exception
                ResourceTargetLocation.Text = instance.RunDirectory
            End Try
        Else
            ResourceTargetLocation.Text = ""
        End If
    End Sub
    Private Async Sub ConfirmResourceTargetAsync()
        If Not _resourceTargetPrompt OrElse _resourceTargetValidating OrElse Not OnlinePackConfirm.IsEnabled Then Return
        Dim request = _resourceTargetRequest, instance = DirectCast(DirectCast(ResourceTargetInstance.SelectedItem, ComboBoxItem).Tag, InstalledInstance)
        Dim world = TryCast(TryCast(ResourceTargetWorld.SelectedItem, ComboBoxItem)?.Tag, InstalledWorld)
        _resourceTargetValidating = True : ResourceTargetInstance.IsEnabled = False : ResourceTargetWorld.IsEnabled = False : ValidateResourceTarget()
        Try
            Dim release = _resourceTargetRelease.Copy()
            Dim plan = Await Task.Run(Function() CommunityResourceInstallation.Create(release, instance, world, request.Token), request.Token)
            If request.IsCancellationRequested OrElse _resourceTargetRequest IsNot request OrElse Not _resourceTargetPrompt Then Return
            _confirmedResourcePlan = plan : CompleteOnlinePrompt("yes")
        Catch ex As OperationCanceledException
        Catch ex As Exception
            If _resourceTargetPrompt AndAlso _resourceTargetRequest Is request Then _resourceTargetProblem = ex.GetBaseException().Message
        Finally
            If _resourceTargetPrompt AndAlso _resourceTargetRequest Is request Then
                _resourceTargetValidating = False : ResourceTargetInstance.IsEnabled = True
                ResourceTargetWorld.IsEnabled = ResourceTargetWorld.Items.Count > 0 : ValidateResourceTarget()
            End If
        End Try
    End Sub
    Private Sub InvalidateResourceManagement(plan As ResourceInstallationPlan)
        If plan Is Nothing Then Return
        _managementDirty = True
        If _localModInstance IsNot Nothing AndAlso SameGameRoot(_localModInstance.RunDirectory, plan.RunDirectory) Then
            CancelLocalModRequest()
            _localModReadSucceeded = False
        End If
    End Sub
End Class
