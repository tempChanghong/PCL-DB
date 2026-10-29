Imports System.IO
Imports System.Windows
Imports System.Windows.Automation
Imports System.Windows.Automation.Peers
Imports System.Windows.Automation.Provider
Imports System.Windows.Controls
Imports System.Windows.Controls.Primitives
Imports System.Windows.Data
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Media.Imaging
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports PclDownloadBridge.Controls
Imports PclDownloadBridge.Core

Partial Public Class MainWindow
    Private Sub VerifySelectionControls(check As Action(Of Boolean, String), artifactDirectory As String)
        Dim selector As New PclComboBox With {.SelectedIndex = 0, .Margin = New Thickness(10)}
        For Each label In {"自动选择", "优先使用官方源", "优先使用镜像源"}
            selector.Items.Add(New PclComboBoxItem With {.Content = label})
        Next
        Dim folders As New PclComboBox With {.ItemTemplate = FolderList.ItemTemplate, .MinHeight = 49, .Margin = New Thickness(10)}
        folders.ItemsSource = New List(Of GameFolder) From {
            New GameFolder With {.Name = "PCL 当前游戏文件夹", .Location = "D:\Games\Minecraft\.minecraft"},
            New GameFolder With {.Name = "中文与空格路径", .Location = "D:\游戏目录\带有空格的 PCL 游戏文件夹\用于检查很长路径仍然受到菜单宽度约束\.minecraft"}}
        folders.SelectedIndex = 0
        Dim flag As New PclCheckBox With {.Content = "复用其他游戏文件夹中的文件", .Margin = New Thickness(10)}
        Dim panel As New StackPanel()
        panel.Children.Add(selector) : panel.Children.Add(folders) : panel.Children.Add(flag)
        Dim host As New Window With {.Content = panel, .Width = 380, .Height = 230, .Left = -32000, .Top = -32000, .Opacity = 0, .AllowsTransparency = True, .WindowStyle = WindowStyle.None, .WindowStartupLocation = WindowStartupLocation.Manual, .ShowInTaskbar = False, .ShowActivated = False}
        Try
            host.Show() : host.UpdateLayout()
            Dim border = DirectCast(selector.Template.FindName("border", selector), Border)
            check(border.CornerRadius.TopLeft = 3 AndAlso selector.Template.FindName("DropDownToggle", selector) IsNot Nothing, "PCL selector applies its own round border and arrow template")
            Dim popup = DirectCast(selector.Template.FindName("PART_Popup", selector), Popup)
            Dim popupPanel = DirectCast(popup.Child, Grid)
            popupPanel.Opacity = 0 ' Keep the owned test popup invisible even when Windows repositions it.
            PressControlKey(selector, Key.F4)
            FlushControlLayout()
            check(selector.IsDropDownOpen AndAlso popup.IsOpen AndAlso Math.Abs(popupPanel.ActualWidth - selector.ActualWidth) < 1, "F4 opens a real popup matching the selector width")
            If artifactDirectory IsNot Nothing Then
                Dim menu = DirectCast(selector.Template.FindName("dropDownBorder", selector), Border)
                SaveControlVisual(menu, Path.Combine(artifactDirectory, "preview-" & GetType(MainWindow).Assembly.GetName().Version.ToString(2) & "-dropdown.png"))
            End If
            PressControlKey(selector, Key.Escape)
            check(Not selector.IsDropDownOpen AndAlso selector.SelectedIndex = 0 AndAlso Double.IsNaN(selector.Width), "Escape closes without changing selection or replacing automatic width")
            Dim selectionChanged = False
            AddHandler selector.SelectionChanged, Sub(sender, e) selectionChanged = e.AddedItems.Count = 1 AndAlso e.RemovedItems.Count = 1
            selector.SelectedIndex = 1
            check(selectionChanged AndAlso CStr(DirectCast(selector.SelectedItem, PclComboBoxItem).Content) = "优先使用官方源", "selecting a PCL item preserves WPF selection-change events and values")
            Dim bubbled = False
            AddHandler panel.MouseWheel, Sub() bubbled = True
            selector.RaiseEvent(New MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) With {.RoutedEvent = Mouse.PreviewMouseWheelEvent})
            check(selector.SelectedIndex = 1 AndAlso bubbled, "wheel on a closed selector scrolls its parent without changing the option")
            Dim folderPopup = DirectCast(folders.Template.FindName("PART_Popup", folders), Popup)
            DirectCast(folderPopup.Child, Grid).Opacity = 0
            folders.IsDropDownOpen = True : FlushControlLayout()
            check(TypeOf folders.ItemContainerGenerator.ContainerFromIndex(1) Is PclComboBoxItem AndAlso Math.Abs(DirectCast(folderPopup.Child, Grid).ActualWidth - folders.ActualWidth) < 1, "bound long-path directories use PCL item containers and a constrained popup")
            folders.SelectedIndex = 1
            folders.IsDropDownOpen = False
            check(DirectCast(folders.SelectedItem, GameFolder).Name = "中文与空格路径", "two-line directory selection retains the underlying game folder")
            VerifyPopupOpacitySynchronization(selector, host, panel, check)
            Dim checkBorder = DirectCast(flag.Template.FindName("ShapeBorder", flag), Border)
            check(checkBorder.Width = 18 AndAlso checkBorder.CornerRadius.TopLeft = 3 AndAlso flag.Template.FindName("ShapeCheck", flag) IsNot Nothing, "PCL checkbox uses its original 18 DIP outline and check geometry")
            Dim peer = UIElementAutomationPeer.CreatePeerForElement(flag)
            Dim toggle = DirectCast(peer.GetPattern(PatternInterface.Toggle), IToggleProvider)
            toggle.Toggle()
            check(flag.IsChecked.GetValueOrDefault() AndAlso toggle.ToggleState = ToggleState.On, "checkbox exposes working WPF accessibility toggle semantics")
            flag.IsEnabled = False : flag.Activate()
            check(flag.IsChecked.GetValueOrDefault(), "disabled checkbox cannot be changed by activation")
        Finally
            selector.IsDropDownOpen = False : folders.IsDropDownOpen = False
            host.Close()
        End Try
        SpeedLimitBox.Text = "NaN"
        SaveSettings(Nothing, Nothing)
        check(SpeedLimitBox.ValidationMessage.Length > 0 AndAlso Not StatusText.Text.Contains("已保存"), "invalid speed shows inline feedback and cannot report a successful save")
        SpeedLimitBox.Text = "12.5"
        Dim limit As Double
        check(TryGetSpeedLimit(limit) AndAlso limit = 12.5 AndAlso SpeedLimitBox.ValidationMessage = "", "correcting speed immediately clears inline validation")
        SpeedLimitBox.Text = "0.0000001"
        SaveSettings(Nothing, Nothing)
        check(Not TryGetSpeedLimit(limit) AndAlso SpeedLimitBox.ValidationMessage.Length > 0 AndAlso Not StatusText.Text.Contains("已保存"), "a positive sub-byte speed cannot silently round to unlimited or be saved")
        SpeedLimitBox.Text = (1.0 / (1024 * 1024)).ToString(Globalization.CultureInfo.CurrentCulture)
        check(TryGetSpeedLimit(limit) AndAlso SpeedLimitBox.ValidationMessage = "", "the minimum one-byte-per-second speed is accepted")
        SpeedLimitBox.Text = "0"
        check(TryGetSpeedLimit(limit) AndAlso limit = 0 AndAlso SpeedLimitBox.ValidationMessage = "", "explicit zero continues to mean unlimited")
        _restoringSettings = True
        check(Not PersistSettings(), "restoring settings cannot write an intermediate directory choice")
        _restoringSettings = False
        SpeedLimitBox.Text = "0"
        Dim directory = Path.Combine(Path.GetTempPath(), "PclDownloadBridgeSettings-" & Guid.NewGuid().ToString("N"))
        Dim file = Path.Combine(directory, "settings.json")
        DownloadSettingsFile.Save(file, New JObject From {{"speed", "0"}})
        DownloadSettingsFile.Save(file, New JObject From {{"speed", "12.5"}, {"gameRoot", "D:\游戏\PCL\.minecraft"}})
        Dim original = IO.File.ReadAllBytes(file)
        check(CStr(JObject.Parse(IO.File.ReadAllText(file))("gameRoot")) = "D:\游戏\PCL\.minecraft", "settings atomically replace an existing file and preserve Unicode")
        Dim failed = False
        Using locked = New FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None)
            Try
                DownloadSettingsFile.Save(file, New JObject From {{"speed", "99"}})
            Catch ex As IOException
                failed = True
            End Try
        End Using
        check(failed AndAlso IO.File.ReadAllBytes(file).SequenceEqual(original) AndAlso IO.Directory.GetFiles(directory, "*.tmp").Length = 0, "failed settings replacement preserves the old file and removes its temporary write")
    End Sub

    Private Shared Sub VerifyPopupOpacitySynchronization(selector As PclComboBox, host As Window, panel As Panel, check As Action(Of Boolean, String))
        Dim popupPanel = DirectCast(selector.Template.FindName("PanPopup", selector), Grid)
        Dim originalTemplate = selector.Template
        Dim secondHost As Window = Nothing
        ' Windows may reposition an off-screen popup onto a visible monitor.
        ' Hide only its pixels while exercising real opening and owner bindings.
        popupPanel.Visibility = Visibility.Hidden
        Try
            host.Opacity = 0.7
            selector.IsDropDownOpen = True : FlushControlLayout()
            Dim firstBinding = BindingOperations.GetBindingExpressionBase(popupPanel, UIElement.OpacityProperty)
            check(firstBinding IsNot Nothing AndAlso Math.Abs(popupPanel.Opacity - 0.7) < 0.00001,
                "the real selector popup follows its own seventy-percent owner rather than the global main window")
            host.Opacity = 0.4 : FlushControlLayout()
            check(Math.Abs(popupPanel.Opacity - 0.4) < 0.00001 AndAlso BindingOperations.GetBindingExpressionBase(popupPanel, UIElement.OpacityProperty) Is firstBinding,
                "changing owner opacity updates an open popup without replacing its binding")
            For index = 0 To 2
                selector.IsDropDownOpen = False : selector.IsDropDownOpen = True : FlushControlLayout()
            Next
            check(BindingOperations.GetBindingExpressionBase(popupPanel, UIElement.OpacityProperty) Is firstBinding AndAlso selector.SelectedIndex = 1,
                "rapid popup reopening retains a single opacity binding and the selected source")
            selector.IsDropDownOpen = False
            host.Opacity = 1 : FlushControlLayout()
            check(popupPanel.Opacity = 1, "restoring full owner opacity also restores the closed popup")

            host.Opacity = 0.4 : FlushControlLayout()
            Dim retiredPanel = popupPanel
            selector.Template = Nothing : selector.ApplyTemplate()
            selector.Template = originalTemplate : selector.ApplyTemplate()
            popupPanel = DirectCast(selector.Template.FindName("PanPopup", selector), Grid)
            popupPanel.Visibility = Visibility.Hidden
            check(popupPanel IsNot retiredPanel AndAlso BindingOperations.GetBindingExpressionBase(retiredPanel, UIElement.OpacityProperty) Is Nothing AndAlso
                Math.Abs(retiredPanel.Opacity - 0.4) < 0.00001 AndAlso Math.Abs(popupPanel.Opacity - 0.4) < 0.00001,
                "retemplating releases only the retired popup binding and preserves its last opacity during fade-out")

            selector.IsDropDownOpen = True : FlushControlLayout() : selector.IsDropDownOpen = False
            panel.Children.Remove(selector)
            secondHost = New Window With {.Content = selector, .Width = 380, .Height = 100, .Left = -32000, .Top = -32000, .Opacity = 0,
                .AllowsTransparency = True, .WindowStyle = WindowStyle.None, .WindowStartupLocation = WindowStartupLocation.Manual, .ShowInTaskbar = False, .ShowActivated = False}
            secondHost.Show() : secondHost.UpdateLayout()
            selector.IsDropDownOpen = True : FlushControlLayout()
            Dim secondBinding = TryCast(BindingOperations.GetBindingExpressionBase(popupPanel, UIElement.OpacityProperty), BindingExpression)
            check(secondBinding IsNot Nothing AndAlso secondBinding.ParentBinding.Source Is secondHost AndAlso popupPanel.Opacity = 0,
                "moving a selector to another real window replaces its opacity source without inheriting the previous owner")
            secondHost.Opacity = 0.6 : FlushControlLayout()
            check(Math.Abs(popupPanel.Opacity - 0.6) < 0.00001, "the second window controls its own open popup opacity")
            selector.IsDropDownOpen = False : secondHost.Content = Nothing : secondHost.Close() : secondHost = Nothing : FlushControlLayout()
            check(BindingOperations.GetBindingExpressionBase(popupPanel, UIElement.OpacityProperty) Is Nothing AndAlso Math.Abs(popupPanel.Opacity - 0.6) < 0.00001,
                "unloading releases the owner binding while preserving the popup's displayed opacity")
            panel.Children.Insert(0, selector) : host.UpdateLayout()

            Dim explicitSource As New Border With {.Opacity = 0.25}
            Dim explicitBinding = BindingOperations.SetBinding(popupPanel, UIElement.OpacityProperty, New Binding("Opacity") With {.Source = explicitSource})
            selector.IsDropDownOpen = True : FlushControlLayout()
            check(BindingOperations.GetBindingExpressionBase(popupPanel, UIElement.OpacityProperty) Is explicitBinding AndAlso popupPanel.Opacity = 0.25,
                "opening never replaces a caller-owned popup opacity binding")
            selector.IsDropDownOpen = False
            BindingOperations.ClearBinding(popupPanel, UIElement.OpacityProperty)
            popupPanel.BeginAnimation(UIElement.OpacityProperty, New System.Windows.Media.Animation.DoubleAnimation(0.2, 0.2, TimeSpan.FromMilliseconds(100)))
            check(popupPanel.HasAnimatedProperties AndAlso Not BindingOperations.IsDataBound(popupPanel, UIElement.OpacityProperty),
                "the caller animation is assigned without an opacity binding before the popup opens or receives its first tick")
            selector.IsDropDownOpen = True : FlushControlLayout()
            check(DependencyPropertyHelper.GetValueSource(popupPanel, UIElement.OpacityProperty).IsAnimated AndAlso Not BindingOperations.IsDataBound(popupPanel, UIElement.OpacityProperty),
                "opening leaves caller-owned popup opacity animation in place")
            selector.IsDropDownOpen = False : popupPanel.BeginAnimation(UIElement.OpacityProperty, Nothing)

            Dim preview As New PclComboBox With {.Template = originalTemplate}
            preview.ApplyTemplate()
            Dim previewPopup = DirectCast(preview.Template.FindName("PanPopup", preview), Grid)
            previewPopup.Opacity = 0.35
            preview.RaiseEvent(New RoutedEventArgs(FrameworkElement.LoadedEvent, preview))
            check(Window.GetWindow(preview) Is Nothing AndAlso Not BindingOperations.IsDataBound(previewPopup, UIElement.OpacityProperty) AndAlso previewPopup.Opacity = 0.35,
                "a windowless preview keeps its local popup opacity without binding to an unrelated application window")
            preview.RaiseEvent(New RoutedEventArgs(FrameworkElement.UnloadedEvent, preview))
        Finally
            selector.IsDropDownOpen = False
            If secondHost IsNot Nothing Then secondHost.Content = Nothing : secondHost.Close()
            If Not panel.Children.Contains(selector) Then panel.Children.Insert(0, selector)
            host.Opacity = 0
        End Try
    End Sub

    Private Shared Sub PressControlKey(control As UIElement, key As Key)
        control.RaiseEvent(New KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(control), Environment.TickCount, key) With {.RoutedEvent = Keyboard.KeyDownEvent})
        control.RaiseEvent(New KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(control), Environment.TickCount, key) With {.RoutedEvent = Keyboard.KeyUpEvent})
    End Sub

    Friend Shared Sub FlushControlLayout()
        Dim frame As New DispatcherFrame()
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, New Action(Sub() frame.Continue = False))
        Dispatcher.PushFrame(frame)
    End Sub

    Private Shared Sub SaveControlVisual(element As Border, file As String)
        element.UpdateLayout()
        Dim bitmap As New RenderTargetBitmap(CInt(Math.Ceiling(element.ActualWidth)), CInt(Math.Ceiling(element.ActualHeight)), 96, 96, PixelFormats.Pbgra32)
        ' The live popup has a white sibling behind this border. Include that surface in its capture.
        Dim surface As New DrawingVisual()
        Using drawing = surface.RenderOpen()
            drawing.DrawRectangle(Brushes.White, Nothing, New Rect(0, 0, element.ActualWidth, element.ActualHeight))
        End Using
        bitmap.Render(surface)
        bitmap.Render(element)
        Dim encoder As New PngBitmapEncoder()
        encoder.Frames.Add(BitmapFrame.Create(bitmap))
        Using output = IO.File.Create(file)
            encoder.Save(output)
        End Using
    End Sub
End Class
