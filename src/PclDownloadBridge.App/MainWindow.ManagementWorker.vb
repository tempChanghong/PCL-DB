Imports System.Threading
Imports System.Threading.Tasks

Partial Public Class MainWindow
    ' PCL ModLoader catches expected cancellation inside its worker boundary.
    ' Do the same before Task.Run returns to framework code: VS Just My Code
    ' otherwise reports a user-unhandled exception even when the UI awaits it.
    Private Shared Function RunManagementWorkerAsync(Of T)(work As Func(Of T), token As CancellationToken) As Task(Of T)
        Return Task.Run(Of T)(Function() As Task(Of T)
                                 Try
                                     Return Task.FromResult(work())
                                 Catch ex As OperationCanceledException When token.IsCancellationRequested AndAlso ex.CancellationToken = token
                                     Return Task.FromCanceled(Of T)(token)
                                 Catch ex As Exception
                                     ' Preserve unexpected failures, including cancellation
                                     ' from a different owner, as faults for the UI to report.
                                     Return Task.FromException(Of T)(ex)
                                 End Try
                             End Function, token)
    End Function
End Class
