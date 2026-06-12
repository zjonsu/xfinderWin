using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using XFinder.Models;
using XFinder.Services;

namespace XFinder.Views;

/// <summary>
/// 메인 창 — mac RootView + App.swift(메뉴/키 라우팅) 대응.
/// 타이틀바 일체형 툴바, 경로 막대, 탭 바, 전역 키 라우팅, 오버레이(확인/알림/HUD)를 담당.
/// </summary>
public partial class MainWindow : Window
{
    public AppModel Model { get; }

    private PaneTab? _observedTab;
    private bool _suppressSearchEvent;
    private bool _pathEditMode;

    public MainWindow()
    {
        InitializeComponent();
        Model = new AppModel();
        DataContext = Model;

        Model.PropertyChanged += OnModelPropertyChanged;
        Model.Tabs.CollectionChanged += OnTabsChanged;
        Model.SettingsRequested += () => SettingsWindowPresenter.Show(Model);

        SourceInitialized += (_, _) =>
        {
            ThemeService.ApplyChrome(this);
            // 커스텀 WindowChrome 창은 최대화 시 프레임 두께만큼 화면 밖으로 넘침 — 작업 영역으로 보정
            if (PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource src)
                src.AddHook(MaximizeBoundsHook);
        };
        Loaded += OnLoaded;
        StateChanged += (_, _) => BtnMax.Content = WindowState == WindowState.Maximized ? "" : "";
        PreviewKeyDown += OnGlobalKeyDown;
        PreviewTextInput += OnGlobalTextInput;
        PreviewMouseDown += OnGlobalMouseDown;
        PreviewGotKeyboardFocus += (_, e) =>
            Model.TextInputActive = e.NewFocus is System.Windows.Controls.Primitives.TextBoxBase;
        ThemeService.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => ThemeService.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged() => RefreshAll();

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        Model.Bootstrap();
        ObserveActiveTab();
        RefreshAll();
    }

    private void RefreshAll()
    {
        RefreshToolbar();
        RefreshTabBar();
        RefreshSearchRow();
        BuildPathBar();
        UpdateTitle();
    }

