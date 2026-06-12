// mac 소스 대응: Views/SidebarView.swift (SidebarView + SidebarRowView, FolderDropModifier 연동 — 스펙 02 §6)
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using XFinder.Models;
using XFinder.Services;

namespace XFinder.Views;

/// <summary>
/// 파인더식 좌측 사이드바 — 섹션 3개(즐겨찾기/위치/태그), 폴더 트리 펼침,
/// 단일 클릭 = 즉시 이동 / 빠른 더블클릭 = 펼침 토글, 드래그 소스·드롭 타깃, 즐겨찾기 순서 변경.
/// 전역 키(↑↓→← 등)는 MainWindow가 라우팅하므로 여기서는 마우스 상호작용만 담당.
/// </summary>
public partial class SidebarView : UserControl
{
    private AppModel? _model;

    /// <summary>현재 렌더된 행의 시각 요소 묶음 — 선택 강조 갱신용.</summary>
    private sealed record RowVisual(SidebarItem Item, Border Row, TextBlock Title, TextBlock? Icon, TextBlock? Disclosure);

    private readonly List<RowVisual> _rows = new();
    private readonly List<SidebarItem> _observedItems = new();

    /// <summary>항목별 마지막 클릭 시각(ms) — 행이 재구축돼도 더블클릭 판정이 유지되게 Id 기준.</summary>
    private readonly Dictionary<Guid, long> _lastTapAt = new();

    // 드래그 시작 판정
    private SidebarItem? _pressedItem;
    private Point _pressedPoint;

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    public SidebarView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    // ── 모델 구독 ────────────────────────────────────────────────────────

