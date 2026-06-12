// mac 소스 대응: Sources/XFinder/Views/MemoryDetailView.swift — 메모리 팝업 (도넛/압력·스왑 카드/상위 프로세스)
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using XFinder.Services;

namespace XFinder.Views.Monitor;

/// <summary>
/// 메모리 상세 팝업 — 도넛(활성화/와이어드/압축됨 + 사용 중 잔여), 압력/스왑 카드,
/// 상위 프로세스 6개(RSS, 3초 타이머 — 팝업 열릴 때만 동작).
/// </summary>
public partial class MemoryDetailView : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3.0) };
    private bool _open;
    private bool _reloading;

    private static SystemMonitor Mon => SystemMonitor.Instance;

    public MemoryDetailView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => _ = ReloadProcessesAsync();
    }

    // ── 라이프사이클 ─────────────────────────────────────────────────────

    public void OnPopupOpened()
    {
        _open = true;
        Mon.PropertyChanged += OnMonitorChanged;
        UpdateStats();
        ProcList.Children.Clear();
        _ = ReloadProcessesAsync();   // 즉시 1회 + 3초 타이머
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
        if (!_open || e.PropertyName != nameof(SystemMonitor.Memory)) return;
        if (Dispatcher.CheckAccess()) UpdateStats();
        else Dispatcher.InvokeAsync(UpdateStats);
    }

    // ── 도넛 + 범례 + 카드 ───────────────────────────────────────────────

    private void UpdateStats()
    {
        var mem = Mon.Memory;
        const double size = 130, thickness = 16;

        DonutCanvas.Children.Clear();
        // 미사용 잔여 트랙 — secondary 18% (전체 원으로 깔아도 used 구간은 세그먼트가 덮음)
        DonutCanvas.Children.Add(MonitorUi.DonutRing(size, thickness,
            MonitorUi.Brush((Color)FindResource("TextSecondaryColor"), 0.18)));

        double total = mem.TotalGB;
        if (total > 0)
        {
            double cursor = 0;
            void Segment(double gb, Brush stroke)
            {
                double frac = Math.Max(0, gb) / total;
                if (frac > 0.0005)
                    DonutCanvas.Children.Add(MonitorUi.DonutSegment(size, thickness, cursor, Math.Min(1, cursor + frac), stroke));
                cursor += frac;
            }
            // 활성화 파랑 → 와이어드 보라 → 압축됨 인디고 → 나머지 used(회색 60%)
            Segment(mem.Active, MonitorUi.Brush(MonitorUi.Blue));
            Segment(mem.Wired, MonitorUi.Brush(MonitorUi.Purple));
            Segment(mem.Compressed, MonitorUi.Brush(MonitorUi.Indigo));
            Segment(Math.Max(0, mem.UsedGB - mem.Active - mem.Wired - mem.Compressed),
                MonitorUi.Brush(MonitorUi.Gray, 0.6));
        }

        CenterUsed.Text = $"{mem.UsedGB:F2}GB";
        CenterTotal.Text = $"/ {mem.TotalGB:F0}GB";
        LegendActive.Text = $"{mem.Active:F2}GB";
        LegendWired.Text = $"{mem.Wired:F2}GB";
        LegendCompressed.Text = $"{mem.Compressed:F2}GB";

        PressureValue.Text = $"{mem.Pressure:F0}%";
        PressureDesc.Text = mem.Pressure < 70 ? "여유로운 상태입니다." : "메모리 사용량이 높습니다.";
        SwapValue.Text = $"{mem.SwapUsed:F2}GB";
    }

    // ── 상위 프로세스 (RSS 내림차순 6개) ─────────────────────────────────

    private async Task ReloadProcessesAsync()
    {
        if (_reloading) return;
        _reloading = true;
        try
        {
            var rows = await Task.Run(() => Mon.TopMemoryProcesses(6)
                .Select(p => (Proc: p, Icon: MonitorUi.ProcessIcon(p.Pid)))
                .ToList());
            if (!_open) return;

            ProcList.Children.Clear();
            foreach (var row in rows)
            {
                int pid = row.Proc.Pid;
                var valueBrush = row.Proc.RssGB >= 2.0
                    ? MonitorUi.Brush(MonitorUi.Orange)
                    : (Brush)FindResource("TextPrimaryBrush");
                ProcList.Children.Add(MonitorUi.ProcessRow(this, row.Proc.Name, row.Icon,
                    $"{row.Proc.RssGB:F2}GB", valueBrush, () => QuitProcess(pid)));
            }
        }
        catch { /* 베스트에포트 — 조용히 무시 */ }
        finally { _reloading = false; }
    }

    /// <summary>"종료" — 정상 종료 요청 후 0.8초 뒤 목록 reload.</summary>
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
