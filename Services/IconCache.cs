using System.Collections.Concurrent;
using System.Windows.Media;

namespace XFinder.Services;

/// <summary>
/// 파일 아이콘/썸네일 캐시 — mac ThumbnailCache 대응.
/// 확장자 단위 셸 아이콘은 영구 캐시, 경로 단위 썸네일은 용량 제한 캐시.
/// </summary>
public static class IconCache
{
    private static readonly ConcurrentDictionary<string, ImageSource?> ExtIcons = new();
    private static readonly ConcurrentDictionary<string, ImageSource?> Thumbs = new();
    private const int ThumbCacheLimit = 512;

    /// <summary>확장자 기반 아이콘 (목록 보기용). key 예: ".png|s", "dir|l"</summary>
    public static ImageSource? IconFor(string extension, bool isDirectory, bool large)
    {
        var key = (isDirectory ? "dir" : extension.ToLowerInvariant()) + (large ? "|l" : "|s");
        return ExtIcons.GetOrAdd(key, _ =>
            ShellInterop.GetIconForExtension(extension, isDirectory, large));
    }

    /// <summary>경로 기반 썸네일 (아이콘 보기·미리보기용). 없으면 null.</summary>
    public static ImageSource? ThumbnailFor(string path, int pixelSize)
    {
        var key = path + "|" + pixelSize;
        if (Thumbs.TryGetValue(key, out var cached)) return cached;
        var thumb = ShellInterop.GetThumbnail(path, pixelSize);
        if (Thumbs.Count >= ThumbCacheLimit) Thumbs.Clear(); // 단순 전체 비우기 (LRU 불필요한 규모)
        Thumbs[key] = thumb;
        return thumb;
    }

    public static void InvalidateThumbnails() => Thumbs.Clear();
}
