// mac 대응: Sources/XFinder/Views/Sheets.swift ManualSheet (사용설명서, ⌘/ → F1) — 키 표기는 Windows 키맵으로 치환
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace XFinder.Views.Sheets;

/// <summary>XFinder 사용설명서 — 고정 660×760, 그라데이션 헤더 + 섹션 11개 스크롤. Esc 닫기.</summary>
public sealed class ManualSheet : SheetWindowBase
{
    private static readonly Color Blue = Color.FromRgb(0x0A, 0x84, 0xFF);
    private static readonly Color Green = Color.FromRgb(0x28, 0xCD, 0x41);
    private static readonly Color Orange = Color.FromRgb(0xFF, 0x95, 0x00);
    private static readonly Color Pink = Color.FromRgb(0xFF, 0x2D, 0x55);
    private static readonly Color Yellow = Color.FromRgb(0xFF, 0xCC, 0x00);
    private static readonly Color Red = Color.FromRgb(0xFF, 0x3B, 0x30);
    private static readonly Color Purple = Color.FromRgb(0xAF, 0x52, 0xDE);
    private static readonly Color Indigo = Color.FromRgb(0x58, 0x56, 0xD6);
    private static readonly Color Gray = Color.FromRgb(0x8E, 0x8E, 0x93);
    private static readonly Color Teal = Color.FromRgb(0x59, 0xAD, 0xC4);

