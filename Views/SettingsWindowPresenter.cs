// mac 대응: Sources/XFinder/Views/SettingsWindow.swift SettingsWindowPresenter — 설정 창 싱글턴.
using System.Windows;
using XFinder.Models;

namespace XFinder.Views;

/// <summary>
/// 설정 단독 창 싱글턴 — 이미 열려 있으면 새로 만들지 않고 앞으로 가져와 활성화 (스펙 04 §10.1).
/// 모달 아님, Owner 없음. 닫히면 참조 해제 → 다음에 새로 생성.
/// </summary>
public static class SettingsWindowPresenter
{
    private static SettingsWindow? _window;

    public static void Show(AppModel model)
    {
        if (_window is not null)
        {
            if (_window.WindowState == WindowState.Minimized)
                _window.WindowState = WindowState.Normal;
            _window.Activate();
            return;
        }

        var win = new SettingsWindow(model);
        win.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, win)) _window = null;
        };
        _window = win;
        win.Show();
    }
}
