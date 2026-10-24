Imports System.Windows
Imports System.Windows.Controls
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Shared ReadOnly VersionCategories As String() = {"正式版", "预览版", "远古版", "愚人节版"}

    Private NotInheritable Class VersionCardState
        Friend Category As String
        Friend Entries As List(Of VersionPresentation)
        Friend Latest As Boolean
    End Class

    Private Sub AddSupplementalVersionCards()
        Dim versions = _versions.Select(Function(entry) New VersionPresentation(entry)).OrderByDescending(Function(view) view.Entry.Released).ToList()
        For Each category In VersionCategories
            Dim entries = versions.Where(Function(view) view.Category = category).ToList()
            If Not entries.Any(Function(view) view.Entry.IsSupplemental) Then Continue For
            Dim card = VersionCards.Children.OfType(Of PclCard)().SingleOrDefault(Function(item) TryCast(item.Tag, VersionCardState)?.Category = category)
            If card Is Nothing Then
                AddVersionCard(category, entries, True, False)
                Continue For
            End If
            Dim state = DirectCast(card.Tag, VersionCardState)
            Dim additions = entries.Select(Function(view, index) New With {.View = view, .Index = index}).
                Where(Function(item) Not state.Entries.Any(Function(previous) previous.Entry Is item.View.Entry)).ToList()
            If additions.Count = 0 Then Continue For
            state.Entries = entries
            card.Title = category & " (" & entries.Count & ")"
            Dim builder As Func(Of Windows.UIElement) = Function() BuildVersionRows(state)
            Dim body = TryCast(card.Body, StackPanel)
            If body Is Nothing OrElse card.HasDeferredBodyRefresh Then
                card.RefreshBody(builder)
            Else
                Dim insert As Action = Sub()
                                           For Each item In additions
                                               body.Children.Insert(item.Index, CreateVersionPlaceholder(item.View, False))
                                           Next
                                       End Sub
                If card.Collapsed Then
                    ' Its closing target remains 40 DIP. Keep the accepted body and
                    ' existing rows available for the exit and the next expansion.
                    insert()
                Else
                    ' RefreshBody remeasures the height animation. Returning the same
                    ' panel keeps existing controls, lazy slots and keyboard focus.
                    card.RefreshBody(Function()
                                         insert()
                                         Return body
                                     End Function)
                End If
                card.Builder = builder
            End If
        Next
    End Sub

    Private Sub AddVersionCard(title As String, entries As List(Of VersionPresentation), collapsed As Boolean, latest As Boolean)
        If entries.Count = 0 Then Return
        Dim state As New VersionCardState With {.Category = title, .Entries = entries, .Latest = latest}
        Dim card As New PclCard With {.Title = title & If(latest, "", " (" & entries.Count & ")"), .Collapsible = Not latest, .Tag = state}
        card.Builder = Function() BuildVersionRows(state)
        card.Collapsed = collapsed
        ' Optional catalogues can introduce a category after later categories have
        ' already appeared. Keep PCL's fixed order without moving an existing card.
        Dim rank = Array.IndexOf(VersionCategories, title)
        Dim index = VersionCards.Children.OfType(Of PclCard)().Count(Function(item) Array.IndexOf(VersionCategories, DirectCast(item.Tag, VersionCardState).Category) < rank)
        VersionCards.Children.Insert(index, card)
    End Sub

    Private Function BuildVersionRows(state As VersionCardState) As StackPanel
        Dim stack As New StackPanel()
        For Each entry In state.Entries
            stack.Children.Add(CreateVersionPlaceholder(entry, state.Latest))
        Next
        Return stack
    End Function

    Private Function CreateVersionPlaceholder(entry As VersionPresentation, latest As Boolean) As PclLazyRow
        Return New PclLazyRow(Function()
                                  Dim row As New PclListItem With {.Title = entry.DisplayId, .Height = 42, .Info = entry.Description(latest), .InfoTag = entry.SourceTag, .Logo = entry.Icon, .Tag = entry.Entry}
                                  AddHandler row.Click, Sub(sender, args)
                                                            If Not CanSelectVersionRow(row, entry.Entry) Then Return
                                                            Dim keyboard = TypeOf args Is Input.KeyEventArgs
                                                            _keyboardVersionReturn = If(keyboard, New WeakReference(Of PclListItem)(row), Nothing)
                                                            SelectVersion(entry.Entry)
                                                            If keyboard Then FocusDownloadPage()
                                                        End Sub
                                  Return row
                              End Function)
    End Function

    Private Function CanSelectVersionRow(row As PclListItem, entry As VersionEntry) As Boolean
        If _closing OrElse _busy OrElse _choosingOnline OrElse _downloadView <> DownloadView.Versions OrElse
            DownloadPage.Visibility <> Visibility.Visible OrElse VersionPage.Visibility <> Visibility.Visible OrElse
            Not PageSurface.IsEnabled OrElse Not PageSurface.IsHitTestVisible OrElse Not DownloadViewHost.IsHitTestVisible OrElse
            Not row.IsEnabled OrElse Not row.IsHitTestVisible OrElse row.Visibility <> Visibility.Visible OrElse
            Not _versions.Any(Function(candidate) candidate Is entry) Then Return False
        Dim body = TryCast(row.Parent, StackPanel)
        Return body IsNot Nothing AndAlso VersionCards.Children.OfType(Of PclCard)().Any(
            Function(card) card.Body Is body AndAlso Not card.Collapsed AndAlso Not card.HasDeferredBodyRefresh AndAlso card.Visibility = Visibility.Visible)
    End Function
End Class
