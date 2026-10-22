Imports System.IO
Imports Newtonsoft.Json

' Both full and focused checks retain the completed assertions on failure.
' A failing run always returns a nonzero exit code; no automatic retry hides it.
Friend Module UiCheckRunner
    <ThreadStatic>
    Private _captureDispatcherFailure As Action(Of Exception)

    Friend Function TryCaptureDispatcherFailure(failure As Exception) As Boolean
        If _captureDispatcherFailure Is Nothing Then Return False
        _captureDispatcherFailure(failure)
        Return True
    End Function

    Friend Function Run(filePath As String, scope As String, verify As Action(Of String, List(Of String))) As Integer
        Dim fullPath As String = Nothing
        Dim passed As New List(Of String)
        Dim failure As Exception = Nothing
        Dim executableHash As String = Nothing
        Dim clientAreaAnimation = System.Windows.SystemParameters.ClientAreaAnimation
        Dim previousCapture = _captureDispatcherFailure
        Try
            ' Dispatcher callbacks run outside verify's synchronous call stack.
            ' Keep the first failure and restore the owner after nested fixtures.
            _captureDispatcherFailure = Sub(ex)
                                            If failure Is Nothing Then failure = ex
                                        End Sub
            fullPath = Path.GetFullPath(filePath)
            Dim reportDirectory = Path.GetDirectoryName(fullPath)
            Directory.CreateDirectory(reportDirectory)
            Using source = File.OpenRead(GetType(UiCheckRunner).Assembly.Location), digest = Security.Cryptography.SHA256.Create()
                executableHash = BitConverter.ToString(digest.ComputeHash(source)).Replace("-", "")
            End Using
            verify(reportDirectory, passed)
        Catch ex As Exception
            If failure Is Nothing Then failure = ex
        Finally
            _captureDispatcherFailure = previousCapture
        End Try
        Try
            File.WriteAllText(fullPath, JsonConvert.SerializeObject(New With {
                .Scope = scope, .Succeeded = failure Is Nothing, .Passed = passed, .Count = passed.Count,
                .ExecutableSHA256 = executableHash, .Standalone = RuntimeBootstrap.IsStandalone,
                .ClientAreaAnimation = clientAreaAnimation,
                .FailedCheck = If(failure Is Nothing, Nothing, failure.Message),
                .Exception = If(failure Is Nothing, Nothing, failure.ToString())}, Formatting.Indented))
        Catch reportFailure As Exception
            If failure IsNot Nothing Then Console.Error.WriteLine(failure.ToString())
            Console.Error.WriteLine(reportFailure.ToString())
            Return 1
        End Try
        Return If(failure Is Nothing, 0, 1)
    End Function
End Module
