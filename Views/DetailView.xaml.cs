// mac 소스 대응: Sources/XFinder/Views/DetailView.swift + FileDrag.swift + FolderDrop.swift (스펙 03 + 08-gaps)
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using XFinder.Models;
using XFinder.Services;
using SortKey = XFinder.Models.SortKey;   // System.Globalization.SortKey와 모호성 회피

namespace XFinder.Views;

/// <summary>
/// 오른쪽 상세 패널 — 열 헤더 + 가상화 목록/아이콘 그리드 + 상태 표시줄.
/// 전역 키는 MainWindow가 라우팅하므로 여기서는 마우스/드래그&드롭/컨텍스트 메뉴만 처리한다.
/// </summary>
public partial class DetailView : UserControl, INotifyPropertyChanged
{
    private AppModel? _model;
    private PaneTab? _tab;
    private ScrollViewer? _listScroll;

    private List<DetailRow> _rows = new();
    private Dictionary<string, int> _rowIndexByPath = new(StringComparer.OrdinalIgnoreCase);

    // 클릭/드래그 상태 (스펙 03 §1.8, §3)
    private const double DoubleClickInterval = 0.3;   // 초 — 시스템 기본보다 짧게
    private string? _lastClickPath;
    private DateTime _lastClickAt = DateTime.MinValue;
    private bool _suppressCursorScroll;               // 마우스 클릭 직후 1회 자동 스크롤 억제

    private DetailRow? _pressRow;
    private Point _pressPoint;
    private bool _pressDraggable;                     // mouse-down 시점(선택 변경 전) 캡처
    private bool _deferredReduce;                     // 다중 선택 축소를 mouse-up으로 연기
    private bool _dragStarted;
    private bool _didRubber;
    private FrameworkElement? _captureElement;

    private bool _bgPress;
    private bool _bgBand;
    private Point _bgPoint;
    private int _bgStartIndex;

