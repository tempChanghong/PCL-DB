Imports System.IO
Imports System.Diagnostics
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Media.Imaging
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Core
Imports PclDownloadBridge.Controls

Partial Public Class MainWindow
    Private _componentSelection As New ComponentSelection
    Private ReadOnly _componentRequests As New Dictionary(Of ComponentKind, CancellationTokenSource)
    Private ReadOnly _componentPending As New HashSet(Of ComponentKind)
    Private ReadOnly _unresolvedComponents As New HashSet(Of ComponentKind)
    Private _componentsDirty As Boolean
    Private _componentProvider As Func(Of SourcePolicy, ComponentKind, String, CancellationToken, Task(Of List(Of ComponentEntry)))
    Private ReadOnly _componentEntries As New Dictionary(Of ComponentKind, List(Of ComponentEntry))
    Private ReadOnly _componentErrors As New Dictionary(Of ComponentKind, String)
    Private ReadOnly _componentViews As New Dictionary(Of ComponentKind, ComponentView)
    Private _autoName As String
    Private _componentGeneration As Integer
    Private ReadOnly _suppressedDependencies As New HashSet(Of ComponentKind)
    Private Class ComponentView
        Public Card As PclCard
        Public Summary As TextBlock
        Public Info As PclComponentSummary
        Public Icon As Image
        Public Clear As PclIconButton
        Public BodyInitialized As Boolean
        Public BodyEntries As List(Of ComponentEntry)
        Public BodyError As String
        Public BodyPending As Boolean
    End Class

    Private Sub InitializeComponents()
        CancelComponentRequests()
        _componentPending.Clear() : _unresolvedComponents.Clear()
        _componentsDirty = False
        _suppressedDependencies.Clear()
        _componentSelection = New ComponentSelection()
        _componentEntries.Clear() : _componentErrors.Clear() : _componentViews.Clear() : ComponentCards.Children.Clear()
        _autoName = New VersionPresentation(_selectedEntry).DisplayId
        Dim owner = _selectedEntry
        For Each kind As ComponentKind In [Enum].GetValues(GetType(ComponentKind))
            Dim card As New PclCard With {.Title = ComponentCatalog.Label(kind), .SwapLogoRight = True, .Collapsible = False, .Collapsed = True, .Margin = New Thickness(0, 0, 0, 12), .BodyMargin = New Thickness(20, 40, 18, 15)}
            Dim summaryGrid As New Grid With {.Height = 18, .VerticalAlignment = VerticalAlignment.Center}
            summaryGrid.ColumnDefinitions.Add(New ColumnDefinition())
            summaryGrid.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(30)})
            Dim info As New PclComponentSummary
            Dim icon As New Image With {.Source = New BitmapImage(New Uri("pack://application:,,,/PclDownloadBridge;component/Images/Blocks/" & ComponentCatalog.Icon(kind) & ".png")), .Width = 18, .Height = 18, .Margin = New Thickness(0, 0, 7, 0), .Visibility = Visibility.Collapsed}
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.Linear)
            Dim summary As New TextBlock With {.Text = "获取中……", .VerticalAlignment = VerticalAlignment.Center, .Foreground = Brushes.Gray, .TextTrimming = TextTrimming.CharacterEllipsis}
            Dim clear As New PclIconButton With {.Width = 30, .Height = 30, .VerticalAlignment = VerticalAlignment.Center, .LogoScale = 0.5,
                .ToolTip = "取消安装 " & ComponentCatalog.Label(kind), .Visibility = Visibility.Collapsed,
                .Logo = "F1 M2,0 L0,2 8,10 0,18 2,20 10,12 18,20 20,18 12,10 20,2 18,0 10,8 2,0Z"}
            AddHandler clear.Click, Sub(sender, e)
                                        If Not CurrentComponentView(kind, owner, card) Then Return
                                        ClearComponent(kind)
                                        e.Handled = True
                                    End Sub
            Grid.SetColumn(summary, 1) : Grid.SetColumn(clear, 1)
            info.Children.Add(icon) : info.Children.Add(summary)
            summaryGrid.Children.Add(info) : summaryGrid.Children.Add(clear)
            card.HeaderContent = summaryGrid
            card.HeaderContentMargin = New Thickness(132, 0, 32, 0)
            _componentViews(kind) = New ComponentView With {.Card = card, .Summary = summary, .Info = info, .Icon = icon, .Clear = clear}
            AddHandler card.FoldChanged, Sub() RefreshComponentHeader(kind)
            ComponentCards.Children.Add(card)
        Next
        If _selectedEntry.IsSupplemental Then
            For Each kind In _componentViews.Keys
                _componentEntries(kind) = New List(Of ComponentEntry)()
            Next
        ElseIf _preview AndAlso _componentProvider Is Nothing Then
            PreviewComponents()
        Else
            LoadComponentsAsync(_selectedEntry)
        End If
        RefreshComponents()
    End Sub

    Private Sub CancelComponentRequests()
        _componentGeneration += 1
        For Each request In _componentRequests.Values.ToList() : request.Cancel() : Next
        _componentRequests.Clear()
        _componentPending.Clear()
    End Sub

    Private Async Sub LoadComponentsAsync(entry As VersionEntry)
        Await RefreshComponentListsAsync()
    End Sub

    Private Async Function RefreshComponentListsAsync(Optional onlyKind As ComponentKind? = Nothing) As Task
        If _busy OrElse _closing OrElse _selectedEntry Is Nothing Then Return
        If _downloadView = DownloadView.Modpacks Then Return
        If _selectedEntry.IsSupplemental Then
            _componentsDirty = False
            RefreshComponents()
            Return
        End If
        If _componentsDirty Then onlyKind = Nothing
        If Not onlyKind.HasValue Then CancelComponentRequests()
        _componentsDirty = False
        Dim entry = _selectedEntry, generation = _componentGeneration
        Dim kinds = If(onlyKind.HasValue, {onlyKind.Value}, [Enum].GetValues(GetType(ComponentKind)).Cast(Of ComponentKind)().ToArray())
        Dim operations As New List(Of Task)
        Dim requestPolicy = Policy()
        For Each kind In kinds
            Dim previous As CancellationTokenSource = Nothing
            If _componentRequests.TryGetValue(kind, previous) Then previous.Cancel()
            Dim request As New CancellationTokenSource(TimeSpan.FromSeconds(100))
            _componentRequests(kind) = request
            _componentPending.Add(kind) : _unresolvedComponents.Remove(kind)
            _componentEntries.Remove(kind) : _componentErrors.Remove(kind)
        Next
        RefreshComponents()
        For Each kind In kinds
            operations.Add(LoadComponentKindAsync(requestPolicy, kind, entry, generation, _componentRequests(kind)))
        Next
        Await Task.WhenAll(operations)
    End Function

    Private Function IsCurrentComponentRequest(kind As ComponentKind, entry As VersionEntry, generation As Integer, request As CancellationTokenSource) As Boolean
        Dim current As CancellationTokenSource = Nothing
        Return Not _closing AndAlso entry Is _selectedEntry AndAlso generation = _componentGeneration AndAlso
            _componentRequests.TryGetValue(kind, current) AndAlso current Is request
    End Function

    Private Async Function LoadComponentKindAsync(policy As SourcePolicy, kind As ComponentKind, entry As VersionEntry, generation As Integer, request As CancellationTokenSource) As Task
        Try
            Dim versions As List(Of ComponentEntry)
            If _componentProvider IsNot Nothing Then
                versions = Await _componentProvider(policy, kind, entry.Id, request.Token)
            ElseIf _preview Then
                versions = New List(Of ComponentEntry)()
            Else
                versions = Await Task.Run(Function() New ComponentCatalog(policy).LoadAsync(kind, entry.Id, request.Token))
            End If
            If Not IsCurrentComponentRequest(kind, entry, generation, request) Then Return
            request.Token.ThrowIfCancellationRequested()
            AcceptComponentList(kind, entry, generation, versions)
        Catch ex As Exception
            If Not IsCurrentComponentRequest(kind, entry, generation, request) Then Return
            _componentPending.Remove(kind)
            _componentEntries.Remove(kind)
            _componentErrors(kind) = If(request.IsCancellationRequested, "获取超时，可展开重试", "获取失败，可展开重试")
            If Not _preview Then Log(ex)
            RefreshComponents()
        Finally
            Dim current As CancellationTokenSource = Nothing
            If _componentRequests.TryGetValue(kind, current) AndAlso current Is request Then _componentRequests.Remove(kind)
            request.Dispose()
        End Try
    End Function

    Private Sub AcceptComponentList(kind As ComponentKind, entry As VersionEntry, generation As Integer, versions As List(Of ComponentEntry))
        If _closing OrElse entry IsNot _selectedEntry OrElse generation <> _componentGeneration Then Return
        _componentEntries(kind) = versions
        _componentPending.Remove(kind) : _componentErrors.Remove(kind)
        If Not _busy Then
            Dim selected = _componentSelection.Item(kind)
            If selected IsNot Nothing Then
                Dim replacement = versions.FirstOrDefault(Function(candidate) candidate.Kind = kind AndAlso candidate.Minecraft = entry.Id AndAlso
                    String.Equals(candidate.Version, selected.Version, StringComparison.Ordinal) AndAlso
                    (kind <> ComponentKind.Forge OrElse String.Equals(candidate.Url, selected.Url, StringComparison.Ordinal)))
                If replacement Is Nothing Then
                    _unresolvedComponents.Add(kind)
                Else
                    _componentSelection.Entries(kind) = replacement
                    _unresolvedComponents.Remove(kind)
                End If
            End If
            ApplyAutoDependencies()
        End If
        RefreshComponents()
    End Sub

    Private Function ComponentInstallProblem() As String
        For Each kind In _componentSelection.Entries.Keys
            If _componentsDirty OrElse _componentPending.Contains(kind) Then Return "正在刷新已选组件，已保留版本与名称，请稍后开始安装。"
            If _componentErrors.ContainsKey(kind) Then Return ComponentCatalog.Label(kind) & " 列表获取失败，请重试或取消选择该组件。"
            If _unresolvedComponents.Contains(kind) Then Return "当前来源未提供已选的 " & ComponentCatalog.Label(kind) & " 版本；已保留选择，请刷新或重新选择。"
        Next
        If _selectedEntry IsNot Nothing Then
            Try
                _componentSelection.Validate(_selectedEntry.Id)
            Catch ex As InvalidDataException
                Return ex.Message
            End Try
        End If
        Return Nothing
    End Function

    Private Sub ApplyAutoDependencies()
        If _componentSelection.Has(ComponentKind.Fabric) Then AutoSelectDependency(ComponentKind.FabricApi)
        If _componentSelection.Has(ComponentKind.Fabric) AndAlso _componentSelection.Has(ComponentKind.OptiFine) Then AutoSelectDependency(ComponentKind.OptiFabric)
    End Sub

    Private Sub RefreshComponents()
        If _selectedEntry Is Nothing Then Return
        Dim versionDrop = MinecraftVersionRules.ReleaseFamily(_selectedEntry.Id)
        For Each pair In _componentViews
            Dim kind = pair.Key, view = pair.Value
            ' PCL ReloadSelected only shows dependencies when their parents are selected.
            Dim visible = True
            If kind = ComponentKind.LiteLoader Then visible = versionDrop < 130 OrElse _selectedEntry.Kind.StartsWith("old_", StringComparison.Ordinal)
            If kind = ComponentKind.Forge Then visible = VersionPresentation.IsStandardFormat(_selectedEntry.Id)
            If kind = ComponentKind.NeoForge Then visible = _selectedEntry.Released >= New DateTimeOffset(2023, 6, 11, 0, 0, 0, TimeSpan.Zero)
            If kind = ComponentKind.Fabric Then visible = Not _selectedEntry.Kind.StartsWith("old_", StringComparison.Ordinal) AndAlso versionDrop > 130
            If kind = ComponentKind.FabricApi Then visible = _componentSelection.Has(ComponentKind.Fabric)
            If kind = ComponentKind.OptiFabric Then visible = _componentSelection.Has(ComponentKind.Fabric) AndAlso _componentSelection.Has(ComponentKind.OptiFine)
            If _selectedEntry.IsSupplemental Then visible = False
            view.Card.Visibility = If(visible, Visibility.Visible, Visibility.Collapsed)
            Dim selected = _componentSelection.Item(kind)
            Dim restriction = _componentSelection.Unavailable(kind, _selectedEntry.Id)
            Dim available As New List(Of ComponentEntry)
            If _componentEntries.ContainsKey(kind) Then available = _componentEntries(kind).Where(Function(v) _componentSelection.Unavailable(kind, _selectedEntry.Id, v) Is Nothing).ToList()
            Dim errorText = If(_componentErrors.ContainsKey(kind), _componentErrors(kind), Nothing)
            Dim availability = If(Not _componentEntries.ContainsKey(kind), "获取中……", If(available.Count > 0, "可以添加", "无适用版本"))
            view.Summary.Text = If(selected?.Version, If(restriction, If(errorText, availability)))
            Dim selectedIssue As String = Nothing
            If selected IsNot Nothing Then
                If _componentsDirty OrElse _componentPending.Contains(kind) Then
                    view.Summary.Text &= "（刷新中）"
                ElseIf _unresolvedComponents.Contains(kind) Then
                    view.Summary.Text &= "（待确认）"
                ElseIf errorText Is Nothing Then
                    selectedIssue = _componentSelection.Unavailable(kind, _selectedEntry.Id, selected)
                    If selectedIssue IsNot Nothing Then view.Summary.Text &= "（不兼容）"
                End If
            End If
            view.Summary.ToolTip = If(selectedIssue Is Nothing, view.Summary.Text, selected.Version & "：" & selectedIssue)
            view.Summary.Foreground = PclAnimation.Brush(If(selected Is Nothing, "ColorBrushGray4", "ColorBrushGray1"))
            view.Icon.Visibility = If(selected Is Nothing, Visibility.Collapsed, Visibility.Visible)
            view.Clear.Visibility = view.Icon.Visibility
            Dim pending = _componentPending.Contains(kind)
            ' PCL ReloadSelected / PreviewSwap keep loading catalogues folded.
            ' Retry remains an explicit local error-card action after loading ends.
            view.Card.Collapsible = Not _componentsDirty AndAlso Not pending AndAlso restriction Is Nothing AndAlso (available.Count > 0 OrElse errorText IsNot Nothing)
            If Not view.Card.Collapsible Then view.Card.Collapsed = True
            If Not view.BodyInitialized OrElse view.BodyPending <> pending OrElse
                view.BodyError <> errorText OrElse Not view.BodyEntries.SequenceEqual(available) Then
                Dim owner = _selectedEntry, generation = _componentGeneration
                view.Card.RefreshBody(Function() BuildComponentBody(kind, available, errorText, owner, generation))
                view.BodyInitialized = True
                view.BodyEntries = available : view.BodyError = errorText : view.BodyPending = pending
            End If
            ' An invalidated body can remain visible only to finish a fold. Its
            ' old tags and highlight must not be rebound to newly accepted data.
            Dim body = If(view.Card.HasDeferredBodyRefresh, Nothing, TryCast(view.Card.Body, StackPanel))
            If body IsNot Nothing Then
                For Each row In body.Children.OfType(Of PclListItem)()
                    Dim chosen = selected IsNot Nothing AndAlso row.Tag Is selected
                    If row.Selected <> chosen Then row.Selected = chosen
                Next
            End If
            RefreshComponentHeader(kind)
        Next
        Dim installProblem = RefreshInstallAvailability()
        If installProblem IsNot Nothing AndAlso InstanceName.ValidationMessage.Length = 0 Then
            ComponentHint.Text = installProblem
        ElseIf _selectedEntry.IsSupplemental Then
            Dim detail = SupplementalVersions.VariantDescription(_selectedEntry)
            ComponentHint.Text = "UVMC 特供下载 · " & SupplementalVersions.Label(_selectedEntry) & "，仅支持原版。" & If(detail Is Nothing, "", " " & detail & "。")
        ElseIf _componentSelection.Has(ComponentKind.Fabric) AndAlso Not _componentSelection.Has(ComponentKind.FabricApi) Then
            ComponentHint.Text = "建议安装 Fabric API，以支持大多数 Mod。"
        ElseIf _componentSelection.Has(ComponentKind.OptiFine) AndAlso _componentSelection.Entries.Count > 1 Then
            ComponentHint.Text = "OptiFine 与部分 Mod 兼容性不佳，请谨慎安装。"
        Else
            ComponentHint.Text = If(_componentSelection.Entries.Count = 0, "展开卡片选择组件，或直接安装原版。", "已选组件将与游戏一同安装。")
        End If
    End Sub

    Private Function BuildComponentBody(kind As ComponentKind, available As List(Of ComponentEntry), errorText As String) As UIElement
        Return BuildComponentBody(kind, available, errorText, _selectedEntry, _componentGeneration)
    End Function

    Private Function BuildComponentBody(kind As ComponentKind, available As List(Of ComponentEntry), errorText As String, owner As VersionEntry, generation As Integer) As UIElement
        Dim body As New StackPanel
        If _componentPending.Contains(kind) Then
            body.Children.Add(New TextBlock With {.Text = "正在获取版本列表…", .Opacity = 0.6})
        ElseIf errorText IsNot Nothing Then
            Dim retry As New PclButton With {.Text = "重新获取 " & ComponentCatalog.Label(kind), .Height = 32, .Width = 170, .HorizontalAlignment = HorizontalAlignment.Left}
            AddHandler retry.Click, Async Sub()
                                        If Not CurrentComponentBody(kind, owner, generation, body) OrElse Not _componentErrors.ContainsKey(kind) Then Return
                                        Await RefreshComponentListsAsync(kind)
                                    End Sub
            body.Children.Add(retry)
        Else
            For Each entry In available
                body.Children.Add(New PclLazyRow(Function() CreateComponentRow(kind, entry, owner, generation, body)))
            Next
        End If
        Return body
    End Function

    Private Sub RefreshComponentHeader(kind As ComponentKind)
        Dim view = _componentViews(kind)
        view.Info.SetShown(view.Card.Collapsed)
    End Sub

    Private Function CurrentComponentView(kind As ComponentKind, owner As VersionEntry, card As PclCard) As Boolean
        Dim view As ComponentView = Nothing
        Return Not _closing AndAlso Not _busy AndAlso owner IsNot Nothing AndAlso owner Is _selectedEntry AndAlso _downloadView = DownloadView.Install AndAlso
            DownloadPage.Visibility = Visibility.Visible AndAlso _componentViews.TryGetValue(kind, view) AndAlso view.Card Is card AndAlso card.Visibility = Visibility.Visible
    End Function

    Private Function CurrentComponentBody(kind As ComponentKind, owner As VersionEntry, generation As Integer, body As StackPanel) As Boolean
        Dim view As ComponentView = Nothing
        Return generation = _componentGeneration AndAlso Not _componentsDirty AndAlso Not _componentPending.Contains(kind) AndAlso
            _componentViews.TryGetValue(kind, view) AndAlso CurrentComponentView(kind, owner, view.Card) AndAlso
            view.Card.Collapsible AndAlso Not view.Card.Collapsed AndAlso Not view.Card.HasDeferredBodyRefresh AndAlso view.Card.Body Is body
    End Function

    Private Function CreateComponentRow(kind As ComponentKind, entry As ComponentEntry, owner As VersionEntry, generation As Integer, body As StackPanel) As PclListItem
        Dim row As New PclListItem With {.Title = If(entry.Title, entry.Version), .Info = If(entry.Stable, "稳定版", "测试版") & If(kind = ComponentKind.OptiFine AndAlso entry.RequiredForge IsNot Nothing, "，兼容 Forge " & If(entry.RequiredForge = "", "已支持版本", entry.RequiredForge), ""), .Height = 42, .Logo = "pack://application:,,,/PclDownloadBridge;component/Images/Blocks/" & ComponentCatalog.Icon(kind) & ".png", .Selected = _componentSelection.Item(kind) Is entry, .Tag = entry}
        AddHandler row.Click, Sub(sender, args)
                                  If Not CurrentComponentBody(kind, owner, generation, body) OrElse _componentErrors.ContainsKey(kind) Then Return
                                  Dim accepted As List(Of ComponentEntry) = Nothing
                                  If entry.Kind <> kind OrElse entry.Minecraft <> owner.Id OrElse Not _componentEntries.TryGetValue(kind, accepted) OrElse
                                      Not accepted.Any(Function(candidate) candidate Is entry) Then Return
                                  SelectComponent(entry)
                                  If TypeOf args Is Input.KeyEventArgs Then _componentViews(kind).Card.FocusHeader()
                              End Sub
        Return row
    End Function

    Private Sub SelectComponent(entry As ComponentEntry)
        If _busy Then Return
        If _selectedEntry Is Nothing OrElse _selectedEntry.IsSupplemental Then Return
        If _componentSelection.Unavailable(entry.Kind, _selectedEntry.Id, entry) IsNot Nothing Then Return
        _componentSelection.Entries(entry.Kind) = entry
        _unresolvedComponents.Remove(entry.Kind)
        _componentViews(entry.Kind).Card.Collapsed = True
        ApplyAutoDependencies()
        UpdateInstanceName()
        RefreshComponents()
    End Sub
    Private Sub AutoSelectDependency(kind As ComponentKind)
        If _componentSelection.Has(kind) OrElse Not _componentEntries.ContainsKey(kind) OrElse _suppressedDependencies.Contains(kind) Then Return
        Dim entry = _componentEntries(kind).FirstOrDefault(Function(v) _componentSelection.Unavailable(kind, _selectedEntry.Id, v) Is Nothing)
        If entry IsNot Nothing Then
            _componentSelection.Entries(kind) = entry
            _unresolvedComponents.Remove(kind)
        End If
    End Sub
    Private Sub ClearComponent(kind As ComponentKind)
        If _busy Then Return
        _componentSelection.Entries.Remove(kind)
        _unresolvedComponents.Remove(kind)
        If kind = ComponentKind.FabricApi OrElse kind = ComponentKind.OptiFabric Then _suppressedDependencies.Add(kind)
        If kind = ComponentKind.Fabric Then
            _componentSelection.Entries.Remove(ComponentKind.FabricApi)
            _unresolvedComponents.Remove(ComponentKind.FabricApi)
        End If
        If kind = ComponentKind.Fabric OrElse kind = ComponentKind.OptiFine Then
            _componentSelection.Entries.Remove(ComponentKind.OptiFabric)
            _unresolvedComponents.Remove(ComponentKind.OptiFabric)
        End If
        _componentViews(kind).Card.Collapsed = True
        UpdateInstanceName()
        RefreshComponents()
    End Sub
    Private Sub UpdateInstanceName()
        Dim icon = IconFor(_selectedEntry)
        For Each kind In {ComponentKind.Fabric, ComponentKind.Forge, ComponentKind.NeoForge, ComponentKind.LiteLoader, ComponentKind.OptiFine}
            If Not _componentSelection.Has(kind) Then Continue For
            icon = "pack://application:,,,/PclDownloadBridge;component/Images/Blocks/" & ComponentCatalog.Icon(kind) & ".png"
            Exit For
        Next
        SelectedVersionIcon.Source = New BitmapImage(New Uri(icon))
        If _nameEdited Then Return
        _autoName = New VersionPresentation(_selectedEntry).DisplayId
        If _componentSelection.Has(ComponentKind.Fabric) Then _autoName &= "-Fabric " & _componentSelection.Item(ComponentKind.Fabric).Version.Split("+"c)(0)
        If _componentSelection.Has(ComponentKind.Forge) Then _autoName &= "-Forge_" & _componentSelection.Item(ComponentKind.Forge).Version
        If _componentSelection.Has(ComponentKind.NeoForge) Then _autoName &= "-NeoForge_" & _componentSelection.Item(ComponentKind.NeoForge).Version.Split("+"c)(0)
        If _componentSelection.Has(ComponentKind.LiteLoader) Then _autoName &= "-LiteLoader"
        If _componentSelection.Has(ComponentKind.OptiFine) Then _autoName &= "-OptiFine_" & _componentSelection.Item(ComponentKind.OptiFine).Version.Replace(" ", "_")
        SetAutomaticInstanceName(_autoName)
    End Sub
    Private Sub ChooseJava(sender As Object, e As RoutedEventArgs)
        Dim dialog As New Microsoft.Win32.OpenFileDialog With {.Title = "选择 java.exe", .Filter = "Java (java.exe)|java.exe", .CheckFileExists = True}
        If dialog.ShowDialog(Me) = True Then JavaPathBox.Text = dialog.FileName
    End Sub
    Private Sub PreviewComponents()
        For Each kind As ComponentKind In [Enum].GetValues(GetType(ComponentKind))
            _componentEntries(kind) = New List(Of ComponentEntry)
        Next
        For Each kind In {ComponentKind.Forge, ComponentKind.NeoForge, ComponentKind.Fabric, ComponentKind.FabricApi, ComponentKind.OptiFine}
            Dim version = If(kind = ComponentKind.NeoForge, "21.1.77", If(kind = ComponentKind.Forge, "52.0.28", If(kind = ComponentKind.Fabric, "0.16.9", If(kind = ComponentKind.FabricApi, "0.110.0+1.21.1", "J2"))))
            _componentEntries(kind).Add(New ComponentEntry With {.Kind = kind, .Minecraft = _selectedEntry.Id, .Version = version, .RequiredForge = Nothing, .Data = New JObject From {{"name", ComponentCatalog.Label(kind) & " " & version}}})
        Next
    End Sub
End Class
