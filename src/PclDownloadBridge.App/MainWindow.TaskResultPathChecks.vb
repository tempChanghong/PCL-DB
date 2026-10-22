Imports System.Diagnostics
Imports System.IO
Imports System.Windows
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq

Partial Public Class MainWindow
    Private Function VerifyTaskResultPaths(artifactDirectory As String) As KeyValuePair(Of Boolean, Boolean)
        Dim originalRoot = _taskRoot, originalInstance = _taskInstancePath, originalFinished = _taskFinished, originalOutcome = _taskOutcome
        Dim originalAction = _taskAction, originalStatus = TaskActionStatus.Text
        Dim originalTitle = TaskTitle.Text, originalProgress = TaskProgress.Value
        Dim fixture = Path.Combine(Path.GetTempPath(), "PclDownloadBridge-ResultPaths-" & Guid.NewGuid().ToString("N"))
        Dim records As New List(Of Object), foldersPassed = True, logsPassed = True
        Dim run As Action(Of String, String, String, Boolean, Boolean, Boolean, TaskOutcome) =
            Sub(label, root, instance, folderExpected, logExpected, finished, outcome)
                _taskRoot = root : _taskInstancePath = instance : _taskFinished = finished : _taskOutcome = outcome
                Dim actions As New List(Of KeyValuePair(Of String, String))
                _taskAction = Sub(kind, value) actions.Add(New KeyValuePair(Of String, String)(kind, value))
                Dim refreshError As String = Nothing, folderError As String = Nothing, logError As String = Nothing
                Dim folderEnabled As Boolean, logEnabled As Boolean
                Try
                    RefreshTaskResultActions()
                    folderEnabled = OpenTaskFolderButton.IsEnabled : logEnabled = OpenTaskLogButton.IsEnabled
                Catch ex As Exception
                    refreshError = ex.GetType().Name & ": " & ex.Message
                End Try
                Try
                    OpenTaskFolder(Nothing, New RoutedEventArgs())
                Catch ex As Exception
                    folderError = ex.GetType().Name & ": " & ex.Message
                End Try
                Try
                    OpenInstallerLog(Nothing, New RoutedEventArgs())
                Catch ex As Exception
                    logError = ex.GetType().Name & ": " & ex.Message
                End Try
                CopyTaskInformation(Nothing, New RoutedEventArgs())
                Dim folderActions = actions.Where(Function(action) action.Key = "folder").ToList()
                Dim logActions = actions.Where(Function(action) action.Key = "log").ToList()
                Dim copyActions = actions.Where(Function(action) action.Key = "copy").ToList()
                Dim snapshotKept = _taskRoot = root AndAlso _taskInstancePath = instance AndAlso _taskOutcome = outcome AndAlso TaskTitle.Text = originalTitle AndAlso TaskProgress.Value = originalProgress
                Dim folderOk = refreshError Is Nothing AndAlso folderError Is Nothing AndAlso folderEnabled = folderExpected AndAlso folderActions.Count = If(folderExpected, 1, 0) AndAlso snapshotKept
                If folderExpected Then folderOk = folderOk AndAlso folderActions.Single().Value = instance
                Dim logOk = refreshError Is Nothing AndAlso logError Is Nothing AndAlso logEnabled = logExpected AndAlso logActions.Count = If(logExpected, 1, 0) AndAlso snapshotKept
                If logExpected Then logOk = logOk AndAlso logActions.Single().Value = Path.Combine(root, ".vanilla-downloader", "installer.log")
                Dim copied = copyActions.Count = If(finished, 1, 0)
                If finished Then copied = copied AndAlso copyActions.Single().Value.Contains(root) AndAlso copyActions.Single().Value.Contains(instance)
                foldersPassed = foldersPassed AndAlso folderOk AndAlso copied
                logsPassed = logsPassed AndAlso logOk AndAlso copied
                records.Add(New With {.Case = label, .FolderPassed = folderOk, .LogPassed = logOk, .CopyPassed = copied,
                    .RefreshError = refreshError, .FolderError = folderError, .LogError = logError,
                    .FolderEnabled = folderEnabled, .LogEnabled = logEnabled, .Actions = actions.Select(Function(action) New With {.Kind = action.Key, .Value = action.Value})})
            End Sub
        Try
            Dim normal = Path.Combine(fixture, "normal"), normalInstance = Path.Combine(normal, "versions", _taskName)
            Directory.CreateDirectory(normalInstance) : Directory.CreateDirectory(Path.Combine(normal, ".vanilla-downloader"))
            File.WriteAllText(Path.Combine(normal, ".vanilla-downloader", "installer.log"), "normal task log")
            run("normal", normal, normalInstance, True, True, True, TaskOutcome.Succeeded)

            Dim logRoot = Path.Combine(fixture, "log-junction"), logInstance = Path.Combine(logRoot, "versions", _taskName)
            Dim logTarget = Path.Combine(fixture, "log-target"), logLink = Path.Combine(logRoot, ".vanilla-downloader")
            Directory.CreateDirectory(logInstance) : Directory.CreateDirectory(logTarget)
            File.WriteAllText(Path.Combine(logTarget, "installer.log"), "keep linked log")
            CreateTaskResultJunction(logLink, logTarget)
            run("log-directory-junction", logRoot, logInstance, True, False, True, TaskOutcome.Succeeded)

            Dim folderRoot = Path.Combine(fixture, "instance-junction"), folderLink = Path.Combine(folderRoot, "versions", _taskName)
            Dim folderTarget = Path.Combine(fixture, "instance-target")
            Directory.CreateDirectory(Path.GetDirectoryName(folderLink)) : Directory.CreateDirectory(folderTarget)
            Directory.CreateDirectory(Path.Combine(folderRoot, ".vanilla-downloader"))
            File.WriteAllText(Path.Combine(folderRoot, ".vanilla-downloader", "installer.log"), "normal log beside a replaced instance")
            File.WriteAllText(Path.Combine(folderTarget, "keep.txt"), "keep linked instance")
            CreateTaskResultJunction(folderLink, folderTarget)
            run("instance-directory-junction", folderRoot, folderLink, False, True, True, TaskOutcome.Succeeded)

            Dim rootLink = Path.Combine(fixture, "game-root-junction")
            CreateTaskResultJunction(rootLink, normal)
            run("game-root-junction", rootLink, Path.Combine(rootLink, "versions", _taskName), False, False, True, TaskOutcome.Succeeded)
            Dim invalidRoot = normal & ChrW(0)
            run("invalid-snapshot-path", invalidRoot, invalidRoot & "\versions\" & _taskName, False, False, True, TaskOutcome.Succeeded)
            Dim missingRoot = Path.Combine(fixture, "missing")
            run("deleted-paths", missingRoot, Path.Combine(missingRoot, "versions", _taskName), False, False, True, TaskOutcome.Succeeded)
            run("instance-from-another-root", normal, logInstance, False, True, True, TaskOutcome.Succeeded)
            run("unfinished-task", normal, normalInstance, False, False, False, TaskOutcome.Succeeded)
            run("failed-task", normal, normalInstance, False, True, True, TaskOutcome.Failed)
            run("cancelled-task", normal, normalInstance, False, True, True, TaskOutcome.Cancelled)

            ' Remove this owned junction only, never its target or any subtree.
            Dim fixturePrefix = Path.GetFullPath(fixture) & Path.DirectorySeparatorChar
            If Not Path.GetFullPath(logLink).StartsWith(fixturePrefix, StringComparison.OrdinalIgnoreCase) OrElse
                (File.GetAttributes(logLink) And FileAttributes.ReparsePoint) = 0 Then Throw New InvalidOperationException("Unexpected fixture junction path.")
            Directory.Delete(logLink)
            Directory.CreateDirectory(logLink) : File.WriteAllText(Path.Combine(logLink, "installer.log"), "restored normal task log")
            run("restored-log-directory", logRoot, logInstance, True, True, True, TaskOutcome.Succeeded)
            Dim targetsKept = File.ReadAllText(Path.Combine(logTarget, "installer.log")) = "keep linked log" AndAlso File.ReadAllText(Path.Combine(folderTarget, "keep.txt")) = "keep linked instance" AndAlso File.ReadAllText(Path.Combine(normal, ".vanilla-downloader", "installer.log")) = "normal task log"
            foldersPassed = foldersPassed AndAlso targetsKept : logsPassed = logsPassed AndAlso targetsKept
            File.WriteAllText(Path.Combine(artifactDirectory, "task-result-paths.json"), JsonConvert.SerializeObject(New With {
                .FixtureRoot = fixture, .FoldersPassed = foldersPassed, .LogsPassed = logsPassed, .TargetsPreserved = targetsKept, .Cases = records}, Formatting.Indented))
        Finally
            _taskRoot = originalRoot : _taskInstancePath = originalInstance : _taskFinished = originalFinished : _taskOutcome = originalOutcome
            _taskAction = originalAction
            RefreshTaskResultActions() : TaskActionStatus.Text = originalStatus
        End Try
        Return New KeyValuePair(Of Boolean, Boolean)(foldersPassed, logsPassed)
    End Function

    Private Shared Sub CreateTaskResultJunction(link As String, target As String)
        Dim options As New ProcessStartInfo("cmd.exe", "/c mklink /J """ & link & """ """ & target & """") With {
            .UseShellExecute = False, .CreateNoWindow = True, .WindowStyle = ProcessWindowStyle.Hidden,
            .RedirectStandardOutput = True, .RedirectStandardError = True}
        Using child = Process.Start(options)
            If Not child.WaitForExit(10000) Then Throw New TimeoutException("Fixture junction creation timed out.")
            Dim output = child.StandardOutput.ReadToEnd() & child.StandardError.ReadToEnd()
            If child.ExitCode <> 0 OrElse Not Directory.Exists(link) OrElse (File.GetAttributes(link) And FileAttributes.ReparsePoint) = 0 Then Throw New IOException("Cannot create isolated fixture junction: " & output)
        End Using
    End Sub
End Class
