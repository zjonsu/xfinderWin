using System.Windows;
using XFinder.Services;

namespace XFinder;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ThemeService.Initialize();   // 저장된 화면 모드 적용 + 시스템 테마 변경 구독
        var window = new Views.MainWindow();
        window.Show();
    }
}
