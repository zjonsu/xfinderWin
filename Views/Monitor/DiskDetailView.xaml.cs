// mac 소스 대응: Sources/XFinder/Views/DiskDetailView.swift — 디스크 팝업 (도넛 분류/S.M.A.R.T./온도/휴지통/파일 계산)
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using XFinder.Models;
using XFinder.Services;

namespace XFinder.Views.Monitor;

/// <summary>
/// 디스크 상세 팝업 — 용량 도넛(카테고리 5색)/사용·전체, 건강 상태(S.M.A.R.T.)/온도 카드,
/// 휴지통 크기+비우기, "파일 계산" 종류별 분류 목록(클릭 → 오른쪽 패널 typeMode 전환 + 팝업 닫기).
/// 캐시 동작: 재오픈 시 재스캔 없음 — "다시 계산"으로만 강제 갱신 (스펙 §5.1~5.2).
/// </summary>
public partial class DiskDetailView : UserControl
{
    /// <summary>팝업 닫기 요청 (카테고리 행 클릭/휴지통 비우기 확인 진입 시) — SystemStatsView가 구독.</summary>
    public event Action? RequestClose;

    private bool _open;

    private static SystemMonitor Mon => SystemMonitor.Instance;

    /// <summary>도넛 팔레트 — id 순: 응용 프로그램=빨강, 다운로드=핑크, 문서=파랑, 데스크탑=틸, 기타=회색.</summary>
    private static readonly Color[] Palette =
        { MonitorUi.Red, MonitorUi.Pink, MonitorUi.Blue, MonitorUi.Teal, MonitorUi.Gray };

    /// <summary>카테고리 메타 (fileTypeOrder 순) — 아이콘 글리프/색. 모르는 이름은 마지막("기타").</summary>
    private static readonly (string Name, string Glyph, Color Color)[] TypeMeta =
    {
        ("문서", IconMap.Glyph("doc.text"), MonitorUi.Blue),
        ("이미지", IconMap.Glyph("photo"), MonitorUi.Green),
        ("동영상", IconMap.Glyph("film"), MonitorUi.Pink),
        ("음악", IconMap.Glyph("music.note"), MonitorUi.Purple),
        ("압축", "", MonitorUi.Orange),   // Segoe ZipFolder
        ("기타", IconMap.Glyph("ellipsis.circle"), MonitorUi.Gray),
    };

    private static (string Name, string Glyph, Color Color) MetaFor(string typeName)
    {
        foreach (var meta in TypeMeta)
            if (meta.Name == typeName) return meta;
        return TypeMeta[^1];
    }

