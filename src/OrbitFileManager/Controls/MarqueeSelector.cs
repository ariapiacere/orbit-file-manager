using OrbitFileManager.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace OrbitFileManager.Controls;

/// <summary>
/// Rubber-band selection for a file list: dragging draws a rectangle and selects every item it touches.
/// Ctrl toggles those items against the selection from before the drag, Shift adds them to it.
/// <para>
/// Tile views (List, icons, thumbnails) are not virtualized, so item bounds are measured once in items-panel
/// coordinates, which stay fixed while the list scrolls. Details rows are virtualized and scroll by item, so they
/// are selected as the index range between the row under the anchor and the row under the pointer.
/// </para>
/// </summary>
public sealed class MarqueeSelector
{
    private readonly ListView _list;
    private readonly ScrollViewer _scrollViewer;
    private readonly FrameworkElement _viewport;
    private readonly bool _byRows;
    private readonly ModifierKeys _modifiers;
    private readonly HashSet<FileEntryItem> _baseSelection;
    private readonly List<FileEntryItem> _items;
    private readonly Dictionary<FileEntryItem, int> _indexOf;
    private readonly Action<List<FileEntryItem>> _applySelection;
    private readonly AdornerLayer? _adornerLayer;
    private readonly MarqueeAdorner _adorner;
    private readonly DispatcherTimer _autoScrollTimer;

    // Tile views
    private readonly Panel? _itemsHost;
    private readonly Point _anchorInHost;
    private readonly Rect[] _itemBounds = [];

    // Details rows: a slot is a row index plus the fraction of the row above the point; past the last row is Count + 0.5.
    private readonly double _anchorSlot;
    private readonly double _anchorBelowLastRow;
    private readonly double _anchorX;
    private readonly double _startHorizontalOffset;

    private Point _pointer;
    private List<FileEntryItem> _lastSelection = [];

    private MarqueeSelector(
        ListView list,
        ScrollViewer scrollViewer,
        FrameworkElement viewport,
        Point start,
        bool byRows,
        ModifierKeys modifiers,
        IEnumerable<FileEntryItem> baseSelection,
        Action<List<FileEntryItem>> applySelection)
    {
        _list = list;
        _scrollViewer = scrollViewer;
        _viewport = viewport;
        _byRows = byRows;
        _modifiers = modifiers;
        _baseSelection = new HashSet<FileEntryItem>(baseSelection);
        _applySelection = applySelection;
        _pointer = start;

        _items = list.Items.OfType<FileEntryItem>().ToList();
        _indexOf = new Dictionary<FileEntryItem, int>(_items.Count);
        for (int i = 0; i < _items.Count; i++)
            _indexOf[_items[i]] = i;

        if (byRows)
        {
            var rows = RealizedRows();
            _anchorSlot = SlotAt(start.Y, rows);
            if (_anchorSlot >= _items.Count && rows.Count > 0)
                _anchorBelowLastRow = start.Y - rows[^1].Bounds.Bottom;
            _anchorX = start.X;
            _startHorizontalOffset = scrollViewer.HorizontalOffset;
        }
        else
        {
            _itemsHost = FindItemsHost(viewport);
            if (_itemsHost is not null)
            {
                _anchorInHost = list.TranslatePoint(start, _itemsHost);
                _itemBounds = new Rect[_items.Count];
                for (int i = 0; i < _items.Count; i++)
                {
                    _itemBounds[i] = list.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement { IsVisible: true } container
                        ? container.TransformToAncestor(_itemsHost).TransformBounds(new Rect(container.RenderSize))
                        : Rect.Empty;
                }
            }
        }

        Color accent = list.TryFindResource("AccentBrush") is SolidColorBrush brush ? brush.Color : Color.FromRgb(0xFF, 0x7A, 0x1A);
        _adorner = new MarqueeAdorner(list, accent);
        _adornerLayer = AdornerLayer.GetAdornerLayer(list);
        _adornerLayer?.Add(_adorner);

        _autoScrollTimer = new DispatcherTimer(DispatcherPriority.Input, list.Dispatcher) { Interval = TimeSpan.FromMilliseconds(40) };
        _autoScrollTimer.Tick += OnAutoScrollTick;
        _autoScrollTimer.Start();
    }

