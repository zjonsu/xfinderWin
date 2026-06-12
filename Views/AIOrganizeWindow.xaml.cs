// mac 소스 대응: Sources/XFinder/Views/AIOrganizeSheet.swift (5단계 상태 머신 + LocalAIGuideView/GeminiGuideView 팝오버)
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using XFinder.Models;
using XFinder.Services;

namespace XFinder.Views;

/// <summary>
/// AI 파일 정리 시트 — 모달 창 480×600 고정.
/// input → loading → preview / empty / error 상태 머신. 닫힐 때 model.Sheet = null.
/// </summary>
public partial class AIOrganizeWindow : Window
{
    private enum Phase { Input, Loading, Preview, Empty, Error }

    /// <summary>빠른 예시 칩 (원문 그대로 — 곡선 따옴표 유지).</summary>
    private static readonly string[] Examples =
    {
        "확장자 종류별로 폴더를 만들어 정리해줘",
        "이미지 파일만 ‘이미지’ 폴더로 모아줘",
        "날짜(연-월)별 폴더로 분류해줘",
        "스크린샷을 ‘스크린샷’ 폴더로 옮겨줘",
    };

    private readonly AppModel _model;
    private readonly List<string> _files;          // 시트 오픈 시점 스냅숏 (헤더 카운트·LLM 입력·검증 공용)
    private readonly AIProvider _provider;
    private Phase _phase = Phase.Input;
    private AIPlan? _plan;
    private CancellationTokenSource? _cts;
    private Popup? _popover;
    private readonly List<Border> _chips = new();

    public AIOrganizeWindow(Window owner, AppModel model)
    {
        InitializeComponent();
        _model = model;
        DataContext = model;
        Owner = owner;
        _files = model.CurrentFolderEntries(300);
        _provider = AIProviderExtensions.FromRawValue(model.AiProviderRaw);

        SourceInitialized += (_, _) => ThemeService.ApplyChrome(this);
        ThemeService.ThemeChanged += OnThemeChanged;
        Closed += OnWindowClosed;
        Loaded += (_, _) =>
        {
            if (_phase == Phase.Input)
                Dispatcher.BeginInvoke(() => InputBox.Focus(), DispatcherPriority.Input);
        };

        InitIcons();
        HeaderSubtitle.Text = $"‘{FolderDisplayName()}’ · 항목 {_files.Count}개 · {_provider.Label()}";
        if (_provider == AIProvider.Ollama)
        {
            PrivacyIcon.Text = "";   // lock.shield
            PrivacyText.Text = "로컬 LLM(Ollama)으로 처리 — 파일이 외부로 나가지 않습니다.";
        }
        else
        {
            PrivacyIcon.Text = "";   // cloud
            PrivacyText.Text = "Gemini(클라우드)로 처리 — 파일 ‘이름’이 구글로 전송됩니다.";
        }

        BuildChips();
        RefreshDerivedBrushes();
        SetPhase(Phase.Input);
    }

    private void InitIcons()
    {
        HeaderIcon.Text = IconMap.Glyph("sparkles");
        CloseIcon.Text = IconMap.Glyph("xmark.circle.fill");
        LocalGuideIcon.Text = IconMap.Glyph("questionmark.circle");
        GeminiGuideIcon.Text = "";                       // cloud (IconMap 미등록)
        LocalGuideChevron.Text = IconMap.Glyph("chevron.right");
        GeminiGuideChevron.Text = IconMap.Glyph("chevron.right");
        CheckIcon.Text = "";                             // checkmark.circle.fill → Completed
        DeleteNoteIcon.Text = IconMap.Glyph("info.circle");
        EmptyIcon.Text = "";                             // tray → FolderOpen
        ErrorIcon.Text = "";                             // exclamationmark.triangle.fill → Warning
    }

    // ── 테마 파생 브러시 (Color.primary/secondary/accent .opacity(x) 대응) ──

    private void OnThemeChanged() => Dispatcher.BeginInvoke(RefreshDerivedBrushes);

    private void RefreshDerivedBrushes()
    {
        InputBorder.Background = PrimaryOpacity(0.05);
        InputBorder.BorderBrush = SecondaryOpacity(0.25);
        PreviewListBorder.Background = PrimaryOpacity(0.04);
        LocalGuideLink.Background = AccentOpacity(0.08);
        GeminiGuideLink.Background = AccentOpacity(0.08);
        foreach (var chip in _chips) chip.Background = AccentOpacity(0.12);
    }

