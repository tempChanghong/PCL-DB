Imports System.Runtime.ExceptionServices
Imports System.Windows

Partial Public Class MainWindow
    ' A check fixture is finished when its action returns. Retaining those
    ' hidden Windows keeps their dynamic resources in later theme/layout work.
    ' The real application window and every pre-existing window are retained.
    Friend Shared Sub VerifyPreviewFixture(action As Action)
        Dim retained = New HashSet(Of Window)(Application.Current.Windows.Cast(Of Window)())
        Dim errors As New List(Of Exception), closed = 0
        Try
            action()
        Catch ex As Exception
            errors.Add(ex)
        Finally
            For Each window In Application.Current.Windows.OfType(Of MainWindow)().
                Where(Function(item) item._preview AndAlso Not retained.Contains(item)).ToArray()
                Try
                    window.Close()
                    If Application.Current.Windows.Cast(Of Window)().Contains(window) Then
                        Throw New InvalidOperationException("A completed preview fixture retained an active window.")
                    End If
                    closed += 1
                Catch ex As Exception
                    errors.Add(ex)
                End Try
            Next
            If closed > 0 Then Console.WriteLine("CHECK_WINDOWS retained={0} closed={1} remaining={2}", retained.Count, closed, Application.Current.Windows.Count)
        End Try
        If errors.Count = 1 Then ExceptionDispatchInfo.Capture(errors(0)).Throw()
        If errors.Count > 1 Then Throw New AggregateException("Preview fixture and window cleanup failed.", errors)
    End Sub
End Class
