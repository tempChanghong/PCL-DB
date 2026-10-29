Imports System.IO
Imports System.Text
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module LauncherDirectoryConfigurationChecks
    Friend ReadOnly CaseNames As String() = {
        "hmcl-selection-object", "hmcl-selection-array", "hmcl-selection-duplicate",
        "ce-shared-version-object", "ce-shared-version-array", "ce-shared-folders-object", "ce-shared-folders-array",
        "ce-shared-duplicate-folders", "ce-shared-duplicate-version",
        "hmcl-shared-duplicate-path", "hmcl-shared-duplicate-list",
        "ce-primary-duplicate", "hmcl-primary-duplicate", "hmcl-legacy-duplicate"}

    Friend Function RunAsync(testRoot As String, Optional group As String = "launcher-config-all") As Task(Of List(Of String))
        Dim passed As New List(Of String), reader As New LauncherDirectoryReader()
        For Each caseName In CaseNames
            If group <> "launcher-config-all" AndAlso group <> "launcher-config-" & caseName Then Continue For
            Dim root = Path.Combine(testRoot, "launcher-config-" & Guid.NewGuid().ToString("N"))
            Dim game = Path.Combine(root, "本地游戏"), other = Path.Combine(root, "other")
            Directory.CreateDirectory(game) : Directory.CreateDirectory(other)
            Dim hmcl = caseName.StartsWith("hmcl", StringComparison.Ordinal)
            Dim legacy = caseName = "hmcl-legacy-duplicate"
            Dim primary = Path.Combine(root, If(hmcl, If(legacy, "hmcl.json", ".hmcl/config/game-directories.json"), "PCL/config.v1.json"))
            Dim selection = Path.Combine(root, ".hmcl/config/launcher-settings.json"), sharedFile = Path.Combine(root, "shared.json")
            Dim q = Quote(game), alternate = Quote(other), healthyPrimary As String
            If hmcl Then
                healthyPrimary = If(legacy, "{""last"":""local"",""configurations"":{""local"":{""gameDir"":" & q & "}}}",
                    "{""directories"":[{""id"":""local"",""path"":" & q & "}]}")
                Put(selection, "{""selectedGameDirectory"":""local""}")
            Else
                healthyPrimary = "{""LocalFileVersion"":1,""LaunchFolderSelect"":" & q & "}"
            End If
            Put(primary, healthyPrimary)
            Dim healthyShared = If(hmcl, "{""directories"":[{""id"":""remote"",""path"":" & alternate & "}]}",
                New JObject From {{"FileVersion", 1}, {"LaunchFolders", "Remote>" & other}}.ToString())
            Dim damaged As String = Nothing, recovery As String = Nothing
            Select Case caseName
                Case "hmcl-selection-object", "hmcl-selection-array"
                    damaged = selection : recovery = "{""selectedGameDirectory"":""local""}"
                    Put(damaged, "{""selectedGameDirectory"":" & If(caseName.EndsWith("object", StringComparison.Ordinal), "{}", "[]") & "}")
                Case "hmcl-selection-duplicate"
                    damaged = selection : recovery = "{""selectedGameDirectory"":""local""}"
                    Put(damaged, "{""selectedGameDirectory"":""local"",""selectedGameDirectory"":""other""}")
                Case "ce-shared-version-object", "ce-shared-version-array"
                    damaged = sharedFile : recovery = healthyShared
                    Put(damaged, "{""FileVersion"":" & If(caseName.EndsWith("object", StringComparison.Ordinal), "{}", "[]") & ",""LaunchFolders"":""""}")
                Case "ce-shared-folders-object", "ce-shared-folders-array"
                    damaged = sharedFile : recovery = healthyShared
                    Put(damaged, "{""FileVersion"":1,""LaunchFolders"":" & If(caseName.EndsWith("object", StringComparison.Ordinal), "{}", "[]") & "}")
                Case "ce-shared-duplicate-folders"
                    damaged = sharedFile : recovery = healthyShared
                    Put(damaged, "{""FileVersion"":1,""LaunchFolders"":"""",""LaunchFolders"":" & Quote("Remote>" & other) & "}")
                Case "ce-shared-duplicate-version"
                    damaged = sharedFile : recovery = healthyShared
                    Put(damaged, "{""FileVersion"":2,""FileVersion"":1,""LaunchFolders"":" & Quote("Remote>" & other) & "}")
                Case "hmcl-shared-duplicate-path"
                    damaged = sharedFile : recovery = healthyShared
                    Put(damaged, "{""directories"":[{""id"":""remote"",""path"":" & q & ",""path"":" & alternate & "}]}")
                Case "hmcl-shared-duplicate-list"
                    damaged = sharedFile : recovery = healthyShared
                    Put(damaged, "{""directories"":[],""directories"":[{""id"":""remote"",""path"":" & alternate & "}]}")
                Case "ce-primary-duplicate"
                    damaged = primary : recovery = healthyPrimary
                    Put(damaged, "{""LocalFileVersion"":1,""LaunchFolderSelect"":" & q & ",""LaunchFolderSelect"":" & alternate & "}")
                Case "hmcl-primary-duplicate"
                    damaged = primary : recovery = healthyPrimary
                    Put(damaged, "{""directories"":[{""id"":""local"",""path"":" & q & ",""path"":" & alternate & "}]}")
                Case "hmcl-legacy-duplicate"
                    damaged = primary : recovery = healthyPrimary
                    Put(damaged, "{""last"":""local"",""configurations"":{""local"":{""gameDir"":" & q & ",""gameDir"":" & alternate & "}}}")
            End Select
            Dim binding As New ExternalLauncherBinding With {.Id = caseName, .Kind = If(hmcl, ExternalLauncherKind.Hmcl, ExternalLauncherKind.PclCe),
                .Workspace = root, .ConfigPath = primary, .SharedConfigPath = If(caseName.Contains("shared"), sharedFile, Nothing)}
            Dim before = Snapshot(root), result = reader.Read(binding, False)
            Dim fatal = caseName.Contains("primary") OrElse legacy
            Check(result.Succeeded = Not fatal AndAlso result.Warnings.Count > 0 AndAlso
                (fatal OrElse Not result.Warnings.Any(Function(w) w.StartsWith("读取启动器目录失败", StringComparison.Ordinal))),
                "foreign configuration failure stays in its required or optional boundary: " & caseName, passed)
            Check(If(fatal, result.Folders.Count = 0, result.Folders.Count = 1 AndAlso result.Folders(0).Location = game AndAlso
                result.Folders(0).IsSelected = Not caseName.Contains("selection")),
                "bad foreign configuration cannot hide a healthy local root or leak a last-value destination: " & caseName, passed)
            Check(Matches(root, before), "failed foreign configuration reads preserve all external bytes and create no sidecars: " & caseName, passed)
            Put(damaged, recovery)
            before = Snapshot(root) : result = reader.Read(binding, False)
            Check(result.Succeeded AndAlso result.Warnings.Count = 0 AndAlso result.Folders.Count > 0 AndAlso
                result.Folders.Single(Function(f) f.IsSelected).Location = game AndAlso Matches(root, before),
                "repaired foreign configuration retries in the same reader without stale warnings or configuration writes: " & caseName, passed)
            Dim target As New LauncherInstallTarget(result.Folders.Single(Function(f) f.IsSelected), Nothing)
            binding.Workspace = other : binding.ConfigPath = Path.Combine(other, "future.json")
            Check(target.Root = game AndAlso target.Workspace = root AndAlso target.ConfigPath = primary AndAlso target.BindingId = caseName AndAlso
                target.Kind = If(hmcl, ExternalLauncherKind.Hmcl, ExternalLauncherKind.PclCe),
                "recovered directory retains its frozen launcher and install target when the editor changes: " & caseName, passed)
        Next
        If passed.Count = 0 Then Throw New ArgumentException("Unknown launcher configuration group: " & group)
        Return Task.FromResult(passed)
    End Function

    Private Function Quote(value As String) As String
        Return New JValue(value).ToString(Formatting.None)
    End Function
    Private Sub Put(path As String, content As String)
        Directory.CreateDirectory(IO.Path.GetDirectoryName(path))
        File.WriteAllText(path, content, New UTF8Encoding(False))
    End Sub
    Private Function Snapshot(root As String) As Dictionary(Of String, Byte())
        Return Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(Function(p) p, Function(p) File.ReadAllBytes(p), StringComparer.OrdinalIgnoreCase)
    End Function
    Private Function Matches(root As String, before As Dictionary(Of String, Byte())) As Boolean
        Dim after = Snapshot(root)
        Return before.Count = after.Count AndAlso before.All(Function(p) after.ContainsKey(p.Key) AndAlso after(p.Key).SequenceEqual(p.Value))
    End Function
    Private Sub Check(condition As Boolean, label As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub
End Module
