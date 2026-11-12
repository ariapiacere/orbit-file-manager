using OrbitFileManager.Controls;
using OrbitFileManager.Models;
using OrbitFileManager.Services;
using OrbitFileManager.Services.Settings;
using OrbitFileManager.ViewModels;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace OrbitFileManager;

public partial class MainWindow : Window
{
    public static readonly DependencyProperty ShowToolbarLabelsProperty = DependencyProperty.Register(
        nameof(ShowToolbarLabels),
        typeof(bool),
        typeof(MainWindow),
        new PropertyMetadata(true));

    /// <summary>
    /// The "Show button text labels" setting, except that labels are dropped while the window is too
    /// narrow for the full toolbar (e.g. dual-pane controls at the default width).
    /// </summary>
    public bool ShowToolbarLabels
    {
        get => (bool)GetValue(ShowToolbarLabelsProperty);
        set => SetValue(ShowToolbarLabelsProperty, value);
    }

    private readonly MainViewModel _viewModel;
    private readonly SettingsService _settingsService;
    private readonly ShortcutService _shortcutService;
    private readonly Dictionary<TextBlock, double> _toolbarLabelWidths = [];
    private double _savedSidebarWidth = 260;
    private double _savedPreviewWidth = 310;
    private const double PaneSplitterThickness = 8;

    public MainWindow(MainViewModel viewModel, SettingsService settingsService, ShortcutService shortcutService)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settingsService = settingsService;
        _shortcutService = shortcutService;
        DataContext = _viewModel;

        ApplyInitialWindowPlacement(_settingsService.Current);

        SourceInitialized += (_, _) =>
            ThemeService.ApplyWindowTitleBarTheme(this, _settingsService.Current.Theme == AppThemeMode.Dark);

        _viewModel.RequestSelectAllInActiveList += () => GetActivePaneControl().SelectAllItems();
        _viewModel.RequestFocusAddressBar += () => GetActivePaneControl().FocusAddressBar();
        _viewModel.RequestFocusQuickFilter += () => GetActivePaneControl().FocusQuickFilter();
        _viewModel.RequestCloseForUpdate += Close;
        _viewModel.PropertyChanged += OnMainViewModelPropertyChanged;