    private static readonly Brush White85 = Frozen(new SolidColorBrush(Color.FromArgb(217, 255, 255, 255)));
    private static readonly Brush DestructiveRed = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0x3B, 0x30)));

    public DetailView()
    {
        InitializeComponent();
        UpdateLayoutProps();
        DataContextChanged += OnDataContextChanged;
        ThemeService.ThemeChanged += OnThemeChanged;
        Unloaded += (_, _) => ThemeService.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged()
    {
        UpdateRowVisuals();
        RefreshHeader();
    }

    // ── 모델/탭 구독 ─────────────────────────────────────────────────────

    private void OnDataContextChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (_model is not null) _model.PropertyChanged -= OnModelPropertyChanged;
        _model = DataContext as AppModel;
        if (_model is not null) _model.PropertyChanged += OnModelPropertyChanged;
        ObserveTab();
        UpdateLayoutProps();
        RefreshAll();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnModelPropertyChanged(sender, e)));
            return;
        }
        switch (e.PropertyName)
        {
            case nameof(AppModel.Detail):
                ObserveTab();
                RefreshAll();
                break;
            case nameof(AppModel.ListScale):
                UpdateLayoutProps();
                RefreshHeader();
                break;
            case nameof(AppModel.ColumnWidths):
                UpdateLayoutProps();   // 헤더 재구성 없이 바인딩으로 반영 (드래그 중 핸들 유지)
                break;
            case nameof(AppModel.DateStyle):
                RebuildRows();
                break;
            case nameof(AppModel.FocusedPane):
                UpdateRowVisuals();
                break;
            case nameof(AppModel.StatusFreeSpace):
                RefreshStatus();
                break;
        }
    }

    private void ObserveTab()
    {
        if (_tab is not null) _tab.PropertyChanged -= OnTabPropertyChanged;
        _tab = _model?.Detail;
        if (_tab is not null) _tab.PropertyChanged += OnTabPropertyChanged;
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnTabPropertyChanged(sender, e)));
            return;
        }
        if (_model is null || !ReferenceEquals(sender, _model.Detail)) return;
        switch (e.PropertyName)
        {
            case nameof(PaneTab.Items):
                RebuildRows();
                RefreshStatus();
                break;
            case nameof(PaneTab.Cursor):
                UpdateRowVisuals();
                if (_suppressCursorScroll) _suppressCursorScroll = false;
                else ScrollCursorIntoView();
                break;
            case nameof(PaneTab.Selection):
                UpdateRowVisuals();
                RefreshStatus();
                break;
            case nameof(PaneTab.ViewMode):
                ApplyViewMode();
                break;
            case nameof(PaneTab.LoadError):
                RefreshEmptyState();
                break;
        }
    }

    private void RefreshAll()
    {
        ApplyViewMode();
        RefreshHeader();
        RebuildRows();
        RefreshStatus();
    }

    // ── 배율/열 너비 레이아웃 값 (RelativeSource 바인딩 대상) ─────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField(ref double field, double value, [CallerMemberName] string? name = null)
    {
        if (Math.Abs(field - value) < 0.001) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
        return true;
    }

    private double _iconColWidth = 18, _iconSize = 16, _rowHeight = 22;
    private double _nameFontSize = 12, _metaFontSize = 11, _headerFontSize = 11, _groupCountFontSize = 10, _chevronFontSize = 7;
    private double _sizeColWidth = 70, _modifiedColWidth = 120, _createdColWidth = 120, _kindColWidth = 96;
    private double _sizeCellWidth = 76, _modifiedCellWidth = 126, _createdCellWidth = 126, _kindCellWidth = 102;
    private double _cellSize = 104, _thumbSize = 70, _iconNameFontSize = 11, _iconLabelMaxHeight = 34;

    public double IconColWidth { get => _iconColWidth; private set => SetField(ref _iconColWidth, value); }
    public double IconSize { get => _iconSize; private set => SetField(ref _iconSize, value); }
    public double RowHeight { get => _rowHeight; private set => SetField(ref _rowHeight, value); }
    public double NameFontSize { get => _nameFontSize; private set => SetField(ref _nameFontSize, value); }
    public double MetaFontSize { get => _metaFontSize; private set => SetField(ref _metaFontSize, value); }
    public double HeaderFontSize { get => _headerFontSize; private set => SetField(ref _headerFontSize, value); }
    public double GroupCountFontSize { get => _groupCountFontSize; private set => SetField(ref _groupCountFontSize, value); }
    public double ChevronFontSize { get => _chevronFontSize; private set => SetField(ref _chevronFontSize, value); }
    public double SizeColWidth { get => _sizeColWidth; private set => SetField(ref _sizeColWidth, value); }
    public double ModifiedColWidth { get => _modifiedColWidth; private set => SetField(ref _modifiedColWidth, value); }
    public double CreatedColWidth { get => _createdColWidth; private set => SetField(ref _createdColWidth, value); }
    public double KindColWidth { get => _kindColWidth; private set => SetField(ref _kindColWidth, value); }
    public double SizeCellWidth { get => _sizeCellWidth; private set => SetField(ref _sizeCellWidth, value); }
    public double ModifiedCellWidth { get => _modifiedCellWidth; private set => SetField(ref _modifiedCellWidth, value); }
    public double CreatedCellWidth { get => _createdCellWidth; private set => SetField(ref _createdCellWidth, value); }
    public double KindCellWidth { get => _kindCellWidth; private set => SetField(ref _kindCellWidth, value); }
    public double CellSize { get => _cellSize; private set => SetField(ref _cellSize, value); }
    public double ThumbSize { get => _thumbSize; private set => SetField(ref _thumbSize, value); }
    public double IconNameFontSize { get => _iconNameFontSize; private set => SetField(ref _iconNameFontSize, value); }
    public double IconLabelMaxHeight { get => _iconLabelMaxHeight; private set => SetField(ref _iconLabelMaxHeight, value); }

    private void UpdateLayoutProps()
    {
        var s = _model?.ListScale ?? 1.0;
        IconColWidth = 18 * s;
        IconSize = 16 * s;
        RowHeight = 22 * s;
        NameFontSize = 12 * s;
        MetaFontSize = 11 * s;
        HeaderFontSize = 11 * s;
        GroupCountFontSize = 10 * s;
        ChevronFontSize = Math.Max(7 * s, 6);
        double W(ListColumn c) => (_model?.ColumnWidth(c) ?? c.DefaultWidth()) * s;
        SizeColWidth = W(ListColumn.Size);
        ModifiedColWidth = W(ListColumn.Modified);
        CreatedColWidth = W(ListColumn.Created);
        KindColWidth = W(ListColumn.Kind);
        SizeCellWidth = SizeColWidth + 6;
        ModifiedCellWidth = ModifiedColWidth + 6;
        CreatedCellWidth = CreatedColWidth + 6;
        KindCellWidth = KindColWidth + 6;
        CellSize = 104 * s;
        ThumbSize = 70 * s;
        IconNameFontSize = 11 * s;
        IconLabelMaxHeight = 11 * s * 3.0;   // 2줄 + 여유
    }

    // ── 열 헤더 ──────────────────────────────────────────────────────────

    private void RefreshHeader()
    {
        if (_model is null) { HeaderBar.Child = null; return; }
        var dock = new DockPanel { LastChildFill = true };

        var spacer = new Border();
        spacer.SetBinding(WidthProperty, BindSelf(nameof(IconColWidth)));
        DockPanel.SetDock(spacer, Dock.Left);
        dock.Children.Add(spacer);

        // 오른쪽 도킹 순서: 종류(맨 오른쪽) → 생성일 → 수정일 → 크기
        dock.Children.Add(HeaderCell("종류", SortKey.Kind, ListColumn.Kind, trailing: false, nameof(KindCellWidth)));
        dock.Children.Add(HeaderCell("생성일", SortKey.Created, ListColumn.Created, trailing: false, nameof(CreatedCellWidth)));
        dock.Children.Add(HeaderCell("수정일", SortKey.Modified, ListColumn.Modified, trailing: false, nameof(ModifiedCellWidth)));
        dock.Children.Add(HeaderCell("크기", SortKey.Size, ListColumn.Size, trailing: true, nameof(SizeCellWidth)));

        var nameCell = new Grid();
        var nameBtn = HeaderButton("이름", SortKey.Name);
        nameBtn.Margin = new Thickness(6, 0, 0, 0);
        nameBtn.HorizontalAlignment = HorizontalAlignment.Left;
        nameCell.Children.Add(nameBtn);
        dock.Children.Add(nameCell);

        var menu = new ContextMenu();
        menu.Items.Add(Mi("열 너비 재설정", () => _model.ResetAllColumnWidths()));
        HeaderBar.ContextMenu = menu;
        HeaderBar.Child = dock;
    }

    private FrameworkElement HeaderCell(string label, SortKey key, ListColumn col, bool trailing, string cellWidthProp)
    {
        var grid = new Grid();
        grid.SetBinding(WidthProperty, BindSelf(cellWidthProp));

        var btn = HeaderButton(label, key);
        btn.Margin = new Thickness(6, 0, 0, 0);
        btn.HorizontalAlignment = trailing ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        grid.Children.Add(btn);

        grid.Children.Add(ResizeHandle(col));
        DockPanel.SetDock(grid, Dock.Right);
        return grid;
    }

    private Button HeaderButton(string label, SortKey key)
    {
        var pane = _model!.Detail;
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        var text = new TextBlock
        {
            Text = label,
            FontSize = HeaderFontSize,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        stack.Children.Add(text);
        if (pane.SortKey == key)
        {
            var chevron = new TextBlock
            {
                Text = IconMap.Glyph(pane.SortAscending ? "chevron.up" : "chevron.down"),
                FontFamily = (FontFamily)FindResource("IconFontFamily"),
                FontSize = ChevronFontSize,
                Margin = new Thickness(3, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            chevron.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            stack.Children.Add(chevron);
        }
        var btn = new Button
        {
            Content = stack,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Focusable = false,
            Cursor = Cursors.Hand,
            Template = FlatButtonTemplate(),
        };
        btn.Click += (_, _) => _model?.SetSort(key);
        return btn;
    }

    private static ControlTemplate FlatButtonTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        template.VisualTree = presenter;
        return template;
    }

    /// <summary>열 왼쪽 경계 리사이즈 핸들 — 오른쪽으로 끌면 열이 좁아진다 (스펙 2.2).</summary>
    private Thumb ResizeHandle(ListColumn col)
    {
        var thumb = new Thumb
        {
            Width = 9,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(-2, 0, 0, 0),
            Cursor = Cursors.SizeWE,
            Focusable = false,
            Template = HandleTemplate(),
        };
        double baseWidth = 0, cum = 0;
        thumb.DragStarted += (_, _) =>
        {
            baseWidth = _model?.ColumnWidth(col) ?? col.DefaultWidth();
            cum = 0;
        };
        thumb.DragDelta += (_, e) =>
        {
            if (_model is null) return;
            cum += e.HorizontalChange;
            var s = Math.Max(_model.ListScale, 0.01);
            _model.SetColumnWidth(col, baseWidth - cum / s);
        };
        thumb.MouseDoubleClick += (_, e) =>
        {
            _model?.ResetColumnWidth(col);
            e.Handled = true;
        };
        return thumb;
    }

    private static ControlTemplate HandleTemplate()
    {
        var template = new ControlTemplate(typeof(Thumb));
        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
        var line = new FrameworkElementFactory(typeof(System.Windows.Shapes.Rectangle));
        line.SetValue(WidthProperty, 1.0);
        line.SetValue(HeightProperty, 14.0);
        line.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        line.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        line.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "DividerBrush");
        grid.AppendChild(line);
        template.VisualTree = grid;
        return template;
    }

    private Binding BindSelf(string property) => new(property) { Source = this };

    // ── 행 구성 ──────────────────────────────────────────────────────────

    private void RebuildRows()
    {
        if (_model is null) return;
        var pane = _model.Detail;
        var items = pane.Items;
        var style = _model.DateStyle;
        var search = pane.SearchMode;
        var rows = new List<DetailRow>(items.Count + 8);
        var map = new Dictionary<string, int>(Math.Max(items.Count, 1), StringComparer.OrdinalIgnoreCase);

        void AddItem(int i)
        {
            if (i < 0 || i >= items.Count) return;   // 경계 검사 필수 (스펙 2.5)
            var it = items[i];
            var iconExt = it.Ext.Length > 0 ? "." + it.Ext : "";
            string? modTip = style == DateDisplayStyle.Relative && it.Modified != DateTime.MinValue ? Format.Date(it.Modified) : null;
            string? creTip = style == DateDisplayStyle.Relative && it.Created != DateTime.MinValue ? Format.Date(it.Created) : null;
            var row = new DetailRow
            {
                Item = it,
                Index = i,
                NameText = search ? _model.RelativeDisplay(it) : it.Name,
                SizeText = Format.Size(it.Size, it.IsDirectory),
                ModifiedText = Format.Date(it.Modified, style),
                CreatedText = Format.Date(it.Created, style),
                KindText = Format.KindLabel(it),
                ModifiedTip = modTip,
                CreatedTip = creTip,
                Icon = IconCache.IconFor(iconExt, it.IsDirectory, large: false),
                Thumb = IconCache.IconFor(iconExt, it.IsDirectory, large: true),
            };
            map[it.Path] = rows.Count;
            rows.Add(row);
        }

        var groups = pane.ActiveGroups;
        if (groups is not null)
        {
            foreach (var g in groups)
            {
                rows.Add(new DetailRow { IsHeader = true, HeaderTitle = g.Title, HeaderCountText = $"{g.Count}개" });
                for (int i = g.Start; i < g.End && i < items.Count; i++) AddItem(i);
            }
        }
        else
        {
            for (int i = 0; i < items.Count; i++) AddItem(i);
        }

        _rows = rows;
        _rowIndexByPath = map;
        ListHost.ItemsSource = rows;
        IconHost.ItemsSource = rows.Where(r => !r.IsHeader).ToList();
        UpdateRowVisuals();
        RefreshEmptyState();
        Dispatcher.BeginInvoke(new Action(MaybeLoadMoreVisible), DispatcherPriority.Background);
    }

    private void ApplyViewMode()
    {
        var full = (_model?.Detail.ViewMode ?? ViewMode.Full) == ViewMode.Full;
        ListHost.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
        IconScroll.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── 행 비주얼 (커서/마크/줄무늬/숨김) ────────────────────────────────

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private void UpdateRowVisuals()
    {
        if (_model is null || _rows.Count == 0) return;
        var pane = _model.Detail;
        var focused = _model.FocusedPane == FocusPane.Detail;
        var accent = TryFindResource("AccentBrush") is SolidColorBrush ab ? ab.Color : Color.FromRgb(0x0A, 0x84, 0xFF);

        Brush cursorBg = focused
            ? Frozen(new SolidColorBrush(Color.FromArgb(217, accent.R, accent.G, accent.B)))   // Accent 0.85
            : Res("SelectionInactiveBrush", Brushes.Gray);
        Brush markBg = Frozen(new SolidColorBrush(Color.FromArgb(56, accent.R, accent.G, accent.B)));   // Accent 0.22
        Brush stripe = Res("ControlFillBrush", Brushes.Transparent);
        Brush clear = Brushes.Transparent;
        Brush primary = Res("TextPrimaryBrush", Brushes.Black);
        Brush secondary = Res("TextSecondaryBrush", Brushes.Gray);
        Brush accentFg = Res("AccentBrush", Brushes.Blue);

        var cursor = pane.Cursor;
        foreach (var row in _rows)
        {
            if (row.IsHeader || row.Item is null) continue;
            var it = row.Item;
            bool isCursor = cursor is not null && string.Equals(it.Path, cursor, StringComparison.OrdinalIgnoreCase);
            bool isMarked = !isCursor && pane.Selection.Contains(it.Path);
            row.RowBackground = isCursor ? cursorBg : isMarked ? markBg : row.Index % 2 == 1 ? stripe : clear;
            row.NameBrush = isCursor ? Brushes.White : isMarked ? accentFg : it.IsHidden ? secondary : primary;
            row.MetaBrush = isCursor ? White85 : secondary;
            row.LabelBackground = isCursor ? cursorBg : isMarked ? markBg : clear;
        }
    }

    // ── 빈/오류 상태 + 상태 표시줄 ───────────────────────────────────────

    private void RefreshEmptyState()
    {
        if (_model is null) return;
        var pane = _model.Detail;
        if (pane.LoadError is { } err)
        {
            EmptyIcon.Text = IconMap.Glyph("exclamationmark.triangle");
            EmptyIcon.Visibility = Visibility.Visible;
            EmptyText.Text = err;
            EmptyState.Visibility = Visibility.Visible;
        }
        else if (pane.Items.Count == 0)
        {
            EmptyIcon.Visibility = Visibility.Collapsed;
            EmptyText.Text = "빈 폴더";
            EmptyState.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyState.Visibility = Visibility.Collapsed;
        }
        StatusCenter.Text = pane.LoadError ?? "";
    }

    private void RefreshStatus()
    {
        if (_model is null) return;
        var pane = _model.Detail;
        var n = pane.Items.Count;
        var m = pane.Selection.Count;
        var text = m > 0 ? $"{n}개 중 {m}개 선택" : $"{n}개 항목";
        if (_model.InternalClipboard is { } clip)
            text += $" · 클립보드: {clip.Paths.Count}개" + (clip.IsCut ? " (잘라냄)" : "");
        StatusLeft.Text = text;
        StatusRight.Text = _model.StatusFreeSpace ?? "";
    }

    // ── 스크롤 ───────────────────────────────────────────────────────────

    private ScrollViewer? GetListScroll()
    {
        if (_listScroll is null)
        {
            ListHost.ApplyTemplate();
            _listScroll = ListHost.Template.FindName("PART_Scroll", ListHost) as ScrollViewer;
        }
        return _listScroll;
    }

    /// <summary>커서 행을 화면 가운데로 (키보드 이동 전용 — 클릭은 suppress).</summary>
    private void ScrollCursorIntoView()
    {
        if (_model is null) return;
        var pane = _model.Detail;
        if (pane.Cursor is null) return;
        if (!_rowIndexByPath.TryGetValue(pane.Cursor, out var rowIdx)) return;

        if (pane.ViewMode == ViewMode.Full)
        {
            var sv = GetListScroll();
            if (sv is null) return;
            var rowH = Math.Max(RowHeight, 1);
            var target = rowIdx * rowH - (sv.ViewportHeight - rowH) / 2;
            sv.ScrollToVerticalOffset(Math.Max(0, target));
        }
        else
        {
            if (rowIdx >= _rows.Count) return;
            var itemIdx = _rows[rowIdx].Index;
            var pitch = CellSize + 8;
            var perRow = Math.Max(1, (int)((Math.Max(IconScroll.ViewportWidth, pitch) - 24) / pitch));
            var line = itemIdx / perRow;
            IconScroll.ScrollToVerticalOffset(Math.Max(0, line * pitch - (IconScroll.ViewportHeight - pitch) / 2));
        }
    }

    private void ListScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) => MaybeLoadMoreVisible();

    /// <summary>typeMode 무한 스크롤 — 마지막 가시 인덱스를 모델에 알림 (스펙 §8).</summary>
    private void MaybeLoadMoreVisible()
    {
        if (_model is null || !_model.Detail.TypeMode) return;
        int last;
        if (_model.Detail.ViewMode == ViewMode.Full)
        {
            var sv = GetListScroll();
            if (sv is null) return;
            last = (int)((sv.VerticalOffset + sv.ViewportHeight) / Math.Max(RowHeight, 1));
        }
        else
        {
            var pitch = CellSize + 8;
            var perRow = Math.Max(1, (int)((Math.Max(IconScroll.ViewportWidth, pitch) - 24) / pitch));
            last = ((int)((IconScroll.VerticalOffset + IconScroll.ViewportHeight) / pitch) + 1) * perRow;
        }
        _model.LoadMoreTypeItemsIfNeeded(Math.Max(0, last));
    }

    // ── 행 마우스: mouse-DOWN 즉시 선택 (스펙 §3.2~3.5) ──────────────────

    private static bool PathEq(string? a, string? b)
        => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private int IndexOfPath(string? path)
    {
        if (_model is null || path is null) return -1;
        var items = _model.Detail.Items;
        for (int i = 0; i < items.Count; i++)
            if (PathEq(items[i].Path, path)) return i;
        return -1;
    }

    private void Row_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_model is null) return;
        if (sender is not FrameworkElement fe || fe.DataContext is not DetailRow row || row.IsHeader || row.Item is null) return;
        var pane = _model.Detail;
        var item = row.Item;
        _model.FocusedPane = FocusPane.Detail;
        e.Handled = true;

        // 더블클릭(0.3초) → 열기
        var now = DateTime.Now;
        if (PathEq(_lastClickPath, item.Path) && (now - _lastClickAt).TotalSeconds <= DoubleClickInterval)
        {
            _lastClickPath = null;
            _lastClickAt = DateTime.MinValue;
            _model.Open(item);
            return;
        }
        _lastClickPath = item.Path;
        _lastClickAt = now;

        // 드래그 가능 여부는 선택 변경 전에 캡처
        _pressDraggable = !item.IsParent && (pane.Selection.Contains(item.Path) || PathEq(pane.Cursor, item.Path));

        // 클릭으로 인한 커서 변경은 자동 스크롤 금지 (PropertyChanged는 동기이므로 블록 동안만 억제)
        _deferredReduce = false;
        _suppressCursorScroll = true;
        try
        {
            var mods = Keyboard.Modifiers;
            if (mods.HasFlag(ModifierKeys.Control))
            {
                pane.ToggleMark(item.Path);
                pane.Cursor = item.Path;
            }
            else if (mods.HasFlag(ModifierKeys.Shift))
            {
                var anchorIdx = IndexOfPath(pane.Cursor);
                if (anchorIdx < 0) anchorIdx = 0;
                _model.SelectRange(anchorIdx, row.Index);
                pane.Cursor = item.Path;
            }
            else if (pane.Selection.Contains(item.Path))
            {
                pane.Cursor = item.Path;
                _deferredReduce = true;   // 축소는 mouse-up으로 연기 (그룹 드래그 허용)
            }
            else
            {
                pane.Cursor = item.Path;
                pane.Selection.Clear();
                pane.SelectionAnchor = item.Path;
                pane.NotifySelectionChanged();
            }
        }
        finally { _suppressCursorScroll = false; }

        _pressRow = row;
        _pressPoint = e.GetPosition(ContentRoot);
        _dragStarted = false;
        _didRubber = false;
        _captureElement = fe;
        fe.CaptureMouse();
    }

    private void Row_MouseMove(object sender, MouseEventArgs e)
    {
        if (_model is null || _pressRow is null || _captureElement is null) return;
        if (!ReferenceEquals(sender, _captureElement) || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(ContentRoot);
        var dx = pos.X - _pressPoint.X;
        var dy = pos.Y - _pressPoint.Y;
        if (!_dragStarted && !_didRubber && Math.Sqrt(dx * dx + dy * dy) <= 4) return;

        if (_pressDraggable)
        {
            if (_dragStarted) return;
            _dragStarted = true;
            var urls = DragUrls(_pressRow.Item!);
            var capture = _captureElement;
            capture.ReleaseMouseCapture();   // LostMouseCapture에서 press 상태 정리
            if (urls.Count > 0)
            {
                var data = new DataObject(DataFormats.FileDrop, urls.ToArray());
                try { DragDrop.DoDragDrop(capture, data, DragDropEffects.Copy | DragDropEffects.Move); }
                catch { /* 드래그 실패는 무시 */ }
            }
        }
        else if (_model.Detail.ViewMode == ViewMode.Full)
        {
            // 선택되지 않은 행에서 드래그 → 러버밴드 (목록 보기 전용)
            _didRubber = true;
            _suppressCursorScroll = true;
            try { _model.SelectRange(_pressRow.Index, ItemIndexAtPoint(pos)); }
            finally { _suppressCursorScroll = false; }
        }
    }

    private void Row_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressRow is null) return;
        var row = _pressRow;
        var reduce = !_dragStarted && !_didRubber && _deferredReduce && Keyboard.Modifiers == ModifierKeys.None;
        _captureElement?.ReleaseMouseCapture();
        if (reduce && _model is not null && row.Item is not null)
        {
            var pane = _model.Detail;
            if (pane.Selection.Count > 0)
            {
                _suppressCursorScroll = true;
                try
                {
                    pane.Cursor = row.Item.Path;
                    pane.Selection.Clear();
                    pane.SelectionAnchor = row.Item.Path;
                    pane.NotifySelectionChanged();
                }
                finally { _suppressCursorScroll = false; }
            }
        }
        ClearPress();
        e.Handled = true;
    }

    private void Row_LostMouseCapture(object sender, MouseEventArgs e) => ClearPress();

    private void ClearPress()
    {
        _pressRow = null;
        _pressDraggable = false;
        _deferredReduce = false;
        _didRubber = false;
        _captureElement = null;
    }

    /// <summary>드래그 페이로드 — 선택에 포함된 행이면 마크 전체, 아니면 그 행 하나 (스펙 3.5).</summary>
    private List<string> DragUrls(FileItem item)
    {
        if (_model is null || item.IsParent) return new List<string>();
        var pane = _model.Detail;
        if (pane.Selection.Contains(item.Path))
            return pane.Items.Where(i => !i.IsParent && pane.Selection.Contains(i.Path)).Select(i => i.Path).ToList();
        return new List<string> { item.Path };
    }

    // ── 행 우클릭 메뉴 (스펙 §5.1) ──────────────────────────────────────

    private void Row_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_model is null) return;
        if (sender is not FrameworkElement fe || fe.DataContext is not DetailRow row || row.IsHeader || row.Item is null) return;
        _model.FocusedPane = FocusPane.Detail;
        var menu = BuildRowMenu(row.Item);
        menu.PlacementTarget = fe;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>항목이 선택에 없으면 단일 선택으로 만든 뒤 동작 실행 (스펙 5.1 selectIfNeeded).</summary>
    private void SelectIfNeeded(FileItem item)
    {
        if (_model is null) return;
        var pane = _model.Detail;
        if (pane.Selection.Contains(item.Path)) return;
        pane.Cursor = item.Path;
        pane.Selection.Clear();
        pane.SelectionAnchor = item.Path;
        pane.NotifySelectionChanged();
    }

    /// <summary>우클릭 대상 — 선택에 포함되면 마크 전체, 아니면 그 항목 하나.</summary>
    private List<string> ContextTargetPaths(FileItem item)
    {
        if (_model is null || item.IsParent) return new List<string>();
        var pane = _model.Detail;
        if (pane.Selection.Contains(item.Path))
            return pane.Items.Where(i => !i.IsParent && pane.Selection.Contains(i.Path)).Select(i => i.Path).ToList();
        return new List<string> { item.Path };
    }

    private ContextMenu BuildRowMenu(FileItem item)
    {
        var model = _model!;
        var pane = model.Detail;
        var isFolder = item.IsDirectory && !item.IsBundle;
        var menu = new ContextMenu();

        menu.Items.Add(Mi("열기", () => model.Open(item)));
        if (isFolder)
            menu.Items.Add(Mi("새 탭에서 열기", () => model.NewTab(item.Path), "Ctrl+T"));
        if (pane.IsVirtualMode)
            menu.Items.Add(Mi("위치로 이동", () => model.RevealInList(item)));
        menu.Items.Add(Mi("미리 보기", () => { pane.Cursor = item.Path; model.ViewSelected(); }, "Space"));
        menu.Items.Add(Mi("탐색기에서 보기", () => SystemActions.RevealInExplorer(item.Path)));
        menu.Items.Add(Mi("정보 가져오기", () => { SelectIfNeeded(item); model.GetInfoSelection(); }, "Ctrl+I"));
        menu.Items.Add(BuildTagMenu(item));
        menu.Items.Add(new Separator());

        menu.Items.Add(Mi("복사", () => { SelectIfNeeded(item); model.CopySelection(); }, "Ctrl+C"));
        menu.Items.Add(Mi("경로 복사", () => { SelectIfNeeded(item); model.CopySelectedPath(); }, "Ctrl+Shift+C"));
        menu.Items.Add(Mi("잘라내기", () => { SelectIfNeeded(item); model.CutSelection(); }, "Ctrl+X"));
        var paste = Mi("붙여넣기", model.Paste, "Ctrl+V");
        paste.IsEnabled = HasPasteboard();
        menu.Items.Add(paste);
        menu.Items.Add(Mi("복제", () => { SelectIfNeeded(item); model.Duplicate(); }, "Ctrl+D"));
        menu.Items.Add(Mi("이름 변경…", () => { pane.Cursor = item.Path; model.RequestRename(); }, "F2"));
        if (isFolder)
        {
            if (model.IsFavorite(item.Path))
                menu.Items.Add(Mi("즐겨찾기에서 제거", () => model.RemoveFavorite(item.Path)));
            else
                menu.Items.Add(Mi("즐겨찾기에 추가", () => model.AddFavorite(item.Path)));
            if (model.IsDirectlyExcluded(item.Path))
                menu.Items.Add(Mi("AI 정리 예외 해제", () => model.RemoveExcludedFolder(item.Path)));
            else
                menu.Items.Add(Mi("AI 정리 예외 폴더로 등록", () => model.AddExcludedFolder(item.Path)));
        }
        menu.Items.Add(new Separator());

        menu.Items.Add(Mi("압축", () => { SelectIfNeeded(item); model.CompressSelected(); }));
        if (string.Equals(item.Ext, "zip", StringComparison.OrdinalIgnoreCase))
            menu.Items.Add(Mi("압축 풀기", () => { pane.Cursor = item.Path; model.ExtractSelected(); }));
        menu.Items.Add(new Separator());

        menu.Items.Add(Mi("터미널 열기", () =>
        {
            var dir = item.IsDirectory ? item.Path : Path.GetDirectoryName(item.Path) ?? model.SelectedFolder;
            SystemActions.OpenTerminal(dir, model.TerminalApp);
        }));
        var del = Mi("휴지통으로 이동", () => { SelectIfNeeded(item); model.RequestDelete(); }, "Delete");
        del.Foreground = DestructiveRed;
        menu.Items.Add(del);
        return menu;
    }

    /// <summary>태그 서브메뉴 — 7색 토글 + 태그 모두 제거. 렌더 시 상태를 읽지 않음 (스펙 5.4).</summary>
    private MenuItem BuildTagMenu(FileItem item)
    {
        var tagMenu = new MenuItem { Header = "태그" };
        var targets = ContextTargetPaths(item);
        foreach (var tag in TagService.Standard)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = new SolidColorBrush(TagService.ColorFor(tag.ColorIndex)),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            header.Children.Add(new TextBlock { Text = tag.Name, VerticalAlignment = VerticalAlignment.Center });
            var mi = new MenuItem { Header = header };
            var captured = tag;
            mi.Click += (_, _) => Task.Run(() => TagService.Toggle(captured, targets));
            tagMenu.Items.Add(mi);
        }
        tagMenu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "태그 모두 제거" };
        clear.Click += (_, _) => Task.Run(() => TagService.Clear(targets));
        tagMenu.Items.Add(clear);
        return tagMenu;
    }

    private bool HasPasteboard()
    {
        if (_model?.InternalClipboard is not null) return true;
        try { return Clipboard.ContainsFileDropList(); }
        catch { return false; }
    }

    private static MenuItem Mi(string header, Action action, string? gesture = null)
    {
        var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
        mi.Click += (_, _) => action();
        return mi;
    }

    // ── 빈 영역: 클릭 해제 / 러버밴드 / 배경 메뉴 (스펙 §3.4, §3.7, §5.2) ─

    /// <summary>화면 좌표(ContentRoot 기준) → pane.Items 인덱스 (그룹 헤더 행 보정).</summary>
    private int ItemIndexAtPoint(Point p)
    {
        if (_model is null || _rows.Count == 0) return 0;
        var sv = GetListScroll();
        var rowIdx = (int)((p.Y + (sv?.VerticalOffset ?? 0)) / Math.Max(RowHeight, 1));
        rowIdx = Math.Clamp(rowIdx, 0, _rows.Count - 1);
        for (int r = rowIdx; r < _rows.Count; r++)
            if (!_rows[r].IsHeader) return _rows[r].Index;
        for (int r = rowIdx; r >= 0; r--)
            if (!_rows[r].IsHeader) return _rows[r].Index;
        return 0;
    }

    private void Background_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_model is null) return;
        _model.FocusedPane = FocusPane.Detail;
        var pane = _model.Detail;
        if (pane.Selection.Count > 0)
        {
            pane.Selection.Clear();
            pane.NotifySelectionChanged();
        }
        if (pane.ViewMode == ViewMode.Full && pane.Items.Count > 0)
        {
            _bgPress = true;
            _bgBand = false;
            _bgPoint = e.GetPosition(ContentRoot);
            _bgStartIndex = ItemIndexAtPoint(_bgPoint);
            ContentRoot.CaptureMouse();
        }
    }

    private void Background_MouseMove(object sender, MouseEventArgs e)
    {
        if (_model is null || !_bgPress || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(ContentRoot);
        var dx = pos.X - _bgPoint.X;
        var dy = pos.Y - _bgPoint.Y;
        if (!_bgBand && Math.Sqrt(dx * dx + dy * dy) <= 4) return;
        _bgBand = true;
        _suppressCursorScroll = true;
        try { _model.SelectRange(_bgStartIndex, ItemIndexAtPoint(pos)); }
        finally { _suppressCursorScroll = false; }
    }

    private void Background_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_bgPress) return;
        _bgPress = false;
        _bgBand = false;
        ContentRoot.ReleaseMouseCapture();
    }

    private void Background_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _bgPress = false;
        _bgBand = false;
    }

    private void Background_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_model is null) return;
        _model.FocusedPane = FocusPane.Detail;
        var menu = BuildBackgroundMenu();
        menu.PlacementTarget = ContentRoot;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static string SortLabel(SortKey k) => k switch
    {
        SortKey.Name => "이름",
        SortKey.Ext => "확장자",
        SortKey.Size => "크기",
        SortKey.Modified => "수정일",
        SortKey.Created => "생성일",
        SortKey.Kind => "종류",
        _ => "",
    };

    private ContextMenu BuildBackgroundMenu()
    {
        var model = _model!;
        var pane = model.Detail;
        var canEdit = !pane.SearchMode && !pane.RecentsMode && !pane.TagMode && !pane.TypeMode;
        var menu = new ContextMenu();

        if (canEdit)
        {
            menu.Items.Add(Mi("새 폴더", model.RequestNewFolder, "Ctrl+Shift+N"));
            var paste = Mi("붙여넣기", model.Paste, "Ctrl+V");
            paste.IsEnabled = HasPasteboard();
            menu.Items.Add(paste);
            menu.Items.Add(new Separator());
        }

        // 보기 ▸
        var view = new MenuItem { Header = "보기" };
        foreach (var mode in new[] { ViewMode.Full, ViewMode.Icon })
        {
            var mi = Mi(mode.Label(), () => model.SetViewMode(mode));
            mi.IsChecked = pane.ViewMode == mode;
            view.Items.Add(mi);
        }
        menu.Items.Add(view);

        // 정렬 기준 ▸ (ext 제외) + 오름/내림차순
        var sort = new MenuItem { Header = "정렬 기준" };
        foreach (var key in new[] { SortKey.Name, SortKey.Size, SortKey.Kind, SortKey.Modified, SortKey.Created })
        {
            var k = key;
            var mi = Mi(SortLabel(k), () => model.SetSort(k));
            mi.IsChecked = pane.SortKey == k;
            sort.Items.Add(mi);
        }
        sort.Items.Add(new Separator());
        var asc = Mi("오름차순", () => model.SetSort(pane.SortKey, true));
        asc.IsChecked = pane.SortAscending;
        sort.Items.Add(asc);
        var desc = Mi("내림차순", () => model.SetSort(pane.SortKey, false));
        desc.IsChecked = !pane.SortAscending;
        sort.Items.Add(desc);
        menu.Items.Add(sort);

        // 다음으로 그룹화 ▸
        if (canEdit)
        {
            var group = new MenuItem { Header = "다음으로 그룹화" };
            foreach (var key in Enum.GetValues<GroupKey>())
            {
                var k = key;
                var mi = Mi(k.Label(), () => model.SetGroupKey(k));
                mi.IsChecked = pane.GroupKey == k;
                group.Items.Add(mi);
            }
            menu.Items.Add(group);
        }

        if (pane.ViewMode == ViewMode.Full)
            menu.Items.Add(Mi("열 너비 재설정", model.ResetAllColumnWidths));

        var hidden = Mi("숨김 항목 보기", model.ToggleHidden, "Ctrl+H");
        hidden.IsChecked = model.ShowHidden;
        menu.Items.Add(hidden);
        menu.Items.Add(new Separator());
        menu.Items.Add(Mi("새로 고침", model.ReloadDetail, "F5"));
        return menu;
    }

    // ── 드롭 대상 (스펙 §3.6 — Ctrl=복사, 기본=이동) ──────────────────────

    private static bool IsCopyDrop(DragEventArgs e)
        => e.KeyStates.HasFlag(DragDropKeyStates.ControlKey);

    private void Row_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not DetailRow row) return;
        var item = row.Item;
        if (item is null || !item.IsDirectory || item.IsBundle || item.IsParent) return;   // 배경 핸들러로 통과
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = IsCopyDrop(e) ? DragDropEffects.Copy : DragDropEffects.Move;
        e.Handled = true;
    }

    private void Row_Drop(object sender, DragEventArgs e)
    {
        if (_model is null) return;
        if (sender is not FrameworkElement fe || fe.DataContext is not DetailRow row) return;
        var item = row.Item;
        if (item is null || !item.IsDirectory || item.IsBundle || item.IsParent) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        e.Handled = true;
        _model.DropFiles(paths, item.Path, copy: IsCopyDrop(e));
    }

    /// <summary>빈 영역 드롭 = 현재 폴더로. 핸들러는 항상 붙이고 enabled는 내부에서 검사 (스펙 3.6 구현 노트).</summary>
    private bool BackgroundDropEnabled()
    {
        if (_model is null) return false;
        var pane = _model.Detail;
        return !pane.SearchMode && !pane.RecentsMode && !pane.TypeMode;
    }

    private void Background_DragOver(object sender, DragEventArgs e)
    {
        if (!BackgroundDropEnabled() || !e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = IsCopyDrop(e) ? DragDropEffects.Copy : DragDropEffects.Move;
        e.Handled = true;
    }

    private void Background_Drop(object sender, DragEventArgs e)
    {
        if (_model is null || !BackgroundDropEnabled()) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        e.Handled = true;
        _model.DropFiles(paths, _model.Detail.Directory, copy: IsCopyDrop(e));
    }

    // ── 아이콘 보기 썸네일 (셀 실현 시 비동기 로드, 동시 2건 제한) ───────

    private static readonly SemaphoreSlim ThumbGate = new(2, 2);

    private void IconCell_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not DetailRow row) return;
        if (row.IsHeader || row.Item is null || row.ThumbRequested) return;
        row.ThumbRequested = true;
        var item = row.Item;
        if (item.IsDirectory) return;   // 폴더는 셸 아이콘 유지
        var path = item.Path;
        var dispatcher = Dispatcher;
        Task.Run(async () =>
        {
            await ThumbGate.WaitAsync().ConfigureAwait(false);
            ImageSource? thumb;
            try { thumb = IconCache.ThumbnailFor(path, 96); }
            finally { ThumbGate.Release(); }
            if (thumb is null) return;
            dispatcher.BeginInvoke(new Action(() => row.Thumb = thumb));
        });
    }
}

