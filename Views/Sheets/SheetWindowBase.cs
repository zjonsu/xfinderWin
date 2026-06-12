// mac 대응: Sources/XFinder/Views/Sheets.swift — SwiftUI .sheet 공통 컨테이너 (오너 중앙 모달 창)
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using XFinder.Services;

namespace XFinder.Views.Sheets;

/// <summary>
/// 시트 공통 베이스 — WindowStyle=None + DWM 둥근 모서리, 오너 중앙, Esc 닫기, 배경 드래그 이동.
/// 색은 전부 테마 리소스 키(SetResourceReference = DynamicResource)로 적용.
/// </summary>
public abstract class SheetWindowBase : Window
{
    /// <summary>false면 Esc로 닫히지 않음 (진행률 시트).</summary>
    protected bool CloseOnEscape { get; set; } = true;

    protected SheetWindowBase(Window owner)
    {
        Owner = owner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        FontFamily = new FontFamily("Segoe UI Variable, Segoe UI");
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");

        SourceInitialized += (_, _) => ThemeService.ApplyChrome(this);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && CloseOnEscape) { e.Handled = true; Close(); }
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); } catch { /* 마우스 캡처 충돌 무시 */ }
        };
    }

    /// <summary>루트 콘텐츠 — 1px 윤곽선 테두리로 감싼다 (WindowStyle=None 창의 경계).</summary>
    protected void SetSheetContent(UIElement child)
    {
        var border = new Border { Child = child, BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
        border.SetResourceReference(Border.BackgroundProperty, "WindowBackgroundBrush");
        Content = border;
    }
}

/// <summary>시트 공용 UI 빌더 — 테마 브러시는 SetResourceReference로 연결.</summary>
public static class SheetUi
{
    public static TextBlock Text(string text, double size, FontWeight weight, string brushKey,
        TextWrapping wrap = TextWrapping.NoWrap)
    {
        var tb = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = wrap };
        tb.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return tb;
    }

    /// <summary>둥근 테마 입력창 + 플레이스홀더 오버레이.</summary>
    public static Grid InputField(out TextBox box, string placeholder, string initial, double width)
    {
        var grid = new Grid { Width = width };
        var tb = new TextBox { Text = initial, FontSize = 13 };
        if (Application.Current?.TryFindResource("SearchBox") is Style style) tb.Style = style;

        var hint = new TextBlock
        {
            Text = placeholder,
            FontSize = 13,
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0),
            Visibility = initial.Length > 0 ? Visibility.Collapsed : Visibility.Visible,
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        tb.TextChanged += (_, _) =>
            hint.Visibility = tb.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;

        grid.Children.Add(tb);
        grid.Children.Add(hint);
        box = tb;
        return grid;
    }

    /// <summary>시트 버튼 — prominent=악센트, destructive=빨강, 기본=회색 (mac 시트 버튼 톤).</summary>
    public static Button SheetButton(string label, bool prominent = false, bool destructive = false,
        double minWidth = 84)
    {
        var btn = new Button
        {
            Content = label,
            MinWidth = minWidth,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(14, 6, 14, 6),
            BorderThickness = new Thickness(0),
            Focusable = false,
            Cursor = Cursors.Hand,
        };

        var template = new ControlTemplate(typeof(Button));
        var bg = new FrameworkElementFactory(typeof(Border), "Bg");
        bg.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        bg.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        bg.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        bg.AppendChild(presenter);
        template.VisualTree = bg;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.85, "Bg"));
        template.Triggers.Add(hover);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45, "Bg"));
        template.Triggers.Add(disabled);
        btn.Template = template;

        if (destructive)
        {
            btn.Background = new SolidColorBrush(Color.FromRgb(0xE8, 0x3B, 0x30));
            btn.Foreground = Brushes.White;
        }
        else if (prominent)
        {
            btn.SetResourceReference(Control.BackgroundProperty, "AccentBrush");
            btn.Foreground = Brushes.White;
        }
        else
        {
            btn.SetResourceReference(Control.BackgroundProperty, "SelectionInactiveBrush");
            btn.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        }
        return btn;
    }

    /// <summary>우측 정렬 버튼 행 (취소 · 실행).</summary>
    public static StackPanel ButtonRow(params Button[] buttons)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        for (int i = 0; i < buttons.Length; i++)
        {
            if (i > 0) buttons[i].Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(buttons[i]);
        }
        return row;
    }

    /// <summary>About/Manual 헤더 그라데이션 (mac: #368CFD → #7D4FF5, topLeading→bottomTrailing).</summary>
    public static LinearGradientBrush HeaderGradient()
    {
        var brush = new LinearGradientBrush(
            Color.FromRgb(54, 140, 253), Color.FromRgb(125, 79, 245),
            new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        return brush;
    }

    /// <summary>앱 아이콘 — 실행 파일 셸 아이콘, 실패 시 그라데이션 사각형 + 폴더 글리프.</summary>
    public static UIElement AppIcon(double size)
    {
        System.Windows.Media.ImageSource? icon = null;
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
                icon = ShellInterop.GetThumbnail(exe, (int)(size * 2), thumbnailOnly: false);
        }
        catch { }

        UIElement visual;
        if (icon is not null)
        {
            visual = new Image { Source = icon, Width = size, Height = size };
        }
        else
        {
            var glyph = new TextBlock
            {
                Text = Views.IconMap.Glyph("folder"),
                FontFamily = IconFont(),
                FontSize = size * 0.46,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            visual = new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size * 0.22),
                Background = new LinearGradientBrush(
                    Color.FromRgb(0x58, 0x56, 0xD6), Color.FromRgb(0x0A, 0x84, 0xFF),
                    new Point(0, 0), new Point(1, 1)),
                Child = glyph,
            };
        }
        var host = new Border
        {
            Child = visual,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black, Opacity = 0.25, BlurRadius = 6, ShadowDepth = 3, Direction = 270,
            },
        };
        return host;
    }

    public static FontFamily IconFont()
        => Application.Current?.TryFindResource("IconFontFamily") as FontFamily
           ?? new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
}
