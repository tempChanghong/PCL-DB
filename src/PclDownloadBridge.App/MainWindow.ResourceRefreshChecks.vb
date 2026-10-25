Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Async Function CheckResourceRefreshBoundariesAsync(directory As String, target As LauncherInstallTarget, owner As FrameworkElement, layout As Action, check As Action(Of Boolean, String)) As Task
        Dim release As New ModpackRelease With {.Kind = CommunityResourceKind.Mod, .Name = "刷新中的明确实例", .FileName = "refresh.jar", .GameVersions = {"1.20.1"}, .Loaders = {"fabric"}, .Size = 100, .Sha1 = New String("a"c, 40)}
        _managementProvider = Function(binding, token) Task.FromResult(InstalledInstanceCatalog.Scan(binding, token))
        Dim prompt = PromptResourceTargetAsync(release, target, owner)
        Await WaitPackUiAsync(Function() Not _resourceTargetLoading)
        ResourceTargetInstance.SelectedItem = ResourceTargetInstance.Items.OfType(Of ComboBoxItem)().Single(Function(item) DirectCast(item.Tag, InstalledInstance).Name = "Isolated")
        Dim available = InstalledInstanceCatalog.Scan(target, CancellationToken.None).Where(Function(item) item.Name <> "Isolated").ToList()
        _managementProvider = Function(binding, token) Task.FromResult(available)
        RefreshResourceTargets(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _resourceTargetLoading) : layout()
        File.WriteAllText(Path.Combine(directory, "refresh-selection-observation.json"), New JObject From {{"Previous", "Isolated"}, {"Current", TryCast(TryCast(ResourceTargetInstance.SelectedItem, ComboBoxItem)?.Tag, InstalledInstance)?.Name}, {"ConfirmEnabled", OnlinePackConfirm.IsEnabled}, {"Available", ResourceTargetInstance.Items.Count}}.ToString())
        check(ResourceTargetInstance.SelectedItem Is Nothing AndAlso Not OnlinePackConfirm.IsEnabled AndAlso ResourceTargetInstance.Items.OfType(Of ComboBoxItem)().Any(Function(item) item.IsEnabled), "refresh never substitutes another compatible instance when the explicit previous instance disappears")
        ResourceTargetInstance.SelectedItem = ResourceTargetInstance.Items.OfType(Of ComboBoxItem)().Single(Function(item) item.IsEnabled)
        check(OnlinePackConfirm.IsEnabled, "an unavailable previous instance can be replaced only by an explicit new selection")
        Dim metadataPath = Path.Combine(target.Root, "versions/Isolated/Isolated.json"), original = File.ReadAllBytes(metadataPath)
        Try
            Dim metadata = JObject.Parse(File.ReadAllText(metadataPath)) : metadata("libraries") = New JArray()
            _managementProvider = Function(binding, token) Task.FromResult(InstalledInstanceCatalog.Scan(binding, token))
            CompleteOnlinePrompt("no") : Await prompt
            prompt = PromptResourceTargetAsync(release, target, owner) : Await WaitPackUiAsync(Function() Not _resourceTargetLoading)
            ResourceTargetInstance.SelectedItem = ResourceTargetInstance.Items.OfType(Of ComboBoxItem)().Single(Function(item) DirectCast(item.Tag, InstalledInstance).Name = "Isolated")
            File.WriteAllText(metadataPath, metadata.ToString())
            RefreshResourceTargets(Nothing, Nothing) : Await WaitPackUiAsync(Function() Not _resourceTargetLoading) : layout()
            check(ResourceTargetInstance.SelectedItem Is Nothing AndAlso Not OnlinePackConfirm.IsEnabled AndAlso Not ResourceTargetInstance.Items.OfType(Of ComboBoxItem)().Single(Function(item) DirectCast(item.Tag, InstalledInstance).Name = "Isolated").IsEnabled, "refresh never redirects installation when the previous instance loses its required loader")
        Finally
            File.WriteAllBytes(metadataPath, original)
        End Try
        CompleteOnlinePrompt("no") : Await prompt
        prompt = PromptResourceTargetAsync(release, target, owner) : Await WaitPackUiAsync(Function() Not _resourceTargetLoading)
        PackNameDescription.Text = String.Concat(Enumerable.Repeat("刷新前后的正文阅读和选择 ", 70)) : layout()
        OnlinePromptBody.ForceMotionForChecks = True : OnlinePromptBody.MotionPolicy.PauseForChecks = True
        OnlinePromptBody.ScrollToHome() : layout() : OnlinePromptBody.ScrollDelta(240)
        Dim retired = OnlinePromptBody.MotionClock
        If retired Is Nothing OrElse Not PumpMotionUntil(Function() retired.IsPaused) Then Throw New InvalidOperationException("Refresh fixture did not create a paused wheel clock.")
        retired.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(35), TimeSeekOrigin.BeginTime) : layout()
        Dim delayed As New TaskCompletionSource(Of List(Of InstalledInstance))()
        _managementProvider = Function(binding, token) delayed.Task
        RefreshResourceTargets(Nothing, Nothing) : layout()
        File.WriteAllText(Path.Combine(directory, "refresh-scroll-observation.json"), New JObject From {{"ClockStillAttached", OnlinePromptBody.MotionClock IsNot Nothing}, {"Offset", OnlinePromptBody.VerticalOffset}, {"Loading", _resourceTargetLoading}}.ToString())
        check(OnlinePromptBody.MotionClock Is Nothing AndAlso OnlinePromptBody.VerticalOffset = 0 AndAlso Not OnlinePackConfirm.IsEnabled, "explicit target refresh retires the previous wheel gesture and returns the replacement body to its top")
        OnlinePromptBody.ScrollDelta(90) : Dim current = OnlinePromptBody.MotionClock
        retired.Controller.SkipToFill() : layout()
        check(current IsNot Nothing AndAlso OnlinePromptBody.MotionClock Is current, "a retired pre-refresh wheel completion cannot cancel a newer reading gesture")
        CompleteOnlinePrompt("no") : Await prompt
        _managementProvider = Function(binding, token) Task.FromResult(InstalledInstanceCatalog.Scan(binding, token))
        Dim replacement = PromptResourceTargetAsync(release, target, owner)
        delayed.SetResult(New List(Of InstalledInstance)()) : Await WaitPackUiAsync(Function() Not _resourceTargetLoading) : layout()
        check(ResourceTargetInstance.Items.Count > 0 AndAlso OnlinePromptBody.MotionClock Is Nothing AndAlso OnlinePromptBody.VerticalOffset = 0, "cancelling a pending refresh cannot let its empty late result replace a newly opened picker")
        CompleteOnlinePrompt("no") : Await replacement
        OnlinePromptBody.ForceMotionForChecks = False : OnlinePromptBody.MotionPolicy.PauseForChecks = False
    End Function
End Class