// ── 행 데이터 (목록/아이콘 공용) ─────────────────────────────────────────

/// <summary>상세 목록의 행 하나 — 그룹 헤더이거나 FileItem 행. 비주얼 브러시만 변경 통지.</summary>
public sealed class DetailRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public bool IsHeader { get; init; }
    public string HeaderTitle { get; init; } = "";
    public string HeaderCountText { get; init; } = "";

    public FileItem? Item { get; init; }
    public int Index { get; init; }                  // pane.Items 안의 전역 인덱스

    public string NameText { get; init; } = "";
    public string SizeText { get; init; } = "";
    public string ModifiedText { get; init; } = "";
    public string CreatedText { get; init; } = "";
    public string KindText { get; init; } = "";
    public string? ModifiedTip { get; init; }        // 상대 시간 모드에서만 실제 날짜
    public string? CreatedTip { get; init; }
    public ImageSource? Icon { get; init; }

    private ImageSource? _thumb;
    public ImageSource? Thumb
    {
        get => _thumb;
        set { if (!ReferenceEquals(_thumb, value)) { _thumb = value; Raise(nameof(Thumb)); } }
    }
    public bool ThumbRequested;

    private Brush _rowBackground = Brushes.Transparent;
    public Brush RowBackground
    {
        get => _rowBackground;
        set { if (!ReferenceEquals(_rowBackground, value)) { _rowBackground = value; Raise(nameof(RowBackground)); } }
    }

    private Brush _nameBrush = Brushes.Black;
    public Brush NameBrush
    {
        get => _nameBrush;
        set { if (!ReferenceEquals(_nameBrush, value)) { _nameBrush = value; Raise(nameof(NameBrush)); } }
    }

    private Brush _metaBrush = Brushes.Gray;
    public Brush MetaBrush
    {
        get => _metaBrush;
        set { if (!ReferenceEquals(_metaBrush, value)) { _metaBrush = value; Raise(nameof(MetaBrush)); } }
    }

    private Brush _labelBackground = Brushes.Transparent;
    public Brush LabelBackground
    {
        get => _labelBackground;
        set { if (!ReferenceEquals(_labelBackground, value)) { _labelBackground = value; Raise(nameof(LabelBackground)); } }
    }
}