    public ManualSheet(Window owner) : base(owner)
    {
        Title = "XFinder 사용설명서";
        Width = 660; Height = 760;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(96) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        root.Children.Add(BuildHeader());

        var divider = new Border { Height = 1 };
        divider.SetResourceReference(Border.BackgroundProperty, "DividerBrush");
        Grid.SetRow(divider, 1);
        root.Children.Add(divider);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = BuildBody(),
        };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);

        SetSheetContent(root);
    }

    // ── 헤더 ─────────────────────────────────────────────────────────────

    private UIElement BuildHeader()
    {
        var grid = new Grid();

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(22, 0, 22, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(SheetUi.AppIcon(56));
        var titles = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock
        {
            Text = "XFinder 사용설명서", FontSize = 20, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
        });
        titles.Children.Add(new TextBlock
        {
            Text = "사이드바 + 상세 보기 파일 관리자 — 한눈에 보는 전체 안내",
            FontSize = 12, Margin = new Thickness(0, 2, 0, 0),
            Foreground = new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)),
        });
        row.Children.Add(titles);
        grid.Children.Add(row);

        var close = new Button
        {
            Content = IconMap.Glyph("xmark.circle.fill"),
            FontFamily = SheetUi.IconFont(),
            FontSize = 16,
            Width = 30, Height = 30,
            Foreground = new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 12, 12, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = false,
            ToolTip = "닫기 (Esc)",
        };
        var template = new ControlTemplate(typeof(Button));
        var hit = new FrameworkElementFactory(typeof(Border));
        hit.SetValue(Border.BackgroundProperty, Brushes.Transparent);   // 전체 영역 클릭 가능
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        hit.AppendChild(presenter);
        template.VisualTree = hit;
        close.Template = template;
        close.Click += (_, _) => Close();
        grid.Children.Add(close);

        return new Border { Background = SheetUi.HeaderGradient(), Child = grid };
    }

    // ── 본문 (섹션 11개 + 푸터) ──────────────────────────────────────────

    private UIElement BuildBody()
    {
        var body = new StackPanel { Margin = new Thickness(22) };

        body.Children.Add(Section("", Blue, "1. 화면 구성",
            Feature("", Blue, "도구 막대", "창 위쪽 — 뒤로·앞으로·상위 이동, 검색창, CPU/메모리/디스크 사용량"),
            Feature("", Indigo, "사이드바", "왼쪽 — 즐겨찾기와 위치(드라이브) 트리. 클릭해서 폴더로 이동"),
            Feature(IconMap.Glyph("list.bullet"), Teal, "파일 목록", "가운데 — 이름·크기·수정일. 더블클릭으로 열기/폴더 진입"),
            Feature(IconMap.Glyph("info.circle"), Gray, "상태 막대", "창 아래쪽 — 현재 폴더의 항목 수와 선택 정보")));

        body.Children.Add(Section("", Green, "2. 기본 탐색",
            Feature(null, null, "커서 이동", "↑ ↓ 로 항목 사이를 이동하고, PageUp/PageDown · Home/End 로 빠르게 건너뜁니다."),
            Feature(null, null, "폴더 진입 / 열기", "Enter 로 폴더에 들어가거나 파일을 엽니다. 더블클릭도 동일합니다."),
            Feature(null, null, "뒤로 · 앞으로 · 상위", "Alt+← Alt+→ 로 방문 기록을 오가고, Alt+↑ 또는 Backspace 로 상위 폴더로 갑니다."),
            Feature(null, null, "포커스 전환", "Tab 으로 사이드바 ⇄ 파일 목록 포커스를 전환합니다. 사이드바에서는 ↑↓ 이동, → 펼치기, ← 접기."),
            Feature(null, null, "새 창", "Ctrl+N 으로 독립된 새 창을 엽니다. 창마다 탐색·선택·히스토리가 따로 유지됩니다."),
            Feature(null, null, "폴더로 이동", "Ctrl+Shift+G 로 경로를 직접 입력해 그 폴더로 한 번에 이동합니다."),
            Feature(null, null, "탐색기 · 터미널 열기", "‘작업’ 메뉴 또는 우클릭으로 현재/선택 위치를 Windows 탐색기에서 보거나 터미널로 엽니다.")));

        body.Children.Add(Section("", Orange, "3. 파일 작업",
            Feature(null, null, "복사 · 잘라내기 · 붙여넣기", "Ctrl+C / Ctrl+X 로 담고 Ctrl+V 로 붙여넣습니다. 여러 항목을 Ctrl·Shift 클릭으로 선택할 수 있습니다."),
            Feature(null, null, "경로 복사", "Ctrl+Shift+C 로 선택 항목의 전체 경로를 텍스트로 복사합니다(여러 개면 줄바꿈으로 구분). 우클릭 메뉴에도 있습니다."),
            Feature(null, null, "복제", "Ctrl+D 로 같은 폴더에 사본을 만듭니다."),
            Feature(null, null, "이름 변경", "F2 를 누르고 새 이름을 입력한 뒤 Enter."),
            Feature(null, null, "새 폴더", "Ctrl+Shift+N 으로 현재 위치에 새 폴더를 만듭니다."),
            Feature(null, null, "압축 · 압축 풀기", "선택 항목을 우클릭 → 압축(.zip), zip 파일은 우클릭 → 압축 풀기."),
            Feature(null, null, "휴지통으로 이동", "Delete 로 선택 항목을 휴지통에 넣습니다.", red: true),
            Feature(null, null, "프로그램 제거", "설치된 프로그램을 우클릭 → ‘프로그램 제거…’ 하면 설정·캐시·잔여 파일 등 관련 항목을 함께 찾아 체크해 한 번에 휴지통으로 보냅니다(AppCleaner 방식).", red: true)));

        body.Children.Add(Section(IconMap.Glyph("hand.raised"), Pink, "4. 드래그 & 드롭",
            Feature(null, null, "목록에서 끌어 이동", "파일을 폴더 위로 끌어다 놓으면 그 폴더로 이동합니다."),
            Feature(null, null, "사이드바로 끌기", "왼쪽 사이드바의 즐겨찾기·폴더 위로 끌어다 놓아도 이동/복사됩니다."),
            Feature(null, null, "다른 앱으로 내보내기", "파일을 메일·메신저·바탕화면 등 다른 앱으로 끌면 복사됩니다."),
            TipRow("끌어 놓을 때 Ctrl 을 누르고 있으면 ‘이동’ 대신 ‘복사’가 됩니다.")));

        body.Children.Add(Section("", Yellow, "5. 즐겨찾기 & 사이드바",
            Feature(null, null, "즐겨찾기 추가/제거", "폴더를 우클릭 → ‘즐겨찾기에 추가/제거’. 자주 쓰는 위치를 사이드바 맨 위에 고정합니다."),
            Feature(null, null, "폴더 트리 펼치기", "사이드바 항목의 ▶ 화살표로 하위 폴더를 펼치거나 접습니다."),
            Feature(null, null, "위치(드라이브)", "‘위치’ 섹션에서 홈 폴더와 연결된 드라이브/볼륨에 바로 접근합니다.")));

        body.Children.Add(Section(IconMap.Glyph("tag"), Red, "6. 색 태그",
            Feature(null, null, "태그 지정", "파일/폴더를 우클릭 → ‘태그’에서 빨강·주황·노랑·초록·파랑·보라·회색을 켜고 끕니다. 여러 색을 동시에 붙일 수 있습니다."),
            Feature(null, null, "태그 제거", "우클릭 → ‘태그’ → ‘태그 모두 제거’로 선택 항목의 색 태그를 한 번에 지웁니다."),
            Feature(null, null, "태그로 모아보기", "사이드바 ‘태그’ 섹션에서 색을 클릭하면 그 태그가 붙은 파일만 모아 보여줍니다. 우클릭 → ‘위치로 이동’으로 실제 폴더로 갈 수 있습니다.")));

        body.Children.Add(Section(IconMap.Glyph("clock"), Blue, "7. 최근 항목 & 검색",
            Feature(null, null, "최근 항목", "사이드바 ‘최근 항목’은 최근 사용한 파일을 최신순으로 보여줍니다."),
            Feature(null, null, "검색", "도구 막대 오른쪽 검색창에 입력하면 현재 폴더 안을 빠르게 걸러냅니다."),
            Feature(null, null, "위치로 이동", "검색/최근 항목에서 파일을 우클릭 → ‘위치로 이동’ 하면 실제 폴더로 이동합니다.")));

        body.Children.Add(Section(IconMap.Glyph("eye.fill"), Purple, "8. 보기 & 미리보기",
            Feature(null, null, "목록 / 아이콘 전환", "Ctrl+M 으로 목록 보기와 아이콘 보기를 전환합니다. 폴더마다 마지막 보기 방식을 기억합니다."),
            Feature(null, null, "빠른 보기", "Space (또는 F3) 로 선택 파일을 즉시 미리봅니다(이미지·텍스트 등)."),
            Feature(null, null, "기본 앱으로 열기 / 다른 앱으로", "F4 로 기본 앱에서 열거나, 우클릭 → ‘다음으로 열기’에서 앱을 골라(‘기타…’ 포함) 엽니다."),
            Feature(null, null, "정렬", "열 머리글(이름·크기·종류·수정일·생성일)을 클릭하면 그 기준으로 정렬되고, 다시 누르면 오름/내림차순이 바뀝니다. 빈 영역 우클릭 → ‘정렬 기준’에서도 선택할 수 있습니다."),
            Feature(null, null, "화면 모드", "‘설정 ▸ 화면 모드’에서 시스템 · 라이트 · 다크를 전환합니다. 선택은 다음 실행에도 유지됩니다."),
            Feature(null, null, "숨김 파일", "Ctrl+H 로 숨김 파일 표시를 켜고 끕니다.")));

        body.Children.Add(Section(IconMap.Glyph("ellipsis.circle"), Indigo, "9. 우클릭 메뉴",
            Feature(null, null, "항목 메뉴", "파일/폴더를 우클릭하면 열기·다음으로 열기·탐색기에서 보기·태그·복사/경로 복사/잘라내기/붙여넣기·복제·이름 변경·즐겨찾기·압축/풀기·휴지통이 한 메뉴에 모입니다."),
            Feature(null, null, "빈 영역 메뉴", "목록의 빈 공간을 우클릭하면 새 폴더·붙여넣기·보기(목록/아이콘)·정렬 기준·숨김 항목 보기·새로 고침이 나오는 배경 메뉴가 열립니다."),
            TipRow("검색·최근 항목·태그 모아보기처럼 실제 폴더가 아닐 때는 ‘새 폴더·붙여넣기’가 숨겨지고 보기/정렬만 표시됩니다.")));

        body.Children.Add(Section("", Red, "10. 시스템 모니터",
            Feature(IconMap.Glyph("cpu"), Red, "CPU", "도구 막대의 CPU 사용량을 클릭하면 추이 그래프·부하가 큰 프로세스를 봅니다."),
            Feature(IconMap.Glyph("memorychip"), Blue, "메모리", "메모리 사용량을 클릭하면 앱·캐시 등 상세 내역이 열립니다."),
            Feature(IconMap.Glyph("internaldrive"), Purple, "디스크", "디스크 사용량을 클릭하면 용량 분류와 S.M.A.R.T. 상태를 확인합니다.")));

        body.Children.Add(Section("", Gray, "11. 키보드 단축키",
            ShortcutGroup("탐색",
                (new[] { "↑", "↓" }, "커서 이동"),
                (new[] { "Enter" }, "열기 / 폴더 진입"),
                (new[] { "Ctrl", "↓" }, "선택 항목 열기"),
                (new[] { "Alt", "↑" }, "상위 폴더 (또는 Backspace)"),
                (new[] { "Alt", "←" }, "뒤로"),
                (new[] { "Alt", "→" }, "앞으로"),
                (new[] { "Tab" }, "사이드바 ⇄ 목록")),
            ShortcutGroup("파일",
                (new[] { "Ctrl", "C" }, "복사"),
                (new[] { "Ctrl", "Shift", "C" }, "경로 복사"),
                (new[] { "Ctrl", "X" }, "잘라내기"),
                (new[] { "Ctrl", "V" }, "붙여넣기"),
                (new[] { "Ctrl", "D" }, "복제"),
                (new[] { "F2" }, "이름 변경"),
                (new[] { "Delete" }, "휴지통으로"),
                (new[] { "Ctrl", "Shift", "N" }, "새 폴더")),
            ShortcutGroup("보기 · 기타",
                (new[] { "Space" }, "빠른 보기 (F3)"),
                (new[] { "F4" }, "기본 앱으로 열기"),
                (new[] { "Ctrl", "M" }, "목록 / 아이콘"),
                (new[] { "Ctrl", "R" }, "새로고침 (F5)"),
                (new[] { "Ctrl", "H" }, "숨김 파일"),
                (new[] { "Ctrl", "Shift", "G" }, "폴더로 이동"),
                (new[] { "Ctrl", "N" }, "새 창"),
                (new[] { "F1" }, "이 사용설명서"))));

        var footer = SheetUi.Text("XFinder · 정종수", 10, FontWeights.Normal, "TextSecondaryBrush");
        footer.HorizontalAlignment = HorizontalAlignment.Center;
        footer.Margin = new Thickness(0, 0, 0, 6);
        body.Children.Add(footer);
        return body;
    }

    // ── 구성요소 ─────────────────────────────────────────────────────────

    private UIElement Section(string glyph, Color color, string title, params UIElement[] children)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 22) };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(new Border
        {
            Width = 30, Height = 30,
            CornerRadius = new CornerRadius(8),
            Background = new LinearGradientBrush(color, Darken(color, 0.78), new Point(0, 0), new Point(1, 1)),
            Child = new TextBlock
            {
                Text = glyph,
                FontFamily = SheetUi.IconFont(),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        var titleText = SheetUi.Text(title, 17, FontWeights.Bold, "TextPrimaryBrush");
        titleText.Margin = new Thickness(10, 0, 0, 0);
        titleText.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(titleText);
        panel.Children.Add(header);

        var stack = new StackPanel();
        for (int i = 0; i < children.Length; i++)
        {
            if (children[i] is FrameworkElement fe && i < children.Length - 1)
                fe.Margin = new Thickness(fe.Margin.Left, fe.Margin.Top, fe.Margin.Right, 11);
            stack.Children.Add(children[i]);
        }
        var card = new Border
        {
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(12),
            Child = stack,
        };
        card.SetResourceReference(Border.BackgroundProperty, "ControlFillBrush");
        panel.Children.Add(card);
        return panel;
    }

    private UIElement Feature(string? glyph, Color? color, string title, string desc, bool red = false)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (glyph is not null)
        {
            grid.Children.Add(new TextBlock
            {
                Text = glyph,
                FontFamily = SheetUi.IconFont(),
                FontSize = 13,
                Foreground = new SolidColorBrush(color ?? Gray),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 0, 0),
            });
        }
        else if (red)
        {
            grid.Children.Add(new TextBlock
            {
                Text = "•", FontSize = 13, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Red), Margin = new Thickness(4, 0, 0, 0),
            });
        }

        var textStack = new StackPanel();
        var titleText = SheetUi.Text(title, 13, FontWeights.SemiBold, "TextPrimaryBrush", TextWrapping.Wrap);
        if (red) titleText.Foreground = new SolidColorBrush(Red);
        textStack.Children.Add(titleText);
        var descText = SheetUi.Text(desc, 11.5, FontWeights.Normal, "TextSecondaryBrush", TextWrapping.Wrap);
        descText.Margin = new Thickness(0, 2, 0, 0);
        textStack.Children.Add(descText);
        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);
        return grid;
    }

    private UIElement TipRow(string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = "💡",
            FontFamily = new FontFamily("Segoe UI Emoji"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 8, 0),
        });
        var tip = SheetUi.Text(text, 11.5, FontWeights.Medium, "TextPrimaryBrush", TextWrapping.Wrap);
        tip.MaxWidth = 520;
        row.Children.Add(tip);
        return new Border
        {
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xCC, 0x00)),   // yellow 12%
            Child = row,
        };
    }

    private UIElement ShortcutGroup(string title, params (string[] Keys, string Desc)[] rows)
    {
        var panel = new StackPanel();
        var titleText = SheetUi.Text(title, 12, FontWeights.Bold, "TextSecondaryBrush");
        titleText.Margin = new Thickness(0, 0, 0, 6);
        panel.Children.Add(titleText);

        foreach (var (keys, desc) in rows)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var caps = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var key in keys) caps.Children.Add(KeyCap(key));
            grid.Children.Add(caps);

            var descText = SheetUi.Text(desc, 12, FontWeights.Normal, "TextPrimaryBrush");
            descText.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(descText, 1);
            grid.Children.Add(descText);
            panel.Children.Add(grid);
        }
        return panel;
    }

    private UIElement KeyCap(string label)
    {
        var text = SheetUi.Text(label, 11, FontWeights.SemiBold, "TextPrimaryBrush");
        text.TextAlignment = TextAlignment.Center;
        var cap = new Border
        {
            MinWidth = 18,
            Padding = new Thickness(6, 3, 6, 3),
            Margin = new Thickness(0, 0, 4, 0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(0.5),
            Child = text,
            VerticalAlignment = VerticalAlignment.Center,
        };
        cap.SetResourceReference(Border.BackgroundProperty, "ControlFillBrush");
        cap.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeBrush");
        return cap;
    }

    private static Color Darken(Color c, double factor)
        => Color.FromRgb((byte)(c.R * factor), (byte)(c.G * factor), (byte)(c.B * factor));
}
