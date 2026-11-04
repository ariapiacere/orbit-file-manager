using System.Windows;
using System.Windows.Controls;

namespace OrbitFileManager.Controls;

/// <summary>
/// Lays out a pane's tab strip browser-style: the tab list (first child) on the left and the new-tab button
/// (second child) right after the last tab. When the tabs need more room than there is, the list gets everything
/// except the button's width and scrolls, so the button stays visible at the right edge.
/// </summary>
public sealed class TabStripPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count < 2)
            return MeasureSingle(availableSize);

        UIElement tabs = InternalChildren[0], button = InternalChildren[1];
        button.Measure(availableSize);
        double room = Math.Max(0, availableSize.Width - button.DesiredSize.Width);
        tabs.Measure(new Size(room, availableSize.Height));

        return new Size(
            Math.Min(tabs.DesiredSize.Width, room) + button.DesiredSize.Width,
            Math.Max(tabs.DesiredSize.Height, button.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count < 2)
        {
            foreach (UIElement child in InternalChildren)
                child.Arrange(new Rect(finalSize));
            return finalSize;
        }

        UIElement tabs = InternalChildren[0], button = InternalChildren[1];
        double buttonWidth = button.DesiredSize.Width;
        double tabsWidth = Math.Min(tabs.DesiredSize.Width, Math.Max(0, finalSize.Width - buttonWidth));
        tabs.Arrange(new Rect(0, 0, tabsWidth, finalSize.Height));
        button.Arrange(new Rect(tabsWidth, 0, buttonWidth, finalSize.Height));
        return finalSize;
    }

    private Size MeasureSingle(Size availableSize)
    {
        Size size = default;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(availableSize);
            size = child.DesiredSize;
        }
        return size;
    }
}