/// <summary>그룹 헤더/항목 행 템플릿 선택.</summary>
public sealed class DetailRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? RowTemplate { get; set; }
    public DataTemplate? HeaderTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
        => item is DetailRow { IsHeader: true } ? HeaderTemplate : RowTemplate;
}

/// <summary>가운데 말줄임("abc…xyz") TextBlock — WPF TextTrimming에 middle이 없어 자체 측정 (스펙 §10).</summary>
public sealed class MiddleEllipsisTextBlock : TextBlock
{
    public static readonly DependencyProperty FullTextProperty = DependencyProperty.Register(
        nameof(FullText), typeof(string), typeof(MiddleEllipsisTextBlock),
        new FrameworkPropertyMetadata("", (d, _) => ((MiddleEllipsisTextBlock)d).Recompute()));

    public string FullText
    {
        get => (string)GetValue(FullTextProperty);
        set => SetValue(FullTextProperty, value);
    }

    public MiddleEllipsisTextBlock()
    {
        // TextBlock.MeasureOverride는 sealed — SizeChanged + 프로퍼티 콜백으로 재계산.
        SizeChanged += (_, _) => Recompute();
    }

    private string _cachedFull = "\0";
    private double _cachedWidth = -1;
    private double _cachedFontSize = -1;
    private bool _updating;

