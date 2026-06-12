// mac 대응: Sources/XFinder/Views/Sheets.swift AboutSheet (앱 정보 — 그라데이션 헤더 + 담쟁이 시)
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace XFinder.Views.Sheets;

/// <summary>XFinder 정보 시트 — 고정 폭 420, 높이 내용 맞춤. Enter/Esc 닫기.</summary>
public sealed class AboutSheet : SheetWindowBase
{
    private const string Poem =
        "저것은 벽\n" +
        "어쩔 수 없는 벽이라고 우리가 느낄 때\n" +
        "그때 담쟁이는 말없이 그 벽을 오른다\n" +
        "\n" +
        "물 한 방울 없고 씨앗 한 톨 살아남을 수 없는\n" +
        "저것은 절망의 벽이라고 말할 때\n" +
        "담쟁이는 서두르지 않고 앞으로 나아간다\n" +
        "\n" +
        "한 뼘이라도 꼭 여럿이 함께 손을 잡고 올라간다\n" +
        "푸르게 절망을 다 덮을 때까지\n" +
        "바로 그 절망을 잡고 놓지 않는다\n" +
        "\n" +
        "저것은 넘을 수 없는 벽이라고\n" +
        "고개를 떨구고 있을 때\n" +
        "담쟁이 잎 하나는 담쟁이 잎 수천 개를 이끌고\n" +
        "결국 그 벽을 넘는다";

    public AboutSheet(Window owner) : base(owner)
    {
        Title = "XFinder 정보";
        Width = 420;
        SizeToContent = SizeToContent.Height;

        var root = new StackPanel();

        // ── 그라데이션 헤더 (92) ────────────────────────────────────────
        var headerRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(22, 0, 22, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        headerRow.Children.Add(SheetUi.AppIcon(52));

        var titleStack = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(new TextBlock
        {
            Text = "XFinder", FontSize = 20, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = VersionText(), FontSize = 11, Margin = new Thickness(0, 2, 0, 0),
            Foreground = new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)),
        });
        headerRow.Children.Add(titleStack);

        root.Children.Add(new Border
        {
            Height = 92,
            Background = SheetUi.HeaderGradient(),
            Child = headerRow,
        });

        // ── 본문 ────────────────────────────────────────────────────────
        var body = new StackPanel { Margin = new Thickness(24, 18, 24, 18) };

        var tagline = SheetUi.Text("사이드바 + 상세 보기 파일 관리자", 12, FontWeights.Normal, "TextSecondaryBrush");
        tagline.HorizontalAlignment = HorizontalAlignment.Center;
        body.Children.Add(tagline);

        body.Children.Add(DeveloperCard());
        body.Children.Add(PoemBlock());

        var ok = SheetUi.SheetButton("확인", prominent: true);
        ok.HorizontalAlignment = HorizontalAlignment.Stretch;
        ok.Margin = new Thickness(0, 16, 0, 0);
        ok.Click += (_, _) => Close();
        body.Children.Add(ok);

        root.Children.Add(body);
        SetSheetContent(root);

        KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Close(); } };
    }

    private static string VersionText()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? "버전 1.0" : $"버전 {v.Major}.{v.Minor}.{v.Build}";
    }

    private UIElement DeveloperCard()
    {
        var grid = new Grid { Margin = new Thickness(0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 원형 30×30 인디고→파랑 그라데이션 + </> 아이콘
        var circle = new Border
        {
            Width = 30, Height = 30,
            CornerRadius = new CornerRadius(15),
            Background = new LinearGradientBrush(
                Color.FromRgb(0x58, 0x56, 0xD6), Color.FromRgb(0x0A, 0x84, 0xFF),
                new Point(0, 0), new Point(1, 1)),
            Child = new TextBlock
            {
                Text = "</>", FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.Children.Add(circle);

        var nameStack = new StackPanel { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        nameStack.Children.Add(SheetUi.Text("정종수", 13, FontWeights.SemiBold, "TextPrimaryBrush"));
        nameStack.Children.Add(SheetUi.Text("Developer", 11.5, FontWeights.Normal, "TextSecondaryBrush"));
        Grid.SetColumn(nameStack, 1);
        grid.Children.Add(nameStack);

        var email = SheetUi.Text("zjonsu@gmail.com", 11.5, FontWeights.Normal, "AccentBrush");
        email.VerticalAlignment = VerticalAlignment.Center;
        email.Cursor = Cursors.Hand;
        email.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;   // 창 드래그로 흘러가지 않게
            try
            {
                Process.Start(new ProcessStartInfo("mailto:zjonsu@gmail.com") { UseShellExecute = true });
            }
            catch { }
        };
        Grid.SetColumn(email, 2);
        grid.Children.Add(email);

        var card = new Border
        {
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(0, 14, 0, 0),
            Child = grid,
        };
        card.SetResourceReference(Border.BackgroundProperty, "ControlFillBrush");
        return card;
    }

    private UIElement PoemBlock()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };

        var title = SheetUi.Text("담쟁이", 14, FontWeights.SemiBold, "TextPrimaryBrush");
        title.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(title);

        var author = SheetUi.Text("— 도종환", 10, FontWeights.Normal, "TextSecondaryBrush");
        author.HorizontalAlignment = HorizontalAlignment.Center;
        author.Margin = new Thickness(0, 2, 0, 0);
        panel.Children.Add(author);

        // AppleMyungjo 대체: 바탕(Batang) 계열 명조
        var poem = new TextBlock
        {
            Text = Poem,
            FontFamily = new FontFamily("Batang, Noto Serif KR, Malgun Gothic"),
            FontSize = 13,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Opacity = 0.7,
            Margin = new Thickness(0, 10, 0, 0),
        };
        poem.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        panel.Children.Add(poem);
        return panel;
    }
}