    // ── 모델 변경 반응 ───────────────────────────────────────────────────

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppModel.Sheet):
                if (Model.Sheet is not null) SheetPresenter.Present(this, Model);
                break;
            case nameof(AppModel.Confirm):
                RefreshConfirmOverlay();
                break;
            case nameof(AppModel.ConfirmFocus):
                RefreshConfirmFocus();
                break;
            case nameof(AppModel.ErrorMessage):
                if (Model.ErrorMessage is not null) ShowAlert("오류", Model.ErrorMessage);
                break;
            case nameof(AppModel.InfoMessage):
                if (Model.InfoMessage is not null) ShowAlert("XFinder", Model.InfoMessage);
                break;
            case nameof(AppModel.TypeSelectDisplay):
                TypeSelectText.Text = Model.TypeSelectDisplay ?? "";
                TypeSelectHud.Visibility = Model.TypeSelectDisplay is null ? Visibility.Collapsed : Visibility.Visible;
                break;
            case nameof(AppModel.SelectedFolder):
            case nameof(AppModel.Detail):
            case nameof(AppModel.ActiveTabIndex):
                ObserveActiveTab();
                ExitPathEdit();
                RefreshAll();
                break;
            case nameof(AppModel.CanGoBack):
            case nameof(AppModel.CanGoForward):
                BtnBack.IsEnabled = Model.CanGoBack;
                BtnForward.IsEnabled = Model.CanGoForward;
                break;
            case nameof(AppModel.SearchPosition):
                RefreshSearchRow();
                break;
            case nameof(AppModel.ShowHidden):
                RefreshToolbar();
                break;
        }
    }

    /// <summary>활성 탭의 변경(폴더/필터/모드)을 구독해 경로 막대·제목·검색창을 동기화.</summary>
    private void ObserveActiveTab()
    {
        if (_observedTab is not null) _observedTab.PropertyChanged -= OnTabPropertyChanged;
        _observedTab = Model.Detail;
        _observedTab.PropertyChanged += OnTabPropertyChanged;
        SyncSearchBoxes();
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, Model.Detail)) return;
        switch (e.PropertyName)
        {
            case nameof(PaneTab.Directory):
                ExitPathEdit();
                BuildPathBar();
                UpdateTitle();
                RefreshTabBar();
                break;
            case nameof(PaneTab.Items):
                // 폴더 용량 계산 등 비동기 Items 재할당이 경로 입력을 강제 종료하지 않게
                if (!_pathEditMode) BuildPathBar();
                UpdateTitle();
                RefreshTabBar();
                break;
            case nameof(PaneTab.Filter):
                SyncSearchBoxes();
                break;
            case nameof(PaneTab.ViewMode):
                RefreshToolbar();
                break;
        }
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshTabBar();

    private void UpdateTitle()
    {
        var t = Model.Detail.TabTitle;
        if (Model.Detail.Directory.Length <= 3 && !Model.Detail.IsVirtualMode)
            t = DriveLabel(Model.Detail.Directory);
        Title = string.IsNullOrEmpty(t) ? "XFinder" : t;
    }

    private static string DriveLabel(string root)
    {
        try
        {
            var d = new DriveInfo(root);
            var letter = root.TrimEnd('\\');
            return string.IsNullOrEmpty(d.VolumeLabel) ? $"로컬 디스크 ({letter})" : $"{d.VolumeLabel} ({letter})";
        }
        catch { return root; }
    }

    // ── 툴바 ─────────────────────────────────────────────────────────────

    private void RefreshToolbar()
    {
        BtnBack.IsEnabled = Model.CanGoBack;
        BtnForward.IsEnabled = Model.CanGoForward;

        var full = Model.Detail.ViewMode == ViewMode.Full;
        BtnViewMode.Content = full ? "" : "";
        BtnViewMode.FontFamily = (FontFamily)FindResource("IconFontFamily");
        BtnViewMode.ToolTip = full ? "아이콘 보기로 전환 (Ctrl+M)" : "목록 보기로 전환 (Ctrl+M)";

        BtnGroup.Content = "";
        BtnGroup.Foreground = Model.Detail.GroupKey == GroupKey.None
            ? (Brush)FindResource("TextPrimaryBrush")
            : (Brush)FindResource("AccentBrush");
        BtnNewFolder.Content = "";
        BtnActions.Content = "";

        BtnHidden.Content = Model.ShowHidden ? "" : "";
        BtnHidden.ToolTip = Model.ShowHidden ? "숨김 파일 숨기기 (Ctrl+H)" : "숨김 파일 표시 (Ctrl+H)";

        var blocked = Model.AiOrganizeBlocked(Model.SelectedFolder);
        BtnAI.IsEnabled = !blocked;
        BtnAI.ToolTip = AppModel.IsApplicationsLocation(Model.SelectedFolder)
            ? "응용 프로그램 폴더는 AI 파일 정리에서 제외됩니다"
            : AppModel.IsProtectedLocation(Model.SelectedFolder)
                ? "시스템 폴더는 AI 파일 정리에서 제외됩니다"
                : Model.IsExcluded(Model.SelectedFolder)
                    ? "이 폴더는 AI 정리 예외 폴더로 지정되어 정리할 수 없습니다"
                    : "AI 파일 정리 — 프롬프트로 현재 폴더 파일 정리 (로컬 LLM)";
    }

    // ── 최대화 보정 (WM_GETMINMAXINFO) ───────────────────────────────────

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct W32Point { public int X, Y; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct W32Rect { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public W32Point ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public W32Rect rcMonitor, rcWork;
        public int dwFlags;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);

    private static IntPtr MaximizeBoundsHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;
        var hMon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        if (hMon == IntPtr.Zero) return IntPtr.Zero;
        var mi = new MonitorInfo { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(hMon, ref mi)) return IntPtr.Zero;
        var mmi = System.Runtime.InteropServices.Marshal.PtrToStructure<MinMaxInfo>(lParam);
        mmi.ptMaxPosition.X = mi.rcWork.Left - mi.rcMonitor.Left;
        mmi.ptMaxPosition.Y = mi.rcWork.Top - mi.rcMonitor.Top;
        mmi.ptMaxSize.X = mi.rcWork.Right - mi.rcWork.Left;
        mmi.ptMaxSize.Y = mi.rcWork.Bottom - mi.rcWork.Top;
        System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

    private void OnGoBack(object sender, RoutedEventArgs e) => Model.GoBack();
    private void OnGoForward(object sender, RoutedEventArgs e) => Model.GoForward();
    private void OnGoUp(object sender, RoutedEventArgs e) => Model.GoUp();
    private void OnOpenTerminal(object sender, RoutedEventArgs e) => Model.OpenTerminal();
    private void OnHangulFix(object sender, RoutedEventArgs e) => Model.FixDecomposedNames(recursive: false);
    private void OnAIOrganize(object sender, RoutedEventArgs e) => Model.RequestAIOrganize();
    private void OnToggleViewMode(object sender, RoutedEventArgs e) => Model.ToggleViewMode();
    private void OnNewFolder(object sender, RoutedEventArgs e) => Model.RequestNewFolder();
    private void OnToggleHidden(object sender, RoutedEventArgs e) => Model.ToggleHidden();
    private void OnNewTab(object sender, RoutedEventArgs e) => Model.NewTab();

    private void OnHangulMenu(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItemOf("이 폴더의 한글 파일명 복원", () => Model.FixDecomposedNames(false)));
        menu.Items.Add(MenuItemOf("하위 폴더까지 복원", () => Model.FixDecomposedNames(true)));
        menu.PlacementTarget = BtnHangul;
        menu.IsOpen = true;
    }

    private void OnGroupMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var virtualMode = Model.Detail.IsVirtualMode;
        foreach (var key in Enum.GetValues<GroupKey>())
        {
            var item = MenuItemOf(key.Label(), () => Model.SetGroupKey(key));
            item.IsChecked = Model.Detail.GroupKey == key;
            item.IsEnabled = !virtualMode;
            menu.Items.Add(item);
        }
        menu.PlacementTarget = BtnGroup;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnActionMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItemOf("열기", Model.OpenSelected));
        menu.Items.Add(MenuItemOf("미리 보기", Model.ViewSelected, "Space"));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemOf("복사", Model.CopySelection, "Ctrl+C"));
        menu.Items.Add(MenuItemOf("잘라내기", Model.CutSelection, "Ctrl+X"));
        var paste = MenuItemOf("붙여넣기", Model.Paste, "Ctrl+V");
        paste.IsEnabled = Model.InternalClipboard is not null || Clipboard.ContainsFileDropList();
        menu.Items.Add(paste);
        menu.Items.Add(MenuItemOf("복제", Model.Duplicate, "Ctrl+D"));
        menu.Items.Add(MenuItemOf("이름 변경…", Model.RequestRename, "F2"));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemOf("압축", Model.CompressSelected));
        menu.Items.Add(MenuItemOf("압축 풀기", Model.ExtractSelected));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemOf("탐색기에서 보기", Model.RevealInExplorer));
        menu.Items.Add(MenuItemOf("터미널 열기", Model.OpenTerminal));
        menu.Items.Add(new Separator());
        var hangul = new MenuItem { Header = "한글 파일명 복원" };
        hangul.Items.Add(MenuItemOf("이 폴더", () => Model.FixDecomposedNames(false)));
        hangul.Items.Add(MenuItemOf("하위 폴더까지", () => Model.FixDecomposedNames(true)));
        menu.Items.Add(hangul);
        menu.Items.Add(MenuItemOf("AI 파일 정리…", Model.RequestAIOrganize));
        menu.Items.Add(MenuItemOf("프로그램 제거…", Model.RequestUninstall));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemOf("새 창", () => new MainWindow().Show(), "Ctrl+N"));
        menu.Items.Add(MenuItemOf("설정…", Model.OpenSettings, "Ctrl+,"));
        menu.Items.Add(MenuItemOf("키보드 단축키", Model.ShowHelp));
        menu.Items.Add(MenuItemOf("XFinder 사용설명서", () => Model.Sheet = new AppSheet.Manual(), "F1"));
        menu.Items.Add(MenuItemOf("XFinder 정보", () => Model.Sheet = new AppSheet.About()));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemOf("휴지통으로 이동", Model.RequestDelete, "Delete"));
        menu.PlacementTarget = BtnActions;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private static MenuItem MenuItemOf(string header, Action action, string? gesture = null)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
        item.Click += (_, _) => action();
        return item;
    }

    // ── 검색창 ───────────────────────────────────────────────────────────

    private void RefreshSearchRow()
    {
        var below = Model.SearchPosition == SearchBarPosition.Below;
        ToolbarSearchHost.Visibility = below ? Visibility.Collapsed : Visibility.Visible;
        SearchRowHost.Visibility = below ? Visibility.Visible : Visibility.Collapsed;
        SyncSearchBoxes();
    }

    private void SyncSearchBoxes()
    {
        _suppressSearchEvent = true;
        var filter = Model.Detail.Filter;
        if (ToolbarSearchBox.Text != filter) ToolbarSearchBox.Text = filter;
        if (BelowSearchBox.Text != filter) BelowSearchBox.Text = filter;
        BtnClearSearch.Visibility = filter.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _suppressSearchEvent = false;
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSearchEvent) return;
        var text = ((TextBox)sender).Text;
        BtnClearSearch.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Model.UpdateSearch(text);
    }

    private void OnClearSearch(object sender, RoutedEventArgs e) => Model.UpdateSearch("");

    // ── 경로 막대 ────────────────────────────────────────────────────────

    private void BuildPathBar()
    {
        PathBarHost.Children.Clear();
        var pane = Model.Detail;

        if (pane.RecentsMode) { PathBarHost.Children.Add(SpecialBar("", "최근 항목", null)); return; }
        if (pane.TagMode && pane.TagName is not null)
        {
            PathBarHost.Children.Add(SpecialBar(null, pane.TagName, IconMap.TagColorByName(pane.TagName)));
            return;
        }
        if (pane.TypeMode && pane.TypeName is not null)
        {
            PathBarHost.Children.Add(SpecialBar("", Model.TypeStatusText, null));
            return;
        }
        if (_pathEditMode) { PathBarHost.Children.Add(BuildPathEditor()); return; }

        // 배경 없으면 빈 영역이 히트테스트되지 않아 더블클릭 편집 진입 불가
        var dock = new DockPanel { LastChildFill = true, Background = Brushes.Transparent };

        var pencil = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Width = 22, Height = 20, FontSize = 11,
            Content = "",
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            ToolTip = "경로 직접 입력",
        };
        pencil.Click += (_, _) => EnterPathEdit();
        DockPanel.SetDock(pencil, Dock.Right);
        dock.Children.Add(pencil);

        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var strip = new StackPanel { Orientation = Orientation.Horizontal };

        strip.Children.Add(new TextBlock
        {
            Text = "",
            FontFamily = (FontFamily)FindResource("IconFontFamily"),
            FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });

        var segments = PathSegments(pane.Directory);
        for (int i = 0; i < segments.Count; i++)
        {
            var (name, url) = segments[i];
            var last = i == segments.Count - 1;
            var btn = new Button
            {
                Content = name,
                FontSize = 12,
                Padding = new Thickness(3, 1, 3, 1),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = (Brush)FindResource(last ? "TextPrimaryBrush" : "TextSecondaryBrush"),
                Cursor = Cursors.Hand,
                Focusable = false,
            };
            btn.Template = FlatButtonTemplate();
            var target = url;
            btn.Click += (_, _) => Model.Select(target);
            strip.Children.Add(btn);
            if (!last)
            {
                strip.Children.Add(new TextBlock
                {
                    Text = "",
                    FontFamily = (FontFamily)FindResource("IconFontFamily"),
                    FontSize = 8,
                    Foreground = (Brush)FindResource("TextSecondaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 2, 0),
                });
            }
        }
        scroll.Content = strip;
        dock.Children.Add(scroll);

        dock.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) EnterPathEdit(); };
        PathBarHost.Children.Add(dock);
    }

    private static ControlTemplate FlatButtonTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(MarginProperty, new Thickness(3, 1, 3, 1));
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        template.VisualTree = presenter;
        return template;
    }

    private UIElement SpecialBar(string? glyph, string title, Color? dotColor)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        if (dotColor is { } c)
        {
            strip.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 10, Height = 10,
                Fill = new SolidColorBrush(c),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            });
        }
        else if (glyph is not null)
        {
            strip.Children.Add(new TextBlock
            {
                Text = glyph,
                FontFamily = (FontFamily)FindResource("IconFontFamily"),
                FontSize = 11,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            });
        }
        strip.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return strip;
    }

    private static List<(string Name, string Url)> PathSegments(string path)
    {
        var result = new List<(string, string)>();
        try
        {
            var current = AppModel.Standardize(path);
            while (true)
            {
                var name = Path.GetFileName(current.TrimEnd('\\'));
                if (string.IsNullOrEmpty(name))
                {
                    result.Add((DriveLabel(current), current));
                    break;
                }
                result.Add((name, current));
                var parent = Path.GetDirectoryName(current.TrimEnd('\\'));
                if (string.IsNullOrEmpty(parent)) break;
                current = parent;
            }
        }
        catch { }
        result.Reverse();
        return result;
    }

    private void EnterPathEdit()
    {
        _pathEditMode = true;
        BuildPathBar();
    }

    private void ExitPathEdit()
    {
        if (!_pathEditMode) return;
        _pathEditMode = false;
        BuildPathBar();
    }

    private UIElement BuildPathEditor()
    {
        var dock = new DockPanel { LastChildFill = true };
        var box = new TextBox
        {
            Style = (Style)FindResource("PlainTextBox"),
            Text = Model.SelectedFolder,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var go = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Width = 22, Height = 20, FontSize = 13,
            Content = "",
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            ToolTip = "이동",
        };
        void Commit()
        {
            var text = box.Text.Trim();
            _pathEditMode = false;
            if (text.Length > 0) Model.GoToFolderPath(text);
            BuildPathBar();
        }
        go.Click += (_, _) => Commit();
        DockPanel.SetDock(go, Dock.Right);
        dock.Children.Add(go);

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { ExitPathEdit(); e.Handled = true; }
        };
        dock.Children.Add(box);
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return dock;
    }

    // ── 탭 바 ────────────────────────────────────────────────────────────

    private void RefreshTabBar()
    {
        TabBarHost.Visibility = Model.Tabs.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        TabStrip.Children.Clear();
        if (Model.Tabs.Count < 2) return;

        for (int i = 0; i < Model.Tabs.Count; i++)
        {
            var tab = Model.Tabs[i];
            var index = i;
            var active = i == Model.ActiveTabIndex;
            var color = IconMap.TabColor(tab.ColorIndex);

            var cell = new Border
            {
                Height = 22,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(2, 0, 2, 0),
                Padding = new Thickness(4, 0, 4, 0),
                Background = TabFill(color, active ? 0.38 : 0.16),
                Cursor = Cursors.Hand,
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });

            var close = new Button
            {
                Style = (Style)FindResource("IconButton"),
                Width = 16, Height = 16, FontSize = 8, FontWeight = FontWeights.Bold,
                Content = "",
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                ToolTip = "탭 닫기 (Ctrl+W)",
                Visibility = Visibility.Hidden,
                VerticalAlignment = VerticalAlignment.Center,
            };
            close.Click += (_, e) => { e.Handled = true; Model.CloseTab(index); };
            grid.Children.Add(close);

            var title = new TextBlock
            {
                Text = tab.TabTitle,
                FontSize = 11.5,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = (Brush)FindResource(active ? "TextPrimaryBrush" : "TextSecondaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(title, 1);
            grid.Children.Add(title);

            cell.Child = grid;
            cell.MouseEnter += (_, _) =>
            {
                close.Visibility = Visibility.Visible;
                if (index != Model.ActiveTabIndex) cell.Background = TabFill(color, 0.25);
            };
            cell.MouseLeave += (_, _) =>
            {
                close.Visibility = Visibility.Hidden;
                cell.Background = TabFill(color, index == Model.ActiveTabIndex ? 0.38 : 0.16);
            };
            cell.MouseLeftButtonDown += (_, _) => Model.SelectTab(index);

            var menu = new ContextMenu();
            menu.Items.Add(MenuItemOf("새 탭", () => Model.NewTab()));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItemOf("탭 닫기", () => Model.CloseTab(index)));
            var others = MenuItemOf("다른 탭 닫기", () => Model.CloseOtherTabs(index));
            others.IsEnabled = Model.Tabs.Count > 1;
            menu.Items.Add(others);
            cell.ContextMenu = menu;

            TabStrip.Children.Add(cell);
        }
    }

    private static SolidColorBrush TabFill(Color c, double opacity)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }

    // ── 알림(오류/안내) 오버레이 ─────────────────────────────────────────

    private void ShowAlert(string title, string message)
    {
        AlertTitle.Text = title;
        AlertMessage.Text = message;
        AlertOverlay.Visibility = Visibility.Visible;
    }

    private void DismissAlert()
    {
        AlertOverlay.Visibility = Visibility.Collapsed;
        Model.ErrorMessage = null;
        Model.InfoMessage = null;
    }

    private void OnAlertOk(object sender, RoutedEventArgs e) => DismissAlert();
    private void OnAlertScrim(object sender, MouseButtonEventArgs e) => DismissAlert();
    private void OnPanelClickEat(object sender, MouseButtonEventArgs e) => e.Handled = true;

    // ── 확인 다이얼로그 오버레이 ─────────────────────────────────────────

    private void RefreshConfirmOverlay()
    {
        var req = Model.Confirm;
        if (req is null)
        {
            ConfirmOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        ConfirmTitle.Text = req.Title;
        ConfirmMessage.Text = req.Message;
        ConfirmActionBtn.Content = req.ConfirmTitle;
        ConfirmOverlay.Visibility = Visibility.Visible;
        RefreshConfirmFocus();
    }

    private void RefreshConfirmFocus()
    {
        var req = Model.Confirm;
        if (req is null) return;
        var accent = (Brush)FindResource("AccentBrush");

        ApplyConfirmButtonLook(ConfirmActionBtn, req.IsDestructive, focused: Model.ConfirmFocus == 0, accent);
        ApplyConfirmButtonLook(ConfirmCancelBtn, destructive: false, focused: Model.ConfirmFocus == 1, accent);
    }

    private void ApplyConfirmButtonLook(Button btn, bool destructive, bool focused, Brush accent)
    {
        if (btn.Template.FindName("Bg", btn) is Border bg)
        {
            bg.BorderBrush = focused ? accent : Brushes.Transparent;
            bg.BorderThickness = new Thickness(focused ? 3 : 0);
            if (destructive)
            {
                bg.Background = new SolidColorBrush(Color.FromArgb((byte)((focused ? 1.0 : 0.75) * 255), 0xE8, 0x3B, 0x30));
                btn.Foreground = Brushes.White;
            }
            else
            {
                // 직전 destructive 확인의 빨간 스타일이 잔존하지 않게 복원
                bg.Background = (Brush)FindResource("SelectionInactiveBrush");
                btn.Foreground = (Brush)FindResource("TextPrimaryBrush");
            }
        }
        else
        {
            // 템플릿이 아직 적용 전이면 적용 후 재시도
            btn.ApplyTemplate();
            Dispatcher.BeginInvoke(RefreshConfirmFocus, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void OnConfirmAction(object sender, RoutedEventArgs e) => Model.ExecuteConfirm(0);
    private void OnConfirmCancel(object sender, RoutedEventArgs e) => Model.ExecuteConfirm(1);
    private void OnConfirmScrim(object sender, MouseButtonEventArgs e) => Model.CancelConfirm();

    // ── 전역 키 라우팅 (mac KeyboardMonitor 대응) ────────────────────────

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        // 알림 오버레이: Enter/Esc/Space로 닫기
        if (AlertOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key is Key.Enter or Key.Escape or Key.Space) { DismissAlert(); e.Handled = true; }
            return;
        }

        // 확인 다이얼로그: 포커스 이동/실행/취소
        if (Model.Confirm is not null)
        {
            switch (e.Key)
            {
                case Key.Left or Key.Up: Model.MoveConfirmFocus(-1); e.Handled = true; break;
                case Key.Right or Key.Down or Key.Tab: Model.MoveConfirmFocus(+1); e.Handled = true; break;
                case Key.Enter: Model.ExecuteConfirmFocus(); e.Handled = true; break;
                case Key.Escape: Model.CancelConfirm(); e.Handled = true; break;
            }
            return;
        }

        // 텍스트 입력 중에는 가로채지 않음 (검색창/경로 입력 등)
        if (Model.TextInputActive)
        {
            if (e.Key == Key.Escape && (ReferenceEquals(Keyboard.FocusedElement, ToolbarSearchBox)
                || ReferenceEquals(Keyboard.FocusedElement, BelowSearchBox)))
            {
                Model.UpdateSearch("");
                Model.FocusedPane = FocusPane.Detail;
                // ClearFocus는 포커스를 null로 만들어 이후 키 이벤트가 창에 오지 않음 — 창으로 이동
                FocusManager.SetFocusedElement(this, null);
                Keyboard.Focus(this);
                Model.TextInputActive = false;
                e.Handled = true;
            }
            return;
        }

        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Alt 계열 탐색 — 미처리 Alt 조합(Alt+F4, Alt+Space 등)은 시스템 기본 처리에 위임
        if (alt)
        {
            switch (key)
            {
                case Key.Left: Model.GoBack(); e.Handled = true; return;
                case Key.Right: Model.GoForward(); e.Handled = true; return;
                case Key.Up: Model.GoUp(); e.Handled = true; return;
                case Key.Down: Model.OpenSelected(); e.Handled = true; return;
                case Key.Enter: Model.GetInfoSelection(); e.Handled = true; return;   // Alt+Enter = 속성
            }
            return;
        }

        // Ctrl 계열
        if (ctrl)
        {
            switch (key)
            {
                case Key.A: Model.SelectAll(); e.Handled = true; return;
                case Key.C when shift: Model.CopySelectedPath(); e.Handled = true; return;
                case Key.C: Model.CopySelection(); e.Handled = true; return;
                case Key.X: Model.CutSelection(); e.Handled = true; return;
                case Key.V: Model.Paste(); e.Handled = true; return;
                case Key.D: Model.Duplicate(); e.Handled = true; return;
                case Key.N when shift: Model.RequestNewFolder(); e.Handled = true; return;
                case Key.N: new MainWindow().Show(); e.Handled = true; return;
                case Key.G when shift: Model.RequestGoToFolder(); e.Handled = true; return;
                case Key.R: Model.Refresh(); e.Handled = true; return;
                case Key.H: Model.ToggleHidden(); e.Handled = true; return;
                case Key.M: Model.ToggleViewMode(); e.Handled = true; return;
                case Key.T: Model.NewTab(); e.Handled = true; return;
                case Key.W:
                    if (Model.Tabs.Count > 1) Model.CloseCurrentTab();
                    else Close();
                    e.Handled = true; return;
                case Key.Tab: Model.CycleTab(shift ? -1 : +1); e.Handled = true; return;
                case Key.I: Model.GetInfoSelection(); e.Handled = true; return;
                case Key.OemComma: Model.OpenSettings(); e.Handled = true; return;
                case Key.Down: Model.OpenSelected(); e.Handled = true; return;
            }
            return;
        }

        // 사이드바 포커스 시 방향키
        if (Model.FocusedPane == FocusPane.Sidebar)
        {
            switch (key)
            {
                case Key.Up: Model.MoveSidebarSelection(-1); e.Handled = true; return;
                case Key.Down: Model.MoveSidebarSelection(+1); e.Handled = true; return;
                case Key.Right: Model.ExpandSidebarSelection(); e.Handled = true; return;
                case Key.Left: Model.CollapseSidebarSelection(); e.Handled = true; return;
                case Key.Enter: Model.ActivateSelectedSidebar(); e.Handled = true; return;
                case Key.Tab when !ctrl: Model.ToggleFocusedPane(); e.Handled = true; return;
            }
        }

        // 목록 키
        switch (key)
        {
            case Key.Up: if (shift) Model.ExtendCursor(-1); else Model.MoveCursor(-1); e.Handled = true; return;
            case Key.Down: if (shift) Model.ExtendCursor(+1); else Model.MoveCursor(+1); e.Handled = true; return;
            case Key.PageUp: Model.MoveCursor(-VisibleRowEstimate()); e.Handled = true; return;
            case Key.PageDown: Model.MoveCursor(+VisibleRowEstimate()); e.Handled = true; return;
            case Key.Home: if (shift) Model.ExtendCursorToTop(); else Model.CursorToTop(); e.Handled = true; return;
            case Key.End: if (shift) Model.ExtendCursorToBottom(); else Model.CursorToBottom(); e.Handled = true; return;
            case Key.Enter: Model.OpenCursorItem(); e.Handled = true; return;
            case Key.Back: Model.GoUp(); e.Handled = true; return;
            case Key.Space or Key.F3: Model.ViewSelected(); e.Handled = true; return;
            case Key.F1: Model.Sheet = new AppSheet.Manual(); e.Handled = true; return;
            case Key.F2: Model.RequestRename(); e.Handled = true; return;
            case Key.F4: Model.EditSelected(); e.Handled = true; return;
            case Key.F5: Model.Refresh(); e.Handled = true; return;
            case Key.Delete: Model.RequestDelete(); e.Handled = true; return;
            case Key.Tab: Model.ToggleFocusedPane(); e.Handled = true; return;
            case Key.Escape:
                if (Model.Detail.Filter.Length > 0) { Model.UpdateSearch(""); e.Handled = true; }
                return;
        }
    }

    private int VisibleRowEstimate()
    {
        var rowHeight = 22 * Model.ListScale;
        var rows = (int)(DetailPane.ActualHeight / rowHeight);
        return Math.Max(rows - 1, 5);
    }

    private void OnGlobalTextInput(object? sender, TextCompositionEventArgs e)
    {
        if (Model.TextInputActive || Model.Confirm is not null || Model.Sheet is not null) return;
        if (AlertOverlay.Visibility == Visibility.Visible) return;
        if (Model.FocusedPane != FocusPane.Detail) return;
        if (string.IsNullOrEmpty(e.Text)) return;
        if (Model.TypeSelect(e.Text)) e.Handled = true;
    }

    private void OnGlobalMouseDown(object? sender, MouseButtonEventArgs e)
    {
        // 마우스 측면 버튼 = 뒤로/앞으로 (탐색기 관례)
        if (e.ChangedButton == MouseButton.XButton1) { Model.GoBack(); e.Handled = true; }
        else if (e.ChangedButton == MouseButton.XButton2) { Model.GoForward(); e.Handled = true; }
    }
}
