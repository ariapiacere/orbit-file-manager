using OrbitFileManager.Models;
using OrbitFileManager.Utilities;
using OrbitFileManager.ViewModels;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace OrbitFileManager.Controls;

public partial class BrowserPaneControl : UserControl
{
    private const string TabDragFormat = "OrbitInternalTabDrag";
    private const double NameColumnMinWidth = 200;
    // ListViewItem margin + border + padding on both sides, so a fitted row never triggers the horizontal scrollbar.
    private const double DetailsRowChromeWidth = 14;
    private Point _fileDragStartPoint;
    private bool _isFileMouseDownOnItem;
    private Point _tabDragStartPoint;
    private BrowserPaneViewModel? _subscribedPane;
    private BrowserTabViewModel? _subscribedTab;
    private bool _nameColumnUserSized;
    private bool _isFittingNameColumn;
    private bool _isSyncingSelection;
    private bool _isMarqueeArmed;
    private Point _marqueeStartPoint;
    private ModifierKeys _marqueeModifiers;
    private List<FileEntryItem> _marqueeBaseSelection = [];
    private MarqueeSelector? _marquee;

    public static readonly DependencyProperty MainVmProperty = DependencyProperty.Register(
        nameof(MainVm),
        typeof(MainViewModel),
        typeof(BrowserPaneControl),
        new PropertyMetadata(null, OnMainVmChanged));

    public MainViewModel? MainVm
    {
        get => (MainViewModel?)GetValue(MainVmProperty);
        set => SetValue(MainVmProperty, value);
    }

    private BrowserPaneViewModel? PaneVm => DataContext as BrowserPaneViewModel;

