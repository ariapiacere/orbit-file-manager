using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace OrbitFileManager.Controls;

/// <summary>
/// Row panel of the Details view. Bringing a row into view only scrolls up or down, never sideways.
/// </summary>
/// <remarks>
/// A Details row is as wide as all its columns, often wider than the pane. Whenever WPF brings a row into view, it
/// also scrolls horizontally to the row's edge: selecting after New Folder / Rename / a reload, focus returning from
/// a dialog, Home/End and arrow keys, type-to-select. For a row that is not created yet that is nearly the far
/// right, which hides the Name column. All of those arrive here through <see cref="IScrollInfo.MakeVisible"/>.
/// The request is narrowed to a 1 px strip at the left edge of what is already visible, so only the vertical part
/// is left to do. (Putting the offset back afterwards instead makes WPF retry the request in an endless layout loop.)
/// Scrolling sideways on purpose (scrollbar, Shift+wheel, the selection rectangle's auto-scroll) goes through
/// SetHorizontalOffset / LineLeft / MouseWheelLeft instead and still works.
/// </remarks>
public sealed class DetailsRowsPanel : VirtualizingStackPanel, IScrollInfo
{
    Rect IScrollInfo.MakeVisible(Visual visual, Rect rectangle) => MakeVisible(visual, VerticalOnly(visual, rectangle));

    private Rect VerticalOnly(Visual visual, Rect rectangle)
    {
        if (rectangle.IsEmpty || !CanHorizontallyScroll || !IsAncestorOf(visual))
            return rectangle;

        try
        {
            // The panel arranges its rows shifted left by the horizontal offset, so in its own coordinates x = 0 is
            // always the left edge of what is visible. Measured: viewport-relative alternatives land 2 px off.
            double panelX = visual.TransformToAncestor(this).Transform(rectangle.TopLeft).X;
            double shift = 1 - panelX;
            return new Rect(rectangle.X + shift, rectangle.Y, 1, rectangle.Height);
        }
        catch (InvalidOperationException)
        {
            return rectangle; // not connected to this panel after all
        }
    }
}