    private void Recompute()
    {
        if (_updating) return;
        _updating = true;
        try
        {
            var full = FullText ?? "";
            var width = ActualWidth;
            if (width <= 0 || double.IsNaN(width))
            {
                if (Text != full) Text = full;
                return;
            }
            if (full == _cachedFull && Math.Abs(width - _cachedWidth) <= 0.5
                && Math.Abs(FontSize - _cachedFontSize) <= 0.1) return;
            _cachedFull = full;
            _cachedWidth = width;
            _cachedFontSize = FontSize;
            var fitted = Fit(full, width);
            if (Text != fitted) Text = fitted;
        }
        finally { _updating = false; }
    }

    private string Fit(string full, double width)
    {
        if (full.Length == 0 || width <= 0) return full;
        if (MeasureWidth(full) <= width) return full;
        int lo = 0, hi = full.Length - 1, best = 0;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (MeasureWidth(Compose(full, mid)) <= width) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return Compose(full, best);
    }

    private static string Compose(string full, int keep)
    {
        if (keep <= 0) return "…";
        var front = (keep + 1) / 2;
        var back = keep / 2;
        return full[..front] + "…" + (back > 0 ? full[^back..] : "");
    }

    private double MeasureWidth(string s)
    {
        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, FontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return ft.WidthIncludingTrailingWhitespace;
    }
}
