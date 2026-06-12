// mac 소스 대응: Sources/XFinder/Services/FileSystemService.swift — 디렉터리 나열·재귀 검색·fts 고속 순회(크기/통계)·종류별 파일 계산
using System.Collections.Concurrent;
using System.IO;
using XFinder.Models;

namespace XFinder.Services;

/// <summary>
/// 종류별(by-type) 분석 결과 한 카테고리.
/// Files는 크기 내림차순 정렬된 (경로, 크기) 인덱스로 최대 TypeIndexLimit개 —
/// UI는 여기서 페이지 단위로 잘라 FileItem으로 변환한다 (AppModel.ShowTypeBreakdown).
/// </summary>
public readonly record struct TypeBreakdown(
    string Name,                                     // "문서"/"이미지"/"동영상"/"음악"/"압축"/"기타"
    long Bytes,
    int Count,
    IReadOnlyList<(string Path, long Size)> Files);

/// <summary>파일 시스템 서비스 — mac FileSystemService 대응 (fts는 재귀 열거 + 병렬로 대체).</summary>
public static class FileSystemService
{
    /// <summary>카테고리 표시 순서 — 원문 그대로.</summary>
    public static readonly string[] FileTypeOrder = { "문서", "이미지", "동영상", "음악", "압축", "기타" };

    /// <summary>카테고리당 내역 인덱스 최대 파일 수 (항목당 ~100B → 수십 MB 상한 안전판).</summary>
    public const int TypeIndexLimit = 200_000;

    private const int OtherIndex = 5;   // "기타"

