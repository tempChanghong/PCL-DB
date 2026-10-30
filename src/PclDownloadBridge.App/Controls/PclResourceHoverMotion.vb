Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Media.Animation

Namespace Controls
    ' PCL 5ef81e4 MyResourceItem.RefreshColor: animate only the backing plate.
    ' It has its own 0.8 / 0.996 / 1 scales, unlike MyListItem's whole-row press.
    Friend NotInheritable Class PclResourceHoverMotion
        Private ReadOnly _implementation As Global.PclDownloadBridge.Presentation.ResourceHoverMotion

        Friend Sub New(back As Border)
            _implementation = New Global.PclDownloadBridge.Presentation.ResourceHoverMotion(back,
                Function(origin, destination, milliseconds) New PclRadioColorAnimation(origin, destination, milliseconds))
        End Sub
        Friend ReadOnly Property Transition As ClockGroup
            Get
                Return _implementation.Transition
            End Get
        End Property
        Friend Sub RefreshPalette(color As Color)
            _implementation.RefreshPalette(color)
        End Sub
        Friend Sub Update(active As Boolean, pressed As Boolean, color As Color, animate As Boolean, pauseForChecks As Boolean)
            _implementation.Update(active, pressed, color, animate, pauseForChecks)
        End Sub
    End Class
End Namespace
