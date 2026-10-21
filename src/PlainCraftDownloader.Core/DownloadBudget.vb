Imports System.Collections.Concurrent
Imports System.Diagnostics
Imports System.Threading

' PCL ModNet has one connection/speed budget across concurrent LoaderDownloads.
' Own this for the whole installation; engines own their individual file batches.
Public NotInheritable Class DownloadBudget
    Implements IDisposable
    Friend ReadOnly Connections As SemaphoreSlim
    Friend ReadOnly Hosts As New ConcurrentDictionary(Of String, SemaphoreSlim)(StringComparer.OrdinalIgnoreCase)
    Friend ReadOnly Transfers As New ConcurrentDictionary(Of String, SemaphoreSlim)(StringComparer.Ordinal)
    Friend ReadOnly MirrorGate As New SemaphoreSlim(1)
    Friend ReadOnly Clock As Stopwatch = Stopwatch.StartNew()
    Friend ReadOnly ConnectionLimit As Integer
    Friend ReadOnly HostLimit As Integer
    Friend LastMirrorRequest As Long
    Friend ActiveConnections As Integer
    Private ReadOnly _gate As New Object
    Private _due As Double
    Private _rate As Long
    Private _batches As Integer
    Public Property BytesPerSecondLimit As Long

    Public Sub New(Optional connections As Integer = 16, Optional limit As Long = 0)
        ConnectionLimit = Math.Max(1, Math.Min(connections, 64))
        HostLimit = Math.Min(ConnectionLimit, 16)
        Me.Connections = New SemaphoreSlim(ConnectionLimit)
        BytesPerSecondLimit = limit
    End Sub
    Friend Sub BeginBatch()
        SyncLock _gate
            If _batches = 0 Then
                _due = Clock.Elapsed.TotalSeconds
                _rate = BytesPerSecondLimit
            End If
            _batches += 1
        End SyncLock
    End Sub
    Friend Sub EndBatch()
        SyncLock _gate
            _batches -= 1
        End SyncLock
    End Sub
    Friend Function ReserveBytes(count As Integer) As Double
        Dim rate = BytesPerSecondLimit
        If rate <= 0 Then Return Clock.Elapsed.TotalSeconds
        SyncLock _gate
            Dim now = Clock.Elapsed.TotalSeconds
            If _rate <> rate Then
                _due = now
                _rate = rate
            End If
            _due = Math.Max(now - 0.1, _due) + count / CDbl(rate)
            Return _due
        End SyncLock
    End Function
    Public Sub Dispose() Implements IDisposable.Dispose
        Connections.Dispose()
        MirrorGate.Dispose()
        For Each host In Hosts.Values : host.Dispose() : Next
        For Each transfer In Transfers.Values : transfer.Dispose() : Next
    End Sub
End Class