    private Color ThemeColor(string key) => (Color)FindResource(key);
    private SolidColorBrush PrimaryOpacity(double o) => Scale(ThemeColor("TextPrimaryColor"), o);
    private SolidColorBrush SecondaryOpacity(double o) => Scale(ThemeColor("TextSecondaryColor"), o);
    private SolidColorBrush AccentOpacity(double o) => Scale(ThemeColor("AccentColor"), o);

    private static SolidColorBrush Scale(Color c, double opacity)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)Math.Round(c.A * opacity), c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    // ── 상태 머신 ────────────────────────────────────────────────────────

    private void SetPhase(Phase phase)
    {
        _phase = phase;
        InputPanel.Visibility = phase == Phase.Input ? Visibility.Visible : Visibility.Collapsed;
        LoadingPanel.Visibility = phase == Phase.Loading ? Visibility.Visible : Visibility.Collapsed;
        PreviewPanel.Visibility = phase == Phase.Preview ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = phase == Phase.Empty ? Visibility.Visible : Visibility.Collapsed;
        ErrorPanel.Visibility = phase == Phase.Error ? Visibility.Visible : Visibility.Collapsed;
        if (phase == Phase.Input)
            Dispatcher.BeginInvoke(() => InputBox.Focus(), DispatcherPriority.Input);   // 비동기 포커스
    }

    private string FolderDisplayName()
    {
        var p = _model.SelectedFolder;
        var name = Path.GetFileName(p.TrimEnd('\\'));
        return string.IsNullOrEmpty(name) ? p : name;
    }

    /// <summary>설정 스냅숏 — AppModel 프로퍼티가 단일 소스 (스펙 §1.4: fallbackToOllama 항상 true).</summary>
    private AIConfig BuildConfig() => new()
    {
        Provider = _provider,
        GeminiApiKey = _model.GeminiApiKey,
        GeminiModel = _model.EffectiveGeminiModel,
        OllamaBaseUrl = _model.EffectiveOllamaBaseUrl,
        OllamaModel = _model.EffectiveOllamaModel,
        FallbackToOllama = true,
    };

    // ── input ────────────────────────────────────────────────────────────

    private void BuildChips()
    {
        ChipsHost.Children.Clear();
        _chips.Clear();
        foreach (var example in Examples)
        {
            var text = new TextBlock { Text = example, FontSize = 11 };
            text.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            var chip = new Border
            {
                CornerRadius = new CornerRadius(999),   // 캡슐
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = Cursors.Hand,
                Child = text,
            };
            var captured = example;
            chip.MouseLeftButtonDown += (_, e) =>
            {
                InputBox.Text = captured;               // 입력 내용 교체
                InputBox.CaretIndex = InputBox.Text.Length;
                InputBox.Focus();
                e.Handled = true;
            };
            _chips.Add(chip);
            ChipsHost.Children.Add(chip);
        }
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = InputBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnAnalyze.IsEnabled = !string.IsNullOrWhiteSpace(InputBox.Text);
    }

    private void OnAnalyze(object sender, RoutedEventArgs e) => RunAnalyze();

    private async void RunAnalyze()
    {
        var instruction = InputBox.Text.Trim();
        if (instruction.Length == 0 || _phase == Phase.Loading) return;

        SetPhase(Phase.Loading);
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            var plan = await AIService.OrganizeAsync(FolderDisplayName(), _files, instruction, BuildConfig(), cts.Token);
            if (cts.IsCancellationRequested) return;    // 창 닫힘 — 결과 폐기
            _plan = plan;
            if (plan.Operations.Count == 0)
            {
                EmptySummary.Text = plan.Summary;
                SetPhase(Phase.Empty);
            }
            else
            {
                BuildPreview(plan);
                SetPhase(Phase.Preview);
            }
        }
        catch (OperationCanceledException) { /* 닫힘/재시도 — 무시 */ }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            var msg = ex.Message;                       // AIError.Message = 스펙 원문
            ErrorText.Text = string.IsNullOrWhiteSpace(msg) ? "오류가 발생했습니다." : msg;
            SetPhase(Phase.Error);
        }
    }

    private void OnBackToInput(object sender, RoutedEventArgs e) => SetPhase(Phase.Input);   // 입력 내용 유지

    // ── preview ──────────────────────────────────────────────────────────

    private void BuildPreview(AIPlan plan)
    {
        PreviewSummary.Text = plan.Summary;

        var moves = plan.Operations.Where(o => o.Action == "move").ToList();
        var deletes = plan.Operations.Where(o => o.IsDelete).ToList();

        var parts = new List<string>();
        if (moves.Count > 0)
        {
            var destCount = moves.Select(m => m.Destination ?? "").Distinct().Count();
            parts.Add($"파일 {moves.Count}개를 {destCount}개 폴더로 이동");
        }
        if (deletes.Count > 0) parts.Add($"{deletes.Count}개를 휴지통으로 이동");
        PreviewLine.Text = parts.Count == 0 ? "적용할 항목이 없습니다." : string.Join(" · ", parts) + "합니다.";

        OpsHost.Children.Clear();

        // 이동 그룹: 목적지별 묶음 — LLM 출력의 첫 등장 순서 유지
        var groups = new List<(string Dest, List<AIOperation> Items)>();
        foreach (var m in moves)
        {
            var dest = m.Destination ?? "";
            var idx = groups.FindIndex(g => g.Dest == dest);
            if (idx < 0) groups.Add((dest, new List<AIOperation> { m }));
            else groups[idx].Items.Add(m);
        }
        var firstGroup = true;
        foreach (var (dest, items) in groups)
        {
            OpsHost.Children.Add(OpGroup(IconMap.Glyph("folder.fill"), dest,
                (Brush)FindResource("AccentBrush"), items, firstGroup));
            firstGroup = false;
        }
        if (deletes.Count > 0)
        {
            var red = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30));
            red.Freeze();
            OpsHost.Children.Add(OpGroup("", "휴지통으로 이동 (복구 가능)", red, deletes, firstGroup));
        }

        DeleteNote.Visibility = deletes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private UIElement OpGroup(string glyph, string title, Brush color, List<AIOperation> items, bool first)
    {
        var stack = new StackPanel { Margin = new Thickness(0, first ? 0 : 14, 0, 0) };

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = (FontFamily)FindResource("IconFontFamily"),
            FontSize = 12,
            Foreground = color,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0),
        });
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = color,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        stack.Children.Add(header);

        foreach (var op in items)
            stack.Children.Add(FileRow(op.File));
        return stack;
    }

    private UIElement FileRow(string name)
    {
        var row = new DockPanel { Margin = new Thickness(6, 3, 0, 0), LastChildFill = true };
        var arrow = new TextBlock
        {
            Text = "↳",                                 // arrow.turn.down.right
            FontSize = 9,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0),
        };
        arrow.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        DockPanel.SetDock(arrow, Dock.Left);
        row.Children.Add(arrow);
        var text = new TextBlock
        {
            Text = name,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,   // 1줄 제한 (mac은 중간 생략)
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        row.Children.Add(text);
        return row;
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (_plan is null) return;
        var ops = _plan.Operations
            .Select(o => new AIPlannedOp(o.Action, o.File, o.Destination))
            .ToList();
        _model.ApplyAIPlan(ops);    // 결과 토스트(InfoMessage/ErrorMessage)는 AppModel이 담당
        Close();
    }

    // ── 안내 팝오버 (LocalAIGuideView / GeminiGuideView, 360×440) ─────────

    private void OnLocalGuide(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var m = _model.EffectiveOllamaModel.Trim();
        var steps = new List<UIElement>
        {
            GuideStep(1, "Ollama 설치",
                GuideBody("아래 버튼으로 Ollama 공식 사이트에서 Windows용 앱을 받아 설치하세요."),
                GuideLinkButton("ollama.com/download 열기", "https://ollama.com/download")),
            GuideStep(2, $"‘{m}’ 모델 내려받기",
                GuideBody($"터미널에서 아래 명령으로 현재 설정된 모델({m})을 한 번 받아 두세요. 다른 모델을 쓰려면 설정 → ‘AI 모델’에서 모델 이름을 바꾸면 안내도 그에 맞춰 바뀝니다."),
                CommandRow($"ollama pull {m}", "명령어 복사")),
            GuideStep(3, "Ollama 실행 확인",
                GuideBody("Ollama 앱이 실행 중이면 자동으로 준비됩니다. 수동 실행이 필요하면:"),
                CommandRow("ollama serve", "명령어 복사"),
                GuideBody("받은 모델 목록은 아래 명령으로 확인할 수 있어요."),
                CommandRow("ollama list", "명령어 복사")),
            GuideStep(4, "xFinder에서 로컬 AI 켜기",
                GuideBody($"설정(⚙️) → ‘AI 모델’에서 제공자를 ‘로컬 (Ollama)’로 바꾸세요. 서버 주소와 모델 이름도 같은 화면에서 바꿀 수 있습니다. 이후 AI 파일 정리가 내 PC에서 ‘{m}’ 모델로 처리됩니다.")),
        };
        ShowPopover(LocalGuideLink, BuildGuideShell("", "로컬 AI 설정 방법",
            "Ollama로 내 PC에서 직접 정리 — 파일이 외부로 나가지 않습니다.", steps));
    }

    private void OnGeminiGuide(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var m = _model.EffectiveGeminiModel.Trim();
        var steps = new List<UIElement>
        {
            GuideStep(1, "Google AI Studio에서 API 키 발급",
                GuideBody("구글 계정으로 로그인한 뒤 ‘API 키 만들기(Create API key)’ 버튼을 누르면 무료로 발급됩니다. 신용카드 등록 없이 무료 등급만으로도 파일 정리에는 충분합니다."),
                GuideLinkButton("aistudio.google.com/apikey 열기", "https://aistudio.google.com/apikey")),
            GuideStep(2, "xFinder에 API 키 등록",
                GuideBody("설정(⚙️) → ‘AI 모델’에서 제공자를 ‘Gemini’로 선택하고, 발급받은 키를 ‘Gemini API 키’ 칸에 붙여넣으세요. 키는 이 PC의 설정에만 저장되며 구글 외 다른 곳으로 보내지 않습니다.")),
            GuideStep(3, "모델 확인 (선택)",
                GuideBody($"현재 설정된 모델은 ‘{m}’ 입니다. 같은 화면의 ‘모델’ 칸에서 다른 Gemini 모델로 바꿀 수 있어요. 예:"),
                CommandRow("gemini-flash-latest", "복사"),
                CommandRow("gemini-2.5-flash", "복사")),
            GuideStep(4, "무엇이 전송되나요?",
                GuideBody("정리 분석 시 파일 ‘이름’과 폴더 구조만 Google 서버로 전송됩니다. 파일 내용은 절대 전송되지 않습니다. 파일 이름조차 외부로 보내고 싶지 않다면 ‘로컬 AI(Ollama)’를 사용하세요.")),
        };
        ShowPopover(GeminiGuideLink, BuildGuideShell("", "Gemini 설정 방법",
            "Google Gemini API로 정리 — 파일 ‘이름’만 전송되고 내용은 전송되지 않습니다.", steps));
    }

    private void ShowPopover(UIElement target, UIElement content)
    {
        ClosePopover();
        var shell = new Border
        {
            Width = 360,
            Height = 440,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(8),
            Child = content,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.35 },
        };
        shell.SetResourceReference(Border.BackgroundProperty, "MenuBackgroundBrush");
        shell.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
        var popup = new Popup
        {
            PlacementTarget = target,
            Placement = PlacementMode.Bottom,   // SwiftUI popover(arrowEdge: .top) — 링크 아래로 펼침
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = shell,
        };
        popup.Closed += (_, _) => { if (ReferenceEquals(_popover, popup)) _popover = null; };
        _popover = popup;
        popup.IsOpen = true;
    }

    private void ClosePopover()
    {
        if (_popover is not null) _popover.IsOpen = false;
        _popover = null;
    }

    private UIElement BuildGuideShell(string iconGlyph, string title, string subtitle, IEnumerable<UIElement> steps)
    {
        var dock = new DockPanel { LastChildFill = true };

        var header = new Grid { Margin = new Thickness(16, 13, 12, 13) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new TextBlock
        {
            Text = iconGlyph,
            FontFamily = (FontFamily)FindResource("IconFontFamily"),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        header.Children.Add(icon);

        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleTb = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.Bold };
        titleTb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        titles.Children.Add(titleTb);
        var subTb = new TextBlock
        {
            Text = subtitle,
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };
        subTb.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        titles.Children.Add(subTb);
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);

        var close = new TextBlock
        {
            Text = IconMap.Glyph("xmark.circle.fill"),
            FontFamily = (FontFamily)FindResource("IconFontFamily"),
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = Cursors.Hand,
        };
        close.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        close.MouseLeftButtonDown += (_, e) => { ClosePopover(); e.Handled = true; };
        Grid.SetColumn(close, 2);
        header.Children.Add(close);

        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);

        var divider = new System.Windows.Shapes.Rectangle { Height = 1 };
        divider.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "DividerBrush");
        DockPanel.SetDock(divider, Dock.Top);
        dock.Children.Add(divider);

        var body = new StackPanel { Margin = new Thickness(16) };
        foreach (var step in steps) body.Children.Add(step);
        dock.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = body,
        });
        return dock;
    }

    /// <summary>단계 블록: 번호 원(지름 20, accent) + 제목 12 semibold + 본문.</summary>
    private UIElement GuideStep(int number, string title, params UIElement[] body)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var badge = new Grid { Width = 20, Height = 20, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        var circle = new System.Windows.Shapes.Ellipse();
        circle.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentBrush");
        badge.Children.Add(circle);
        badge.Children.Add(new TextBlock
        {
            Text = number.ToString(),
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        grid.Children.Add(badge);

        var stack = new StackPanel();
        var titleTb = new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 1, 0, 4),
        };
        titleTb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        stack.Children.Add(titleTb);
        foreach (var el in body) stack.Children.Add(el);
        Grid.SetColumn(stack, 1);
        grid.Children.Add(stack);
        return grid;
    }

    private UIElement GuideBody(string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        return tb;
    }

    /// <summary>외부 링크 버튼 (강조·small) — 기본 브라우저로 URL 열기.</summary>
    private UIElement GuideLinkButton(string title, string url)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text = "",   // arrow.up.right.square → OpenInNewWindow
            FontFamily = (FontFamily)FindResource("IconFontFamily"),
            FontSize = 11,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var border = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 0, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = Cursors.Hand,
            Child = panel,
        };
        border.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        border.MouseLeftButtonDown += (_, e) => { OpenUrl(url); e.Handled = true; };
        return border;
    }

    /// <summary>명령 행: 모노스페이스 11pt 선택 가능 텍스트 + 복사 버튼.</summary>
    private UIElement CommandRow(string command, string copyTooltip)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var box = new TextBox
        {
            Text = command,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        box.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        box.SetResourceReference(TextBoxBase.CaretBrushProperty, "TextPrimaryBrush");
        grid.Children.Add(box);

        var copyGlyph = new TextBlock
        {
            Text = "",   // doc.on.doc → Copy
            FontFamily = (FontFamily)FindResource("IconFontFamily"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        copyGlyph.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        var copy = new Border
        {
            Child = copyGlyph,
            Padding = new Thickness(5, 2, 0, 2),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = copyTooltip,
        };
        copy.MouseLeftButtonDown += (_, e) =>
        {
            try { Clipboard.SetText(command); } catch { /* 클립보드 점유 충돌 무시 */ }
            e.Handled = true;
        };
        Grid.SetColumn(copy, 1);
        grid.Children.Add(copy);

        var border = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 0, 0, 6),
            Background = PrimaryOpacity(0.06),
            Child = grid,
        };
        return border;
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* 브라우저 실행 실패 무시 */ }
    }

    // ── 창 동작 / 키 ─────────────────────────────────────────────────────

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch { /* 버튼 떼는 타이밍 레이스 무시 */ }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_popover is { IsOpen: true }) ClosePopover();
            else Close();
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter) return;
        // 입력창에서 Shift+Enter = 줄바꿈, Enter = 정리 분석 (defaultAction 대응)
        if (_phase == Phase.Input && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        switch (_phase)
        {
            case Phase.Input:
                if (BtnAnalyze.IsEnabled) RunAnalyze();
                e.Handled = true;
                break;
            case Phase.Preview:
                OnApply(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Phase.Empty:
            case Phase.Error:
                SetPhase(Phase.Input);
                e.Handled = true;
                break;
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _cts?.Cancel();             // 진행 중 분석 요청 취소 (결과 폐기)
        ClosePopover();
        ThemeService.ThemeChanged -= OnThemeChanged;
        _model.Sheet = null;
    }
}
