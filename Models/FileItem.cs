namespace XFinder.Models;

/// <summary>패널에 표시되는 항목 하나 (파일/폴더/심볼릭 링크/가상 ".." 부모 행).</summary>
public sealed class FileItem : IEquatable<FileItem>
{
    public required string Path { get; init; }      // 전체 경로 (mac URL 대응)
    public required string Name { get; set; }
    public bool IsDirectory { get; init; }
    public bool IsSymlink { get; init; }             // Windows: reparse point (심볼릭 링크/정션)
    public bool IsHidden { get; init; }
    public long Size { get; set; }                   // bytes; 미측정 폴더는 -1
    public DateTime Modified { get; init; }
    public string Ext { get; init; } = "";           // 소문자, 점 없음; 폴더/없음은 ""
    public bool IsParent { get; init; }              // ".." 행
    public DateTime Created { get; init; } = DateTime.MinValue;
    public string TypeName { get; set; } = "";       // 종류(현지화 설명, 예: "PNG 파일"); 비면 Format.Kind로 대체

    public string Id => Path;

    /// <summary>Windows에는 mac 번들 개념이 없음 — 항상 false (그룹/종류 로직 호환용).</summary>
    public bool IsBundle => false;

    public static FileItem ParentOf(string directory) => new()
    {
        Path = System.IO.Path.GetDirectoryName(directory) ?? directory,
        Name = "..",
        IsDirectory = true,
        IsSymlink = false,
        IsHidden = false,
        Size = -1,
        Modified = DateTime.MinValue,
        Ext = "",
        IsParent = true,
    };

    public bool Equals(FileItem? other) => other is not null && Path == other.Path;
    public override bool Equals(object? obj) => Equals(obj as FileItem);
    public override int GetHashCode() => Path.GetHashCode();
}

// ── 표시 포맷팅 ──────────────────────────────────────────────────────────

public static class Format
{
    /// <summary>mac ByteCountFormatter(.file) 근사 — 10진(1000) 단위, KB까지 정수·MB부터 소수 1자리.</summary>
    public static string Size(long bytes, bool isDirectory)
    {
        if (bytes < 0) return "--";   // 미측정 폴더
        return Bytes(bytes);
    }

    public static string Bytes(long bytes)
    {
        if (bytes < 1000) return $"{bytes} B";
        double kb = bytes / 1000.0;
        if (kb < 1000) return $"{Math.Round(kb)} KB";
        double mb = kb / 1000.0;
        if (mb < 1000) return $"{mb:0.#} MB";
        double gb = mb / 1000.0;
        if (gb < 1000) return $"{gb:0.##} GB";
        return $"{gb / 1000.0:0.##} TB";
    }

    public static string Date(DateTime date)
    {
        if (date == DateTime.MinValue) return "";
        return date.ToString("yyyy-MM-dd HH:mm");
    }

    /// <summary>상대 시간: "방금 전"/"N분 전"/"N시간 전"/"N일 전"/"N주 전"/"N개월 전"/"N년 전".
    /// 미래 시각(시계 차이 등)은 실제 날짜로 표시.</summary>
    public static string RelativeDate(DateTime date)
    {
        if (date == DateTime.MinValue) return "";
        var interval = DateTime.Now - date;
        if (interval < TimeSpan.Zero) return Date(date);
        var s = (long)interval.TotalSeconds;
        if (s < 60) return "방금 전";
        var m = s / 60;
        if (m < 60) return $"{m}분 전";
        var h = m / 60;
        if (h < 24) return $"{h}시간 전";
        var d = h / 24;
        if (d < 7) return $"{d}일 전";
        if (d < 30) return $"{d / 7}주 전";
        if (d < 365) return $"{d / 30}개월 전";
        return $"{d / 365}년 전";
    }

    /// <summary>설정(보기 → 날짜 표시)에 따라 실제 날짜 또는 상대 시간.</summary>
    public static string Date(DateTime date, DateDisplayStyle style)
        => style == DateDisplayStyle.Relative ? RelativeDate(date) : Date(date);

    public static string Kind(FileItem item)
    {
        if (item.IsParent) return "";
        if (item.IsSymlink) return "Alias";
        if (item.IsDirectory) return "Folder";
        return item.Ext.Length == 0 ? "File" : item.Ext.ToUpperInvariant();
    }

    /// <summary>종류 컬럼 표시값: 현지화 설명("PNG 파일")이 있으면 그것, 없으면 Kind.</summary>
    public static string KindLabel(FileItem item)
    {
        if (item.IsParent) return "";
        return item.TypeName.Length == 0 ? Kind(item) : item.TypeName;
    }
}