    public DiskDetailView()
    {
        InitializeComponent();
        RecalcIcon.Text = IconMap.Glyph("arrow.clockwise");
        HomePath.Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    // ── 라이프사이클 ─────────────────────────────────────────────────────

    public void OnPopupOpened()
    {
        _open = true;
        Mon.PropertyChanged += OnMonitorChanged;
        UpdateAll();
        // 캐시 있으면 재스캔 없이 즉시 표시 (force=false)
        _ = Mon.RefreshDiskAsync(force: false);
        _ = Mon.RefreshFileTypesAsync(force: false);
    }

    public void OnPopupClosed()
    {
        _open = false;
        Mon.PropertyChanged -= OnMonitorChanged;
    }

    private void OnMonitorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_open || e.PropertyName is null) return;
        if (!e.PropertyName.StartsWith("Disk", StringComparison.Ordinal)
            && !e.PropertyName.StartsWith("FileType", StringComparison.Ordinal)) return;
        if (Dispatcher.CheckAccess()) UpdateAll();
        else Dispatcher.InvokeAsync(UpdateAll);
    }

    // ── 버튼 동작 ───────────────────────────────────────────────────────

    private void OnRecalc(object sender, RoutedEventArgs e)
    {
        _ = Mon.RefreshDiskAsync(force: true);
        _ = Mon.RefreshFileTypesAsync(force: true);
    }

    private void OnEmptyTrash(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AppModel model) return;
        RequestClose?.Invoke();   // 확인 오버레이는 메인 창 소유 — 팝업 먼저 닫기
        model.Confirm = new ConfirmRequest
        {
            Title = "휴지통 비우기",
            Message = "휴지통의 모든 항목을 완전히 삭제하시겠습니까? 이 동작은 취소할 수 없습니다.",
            ConfirmTitle = "비우기",
            IsDestructive = true,
            Action = () => _ = Mon.EmptyTrashAsync(),
        };
    }

    // ── 표시 갱신 ───────────────────────────────────────────────────────

    private void UpdateAll()
    {
        // 헤더
        VolName.Text = Mon.DiskVolumeName;
        bool computing = Mon.DiskComputing || Mon.FileTypeComputing;
        RecalcLabel.Text = computing ? "계산 중…" : "다시 계산";
        RecalcBtn.IsEnabled = !computing;
        RecalcBtn.Opacity = computing ? 0.5 : 1.0;
        CacheTime.Text = !computing && Mon.DiskComputedAt is { } at ? $"계산: {at:HH:mm}" : "";

        UpdateDonut();
        UpdateLegend();
        UpdateCards();

        TrashValue.Text = Mon.DiskTrashBytes is { } trash ? SystemMonitor.Human(trash) : "—";

        UpdateTypeList();
    }

    private void UpdateDonut()
    {
        const double size = 140, thickness = 16;
        DonutCanvas.Children.Clear();
        // 배경 트랙 전체 원 — secondary 18%
        DonutCanvas.Children.Add(MonitorUi.DonutRing(size, thickness,
            MonitorUi.Brush((Color)FindResource("TextSecondaryColor"), 0.18)));

        long total = Mon.DiskTotalBytes;
        if (total > 0)
        {
            double cursor = 0;
            foreach (var category in Mon.DiskCategories)
            {
                double frac = Math.Max(0, category.Bytes) / (double)total;
                if (frac > 0.0005)
                {
                    var color = Palette[Math.Min(Math.Max(category.Id, 0), Palette.Length - 1)];
                    DonutCanvas.Children.Add(MonitorUi.DonutSegment(size, thickness,
                        cursor, Math.Min(1, cursor + frac), MonitorUi.Brush(color)));
                }
                cursor += frac;
            }
            CenterFree.Text = SystemMonitor.Human(Mon.DiskFreeBytes);
            CenterTotal.Text = $"/ {SystemMonitor.Human(total)}";
        }
        else
        {
            CenterFree.Text = "--";
            CenterTotal.Text = "/ --";
        }
    }

    private void UpdateLegend()
    {
        LegendPanel.Children.Clear();
        var categories = Mon.DiskCategories;
        if (categories.Count == 0)
        {
            LegendPanel.Children.Add(new TextBlock
            {
                Text = Mon.DiskComputing ? "계산 중…" : "—",
                FontSize = 12,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
            });
            return;
        }
        bool first = true;
        foreach (var category in categories)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, first ? 0 : 9, 0, 0),
            };
            first = false;
            var color = Palette[Math.Min(Math.Max(category.Id, 0), Palette.Length - 1)];
            row.Children.Add(new Ellipse
            {
                Width = 9, Height = 9,
                Fill = MonitorUi.Brush(color),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var texts = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
            texts.Children.Add(new TextBlock
            {
                Text = category.Name,
                FontSize = 12,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
            });
            texts.Children.Add(new TextBlock
            {
                Text = SystemMonitor.Human(category.Bytes),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
            });
            row.Children.Add(texts);
            LegendPanel.Children.Add(row);
        }
    }

    private void UpdateCards()
    {
        HealthValue.Text = Mon.DiskHealthValue ?? (Mon.DiskComputing ? "확인 중…" : "—");
        HealthDesc.Text = Mon.DiskHealthDesc ?? "드라이브 상태를 확인하고 있습니다.";

        if (Mon.DiskTemperature is { } t)
        {
            TempValue.Text = $"{t:F0}°C";
            TempDesc.Text = t < 50 ? "드라이브 온도가 정상 작동 범위 내에 있습니다."
                : t < 70 ? "드라이브 온도가 다소 높습니다."
                : "드라이브 온도가 높습니다.";
        }
        else
        {
            TempValue.Text = "—";
            TempDesc.Text = "이 PC에서는 드라이브 온도를 읽을 수 없습니다.";
        }
    }

    // ── 파일 계산 목록 ───────────────────────────────────────────────────

    private void UpdateTypeList()
    {
        TypeList.Children.Clear();
        var stats = Mon.FileTypeStats;

        if (stats is null)
        {
            if (Mon.FileTypeComputing)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 10) };
                row.Children.Add(new ProgressBar
                {
                    IsIndeterminate = true,
                    Width = 64, Height = 3,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                row.Children.Add(new TextBlock
                {
                    Text = "종류별로 계산 중…",
                    FontSize = 12,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("TextSecondaryBrush"),
                });
                TypeList.Children.Add(row);
            }
            else
            {
                // 아직 계산 전(또는 실패) — "파일 계산" 버튼으로 수동 시작
                var start = MonitorUi.LinkButton("파일 계산", 12, () => _ = Mon.RefreshFileTypesAsync(force: false));
                start.Margin = new Thickness(0, 10, 0, 10);
                start.HorizontalAlignment = HorizontalAlignment.Left;
                TypeList.Children.Add(start);
            }
            return;
        }

        bool first = true;
        foreach (var stat in stats)
        {
            if (!first)
                TypeList.Children.Add(new Rectangle { Height = 1, Fill = (Brush)FindResource("DividerBrush") });
            first = false;
            TypeList.Children.Add(BuildTypeRow(stat));
        }
    }

    private FrameworkElement BuildTypeRow(TypeBreakdown stat)
    {
        var meta = MetaFor(stat.Name);
        bool enabled = stat.Count > 0;

        var grid = new Grid { Margin = new Thickness(0, 7, 0, 7) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new TextBlock
        {
            Text = meta.Glyph,
            FontFamily = (FontFamily)FindResource("IconFontFamily"),
            FontSize = 14,
            Foreground = MonitorUi.Brush(meta.Color),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.Children.Add(icon);

        var name = new TextBlock
        {
            Text = stat.Name,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
        };
        Grid.SetColumn(name, 2);
        grid.Children.Add(name);

        var count = new TextBlock
        {
            Text = $"{stat.Count:N0}개",
            FontSize = 11,
            Margin = new Thickness(6, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
        };
        Grid.SetColumn(count, 3);
        grid.Children.Add(count);

        var size = new TextBlock
        {
            Text = SystemMonitor.Human(stat.Bytes),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
        };
        Grid.SetColumn(size, 4);
        grid.Children.Add(size);

        var chevron = new TextBlock
        {
            Text = IconMap.Glyph("chevron.right"),
            FontFamily = (FontFamily)FindResource("IconFontFamily"),
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = MonitorUi.Brush((Color)FindResource("TextSecondaryColor"), 0.5),
            Opacity = enabled ? 1.0 : 0.0,   // count==0이면 투명
        };
        Grid.SetColumn(chevron, 5);
        grid.Children.Add(chevron);

        var button = new Button
        {
            Content = grid,
            Focusable = false,
            IsEnabled = enabled,
            Opacity = enabled ? 1.0 : 0.45,
            Cursor = enabled ? System.Windows.Input.Cursors.Hand : null,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Template = MonitorUi.PlainButtonTemplate(),
        };
        if (enabled)
        {
            button.ToolTip = $"{stat.Name} 파일 내역을 오른쪽 패널에 보기";
            var captured = stat;
            button.Click += (_, _) => OpenTypeBreakdown(captured);
        }
        return button;
    }

    /// <summary>카테고리 행 클릭 — 오른쪽 패널을 typeMode로 전환하고 팝업 닫기 (스펙 §7.7).</summary>
    private void OpenTypeBreakdown(TypeBreakdown stat)
    {
        if (DataContext is not AppModel model) return;
        model.ShowTypeBreakdown(stat.Name, stat.Count, stat.Files);
        RequestClose?.Invoke();
    }
}