    /// <summary>
    /// Starts a marquee at <paramref name="start"/> (in list coordinates). Returns null when the point is outside
    /// the list's scrolling area, e.g. on a column header or a scrollbar.
    /// </summary>
    public static MarqueeSelector? Begin(
        ListView list,
        Point start,
        bool byRows,
        ModifierKeys modifiers,
        IEnumerable<FileEntryItem> baseSelection,
        Action<List<FileEntryItem>> applySelection)
    {
        if (FindDescendant<ScrollViewer>(list) is not { } scrollViewer ||
            scrollViewer.Template?.FindName("PART_ScrollContentPresenter", scrollViewer) is not FrameworkElement viewport ||
            !Bounds(viewport, list).Contains(start))
        {
            return null;
        }

        return new MarqueeSelector(list, scrollViewer, viewport, start, byRows, modifiers, baseSelection, applySelection);
    }

    /// <summary>Moves the free corner of the rectangle to <paramref name="pointer"/> (in list coordinates).</summary>
    public void Update(Point pointer)
    {
        _pointer = pointer;
        Refresh();
    }

    public void End()
    {
        _autoScrollTimer.Stop();
        _autoScrollTimer.Tick -= OnAutoScrollTick;
        _adornerLayer?.Remove(_adorner);
    }

    private void Refresh()
    {
        Rect viewport = Bounds(_viewport, _list);
        Func<int, bool> inBand;
        Rect band;

        if (_byRows)
        {
            var rows = RealizedRows();
            double slot = SlotAt(_pointer.Y, rows);
            double low = Math.Min(_anchorSlot, slot);
            double high = Math.Max(_anchorSlot, slot);
            inBand = i => i + 1 > low && i < high;

            double anchorX = _anchorX + (_startHorizontalOffset - _scrollViewer.HorizontalOffset);
            band = new Rect(new Point(anchorX, AnchorY(rows, viewport)), _pointer);
        }
        else if (_itemsHost is not null)
        {
            var hostBand = new Rect(_anchorInHost, _list.TranslatePoint(_pointer, _itemsHost));
            inBand = i => _itemBounds[i].IntersectsWith(hostBand);
            band = new Rect(_itemsHost.TranslatePoint(hostBand.TopLeft, _list), _itemsHost.TranslatePoint(hostBand.BottomRight, _list));
        }
        else
        {
            inBand = _ => false;
            band = new Rect(_pointer, _pointer);
        }

        _adorner.Update(band, viewport);

        bool toggle = (_modifiers & ModifierKeys.Control) != 0;
        bool add = (_modifiers & ModifierKeys.Shift) != 0;
        var selection = new List<FileEntryItem>();
        for (int i = 0; i < _items.Count; i++)
        {
            bool inside = inBand(i);
            bool before = _baseSelection.Contains(_items[i]);
            if (toggle ? inside != before : add ? inside || before : inside)
                selection.Add(_items[i]);
        }

        if (!selection.SequenceEqual(_lastSelection))
        {
            _lastSelection = selection;
            _applySelection(selection);
        }
    }

    private void OnAutoScrollTick(object? sender, EventArgs e)
    {
        Rect viewport = Bounds(_viewport, _list);
        double dy = _pointer.Y < viewport.Top ? _pointer.Y - viewport.Top : _pointer.Y > viewport.Bottom ? _pointer.Y - viewport.Bottom : 0;
        double dx = _pointer.X < viewport.Left ? _pointer.X - viewport.Left : _pointer.X > viewport.Right ? _pointer.X - viewport.Right : 0;
        bool scrolled = false;

        if (dy != 0 && _scrollViewer.ScrollableHeight > 0)
        {
            // Details scrolls by row, the tile views by pixel; further outside the list scrolls faster.
            double step = _byRows && _scrollViewer.CanContentScroll
                ? Math.Min(1 + Math.Floor(Math.Abs(dy) / 25), 5)
                : Math.Min(10 + Math.Abs(dy) / 2, 80);
            _scrollViewer.ScrollToVerticalOffset(_scrollViewer.VerticalOffset + Math.Sign(dy) * step);
            scrolled = true;
        }

        if (dx != 0 && _scrollViewer.ScrollableWidth > 0)
        {
            _scrollViewer.ScrollToHorizontalOffset(_scrollViewer.HorizontalOffset + Math.Sign(dx) * Math.Min(10 + Math.Abs(dx) / 2, 80));
            scrolled = true;
        }

        if (scrolled)
        {
            _list.UpdateLayout();
            Refresh();
        }
    }

