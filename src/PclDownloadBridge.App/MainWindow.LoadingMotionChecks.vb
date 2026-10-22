Imports System.ComponentModel
Imports System.Reflection
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private Sub VerifyLoadingMotionUi(directory As String, check As Action(Of Boolean, String))
        Dim loader As New PclLoadingView("正在获取整合包列表")
        Dim graphic = DirectCast(loader.Children(0), Grid)
        Dim pick = DirectCast(DirectCast(graphic.Children(0), Shapes.Path).RenderTransform, RotateTransform)
        Dim left = DirectCast(graphic.Children(1), Shapes.Path), right = DirectCast(graphic.Children(2), Shapes.Path)
        Dim host As New Border With {.Width = 300, .Height = 150}
        host.SetResourceReference(Border.BackgroundProperty, "CardSurfaceBrush")
        Dim layout = ControlLayout(host)
        Using preference As New ClientAnimationPreferenceFixture(), surface = PreviewSurface.Attach(host, 300, 150)
            layout()
            preference.SetEnabled(True)
            check(loader.Transition Is Nothing, "an unattached loader has no repeating animation")
            Dim watchers = preference.SubscriberCount
            host.Child = loader : layout()
            Dim first = loader.Transition
            check(first IsNot Nothing, "a loaded visible loader starts with client animations enabled")
            first.Controller.Pause()
            If Not PumpMotionUntil(Function() first.IsPaused) Then Throw New TimeoutException("The loader clock did not pause.")
            first.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(600), TimeSeekOrigin.BeginTime)
            check(Math.Abs(pick.Angle + 20) < 0.0001 AndAlso left.Opacity = 1 AndAlso right.Opacity = 1, "the real loader clock reaches the PCL impact angle and debris at 600 ms")
            For Each dark In {False, True}
                RestoreAppearance(New JObject From {{"theme", "blue"}, {"darkMode", dark}}) : layout()
                SaveControlVisual(host, IO.Path.Combine(directory, "loader-" & If(dark, "dark", "blue") & ".png"))
                Dim ink = DirectCast(Application.Current.Resources("ColorBrush3"), SolidColorBrush).Color
                check(DirectCast(DirectCast(graphic.Children(0), Shapes.Path).Stroke, SolidColorBrush).Color = ink AndAlso
                      DirectCast(left.Fill, SolidColorBrush).Color = ink AndAlso DirectCast(right.Fill, SolidColorBrush).Color = ink AndAlso
                      DirectCast(DirectCast(graphic.Children(3), Shapes.Rectangle).Fill, SolidColorBrush).Color = ink AndAlso
                      DirectCast(DirectCast(loader.Children(1), TextBlock).Foreground, SolidColorBrush).Color = ink,
                      "loader pickaxe, debris, ground and label follow PCL accent ink in dark mode " & dark)
                check(loader.Transition Is first, "changing the loader palette retains its current cycle in dark mode " & dark)
            Next
            loader.Opacity = 0.6 : loader.RenderTransform = New TranslateTransform(0, -8)
            preference.SetEnabled(False)
            check(loader.Transition Is Nothing, "turning off client animations immediately retires a visible loader clock")
            CheckLoadingRest(loader, check, "reduced animation")
            check(loader.Opacity = 0.6 AndAlso DirectCast(loader.RenderTransform, TranslateTransform).Y = -8, "loader cleanup leaves page-owned opacity and transform intact")
            preference.SetEnabled(True)
            Dim current = loader.Transition
            check(current IsNot Nothing AndAlso current IsNot first, "turning animations back on starts one fresh loader clock")
            preference.SetEnabled(True)
            check(loader.Transition Is current, "a repeated preference notification does not restart the loading cycle")
            loader.IsHitTestVisible = False : loader.IsEnabled = False : layout()
            check(loader.Transition Is current, "a decorative loader keeps moving through parent input gates")
            first.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(700), TimeSeekOrigin.BeginTime)
            check(loader.Transition Is current, "a retired loader clock cannot replace the active cycle")
            loader.Visibility = Visibility.Collapsed : layout()
            check(loader.Transition Is Nothing, "a hidden loader releases its repeating clock")
            CheckLoadingRest(loader, check, "hidden")
            preference.SetEnabled(False) : preference.SetEnabled(True)
            check(loader.Transition Is Nothing, "preference changes cannot animate a hidden loader")
            loader.Visibility = Visibility.Visible : layout()
            check(loader.Transition IsNot Nothing, "a decorative loader restarts when visible even behind an input gate")
            loader.IsEnabled = True : loader.IsHitTestVisible = True
            loader.Opacity = 1 : loader.RenderTransform = Transform.Identity
            SaveControlVisual(host, IO.Path.Combine(directory, "loader-lifecycle.png"))
            check(preference.SubscriberCount = watchers + 1, "a loaded loader owns exactly one system preference subscription")
            host.Child = Nothing : layout()
            check(loader.Transition Is Nothing AndAlso preference.SubscriberCount = watchers, "unloading releases the loop and its static preference subscription")
            preference.SetEnabled(False) : preference.SetEnabled(True)
            check(loader.Transition Is Nothing, "preference notifications cannot resurrect an unloaded loader")
            For iteration = 1 To 5
                host.Child = loader : layout()
                check(loader.Transition IsNot Nothing AndAlso preference.SubscriberCount = watchers + 1, "reattached loader owns one loop and one preference subscription " & iteration)
                host.Child = Nothing : layout()
                check(loader.Transition Is Nothing AndAlso preference.SubscriberCount = watchers, "loader removal leaves no loop or preference subscription " & iteration)
            Next
        End Using
    End Sub

    Private Shared Sub CheckLoadingRest(loader As PclLoadingView, check As Action(Of Boolean, String), state As String)
        Dim graphic = DirectCast(loader.Children(0), Grid)
        Dim pick = DirectCast(DirectCast(graphic.Children(0), Shapes.Path).RenderTransform, RotateTransform)
        check(Not pick.HasAnimatedProperties AndAlso pick.Angle = 55, "loader " & state & " restores the static PCL pickaxe angle")
        For index = 1 To 2
            Dim piece = DirectCast(graphic.Children(index), Shapes.Path)
            Dim offset = DirectCast(DirectCast(piece.RenderTransform, TransformGroup).Children(1), TranslateTransform)
            check(Not piece.HasAnimatedProperties AndAlso piece.Opacity = 0 AndAlso Not offset.HasAnimatedProperties AndAlso offset.X = 0 AndAlso offset.Y = 0,
                  "loader " & state & " releases debris opacity and translation clocks " & index)
        Next
    End Sub

    ' This changes only the isolated net48 check process's cached WPF value, then
    ' invokes the actual notification path. It never writes Windows settings.
    Private NotInheritable Class ClientAnimationPreferenceFixture
        Implements IDisposable
        Private ReadOnly _original As Boolean = SystemParameters.ClientAreaAnimation
        Private ReadOnly _value As FieldInfo = GetType(SystemParameters).GetField("_clientAreaAnimation", BindingFlags.Static Or BindingFlags.NonPublic)
        Private ReadOnly _changed As FieldInfo = GetType(SystemParameters).GetField("StaticPropertyChanged", BindingFlags.Static Or BindingFlags.NonPublic)
        Public Sub New()
            If _value Is Nothing OrElse _changed Is Nothing Then Throw New InvalidOperationException("The net48 WPF client animation fixture is unavailable.")
        End Sub
        Public Sub SetEnabled(value As Boolean)
            _value.SetValue(Nothing, value)
            If SystemParameters.ClientAreaAnimation <> value Then Throw New InvalidOperationException("The net48 animation preference cache was not initialized.")
            DirectCast(_changed.GetValue(Nothing), PropertyChangedEventHandler)?.Invoke(Nothing, New PropertyChangedEventArgs(NameOf(SystemParameters.ClientAreaAnimation)))
        End Sub
        Public ReadOnly Property SubscriberCount As Integer
            Get
                Return If(DirectCast(_changed.GetValue(Nothing), PropertyChangedEventHandler)?.GetInvocationList().Length, 0)
            End Get
        End Property
        Public Sub Dispose() Implements IDisposable.Dispose
            SetEnabled(_original)
        End Sub
    End Class
End Class
