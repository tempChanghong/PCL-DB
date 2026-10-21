Imports System.Diagnostics
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports PlainCraftDownloader.Core
Imports PlainCraftDownloader.Controls

Partial Public Class MainWindow
    Private ReadOnly _taskRows As New Dictionary(Of String, TaskPhaseView)
    Private ReadOnly _taskClock As New Stopwatch
    Private _taskOverall As Double
    Private _parallelOverall As Double
    Private _lastNetworkBytes As Long
    Private _lastReused As Integer
    Private _lastSplits As Integer
    Private _lastRetries As Integer
    Private _lastPhase As String
    Private _taskName As String
    Private _taskHasComponents As Boolean
    Private _taskGeneration As Long
    Private _taskFinished As Boolean
    Private _taskCancelling As Boolean
    Private Class TaskPhaseView
        Public Label As TextBlock
        Public State As TextBlock
        Public Icon As Shapes.Path
        Public Complete As Boolean
    End Class
    Private Const CompleteIcon As String = "F1 M23.7501,33.25 L34.8334,44.3333 L52.2499,22.1668 L56.9999,26.9168 L34.8334,53.8333 L19.0001,38 L23.7501,33.25 Z"
    Private Const WaitingIcon As String = "F1 M5,0 a5,5 360 1 0 0,0.0001 m15,0 a5,5 360 1 0 0,0.0001 m15,0 a5,5 360 1 0 0,0.0001 Z"
    Private Const FailedIcon As String = "F1 M2.5,0 L0,2.5 7.5,10 0,17.5 2.5,20 10,12.5 17.5,20 20,17.5 12.5,10 20,2.5 17.5,0 10,7.5 2.5,0Z"
    Private Sub ResetTaskView(name As String, hasComponents As Boolean, Optional root As String = Nothing)
        _taskGeneration += 1
        _taskFinished = False : _taskCancelling = False
        _hasTask = True
        SetDownloadView(_downloadView)
        _taskRows.Clear() : TaskPhases.Children.Clear()
        _taskOverall = 0 : _parallelOverall = 0 : _lastNetworkBytes = 0 : _lastReused = 0 : _lastPhase = Nothing
        _lastSplits = 0 : _lastRetries = 0
        _taskName = name : _taskHasComponents = hasComponents : _taskClock.Restart()
        ResetTaskResult(If(root, _selectedFolder?.Location))
        For Each pair In New Dictionary(Of String, String) From {{"metadata", "读取版本信息与资源索引"}, {"files", "下载原版游戏与资源文件"}, {"components", "下载所选组件与支持库"}, {"processors", "运行组件安装处理器"}, {"commit", "校验结果并写入实例"}}
            If Not hasComponents AndAlso (pair.Key = "components" OrElse pair.Key = "processors") Then Continue For
            Dim row As New Grid With {.Height = 26}
            row.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(50)})
            row.ColumnDefinitions.Add(New ColumnDefinition())
            Dim label As New TextBlock With {.Text = pair.Value, .VerticalAlignment = VerticalAlignment.Top}
            Dim state As New TextBlock With {.Foreground = PclAnimation.Brush("ColorBrush3"), .HorizontalAlignment = HorizontalAlignment.Center, .Visibility = Visibility.Collapsed}
            Dim icon As New Shapes.Path With {.Data = Geometry.Parse(WaitingIcon), .Width = 18, .Height = 6, .Stretch = Stretch.Uniform, .Fill = PclAnimation.Brush("ColorBrush3"), .HorizontalAlignment = HorizontalAlignment.Center, .VerticalAlignment = VerticalAlignment.Top, .Margin = New Thickness(0, 7, 0, 0)}
            state.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush3")
            icon.SetResourceReference(Shapes.Path.FillProperty, "ColorBrush3")
            Grid.SetColumn(label, 1)
            row.Children.Add(icon) : row.Children.Add(state) : row.Children.Add(label)
            TaskPhases.Children.Add(row)
            _taskRows(pair.Key) = New TaskPhaseView With {.Label = label, .State = state, .Icon = icon}
        Next
        TaskTitle.Text = name & " 安装"
        TaskPercent.Text = "0.00 %" : TaskSpeed.Text = "0 B/s" : TaskRemaining.Text = "—"
        TaskConnections.Text = "—" : TaskProgress.IsIndeterminate = False : TaskProgress.Value = 0
        TaskDetail.Text = "准备开始安装…"
        TransferStats.Text = "暂无传输数据" : TransferFile.Text = "—" : TransferSource.Text = "—"
        StatusText.Text = "准备安装 " & name
        TaskProgress.ToolTip = "按安装阶段权重计算总进度；文件数量为当前下载阶段。"
    End Sub
    Private Sub FinishTaskView(success As Boolean, detail As String, Optional cancelled As Boolean = False)
        _taskFinished = True
        _taskClock.Stop()
        _taskOutcome = If(success, TaskOutcome.Succeeded, If(cancelled OrElse _taskCancelling, TaskOutcome.Cancelled, TaskOutcome.Failed))
        _taskDiagnostic = detail
        CancelButton.Visibility = Visibility.Collapsed
        TaskProgress.IsIndeterminate = False
        If success Then
            For Each row In _taskRows.Values : MarkComplete(row) : Next
            TaskProgress.Value = 1 : TaskPercent.Text = "100.00 %" : TaskRemaining.Text = "0"
        ElseIf _taskOutcome = TaskOutcome.Cancelled Then
            For Each pair In _taskRows
                If pair.Value.Complete Then Continue For
                pair.Value.Icon.Visibility = Visibility.Collapsed
                pair.Value.State.Visibility = Visibility.Visible
                pair.Value.State.Text = If(pair.Key = _lastPhase, "已取消", "未完成")
                pair.Value.State.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray2")
            Next
        ElseIf _lastPhase IsNot Nothing AndAlso _taskRows.ContainsKey(_lastPhase) Then
            Dim row = _taskRows(_lastPhase)
            row.State.Visibility = Visibility.Collapsed : row.Icon.Visibility = Visibility.Visible
            row.Icon.Data = Geometry.Parse(FailedIcon)
            row.Icon.Width = 15 : row.Icon.Height = 15 : row.Icon.Margin = New Thickness(0, 1, 0, 0)
        End If
        TaskSpeed.Text = "0 B/s"
        TaskConnections.Text = "0"
        TaskDetail.Text = detail & " · 用时 " & _taskClock.Elapsed.ToString("mm\:ss")
        RefreshTaskResultActions()
    End Sub
    Private Sub MarkComplete(row As TaskPhaseView)
        row.Complete = True : row.State.Visibility = Visibility.Collapsed
        row.Icon.Visibility = Visibility.Visible : row.Icon.Data = Geometry.Parse(CompleteIcon)
        row.Icon.Width = 15 : row.Icon.Height = 16 : row.Icon.Margin = New Thickness(0, 3, 0, 0)
    End Sub
    Private Sub RenderTaskProgress(value As TransferProgress)
        Dim phase = value.Phase
        If Not _taskRows.ContainsKey(phase) Then Return
        If value.PhaseFractions Is Nothing Then
            If _lastPhase IsNot Nothing AndAlso _lastPhase <> phase Then MarkComplete(_taskRows(_lastPhase))
        Else
            For Each pair In value.PhaseFractions
                If Not _taskRows.ContainsKey(pair.Key) Then Continue For
                Dim row = _taskRows(pair.Key)
                If pair.Value >= 1 Then
                    MarkComplete(row)
                ElseIf pair.Value > 0 Then
                    row.Complete = False : row.Icon.Visibility = Visibility.Collapsed : row.State.Visibility = Visibility.Visible
                    row.State.Text = Math.Floor(pair.Value * 100).ToString(Globalization.CultureInfo.InvariantCulture) & "%"
                End If
            Next
        End If
        _lastPhase = phase
        Dim current = _taskRows(phase)
        current.Complete = False : current.Icon.Visibility = Visibility.Collapsed : current.State.Visibility = Visibility.Visible
        Dim fraction = If(value.StageFraction < 0, -1, Math.Max(0, Math.Min(1, value.StageFraction)))
        current.State.Text = If(fraction < 0, "…", Math.Floor(fraction * 100).ToString(Globalization.CultureInfo.InvariantCulture) & "%")
        Dim phaseTotal = If(value.PhaseTotal >= 0, value.PhaseTotal, value.Total)
        Dim phaseCompleted = If(value.PhaseTotal >= 0, value.PhaseCompleted, value.Completed)
        current.Label.Text = value.Stage & If(phaseTotal > 0, "（" & phaseCompleted & "/" & phaseTotal & "）", "")
        current.Label.ToolTip = current.Label.Text
        Dim start As Double, weight As Double
        Select Case phase
            Case "metadata" : start = 0 : weight = 0.03
            Case "files" : start = 0.03 : weight = If(_taskHasComponents, 0.62, 0.92)
            Case "components" : start = 0.65 : weight = 0.16
            Case "processors" : start = 0.81 : weight = 0.16
            Case Else : start = 0.97 : weight = 0.03
        End Select
        _taskOverall = Math.Max(_taskOverall, Math.Min(0.99, start + weight * Math.Max(0, fraction)))
        If value.PhaseFractions IsNot Nothing Then
            Dim states = value.PhaseFractions
            Dim overall = states("metadata") * 0.03 + states("files") * If(_taskHasComponents, 0.62, 0.92)
            If _taskHasComponents Then overall += states("components") * 0.16 + states("processors") * 0.16
            overall += states("commit") * 0.03
            ' A later phase can finish while resource files still run.
            _taskOverall = Math.Min(0.99, Math.Max(_parallelOverall, overall))
            _parallelOverall = _taskOverall
            If states(phase) >= 1 Then MarkComplete(current)
        End If
        _lastNetworkBytes = Math.Max(_lastNetworkBytes, value.NetworkBytes)
        _lastReused = Math.Max(_lastReused, value.Reused)
        _lastSplits = Math.Max(_lastSplits, value.DynamicSplits)
        _lastRetries = Math.Max(_lastRetries, value.Retries)
        TaskTitle.Text = _taskName & " 安装"
        TaskProgress.IsIndeterminate = False : TaskProgress.Value = _taskOverall
        TaskPercent.Text = (_taskOverall * 100).ToString("0.00") & " %"
        TaskSpeed.Text = FormatBytes(value.BytesPerSecond) & "/s"
        TaskRemaining.Text = If(value.Total > 0, Math.Max(0, value.Total - value.Completed).ToString("N0"), "—")
        TaskConnections.Text = value.ActiveConnections.ToString("N0")
        TaskDetail.Text = "已传输 " & FormatBytes(_lastNetworkBytes) & " · 已复用 " & _lastReused.ToString("N0") & " 个文件 · 用时 " & _taskClock.Elapsed.ToString("mm\:ss")
        TransferStats.Text = value.ActiveConnections & " 个活动连接 · 累计分段 " & _lastSplits & " 次 · 失败尝试 " & _lastRetries & " 次" & If(value.KnownBytes > 0, vbCrLf & "当前批次已校验 " & FormatBytes(value.VerifiedBytes) & " / " & FormatBytes(value.KnownBytes), "")
        TransferFile.Text = If(value.CurrentFile, value.Detail)
        TransferSource.Text = If(String.IsNullOrWhiteSpace(value.Source), "正在处理本地安装步骤", "最近使用的来源：" & value.Source)
        Dim detail = If(value.Detail, "")
        StatusText.Text = If(detail.Length > 240, detail.Substring(0, 240) & "…（完整内容见安装器日志）", detail)
    End Sub
End Class
