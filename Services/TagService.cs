// mac 소스 대응: Sources/XFinder/Services/TagService.swift (xattr 바이너리 plist + Spotlight TagLoader) — Windows: %APPDATA%\XFinder\tags.json 인덱스 + NTFS ADS 사본 (스펙 §5.4)
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using XFinder.Models;

namespace XFinder.Services;

/// <summary>Finder 태그 한 개 — 이름 + Finder 색번호. Id = Name.</summary>
public readonly record struct FinderTag(string Name, int ColorIndex);

/// <summary>
/// 7색 태그 읽기/쓰기 + 태그별 파일 검색.
/// 저장: 검색 인덱스 = %APPDATA%\XFinder\tags.json (경로 → 태그 배열),
/// 사본 = NTFS ADS "경로:XFinder.Tags" (같은 NTFS 볼륨에서 파일을 따라다님; 비-NTFS는 조용히 DB만).
/// mac과의 상호운용(xattr)은 명시적 비호환 (스펙 §5.4).
/// </summary>
public static class TagService
{
    /// <summary>표준 7색 — 표시 순서·색번호는 Finder 규칙 그대로.</summary>
    public static readonly FinderTag[] Standard =
    {
        new("빨간색", 6), new("주황색", 7), new("노란색", 5), new("초록색", 2),
        new("파란색", 4), new("보라색", 3), new("회색", 1),
    };

    /// <summary>색번호 → WPF 색 (mac 시스템 색상 대응값, 스펙 §5.1).</summary>
    public static Color ColorFor(int colorIndex) => colorIndex switch
    {
        1 => Color.FromRgb(0x8E, 0x8E, 0x93),   // 회색  systemGray
        2 => Color.FromRgb(0x34, 0xC7, 0x59),   // 초록  systemGreen
        3 => Color.FromRgb(0xAF, 0x52, 0xDE),   // 보라  systemPurple
        4 => Color.FromRgb(0x00, 0x7A, 0xFF),   // 파랑  systemBlue
        5 => Color.FromRgb(0xFF, 0xCC, 0x00),   // 노랑  systemYellow
        6 => Color.FromRgb(0xFF, 0x3B, 0x30),   // 빨강  systemRed
        7 => Color.FromRgb(0xFF, 0x95, 0x00),   // 주황  systemOrange
        _ => Color.FromRgb(0x8A, 0x8A, 0x8E),   // default → 보조 텍스트 색
    };

    private const string AdsStreamName = "XFinder.Tags";
    private const int SearchLimit = 500;

    private static readonly object Gate = new();
    private static Dictionary<string, List<TagDto>>? _db;

    private static string DbPath => Path.Combine(SettingsStore.Dir, "tags.json");

    /// <summary>직렬화 DTO (record struct 생성자 바인딩 의존 회피).</summary>
    private sealed class TagDto
    {
        public string Name { get; set; } = "";
        public int ColorIndex { get; set; }
    }

    // ── 읽기/쓰기 ────────────────────────────────────────────────────────

    /// <summary>파일의 태그 목록 — ADS 우선(파일을 따라온 사본), 없으면 인덱스 DB. 실패/없음 → 빈 목록.</summary>
    public static List<FinderTag> TagsOf(string path)
    {
        var full = SafeFullPath(path);
        var ads = ReadAds(full);
        if (ads is not null)
        {
            SyncDb(full, ads);   // 외부에서 옮겨온 파일의 ADS를 검색 인덱스에 반영
            return ads;
        }
        lock (Gate)
        {
            return EnsureDb().TryGetValue(full, out var dtos)
                ? dtos.Select(d => new FinderTag(d.Name, d.ColorIndex)).ToList()
                : new List<FinderTag>();
        }
    }

    /// <summary>태그 목록 기록 — 빈 목록이면 항목 자체 삭제(mac의 xattr 삭제 대응). DB + ADS 모두 갱신.</summary>
    public static void SetTags(string path, IReadOnlyList<FinderTag> tags)
    {
        var full = SafeFullPath(path);
        lock (Gate)
        {
            var db = EnsureDb();
            if (tags.Count == 0) db.Remove(full);
            else db[full] = tags.Select(t => new TagDto { Name = t.Name, ColorIndex = t.ColorIndex }).ToList();
            SaveDb();
        }
        WriteAds(full, tags);
    }

    /// <summary>이름만 비교(색번호 무시) — mac hasTag 동일.</summary>
    public static bool HasTag(FinderTag tag, string path)
        => TagsOf(path).Any(t => t.Name == tag.Name);

    /// <summary>
    /// 토글 — 선택 전부가 태그를 갖고 있으면 모두에서 제거, 아니면 모두에 추가.
    /// 추가 시 기존 동명 태그를 먼저 제거 후 append (중복/색 불일치 정리 효과 — mac 동일).
    /// </summary>
    public static void Toggle(FinderTag tag, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        bool allHave = paths.All(p => HasTag(tag, p));
        foreach (var p in paths)
        {
            var tags = TagsOf(p);
            tags.RemoveAll(t => t.Name == tag.Name);
            if (!allHave) tags.Add(tag);
            SetTags(p, tags);
        }
    }

    /// <summary>각 파일의 태그 전부 제거.</summary>
    public static void Clear(IReadOnlyList<string> paths)
    {
        foreach (var p in paths) SetTags(p, Array.Empty<FinderTag>());
    }