    /// <summary>Realized Details rows in display order, with their bounds in list coordinates.</summary>
    private List<(int Index, Rect Bounds)> RealizedRows()
    {
        var rows = new List<(int Index, Rect Bounds)>();
        CollectRows(_viewport, rows);
        rows.Sort((a, b) => a.Index.CompareTo(b.Index));
        return rows;
    }

    private void CollectRows(DependencyObject parent, List<(int Index, Rect Bounds)> rows)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is ListViewItem row)
            {
                if (row.IsVisible && row.DataContext is FileEntryItem item && _indexOf.TryGetValue(item, out int index))
                    rows.Add((index, Bounds(row, _list)));
            }
            else
            {
                CollectRows(child, rows);
            }
        }
    }

    private double SlotAt(double y, List<(int Index, Rect Bounds)> rows)
    {
        if (rows.Count == 0)
            return _items.Count + 0.5;

        if (y < rows[0].Bounds.Top)
            return rows[0].Index;

        var last = rows[^1];
        if (y >= last.Bounds.Bottom)
            return last.Index == _items.Count - 1 ? _items.Count + 0.5 : last.Index + 1;

        for (int i = rows.Count - 1; i >= 0; i--)
        {
            Rect bounds = rows[i].Bounds;
            if (y >= bounds.Top)
                return rows[i].Index + Math.Min((y - bounds.Top) / Math.Max(1, bounds.Height), 0.999);
        }

        return rows[0].Index;
    }

    /// <summary>Where the anchor is now drawn; just outside the viewport once its row has scrolled away.</summary>
    private double AnchorY(List<(int Index, Rect Bounds)> rows, Rect viewport)
    {
        if (_anchorSlot >= _items.Count)
        {
            return rows.Count > 0 && rows[^1].Index == _items.Count - 1
                ? rows[^1].Bounds.Bottom + _anchorBelowLastRow
                : viewport.Bottom + 2;
        }

        int index = (int)Math.Floor(_anchorSlot);
        foreach (var row in rows)
        {
            if (row.Index == index)
                return row.Bounds.Top + (_anchorSlot - index) * row.Bounds.Height;
        }

        return rows.Count > 0 && index < rows[0].Index ? viewport.Top - 2 : viewport.Bottom + 2;
    }

    private static Panel? FindItemsHost(DependencyObject viewport) =>
        FindDescendant<ItemsPresenter>(viewport) is { } presenter && VisualTreeHelper.GetChildrenCount(presenter) > 0
            ? VisualTreeHelper.GetChild(presenter, 0) as Panel
            : null;

    private static Rect Bounds(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;

            if (FindDescendant<T>(child) is { } nested)
                return nested;
        }

        return null;
    }

    /// <summary>The translucent accent rectangle, clipped to the list's scrolling area.</summary>
    private sealed class MarqueeAdorner : Adorner
    {
        private readonly Brush _fill;
        private readonly Pen _outline;
        private Rect _band;
        private Rect _clip;

        public MarqueeAdorner(UIElement adornedElement, Color accent) : base(adornedElement)
        {
            IsHitTestVisible = false;
            _fill = new SolidColorBrush(Color.FromArgb(0x30, accent.R, accent.G, accent.B));
            _fill.Freeze();
            _outline = new Pen(new SolidColorBrush(accent), 1);
            _outline.Freeze();
        }

        public void Update(Rect band, Rect clip)
        {
            _band = band;
            _clip = clip;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            if (_band.IsEmpty || _clip.IsEmpty)
                return;

            // Half-pixel offsets keep the 1 px outline crisp.
            var rect = new Rect(
                Math.Round(_band.X) + 0.5,
                Math.Round(_band.Y) + 0.5,
                Math.Max(0, Math.Round(_band.Width) - 1),
                Math.Max(0, Math.Round(_band.Height) - 1));

            drawingContext.PushClip(new RectangleGeometry(_clip));
            drawingContext.DrawRectangle(_fill, _outline, rect);
            drawingContext.Pop();
        }
    }
}
