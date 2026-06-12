// mac 대응: Sources/XFinder/Views/Sheets.swift ViewerSheet (Quick Look 폴백 → Windows 기본 미리보기, Space/F3)
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XFinder.Models;
using XFinder.Services;

namespace XFinder.Views.Sheets;

/// <summary>
/// 파일 미리보기 — 이미지/텍스트(1MB 컷)/셸 썸네일+파일 정보 폴백. Space/Esc로 닫기.
/// 로딩은 백그라운드(Task.Run), 결과 반영은 await 컨텍스트(UI 스레드).
/// </summary>
public sealed class ViewerSheet : SheetWindowBase
{
    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "gif", "bmp", "tiff", "tif", "heic", "webp", "icns", "ico",
    };

    private readonly FileItem _item;
    private readonly Border _contentHost;

    private sealed record LoadedContent(BitmapImage? Image, string? Text, ImageSource? Thumb);

    public ViewerSheet(Window owner, FileItem item) : base(owner)
    {
        _item = item;
        Title = item.Name;
        Width = 720; Height = 560;
        MinWidth = 420; MinHeight = 320;
        ResizeMode = ResizeMode.CanResize;
        // 보더리스 창 리사이즈 가장자리
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false,
        });

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // 헤더: 이름 + 앱으로 열기/닫기
        var header = new DockPanel { Margin = new Thickness(10) };
        var openBtn = SheetUi.SheetButton("앱으로 열기", minWidth: 0);
        openBtn.FontSize = 12;
        openBtn.Padding = new Thickness(12, 4, 12, 4);
        openBtn.Click += (_, _) => SystemActions.Open(_item.Path);
        var closeBtn = SheetUi.SheetButton("닫기", prominent: true, minWidth: 0);
        closeBtn.FontSize = 12;
        closeBtn.Padding = new Thickness(12, 4, 12, 4);
        closeBtn.Margin = new Thickness(8, 0, 0, 0);
        closeBtn.Click += (_, _) => Close();
        var btns = new StackPanel { Orientation = Orientation.Horizontal };
        btns.Children.Add(openBtn);
        btns.Children.Add(closeBtn);
        DockPanel.SetDock(btns, Dock.Right);
        header.Children.Add(btns);

        var name = SheetUi.Text(item.Name, 13, FontWeights.Bold, "TextPrimaryBrush");
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.VerticalAlignment = VerticalAlignment.Center;
        name.Margin = new Thickness(2, 0, 10, 0);
        header.Children.Add(name);
        root.Children.Add(header);

        var divider = new Border { Height = 1 };
        divider.SetResourceReference(Border.BackgroundProperty, "DividerBrush");
        Grid.SetRow(divider, 1);
        root.Children.Add(divider);

        // 콘텐츠 (로딩 → 비동기 교체)
        _contentHost = new Border { Child = BuildLoading() };
        _contentHost.SetResourceReference(Border.BackgroundProperty, "ContentBackgroundBrush");
        Grid.SetRow(_contentHost, 2);
        root.Children.Add(_contentHost);

        SetSheetContent(root);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Space) { e.Handled = true; Close(); }
        };
        Loaded += async (_, _) =>
        {
            var loaded = await Task.Run(LoadContent);
            _contentHost.Child = BuildContent(loaded);   // await 후 UI 스레드
        };
    }

    // ── 로딩 (백그라운드) ────────────────────────────────────────────────

    private LoadedContent LoadContent()
    {
        if (!_item.IsDirectory)
        {
            if (ImageExts.Contains(_item.Ext))
            {
                var img = TryLoadImage(_item.Path);
                if (img is not null) return new LoadedContent(img, null, null);
            }
            var text = TryLoadText(_item.Path);
            if (text is not null) return new LoadedContent(null, text, null);
        }
        ImageSource? thumb = null;
        try { thumb = IconCache.ThumbnailFor(_item.Path, 512); } catch { }
        return new LoadedContent(null, null, thumb);
    }

    private static BitmapImage? TryLoadImage(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = fs;
            bmp.EndInit();
            bmp.Freeze();   // 백그라운드 생성 → UI 스레드 사용
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>앞부분 최대 1,000,000바이트 UTF-8 엄격 디코딩 — 실패 시 null (원본과 동일 규칙).</summary>
    private static string? TryLoadText(string path)
    {
        const int Limit = 1_000_000;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var len = (int)Math.Min(fs.Length, Limit);
            var buf = new byte[len];
            var read = 0;
            while (read < len)
            {
                var n = fs.Read(buf, read, len - read);
                if (n <= 0) break;
                read += n;
            }
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            // 1MB 컷 경계에서 멀티바이트 문자가 잘릴 수 있음 — 최대 3바이트 되감기 허용
            var maxTrim = read == Limit ? 3 : 0;
            for (int trim = 0; trim <= maxTrim && read - trim >= 0; trim++)
            {
                try { return strict.GetString(buf, 0, read - trim); }
                catch (DecoderFallbackException) { }
            }
            return null;
        }
        catch { return null; }
    }

    // ── 콘텐츠 뷰 ────────────────────────────────────────────────────────

    private static UIElement BuildLoading()
    {
        var bar = new ProgressBar { IsIndeterminate = true, Width = 160, Height = 4, BorderThickness = new Thickness(0) };
        bar.SetResourceReference(Control.ForegroundProperty, "AccentBrush");
        bar.SetResourceReference(Control.BackgroundProperty, "ControlFillBrush");
        bar.HorizontalAlignment = HorizontalAlignment.Center;
        bar.VerticalAlignment = VerticalAlignment.Center;
        return bar;
    }

    private UIElement BuildContent(LoadedContent loaded)
    {
        if (loaded.Image is not null)
        {
            var img = new Image
            {
                Source = loaded.Image,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10),
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            return img;
        }

        if (loaded.Text is not null)
        {
            var box = new TextBox
            {
                Text = loaded.Text,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 12,
                Padding = new Thickness(10),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            box.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
            box.SetResourceReference(TextBox.CaretBrushProperty, "TextPrimaryBrush");
            return box;
        }

        // 폴백: 셸 썸네일 + 파일 정보
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 460,
        };
        if (loaded.Thumb is not null)
        {
            var thumb = new Image
            {
                Source = loaded.Thumb,
                MaxWidth = 256, MaxHeight = 256,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                Margin = new Thickness(0, 0, 0, 14),
            };
            RenderOptions.SetBitmapScalingMode(thumb, BitmapScalingMode.HighQuality);
            panel.Children.Add(thumb);
        }
        else
        {
            var glyph = new TextBlock
            {
                Text = IconMap.Glyph("doc"),
                FontFamily = SheetUi.IconFont(),
                FontSize = 40,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 14),
            };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            panel.Children.Add(glyph);
        }

        var title = SheetUi.Text(_item.Name, 13, FontWeights.SemiBold, "TextPrimaryBrush", TextWrapping.Wrap);
        title.TextAlignment = TextAlignment.Center;
        panel.Children.Add(title);

        var info = new[]
        {
            $"크기: {Format.Size(_item.Size, _item.IsDirectory)}",
            $"수정일: {Format.Date(_item.Modified)}",
            $"종류: {Format.KindLabel(_item)}",
        };
        foreach (var line in info)
        {
            var tb = SheetUi.Text(line, 11.5, FontWeights.Normal, "TextSecondaryBrush");
            tb.HorizontalAlignment = HorizontalAlignment.Center;
            tb.Margin = new Thickness(0, 4, 0, 0);
            panel.Children.Add(tb);
        }

        if (loaded.Thumb is null && !_item.IsDirectory)
        {
            var none = SheetUi.Text($"“{_item.Name}”은(는) 미리 볼 수 없습니다.",
                11.5, FontWeights.Normal, "TextSecondaryBrush", TextWrapping.Wrap);
            none.TextAlignment = TextAlignment.Center;
            none.Margin = new Thickness(0, 12, 0, 0);
            panel.Children.Add(none);
        }
        return panel;
    }
}
