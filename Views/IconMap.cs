namespace XFinder.Views;

/// <summary>
/// SF Symbol 이름 → Segoe Fluent Icons/MDL2 글리프 매핑 (mac 소스의 icon 키를 그대로 쓰기 위함).
/// 폰트: 리소스 "IconFontFamily" = "Segoe Fluent Icons, Segoe MDL2 Assets".
/// 글리프는 빌드 후 시각 확인 — 누락 시 근사 글리프로 대체.
/// </summary>
public static class IconMap
{
    private static readonly Dictionary<string, string> Map = new()
    {
        // 탐색/디스클로저
        ["chevron.backward"] = "",
        ["chevron.forward"] = "",
        ["chevron.up"] = "",
        ["chevron.down"] = "",
        ["chevron.right"] = "",
        // 툴바
        ["terminal"] = "",
        ["calendar"] = "",
        ["sparkles"] = "✨",
        ["magnifyingglass"] = "",
        ["xmark"] = "",
        ["xmark.circle.fill"] = "",
        ["plus"] = "",
        ["square.grid.2x2"] = "",
        ["list.bullet"] = "",
        ["square.grid.3x1.below.line.grid.1x2"] = "",
        ["folder.badge.plus"] = "",
        ["ellipsis.circle"] = "",
        ["eye.fill"] = "",
        ["eye.slash"] = "",
        ["pencil"] = "",
        ["arrow.right.circle.fill"] = "",
        ["checkmark"] = "",
        ["arrow.clockwise"] = "",
        // 시스템 상태
        ["cpu"] = "",
        ["memorychip"] = "",
        ["internaldrive"] = "",
        ["externaldrive"] = "",
        // 사이드바
        ["clock"] = "",
        ["clock.fill"] = "",
        ["house"] = "",
        ["doc"] = "",
        ["arrow.down.circle"] = "",
        ["photo"] = "",
        ["film"] = "",
        ["music.note"] = "",
        ["menubar.dock.rectangle"] = "",
        ["folder"] = "",
        ["folder.fill"] = "",
        ["circle.fill"] = "●",      // 태그 점 — 뷰에서는 Ellipse 권장
        // 설정/기타
        ["gearshape"] = "",
        ["rectangle.grid.1x2"] = "",
        ["circle.lefthalf.filled"] = "◐",
        ["rectangle.split.3x1"] = "",
        ["textformat.size.smaller"] = "",
        ["textformat.size.larger"] = "",
        ["calendar.badge.clock"] = "",
        ["sum"] = "",
        ["hand.raised"] = "",
        ["trash"] = "",
        ["doc.text"] = "",
        ["info.circle"] = "",
        ["questionmark.circle"] = "",
        ["play.fill"] = "",
        ["square.and.arrow.up"] = "",
        ["tag"] = "",
        ["bolt.fill"] = "",
        ["exclamationmark.triangle"] = "",
    };

    /// <summary>글리프 반환 — 모르는 키는 폴더 글리프.</summary>
    public static string Glyph(string sfName) => Map.TryGetValue(sfName, out var g) ? g : "";

    /// <summary>태그 colorIndex → 색 (mac Finder 체계: 1=회색, 2=초록, 3=보라, 4=파랑, 5=노랑, 6=빨강, 7=주황).</summary>
    public static System.Windows.Media.Color TagColor(int colorIndex) => colorIndex switch
    {
        1 => System.Windows.Media.Color.FromRgb(0x8E, 0x8E, 0x93),
        2 => System.Windows.Media.Color.FromRgb(0x28, 0xCD, 0x41),
        3 => System.Windows.Media.Color.FromRgb(0xAF, 0x52, 0xDE),
        4 => System.Windows.Media.Color.FromRgb(0x00, 0x7A, 0xFF),
        5 => System.Windows.Media.Color.FromRgb(0xFF, 0xCC, 0x00),
        6 => System.Windows.Media.Color.FromRgb(0xFF, 0x3B, 0x30),
        7 => System.Windows.Media.Color.FromRgb(0xFF, 0x95, 0x00),
        _ => System.Windows.Media.Color.FromRgb(0x8E, 0x8E, 0x93),
    };

    /// <summary>태그 이름 → 색 (표준 7색).</summary>
    public static System.Windows.Media.Color TagColorByName(string name)
    {
        foreach (var (n, idx) in Models.AppModel.StandardTags)
            if (n == name) return TagColor(idx);
        return System.Windows.Media.Color.FromRgb(0x8E, 0x8E, 0x93);
    }

    /// <summary>탭 파스텔 팔레트 8색 — 파랑/초록/주황/보라/분홍/청록/남색/갈색 (colorIndex % 8).</summary>
    public static readonly System.Windows.Media.Color[] TabPalette =
    {
        System.Windows.Media.Color.FromRgb(0x00, 0x7A, 0xFF),   // blue
        System.Windows.Media.Color.FromRgb(0x28, 0xCD, 0x41),   // green
        System.Windows.Media.Color.FromRgb(0xFF, 0x95, 0x00),   // orange
        System.Windows.Media.Color.FromRgb(0xAF, 0x52, 0xDE),   // purple
        System.Windows.Media.Color.FromRgb(0xFF, 0x2D, 0x55),   // pink
        System.Windows.Media.Color.FromRgb(0x59, 0xAD, 0xC4),   // teal
        System.Windows.Media.Color.FromRgb(0x58, 0x56, 0xD6),   // indigo
        System.Windows.Media.Color.FromRgb(0xA2, 0x84, 0x5E),   // brown
    };

    public static System.Windows.Media.Color TabColor(int colorIndex)
        => TabPalette[((colorIndex % 8) + 8) % 8];
}
