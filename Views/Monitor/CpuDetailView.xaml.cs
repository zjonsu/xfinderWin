// mac 소스 대응: Sources/XFinder/Views/CPUDetailView.swift — CPU 팝업 (그래프/온도·가동시간 카드/상위 프로세스)
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using XFinder.Services;

namespace XFinder.Views.Monitor;

/// <summary>모니터 팝업 3종 공용 도우미 — 색 팔레트/도넛 호/프로세스 행/링크 버튼.</summary>
internal static class MonitorUi
{
    // mac 시스템 색 근사 (다크 팔레트 기준 — 의미색이라 테마 키 아님)
    public static readonly Color Blue = Color.FromRgb(0x0A, 0x84, 0xFF);
    public static readonly Color Red = Color.FromRgb(0xFF, 0x45, 0x3A);
    public static readonly Color Purple = Color.FromRgb(0xBF, 0x5A, 0xF2);
    public static readonly Color Indigo = Color.FromRgb(0x5E, 0x5C, 0xE6);
    public static readonly Color Orange = Color.FromRgb(0xFF, 0x9F, 0x0A);
    public static readonly Color Pink = Color.FromRgb(0xFF, 0x2D, 0x55);
    public static readonly Color Teal = Color.FromRgb(0x59, 0xAD, 0xC4);
    public static readonly Color Green = Color.FromRgb(0x32, 0xD7, 0x4B);
    public static readonly Color Gray = Color.FromRgb(0x98, 0x98, 0x9D);

