Imports System.IO
Imports Newtonsoft.Json

' Both full and focused checks retain the completed assertions on failure.
' A failing run always returns a nonzero exit code; no automatic retry hides it.
Friend Module UiCheckRunner
    Friend Function Run(filePath As String, scope As String, verify As Action(Of String, List(Of String))) As Integer
        Dim fullPath = Path.GetFullPath(filePath)
        Dim reportDirectory = Path.GetDirectoryName(fullPath)
        Directory.CreateDirectory(reportDirectory)
        Dim passed As New List(Of String)
        Dim failure As Exception = Nothing
        Dim executableHash As String = Nothing
        Dim clientAreaAnimation = System.Windows.SystemParameters.ClientAreaAnimation
        Try
            Using source = File.OpenRead(GetType(UiCheckRunner).Assembly.Location), digest = Security.Cryptography.SHA256.Create()
                executableHash = BitConverter.ToString(digest.ComputeHash(source)).Replace("-", "")
            End Using
            verify(reportDirectory, passed)
        Catch ex As Exception
            failure = ex
        End Try
        File.WriteAllText(fullPath, JsonConvert.SerializeObject(New With {
            .Scope = scope, .Succeeded = failure Is Nothing, .Passed = passed, .Count = passed.Count,
            .ExecutableSHA256 = executableHash, .Standalone = RuntimeBootstrap.IsStandalone,
            .ClientAreaAnimation = clientAreaAnimation,
            .FailedCheck = If(failure Is Nothing, Nothing, failure.Message),
            .Exception = If(failure Is Nothing, Nothing, failure.ToString())}, Formatting.Indented))
        Return If(failure Is Nothing, 0, 1)
    End Function
End Module