    private void OnDataContextChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (_model is not null) _model.PropertyChanged -= OnModelPropertyChanged;
        _model = DataContext as AppModel;
        if (_model is not null) _model.PropertyChanged += OnModelPropertyChanged;
        Rebuild();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppModel.Sections):
            case nameof(AppModel.ListScale):
                RunOnUI(Rebuild);
                break;
            case nameof(AppModel.SelectedSidebarId):
            case nameof(AppModel.FocusedPane):
                RunOnUI(RefreshHighlight);
                break;
        }
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 펼침/자식 로드/디스클로저 가능 여부 — 트리 구조가 바뀌므로 재구축
        if (e.PropertyName is nameof(SidebarItem.IsExpanded)
            or nameof(SidebarItem.Children)
            or nameof(SidebarItem.MayHaveChildren))
            RunOnUI(Rebuild);
    }

    private void RunOnUI(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(action);
    }

    // ── 트리 렌더 ────────────────────────────────────────────────────────

    private void Rebuild()
    {
        foreach (var item in _observedItems) item.PropertyChanged -= OnItemPropertyChanged;
        _observedItems.Clear();
        _rows.Clear();
        Host.Children.Clear();
        if (_model is null) return;

        var scale = _model.ListScale;
        foreach (var section in _model.Sections)
        {
            // 섹션 제목: 11 × scale semibold, secondary, 패딩 가로 10 / 위 12 / 아래 2
            var header = new TextBlock
            {
                Text = section.Title,
                FontSize = 11 * scale,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(10, 12, 10, 2),
            };
            header.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            Host.Children.Add(header);

            foreach (var item in section.Items)
                AddRowRecursive(item, scale);
        }
        RefreshHighlight();
    }

    private void AddRowRecursive(SidebarItem item, double scale)
    {
        Host.Children.Add(BuildRow(item, scale));
        item.PropertyChanged += OnItemPropertyChanged;
        _observedItems.Add(item);

        if (item.IsExpanded && item.Children is not null)
            foreach (var child in item.Children)
                AddRowRecursive(child, scale);
    }

    private Border BuildRow(SidebarItem item, double scale)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };

        // 들여쓰기: depth × 15 × scale
        if (item.Depth > 0)
            content.Children.Add(new Border { Width = item.Depth * 15 * scale });

        // 디스클로저 (펼침/접힘 화살표 — 직접 클릭만 토글)
        TextBlock? disclosure = null;
        if (item.CanExpand)
        {
            disclosure = new TextBlock
            {
                Text = IconMap.Glyph(item.IsExpanded ? "chevron.down" : "chevron.right"),
                FontFamily = (FontFamily)FindResource("IconFontFamily"),
                FontSize = 10 * scale,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var hit = new Border
            {
                Width = 13 * scale,
                Height = 13 * scale,
                Background = Brushes.Transparent,   // 빈 영역도 히트
                Child = disclosure,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0),
            };
            hit.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;   // 행 클릭(탐색)으로 번지지 않게
                if (_model is null) return;
                _model.FocusedPane = FocusPane.Sidebar;
                _model.ToggleExpand(item);
            };
            content.Children.Add(hit);
        }
        else
        {
            content.Children.Add(new Border { Width = 13 * scale, Margin = new Thickness(0, 0, 5, 0) });
        }

        // 아이콘: 태그 = 색 점, 그 외 = 외곽선 글리프(.fill 제거) — 컨테이너 폭 22 × scale
        TextBlock? icon = null;
        if (item.Kind == SidebarItem.ItemKind.Tag)
        {
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 11 * scale,
                Height = 11 * scale,
                Fill = new SolidColorBrush(IconMap.TagColorByName(item.Title)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            content.Children.Add(new Border
            {
                Width = 22 * scale,
                Child = dot,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0),
            });
        }
        else
        {
            icon = new TextBlock
            {
                Text = IconMap.Glyph(item.Icon.Replace(".fill", "")),
                FontFamily = (FontFamily)FindResource("IconFontFamily"),
                FontSize = 14.5 * scale,
                Width = 22 * scale,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0),
            };
            content.Children.Add(icon);
        }

        // 제목: 13.5 × scale, 1줄 말줄임 (middle 말줄임은 WPF 미지원 — 끝 말줄임 근사)
        var title = new TextBlock
        {
            Text = item.Title,
            FontSize = 13.5 * scale,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(title);

        var row = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 5 * scale, 6, 5 * scale),
            Margin = new Thickness(0, 0, 0, 1),    // LazyVStack spacing 1
            Background = Brushes.Transparent,      // 빈 영역까지 히트 영역
            Child = content,
        };

        _rows.Add(new RowVisual(item, row, title, icon, disclosure));
        WireRowInteractions(item, row);
        return row;
    }

    // ── 선택 강조 ────────────────────────────────────────────────────────

    private void RefreshHighlight()
    {
        if (_model is null) return;
        foreach (var rv in _rows)
        {
            var selected = _model.SelectedSidebarId == rv.Item.Id;
            var focused = selected && _model.FocusedPane == FocusPane.Sidebar;

            if (focused) rv.Row.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            else if (selected) rv.Row.SetResourceReference(Border.BackgroundProperty, "SelectionInactiveBrush");
            else rv.Row.Background = Brushes.Transparent;

            if (focused)
            {
                rv.Title.Foreground = Brushes.White;
                if (rv.Icon is not null) rv.Icon.Foreground = Brushes.White;
                if (rv.Disclosure is not null)
                    rv.Disclosure.Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
            }
            else
            {
                rv.Title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                rv.Icon?.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                rv.Disclosure?.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            }
        }
    }

    // ── 행 상호작용 (클릭/더블클릭/우클릭/드래그/드롭) ───────────────────

    private void WireRowInteractions(SidebarItem item, Border row)
    {
        row.MouseLeftButtonDown += (_, e) =>
        {
            _pressedItem = item;
            _pressedPoint = e.GetPosition(this);
        };

        row.MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || !ReferenceEquals(_pressedItem, item)) return;
            var pos = e.GetPosition(this);
            if (Math.Abs(pos.X - _pressedPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _pressedPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _pressedItem = null;
            if (item.Kind == SidebarItem.ItemKind.Folder && item.Path is not null)
                StartDrag(item, row);
        };

        row.MouseLeftButtonUp += (_, _) =>
        {
            if (!ReferenceEquals(_pressedItem, item)) return;
            _pressedItem = null;
            HandleTap(item);
        };

        // 우클릭 메뉴 — 폴더 행만
        if (item.Kind == SidebarItem.ItemKind.Folder && item.Path is not null)
        {
            row.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                OpenRowMenu(item, row);
            };
        }

        // 드롭 타깃 — 폴더 행에 파일 드롭 = 이동(Ctrl = 복사), 즐겨찾기끼리 = 순서 변경
        if (item.Kind == SidebarItem.ItemKind.Folder && item.Path is not null)
        {
            row.AllowDrop = true;
            row.DragOver += (_, e) =>
            {
                e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                    ? (e.KeyStates.HasFlag(DragDropKeyStates.ControlKey) ? DragDropEffects.Copy : DragDropEffects.Move)
                    : DragDropEffects.None;
                e.Handled = true;
            };
            row.DragEnter += (_, e) =>
            {
                if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                if (_model?.SelectedSidebarId != item.Id || _model?.FocusedPane != FocusPane.Sidebar)
                    row.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
            };
            row.DragLeave += (_, _) => RefreshHighlight();
            row.Drop += (_, e) => HandleDrop(item, e);
        }
    }

    /// <summary>단일 클릭 = 즉시 이동, 시스템 더블클릭 간격 내 재클릭 = 펼침 토글 (mac handleTap 대응).</summary>
    private void HandleTap(SidebarItem item)
    {
        if (_model is null) return;
        _model.FocusedPane = FocusPane.Sidebar;

        var now = Environment.TickCount64;
        uint interval;
        try { interval = GetDoubleClickTime(); } catch { interval = 500; }
        if (interval == 0) interval = 500;

        if (_lastTapAt.TryGetValue(item.Id, out var last) && now - last < interval)
        {
            if (item.CanExpand) _model.ToggleExpand(item);
            _lastTapAt.Remove(item.Id);
        }
        else
        {
            _model.ActivateSidebar(item);
            _lastTapAt[item.Id] = now;
        }
    }

    private void OpenRowMenu(SidebarItem item, Border row)
    {
        if (_model is null || item.Path is null) return;
        var model = _model;
        var path = item.Path;

        var menu = new ContextMenu();
        if (model.IsFavorite(path))
            menu.Items.Add(MenuItemOf("즐겨찾기에서 제거", () => model.RemoveFavorite(path)));
        else
            menu.Items.Add(MenuItemOf("즐겨찾기에 추가", () => model.AddFavorite(path)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemOf("탐색기에서 보기", () => SystemActions.RevealInExplorer(path)));
        menu.Items.Add(MenuItemOf("터미널에서 열기", () => SystemActions.OpenTerminal(path, model.TerminalApp)));
        menu.PlacementTarget = row;
        menu.IsOpen = true;
    }

    private static MenuItem MenuItemOf(string header, Action action)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => action();
        return mi;
    }

    /// <summary>폴더 행 드래그 시작 — FileDrop 페이로드. 즐겨찾기 최상위 행이면 순서 변경 후보로 표시.</summary>
    private void StartDrag(SidebarItem item, Border row)
    {
        if (_model is null || item.Path is null) return;
        var reorderable = item.Depth == 0 && _model.IsFavorite(item.Path);
        _model.DraggingFavorite = reorderable ? item.Path : null;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { item.Path });
            DragDrop.DoDragDrop(row, data, DragDropEffects.Move | DragDropEffects.Copy);
        }
        catch { }
        finally
        {
            _model.DraggingFavorite = null;
        }
    }

    /// <summary>폴더 행 드롭 — 즐겨찾기 최상위끼리는 순서 변경, 그 외에는 이동(Ctrl = 복사).</summary>
    private void HandleDrop(SidebarItem item, DragEventArgs e)
    {
        e.Handled = true;
        if (_model is null || item.Path is null) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            RefreshHighlight();
            return;
        }

        var dragging = _model.DraggingFavorite;
        var targetIsTopFavorite = item.Depth == 0
            && item.Kind == SidebarItem.ItemKind.Folder
            && _model.IsFavorite(item.Path);

        if (dragging is not null && targetIsTopFavorite
            && paths.Length == 1 && AppModel.PathEquals(paths[0], dragging))
        {
            // 즐겨찾기 순서 변경 — 끌어온 항목을 대상 항목 앞으로
            if (!AppModel.PathEquals(dragging, item.Path))
                _model.MoveFavorite(dragging, toBefore: item.Path);
            _model.DraggingFavorite = null;
            RefreshHighlight();
            return;
        }

        var copy = e.KeyStates.HasFlag(DragDropKeyStates.ControlKey);
        _model.DropFiles(paths, item.Path, copy);
        RefreshHighlight();
    }
}
