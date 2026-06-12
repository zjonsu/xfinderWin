using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace XFinder.Services;

public enum AppTheme { System, Light, Dark }

/// <summary>
/// 화면 모드(라이트/다크/시스템) 전환 — mac '화면 모드' 설정 대응.
/// 리소스 사전 교체 + Win11 DWM 다크 타이틀바/둥근 모서리 적용.
/// </summary>
public static class ThemeService
{
    public const string SettingsKey = "appearanceMode";

    public static AppTheme Current { get; private set; } = AppTheme.System;

    public static event Action? ThemeChanged;

    public static void Initialize()
    {
        var saved = SettingsStore.Get<string>(SettingsKey, "system");
        Current = saved switch { "light" => AppTheme.Light, "dark" => AppTheme.Dark, _ => AppTheme.System };
        Apply(Current, save: false);
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && Current == AppTheme.System)
                Application.Current?.Dispatcher.BeginInvoke(() => Apply(AppTheme.System, save: false));
        };
    }

    public static bool IsDarkEffective =>
        Current == AppTheme.Dark || (Current == AppTheme.System && SystemUsesDark());

    private static bool SystemUsesDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    public static void Apply(AppTheme theme, bool save = true)
    {
        Current = theme;
        if (save)
            SettingsStore.Set(SettingsKey, theme switch
            {
                AppTheme.Light => "light", AppTheme.Dark => "dark", _ => "system",
            });

        var dark = IsDarkEffective;
        var dict = new ResourceDictionary
        {
            Source = new Uri($"Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative),
        };
        var app = Application.Current;
        if (app is null) return;
        var merged = app.Resources.MergedDictionaries;
        // 0번 = 색상 테마, 1번 = 공용 스타일 (App.xaml 구조와 일치해야 함)
        if (merged.Count > 0) merged[0] = dict; else merged.Add(dict);

        foreach (Window w in app.Windows)
            ApplyChrome(w);
        ThemeChanged?.Invoke();
    }

    // ── Win11 DWM: 다크 타이틀바 + 둥근 모서리 ──────────────────────────

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    /// <summary>창 핸들에 다크 모드·둥근 모서리 적용. 창 SourceInitialized 이후 호출.</summary>
    public static void ApplyChrome(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = IsDarkEffective ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        int corner = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }
}
