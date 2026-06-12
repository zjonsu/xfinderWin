// mac 대응: Sources/XFinder/Views/RootView.swift SettingsView(501~784행) — 설정 창 3탭(일반/보기/AI) 로직.
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using XFinder.Models;
using XFinder.Services;

namespace XFinder.Views;

/// <summary>
/// 설정 단독 창 — 호출한 창의 AppModel을 캡처해 계속 편집 (설정 대부분은 settings.json 공유라 효과는 전역).
/// 변경 즉시 적용: AppModel 프로퍼티 setter가 저장을 담당한다 (스펙 04 §12 저장 패턴).
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppModel _model;
    private readonly bool _hasWt;
    private readonly List<CheckBox> _recentChecks = new();
    private bool _syncing;

    public SettingsWindow(AppModel model)
    {
        _model = model;
        _hasWt = SystemActions.FindWindowsTerminal() is not null;
        InitializeComponent();

        AssignGlyphs();
        BuildRecentsChecks();

        _model.PropertyChanged += OnModelPropertyChanged;
        SourceInitialized += (_, _) => ThemeService.ApplyChrome(this);
        // 다른 창에서 예외 폴더 등록/탭 변경 후 돌아왔을 때 동기화 (List 프로퍼티는 변경 알림 없음)
        Activated += (_, _) => { RefreshDefaultTabs(); RefreshExcluded(); };
        Closed += (_, _) => _model.PropertyChanged -= OnModelPropertyChanged;

        SelectTab(0);   // 탭 상태는 저장 안 함 — 열 때마다 "일반"
        RefreshAll();
    }

    private void AssignGlyphs()
    {
        TabGeneralIcon.Text = IconMap.Glyph("gearshape");
        TabDisplayIcon.Text = IconMap.Glyph("rectangle.grid.1x2");
        TabAiIcon.Text = IconMap.Glyph("sparkles");
        IconAppearance.Text = IconMap.Glyph("circle.lefthalf.filled");
        IconTerminal.Text = IconMap.Glyph("terminal");
        IconDefaultTabs.Text = IconMap.Glyph("rectangle.split.3x1");
        IconListScale.Text = IconMap.Glyph("textformat.size.larger");
        BtnScaleMinus.Content = IconMap.Glyph("textformat.size.smaller");
        BtnScalePlus.Content = IconMap.Glyph("textformat.size.larger");
        IconDateStyle.Text = IconMap.Glyph("calendar.badge.clock");
        IconSearchPos.Text = IconMap.Glyph("magnifyingglass");
        IconCalcSizes.Text = IconMap.Glyph("sum");
        IconRecents.Text = IconMap.Glyph("clock");
        IconAiModel.Text = IconMap.Glyph("sparkles");
        IconExcluded.Text = IconMap.Glyph("hand.raised");
    }

    // ── 모델 변경 반응 (Dispatcher 안전) ─────────────────────────────────

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnModelPropertyChanged(sender, e));
            return;
        }
        switch (e.PropertyName)
        {
            case nameof(AppModel.Appearance): RefreshAppearance(); break;
            case nameof(AppModel.TerminalApp): RefreshTerminal(); break;
            case nameof(AppModel.DateStyle): RefreshDate(); break;
            case nameof(AppModel.SearchPosition): RefreshSearch(); break;
            case nameof(AppModel.ListScale): RefreshListScale(); break;
            case nameof(AppModel.CalculateFolderSizes): RefreshCalc(); break;
            case nameof(AppModel.RecentsCategories): RefreshRecents(); break;
            case nameof(AppModel.AiProviderRaw): RefreshAiProvider(); break;
            case nameof(AppModel.GeminiApiKey):
            case nameof(AppModel.GeminiModel):
            case nameof(AppModel.OllamaBaseUrl):
            case nameof(AppModel.OllamaModel): RefreshAiFields(); break;
        }
    }

    private void RefreshAll()
    {
        RefreshAppearance();
        RefreshTerminal();
        RefreshDefaultTabs();
        RefreshListScale();
        RefreshDate();
        RefreshSearch();
        RefreshCalc();
        RefreshRecents();
        RefreshAiProvider();
        RefreshAiFields();
        RefreshExcluded();
    }

    // ── 탭 전환 ──────────────────────────────────────────────────────────

    private void OnTabGeneral(object sender, RoutedEventArgs e) => SelectTab(0);
    private void OnTabDisplay(object sender, RoutedEventArgs e) => SelectTab(1);
    private void OnTabAi(object sender, RoutedEventArgs e) => SelectTab(2);

    private void SelectTab(int index)
    {
        TabGeneral.IsChecked = index == 0;
        TabDisplay.IsChecked = index == 1;
        TabAi.IsChecked = index == 2;
        PanelGeneral.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        PanelDisplay.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        PanelAi.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── 일반: 화면 모드 ──────────────────────────────────────────────────

    private void OnSegAppearance(object sender, RoutedEventArgs e)
    {
        _model.Appearance = ReferenceEquals(sender, SegLight) ? AppTheme.Light
            : ReferenceEquals(sender, SegDark) ? AppTheme.Dark
            : AppTheme.System;
        RefreshAppearance();
    }

    private void RefreshAppearance()
    {
        SegSystem.IsChecked = _model.Appearance == AppTheme.System;
        SegLight.IsChecked = _model.Appearance == AppTheme.Light;
        SegDark.IsChecked = _model.Appearance == AppTheme.Dark;
    }

    // ── 일반: 터미널 앱 (08-gaps §2.4 정본) ──────────────────────────────

    private void OnSegTerminal(object sender, RoutedEventArgs e)
    {
        _model.TerminalApp = ReferenceEquals(sender, SegTermWt) ? TerminalAppChoice.WindowsTerminal
            : ReferenceEquals(sender, SegTermPs) ? TerminalAppChoice.PowerShell
            : TerminalAppChoice.Auto;
        RefreshTerminal();
    }

    private void RefreshTerminal()
    {
        SegTermAuto.IsChecked = _model.TerminalApp == TerminalAppChoice.Auto;
        SegTermWt.IsChecked = _model.TerminalApp == TerminalAppChoice.WindowsTerminal;
        SegTermPs.IsChecked = _model.TerminalApp == TerminalAppChoice.PowerShell;
        HintTerminal.Text = _model.TerminalApp switch
        {
            TerminalAppChoice.Auto => _hasWt
                ? "자동: Windows Terminal이 설치되어 wt로 엽니다."
                : "자동: Windows Terminal이 없어 PowerShell로 엽니다.",
            TerminalAppChoice.WindowsTerminal => _hasWt
                ? "Windows Terminal로 엽니다."
                : "Windows Terminal이 설치되어 있지 않아 PowerShell로 엽니다.",
            _ => "PowerShell로 엽니다.",
        };
    }

    // ── 일반: 기본 탭 ────────────────────────────────────────────────────

    private void OnSaveTabs(object sender, RoutedEventArgs e)
    {
        _model.SaveCurrentTabsAsDefault();
        RefreshDefaultTabs();
    }

    private void OnClearTabs(object sender, RoutedEventArgs e)
    {
        _model.ClearDefaultTabs();
        RefreshDefaultTabs();
    }

    private void RefreshDefaultTabs()
    {
        var paths = _model.DefaultTabPaths;
        BtnSaveTabs.ToolTip = $"지금 이 창에 열려 있는 탭들의 폴더를 저장합니다 ({_model.Tabs.Count}개)";
        if (paths.Count == 0)
        {
            DefaultTabsList.Visibility = Visibility.Collapsed;
            BtnClearTabs.Visibility = Visibility.Collapsed;
            HintDefaultTabs.Text = "저장된 기본 탭이 없습니다 — 시작할 때 '최근 항목' 하나로 시작합니다.";
        }
        else
        {
            DefaultTabsList.Text = string.Join("  ·  ", paths.Select(DisplayFolderName));
            DefaultTabsList.Visibility = Visibility.Visible;
            BtnClearTabs.Visibility = Visibility.Visible;
            HintDefaultTabs.Text = $"시작할 때 위 {paths.Count}개 폴더가 탭으로 자동으로 열립니다.";
        }
    }

    /// <summary>폴더 표시명 — 드라이브 루트("C:\")는 그대로.</summary>
    private static string DisplayFolderName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\'));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    // ── 보기: 파일 목록 크기 ─────────────────────────────────────────────

    private void OnScaleSlider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing) return;
        _model.ListScale = Math.Round(e.NewValue * 20) / 20;
    }

    private void OnScaleMinus(object sender, RoutedEventArgs e)
        => _model.ListScale = Math.Max(0.8, Math.Round((_model.ListScale - 0.05) * 20) / 20);

    private void OnScalePlus(object sender, RoutedEventArgs e)
        => _model.ListScale = Math.Min(1.8, Math.Round((_model.ListScale + 0.05) * 20) / 20);

    private void RefreshListScale()
    {
        _syncing = true;
        ScaleSlider.Value = _model.ListScale;
        ListScaleValue.Text = $"{(int)Math.Round(_model.ListScale * 100)}%";
        _syncing = false;
    }

    // ── 보기: 날짜 표시 ──────────────────────────────────────────────────

    private void OnSegDate(object sender, RoutedEventArgs e)
    {
        _model.DateStyle = ReferenceEquals(sender, SegDateRelative)
            ? DateDisplayStyle.Relative : DateDisplayStyle.Absolute;
        RefreshDate();
    }

    private void RefreshDate()
    {
        SegDateAbsolute.IsChecked = _model.DateStyle == DateDisplayStyle.Absolute;
        SegDateRelative.IsChecked = _model.DateStyle == DateDisplayStyle.Relative;
        HintDate.Text = _model.DateStyle == DateDisplayStyle.Relative
            ? "수정일·생성일을 ‘1분 전’, ‘1시간 전’, ‘1일 전’ 형식으로 표시합니다. 마우스를 올리면 실제 날짜가 보입니다."
            : "수정일·생성일을 ‘2026-06-09 17:16’ 형식으로 표시합니다.";
    }

    // ── 보기: 검색창 위치 ────────────────────────────────────────────────

    private void OnSegSearch(object sender, RoutedEventArgs e)
    {
        _model.SearchPosition = ReferenceEquals(sender, SegSearchBelow)
            ? SearchBarPosition.Below : SearchBarPosition.Toolbar;
        RefreshSearch();
    }

    private void RefreshSearch()
    {
        SegSearchToolbar.IsChecked = _model.SearchPosition == SearchBarPosition.Toolbar;
        SegSearchBelow.IsChecked = _model.SearchPosition == SearchBarPosition.Below;
        HintSearchPos.Text = _model.SearchPosition == SearchBarPosition.Toolbar
            ? "검색창을 툴바 오른쪽에 표시합니다."
            : "검색창을 툴바 아래 전체 폭의 별도 줄로 표시합니다.";
    }

    // ── 보기: 폴더 용량 계산 ─────────────────────────────────────────────

    private void OnCalcToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        _model.CalculateFolderSizes = ChkCalcSizes.IsChecked == true;
    }

    private void RefreshCalc()
    {
        _syncing = true;
        ChkCalcSizes.IsChecked = _model.CalculateFolderSizes;
        _syncing = false;
    }

    // ── 보기: 최근 항목 표시 종류 (FileSystemService.FileTypeOrder 순서 고정) ──

    private void BuildRecentsChecks()
    {
        foreach (var name in FileSystemService.FileTypeOrder)
        {
            var cb = new CheckBox
            {
                Content = name,
                Style = (Style)FindResource("SettingsCheck"),
                Margin = new Thickness(0, 0, 0, 8),
            };
            var category = name;
            cb.Click += (_, _) => _model.ToggleRecentsCategory(category);
            _recentChecks.Add(cb);
            RecentsPanel.Children.Add(cb);
        }
    }

    private void RefreshRecents()
    {
        _syncing = true;
        foreach (var cb in _recentChecks)
            cb.IsChecked = _model.RecentsCategories.Contains((string)cb.Content);
        _syncing = false;
    }

    // ── AI: 제공자 ───────────────────────────────────────────────────────

    private void OnSegAiProvider(object sender, RoutedEventArgs e)
    {
        _model.AiProviderRaw = ReferenceEquals(sender, SegAiOllama) ? "ollama" : "gemini";
        RefreshAiProvider();
    }

    private void RefreshAiProvider()
    {
        var ollama = _model.AiProviderRaw == "ollama";
        SegAiOllama.IsChecked = ollama;
        SegAiGemini.IsChecked = !ollama;
        OllamaPanel.Visibility = ollama ? Visibility.Visible : Visibility.Collapsed;
        GeminiPanel.Visibility = ollama ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── AI: 키/모델 입력 (변경 즉시 저장 — setter가 저장) ────────────────

    private void OnGeminiKeyChanged(object sender, RoutedEventArgs e)
    {
        GeminiKeyPlaceholder.Visibility = GeminiKeyBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_syncing) return;
        _model.GeminiApiKey = GeminiKeyBox.Password;
    }

    private void OnGeminiModelChanged(object sender, TextChangedEventArgs e)
    {
        GeminiModelPlaceholder.Visibility = GeminiModelBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_syncing) return;
        _model.GeminiModel = GeminiModelBox.Text;
    }

    private void OnOllamaUrlChanged(object sender, TextChangedEventArgs e)
    {
        OllamaUrlPlaceholder.Visibility = OllamaUrlBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_syncing) return;
        _model.OllamaBaseUrl = OllamaUrlBox.Text;
    }

    private void OnOllamaModelChanged(object sender, TextChangedEventArgs e)
    {
        OllamaModelPlaceholder.Visibility = OllamaModelBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_syncing) return;
        _model.OllamaModel = OllamaModelBox.Text;
    }

    private void OnOllamaDefaults(object sender, RoutedEventArgs e)
    {
        _model.OllamaBaseUrl = "http://localhost:11434";
        _model.OllamaModel = "gemma4:latest";
        RefreshAiFields();
    }

    private void RefreshAiFields()
    {
        _syncing = true;
        if (GeminiKeyBox.Password != _model.GeminiApiKey) GeminiKeyBox.Password = _model.GeminiApiKey;
        if (GeminiModelBox.Text != _model.GeminiModel) GeminiModelBox.Text = _model.GeminiModel;
        if (OllamaUrlBox.Text != _model.OllamaBaseUrl) OllamaUrlBox.Text = _model.OllamaBaseUrl;
        if (OllamaModelBox.Text != _model.OllamaModel) OllamaModelBox.Text = _model.OllamaModel;
        _syncing = false;
    }

    // ── AI: 정리 예외 폴더 목록 ──────────────────────────────────────────

    private void RefreshExcluded()
    {
        var paths = _model.ExcludedPaths.ToList();
        ExcludedCount.Text = $"{paths.Count}개";
        ExcludedList.Children.Clear();
        var any = paths.Count > 0;
        ExcludedEmptyHint.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        ExcludedScroll.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        ExcludedHint.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        foreach (var p in paths)
            ExcludedList.Children.Add(BuildExcludedRow(p));
    }

    private UIElement BuildExcludedRow(string path)
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0, 0, 0, 4),
        };
        row.SetResourceReference(Border.BackgroundProperty, "ControlFillBrush");

        var dock = new DockPanel { LastChildFill = true };

        var icon = new TextBlock
        {
            Text = IconMap.Glyph("folder.fill"),
            FontFamily = (FontFamily)FindResource("IconFontFamily"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0),
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);

        var remove = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Width = 20, Height = 20, FontSize = 11,
            Content = IconMap.Glyph("xmark.circle.fill"),
            ToolTip = "예외 해제",
            VerticalAlignment = VerticalAlignment.Center,
        };
        remove.SetResourceReference(ForegroundProperty, "TextSecondaryBrush");
        remove.Click += (_, _) =>
        {
            _model.RemoveExcludedFolder(path);
            RefreshExcluded();
        };
        DockPanel.SetDock(remove, Dock.Right);
        dock.Children.Add(remove);

        var reveal = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Width = 20, Height = 20, FontSize = 10,
            Content = IconMap.Glyph("magnifyingglass"),
            ToolTip = "탐색기에서 보기",
            VerticalAlignment = VerticalAlignment.Center,
        };
        reveal.SetResourceReference(ForegroundProperty, "TextSecondaryBrush");
        reveal.Click += (_, _) => SystemActions.RevealInExplorer(path);
        DockPanel.SetDock(reveal, Dock.Right);
        dock.Children.Add(reveal);

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameText = new TextBlock
        {
            Text = DisplayFolderName(path),
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        nameText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        textStack.Children.Add(nameText);
        var pathText = new TextBlock
        {
            Text = path,
            FontSize = 9,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        pathText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        textStack.Children.Add(pathText);
        dock.Children.Add(textStack);

        row.Child = dock;
        return row;
    }
}
