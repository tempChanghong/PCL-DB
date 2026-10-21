Imports System.Diagnostics

' Direct port of PCL ModNet.RefreshStat: 100ms samples, last 30 weighted
' from newest to oldest, and a non-decreasing floor based on 85% of last second.
Public Class PclTransferStatistics
    Private ReadOnly _clock As Stopwatch = Stopwatch.StartNew()
    Private ReadOnly _samples As New List(Of Double)
    Private _lastTime As Long
    Private _lastBytes As Long
    Private _speed As Double
    Private _floor As Double = 256 * 1024
    Public Sub Sample(bytes As Long)
        SyncLock _samples
            Dim now = _clock.ElapsedMilliseconds
            Dim delta = now - _lastTime
            If delta < 100 Then Return
            _samples.Insert(0, Math.Max(0, (bytes - _lastBytes) * 1000.0 / delta))
            If _samples.Count > 30 Then _samples.RemoveAt(30)
            _lastBytes = bytes
            _lastTime = now
            Dim total As Double
            Dim weights As Integer
            For i = 0 To _samples.Count - 1
                Dim weight = _samples.Count - i
                total += _samples(i) * weight
                weights += weight
            Next
            _speed = total / Math.Max(1, weights)
            If _samples.Count >= 10 Then _floor = Math.Max(_floor, _samples.Take(10).Average() * 0.85)
        End SyncLock
    End Sub
    Public ReadOnly Property Speed As Double
        Get
            SyncLock _samples
                Return _speed
            End SyncLock
        End Get
    End Property
    Public ReadOnly Property WantsMoreConnections As Boolean
        Get
            SyncLock _samples
                Return _speed < _floor
            End SyncLock
        End Get
    End Property
End Class