        ToolbarDock.SizeChanged += (_, _) => UpdateToolbarLabelFit();
        ToolbarRightGroup.SizeChanged += (_, _) => UpdateToolbarLabelFit();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
        SyncColumnVisibility();
        SyncDualPaneLayout();
        ScheduleToolbarLabelFit();
    }

    private void ScheduleToolbarLabelFit() =>
        Dispatcher.BeginInvoke(UpdateToolbarLabelFit, System.Windows.Threading.DispatcherPriority.Loaded);

    /// <summary>Hides the toolbar text labels when the left button groups would otherwise be clipped.</summary>
    private void UpdateToolbarLabelFit()
    {
        if (!IsLoaded)
            return;

        if (!_viewModel.ShowToolbarTextLabels)
        {
            ShowToolbarLabels = false;
            return;
        }

        // Width the left side needs: StackPanel children are measured unconstrained, so their sizes are real.
        double needed = 0;
        foreach (UIElement child in ToolbarDock.Children)
        {
            if (ReferenceEquals(child, ToolbarRightGroup))
                continue;

            needed += ReferenceEquals(child, ToolbarActionsPanel)
                ? ToolbarActionsPanel.Children.Cast<UIElement>().Sum(c => c.DesiredSize.Width)
                : child.DesiredSize.Width;
        }

        // While labels are hidden, add back the widths they had so we know when they fit again.
        foreach (TextBlock label in new[] { ToolbarLabelNewFolder, ToolbarLabelBatchRename, ToolbarLabelSearch, ToolbarLabelDualPane, ToolbarLabelTools })
        {
            if (label.IsVisible)
                _toolbarLabelWidths[label] = label.DesiredSize.Width;
            else if (!ShowToolbarLabels &&
                     label.Parent is FrameworkElement { Parent: UIElement { Visibility: Visibility.Visible } } &&
                     _toolbarLabelWidths.TryGetValue(label, out double width))
                needed += width;
        }

        double available = ToolbarDock.ActualWidth - ToolbarRightGroup.ActualWidth;
        ShowToolbarLabels = needed <= available;
    }

    private BrowserPaneControl GetActivePaneControl() =>
        ReferenceEquals(_viewModel.ActivePane, _viewModel.RightPane) ? RightPaneControl : LeftPaneControl;

    private void ApplyInitialWindowPlacement(AppSettings settings)
    {
        Width = settings.WindowWidth;
        Height = settings.WindowHeight;
        _savedSidebarWidth = settings.SidebarWidth;
        _savedPreviewWidth = settings.PreviewPanelWidth;

        if (settings.WindowLeft.HasValue && settings.WindowTop.HasValue)
        {
            double vLeft = SystemParameters.VirtualScreenLeft;
            double vTop = SystemParameters.VirtualScreenTop;
            double vWidth = SystemParameters.VirtualScreenWidth;
            double vHeight = SystemParameters.VirtualScreenHeight;

            if (settings.WindowLeft.Value >= vLeft - 50 &&
                settings.WindowLeft.Value < vLeft + vWidth - 100 &&
                settings.WindowTop.Value >= vTop - 50 &&
                settings.WindowTop.Value < vTop + vHeight - 100)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = settings.WindowLeft.Value;
                Top = settings.WindowTop.Value;
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (settings.IsWindowMaximized)
            WindowState = WindowState.Maximized;
    }

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.ShowSidebar):
            case nameof(MainViewModel.ShowPreviewPanel):
                SyncColumnVisibility();
                break;

            case nameof(MainViewModel.IsDualPane):
            case nameof(MainViewModel.SplitOrientation):
                SyncDualPaneLayout();
                ScheduleToolbarLabelFit();
                break;

            case nameof(MainViewModel.VerticalSplitRatio):
            case nameof(MainViewModel.HorizontalSplitRatio):
                SyncDualPaneLayout();
                break;

            case nameof(MainViewModel.ShowToolbarTextLabels):
            case nameof(MainViewModel.ShowToolbarBatchRename):
            case nameof(MainViewModel.ShowToolbarSearch):
            case nameof(MainViewModel.ShowToolbarDualPane):
            case nameof(MainViewModel.ShowToolbarToolsMenu):
            case nameof(MainViewModel.CanUndoBatchRename):
                ScheduleToolbarLabelFit();
                break;
        }
    }

    private void SyncColumnVisibility()
    {
        if (_viewModel.ShowSidebar)
        {
            SidebarColumn.MinWidth = 170;
            SidebarColumn.Width = new GridLength(Math.Clamp(_savedSidebarWidth, 170, 520));
            SidebarSplitterColumn.Width = new GridLength(5);
        }
        else
        {
            if (SidebarColumn.ActualWidth >= 170)
                _savedSidebarWidth = SidebarColumn.ActualWidth;
            SidebarColumn.MinWidth = 0;
            SidebarColumn.Width = new GridLength(0);
            SidebarSplitterColumn.Width = new GridLength(0);
        }

        if (_viewModel.ShowPreviewPanel)
        {
            PreviewColumn.MinWidth = 220;
            PreviewColumn.Width = new GridLength(Math.Clamp(_savedPreviewWidth, 220, 600));
            PreviewSplitterColumn.Width = new GridLength(5);
        }
        else
        {
            if (PreviewColumn.ActualWidth >= 220)
                _savedPreviewWidth = PreviewColumn.ActualWidth;
            PreviewColumn.MinWidth = 0;
            PreviewColumn.Width = new GridLength(0);
            PreviewSplitterColumn.Width = new GridLength(0);
        }
    }

    private void SyncDualPaneLayout()
    {
        if (!_viewModel.IsDualPane)
        {
            LeftPaneCol.Width = new GridLength(1, GridUnitType.Star);
            VerticalPaneSplitterCol.Width = new GridLength(0);
            RightPaneCol.MinWidth = 0;
            RightPaneCol.Width = new GridLength(0);

            TopPaneRow.Height = new GridLength(1, GridUnitType.Star);
            HorizontalPaneSplitterRow.Height = new GridLength(0);
            BottomPaneRow.MinHeight = 0;
            BottomPaneRow.Height = new GridLength(0);

            VerticalPaneSplitter.Visibility = Visibility.Collapsed;
            HorizontalPaneSplitter.Visibility = Visibility.Collapsed;
            RightPaneControl.Visibility = Visibility.Collapsed;
            return;
        }

        RightPaneControl.Visibility = Visibility.Visible;

        if (_viewModel.SplitOrientation == SplitOrientationMode.Vertical)
        {
            // Left | Right
            TopPaneRow.Height = new GridLength(1, GridUnitType.Star);
            HorizontalPaneSplitterRow.Height = new GridLength(0);
            BottomPaneRow.MinHeight = 0;
            BottomPaneRow.Height = new GridLength(0);

            double share = _viewModel.VerticalSplitRatio;
            LeftPaneCol.Width = new GridLength(share, GridUnitType.Star);
            VerticalPaneSplitterCol.Width = new GridLength(PaneSplitterThickness);
            RightPaneCol.MinWidth = 220;
            RightPaneCol.Width = new GridLength(1 - share, GridUnitType.Star);

            Grid.SetRow(RightPaneControl, 0);
            Grid.SetColumn(RightPaneControl, 2);

            HorizontalPaneSplitter.Visibility = Visibility.Collapsed;
            VerticalPaneSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            // Top / Bottom
            LeftPaneCol.Width = new GridLength(1, GridUnitType.Star);
            VerticalPaneSplitterCol.Width = new GridLength(0);
            RightPaneCol.MinWidth = 0;
            RightPaneCol.Width = new GridLength(0);

            double share = _viewModel.HorizontalSplitRatio;
            TopPaneRow.Height = new GridLength(share, GridUnitType.Star);
            HorizontalPaneSplitterRow.Height = new GridLength(PaneSplitterThickness);
            BottomPaneRow.MinHeight = 160;
            BottomPaneRow.Height = new GridLength(1 - share, GridUnitType.Star);

            Grid.SetRow(RightPaneControl, 2);
            Grid.SetColumn(RightPaneControl, 0);

            VerticalPaneSplitter.Visibility = Visibility.Collapsed;
            HorizontalPaneSplitter.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Remembers where the user left the pane splitter (saved with the session).</summary>
    private void PaneSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        // The splitter has only changed the definitions so far; measure them before reading sizes.
        PanesHostGrid.UpdateLayout();

        if (ReferenceEquals(sender, VerticalPaneSplitter))
        {
            double total = LeftPaneCol.ActualWidth + RightPaneCol.ActualWidth;
            if (total > 0)
                _viewModel.VerticalSplitRatio = LeftPaneCol.ActualWidth / total;
        }
        else
        {
            double total = TopPaneRow.ActualHeight + BottomPaneRow.ActualHeight;
            if (total > 0)
                _viewModel.HorizontalSplitRatio = TopPaneRow.ActualHeight / total;
        }
    }

    private void PaneSplitter_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(sender, VerticalPaneSplitter))
            _viewModel.VerticalSplitRatio = 0.5;
        else
            _viewModel.HorizontalSplitRatio = 0.5;

        // The ratio may already be 0.5 while the panes are clamped by their minimum sizes.
        SyncDualPaneLayout();
        e.Handled = true;
    }

    private void FavoritesSection_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] paths &&
            paths.Any(Directory.Exists))
        {
            e.Effects = DragDropEffects.Link;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void FavoritesSection_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            _viewModel.DropPathsIntoFavorites(paths);
            e.Handled = true;
        }
    }

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderTreeNodeViewModel node && !string.IsNullOrWhiteSpace(node.FullPath))
        {
            _ = _viewModel.NavigateToPathAsync(node.FullPath);
        }
    }

    private void SidebarTreeView_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!e.Handled)
        {
            e.Handled = true;
            var forwarded = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = sender
            };
            SidebarScrollViewer.RaiseEvent(forwarded);
        }
    }

    private void ToolsMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (ToolsMenuButton.ContextMenu is { } menu)
        {
            menu.PlacementTarget = ToolsMenuButton;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        ModifierKeys mods = Keyboard.Modifiers;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool inTextBox = Keyboard.FocusedElement is TextBoxBase;

        // 1. Evaluate user-customizable shortcuts first (Data/shortcuts.json)
        bool isTypingKeyInTextBox = inTextBox &&
                                    (mods == ModifierKeys.None || mods == ModifierKeys.Shift) &&
                                    (key < Key.F1 || key > Key.F24);
        if (!isTypingKeyInTextBox)
        {
            OrbitShortcutAction? customAction = _shortcutService.MatchShortcut(mods, key);
            if (customAction.HasValue)
            {
                bool handled = await ExecuteShortcutActionAsync(customAction.Value);
                if (handled)
                {
                    e.Handled = true;
                    return;
                }
            }
        }

        // 2. Built-in navigation & editing shortcuts
        if (mods == ModifierKeys.Control)
        {
            switch (key)
            {
                case Key.Tab:
                    _viewModel.NextTab();
                    e.Handled = true;
                    return;
                case Key.L:
                    _viewModel.BeginAddressEdit();
                    e.Handled = true;
                    return;
                case Key.N:
                    await _viewModel.CreateNewFileAsync();
                    e.Handled = true;
                    return;
            }
        }
        else if (mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            switch (key)
            {
                case Key.Tab:
                    _viewModel.PreviousTab();
                    e.Handled = true;
                    return;
                case Key.N:
                    await _viewModel.CreateNewFolderAsync();
                    e.Handled = true;
                    return;
                case Key.C:
                    _viewModel.CopyFullPath();
                    e.Handled = true;
                    return;
            }
        }
        else if (mods == ModifierKeys.Alt)
        {
            switch (key)
            {
                case Key.Left:
                    await _viewModel.NavigateBackAsync();
                    e.Handled = true;
                    return;
                case Key.Right:
                    await _viewModel.NavigateForwardAsync();
                    e.Handled = true;
                    return;
                case Key.Up:
                    await _viewModel.NavigateUpAsync();
                    e.Handled = true;
                    return;
                case Key.D:
                    _viewModel.BeginAddressEdit();
                    e.Handled = true;
                    return;
                case Key.Enter:
                    _viewModel.ShowProperties();
                    e.Handled = true;
                    return;
            }
        }

        if (mods == ModifierKeys.None && key == Key.F5)
        {
            await _viewModel.RefreshAsync();
            e.Handled = true;
            return;
        }

        if (inTextBox)
            return;

        if (mods == ModifierKeys.Control)
        {
            switch (key)
            {
                case Key.C:
                    _viewModel.CopySelected();
                    e.Handled = true;
                    return;
                case Key.X:
                    _viewModel.CutSelected();
                    e.Handled = true;
                    return;
                case Key.V:
                    await _viewModel.PasteAsync();
                    e.Handled = true;
                    return;
                case Key.A:
                    _viewModel.SelectAll();
                    e.Handled = true;
                    return;
                case Key.Z:
                    if (_viewModel.CanUndoBatchRename)
                    {
                        await _viewModel.UndoLastBatchRenameAsync();
                        e.Handled = true;
                        return;
                    }
                    break;
            }
        }
        else if (mods == ModifierKeys.Shift && key == Key.Delete)
        {
            await _viewModel.DeletePermanentlyAsync();
            e.Handled = true;
        }
        else if (mods == ModifierKeys.None)
        {
            switch (key)
            {
                case Key.F2:
                    await _viewModel.RenameSelectedAsync();
                    e.Handled = true;
                    return;
                case Key.Delete:
                    await _viewModel.DeleteToRecycleBinAsync();
                    e.Handled = true;
                    return;
                case Key.Enter:
                    await _viewModel.OpenSelectedAsync();
                    e.Handled = true;
                    return;
                case Key.Back:
                    await _viewModel.NavigateUpAsync();
                    e.Handled = true;
                    return;
            }
        }
    }

    private async Task<bool> ExecuteShortcutActionAsync(OrbitShortcutAction action)
    {
        switch (action)
        {
            case OrbitShortcutAction.NewTab:
                await _viewModel.NewTabAsync();
                return true;
            case OrbitShortcutAction.CloseTab:
                _viewModel.CloseTab();
                return true;
            case OrbitShortcutAction.RestoreClosedTab:
                await _viewModel.RestoreClosedTabAsync();
                return true;
            case OrbitShortcutAction.ToggleDualPane:
                await _viewModel.ToggleDualPaneAsync();
                return true;
            case OrbitShortcutAction.SwitchActivePane:
                _viewModel.SwitchActivePane();
                GetActivePaneControl().GetActiveListView().Focus();
                return true;
            case OrbitShortcutAction.BatchRename:
                await _viewModel.OpenBatchRenameAsync();
                return true;
            case OrbitShortcutAction.AdvancedSearch:
                await _viewModel.OpenAdvancedSearchAsync();
                return true;
            case OrbitShortcutAction.QuickFilter:
                _viewModel.FocusQuickFilter();
                return true;
            case OrbitShortcutAction.ToggleHiddenFiles:
                await _viewModel.ToggleHiddenFilesAsync();
                return true;
            case OrbitShortcutAction.TogglePreviewPanel:
                _viewModel.TogglePreviewPanel();
                return true;
            case OrbitShortcutAction.ToggleSidebar:
                _viewModel.ToggleSidebar();
                return true;
            case OrbitShortcutAction.DuplicateFinder:
                await _viewModel.OpenDuplicateFinderAsync();
                return true;
            case OrbitShortcutAction.FolderSizeAnalyzer:
                await _viewModel.OpenFolderSizeAnalyzerAsync();
                return true;
            case OrbitShortcutAction.CalculateChecksums:
                _viewModel.OpenChecksumTool();
                return true;
            default:
                return false;
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        SaveWindowAndSession();
        _viewModel.Dispose();
    }

    /// <summary>
    /// Saves window placement, panel widths and the open tabs. Called on close and when Windows signs out, restarts
    /// or shuts down: WPF does not raise Closing in that case.
    /// </summary>
    public void SaveWindowAndSession()
    {
        double sidebarW = _viewModel.ShowSidebar && SidebarColumn.ActualWidth >= 170
            ? SidebarColumn.ActualWidth
            : _savedSidebarWidth;
        double previewW = _viewModel.ShowPreviewPanel && PreviewColumn.ActualWidth >= 220
            ? PreviewColumn.ActualWidth
            : _savedPreviewWidth;

        _viewModel.SaveSessionAndSettings(
            Width,
            Height,
            Left,
            Top,
            WindowState == WindowState.Maximized,
            sidebarW,
            previewW);
    }
}
