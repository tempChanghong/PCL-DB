Imports System.IO
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq

Friend Module UiCheckRunnerChecks
    Friend Sub Verify(artifactDirectory As String, check As Action(Of Boolean, String))
        Dim root = Path.Combine(artifactDirectory, "diagnostic-runner")
        Directory.CreateDirectory(root)
        Dim reportPath = Path.Combine(root, "dispatcher-failure.json")
        Dim exitCode = UiCheckRunner.Run(reportPath, "controlled-failure", Sub(unused, passed)
            passed.Add("before dispatcher failure")
            QueueFailure("first dispatcher failure")
            QueueFailure("later dispatcher failure")
            MainWindow.FlushControlLayout()
        End Sub)
        Dim report = JObject.Parse(File.ReadAllText(reportPath))
        check(exitCode = 1 AndAlso Not CBool(report("Succeeded")) AndAlso CStr(report("FailedCheck")) = "first dispatcher failure",
              "queued dispatcher failures produce a failed receipt without interactive UI")
        check(CInt(report("Count")) = 1 AndAlso CStr(report("Passed")(0)) = "before dispatcher failure" AndAlso CStr(report("Exception")).Contains("InvalidOperationException"),
              "dispatcher failure receipts retain completed checks and the first exception")
        exitCode = UiCheckRunner.Run(Path.Combine(root, "retry.json"), "controlled-success", Sub(unused, passed) passed.Add("fresh run"))
        report = JObject.Parse(File.ReadAllText(Path.Combine(root, "retry.json")))
        check(exitCode = 0 AndAlso CBool(report("Succeeded")) AndAlso report("Exception").Type = JTokenType.Null,
              "a fresh check run does not inherit a previous dispatcher failure")
        exitCode = UiCheckRunner.Run(Path.Combine(root, "outer.json"), "controlled-outer", Sub(unused, passed)
            UiCheckRunner.Run(Path.Combine(root, "inner.json"), "controlled-inner", Sub(innerDirectory, innerPassed)
                QueueFailure("inner failure") : MainWindow.FlushControlLayout()
            End Sub)
            QueueFailure("outer failure") : MainWindow.FlushControlLayout()
        End Sub)
        report = JObject.Parse(File.ReadAllText(Path.Combine(root, "outer.json")))
        check(exitCode = 1 AndAlso CStr(report("FailedCheck")) = "outer failure", "nested checks restore the outer dispatcher failure owner")
        report = JObject.Parse(File.ReadAllText(Path.Combine(root, "inner.json")))
        check(Not CBool(report("Succeeded")) AndAlso CStr(report("FailedCheck")) = "inner failure", "nested dispatcher failures stay in their own diagnostic receipt")
        exitCode = UiCheckRunner.Run(Path.Combine(root, "synchronous.json"), "controlled-sync", Sub(unused, passed)
            Throw New InvalidOperationException("synchronous failure")
        End Sub)
        report = JObject.Parse(File.ReadAllText(Path.Combine(root, "synchronous.json")))
        check(exitCode = 1 AndAlso CStr(report("FailedCheck")) = "synchronous failure", "synchronous verification failures keep the same nonzero receipt contract")
        Dim blocker = Path.Combine(root, "blocked-parent"), reached = False, originalError = Console.Error
        File.WriteAllText(blocker, "preserved")
        Using errorOutput As New StringWriter()
            Try
                Console.SetError(errorOutput)
                exitCode = UiCheckRunner.Run(Path.Combine(blocker, "report.json"), "controlled-path", Sub(unused, passed) reached = True)
                check(exitCode = 1 AndAlso Not reached AndAlso File.ReadAllText(blocker) = "preserved" AndAlso errorOutput.ToString().Contains("IOException"),
                      "unusable report directories fail before checks and preserve the conflicting file")
                errorOutput.GetStringBuilder().Clear()
                exitCode = UiCheckRunner.Run(root, "controlled-write", Sub(unused, passed) passed.Add("completed"))
                check(exitCode = 1 AndAlso Directory.Exists(root) AndAlso errorOutput.ToString().Contains("UnauthorizedAccessException"),
                      "unwritable report outputs fail through stderr instead of interactive error UI")
            Finally
                Console.SetError(originalError)
            End Try
        End Using
    End Sub

    Private Sub QueueFailure(message As String)
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Normal, New Action(Sub()
            Throw New InvalidOperationException(message)
        End Sub))
    End Sub
End Module
