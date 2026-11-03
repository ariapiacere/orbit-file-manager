using OrbitFileManager.Models;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OrbitFileManager.Services;

public sealed class ThemeService : IThemeService
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private ResourceDictionary? _activeThemeDictionary;

    public AppThemeMode CurrentTheme { get; private set; } = AppThemeMode.Dark;

    public void ApplyTheme(AppThemeMode mode)
    {
        CurrentTheme = mode;

        if (Application.Current is null)
            return;

        // Assembly-qualified so the swap also resolves when another entry assembly hosts the app (UI test harness).
        string uriPath = mode == AppThemeMode.Light
            ? "pack://application:,,,/OrbitFileManager;component/Themes/LightTheme.xaml"
            : "pack://application:,,,/OrbitFileManager;component/Themes/DarkTheme.xaml";

        var nextDict = new ResourceDictionary
        {
            Source = new Uri(uriPath, UriKind.Absolute)
        };

        var merged = Application.Current.Resources.MergedDictionaries;
        if (_activeThemeDictionary is not null)
        {
            int idx = merged.IndexOf(_activeThemeDictionary);
            if (idx >= 0)
                merged[idx] = nextDict;
            else
                merged.Insert(0, nextDict);
        }
        else
        {
            // Replace initial theme dictionary at index 0 if present
            if (merged.Count > 0 && merged[0].Source?.OriginalString.Contains("Theme.xaml", StringComparison.OrdinalIgnoreCase) == true)
                merged[0] = nextDict;
            else
                merged.Insert(0, nextDict);
        }

        _activeThemeDictionary = nextDict;

        foreach (Window window in Application.Current.Windows)
            ApplyWindowTitleBarTheme(window, mode == AppThemeMode.Dark);
    }

    public static void ApplyWindowTitleBarTheme(Window window, bool useDarkMode)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            int flag = useDarkMode ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref flag, sizeof(int));
        }
        catch
        {
            // Non-critical on older Windows builds
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}
