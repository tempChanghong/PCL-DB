Imports System.IO
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Sub VerifyInstallTargetSaveConflicts(check As Action(Of Boolean, String), root As String)
        Dim bindings = [Enum].GetValues(GetType(ExternalLauncherKind)).Cast(Of ExternalLauncherKind)().
            Select(Function(kind) New ExternalLauncherBinding With {.Id = "target-" & kind.ToString(), .Kind = kind, .Workspace = root}).ToArray()
        Dim game = Path.Combine(root, "target-one"), otherGame = Path.Combine(root, "target-two")
        Dim seed As New LauncherBindingsState()
        seed.Bindings.AddRange(bindings)
        Dim selectTarget As Action(Of MainWindow, ExternalLauncherBinding, String) =
            Sub(owner, binding, location)
                owner._selectedFolder = New GameFolder With {.Location = location, .Name = "fixture",
                    .LauncherBindingId = binding?.Id, .LauncherKind = If(binding Is Nothing, Nothing, New ExternalLauncherKind?(binding.Kind)),
                    .LauncherWorkspace = binding?.Workspace}
            End Sub
        Dim makeOwner As Func(Of MainWindow) =
            Function()
                Dim owner As New MainWindow(True)
                owner._launcherState = LauncherBindingsState.Restore(New JObject From {{"externalLaunchers", seed.ToJson()}}, root)
                Return owner
            End Function
        For scenario = 0 To 7
            Dim first = makeOwner(), stale = makeOwner()
            Try
                Dim initialBinding = If(scenario = 7, Nothing, bindings(scenario Mod 3))
                Dim newBinding = If(scenario = 6, Nothing, bindings((scenario + 1) Mod 3))
                selectTarget(first, initialBinding, game) : selectTarget(stale, initialBinding, game)
                Dim path = IO.Path.Combine(root, "target-conflict-" & scenario & ".json")
                first.SavePreferenceValues(path, Nothing, 0, True)
                stale._preferenceBaseline = first.BuildSettings(0)
                stale._preferenceBaselinePath = IO.Path.GetFullPath(path)
                Dim identityOnly = scenario < 3 OrElse scenario >= 6
                selectTarget(first, If(identityOnly, initialBinding, newBinding), If(identityOnly, otherGame, game))
                first.SavePreferenceValues(path, Nothing, 0)
                Dim changed = File.ReadAllBytes(path)
                stale.SavePreferenceValues(path, Nothing, 0)
                check(File.ReadAllBytes(path).SequenceEqual(changed), "unchanged stale window preserves the complete newer install target: " & scenario)
                DownloadSettingsFile.SaveChanges(path, Nothing, New JObject From {{"source", 99}, {"future", True},
                    {"externalLaunchers", New JObject From {{"futureTargetMetadata", "keep"}, {"selectedRows", New JObject From {{"FutureLauncher", "future-row"}}},
                        {"bindings", New JObject From {{"other-window", New ExternalLauncherBinding With {.Id = "other-window", .Workspace = root}.ToJson()}}}}}})
                Dim expectedBinding = If(identityOnly, newBinding, initialBinding), expectedRoot = If(identityOnly, game, otherGame)
                selectTarget(stale, expectedBinding, expectedRoot)
                stale.SavePreferenceValues(path, Nothing, 1.25)
                Dim saved = JObject.Parse(File.ReadAllText(path)), target = saved("externalLaunchers")
                Dim expectedId = If(expectedBinding?.Id, "")
                check(CStr(saved("gameRoot")) = expectedRoot AndAlso CStr(target("targetRoot")) = expectedRoot AndAlso CStr(target("targetBindingId")) = expectedId,
                    "editing either install target half publishes the whole current target: " & scenario)
                Dim restored = LauncherBindingsState.Restore(saved, root)
                check(restored.TargetRoot = expectedRoot AndAlso restored.TargetBindingId = expectedId AndAlso CInt(saved("source")) = 99 AndAlso
                    CBool(saved("future")) AndAlso CStr(saved("speed")) = "1.25" AndAlso CStr(target("futureTargetMetadata")) = "keep" AndAlso
                    CStr(target("selectedRows")("FutureLauncher")) = "future-row" AndAlso target("bindings")("other-window") IsNot Nothing,
                    "target saves round-trip without replacing independent or unknown concurrent settings: " & scenario)
            Finally
                first.Close() : stale.Close()
            End Try
        Next

        Dim retry = makeOwner()
        Try
            Dim path = IO.Path.Combine(root, "target-retry.json")
            selectTarget(retry, bindings(0), game) : retry.SavePreferenceValues(path, Nothing, 0, True)
            Dim before = File.ReadAllBytes(path), baseline = retry._preferenceBaseline.ToString()
            selectTarget(retry, bindings(1), game)
            Dim blocked = False
            Using held As New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)
                Try
                    retry.SavePreferenceValues(path, Nothing, 0)
                Catch ex As IOException
                    blocked = True
                End Try
            End Using
            check(blocked AndAlso retry._preferenceBaseline.ToString() = baseline AndAlso File.ReadAllBytes(path).SequenceEqual(before),
                "failed install target publication retains the baseline and original bytes for retry")
            retry.SavePreferenceValues(path, Nothing, 0)
            Dim saved = JObject.Parse(File.ReadAllText(path))
            check(CStr(saved("gameRoot")) = game AndAlso CStr(saved("externalLaunchers")("targetRoot")) = game AndAlso
                CStr(saved("externalLaunchers")("targetBindingId")) = bindings(1).Id,
                "retry publishes the complete chosen target after releasing the failed save gate")

            Dim future As New JObject From {{"format", 999}, {"targetRoot", "opaque"}, {"targetBindingId", New JArray("future")}}
            path = IO.Path.Combine(root, "target-unknown-format.json")
            DownloadSettingsFile.Save(path, New JObject From {{"externalLaunchers", future.DeepClone()}})
            retry._launcherState = LauncherBindingsState.Restore(New JObject From {{"externalLaunchers", future}}, root)
            selectTarget(retry, Nothing, game) : retry.SavePreferenceValues(path, Nothing, 0, True)
            check(JToken.DeepEquals(JObject.Parse(File.ReadAllText(path))("externalLaunchers"), future),
                "explicit save keeps unknown launcher formats opaque while preserving the manual game directory")
            selectTarget(retry, Nothing, otherGame) : retry.SavePreferenceValues(path, Nothing, 0)
            saved = JObject.Parse(File.ReadAllText(path))
            check(CStr(saved("gameRoot")) = otherGame AndAlso JToken.DeepEquals(saved("externalLaunchers"), future),
                "editing a manual root never inserts known target fields into an unknown launcher format")
        Finally
            retry.Close()
        End Try
    End Sub
End Class