    /// <summary>고정(Frozen) 브러시 — opacity &lt; 1 이면 알파를 새로 부여.</summary>
    public static SolidColorBrush Brush(Color c, double opacity = 1.0)
    {
        var color = opacity >= 1.0 ? c : Color.FromArgb((byte)Math.Clamp(opacity * 255, 0, 255), c.R, c.G, c.B);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // ── 도넛 (시작 12시 방향, 시계 방향, lineCap butt) ────────────────────

    /// <summary>도넛 배경 트랙 — size×size 캔버스에 두께 thickness의 전체 원.</summary>
    public static Ellipse DonutRing(double size, double thickness, Brush stroke)
    {
        var ring = new Ellipse
        {
            Width = size - thickness,
            Height = size - thickness,
            Stroke = stroke,
            StrokeThickness = thickness,
        };
        Canvas.SetLeft(ring, thickness / 2);
        Canvas.SetTop(ring, thickness / 2);
        return ring;
    }

    /// <summary>도넛 세그먼트 — f0..f1 (0..1 분수, 12시 시작 시계 방향).</summary>
    public static Shape DonutSegment(double size, double thickness, double f0, double f1, Brush stroke)
    {
        f0 = Math.Clamp(f0, 0, 1);
        f1 = Math.Clamp(f1, 0, 1);
        if (f1 - f0 >= 0.9995) return DonutRing(size, thickness, stroke);

        double r = (size - thickness) / 2.0;
        double cx = size / 2.0, cy = size / 2.0;
        double a0 = (f0 * 360.0 - 90.0) * Math.PI / 180.0;
        double a1 = (f1 * 360.0 - 90.0) * Math.PI / 180.0;
        var start = new Point(cx + r * Math.Cos(a0), cy + r * Math.Sin(a0));
        var end = new Point(cx + r * Math.Cos(a1), cy + r * Math.Sin(a1));

        var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0,
            isLargeArc: f1 - f0 > 0.5, SweepDirection.Clockwise, isStroked: true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();

        return new Path
        {
            Data = geometry,
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Flat,
            StrokeEndLineCap = PenLineCap.Flat,
        };
    }

    // ── 프로세스 행 (스펙 §7.6 공통) ──────────────────────────────────────

    private static readonly ConcurrentDictionary<string, ImageSource?> ProcIcons = new();

    /// <summary>프로세스 아이콘 — 실행 파일 연관 아이콘(경로→ImageSource 캐시). 접근 불가 시 null.</summary>
    public static ImageSource? ProcessIcon(int pid)
    {
        var path = SystemMonitor.TryGetProcessPath(pid);
        if (string.IsNullOrEmpty(path)) return null;
        return ProcIcons.GetOrAdd(path, p =>
        {
            try { return ShellInterop.GetIcon(p, isDirectory: false, large: false); }
            catch { return null; }
        });
    }

    /// <summary>행: [아이콘 18×18 또는 폴백 글리프][이름 13pt 한 줄][값][종료 버튼].</summary>
    public static FrameworkElement ProcessRow(FrameworkElement owner, string name, ImageSource? icon,
        string valueText, Brush valueBrush, Action onQuit)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (icon is not null)
        {
            var image = new Image { Source = icon, Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(image);
        }
        else
        {
            var fallback = new TextBlock
            {
                Text = "",   // Segoe AppIconDefault — mac app.dashed 대응
                FontFamily = (FontFamily)owner.FindResource("IconFontFamily"),
                FontSize = 13,
                Foreground = Brush(Gray),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            grid.Children.Add(fallback);
        }

        var nameText = new TextBlock
        {
            Text = name,
            FontSize = 13,
            Margin = new Thickness(8, 0, 6, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)owner.FindResource("TextPrimaryBrush"),
        };
        Grid.SetColumn(nameText, 1);
        grid.Children.Add(nameText);

        var value = new TextBlock
        {
            Text = valueText,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = valueBrush,
        };
        Grid.SetColumn(value, 2);
        grid.Children.Add(value);

        var quit = LinkButton("종료", 11, onQuit);
        quit.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(quit, 3);
        grid.Children.Add(quit);

        return grid;
    }

    /// <summary>플레인 텍스트 버튼 (파란색 링크 모양).</summary>
    public static Button LinkButton(string text, double fontSize, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            FontSize = fontSize,
            Foreground = Brush(Blue),
            Cursor = Cursors.Hand,
            Focusable = false,
            Padding = new Thickness(2, 1, 2, 1),
            Template = PlainButtonTemplate(),
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>크롬 없는 플레인 버튼 템플릿 — ContentPresenter만.</summary>
    public static ControlTemplate PlainButtonTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        template.VisualTree = presenter;
        return template;
    }
}

/// <summary>
/// CPU 상세 팝업 — 사용률 그래프(user/system 2라인, 2분 추이), 가동시간/온도 카드,
/// 상위 프로세스 5개(2초 타이머 — 팝업 열릴 때만 동작, 델타 기반이라 첫 표시는 "측정 중…").
/// </summary>
public partial class CpuDetailView : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2.0) };
    private bool _open;
    private bool _reloading;

    private static SystemMonitor Mon => SystemMonitor.Instance;

    public CpuDetailView()
    {
        InitializeComponent();
        ChipName.Text = Mon.CpuName;
        CoreText.Text = $"코어 {Mon.CoreCount}개";
        _timer.Tick += (_, _) => { UpdateCards(); _ = ReloadProcessesAsync(); };
        ChartCanvas.SizeChanged += (_, _) => { if (_open) DrawChart(); };
    }

    // ── 라이프사이클 (SystemStatsView의 Popup Opened/Closed에서 호출) ────

    public void OnPopupOpened()
    {
        _open = true;
        Mon.PropertyChanged += OnMonitorChanged;
        DrawChart();
        UpdateReadouts();
        UpdateCards();
        ProcList.Children.Clear();
        _ = ReloadProcessesAsync();   // 즉시 1회 + 2초 타이머
        _timer.Start();
    }

    public void OnPopupClosed()
    {
        _open = false;
        _timer.Stop();
        Mon.PropertyChanged -= OnMonitorChanged;
    }

    private void OnMonitorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_open) return;
        switch (e.PropertyName)
        {
            case nameof(SystemMonitor.CpuHistory):
            case nameof(SystemMonitor.Cpu):
                if (Dispatcher.CheckAccess()) { DrawChart(); UpdateReadouts(); }
                else Dispatcher.InvokeAsync(() => { DrawChart(); UpdateReadouts(); });
                break;
            case nameof(SystemMonitor.CpuTemperature):
                if (Dispatcher.CheckAccess()) UpdateCards();
                else Dispatcher.InvokeAsync(UpdateCards);
                break;
        }
    }

    // ── 그래프 (스펙 §7.2 CPUChart) ──────────────────────────────────────

    private void DrawChart()
    {
        ChartCanvas.Children.Clear();
        double w = ChartCanvas.ActualWidth;
        double h = ChartCanvas.ActualHeight;
        if (w <= 0) w = 328;
        if (h <= 0) h = 120;

        // 격자: 8열 × 4행, secondary 18%, 두께 0.5
        var gridBrush = MonitorUi.Brush((Color)FindResource("TextSecondaryColor"), 0.18);
        for (int i = 1; i < 8; i++)
        {
            double x = w * i / 8.0;
            ChartCanvas.Children.Add(new Line { X1 = x, Y1 = 0, X2 = x, Y2 = h, Stroke = gridBrush, StrokeThickness = 0.5 });
        }
        for (int j = 1; j < 4; j++)
        {
            double y = h * j / 4.0;
            ChartCanvas.Children.Add(new Line { X1 = 0, Y1 = y, X2 = w, Y2 = y, Stroke = gridBrush, StrokeThickness = 0.5 });
        }

        // user(파랑)/system(빨강) 라인 — 점 1개 이하면 그리지 않음
        var history = Mon.CpuHistory;
        int n = history.Count;
        if (n < 2) return;

        var userPoints = new PointCollection();
        var sysPoints = new PointCollection();
        for (int i = 0; i < n; i++)
        {
            double x = (double)i / (n - 1) * w;
            userPoints.Add(new Point(x, h * (1 - Math.Clamp(history[i].User, 0, 100) / 100.0)));
            sysPoints.Add(new Point(x, h * (1 - Math.Clamp(history[i].System, 0, 100) / 100.0)));
        }
        userPoints.Freeze();
        sysPoints.Freeze();
        ChartCanvas.Children.Add(new Polyline { Points = sysPoints, Stroke = MonitorUi.Brush(MonitorUi.Red), StrokeThickness = 1.5 });
        ChartCanvas.Children.Add(new Polyline { Points = userPoints, Stroke = MonitorUi.Brush(MonitorUi.Blue), StrokeThickness = 1.5 });
    }

    private void UpdateReadouts()
    {
        var cpu = Mon.Cpu;
        IdleValue.Text = $"{cpu.Idle:F1}%";
        UserValue.Text = $"{cpu.User:F1}%";
        SysValue.Text = $"{cpu.System:F1}%";
    }

    // ── 카드 (가동시간 / 온도) ───────────────────────────────────────────

    private void UpdateCards()
    {
        var uptime = Mon.Uptime;
        int d = uptime.Days, h = uptime.Hours, m = uptime.Minutes;
        UptimeValue.Text = d > 0 ? $"{d}일" : h > 0 ? $"{h}시간" : $"{m}분";
        UptimeDesc.Text = d == 0 ? "시스템을 아주 최근에 시작했습니다."
            : d < 7 ? "재시작 없이 잘 작동하고 있습니다."
            : "한동안 재시작하지 않았습니다.";

        if (Mon.CpuTemperature is { } t)
        {
            TempTitle.Text = "온도";
            TempValue.Text = $"{t:F0}°C";
            TempDesc.Text = t < 70 ? "정상 작동 범위 내에 있습니다."
                : t < 90 ? "온도가 다소 높습니다."
                : "온도가 매우 높습니다.";
        }
        else
        {
            // Windows에는 thermalState 폴백이 없음 — 스펙 §4.2 권장 문구
            TempTitle.Text = "온도";
            TempValue.Text = "—";
            TempDesc.Text = "이 PC에서는 CPU 온도를 읽을 수 없습니다.";
        }
    }

    // ── 상위 프로세스 (TopCpuProcesses 호출처는 이 타이머 하나로 유지) ────

    private async Task ReloadProcessesAsync()
    {
        if (_reloading) return;
        _reloading = true;
        try
        {
            var rows = await Task.Run(() => Mon.TopCpuProcesses(5)
                .Select(p => (Proc: p,
                              Name: SystemMonitor.ProcessDisplayName(p.Pid, p.Name),
                              Icon: MonitorUi.ProcessIcon(p.Pid)))
                .ToList());
            if (!_open) return;

            ProcList.Children.Clear();
            if (rows.Count == 0)
            {
                ProcList.Children.Add(new TextBlock
                {
                    Text = "측정 중…",
                    FontSize = 12,
                    Margin = new Thickness(0, 4, 0, 4),
                    Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                });
                return;
            }
            foreach (var row in rows)
            {
                int pid = row.Proc.Pid;
                var valueBrush = row.Proc.CpuPercent >= 50
                    ? MonitorUi.Brush(MonitorUi.Orange)
                    : (System.Windows.Media.Brush)FindResource("TextPrimaryBrush");
                ProcList.Children.Add(MonitorUi.ProcessRow(this, row.Name, row.Icon,
                    $"{row.Proc.CpuPercent:F1}%", valueBrush, () => QuitProcess(pid)));
            }
        }
        catch { /* 베스트에포트 — 조용히 무시 */ }
        finally { _reloading = false; }
    }

    /// <summary>"종료" — 정상 종료 요청 후 0.8초 뒤 목록 reload (스펙 §6.3 reloadSoon).</summary>
    private void QuitProcess(int pid)
    {
        Mon.Quit(pid);
        var once = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.8) };
        once.Tick += (_, _) =>
        {
            once.Stop();
            if (_open) _ = ReloadProcessesAsync();
        };
        once.Start();
    }
}