    public BrowserPaneControl()
    {
        InitializeComponent();
        DataContextChanged += OnPaneDataContextChanged;

        DetailsListView.SizeChanged += (_, _) => FitNameColumn();
        DetailsListView.Loaded += (_, _) => FitNameColumn();
        DetailsListView.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            // The vertical scrollbar appearing/disappearing changes the usable width.
            if (e.ViewportWidthChange != 0)
                FitNameColumn();
        }));

        var widthDescriptor = DependencyPropertyDescriptor.FromProperty(GridViewColumn.WidthProperty, typeof(GridViewColumn));
        foreach (GridViewColumn column in DetailsGridView.Columns)
            widthDescriptor.AddValueChanged(column, OnDetailsColumnWidthChanged);

        foreach (ListView list in new[] { DetailsListView, CompactListView, SmallIconsListView, LargeIconsListView, ThumbnailsListView })
        {
            list.IsTextSearchEnabled = true;
            TextSearch.SetTextPath(list, nameof(FileEntryItem.Name));
            list.PreviewMouseLeftButtonUp += FileListView_PreviewMouseLeftButtonUp;
            list.LostMouseCapture += FileListView_LostMouseCapture;
        }
    }

    private void OnPaneDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_subscribedPane is not null)
            _subscribedPane.PropertyChanged -= OnPaneVmPropertyChanged;

        _subscribedPane = e.NewValue as BrowserPaneViewModel;
        if (_subscribedPane is not null)
            _subscribedPane.PropertyChanged += OnPaneVmPropertyChanged;

        RebindActiveTabSubscription(_subscribedPane?.ActiveTab);
    }

    private void OnPaneVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserPaneViewModel.ActiveTab))
        {
            RebindActiveTabSubscription(_subscribedPane?.ActiveTab);
            ResetScrollToLeftAndTop(resetVertical: true);
        }
    }

    private void RebindActiveTabSubscription(BrowserTabViewModel? newTab)
    {
        if (ReferenceEquals(_subscribedTab, newTab))
            return;

        if (_subscribedTab is not null)
        {
            _subscribedTab.FolderNavigated -= OnTabFolderNavigated;
            _subscribedTab.PropertyChanged -= OnActiveTabPropertyChanged;
        }

        _subscribedTab = newTab;
        if (_subscribedTab is not null)
        {
            _subscribedTab.FolderNavigated += OnTabFolderNavigated;
            _subscribedTab.PropertyChanged += OnActiveTabPropertyChanged;
        }
    }

    private void OnTabFolderNavigated(bool hasExplicitSelection)
    {
        ResetScrollToLeftAndTop(resetVertical: !hasExplicitSelection);
    }

    private void OnActiveTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Each view mode is its own ListView, and reloads (F5, view switch, file watcher) replace the item
        // instances. Either way the tab's selection must be pushed back into the visible list, once the
        // bindings have settled.
        if (e.PropertyName is nameof(BrowserTabViewModel.ViewMode) or nameof(BrowserTabViewModel.SelectedItem))
        {
            bool viewModeChanged = e.PropertyName == nameof(BrowserTabViewModel.ViewMode);
            Dispatcher.BeginInvoke(
                () => SyncSelectionIntoActiveList(viewModeChanged),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// Makes the visible list highlight exactly the tab's selection. Without this the list showed a stale or empty
    /// highlight after a view switch or refresh while Delete/Copy acted on the tab's real (invisible) selection.
    /// </summary>
    private void SyncSelectionIntoActiveList(bool viewModeChanged)
    {
        if (PaneVm?.ActiveTab is not { } tab)
            return;

        ListView lv = GetActiveListView();
        var displayed = new HashSet<FileEntryItem>(tab.DisplayedItems);
        var wanted = tab.SelectedItems.Where(displayed.Contains).ToList();
        var shown = lv.SelectedItems.OfType<FileEntryItem>().ToHashSet();

        if (!shown.SetEquals(wanted))
        {
            _isSyncingSelection = true;
            try
            {
                lv.SelectedItems.Clear();
                foreach (FileEntryItem item in wanted)
                    lv.SelectedItems.Add(item);
            }
            finally
            {
                _isSyncingSelection = false;
            }

            if (wanted.Count > 0)
                lv.ScrollIntoView(wanted[0]);
        }

        if (viewModeChanged && ReferenceEquals(lv, DetailsListView))
        {
            // Coming back to Details could leave the rows scrolled to the far right (Name column hidden).
            Dispatcher.BeginInvoke(() => FindDescendant<ScrollViewer>(DetailsListView)?.ScrollToLeftEnd(),
                System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
    }

    public void ResetScrollToLeftAndTop(bool resetVertical = true)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ResetScrollToLeftAndTop(resetVertical));
            return;
        }

        void Apply()
        {
            foreach (ListView lv in EnumerateFileListViews())
            {
                if (FindDescendant<ScrollViewer>(lv) is { } sv)
                {
                    sv.ScrollToLeftEnd();
                    if (resetVertical)
                        sv.ScrollToTop();
                }
            }
        }

        Apply();
        Dispatcher.BeginInvoke(Apply, System.Windows.Threading.DispatcherPriority.Loaded);
        Dispatcher.BeginInvoke(() =>
        {
            foreach (ListView lv in EnumerateFileListViews())
            {
                if (FindDescendant<ScrollViewer>(lv) is { } sv)
                    sv.ScrollToLeftEnd();
            }
        }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private IEnumerable<ListView> EnumerateFileListViews()
    {
        yield return DetailsListView;
        yield return CompactListView;
        yield return SmallIconsListView;
        yield return LargeIconsListView;
        yield return ThumbnailsListView;
    }

    private static void OnMainVmChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not BrowserPaneControl control)
            return;

        if (e.OldValue is MainViewModel oldVm)
            oldVm.PropertyChanged -= control.OnMainVmPropertyChanged;

        if (e.NewValue is MainViewModel newVm)
        {
            newVm.PropertyChanged += control.OnMainVmPropertyChanged;
            control.SyncDetailsColumns(newVm);
        }
    }

    private void OnMainVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is MainViewModel vm &&
            e.PropertyName is nameof(MainViewModel.ShowExtensionColumn)
                or nameof(MainViewModel.ShowSizeColumn)
                or nameof(MainViewModel.ShowTypeColumn)
                or nameof(MainViewModel.ShowModifiedColumn)
                or nameof(MainViewModel.ShowCreatedColumn)
                or nameof(MainViewModel.ShowAttributesColumn))
        {
            SyncDetailsColumns(vm);
        }
    }

    private void SyncDetailsColumns(MainViewModel vm)
    {
        ColExtension.Width = vm.ShowExtensionColumn ? 56 : 0;
        ColSize.Width = vm.ShowSizeColumn ? 80 : 0;
        ColType.Width = vm.ShowTypeColumn ? 132 : 0;
        ColModified.Width = vm.ShowModifiedColumn ? 124 : 0;
        ColCreated.Width = vm.ShowCreatedColumn ? 124 : 0;
        ColAttr.Width = vm.ShowAttributesColumn ? 48 : 0;
        FitNameColumn();
    }

    private void OnDetailsColumnWidthChanged(object? sender, EventArgs e)
    {
        if (_isFittingNameColumn)
            return;

        // A width change on Name that we did not make means the user dragged it: stop auto-fitting.
        if (ReferenceEquals(sender, ColName))
            _nameColumnUserSized = true;
        else
            FitNameColumn();
    }

    /// <summary>Gives the Name column whatever width the other visible columns leave free (never below the minimum).</summary>
    private void FitNameColumn()
    {
        if (_nameColumnUserSized || !DetailsListView.IsLoaded)
            return;

        double available = FindDescendant<ScrollViewer>(DetailsListView) is { ViewportWidth: > 0 } sv
            ? sv.ViewportWidth
            : DetailsListView.ActualWidth;
        double otherColumns = DetailsGridView.Columns
            .Where(c => !ReferenceEquals(c, ColName))
            .Sum(c => double.IsNaN(c.Width) ? c.ActualWidth : c.Width);
        double target = Math.Floor(Math.Max(NameColumnMinWidth, available - otherColumns - DetailsRowChromeWidth));

        if (Math.Abs(ColName.Width - target) < 1)
            return;

        _isFittingNameColumn = true;
        try
        {
            ColName.Width = target;
        }
        finally
        {
            _isFittingNameColumn = false;
        }
    }

    public ListView GetActiveListView()
    {
        return PaneVm?.ActiveTab?.ViewMode switch
        {
            BrowserViewMode.List => CompactListView,
            BrowserViewMode.SmallIcons => SmallIconsListView,
            BrowserViewMode.LargeIcons => LargeIconsListView,
            BrowserViewMode.Thumbnails => ThumbnailsListView,
            _ => DetailsListView
        };
    }

    public void SelectAllItems()
    {
        var lv = GetActiveListView();
        lv.Focus();
        lv.SelectAll();
    }

    public void FocusAddressBar()
    {
        Dispatcher.BeginInvoke(() =>
        {
            PaneAddressTextBox.Focus();
            PaneAddressTextBox.SelectAll();
        });
    }

    public void FocusQuickFilter()
    {
        PaneQuickFilterBox.Focus();
        PaneQuickFilterBox.SelectAll();
    }

    private void PaneRoot_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        PaneVm?.Activate();
    }

    // ─── Tab Strip Events & Drag Reorder ───────────────────────────────────────

    private void TabPill_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _tabDragStartPoint = e.GetPosition(this);

        if (e.ChangedButton == MouseButton.Middle &&
            sender is FrameworkElement { DataContext: BrowserTabViewModel tab })
        {
            PaneVm?.CloseTab(tab);
            e.Handled = true;
        }
    }

    private void TabPill_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed ||
            sender is not FrameworkElement { DataContext: BrowserTabViewModel tab } ||
            FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        Vector delta = e.GetPosition(this) - _tabDragStartPoint;
        if (Math.Abs(delta.X) > SystemParameters.MinimumHorizontalDragDistance * 2)
        {
            var data = new DataObject(TabDragFormat, tab);
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);
        }
    }

    private void TabPill_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(TabDragFormat) &&
            e.Data.GetData(TabDragFormat) is BrowserTabViewModel draggedTab &&
            sender is FrameworkElement { DataContext: BrowserTabViewModel targetTab })
        {
            PaneVm?.MoveTab(draggedTab, targetTab);
            e.Handled = true;
            return;
        }

        // Also support dropping files directly onto a Tab to copy/move into that tab's folder!
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] droppedFiles &&
            sender is FrameworkElement { DataContext: BrowserTabViewModel destTab } &&
            !destTab.IsSearchTab &&
            Directory.Exists(destTab.CurrentPath) &&
            MainVm is not null)
        {
            bool isMove = DetermineDropIsMove(e.KeyStates, droppedFiles[0], destTab.CurrentPath);
            _ = MainVm.EnqueueOrRunTransferAsync(droppedFiles, destTab.CurrentPath, isMove);
            e.Handled = true;
        }
    }

    private void CloseTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BrowserTabViewModel tab })
        {
            PaneVm?.CloseTab(tab);
            e.Handled = true;
        }
    }

    private BrowserTabViewModel? GetContextMenuTab(object sender)
    {
        if (sender is MenuItem mi &&
            mi.Parent is ContextMenu cm &&
            cm.PlacementTarget is FrameworkElement { DataContext: BrowserTabViewModel tab })
        {
            return tab;
        }
        return PaneVm?.ActiveTab;
    }

    private void TabContextMenu_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu { PlacementTarget: FrameworkElement { DataContext: BrowserTabViewModel tab } } cm)
        {
            foreach (object? item in cm.Items)
            {
                if (item is MenuItem mi)
                {
                    if (mi.Name == "PinMenuItem")
                        mi.Header = tab.IsPinned ? "Unpin Tab" : "Pin Tab";
                    else if (mi.Name == "LockMenuItem")
                        mi.Header = tab.IsLocked ? "Unlock Tab" : "Lock Tab to Current Folder";
                }
            }
        }
    }

    private void TabMenu_NewTab_Click(object sender, RoutedEventArgs e) => _ = PaneVm?.NewTabAsync();
    private void TabMenu_Duplicate_Click(object sender, RoutedEventArgs e) => _ = PaneVm?.DuplicateTabAsync(GetContextMenuTab(sender));
    private void TabMenu_RestoreClosed_Click(object sender, RoutedEventArgs e) => _ = PaneVm?.RestoreClosedTabAsync();
    private void TabMenu_Pin_Click(object sender, RoutedEventArgs e) => PaneVm?.TogglePinTab(GetContextMenuTab(sender));
    private void TabMenu_Lock_Click(object sender, RoutedEventArgs e) => PaneVm?.ToggleLockTab(GetContextMenuTab(sender));
    private void TabMenu_CloseOthers_Click(object sender, RoutedEventArgs e) => PaneVm?.CloseOtherTabs(GetContextMenuTab(sender));
    private void TabMenu_Close_Click(object sender, RoutedEventArgs e) => PaneVm?.CloseTab(GetContextMenuTab(sender));

    private void TabMenu_Rename_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetContextMenuTab(sender);
        if (tab is not null)
            MainVm?.PromptRenameTab(tab);
    }

    private void TabMenu_ResetLabel_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetContextMenuTab(sender);
        if (tab is not null)
            tab.CustomLabel = null;
    }

    // ─── Address Bar & Breadcrumb Events ───────────────────────────────────────

    // Hooked to PreviewMouseLeftButtonDown: the breadcrumb ScrollViewer fills the bar and marks the bubbling
    // MouseDown handled, so a bubbling handler only fired on the 2 px padding strip.
    private void BreadcrumbBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) is null && PaneVm?.ActiveTab is { } tab)
        {
            PaneVm.Activate();
            tab.AddressInputText = tab.CurrentPath;
            tab.IsAddressEditing = true;
            FocusAddressBar();
            e.Handled = true;
        }
    }

    private void BreadcrumbScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Long paths overflow the bar; keep the current (last) folder in view instead of the drive root.
        if (e.ExtentWidthChange != 0 || e.ViewportWidthChange != 0)
            BreadcrumbScrollViewer.ScrollToRightEnd();
    }

    private void BreadcrumbSegment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string targetPath } && PaneVm is not null)
        {
            PaneVm.Activate();
            _ = PaneVm.NavigateActiveOrSpawnIfLockedAsync(targetPath);
        }
    }

    private void PinCurrentToFavorites_Click(object sender, RoutedEventArgs e)
    {
        PaneVm?.Activate();
        MainVm?.AddCurrentFolderToFavorites();
    }

    private void AddressTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (PaneVm?.ActiveTab is not { } tab)
            return;

        if (e.Key == Key.Enter)
        {
            PaneVm.Activate();
            _ = MainVm?.NavigateToPathAsync(tab.AddressInputText);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            tab.AddressInputText = tab.CurrentPath;
            tab.IsAddressEditing = false;
            GetActiveListView().Focus();
            e.Handled = true;
        }
    }

    private void AddressTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (PaneVm?.ActiveTab is { } tab)
        {
            tab.AddressInputText = tab.CurrentPath;
            tab.IsAddressEditing = false;
        }
    }

    private void ClearFilter_Click(object sender, RoutedEventArgs e)
    {
        if (PaneVm?.ActiveTab is { } tab)
            tab.QuickFilterText = string.Empty;
    }

    // ─── Unavailable folder banner ────────────────────────────────────────────

    private void UnavailableRetry_Click(object sender, RoutedEventArgs e)
    {
        PaneVm?.Activate();
        if (PaneVm?.ActiveTab is { } tab)
            _ = tab.RefreshAsync(preserveSelection: true);
    }

    private async void UnavailableOpenParent_Click(object sender, RoutedEventArgs e)
    {
        if (PaneVm is not { ActiveTab: { } tab } pane)
            return;

        pane.Activate();
        string? target = await tab.FindNearestExistingFolderAsync();
        // In place, so Back returns to the unavailable folder; a locked tab opens it in a new tab instead.
        if (target is not null && ReferenceEquals(pane.ActiveTab, tab))
            await pane.NavigateActiveOrSpawnIfLockedAsync(target);
    }

    // ─── Column Sorting ────────────────────────────────────────────────────────

    private void SortHeader_Name_Click(object sender, RoutedEventArgs e) => PaneVm?.ActiveTab?.ToggleSort(SortColumn.Name);
    private void SortHeader_Ext_Click(object sender, RoutedEventArgs e) => PaneVm?.ActiveTab?.ToggleSort(SortColumn.Extension);
    private void SortHeader_Size_Click(object sender, RoutedEventArgs e) => PaneVm?.ActiveTab?.ToggleSort(SortColumn.Size);
    private void SortHeader_Type_Click(object sender, RoutedEventArgs e) => PaneVm?.ActiveTab?.ToggleSort(SortColumn.Type);
    private void SortHeader_Modified_Click(object sender, RoutedEventArgs e) => PaneVm?.ActiveTab?.ToggleSort(SortColumn.Modified);
    private void SortHeader_Created_Click(object sender, RoutedEventArgs e) => PaneVm?.ActiveTab?.ToggleSort(SortColumn.Created);

    // ─── File List Selection, Mouse & Drag-and-Drop ────────────────────────────

    private void DetailsListView_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        // Prevent wide GridView rows from snapping the horizontal ScrollBar to the right edge on click/focus
        if (!e.TargetRect.IsEmpty || e.OriginalSource is not DependencyObject src)
            return;

        var item = src as ListViewItem ?? FindAncestor<ListViewItem>(src);
        if (item is null)
            return;

        e.Handled = true;

        var sv = FindDescendant<ScrollViewer>(DetailsListView);
        if (sv is null)
            return;

        double savedHorizontalOffset = sv.HorizontalOffset;
        double safeX = Math.Clamp(savedHorizontalOffset, 0, Math.Max(0, item.ActualWidth - 2.0));
        double safeHeight = Math.Max(1.0, item.ActualHeight);

        item.BringIntoView(new Rect(safeX, 0, 1.0, safeHeight));

        if (Math.Abs(sv.HorizontalOffset - savedHorizontalOffset) > 0.5)
        {
            sv.ScrollToHorizontalOffset(savedHorizontalOffset);
        }
    }

    private void FileListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingSelection || sender is not ListView lv || PaneVm?.ActiveTab is null || lv.Visibility != Visibility.Visible)
            return;

        var selected = new List<FileEntryItem>(lv.SelectedItems.Count);
        foreach (object? obj in lv.SelectedItems)
        {
            if (obj is FileEntryItem item)
                selected.Add(item);
        }

        PaneVm.ActiveTab.UpdateSelection(selected);
        if (PaneVm.IsActivePane)
            MainVm?.NotifyActiveSelectionChanged();
    }

    private void FileListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        if (FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject) is not null)
        {
            PaneVm?.Activate();
            _ = MainVm?.OpenSelectedAsync();
            e.Handled = true;
        }
    }

    private void FileListView_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListView lv)
            return;

        var source = e.OriginalSource as DependencyObject;
        var clickedItem = FindAncestor<ListViewItem>(source);
        bool armed = e.ChangedButton == MouseButton.Left
            ? ArmMarquee(lv, e.GetPosition(lv), source, Keyboard.Modifiers)
            : _isMarqueeArmed = false;
        _fileDragStartPoint = e.GetPosition(lv);
        _isFileMouseDownOnItem = clickedItem is not null && !armed;

        if (e.ChangedButton == MouseButton.Middle &&
            clickedItem?.DataContext is FileEntryItem { IsDirectory: true } folder)
        {
            _ = PaneVm?.NewTabAsync(folder.FullPath);
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Left &&
            clickedItem is null &&
            !IsOnListChrome(source) &&
            (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0)
        {
            lv.SelectedItems.Clear();
        }
    }

    // ─── Rectangle (Marquee) Selection ─────────────────────────────────────────

    /// <summary>
    /// Arms a selection rectangle for a left press that is not meant to drag files: on empty space in any view, or
    /// in Details on any column but Name (dragging the Name cell still moves the files), like Explorer.
    /// </summary>
    private bool ArmMarquee(ListView lv, Point position, DependencyObject? source, ModifierKeys modifiers)
    {
        _isMarqueeArmed = false;
        if (IsOnListChrome(source))
            return false;

        if (FindAncestor<ListViewItem>(source) is not null &&
            (!ReferenceEquals(lv, DetailsListView) || IsInNameCell(source)))
        {
            return false;
        }

        _isMarqueeArmed = true;
        _marqueeStartPoint = position;
        _marqueeModifiers = modifiers;
        // Snapshot before the row under the pointer reacts to the press, so Ctrl toggles against the old selection.
        _marqueeBaseSelection = lv.SelectedItems.OfType<FileEntryItem>().ToList();
        return true;
    }

    /// <summary>Starts the rectangle once the pointer has moved far enough, then follows it. True while it is active.</summary>
    private bool TrackMarquee(ListView lv, Point position, bool leftButtonPressed)
    {
        if (_marquee is not null)
        {
            if (leftButtonPressed)
                _marquee.Update(position);
            else
                FinishMarquee(lv);
            return true;
        }

        if (!_isMarqueeArmed)
            return false;

        if (!leftButtonPressed)
        {
            _isMarqueeArmed = false;
            return false;
        }

        Vector moved = position - _marqueeStartPoint;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return false;
        }

        _isMarqueeArmed = false;
        // Capture before the marquee exists: re-capturing can raise LostMouseCapture, which ends an active marquee.
        lv.CaptureMouse();
        _marquee = MarqueeSelector.Begin(
            lv,
            _marqueeStartPoint,
            byRows: ReferenceEquals(lv, DetailsListView),
            _marqueeModifiers,
            _marqueeBaseSelection,
            items => ApplyMarqueeSelection(lv, items));

        if (_marquee is null)
        {
            lv.ReleaseMouseCapture();
            return false;
        }

        _marquee.Update(position);
        return true;
    }

    private bool FinishMarquee(ListView lv)
    {
        _isMarqueeArmed = false;
        if (_marquee is null)
            return false;

        MarqueeSelector marquee = _marquee;
        _marquee = null;
        marquee.End();
        if (lv.IsMouseCaptured)
            lv.ReleaseMouseCapture();

        if (PaneVm?.IsActivePane == true)
            MainVm?.NotifyActiveSelectionChanged();
        return true;
    }

    /// <summary>Shows <paramref name="items"/> as the list's selection and pushes it to the tab in one step.</summary>
    private void ApplyMarqueeSelection(ListView lv, List<FileEntryItem> items)
    {
        if (PaneVm?.ActiveTab is not { } tab)
            return;

        var wanted = new HashSet<FileEntryItem>(items);
        _isSyncingSelection = true;
        try
        {
            for (int i = lv.SelectedItems.Count - 1; i >= 0; i--)
            {
                if (lv.SelectedItems[i] is not FileEntryItem item || !wanted.Contains(item))
                    lv.SelectedItems.RemoveAt(i);
            }

            var shown = lv.SelectedItems.OfType<FileEntryItem>().ToHashSet();
            foreach (FileEntryItem item in items)
            {
                if (!shown.Contains(item))
                    lv.SelectedItems.Add(item);
            }
        }
        finally
        {
            _isSyncingSelection = false;
        }

        tab.UpdateSelection(items);
    }

    private void FileListView_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListView lv && FinishMarquee(lv))
            e.Handled = true;
    }

    private void FileListView_LostMouseCapture(object sender, MouseEventArgs e)
    {
        // Capture taken away mid-drag (Alt+Tab, a dialog): keep what is selected so far.
        if (sender is ListView lv && ReferenceEquals(e.OriginalSource, lv))
            FinishMarquee(lv);
    }

    private static bool IsOnListChrome(DependencyObject? source) =>
        FindAncestor<ScrollBar>(source) is not null || FindAncestor<GridViewColumnHeader>(source) is not null;

    private static bool IsInNameCell(DependencyObject? source)
    {
        for (DependencyObject? current = source; current is not null and not ListViewItem; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { Name: "NameCell" })
                return true;
        }
        return false;
    }

    private void FileListView_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is ListView marqueeList && TrackMarquee(marqueeList, e.GetPosition(marqueeList), e.LeftButton == MouseButtonState.Pressed))
        {
            // Keeps the list's own drag-to-extend selection and the file drag below from running.
            e.Handled = true;
            return;
        }

        if (!_isFileMouseDownOnItem ||
            e.LeftButton != MouseButtonState.Pressed ||
            sender is not ListView lv ||
            PaneVm?.ActiveTab is null)
        {
            return;
        }

        if (FindAncestor<ScrollBar>(e.OriginalSource as DependencyObject) is not null ||
            FindAncestor<GridViewColumnHeader>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        Vector diff = e.GetPosition(lv) - _fileDragStartPoint;
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance * 2 &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance * 2)
        {
            return;
        }

        var selectedPaths = PaneVm.ActiveTab.SelectedItems.Select(i => i.FullPath).ToArray();
        if (selectedPaths.Length == 0)
            return;

        _isFileMouseDownOnItem = false;
        var data = new DataObject(DataFormats.FileDrop, selectedPaths);
        DragDrop.DoDragDrop(lv, data, DragDropEffects.Copy | DragDropEffects.Move);
    }

    private void PaneRoot_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(TabDragFormat))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        if (!e.Data.GetDataPresent(DataFormats.FileDrop) ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] files ||
            files.Length == 0 ||
            PaneVm?.ActiveTab is not { IsSearchTab: false } tab ||
            string.IsNullOrWhiteSpace(tab.CurrentPath))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        string destDir = ResolveDropDestinationDirectory(e.OriginalSource as DependencyObject, tab.CurrentPath);
        bool isMove = DetermineDropIsMove(e.KeyStates, files[0], destDir);
        e.Effects = isMove ? DragDropEffects.Move : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void PaneRoot_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] files ||
            files.Length == 0 ||
            PaneVm?.ActiveTab is not { IsSearchTab: false } tab ||
            string.IsNullOrWhiteSpace(tab.CurrentPath) ||
            MainVm is null)
        {
            return;
        }

        string destDir = ResolveDropDestinationDirectory(e.OriginalSource as DependencyObject, tab.CurrentPath);

        // Ignore dropping onto the exact same folder when moving
        bool isMove = DetermineDropIsMove(e.KeyStates, files[0], destDir);
        if (isMove && files.All(f => PathValidator.PathsEqual(Path.GetDirectoryName(f), destDir)))
            return;

        _ = MainVm.EnqueueOrRunTransferAsync(files, destDir, isMove);
        e.Handled = true;
    }

    private static string ResolveDropDestinationDirectory(DependencyObject? hitTarget, string fallbackPaneDir)
    {
        var row = FindAncestor<ListViewItem>(hitTarget);
        if (row?.DataContext is FileEntryItem { IsDirectory: true } folderEntry)
            return folderEntry.FullPath;

        return fallbackPaneDir;
    }

    private static bool DetermineDropIsMove(DragDropKeyStates keyStates, string firstSourcePath, string destinationDir)
    {
        if ((keyStates & DragDropKeyStates.ControlKey) != 0)
            return false; // Explicit Copy

        if ((keyStates & DragDropKeyStates.ShiftKey) != 0)
            return true;  // Explicit Move

        // Windows Explorer standard: same volume defaults to Move, different volume defaults to Copy
        string? srcRoot = Path.GetPathRoot(firstSourcePath);
        string? dstRoot = Path.GetPathRoot(destinationDir);
        return !string.IsNullOrEmpty(srcRoot) &&
               string.Equals(srcRoot, dstRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
                return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null)
            return null;

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
}
