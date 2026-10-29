Imports System.IO
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core

Friend Module LauncherBindingRecoveryChecks
    Friend ReadOnly CaseNames As String() = {
        "missing-format-empty", "missing-format-bindings", "row-pcl-object", "row-hmcl-array", "row-ce-object",
        "rows-array", "rows-string", "target-root-object", "target-id-array", "target-both-object",
        "legacy-root-array", "legacy-workspace-object", "future-row-string", "future-row-object", "future-row-null",
        "future-row-bad-selection", "control-native", "control-legacy", "control-version", "control-string"}

    Friend Function RunAsync(testRoot As String, Optional group As String = "binding-recovery-all") As Task(Of List(Of String))
        Dim passed As New List(Of String)
        For Each caseName In CaseNames
            If group <> "binding-recovery-all" AndAlso group <> "binding-recovery-" & caseName Then Continue For
            Dim root = Path.Combine(testRoot, "binding-recovery-" & Guid.NewGuid().ToString("N"))
            Dim fallback = Path.Combine(root, "fallback"), game = Path.Combine(root, "game")
            Dim settings = BuildSettings(root), raw = DirectCast(settings("externalLaunchers"), JObject)
            Select Case caseName
                Case "missing-format-empty"
                    settings("externalLaunchers") = New JObject()
                Case "missing-format-bindings"
                    raw.Remove("format")
                Case "row-pcl-object", "future-row-bad-selection"
                    raw("selectedRows")("Pcl") = New JObject()
                Case "row-hmcl-array"
                    raw("selectedRows")("Hmcl") = New JArray()
                Case "row-ce-object"
                    raw("selectedRows")("PclCe") = New JObject()
                Case "rows-array"
                    raw("selectedRows") = New JArray("p2")
                Case "rows-string"
                    raw("selectedRows") = "p2"
                Case "target-root-object"
                    raw("targetRoot") = New JObject()
                Case "target-id-array"
                    raw("targetBindingId") = New JArray()
                Case "target-both-object"
                    raw("targetRoot") = New JObject()
                    raw("targetBindingId") = New JObject()
                Case "legacy-root-array"
                    settings.Remove("externalLaunchers")
                    settings("gameRoot") = New JArray()
                Case "legacy-workspace-object"
                    settings.Remove("externalLaunchers")
                    settings("pclBase") = New JObject()
                Case "control-legacy"
                    settings.Remove("externalLaunchers")
                Case "control-version"
                    raw("format") = 999
                Case "control-string"
                    settings("externalLaunchers") = New JValue("future")
            End Select
            If caseName.StartsWith("future-row-", StringComparison.Ordinal) Then
                raw("selectedRows")("FutureLauncher") = If(caseName = "future-row-null", JValue.CreateNull(),
                    If(caseName = "future-row-string", CType(New JValue("future-id"), JToken), New JObject From {{"opaque", New JArray(1, 2, 3)}}))
            End If
            Dim before = settings.DeepClone(), state As LauncherBindingsState = Nothing, failure As Exception = Nothing
            Try
                state = LauncherBindingsState.Restore(settings, fallback)
            Catch ex As Exception
                failure = ex
            End Try
            Check(failure Is Nothing AndAlso Expected(state, settings, root, caseName),
                "binding recovery isolates invalid fields and preserves the intended effective state: " & caseName, passed)
            Dim saved = state.ToJson(), restoredSettings = DirectCast(settings.DeepClone(), JObject)
            restoredSettings("externalLaunchers") = saved.DeepClone()
            Dim restored = LauncherBindingsState.Restore(restoredSettings, fallback)
            Check(Equivalent(state, restored) AndAlso If(state.ReadOnlyFormat, JToken.DeepEquals(saved, settings("externalLaunchers")), True),
                "recovered bindings and targets round trip without an implicit format migration: " & caseName, passed)
            Check(JToken.DeepEquals(settings, before), "binding recovery and save leave caller preference JSON unchanged: " & caseName, passed)
            Dim exported = state.ToJson()
            If TypeOf exported Is JObject Then
                exported("probe") = True
                If TypeOf exported("selectedRows") Is JObject Then DirectCast(exported("selectedRows"), JObject).RemoveAll()
                If TypeOf exported("bindings") Is JObject Then DirectCast(exported("bindings"), JObject).RemoveAll()
            ElseIf TypeOf exported Is JValue Then
                DirectCast(exported, JValue).Value = "changed"
            End If
            Check(JToken.DeepEquals(state.ToJson(), saved), "exported binding preferences cannot mutate the stored original snapshot: " & caseName, passed)
            Dim healthy = BuildSettings(root), repaired = LauncherBindingsState.Restore(healthy, fallback)
            Dim binding = repaired.Bindings.Single(Function(b) b.Id = repaired.TargetBindingId)
            Dim target As New LauncherInstallTarget(New GameFolder With {.Location = repaired.TargetRoot, .LauncherBindingId = binding.Id,
                .LauncherKind = binding.Kind, .LauncherWorkspace = binding.Workspace}, fallback)
            binding.Workspace = Path.Combine(root, "later")
            Check(repaired.Warnings.Count = 0 AndAlso repaired.Bindings.Count = 6 AndAlso target.Root = game AndAlso target.BindingId = "h2" AndAlso
                target.Workspace = Path.Combine(root, "h2") AndAlso target.Kind = ExternalLauncherKind.Hmcl,
                "explicitly repaired preferences recover without stale warnings and retain a frozen install target: " & caseName, passed)
        Next
        If passed.Count = 0 Then Throw New ArgumentException("Unknown binding recovery group: " & group)
        Return Task.FromResult(passed)
    End Function

    Private Function BuildSettings(root As String) As JObject
        Dim bindings As New JObject()
        For Each pair In New KeyValuePair(Of String, String)() {
            New KeyValuePair(Of String, String)("p", "Pcl"), New KeyValuePair(Of String, String)("h", "Hmcl"),
            New KeyValuePair(Of String, String)("ce", "PclCe")}
            For index = 1 To 2
                Dim id = pair.Key & index
                bindings(id) = New JObject From {{"kind", pair.Value}, {"workspace", Path.Combine(root, id)}, {"futureBinding", "keep"}}
            Next
        Next
        Return New JObject From {{"pclBase", Path.Combine(root, "p1")}, {"gameRoot", Path.Combine(root, "game")},
            {"externalLaunchers", New JObject From {{"format", 1}, {"bindings", bindings}, {"futureRoot", New JObject From {{"keep", True}}},
            {"selectedRows", New JObject From {{"Pcl", "p2"}, {"Hmcl", "h2"}, {"PclCe", "ce2"}}},
            {"targetRoot", Path.Combine(root, "game")}, {"targetBindingId", "h2"}}}}
    End Function

    Private Function Expected(state As LauncherBindingsState, settings As JObject, root As String, caseName As String) As Boolean
        If state Is Nothing Then Return False
        If caseName.StartsWith("missing-format-", StringComparison.Ordinal) OrElse caseName = "control-version" OrElse caseName = "control-string" Then
            Return state.ReadOnlyFormat AndAlso state.Bindings.Count = 0 AndAlso state.Warnings.Count > 0 AndAlso JToken.DeepEquals(state.ToJson(), settings("externalLaunchers"))
        End If
        If caseName.StartsWith("legacy-", StringComparison.Ordinal) OrElse caseName = "control-legacy" Then
            Return Not state.ReadOnlyFormat AndAlso state.Bindings.Count = 1 AndAlso state.TargetBindingId = "legacy-pcl" AndAlso
                state.Bindings(0).Workspace = Path.Combine(root, If(caseName = "legacy-workspace-object", "fallback", "p1")) AndAlso
                state.TargetRoot = If(caseName = "legacy-root-array" OrElse caseName = "legacy-workspace-object", "", Path.Combine(root, "game")) AndAlso
                (state.Warnings.Count > 0) = (caseName <> "control-legacy")
        End If
        Dim badRow = caseName.StartsWith("row-", StringComparison.Ordinal) OrElse caseName.StartsWith("rows-", StringComparison.Ordinal) OrElse caseName = "future-row-bad-selection"
        Dim badTarget = caseName.StartsWith("target-", StringComparison.Ordinal)
        If state.ReadOnlyFormat OrElse state.Bindings.Count <> 6 OrElse (state.Warnings.Count > 0) <> (badRow OrElse badTarget) Then Return False
        If state.TargetRoot <> If(badTarget, "", Path.Combine(root, "game")) OrElse If(state.TargetBindingId, "") <> If(badTarget, "", "h2") Then Return False
        Dim expectedPcl = If(caseName = "row-pcl-object" OrElse caseName = "future-row-bad-selection" OrElse caseName.StartsWith("rows-", StringComparison.Ordinal), "p1", "p2")
        Dim expectedHmcl = If(caseName = "row-hmcl-array" OrElse caseName.StartsWith("rows-", StringComparison.Ordinal), "h1", "h2")
        Dim expectedCe = If(caseName = "row-ce-object" OrElse caseName.StartsWith("rows-", StringComparison.Ordinal), "ce1", "ce2")
        If state.SelectedRows(ExternalLauncherKind.Pcl) <> expectedPcl OrElse state.SelectedRows(ExternalLauncherKind.Hmcl) <> expectedHmcl OrElse state.SelectedRows(ExternalLauncherKind.PclCe) <> expectedCe Then Return False
        Dim saved = state.ToJson()
        Return JToken.DeepEquals(saved("futureRoot"), settings("externalLaunchers")("futureRoot")) AndAlso
            JToken.DeepEquals(saved("bindings")("h2")("futureBinding"), settings("externalLaunchers")("bindings")("h2")("futureBinding")) AndAlso
            If(caseName.StartsWith("future-row-", StringComparison.Ordinal), JToken.DeepEquals(saved("selectedRows")("FutureLauncher"), settings("externalLaunchers")("selectedRows")("FutureLauncher")), True)
    End Function

    Private Function Equivalent(a As LauncherBindingsState, b As LauncherBindingsState) As Boolean
        Return a.ReadOnlyFormat = b.ReadOnlyFormat AndAlso a.Bindings.Count = b.Bindings.Count AndAlso If(a.TargetRoot, "") = If(b.TargetRoot, "") AndAlso
            If(a.TargetBindingId, "") = If(b.TargetBindingId, "") AndAlso a.SelectedRows.Count = b.SelectedRows.Count AndAlso
            a.SelectedRows.All(Function(pair) b.SelectedRows.ContainsKey(pair.Key) AndAlso b.SelectedRows(pair.Key) = pair.Value)
    End Function
    Private Sub Check(condition As Boolean, label As String, passed As List(Of String))
        If Not condition Then Throw New InvalidOperationException(label)
        passed.Add(label)
    End Sub
End Module