    /// <summary>숨김 포함 + 접근 불가 무시 열거 옵션 (EnumerationOptions 기본값은 Hidden|System을 건너뛰므로 직접 지정).</summary>
    private static readonly EnumerationOptions WalkOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.None,
        RecurseSubdirectories = false,
    };

    // ── 1.2 디렉터리 한 단계 나열 ────────────────────────────────────────

    /// <summary>
    /// 디렉터리 한 단계 나열 — 숨김 포함(필터는 상위 모델), 정렬하지 않음.
    /// 실패(권한 없음 등)는 예외 그대로 던짐 — 호출부(AppModel.LoadDetail)가 메시지 표시.
    /// </summary>
    public static List<FileItem> List(string directory)
    {
        var result = new List<FileItem>();
        var listOptions = new EnumerationOptions
        {
            IgnoreInaccessible = false,     // 디렉터리 자체 접근 실패는 예외로 전달
            AttributesToSkip = FileAttributes.None,
        };
        foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", listOptions))
        {
            FileAttributes attrs;
            try { attrs = info.Attributes; } catch { continue; }
            bool isDir = (attrs & FileAttributes.Directory) != 0;
            bool isSymlink = (attrs & FileAttributes.ReparsePoint) != 0;
            bool isHidden = (attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0
                            || info.Name.StartsWith('.');   // 맥/리눅스에서 온 도트파일

            long size = -1;                                  // 디렉터리는 -1 (미측정)
            if (!isDir)
            {
                try { size = ((FileInfo)info).Length; } catch { size = 0; }
            }
            DateTime modified, created;
            try { modified = info.LastWriteTime; } catch { modified = DateTime.MinValue; }
            try { created = info.CreationTime; } catch { created = DateTime.MinValue; }

            result.Add(new FileItem
            {
                Path = info.FullName,
                Name = info.Name,
                IsDirectory = isDir,
                IsSymlink = isSymlink,
                IsHidden = isHidden,
                Size = size,
                Modified = modified,
                Ext = isDir ? "" : ExtensionLower(info.Name),
                IsParent = false,
                Created = created,
                TypeName = TypeNameFor(info.Name, isDir),
            });
        }
        return result;
    }

    // ── 1.3 이름 부분일치 재귀 검색 ──────────────────────────────────────

    /// <summary>
    /// 이름 부분일치 재귀 검색 — needle은 이미 소문자로 정규화되어 들어옴.
    /// 숨김: showHidden == false면 숨김 항목·숨김 트리 전체 스킵. 권한 없는 폴더는 조용히 건너뜀.
    /// limit개 모이면 즉시 중단. 결과: 폴더 우선 → 이름 자연 정렬. 백그라운드에서 호출할 것.
    /// </summary>
    public static List<FileItem> SearchRecursive(string root, string needleLower, bool showHidden,
                                                 int limit = 1000, CancellationToken ct = default)
    {
        var results = new List<FileItem>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0 && results.Count < limit)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            foreach (var info in SafeEntries(dir))
            {
                if (results.Count >= limit) break;
                ct.ThrowIfCancellationRequested();
                FileAttributes attrs;
                try { attrs = info.Attributes; } catch { continue; }
                bool isDir = (attrs & FileAttributes.Directory) != 0;
                bool isSymlink = (attrs & FileAttributes.ReparsePoint) != 0;
                bool hidden = (attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0
                              || info.Name.StartsWith('.');
                if (!showHidden && hidden) continue;          // 숨김 트리 전체 스킵

                if (isDir && !isSymlink) stack.Push(info.FullName);   // 심링크/정션은 재귀 안 함

                if (!info.Name.ToLowerInvariant().Contains(needleLower)) continue;

                long size = -1;
                if (!isDir)
                {
                    try { size = ((FileInfo)info).Length; } catch { size = 0; }
                }
                DateTime modified;
                try { modified = info.LastWriteTime; } catch { modified = DateTime.MinValue; }

                results.Add(new FileItem
                {
                    Path = info.FullName,
                    Name = info.Name,
                    IsDirectory = isDir,
                    IsSymlink = isSymlink,
                    IsHidden = hidden,
                    Size = size,
                    Modified = modified,
                    Ext = isDir ? "" : ExtensionLower(info.Name),
                    IsParent = false,
                    // created/typeName은 기본값 (원본 동일)
                });
            }
        }
        results.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;   // 폴더 우선
            return NaturalSort.Compare(a.Name, b.Name);                           // Finder식 자연 정렬
        });
        return results;
    }

    // ── 1.4 / 1.5 사이드바 트리 ──────────────────────────────────────────

    /// <summary>직속 하위 폴더 전체 경로 — 이름 자연 정렬 오름차순. 실패 시 빈 배열.</summary>
    public static List<string> Subfolders(string dir, bool showHidden)
    {
        var list = new List<string>();
        try
        {
            foreach (var d in new DirectoryInfo(dir).EnumerateDirectories("*", WalkOptions))
            {
                bool hidden = (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0
                              || d.Name.StartsWith('.');
                if (!showHidden && hidden) continue;
                list.Add(d.FullName);
            }
        }
        catch { return new List<string>(); }
        list.Sort((a, b) => NaturalSort.Compare(Path.GetFileName(a), Path.GetFileName(b)));
        return list;
    }

    /// <summary>트리 행 확장 가능 여부 — 하위 폴더 첫 발견 즉시 true. 실패 시 false.</summary>
    public static bool HasSubfolders(string dir, bool showHidden = false)
    {
        try
        {
            foreach (var d in new DirectoryInfo(dir).EnumerateDirectories("*", WalkOptions))
            {
                bool hidden = (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0
                              || d.Name.StartsWith('.');
                if (showHidden || !hidden) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>준비된 드라이브 루트 목록 (mac /Volumes 대응) — 예: "C:\", "D:\".</summary>
    public static List<string> DriveRoots()
    {
        var roots = new List<string>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try { if (d.IsReady) roots.Add(d.RootDirectory.FullName); } catch { }
            }
        }
        catch { }
        return roots;
    }

    // ── 1.6 / 1.8 재귀 크기·통계 (fts 대응) ─────────────────────────────

    /// <summary>재귀 총 크기 — 일반 파일의 논리 크기 합. 숨김 포함, 심링크/정션 미추적, 에러/빈 폴더 0.
    /// 메인 스레드 밖에서 호출할 것. 취소 시 OperationCanceledException.</summary>
    public static long FolderSize(string dir, CancellationToken ct = default)
    {
        long total = 0;
        Walk(dir, skipHidden: false, ct, (_, size) => total += size);
        return total;
    }

    /// <summary>재귀 파일/폴더 수 + 총 바이트 — 숨김 포함. 에러 항목은 건너뜀.</summary>
    public static (int Files, int Folders, long Bytes) FolderStats(string dir, CancellationToken ct = default)
    {
        int files = 0, folders = 0;
        long bytes = 0;
        Walk(dir, skipHidden: false, ct,
            (_, size) => { files++; bytes += size; },
            _ => folders++);
        return (files, folders, bytes);
    }

    // ── 1.9 종류별 카테고리 맵 (확장자 → 카테고리, 원본 그대로) ──────────

    private static readonly Dictionary<string, int> CategoryByExt = BuildCategoryMap();

    private static Dictionary<string, int> BuildCategoryMap()
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        void Add(int index, params string[] exts)
        {
            foreach (var e in exts) map[e] = index;
        }
        Add(0, "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "hwp", "hwpx",
               "pages", "numbers", "key", "md", "csv", "rtf", "odt", "ods", "odp", "epub");   // 문서
        Add(1, "jpg", "jpeg", "png", "gif", "heic", "heif", "webp", "tiff", "tif", "bmp",
               "svg", "raw", "cr2", "nef", "dng", "psd", "ai");                                // 이미지
        Add(2, "mp4", "mov", "avi", "mkv", "m4v", "wmv", "flv", "webm", "mpg", "mpeg", "3gp"); // 동영상
        Add(3, "mp3", "wav", "aac", "flac", "m4a", "aiff", "aif", "ogg", "wma", "opus");       // 음악
        Add(4, "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "dmg", "pkg", "iso");             // 압축
        return map;
    }

    /// <summary>확장자 → 카테고리 이름 ("문서"/"이미지"/…/"기타").</summary>
    public static string FileCategory(string ext)
        => CategoryByExt.TryGetValue(ext.ToLowerInvariant(), out var idx)
            ? FileTypeOrder[idx] : FileTypeOrder[OtherIndex];

    // ── 1.10 병렬 종류별 크기 계산 ───────────────────────────────────────

    /// <summary>
    /// 종류별(문서/이미지/…) 카운트·크기·내역 인덱스 — 숨김 트리 제외, 심링크 무시.
    /// 1·2단계를 훑어 작업 단위를 만들고 코어 수만큼 병렬 스캔(워커 로컬 누적 → 락 병합).
    /// 항상 FileTypeOrder 순서의 6개를 반환(빈 카테고리 포함). 메인 스레드 밖에서 호출(수 초 소요 가능).
    /// </summary>
    public static List<TypeBreakdown> SizeByFileType(string root, CancellationToken ct = default)
    {
        int n = FileTypeOrder.Length;
        var global = new TypeAcc(n);
        var gate = new object();

        // 1) 작업 단위 수집 — 루트 직속부터 숨김 스킵. 직속 일반 파일은 그 자리에서 누적.
        //    1단계 폴더의 자식 나열 실패는 그 폴더 통째로 건너뜀 (원본 동일).
        var units = new List<string>();
        foreach (var e1 in SafeEntries(root))
        {
            ct.ThrowIfCancellationRequested();
            if (SkipForType(e1, out bool isDir1, out long size1)) continue;
            if (!isDir1) { Accumulate(global, e1.Name, e1.FullName, size1); continue; }
            foreach (var e2 in SafeEntries(e1.FullName))
            {
                ct.ThrowIfCancellationRequested();
                if (SkipForType(e2, out bool isDir2, out long size2)) continue;
                if (isDir2) units.Add(e2.FullName);
                else Accumulate(global, e2.Name, e2.FullName, size2);
            }
        }

        // 2) 병렬 스캔 — 로컬 딕셔너리에 락 없이 누적, 단위 완료 시에만 전역 병합.
        Parallel.ForEach(
            units,
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
            () => new TypeAcc(n),
            (unit, _, local) =>
            {
                Walk(unit, skipHidden: true, ct,
                    (path, size) => Accumulate(local, Path.GetFileName(path), path, size));
                return local;
            },
            local =>
            {
                lock (gate)
                {
                    for (int i = 0; i < n; i++)
                    {
                        global.Bytes[i] += local.Bytes[i];
                        global.Counts[i] += local.Counts[i];
                        global.Files[i].AddRange(local.Files[i]);
                        Prune(global.Files[i]);
                    }
                }
            });

        // 3) 결과 — 크기 내림차순 정렬 후 상위 TypeIndexLimit개.
        var result = new List<TypeBreakdown>(n);
        for (int i = 0; i < n; i++)
        {
            var files = global.Files[i];
            files.Sort((a, b) => b.Size.CompareTo(a.Size));
            if (files.Count > TypeIndexLimit)
                files.RemoveRange(TypeIndexLimit, files.Count - TypeIndexLimit);
            result.Add(new TypeBreakdown(FileTypeOrder[i], global.Bytes[i], global.Counts[i], files));
        }
        return result;
    }

    /// <summary>워커 로컬/전역 누적기 — 카테고리 인덱스 배열.</summary>
    private sealed class TypeAcc
    {
        public readonly long[] Bytes;
        public readonly int[] Counts;
        public readonly List<(string Path, long Size)>[] Files;

        public TypeAcc(int n)
        {
            Bytes = new long[n];
            Counts = new int[n];
            Files = new List<(string Path, long Size)>[n];
            for (int i = 0; i < n; i++) Files[i] = new List<(string Path, long Size)>();
        }
    }

    private static void Accumulate(TypeAcc acc, string name, string path, long size)
    {
        int cat = CategoryIndex(name);
        acc.Bytes[cat] += size;
        acc.Counts[cat] += 1;
        acc.Files[cat].Add((path, size));
        Prune(acc.Files[cat]);
    }

    /// <summary>prune 규칙: 2×TypeIndexLimit 도달 시 크기 내림차순 정렬 후 상위 TypeIndexLimit개만 유지
    /// (버려지는 항목은 이미 자기보다 큰 파일이 N개 존재 → 최종 top-N 정확성 보장).</summary>
    private static void Prune(List<(string Path, long Size)> files)
    {
        if (files.Count < 2 * TypeIndexLimit) return;
        files.Sort((a, b) => b.Size.CompareTo(a.Size));
        files.RemoveRange(TypeIndexLimit, files.Count - TypeIndexLimit);
    }

    /// <summary>종류별 스캔에서 건너뛸 항목인지 — 심링크/숨김/속성 조회 실패.</summary>
    private static bool SkipForType(FileSystemInfo info, out bool isDir, out long size)
    {
        isDir = false;
        size = 0;
        FileAttributes attrs;
        try { attrs = info.Attributes; } catch { return true; }
        if ((attrs & FileAttributes.ReparsePoint) != 0) return true;   // 심볼릭 링크는 전부 무시
        if ((attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0
            || info.Name.StartsWith('.')) return true;                 // 숨김 트리 스킵
        isDir = (attrs & FileAttributes.Directory) != 0;
        if (!isDir)
        {
            try { size = ((FileInfo)info).Length; } catch { return true; }
        }
        return false;
    }

    /// <summary>fts 확장자 규칙(원본 그대로): 마지막 점 뒤 1~12자만 인정, 도트파일은 확장자 없음,
    /// ASCII 대문자만 소문자화 → 카테고리 인덱스 (없으면 "기타").</summary>
    private static int CategoryIndex(string name)
    {
        int i = name.LastIndexOf('.');
        if (i <= 0) return OtherIndex;
        int len = name.Length - i - 1;
        if (len < 1 || len > 12) return OtherIndex;
        Span<char> buf = stackalloc char[len];
        for (int k = 0; k < len; k++)
        {
            char c = name[i + 1 + k];
            buf[k] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
        }
        return CategoryByExt.TryGetValue(new string(buf), out var idx) ? idx : OtherIndex;
    }

    // ── 1.7 fts 대응 고속 순회 ───────────────────────────────────────────

    /// <summary>
    /// fts(FTS_PHYSICAL | FTS_NOCHDIR) 대응 재귀 순회 — 일반 파일만 onFile 콜백, 디렉터리는 onDirectory.
    /// 심링크/정션은 따라가지 않음(무한 루프 방지). skipHidden이면 숨김 서브트리 전체 스킵
    /// (루트 자체는 숨김이어도 스킵하지 않음 — 레벨>0 조건). 접근 불가/에러 항목은 조용히 건너뜀.
    /// </summary>
    private static void Walk(string root, bool skipHidden, CancellationToken ct,
                             Action<string, long> onFile, Action<string>? onDirectory = null)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            foreach (var info in SafeEntries(dir))
            {
                ct.ThrowIfCancellationRequested();
                FileAttributes attrs;
                try { attrs = info.Attributes; } catch { continue; }
                if ((attrs & FileAttributes.ReparsePoint) != 0) continue;   // 물리 순회 — 심링크 미추적
                bool hidden = (attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0
                              || info.Name.StartsWith('.');
                if (skipHidden && hidden) continue;
                if ((attrs & FileAttributes.Directory) != 0)
                {
                    onDirectory?.Invoke(info.FullName);
                    stack.Push(info.FullName);
                }
                else
                {
                    long size;
                    try { size = ((FileInfo)info).Length; } catch { continue; }
                    onFile(info.FullName, size);
                }
            }
        }
    }

    /// <summary>한 단계 안전 나열 — 실패하면 빈 목록 (권한 없는 폴더 등).</summary>
    private static List<FileSystemInfo> SafeEntries(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).EnumerateFileSystemInfos("*", WalkOptions).ToList();
        }
        catch { return new List<FileSystemInfo>(); }
    }

    // ── 1.11 볼륨 여유 공간 ─────────────────────────────────────────────

    /// <summary>볼륨 여유 공간 문자열 (십진 단위, Format.Bytes 규칙). 실패 시 null.</summary>
    public static string? FreeSpace(string anyPathOnVolume)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(anyPathOnVolume));
            if (string.IsNullOrEmpty(root)) return null;
            var drive = new DriveInfo(root);
            if (!drive.IsReady) return null;
            return Format.Bytes(drive.AvailableFreeSpace);
        }
        catch { return null; }
    }

    // ── 공용 유틸 ────────────────────────────────────────────────────────

    /// <summary>표시용 확장자 — 소문자, 점 없음. 도트파일(.gitignore)은 확장자 없음.</summary>
    public static string ExtensionLower(string name)
    {
        int i = name.LastIndexOf('.');
        if (i <= 0 || i == name.Length - 1) return "";
        return name[(i + 1)..].ToLowerInvariant();
    }

    private static readonly ConcurrentDictionary<string, string> TypeNameCache = new();

    /// <summary>셸 종류 문자열 (예: "PNG 파일") — 확장자별 캐시, 디스크 무접근(SHGFI_USEFILEATTRIBUTES).</summary>
    public static string TypeNameFor(string nameOrPath, bool isDirectory)
    {
        var key = isDirectory ? "\0dir" : ExtensionLower(Path.GetFileName(nameOrPath));
        return TypeNameCache.GetOrAdd(key, _ =>
        {
            try { return ShellInterop.GetTypeName(nameOrPath, isDirectory); }
            catch { return ""; }
        });
    }
}
