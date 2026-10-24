Imports System.Diagnostics
Imports System.Windows

Partial Public Class MainWindow
    Private _packProjectAction As Action(Of String, String)
    Private _packProjectActionWait As Func(Of Task) = Function() Task.Delay(200)
    Private _packProjectActionGeneration As Long

    Private Sub RetirePackProjectAction()
        _packProjectActionGeneration += 1
        PackProjectActionStatus.Text = ""
        PackProjectActionStatus.Visibility = Visibility.Collapsed
    End Sub

    Private Function OwnsPackProjectAction(owner As Core.ModpackProject, generation As Long) As Boolean
        Return Not _closing AndAlso Not _choosingOnline AndAlso generation = _packProjectActionGeneration AndAlso _packProject Is owner AndAlso
            _downloadView = DownloadView.Modpacks AndAlso _packMode = PackBrowserMode.Detail AndAlso PackProjectIntro.IsVisible AndAlso
            PackProjectIntro.IsEnabled AndAlso PackProjectIntro.IsHitTestVisible
    End Function

    Private Sub PerformPackProjectAction(kind As String, value As String)
        If _packProjectAction IsNot Nothing Then
            _packProjectAction(kind, value)
        ElseIf kind = "copy" Then
            ' Do not clear first. Use the existing Forms dependency to disable
            ' blocking inner retries; the four attempts yield between STA calls.
            System.Windows.Forms.Clipboard.SetDataObject(New System.Windows.Forms.DataObject(DataFormats.UnicodeText, value), True, 0, 0)
        Else
            Process.Start(New ProcessStartInfo(value) With {.UseShellExecute = True})
        End If
    End Sub

    ' PCL ModBase.ClipboardSet: four attempts, 200 ms apart, no escaping error.
    ' Delay asynchronously on the dispatcher instead of sleeping the WPF thread.
    Private Async Function CopyPackProjectValueAsync(value As String, owner As Core.ModpackProject, generation As Long) As Task(Of Exception)
        For attempt = 1 To 4
            If Not OwnsPackProjectAction(owner, generation) Then Return Nothing
            Try
                PerformPackProjectAction("copy", value)
                Return Nothing
            Catch ex As Exception
                If attempt = 4 Then Return ex
            End Try
            Await _packProjectActionWait()
        Next
        Return Nothing
    End Function

    Private Async Function DispatchPackProjectActionAsync(kind As String) As Task
        Dim owner = _packProject, project = ActivePackProject()
        If project Is Nothing OrElse (kind <> "copy" AndAlso kind <> "link") OrElse
            (_preview AndAlso _packProjectAction Is Nothing) OrElse Not OwnsPackProjectAction(owner, _packProjectActionGeneration) Then Return
        RetirePackProjectAction()
        Dim generation = _packProjectActionGeneration
        Dim value = If(kind = "copy", project.Name, project.Website), message As String
        Try
            If kind = "copy" Then
                Dim failure = Await CopyPackProjectValueAsync(value, owner, generation)
                message = If(failure Is Nothing, "已成功复制！", "名称复制失败，请稍后重试。")
                If failure IsNot Nothing AndAlso Not _preview Then Log(failure)
            Else
                Dim browserFailure As Exception = Nothing
                Try
                    PerformPackProjectAction("link", value)
                Catch ex As Exception
                    browserFailure = ex
                End Try
                If browserFailure Is Nothing Then
                    message = "已在默认浏览器中打开。"
                Else
                    If Not _preview Then Log(browserFailure)
                    Dim failure = Await CopyPackProjectValueAsync(value, owner, generation)
                    message = If(failure Is Nothing, "无法打开浏览器，项目网址已复制，可手动粘贴访问。", "无法打开浏览器，网址复制也失败。请手动访问：" & value)
                    If failure IsNot Nothing AndAlso Not _preview Then Log(failure)
                End If
            End If
        Catch ex As Exception
            ' Keep exceptional dispatcher/delay failures inside the click action.
            If Not _preview Then Log(ex)
            message = "操作未完成，请稍后重试。"
        End Try
        If Not OwnsPackProjectAction(owner, generation) Then Return
        PackProjectActionStatus.Text = message
        PackProjectActionStatus.Visibility = Visibility.Visible
    End Function
End Class
