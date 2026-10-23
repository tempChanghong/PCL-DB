Imports System.Diagnostics
Imports System.Threading
Imports System.Windows
Imports System.Windows.Threading

Namespace Controls
    ' PCL MyPageRight: LoaderWait=400ms, then LoaderEnter, then
    ' LoaderStayForce=400ms measured after the actual entry has settled.
    Friend NotInheritable Class PclLoadingPresentation
        Friend Enum Phases
            Idle
            Waiting
            Entering
            MinimumStay
            Loading
            Hidden
        End Enum
        Friend Const WaitMilliseconds As Double = 400
        Friend Const MinimumMilliseconds As Double = 400
        Private ReadOnly _host As FrameworkElement
        Private ReadOnly _enabled As Func(Of Boolean)
        Private ReadOnly _entry As Func(Of PclSurfaceMotion)
        Private ReadOnly _timer As DispatcherTimer
        Private ReadOnly _watch As Stopwatch = Stopwatch.StartNew()
        Private _owner As Object
        Private _prepare As Action
        Private _show As Action
        Private _ready As TaskCompletionSource(Of Boolean)
        Private _registration As CancellationTokenRegistration
        Private _motion As PclSurfaceMotion
        Private _settled As EventHandler
        Private _finished As Boolean
        Private _due As Double
        Friend Property NowForChecks As Func(Of Double)
        Private _phase As Phases = Phases.Idle
        Friend Property Phase As Phases
            Get
                Return _phase
            End Get
            Private Set(value As Phases)
                _phase = value
            End Set
        End Property
        Friend ReadOnly Property TimerArmed As Boolean
            Get
                Return _timer.IsEnabled
            End Get
        End Property
        Friend ReadOnly Property CompletionPending As Boolean
            Get
                Return _finished
            End Get
        End Property

        Friend Sub New(host As FrameworkElement, enabled As Func(Of Boolean), entry As Func(Of PclSurfaceMotion))
            _host = host : _enabled = enabled : _entry = entry
            _timer = New DispatcherTimer(DispatcherPriority.Background, host.Dispatcher)
            AddHandler _timer.Tick, Sub() Pulse()
            AddHandler host.Unloaded, Sub() Reset()
            AddHandler host.Loaded, Sub() ResumeHidden()
            AddHandler host.IsVisibleChanged, AddressOf VisibilityChanged
        End Sub

        Friend Sub Start(owner As Object, token As CancellationToken, prepare As Action, show As Action)
            Reset()
            _owner = owner : _prepare = prepare : _show = show
            _ready = New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
            _registration = token.Register(Sub() _host.Dispatcher.BeginInvoke(New Action(Sub()
                                                                                           If _owner Is owner Then Reset()
                                                                                       End Sub)))
            If token.IsCancellationRequested Then Reset() : Return
            If _enabled() Then
                If _host.IsVisible AndAlso _host.IsLoaded Then
                    BeginWait()
                Else
                    Phase = Phases.Hidden : prepare()
                End If
            Else
                Phase = Phases.Loading : show()
            End If
        End Sub

        Friend Function CompleteAsync(owner As Object) As Task
            If _owner IsNot owner Then Return Task.CompletedTask
            Dim pending = _ready.Task
            _finished = True
            If Phase = Phases.Waiting OrElse Phase = Phases.Hidden OrElse Phase = Phases.Loading OrElse Not _enabled() Then Reset()
            Return pending
        End Function

        Friend Sub Cancel(owner As Object)
            If _owner Is owner Then Reset()
        End Sub

        Friend Sub Reset()
            _timer.Stop() : DetachEntry()
            _registration.Dispose() : _registration = Nothing
            Dim pending = _ready
            _owner = Nothing : _ready = Nothing : _prepare = Nothing : _show = Nothing
            _finished = False : Phase = Phases.Idle
            pending?.TrySetResult(True)
        End Sub

        Private Function Now() As Double
            Return If(NowForChecks Is Nothing, _watch.Elapsed.TotalMilliseconds, NowForChecks.Invoke())
        End Function
        Private Sub Arm(milliseconds As Double)
            _timer.Stop() : _due = Now() + milliseconds
            If NowForChecks Is Nothing Then
                _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, milliseconds)) : _timer.Start()
            End If
        End Sub
        Private Sub BeginWait()
            Phase = Phases.Waiting : _prepare()
            Arm(WaitMilliseconds)
        End Sub
        Private Sub DetachEntry()
            If _motion IsNot Nothing AndAlso _settled IsNot Nothing Then RemoveHandler _motion.Settled, _settled
            _motion = Nothing : _settled = Nothing
        End Sub
        Private Sub Entered(owner As Object)
            If _owner IsNot owner OrElse Phase <> Phases.Entering Then Return
            DetachEntry()
            If Not _enabled() Then
                If _finished Then Reset() Else Phase = Phases.Loading
            Else
                Phase = Phases.MinimumStay : Arm(MinimumMilliseconds)
            End If
        End Sub

        Friend Sub Pulse()
            If _owner Is Nothing Then Return
            If Not _host.IsVisible Then Return
            If Not _enabled() Then
                Dim show = _show, finished = _finished, wasWaiting = Phase = Phases.Waiting OrElse Phase = Phases.Hidden
                _timer.Stop() : DetachEntry()
                If finished Then
                    Reset()
                Else
                    Phase = Phases.Loading
                    If wasWaiting Then show()
                End If
                Return
            End If
            If Now() < _due Then Return
            _timer.Stop()
            If Phase = Phases.Waiting Then
                Dim owner = _owner
                Phase = Phases.Entering : _show()
                ' ChangePackContent may hand this entry to the existing root or
                ' category controller. Follow that controller rather than a guessed duration.
                If _owner IsNot owner OrElse Phase <> Phases.Entering Then Return
                _motion = _entry()
                If _motion Is Nothing OrElse _motion.Transition Is Nothing Then
                    Entered(owner)
                Else
                    _settled = Sub() Entered(owner)
                    AddHandler _motion.Settled, _settled
                End If
            ElseIf Phase = Phases.MinimumStay Then
                If _finished Then
                    Reset()
                Else
                    Phase = Phases.Loading
                End If
            End If
        End Sub

        Private Sub VisibilityChanged(sender As Object, e As DependencyPropertyChangedEventArgs)
            If _owner Is Nothing Then Return
            If Not _host.IsVisible Then
                _timer.Stop() : DetachEntry()
                If _finished Then
                    Reset() ' Completed hidden data can be cached immediately.
                ElseIf Phase <> Phases.Loading OrElse _enabled() Then
                    Phase = Phases.Hidden
                    _prepare()
                End If
            ElseIf Phase = Phases.Hidden Then
                ' Visibility can change inside the parent's changePage callback,
                ' before its entry clock exists. Resume after that transaction so
                ' ChangePackContent can retain the real parent animation owner.
                ResumeHidden()
            End If
        End Sub
        Private Sub ResumeHidden()
            If _owner Is Nothing OrElse Phase <> Phases.Hidden Then Return
            Dim owner = _owner
            _host.Dispatcher.BeginInvoke(New Action(Sub()
                                                       If _owner IsNot owner OrElse Not _host.IsVisible OrElse Not _host.IsLoaded OrElse Phase <> Phases.Hidden Then Return
                                                       If _enabled() Then
                                                           BeginWait()
                                                       Else
                                                           Phase = Phases.Loading : _show()
                                                       End If
                                                   End Sub))
        End Sub
    End Class
End Namespace
