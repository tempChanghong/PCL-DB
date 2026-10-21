Imports System.Threading

' Concurrent branches report snapshots; totals are cumulative per branch, never
' summed twice when another branch emits. Call the UI observer under one lock.
Friend NotInheritable Class InstallationProgress
    Private ReadOnly _target As IProgress(Of TransferProgress)
    Private ReadOnly _budget As DownloadBudget
    Private ReadOnly _gate As New Object
    Private ReadOnly _values As New Dictionary(Of String, TransferProgress)
    Private ReadOnly _phases As New Dictionary(Of String, Double) From {{"metadata", 1}, {"files", 0}, {"components", 0}, {"processors", 0}, {"commit", 0}}
    Private ReadOnly _supportCount As Integer
    Private ReadOnly _assetCount As Integer
    Friend Sub New(target As IProgress(Of TransferProgress), budget As DownloadBudget, supportCount As Integer, assetCount As Integer)
        _target = target : _budget = budget : _supportCount = supportCount : _assetCount = assetCount
    End Sub
    Friend Function Branch(name As String) As IProgress(Of TransferProgress)
        Return New InlineProgress(Sub(value) Record(name, value))
    End Function
    Friend Sub ComponentsComplete()
        SyncLock _gate
            _phases("components") = 1 : _phases("processors") = 1
            Record("components", New TransferProgress With {.Phase = "processors", .Stage = "组件安装完成", .StageFraction = 1,
                .NetworkBytes = Snapshot("components").NetworkBytes, .Reused = Snapshot("components").Reused,
                .DynamicSplits = Snapshot("components").DynamicSplits, .Retries = Snapshot("components").Retries})
        End SyncLock
    End Sub
    Friend Sub Failed(phase As String)
        SyncLock _gate
            _target?.Report(New TransferProgress With {.Phase = phase, .Stage = "安装失败", .StageFraction = _phases(phase),
                .PhaseFractions = New Dictionary(Of String, Double)(_phases), .Detail = "正在结束其余并行任务",
                .NetworkBytes = _values.Values.Sum(Function(v) v.NetworkBytes), .Reused = _values.Values.Sum(Function(v) v.Reused),
                .ActiveConnections = Volatile.Read(_budget.ActiveConnections)})
        End SyncLock
    End Sub
    Private Function Snapshot(name As String) As TransferProgress
        Dim result As TransferProgress = Nothing
        If _values.TryGetValue(name, result) Then Return result
        Return New TransferProgress()
    End Function
    Private Sub Record(name As String, value As TransferProgress)
        SyncLock _gate
            _values(name) = value
            If name = "components" Then
                _phases(value.Phase) = Math.Max(_phases(value.Phase), Math.Min(0.99, value.StageFraction))
            End If
            Dim supportDone = Snapshot("client").Completed + Snapshot("libraries").Completed
            Dim assetsDone = Snapshot("assets").Completed
            _phases("files") = 14 / 32.0 * If(_supportCount = 0, 1, supportDone / CDbl(_supportCount)) + 18 / 32.0 * If(_assetCount = 0, 1, assetsDone / CDbl(_assetCount))
            Dim phase = If(name = "components", value.Phase, "files")
            _target?.Report(New TransferProgress With {
                .Phase = phase, .Stage = If(name = "components", value.Stage, "下载原版游戏与资源文件"),
                .StageFraction = If(name = "components" AndAlso value.StageFraction < 0, -1, _phases(phase)), .PhaseFractions = New Dictionary(Of String, Double)(_phases),
                .PhaseTotal = If(name = "components", value.Total, _supportCount + _assetCount),
                .PhaseCompleted = If(name = "components", value.Completed, supportDone + assetsDone),
                .Detail = value.Detail, .CurrentFile = value.CurrentFile, .Source = value.Source,
                .NetworkBytes = _values.Values.Sum(Function(v) v.NetworkBytes),
                .Reused = _values.Values.Sum(Function(v) v.Reused), .Retries = _values.Values.Sum(Function(v) v.Retries),
                .DynamicSplits = _values.Values.Sum(Function(v) v.DynamicSplits), .ActiveConnections = Volatile.Read(_budget.ActiveConnections),
                .BytesPerSecond = _values.Where(Function(p) p.Value.Completed < p.Value.Total).Sum(Function(p) p.Value.BytesPerSecond),
                .Completed = supportDone + assetsDone + Snapshot("components").Completed,
                .Total = _supportCount + _assetCount + Snapshot("components").Total,
                .KnownBytes = _values.Values.Sum(Function(v) v.KnownBytes), .VerifiedBytes = _values.Values.Sum(Function(v) v.VerifiedBytes)})
        End SyncLock
    End Sub
End Class
