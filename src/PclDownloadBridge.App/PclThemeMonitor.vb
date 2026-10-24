Imports System.Threading.Tasks
Imports System.Windows.Threading

' A small polling reader also sees atomic replacement, file recreation and
' directories recreated after a move. It owns no file/registry write handle.
Friend NotInheritable Class PclThemeMonitor
    Implements IDisposable
    Private ReadOnly _dispatcher As Dispatcher
    Private ReadOnly _timer As DispatcherTimer
    Private ReadOnly _read As Func(Of String, PclThemeSnapshot)
    Private ReadOnly _updated As Action(Of PclThemeSnapshot)
    Private _path As String
    Private _generation As Integer
    Private _running As Integer
    Private ReadOnly _reading As New HashSet(Of Integer)
    Private _disposed As Boolean
    Private _stableFingerprint As String
    Private _publishedState As String
    Friend ReadOnly Property Active As Boolean
        Get
            Return _timer.IsEnabled
        End Get
    End Property
    Friend ReadOnly Property PendingReads As Integer
        Get
            Return _running
        End Get
    End Property
    Friend Sub New(dispatcher As Dispatcher, updated As Action(Of PclThemeSnapshot), Optional read As Func(Of String, PclThemeSnapshot) = Nothing, Optional intervalMilliseconds As Integer = 500)
        _dispatcher = dispatcher : _updated = updated : _read = If(read, AddressOf PclThemeSnapshot.Read)
        _timer = New DispatcherTimer(DispatcherPriority.Background, dispatcher) With {.Interval = TimeSpan.FromMilliseconds(intervalMilliseconds)}
        AddHandler _timer.Tick, Sub() Refresh()
    End Sub
    Friend Sub Bind(path As String)
        If _disposed Then Return
        _generation += 1
        _timer.Stop() : _path = path : _stableFingerprint = Nothing : _publishedState = Nothing
        If String.IsNullOrEmpty(path) Then Return
        _timer.Start() : Refresh()
    End Sub
    Friend Sub Refresh()
        If _disposed OrElse Not Active OrElse _running >= 2 OrElse _reading.Contains(_generation) Then Return
        Dim generation = _generation, path = _path
        _running += 1 : _reading.Add(generation)
        Task.Run(Function()
                     Try
                         Return If(_read(path), PclThemeSnapshot.Failure("配置暂时无法读取，将自动重试。"))
                     Catch
                         Return PclThemeSnapshot.Failure("配置暂时无法读取，将自动重试。")
                     End Try
                 End Function).ContinueWith(
            Sub(completed)
                If _dispatcher.HasShutdownStarted OrElse _dispatcher.HasShutdownFinished Then Return
                Try
                    _dispatcher.BeginInvoke(DispatcherPriority.Background, New Action(
                    Sub()
                        _running -= 1
                        _reading.Remove(generation)
                        If _disposed OrElse generation <> _generation OrElse Not Active Then Return
                        Dim result = completed.Result
                        ' Require two consistent complete observations. Brief
                        ' partial writes/replace gaps cannot flash a fallback.
                        If result.Fingerprint <> _stableFingerprint Then
                            _stableFingerprint = result.Fingerprint
                            Return
                        End If
                        If result.StateKey = _publishedState Then Return
                        _publishedState = result.StateKey
                        _updated(result)
                    End Sub))
                Catch ex As InvalidOperationException When _dispatcher.HasShutdownStarted OrElse _dispatcher.HasShutdownFinished
                    ' Shutdown may start between the guard and BeginInvoke.
                    ' The retired read has no UI owner left to notify.
                End Try
            End Sub, TaskScheduler.Default)
    End Sub
    Public Sub Dispose() Implements IDisposable.Dispose
        If _disposed Then Return
        _disposed = True : _generation += 1 : _timer.Stop() : _path = Nothing
    End Sub
End Class