    /// <summary>이름 변경/이동 후 인덱스 키 이전 (호출부 선택 사항 — ADS는 같은 볼륨 이동 시 자동으로 따라감).</summary>
    public static void PathChanged(string oldPath, string newPath)
    {
        var oldFull = SafeFullPath(oldPath);
        var newFull = SafeFullPath(newPath);
        lock (Gate)
        {
            var db = EnsureDb();
            if (!db.TryGetValue(oldFull, out var tags)) return;
            db.Remove(oldFull);
            db[newFull] = tags;
            SaveDb();
        }
    }

    // ── 태그 검색 (mac TagLoader 대응) ──────────────────────────────────

    /// <summary>
    /// 태그가 붙은 파일 목록 (동기 — 호출부가 Task.Run으로 감싼다).
    /// 인덱스 DB에서 후보를 읽어 존재 확인 후 FileItem 생성 — 파일명 자연 정렬 오름차순, limit 500.
    /// IsSymlink/IsHidden = false 고정, Modified = 실제 수정일 (원본 동일).
    /// </summary>
    public static List<FileItem> FilesWithTag(string tagName, CancellationToken ct = default)
    {
        List<string> candidates;
        lock (Gate)
        {
            candidates = EnsureDb()
                .Where(kv => kv.Value.Any(t => t.Name == tagName))
                .Select(kv => kv.Key)
                .ToList();
        }

        var items = new List<FileItem>();
        foreach (var path in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var item = ItemFor(path);
            if (item is not null) items.Add(item);
        }
        items.Sort((a, b) => NaturalSort.Compare(a.Name, b.Name));
        if (items.Count > SearchLimit) items.RemoveRange(SearchLimit, items.Count - SearchLimit);
        return items;
    }

    private static FileItem? ItemFor(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                return new FileItem
                {
                    Path = fi.FullName,
                    Name = fi.Name,
                    IsDirectory = false,
                    IsSymlink = false,
                    IsHidden = false,
                    Size = fi.Length,
                    Modified = fi.LastWriteTime,
                    Ext = FileSystemService.ExtensionLower(fi.Name),
                    Created = fi.CreationTime,
                    TypeName = FileSystemService.TypeNameFor(fi.Name, false),
                };
            }
            if (Directory.Exists(path))
            {
                var di = new DirectoryInfo(path);
                return new FileItem
                {
                    Path = di.FullName,
                    Name = di.Name,
                    IsDirectory = true,
                    IsSymlink = false,
                    IsHidden = false,
                    Size = -1,
                    Modified = di.LastWriteTime,
                    Ext = "",
                    Created = di.CreationTime,
                    TypeName = FileSystemService.TypeNameFor(di.Name, true),
                };
            }
        }
        catch { }
        return null;   // 사라진 항목은 결과에서 제외
    }

    // ── 인덱스 DB (%APPDATA%\XFinder\tags.json) ──────────────────────────

    private static Dictionary<string, List<TagDto>> EnsureDb()   // Gate 잠금 안에서 호출
        => _db ??= LoadDb();

    private static Dictionary<string, List<TagDto>> LoadDb()
    {
        try
        {
            if (File.Exists(DbPath))
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, List<TagDto>>>(File.ReadAllText(DbPath));
                if (parsed is not null)
                    return new Dictionary<string, List<TagDto>>(parsed, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { /* 손상된 인덱스는 초기화 */ }
        return new Dictionary<string, List<TagDto>>(StringComparer.OrdinalIgnoreCase);
    }

    private static void SaveDb()   // Gate 잠금 안에서 호출
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.Dir);
            File.WriteAllText(DbPath, JsonSerializer.Serialize(_db,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 저장 실패는 치명적이지 않음 */ }
    }

    /// <summary>ADS에서 읽은 태그를 인덱스 DB에 반영 (다를 때만 저장).</summary>
    private static void SyncDb(string fullPath, List<FinderTag> tags)
    {
        lock (Gate)
        {
            var db = EnsureDb();
            db.TryGetValue(fullPath, out var existing);
            if (tags.Count == 0 && existing is null) return;
            bool same = existing is not null && existing.Count == tags.Count
                        && !existing.Where((d, i) => d.Name != tags[i].Name || d.ColorIndex != tags[i].ColorIndex).Any();
            if (same) return;
            if (tags.Count == 0) db.Remove(fullPath);
            else db[fullPath] = tags.Select(t => new TagDto { Name = t.Name, ColorIndex = t.ColorIndex }).ToList();
            SaveDb();
        }
    }

    // ── NTFS ADS 사본 ───────────────────────────────────────────────────

    private static string AdsPath(string path) => path + ":" + AdsStreamName;

    private static List<FinderTag>? ReadAds(string path)
    {
        try
        {
            using var fs = new FileStream(AdsPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
            var dtos = JsonSerializer.Deserialize<List<TagDto>>(fs);
            return dtos?.Select(d => new FinderTag(d.Name, d.ColorIndex)).ToList();
        }
        catch { return null; }   // 스트림 없음 / 비-NTFS / 접근 실패 → DB 폴백
    }

    private static void WriteAds(string path, IReadOnlyList<FinderTag> tags)
    {
        try
        {
            if (tags.Count == 0)
            {
                File.Delete(AdsPath(path));   // 빈 배열이면 스트림 자체 삭제 (mac xattr 삭제 대응)
                return;
            }
            using var fs = new FileStream(AdsPath(path), FileMode.Create, FileAccess.Write);
            JsonSerializer.Serialize(fs,
                tags.Select(t => new TagDto { Name = t.Name, ColorIndex = t.ColorIndex }).ToList());
        }
        catch { /* 비-NTFS(USB/FAT) 등 — 조용히 실패, DB만 사용 */ }
    }

    private static string SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); } catch { return path; }
    }
}
