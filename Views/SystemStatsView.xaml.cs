// mac 소스 대응: Sources/XFinder/Views/RootView.swift — SystemStatsView (아이콘+퍼센트 3종, 클릭 시 상세 popover)
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using XFinder.Models;
using XFinder.Services;
using XFinder.Views.Monitor;

namespace XFinder.Views;

/// <summary>
/// 툴바 우측 시스템 상태 컴팩트 표시 — CPU(파랑)/메모리(보라)/디스크(주황) 아이콘+퍼센트.
/// 각 항목 클릭 시 해당 상세 뷰를 Popup(StaysOpen=false, Placement=Bottom)으로 표시.
/// SystemMonitor는 앱 전역 싱글턴 — 로드 시 Start() 호출(이미 동작 중이면 무시).
/// </summary>
public partial class SystemStatsView : UserControl
{
    private AppModel? _model;
    private DateTime _cpuClosedAt = DateTime.MinValue;
    private DateTime _memClosedAt = DateTime.MinValue;
    private DateTime _diskClosedAt = DateTime.MinValue;

    private static SystemMonitor Mon => SystemMonitor.Instance;

    public SystemStatsView()
    {
        InitializeComponent();

        CpuIcon.Text = IconMap.Glyph("cpu");
        MemIcon.Text = IconMap.Glyph("memorychip");
        DiskIcon.Text = IconMap.Glyph("internaldrive");
        CpuIcon.Foreground = MonitorUi.Brush(MonitorUi.Blue);
        MemIcon.Foreground = MonitorUi.Brush(MonitorUi.Purple);
        DiskIcon.Foreground = MonitorUi.Brush(MonitorUi.Orange);

        // 팝업 라이프사이클 — 열릴 때만 타이머 동작, 닫히면 정지(리소스).
        CpuPopup.Opened += (_, _) => CpuDetail.OnPopupOpened();
        CpuPopup.Closed += (_, _) => { CpuDetail.OnPopupClosed(); _cpuClosedAt = DateTime.UtcNow; };
        MemPopup.Opened += (_, _) => MemDetail.OnPopupOpened();
        MemPopup.Closed += (_, _) => { MemDetail.OnPopupClosed(); _memClosedAt = DateTime.UtcNow; };
        DiskPopup.Opened += (_, _) => DiskDetail.OnPopupOpened();
        DiskPopup.Closed += (_, _) => { DiskDetail.OnPopupClosed(); _diskClosedAt = DateTime.UtcNow; };
        DiskDetail.RequestClose += () => DiskPopup.IsOpen = false;

        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoadedView;
        Unloaded += OnUnloadedView;
    }

    // ── 라이프사이클 ─────────────────────────────────────────────────────

    private void OnLoadedView(object? sender, RoutedEventArgs e)
    {
        Mon.Start();   // 전역 싱글턴 — 창마다 인스턴스를 만들지 않음 (스펙 §1)
        Mon.PropertyChanged -= OnMonitorChanged;   // Loaded 중복 호출 대비
        Mon.PropertyChanged += OnMonitorChanged;
        UpdateTexts();
    }

    private void OnUnloadedView(object? sender, RoutedEventArgs e)
    {
        Mon.PropertyChanged -= OnMonitorChanged;
        CpuPopup.IsOpen = false;
        MemPopup.IsOpen = false;
        DiskPopup.IsOpen = false;
    }

    private void OnDataContextChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (_model is not null) _model.PropertyChanged -= OnModelChanged;
        _model = e.NewValue as AppModel;
        if (_model is not null)
        {
            _model.PropertyChanged += OnModelChanged;
            ApplyScale(_model.ListScale);
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppModel.ListScale) && _model is not null)
        {
            var scale = _model.ListScale;
            if (Dispatcher.CheckAccess()) ApplyScale(scale);
            else Dispatcher.InvokeAsync(() => ApplyScale(scale));
        }
    }

    // ── 표시 갱신 ───────────────────────────────────────────────────────

    private void OnMonitorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(SystemMonitor.CpuUsage)
            or nameof(SystemMonitor.MemoryUsage) or nameof(SystemMonitor.DiskUsage))) return;
        if (Dispatcher.CheckAccess()) UpdateTexts();
        else Dispatcher.InvokeAsync(UpdateTexts);
    }

    private void UpdateTexts()
    {
        CpuText.Text = $"{Mon.CpuUsage * 100:F0}%";
        MemText.Text = $"{Mon.MemoryUsage * 100:F0}%";
        DiskText.Text = $"{Mon.DiskUsage * 100:F0}%";
    }

    /// <summary>목록 배율(listScale) 반영 — 항목 간 11×, 아이콘 12×, 아이콘-텍스트 4×, 텍스트 11×.</summary>
    private void ApplyScale(double s)
    {
        CpuIcon.FontSize = MemIcon.FontSize = DiskIcon.FontSize = 12 * s;
        CpuText.FontSize = MemText.FontSize = DiskText.FontSize = 11 * s;
        CpuText.MinWidth = MemText.MinWidth = DiskText.MinWidth = 26 * s;
        CpuGap.Width = MemGap.Width = DiskGap.Width = 4 * s;
        Gap1.Width = Gap2.Width = 11 * s;
    }

    // ── 팝업 토글 ───────────────────────────────────────────────────────

    private void OnCpuClick(object sender, RoutedEventArgs e) => Toggle(CpuPopup, ref _cpuClosedAt);
    private void OnMemClick(object sender, RoutedEventArgs e) => Toggle(MemPopup, ref _memClosedAt);
    private void OnDiskClick(object sender, RoutedEventArgs e) => Toggle(DiskPopup, ref _diskClosedAt);

    /// <summary>
    /// StaysOpen=false 팝업은 버튼 클릭(마우스 다운)으로 먼저 닫히므로,
    /// 방금 닫힌 직후의 Click은 "토글 닫기"로 간주해 다시 열지 않는다.
    /// </summary>
    private static void Toggle(Popup popup, ref DateTime closedAt)
    {
        if (popup.IsOpen) { popup.IsOpen = false; return; }
        if ((DateTime.UtcNow - closedAt).TotalMilliseconds < 250) return;
        popup.IsOpen = true;
    }
}
